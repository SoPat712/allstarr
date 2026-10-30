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
    public void TimeSeekAndRejectionRemainAvailableForScopedProblemContext()
    {
        using var store = new PlaybackDeliveryActivityStore();
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var context = Context("request");
        var stream = Stream(response, true, "managed-cache", true);
        store.StreamOpened(context, "ext-deezer-song-42", ProviderAudioQuality.Any, stream);
        store.SeekRungSelected(context, "ext-deezer-song-42", ProviderAudioQuality.Any, stream, "time-seek");
        store.SeekRungSelected(context, "ext-deezer-song-42", ProviderAudioQuality.Any, stream, "flac-duration");
        Assert.Equal("time-seek", store.StreamFor(context, "ext-deezer-song-42", ProviderAudioQuality.Any)!.SeekRung);
        store.StreamRejected(context, "ext-deezer-song-42", "Unsupported audio profile api_key=private-value");
        var source = store.StreamFor(context.Actor!.EffectiveUserId, "device", "ext-deezer-song-42")!;
        Assert.Equal("Unsupported audio profile api_key=<redacted>", source.RejectionReason);
        Assert.DoesNotContain("private-value", JsonSerializer.Serialize(source));
        Assert.Null(store.StreamFor(Guid.NewGuid(), "device", "ext-deezer-song-42"));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opened_PreservesCompleteFileLengthAndLeaseMedia(bool partial)
    {
        using var store = new PlaybackDeliveryActivityStore();
        using var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[100]) };
        if (partial) response.Content.Headers.ContentRange = new(100, 199, 1000);
        var context = Context("request");
        var stream = Stream(response, false, "lease", true);
        store.StreamOpened(context, "ext-deezer-song-42", ProviderAudioQuality.Any, stream);
        var source = store.StreamFor(context, "ext-deezer-song-42", ProviderAudioQuality.Any)!;
        Assert.Equal(partial ? 1000 : 100, source.Size);
        Assert.Same(stream.Lease.Media, source.Media);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SubsonicClientIdentityPreservesFileFactsAndPreparationSuppression(bool partial, bool preparation)
    {
        using var store = new PlaybackDeliveryActivityStore();
        using var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[100]) };
        if (partial) response.Content.Headers.ContentRange = new(100, 199, 1000);
        var context = new ProtocolExecutionContext(ProtocolKind.Subsonic, "backend", "principal",
            new AllstarrPrincipal(UserId, "subsonic", "backend", "principal", "User", false),
            "request", DateTimeOffset.UtcNow.AddMinutes(1), default, new ProtocolClientDescriptor("client"))
        { IsPreparation = preparation };
        var stream = Stream(response, false, "lease", false);
        const string itemId = "ext-apple-download-song-42";
        store.StreamOpened(context, itemId, ProviderAudioQuality.Any, stream);
        store.SeekRungSelected(context, itemId, ProviderAudioQuality.Any, stream, "flac-duration");
        Assert.Equal(!preparation, store.WasStreamOpened(itemId));
        var source = store.StreamFor(context, itemId, ProviderAudioQuality.Any);
        if (preparation)
        {
            Assert.Null(source);
            Assert.Null(store.StreamFor(context.Actor!.EffectiveUserId, "client", itemId));
        }
        else
        {
            Assert.NotNull(source);
            Assert.Same(source, store.StreamFor(context.Actor!.EffectiveUserId, "client", itemId));
            Assert.Equal(partial ? 1000 : 100, source.Size);
            Assert.Same(stream.Lease.Media, source.Media);
            Assert.Equal(partial ? "byte-range" : "flac-duration", source.SeekRung);
        }
    }

    [Fact]
    public void SourceFailureRemainsAttachedToTheOpenedPlayback()
    {
        using var store = new PlaybackDeliveryActivityStore();
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var context = Context("source-failure");
        var stream = Stream(response, false, "lease", true);
        store.StreamOpened(context, "ext-deezer-song-42", ProviderAudioQuality.Any, stream);
        stream.FileFacts.Failed = true;
        Assert.True(store.StreamFor(context.Actor!.EffectiveUserId, "device", "ext-deezer-song-42")!.Interrupted);
    }

    [Fact]
    public void InterruptedPlaybackIsScopedAndAHealthyReopenClearsIt()
    {
        using var store = new PlaybackDeliveryActivityStore();
        var context = Context("failure");
        const string item = "ext-deezer-song-42";
        store.StreamInterrupted(context, item);
        Assert.True(store.WasInterrupted(context, "device", item, DateTimeOffset.UtcNow));
        Assert.False(store.WasInterrupted(context, "other-device", item, DateTimeOffset.UtcNow));
        Assert.False(store.WasInterrupted(context, "device", "ext-deezer-song-43", DateTimeOffset.UtcNow));
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var first = Stream(response, false, "first", true);
        var next = Stream(response, false, "next", true);
        store.StreamOpened(context, item, ProviderAudioQuality.Any, first);
        Assert.False(store.WasInterrupted(context, "device", item, DateTimeOffset.UtcNow));
        store.StreamOpened(context, item, ProviderAudioQuality.Any, next);
        store.StreamInterrupted(context, item, first);
        Assert.False(store.WasInterrupted(context, "device", item, DateTimeOffset.UtcNow));
        store.StreamInterrupted(context, item, next);
        Assert.True(store.WasInterrupted(context, "device", item, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OptionalReadsCannotInterruptPlayback(bool preparation, bool originalDownload)
    {
        using var store = new PlaybackDeliveryActivityStore();
        var context = Context("optional") with { IsPreparation = preparation, IsOriginalDownload = originalDownload };
        store.StreamInterrupted(context, "ext-deezer-song-42");
        Assert.False(store.WasInterrupted(context, "device", "ext-deezer-song-42", DateTimeOffset.UtcNow));
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
