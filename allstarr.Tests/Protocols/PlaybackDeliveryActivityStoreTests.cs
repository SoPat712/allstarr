using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class PlaybackDeliveryActivityStoreTests
{
    [Theory]
    [InlineData(false, "lease", false, "sequential")]
    [InlineData(false, "lease", true, "sequential")]
    [InlineData(true, "managed-cache", true, "byte-range")]
    [InlineData(true, "managed-progressive-cache", true, "progressive-cache")]
    [InlineData(true, "managed-progressive-cache", false, "sequential-cache")]
    public void Opened_RecordsTheServingRevisionAndHonestSeekEvidence(bool cached, string leaseId, bool ranges, string rung)
    {
        using var store = new PlaybackDeliveryActivityStore();
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var context = Context("private-correlation");
        var stream = Stream(response, cached, leaseId, ranges);
        store.StreamOpened(context, "ext-deezer-song-42", ProviderAudioQuality.Any, stream);
        var source = store.StreamFor(context, "ext-deezer-song-42", ProviderAudioQuality.Any)!;
        Assert.Equal(4, source.AccountRevision);
        Assert.Equal(rung, source.SeekRung);
        Assert.DoesNotContain("private-correlation", JsonSerializer.Serialize(source), StringComparison.Ordinal);
    }

    [Fact]
    public void LazyEvidenceCannotOverwriteANewerRequestOrAnotherQuality()
    {
        using var store = new PlaybackDeliveryActivityStore();
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var first = Context("first");
        var next = Context("first");
        var nextStream = Stream(response, false, "lease", false);
        var stream = Stream(response, false, "lease", false);
        store.StreamOpened(first, "ext-deezer-song-42", ProviderAudioQuality.Any, stream);
        store.StreamOpened(next, "ext-deezer-song-42", ProviderAudioQuality.Lossless, nextStream);
        store.SeekRungSelected(first, "ext-deezer-song-42", ProviderAudioQuality.Any, stream, "flac-duration");
        Assert.Equal("flac-duration", store.StreamFor(first, "ext-deezer-song-42", ProviderAudioQuality.Any)!.SeekRung);
        Assert.Equal("sequential", store.StreamFor(next.Actor!.EffectiveUserId, "device", "ext-deezer-song-42")!.SeekRung);
        store.StreamOpened(next, "ext-deezer-song-42", ProviderAudioQuality.Any, nextStream);
        store.SeekRungSelected(first, "ext-deezer-song-42", ProviderAudioQuality.Any, stream, "mp3-duration");
        Assert.Equal("sequential", store.StreamFor(next, "ext-deezer-song-42", ProviderAudioQuality.Any)!.SeekRung);
    }

    [Fact]
    public void DurationEvidenceDoesNotDowngradeAProgressiveReader()
    {
        using var store = new PlaybackDeliveryActivityStore();
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var context = Context("request");
        var stream = Stream(response, false, "lease", false);
        store.StreamOpened(context, "ext-deezer-song-42", ProviderAudioQuality.Any, stream);
        store.SeekRungSelected(context, "ext-deezer-song-42", ProviderAudioQuality.Any, stream, "progressive-cache");
        store.SeekRungSelected(context, "ext-deezer-song-42", ProviderAudioQuality.Any, stream, "flac-duration");
        Assert.Equal("progressive-cache", store.StreamFor(context, "ext-deezer-song-42", ProviderAudioQuality.Any)!.SeekRung);
    }

    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid AccountId = Guid.CreateVersion7();
    private static ProtocolExecutionContext Context(string correlation) => new(
        ProtocolKind.Jellyfin, "backend", "principal",
        new AllstarrPrincipal(UserId, "jellyfin", "backend", "principal", "User", false),
        correlation, DateTimeOffset.UtcNow.AddMinutes(1), default, new ProtocolClientDescriptor("client", "device"));
    private static ProtocolProviderStream Stream(HttpResponseMessage response, bool cached, string leaseId, bool ranges) => new(
        response, new ProviderStreamLease(leaseId, new Uri("https://media.example.test/audio"),
            DateTimeOffset.UtcNow.AddMinutes(1), ranges, ranges, new("audio/flac", "flac", "flac"),
            ProviderStreamRetryBehavior.DoNotRetry), "deezer", "42", AccountId,
        IsCached: cached, ServingAccountRevision: 4);
}
