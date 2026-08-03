using allstarr.Core.Intelligence;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class ListeningHistoryImportJobTests
{
    [Fact]
    public void CreateEventPreservesClassificationAndUsesResolvedLocalIdentityWhenAvailable()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var importId = Guid.NewGuid();
        var canonicalId = Guid.NewGuid();
        var libraryTrackId = Guid.NewGuid();
        var scope = new IntelligenceScope(tenantId, userId, "jellyfin", "server", "music");
        var payload = new ListeningHistoryImportJobPayload(importId, scope, new string('a', 64), 1);
        var listenedAt = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var row = new ListeningHistoryImportRow(
            7, new string('b', 64), new string('c', 64), listenedAt.AddMinutes(-3), listenedAt,
            180_000, "Song", "Artist", "Album", "spotify:track:1111111111111111111111",
            "desktop", "trackdone", "trackdone", true, listenedAt.AddHours(-1), true,
            ListeningHistoryImportClassification.Completed, "track_finished");
        var identity = new ProviderTrackIdentityRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CanonicalRecordingId = canonicalId,
            ProviderId = "spotify"
        };
        var canonical = new CanonicalRecordingRecord
        {
            Id = canonicalId,
            TenantId = tenantId,
            MusicBrainzRecordingId = "11111111-1111-1111-1111-111111111111",
            Isrc = "USABC1234567"
        };
        var libraryTrack = new LibraryTrackRecord
        {
            Id = libraryTrackId,
            CanonicalRecordingId = canonicalId,
            DurationMilliseconds = 200_000,
            MusicBrainzRecordingId = canonical.MusicBrainzRecordingId,
            Isrc = canonical.Isrc
        };

        var completed = ListeningHistoryImportJobHandler.CreateEvent(
            payload, row, new string('d', 64), identity, canonical, libraryTrack, enrichWithMusicBrainz: true);
        var skipped = ListeningHistoryImportJobHandler.CreateEvent(
            payload,
            row with { Classification = ListeningHistoryImportClassification.Skipped, ReasonCode = "spotify_skipped" },
            new string('e', 64), null, null, null, enrichWithMusicBrainz: true);
        var unresolved = ListeningHistoryImportJobHandler.CreateEvent(
            payload, row, new string('f', 64), null, null, null, enrichWithMusicBrainz: true);

        Assert.Equal(ListeningEventState.Completed, completed.State);
        Assert.Equal(listenedAt, completed.ListenedAt);
        Assert.Equal(libraryTrackId, completed.LibraryTrackId);
        Assert.Equal(canonicalId, completed.CanonicalRecordingId);
        Assert.Equal("library:" + libraryTrackId.ToString("N"), completed.TrackReference);
        Assert.Equal(200_000, completed.DurationMilliseconds);
        Assert.Equal(ListeningEventState.Skipped, skipped.State);
        Assert.Null(skipped.ListenedAt);
        Assert.Equal(MusicBrainzEnrichmentState.Pending, unresolved.MusicBrainzEnrichmentState);
        Assert.Equal("spotify:" + row.SourceItemKey, skipped.TrackReference);
        Assert.DoesNotContain("1111111111111111111111", skipped.TrackReference, StringComparison.Ordinal);
        Assert.Contains("private", completed.ImportProvenance, StringComparison.Ordinal);
    }

    [Fact]
    public void StateTransferExpiresPrivateArtifactsAndCancelsTheirRunningJobs()
    {
        var jobId = Guid.NewGuid();
        var import = new ListeningHistoryImportRecord
        {
            State = ListeningHistoryImportState.Running,
            JobId = jobId,
            Revision = 1
        };
        var job = new DurableJobRecord
        {
            Id = jobId,
            State = DurableJobState.Running,
            Revision = 2
        };
        var attempt = new JobAttemptRecord { JobId = jobId };
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        var jobIds = ListeningHistoryImportStateTransfer.ExpireActiveImports([import]);
        ListeningHistoryImportStateTransfer.CancelJobs([job], jobIds, now);
        ListeningHistoryImportStateTransfer.CancelAttempts([attempt], jobIds, now);

        Assert.Equal(ListeningHistoryImportState.Expired, import.State);
        Assert.Equal(jobId, import.JobId);
        Assert.Equal(DurableJobState.Cancelled, job.State);
        Assert.Equal(now, job.CompletedAt);
        Assert.Equal("cancelled", attempt.Outcome);
    }
}
