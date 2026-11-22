using allstarr.Core.Storage;

namespace allstarr.Core.Playlists;

// Every stored match keeps all the candidates it weighed, several kilobytes a row. Playlist publication and rematch
// checks read only a match's decision, so they leave that list in the database.
internal static class PlaylistMatchReads
{
    public static IQueryable<TrackMatchRecord> WithoutCandidates(IQueryable<TrackMatchRecord> query) =>
        query.Select(item => new TrackMatchRecord
        {
            Id = item.Id,
            OwnerUserId = item.OwnerUserId,
            ExternalSnapshotId = item.ExternalSnapshotId,
            LibraryTrackId = item.LibraryTrackId,
            CanonicalRecordingId = item.CanonicalRecordingId,
            State = item.State,
            Confidence = item.Confidence,
            Threshold = item.Threshold,
            DecisionVersion = item.DecisionVersion,
            SourceSnapshotVersion = item.SourceSnapshotVersion,
            LibraryIndexRevision = item.LibraryIndexRevision,
            MatcherVersion = item.MatcherVersion,
            PolicyVersion = item.PolicyVersion,
            CandidateResultsJson = "[]",
            ReasonsJson = item.ReasonsJson,
            WarningsJson = item.WarningsJson,
            CorrelationId = item.CorrelationId,
            DecidedAt = item.DecidedAt,
            Revision = item.Revision
        });
}
