using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Models.Domain;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task MergedSearchStopsWaitingAtThreeSecondsAndReusesTheLateProviderCache()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        provider.Setup(service => service.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(async () =>
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(10), clock);
                entered.TrySetResult();
                await delay;
                return ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer",
                    [new(new("deezer", ProviderResourceKind.Track, "late"), "Late track", [new("Artist")])]));
            });
        var gateway = BudgetGateway(clock, new TestMemoryApplicationCache(), provider.Object);
        var service = new MusicSearchService(gateway, timeProvider: clock);
        var native = new Mock<INativeMusicSearch<string>>();
        native.Setup(item => item.SearchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new[] { new NativeMusicItem<string>(MusicItemKind.Song, "native", "Track", "Artist", "complete native row") });
        var request = new MusicSearchRequest("Track", new(10), MusicSearchWindow.None, MusicSearchWindow.None,
            MusicSearchWindow.None)
        { LimitProviderWait = true };
        var context = Context();
        var first = service.SearchAsync(context, request, native.Object);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(3));
        var partial = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("complete native row", Assert.Single(partial.Songs).Native!.Payload);
        clock.Advance(TimeSpan.FromSeconds(7));
        var repeated = await service.SearchAsync(context, request, native.Object).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(repeated.Songs, item => item.Song?.ExternalId == "late");
        VerifySearchCount(provider, "songs", 1);
    }
}
