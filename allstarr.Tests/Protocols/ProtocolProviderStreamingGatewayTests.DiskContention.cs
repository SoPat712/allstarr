using System.Reflection;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProviderSearchAnswersWhileDiskGateAndBackgroundImagesAreBusy(bool stream, bool holdAfterRead)
    {
        var root = Path.Combine(Path.GetTempPath(), $"allstarr-search-contention-{Guid.NewGuid():N}");
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var clock = new ExternalMetadataClock(time);
        using var memory = new MemoryApplicationCache(clock);
        using var disk = new FileMediaApplicationCache(new FileMediaCacheOptions(root), clock, NullLogger<FileMediaApplicationCache>.Instance);
        var cache = new HybridApplicationCache(memory, disk, clock: clock);
        var gate = (SemaphoreSlim)typeof(FileMediaApplicationCache).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(disk)!;
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = DelayedSearchProvider("deezer", time, TimeSpan.Zero);
        provider.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(() => { called.TrySetResult(); return answer.Task; });
        var gateway = BudgetGateway(time, cache, provider.Object);
        var context = Context();
        var held = false;
        Task[] images = [];
        Task? searching = null;
        try
        {
            if (!holdAfterRead) { await gate.WaitAsync(); held = true; }
            async Task SearchAsync()
            {
                if (stream)
                {
                    var batches = 0;
                    var late = await gateway.StreamSearchAsync(context, "olivia rodrigo", 10, true, false, false, false,
                        (_, result, _, _) => { Assert.Equal(["second", "first"], result.Songs.Select(song => song.ExternalId)); batches++; return Task.CompletedTask; });
                    Assert.Empty(late);
                    Assert.Equal(1, batches);
                }
                else
                {
                    var result = await gateway.SearchAsync(context, "olivia rodrigo", 10, 0, 0);
                    Assert.Equal(["second", "first"], result.Songs.Select(song => song.ExternalId));
                }
            }
            searching = SearchAsync();
            if (holdAfterRead)
            {
                await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await gate.WaitAsync();
                held = true;
            }
            images = Enumerable.Range(0, 2).Select(index => cache.SetStringAsync($"artwork:payload:v1:background-{index}", new string('x', 65536))).ToArray();
            answer.TrySetResult(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Track, "second"), "Vampire", [new("Olivia Rodrigo")], duration: TimeSpan.FromMilliseconds(241160)),
                 new(new("deezer", ProviderResourceKind.Track, "first"), "Drivers License", [new("Olivia Rodrigo")])])));
            await searching.WaitAsync(TimeSpan.FromMilliseconds(3500));
            await Task.WhenAll(images).WaitAsync(TimeSpan.FromMilliseconds(3500));
            await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
            gate.Release();
            held = false;
            await disk.FlushWritesAsync();
            using var restoredMemory = new MemoryApplicationCache(clock);
            using var restoredDisk = new FileMediaApplicationCache(new FileMediaCacheOptions(root), clock, NullLogger<FileMediaApplicationCache>.Instance);
            var restored = new HybridApplicationCache(restoredMemory, restoredDisk, clock: clock);
            var result = await BudgetGateway(time, restored, provider.Object).SearchAsync(context, "olivia rodrigo", 10, 0, 0);
            Assert.Equal(["second", "first"], result.Songs.Select(song => song.ExternalId));
            Assert.Equal(TimeSpan.FromMilliseconds(241160), result.Songs[0].PreciseDuration);
            VerifySearchCount(provider, "songs", 1);
            await restoredDisk.FlushWritesAsync();
        }
        finally
        {
            answer.TrySetCanceled();
            if (held) gate.Release();
            try { if (searching != null) await searching.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { }
            await Task.WhenAll(images).WaitAsync(TimeSpan.FromSeconds(5));
            await disk.FlushWritesAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
