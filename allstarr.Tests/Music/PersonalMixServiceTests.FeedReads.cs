using System.Diagnostics;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Fact]
    public async Task ServiceFeeds_ThirtyTwoStoredItemReadsBypassHeldRefreshes()
    {
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, options, new(options), clock);
        var (capability, feeds) = await ConcurrentFeedsAsync(32, queue: queue);
        capability.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .ReturnsAsync((ProviderExecutionContext _, ProviderPlaylistTracksRequest request) => ConcurrentFeedPage(request));
        await Task.WhenAll(feeds.Select(feed => service.PrepareServiceFeedsAsync(feed.Owner, new(feed.Account, 0), default)));
        var before = await Task.WhenAll(feeds.Select(feed => service.GetAsync(feed.Owner, feed.Cards[0].Id)));
        clock.UtcNow += TimeSpan.FromHours(7);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capability.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .Returns(async (ProviderExecutionContext context, ProviderPlaylistTracksRequest request) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
                await Task.Delay(2000, context.CancellationToken);
                return ConcurrentFeedPage(request);
            });
        gateway.Invocations.Clear();
        using var cancellation = new CancellationTokenSource();
        var refreshes = Task.WhenAll(feeds.Select(feed => service.PrepareServiceFeedsAsync(feed.Owner, new(feed.Account, 0), cancellation.Token)));
        try
        {
            await entered.Task.WaitAsync(CompletionDeadline);
            var timer = Stopwatch.StartNew();
            var after = await Task.WhenAll(feeds.Select(feed => service.GetAsync(feed.Owner, feed.Cards[0].Id))).WaitAsync(CompletionDeadline);
            output.WriteLine($"32 different stored mix item reads during held two-second refreshes: {timer.Elapsed.TotalMilliseconds:F1} ms; refreshes still held.");
            Assert.False(refreshes.IsCompleted);
            Assert.Equal(before.Select(mix => JsonSerializer.Serialize(mix.Songs)), after.Select(mix => JsonSerializer.Serialize(mix.Songs)));
            gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()), Times.Never);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            try { await refreshes; } catch (OperationCanceledException) { }
        }
    }
}
