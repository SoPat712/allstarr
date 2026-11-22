using System.Data.Common;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Tests;

public sealed partial class PlaylistOrchestrationIntegrationTests
{
    [Fact]
    public async Task PublicationAndRematchChecksReadDecisionsWithoutTheirCandidateLists()
    {
        var probe = new MatchColumnProbe();
        var factory = new DbFactory(new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite(_database.ConnectionString).AddInterceptors(new SqlitePragmaInterceptor(), probe).Options);
        var service = new PlaylistOrchestrationService(factory, _source, new FakeTargetResolver(_target),
            new PlaylistMaterializationPlanner(), new TrackMatchDecisionEngine(), _trackMatches, new Clock(_now),
            new KeyedAsyncLock(), new TestBackendLibraryAccess(factory, "music"),
            new CollectingLogger<PlaylistOrchestrationService>(_logs));
        _source.Snapshot = Snapshot("slim-decisions",
            Entry(0, "one", "source-1", "One"), Entry(1, "two", "source-2", "Two"));
        await service.RunAsync(Context(), new(_link, 1));
        var rematches = new PlaylistRematchService(factory,
            new DurablePlaylistProjectionReader(factory, new TestBackendLibraryAccess(factory, "music")),
            new TrackMatchDecisionEngine(), new TestBackendLibraryAccess(factory, "music"));
        await rematches.PreviewAsync(_user, playlistLinkId: _link);

        await using var db = await factory.CreateDbContextAsync();
        var externalIds = await db.ExternalMetadataSnapshots.Select(item => item.Id).ToArrayAsync();
        var latest = await DurablePlaylistProjectionReader.LoadLatestRematchDecisionsAsync(db, _user, externalIds, default);
        var stored = await db.TrackMatches.AsNoTracking().ToListAsync();

        // The library-candidate lookup during matching, the rematch preview and the latest-decision read each pick
        // a snapshot's newest decision with a grouped subquery; none of them may select the candidate lists.
        Assert.True(probe.Sql.Count(MatchColumnProbe.PicksNewestDecisions) >= 3);
        Assert.DoesNotContain(probe.Sql.Where(MatchColumnProbe.PicksNewestDecisions), MatchColumnProbe.LoadsCandidateLists);
        Assert.Equal(2, latest.Count);
        foreach (var decision in latest.Values)
        {
            var full = stored.Single(item => item.Id == decision.Id);
            Assert.Equal((full.ExternalSnapshotId, full.State, full.MatcherVersion, full.SourceSnapshotVersion, full.DecisionVersion,
                    full.LibraryTrackId, full.CanonicalRecordingId, full.Revision),
                (decision.ExternalSnapshotId, decision.State, decision.MatcherVersion, decision.SourceSnapshotVersion,
                    decision.DecisionVersion, decision.LibraryTrackId, decision.CanonicalRecordingId, decision.Revision));
            Assert.Equal("[]", decision.CandidateResultsJson);
        }
    }

    [Fact]
    public async Task CandidateProbeSeesAFullMatchRead()
    {
        var probe = new MatchColumnProbe();
        await using var db = new AllstarrDbContext(new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite(_database.ConnectionString).AddInterceptors(new SqlitePragmaInterceptor(), probe).Options);
        await db.TrackMatches.AsNoTracking().ToListAsync();
        Assert.Contains(probe.Sql, MatchColumnProbe.LoadsCandidateLists);
        probe.Sql.Clear();
        await PlaylistMatchReads.WithoutCandidates(db.TrackMatches.AsNoTracking()).ToListAsync();
        Assert.DoesNotContain(probe.Sql, MatchColumnProbe.LoadsCandidateLists);
    }

    private sealed class MatchColumnProbe : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];

        // A full read selects the column through a table alias; a slim read names it only as the alias of an empty list.
        public static bool LoadsCandidateLists(string sql) =>
            sql.Contains("FROM \"track_matches\"", StringComparison.Ordinal) &&
            sql.Contains(".\"CandidateResultsJson\"", StringComparison.Ordinal);

        public static bool PicksNewestDecisions(string sql) =>
            sql.Contains("FROM \"track_matches\"", StringComparison.Ordinal) &&
            sql.Contains("MAX(", StringComparison.Ordinal) && sql.Contains("GROUP BY", StringComparison.Ordinal);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            lock (Sql) Sql.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
