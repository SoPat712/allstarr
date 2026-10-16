using System.Data.Common;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishingKeepAllFor10000TracksDoesNotStallAConcurrentFavoriteWrite(bool rematch)
    {
        var publication = await SeedRetentionPublicationAsync(10_000, rematch);
        var barrier = new RetentionPreparationBarrier();
        factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>().UseSqlite(database.ConnectionString)
            .AddInterceptors(new SqlitePragmaInterceptor(), barrier).Options);
        var orchestration = RetentionOrchestration();
        var (_, _, _, favorites) = CreateInterestPipeline();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task publish = Task.CompletedTask;
        Task favorite = Task.CompletedTask;
        try
        {
            publish = Task.Run(() => PublishRetentionAsync(orchestration, publication, rematch, stop.Token), stop.Token);
            await barrier.Opened.Task.WaitAsync(TimeSpan.FromSeconds(10), stop.Token);
            favorite = Task.Run(() => favorites.RecordAsync(Mutation(owner, CanonicalItemId(),
                FavoriteOperation.Favorite, "sync-publication-heart"), stop.Token), stop.Token);

            await favorite.WaitAsync(TimeSpan.FromSeconds(30), stop.Token);

            barrier.Resume.TrySetResult();
            await publish.WaitAsync(TimeSpan.FromSeconds(30), stop.Token);
            await using var verify = new AllstarrDbContext(database.Options);
            Assert.Equal(10_000, await verify.Jobs.CountAsync(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName));
            Assert.True((await verify.Set<FavoriteStateRecord>().SingleAsync()).IsFavorite);
            Assert.NotNull((await verify.PlaylistSourceSnapshots.SingleAsync()).PublishedAt);
        }
        finally
        {
            barrier.Resume.TrySetResult();
            await stop.CancelAsync();
            try { await Task.WhenAll(publish, favorite); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PublicationPreparationReusesStableDataAndRecomputesChangedTargets(bool rematch, bool changed)
    {
        var published = await Service().PublishAsync(Request());
        var publication = await SeedRetentionPublicationAsync(1, rematch);
        var probe = new PublicationProjectionProbe();
        var boundary = new BeforeRetentionWriter(async () =>
        {
            if (!changed) return;
            await using var edit = new AllstarrDbContext(database.Options);
            (await edit.ProviderTrackIdentities.SingleAsync(item => item.ExternalId == "large-track-00000")).CanonicalRecordingId = recordingId;
            (await edit.TrackMatches.SingleAsync()).CanonicalRecordingId = recordingId;
            await edit.SaveChangesAsync();
        });
        factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>().UseSqlite(database.ConnectionString)
            .AddInterceptors(new SqlitePragmaInterceptor(), boundary, probe).Options);

        await PublishRetentionAsync(RetentionOrchestration(), publication, rematch, default);

        Assert.True(probe.BeforeWriter > 0);
        Assert.Equal(changed, probe.InWriter > 0);
        await using var verify = new AllstarrDbContext(database.Options);
        var references = await verify.ManagedFileReferences.Where(item => item.ReleasedAt == null &&
            item.ReferenceKey.StartsWith($"playlist:{publication.Link.Id:N}:")).ToArrayAsync();
        if (changed) Assert.Equal(published.ManagedFileId, Assert.Single(references).ManagedFileId);
        else Assert.Empty(references);
    }

    private sealed record RetentionPublication(PlaylistLinkRecord Link, PlaylistSourceSnapshotRecord Snapshot,
        IReadOnlyDictionary<Guid, Guid?> Decisions, Guid[] ExternalIds);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationPreparationUsesThePendingDecisionInsteadOfThePublishedRoute(bool rematch)
    {
        var kept = await Service().PublishAsync(Request());
        var publication = await SeedRetentionPublicationAsync(1, rematch);
        await using (var edit = new AllstarrDbContext(database.Options))
        {
            if (!rematch)
            {
                edit.PlaylistSourceSnapshots.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = owner,
                    PlaylistLinkId = publication.Link.Id,
                    ProviderAccountId = account,
                    SnapshotVersion = 1,
                    ProviderRevision = "older-empty",
                    Name = "Previously published",
                    PayloadSha256 = PlaylistHash("older-empty"),
                    CorrelationId = "previous-publication",
                    RetrievedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    PublishedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
                });
            }
            var pending = await edit.TrackMatches.SingleAsync();
            var old = new TrackMatchRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = owner,
                ExternalSnapshotId = pending.ExternalSnapshotId,
                CanonicalRecordingId = pending.CanonicalRecordingId,
                State = TrackMatchState.Accepted,
                Confidence = 1,
                Threshold = 0.9,
                DecisionVersion = 1,
                SourceSnapshotVersion = 1,
                PolicyVersion = "single-entry",
                CorrelationId = "previous-publication",
                DecidedAt = pending.DecidedAt.AddMinutes(-1)
            };
            pending.CanonicalRecordingId = recordingId;
            pending.DecisionVersion = 2;
            edit.TrackMatches.Add(old);
            (await edit.PlaylistSourceEntries.SingleAsync()).PublishedTrackMatchId = old.Id;
            await edit.SaveChangesAsync();
        }

        await PublishRetentionAsync(RetentionOrchestration(), publication, rematch, default);

        await using var verify = new AllstarrDbContext(database.Options);
        Assert.Equal(kept.ManagedFileId, (await verify.ManagedFileReferences.SingleAsync(item => item.ReleasedAt == null &&
            item.ReferenceKey.StartsWith($"playlist:{publication.Link.Id:N}:"))).ManagedFileId);
        Assert.Equal(publication.Decisions.Values.Single(), (await verify.PlaylistSourceEntries.SingleAsync()).PublishedTrackMatchId);
    }

    private async Task<RetentionPublication> SeedRetentionPublicationAsync(int count, bool rematch)
    {
        var playlist = await SeedLargeRetentionPlaylistAsync(count, snapshotVersion: rematch ? 1 : 2);
        await using var db = new AllstarrDbContext(database.Options);
        var snapshot = await db.PlaylistSourceSnapshots.SingleAsync();
        if (!rematch) snapshot.PublishedAt = null;
        var entries = await db.PlaylistSourceEntries.ToArrayAsync();
        var metadata = await db.ExternalMetadataSnapshots.ToDictionaryAsync(item => item.Id);
        var canonicals = await db.ProviderTrackIdentities.ToDictionaryAsync(item => item.Id, item => item.CanonicalRecordingId);
        var decisions = new Dictionary<Guid, Guid?>();
        foreach (var entry in entries)
        {
            var match = new TrackMatchRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = owner,
                ExternalSnapshotId = entry.ExternalMetadataSnapshotId,
                CanonicalRecordingId = canonicals[metadata[entry.ExternalMetadataSnapshotId].ProviderTrackIdentityId!.Value],
                State = TrackMatchState.Accepted,
                Confidence = 1,
                Threshold = 0.9,
                DecisionVersion = 1,
                SourceSnapshotVersion = 1,
                PolicyVersion = "single-entry",
                CorrelationId = "publication",
                DecidedAt = DateTimeOffset.UtcNow
            };
            db.TrackMatches.Add(match);
            decisions.Add(entry.Id, match.Id);
        }
        await db.SaveChangesAsync();
        return new(await db.PlaylistLinks.SingleAsync(), snapshot, decisions, metadata.Keys.ToArray());
    }

    private PlaylistOrchestrationService RetentionOrchestration()
    {
        var (clock, _, kept, _) = PlaylistInterestServices();
        return new(factory, null!, null!, new PlaylistMaterializationPlanner(), new TrackMatchDecisionEngine(),
            null!, clock, new KeyedAsyncLock(), new TestBackendLibraryAccess(factory, "music"), kept: kept);
    }

    private Task PublishRetentionAsync(PlaylistOrchestrationService orchestration, RetentionPublication publication,
        bool rematch, CancellationToken token) => rematch
        ? orchestration.PublishExistingDecisionsAsync(PlaylistContext(), publication.Link.Id, publication.Link.Revision,
            publication.Snapshot.Id, publication.ExternalIds, token)
        : orchestration.PublishGenerationAsync(publication.Link, publication.Snapshot, publication.Decisions, token);

    private sealed class PublicationProjectionProbe : DbCommandInterceptor
    {
        public int BeforeWriter;
        public int InWriter;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"external_metadata_snapshots\"", StringComparison.Ordinal))
            {
                if (command.Transaction == null) Interlocked.Increment(ref BeforeWriter);
                else Interlocked.Increment(ref InWriter);
            }
            return ValueTask.FromResult(result);
        }
    }
}
