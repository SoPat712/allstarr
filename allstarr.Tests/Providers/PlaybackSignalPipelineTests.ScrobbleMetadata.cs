using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    [Theory]
    [InlineData("library")]
    [InlineData("canonical")]
    public async Task ScrobbleMetadata_ResolvesLocalReferenceBeforeProviderLookup(string reference)
    {
        string itemId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var track = await db.LibraryTracks.SingleAsync();
            if (reference == "library") itemId = $"library:{track.Id:N}";
            else
            {
                var recording = Guid.CreateVersion7();
                db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = user, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow });
                track.CanonicalRecordingId = recording;
                itemId = CanonicalRecordingRecord.DefaultPublicId(recording);
            }
            await db.SaveChangesAsync();
        }
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, itemId, TimeSpan.FromSeconds(100).Ticks));
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Jobs.SingleAsync()).State = DurableJobState.Failed;
            await db.SaveChangesAsync();
        }
        await new PlaybackScrobbleRepair(factory, jobs, playbackTracks).RecoverPendingAsync(default);
        await AssertRecoveredDestinationAsync(target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScrobbleMetadata_UsesActualStreamWhenRequestedReferenceIsUnavailable(bool partial)
    {
        var metadata = new ReferenceMetadataResolver(partial);
        var payload = Payload() with
        {
            ItemId = "ext-deezer-song-requested",
            StreamSource = new("qobuz", "streamed", null, "jellyfin", "backend", false, clock.UtcNow, "selected")
        };
        var track = await new PlaybackTrackResolver(factory, new TestBackendLibraryAccess(factory, "music"), [metadata]).ResolveAsync(payload);
        Assert.NotNull(track);
        Assert.Equal(180_000, track.DurationMilliseconds);
        Assert.Equal("qobuz:streamed", track.ProviderTrackReference);
        Assert.Contains("ext-qobuz-song-streamed", metadata.Requests);
    }

    [Fact]
    public async Task ScrobbleMetadata_DoesNotUseStreamFromAnotherBackend()
    {
        var metadata = new ReferenceMetadataResolver(false);
        var track = await new PlaybackTrackResolver(factory, new TestBackendLibraryAccess(factory, "music"), [metadata]).ResolveAsync(Payload() with
        {
            ItemId = "ext-deezer-song-requested",
            StreamSource = new("qobuz", "streamed", null, "jellyfin", "other", false, clock.UtcNow, "selected")
        });
        Assert.Null(track);
        Assert.DoesNotContain("ext-qobuz-song-streamed", metadata.Requests);
    }

    [Fact]
    public async Task ScrobbleMetadata_RefreshesIndexedTrackWithoutDuration()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.LibraryTracks.SingleAsync()).DurationMilliseconds = null;
            await db.SaveChangesAsync();
        }
        var resolver = new PlaybackTrackResolver(factory, new TestBackendLibraryAccess(factory, "music"),
            [new BackendMetadataResolver(new("Track", "Artist", null, null, 120))]);
        var track = await resolver.ResolveAsync(Payload());
        Assert.NotNull(track);
        Assert.Equal(120_000, track.DurationMilliseconds);
        Assert.NotNull(track.LibraryTrackId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task ScrobbleMetadata_StopsAfterSevenDaysFromObservation(int days)
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "missing-track", TimeSpan.FromSeconds(100).Ticks) with { ObservedAt = clock.UtcNow.AddDays(-days) });
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Jobs.SingleAsync()).State = DurableJobState.Failed;
            await db.SaveChangesAsync();
        }
        var repair = new PlaybackScrobbleRepair(factory, jobs, new RejectResolver());
        await repair.RecoverPendingAsync(default);
        await repair.RecoverPendingAsync(default);
        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.PlaybackDeliveryCheckpoints.SingleAsync();
        Assert.Equal(days > 7 ? ScopedPlaybackScrobbleOutcome.PermanentFailure : ScopedPlaybackScrobbleOutcome.Retrying, row.State);
        Assert.Equal(days > 7 ? "song-unidentified" : "metadata-unavailable", row.ProviderCode);
        Assert.Null(row.SubmissionJson);
        Assert.Single(await verify.Jobs.ToArrayAsync());
        if (days > 7) Assert.Contains("couldn't identify this song", row.SafeMessage);
    }

    [Fact]
    public async Task ScrobbleMetadata_RefreshesPartialSavedTrackAndFreezesCompletedSubmission()
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks) with
            { SubmittedTrack = new(null, null, "track-1", "Track", "Artist", null, null) });
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Jobs.SingleAsync()).State = DurableJobState.Failed;
            await db.SaveChangesAsync();
        }
        await new PlaybackScrobbleRepair(factory, jobs, playbackTracks).RecoverPendingAsync(default);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.NotNull((await db.PlaybackDeliveryCheckpoints.SingleAsync()).SubmissionJson);
            db.LibraryTracks.RemoveRange(await db.LibraryTracks.ToArrayAsync());
            await db.SaveChangesAsync();
        }
        await AssertRecoveredDestinationAsync(target);
    }

    private sealed class ReferenceMetadataResolver(bool partial) : IPlaybackMetadataResolver
    {
        public List<string> Requests { get; } = [];
        public Task<PlaybackTrackMetadata?> ResolveAsync(string itemId, CancellationToken cancellationToken)
        {
            Requests.Add(itemId);
            return Task.FromResult<PlaybackTrackMetadata?>(itemId == "ext-qobuz-song-streamed"
                ? new("Streamed song", "Artist", null, null, 180)
                : partial ? new("Requested song", "Artist", null, null, null) : null);
        }
        public Task<PlaybackArtwork?> ResolveArtworkAsync(string itemId, CancellationToken cancellationToken) => Task.FromResult<PlaybackArtwork?>(null);
    }
}
