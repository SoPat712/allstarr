using allstarr.Core.Downloads;
using allstarr.Core.Enrichment;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public sealed class PromotionSourceOrderTests
{
    private static readonly string[] Order = ["deezer", "apple-musickit", "spotiflac-deezer"];
    private static readonly PromotionProfile Profile = new() { AllowLossy = true, HiResCeiling = new(24, 48000) };

    private static PromotionTrack Track(Guid recording) => new()
    {
        RecordingId = recording,
        Song = new() { ExternalProvider = "deezer", ExternalId = "built-in", Codec = "mp3", SampleRate = 44100, Bitrate = 320 }
    };

    private static VerifiedProviderDownloadArtifact Artifact(string provider, string codec, int? bits, int rate) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "/song", new string('a', 64), 1, Guid.NewGuid(), Guid.NewGuid(), provider, null,
            ProviderDownloadArtifactState.Verified, null)
        { Codec = codec, BitDepth = bits, SampleRate = rate };

    [Fact]
    public void AnAddWithOneStoredLossyCopyAlsoPlansTheListenersOtherServices()
    {
        var recording = Guid.NewGuid();
        var track = Track(recording);
        var sources = PromotionAcquisition.Sources(Profile, track, [track.Song], Order);

        Assert.Equal(new[] { "apple-musickit", "spotiflac-deezer", "deezer" }, sources.Select(item => item.ProviderId));
        Assert.All(sources[..2], item => Assert.True(item.Recording && item.ExternalId == recording.ToString("D")));
        Assert.False(sources[2].Recording);
        var command = PromotionDownloadJobHandler.Command(new(Guid.NewGuid(), recording, sources, Profile), sources[0],
            Guid.NewGuid(), Guid.NewGuid(), "correlation", 1);
        Assert.Equal("allstarr", command.ProviderId);
        Assert.Equal(recording.ToString("D"), command.ExternalTrackId);
        Assert.Equal(new[] { "apple-musickit" }, command.ProviderPriority);
        Assert.False(command.AcceptArtifact!(Artifact("deezer", "mp3", null, 44100)));
        Assert.True(command.AcceptArtifact!(Artifact("apple-musickit", "flac", 24, 48000)));
    }

    [Fact]
    public void ACopyThatSaysNothingOfItsQualityKeepsTheListenersOrder()
    {
        var track = Track(Guid.NewGuid());
        track.Song.Codec = null;
        var stored = new Song { ExternalProvider = "spotiflac-deezer", ExternalId = "ext", Codec = "flac", BitDepth = 16, SampleRate = 44100 };
        var sources = PromotionAcquisition.Sources(Profile, track, [stored, track.Song], Order);

        Assert.Equal(new[] { "spotiflac-deezer", "deezer", "apple-musickit" }, sources.Select(item => item.ProviderId));
        Assert.Equal(new[] { false, false, true }, sources.Select(item => item.Recording));
    }

    [Fact]
    public void AServiceRejectedForAnEarlierSongIsStillPlannedForTheNextOne()
    {
        var earlier = Track(Guid.NewGuid());
        earlier.RejectedProviders.Add("apple-musickit");
        Assert.DoesNotContain(PromotionAcquisition.Sources(Profile, earlier, [], Order), item => item.ProviderId == "apple-musickit");
        var next = Track(Guid.NewGuid());
        Assert.Contains(PromotionAcquisition.Sources(Profile, next, [], Order), item => item.ProviderId == "apple-musickit");
    }

    [Fact]
    public async Task ALossyFileFromTheFirstSourcePassesTheAddToTheLosslessOne()
    {
        var recording = Guid.NewGuid();
        var sources = new PromotionDownloadSource[]
        {
            new("deezer", "built-in", null, null, null, null),
            new("apple-musickit", recording.ToString("D"), null, null, null, null, Recording: true)
        };
        var tried = new List<string>();
        var completion = await PromotionDownloadJobHandler.RunAsync(new(Guid.NewGuid(), recording, sources, Profile), source =>
        {
            tried.Add(source.ProviderId);
            return Task.FromResult(source.ProviderId == "deezer"
                ? ManagedTrackDownloadResult.Failure("managed_download_route_exhausted", "Below the floor.")
                : ManagedTrackDownloadResult.Success(Artifact("apple-musickit", "flac", 24, 48000)));
        });

        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        Assert.Equal(new[] { "deezer", "apple-musickit" }, tried);
    }

    [Fact]
    public async Task ASourceThatFailedForAnEarlierSongIsTriedForTheNext()
    {
        var sources = new PromotionDownloadSource[]
        {
            new("apple-musickit", "first", null, null, null, null),
            new("spotiflac-deezer", "first", null, null, null, null)
        };
        var tried = new List<string>();
        var first = await PromotionDownloadJobHandler.RunAsync(new(Guid.NewGuid(), Guid.NewGuid(), sources, Profile), source =>
        {
            tried.Add(source.ProviderId);
            return Task.FromResult(source.ProviderId == "apple-musickit"
                ? ManagedTrackDownloadResult.Failure("managed_download_failed", "Failed.")
                : ManagedTrackDownloadResult.Success(Artifact("spotiflac-deezer", "flac", 16, 44100)));
        });
        Assert.Equal(DurableJobCompletionKind.Succeeded, first.Kind);
        var next = await PromotionDownloadJobHandler.RunAsync(new(Guid.NewGuid(), Guid.NewGuid(),
            [new("apple-musickit", "next", null, null, null, null)], Profile), source =>
        {
            tried.Add(source.ProviderId);
            return Task.FromResult(ManagedTrackDownloadResult.Success(Artifact("apple-musickit", "flac", 24, 48000)));
        });

        Assert.Equal(DurableJobCompletionKind.Succeeded, next.Kind);
        Assert.Equal(new[] { "apple-musickit", "spotiflac-deezer", "apple-musickit" }, tried);
    }

    [Fact]
    public async Task AnAddIsHeldOnlyWhenEverySourceFallsBelowTheFloorAndWaitsForAPausedOne()
    {
        var sources = Order.Select(provider => new PromotionDownloadSource(provider, "id", null, null, null, null)).ToArray();
        var tried = new List<string>();
        var held = await PromotionDownloadJobHandler.RunAsync(new(Guid.NewGuid(), Guid.NewGuid(), sources, Profile), source =>
        {
            tried.Add(source.ProviderId);
            // A lossy file handed back as a success still falls below a lossless floor.
            return Task.FromResult(source.ProviderId == "deezer"
                ? ManagedTrackDownloadResult.Success(Artifact("deezer", "mp3", null, 44100))
                : ManagedTrackDownloadResult.Failure("managed_download_route_exhausted", "Below the floor."));
        });
        Assert.Equal(DurableJobCompletionKind.Failed, held.Kind);
        Assert.Equal("promotion_sources_exhausted", held.ErrorCode);
        Assert.Equal(Order, tried);

        var waiting = await PromotionDownloadJobHandler.RunAsync(new(Guid.NewGuid(), Guid.NewGuid(), sources, Profile), source =>
            Task.FromResult(source.ProviderId == "apple-musickit"
                ? ManagedTrackDownloadResult.Retry("managed_download_provider_paused", "Paused.")
                : ManagedTrackDownloadResult.Failure("managed_download_route_exhausted", "Below the floor.")));
        Assert.Equal(DurableJobCompletionKind.Retry, waiting.Kind);
    }
}
