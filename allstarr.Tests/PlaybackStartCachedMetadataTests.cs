using allstarr.Core.Capabilities;
using allstarr.Core.Playback;

namespace allstarr.Tests;

public sealed class PlaybackStartCachedMetadataTests
{
    [Fact]
    public async Task RetainedOriginalKeepsCatalogLabelsAfterCacheRestartWithoutProviderMetadata()
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        using (var response = fixture.Response())
        {
            await fixture.WrapAsync(fixture.Stream(response));
            await response.Content.CopyToAsync(Stream.Null);
        }
        await fixture.RestartCacheAsync();
        using var timing = new PlaybackStartTimings(fixture.Clock);
        timing.Enter(); timing.Enable();
        var gateway = fixture.Gateway((_, _) => throw new InvalidOperationException("A cached original must not open a provider."));
        using var replay = (await gateway.OpenStreamAsync(fixture.Context, fixture.Track.ProviderId,
            fixture.Track.Value, ProviderAudioQuality.Any, "bytes=0-"))!.Response;
        var sample = timing.Snapshot("completed");
        Assert.True(sample.Cached);
        Assert.Equal("Original track", sample.Title);
        Assert.Equal("Artist", sample.Artist);
        Assert.Equal(0, fixture.LeaseCalls);
        Assert.Equal(fixture.Bytes, await replay.Content.ReadAsByteArrayAsync());
    }
}
