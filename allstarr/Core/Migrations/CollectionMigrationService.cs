using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Migrations;

public sealed record MigrationSelection(string SourceId, bool KeepInSync = false);
public sealed record MigrationCounts(int Selected, int Local, int Streamed, int Review, int Unmatched, int Unsupported, int Skipped)
{
    public int Accepted => Local + Streamed;
}
public sealed class MigrationRun
{
    public Guid ProviderAccountId { get; set; }
    public long AccountRevision { get; set; }
    public string SourceUserId { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string BackendInstanceId { get; set; } = "";
    public string BackendPrincipalId { get; set; } = "";
    public string State { get; set; } = "choosing";
    public string Phase { get; set; } = "choosing";
    public Guid? JobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
public sealed class MigrationCollection
{
    public string Kind { get; set; } = "playlist";
    public string Name { get; set; } = "";
    public int? Expected { get; set; }
    public int Read { get; set; }
    public string State { get; set; } = "available";
    public bool KeepInSync { get; set; }
    public string? Cursor { get; set; }
    public string? SourceRevision { get; set; }
    public string? ETag { get; set; }
    public int Pages { get; set; }
    public int Matched { get; set; }
    public int AppliedPosition { get; set; }
    public int Verified { get; set; }
    public string? VerifyCursor { get; set; }
    public Guid? LinkId { get; set; }
    public Guid? SnapshotId { get; set; }
    public Guid? ReceiptRunId { get; set; }
    public DateTimeOffset? CreateStartedAt { get; set; }
    public string[]? DestinationIdsBefore { get; set; }
    public string? Error { get; set; }
}
public sealed record MigrationCollectionStatus(Guid Id, string SourceId, MigrationCollection Collection, MigrationCounts Counts)
{
    public int? Unread => Collection.Expected is { } expected ? Math.Max(0, expected - Collection.Read) : null;
}
public sealed record MigrationStatus(Guid Id, MigrationRun Run, IReadOnlyList<MigrationCollectionStatus> Collections,
    MigrationCounts Total, bool Provisional);
public sealed record MigrationReviewItem(Guid CollectionId, Guid SourceEntryId, int Position, Guid ExternalSnapshotId,
    JsonElement Metadata, TrackMatchRecord? Match, PlaylistSyncEntryResultRecord? Receipt, PersistedPlaylistPreviewEntry? Preview);

public sealed class CollectionMigrationService(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs,
    IProviderRouter router, ITrackMatchRepository matches, IPlaylistPersistenceService playlists,
    PlaylistOrchestrationService orchestration, IBackendPlaylistTargetResolver targets, FavoriteActionPipeline favorites,
    ProviderPlaylistSnapshotCollector collector, IPlatformClock clock, ITrackIdentityService identities) : IDurableJobHandler
{
    public const string Type = "collection.migrate";
    public string JobType => Type;
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json)!;
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Payload(CollectedPlaylistSourceEntry entry) => JsonSerializer.Serialize(new
    {
        entry.ProviderTrackIdHash,
        entry.Title,
        entry.Artists,
        entry.Album,
        entry.DurationMilliseconds,
        entry.Isrc,
        entry.IsExplicit,
        entry.ArtworkUrl,
        entry.CanonicalRecordingId,
        entry.AlbumId,
        entry.ArtistIds,
        entry.AlbumArtists,
        entry.Year,
        entry.Genre,
        entry.Genres,
        entry.MusicBrainzRecordingId
    });

    public async Task<Guid> StartAsync(ProtocolExecutionContext execution, Guid accountId, string requestKey, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(requestKey) || requestKey.Length > 200) throw new ArgumentException("A request key is required.");
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, execution, token);
        var existing = await db.Set<CollectionMigrationRunRecord>().SingleOrDefaultAsync(item => item.OwnerUserId == user.Id && item.RequestKey == requestKey, token);
        if (existing != null)
        {
            var saved = Read<MigrationRun>(existing.PayloadJson);
            if (saved.ProviderAccountId != accountId) throw new InvalidOperationException("The request key is already in use.");
            return existing.Id;
        }
        var account = await AccountAsync(db, user.Id, accountId, token);
        var source = await SourceAsync(execution, account, token);
        var identity = await source.Implementation.GetSourceUserAsync(source.Context);
        if (!identity.IsSuccess) throw new InvalidOperationException("Restore the Spotify connection before starting.");
        var run = new MigrationRun
        {
            ProviderAccountId = account.Id,
            AccountRevision = account.Revision,
            SourceUserId = identity.RequireValue(),
            Protocol = user.BackendType,
            BackendInstanceId = user.BackendInstanceId,
            BackendPrincipalId = user.BackendPrincipalId,
            CreatedAt = clock.UtcNow
        };
        var record = new CollectionMigrationRunRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = user.Id,
            RequestKey = requestKey,
            PayloadJson = JsonSerializer.Serialize(run)
        };
        db.Add(record);
        await db.SaveChangesAsync(token);
        return record.Id;
    }

    public async Task<object> DiscoverAsync(ProtocolExecutionContext execution, Guid id, string? cursor, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var (record, run) = await LoadAsync(db, execution, id, token);
        if (run.State != "choosing") throw new InvalidOperationException("Selections are already frozen.");
        var account = await AccountAsync(db, record.OwnerUserId, run.ProviderAccountId, token);
        var source = await SourceAsync(execution, account, token);
        await VerifySourceAsync(source, run, token);
        var page = await source.Implementation.GetUserPlaylistsAsync(source.Context, new(new(100, cursor)));
        if (!page.IsSuccess) throw new InvalidOperationException("Spotify playlists are temporarily unavailable.");
        var likes = await source.Implementation.GetPlaylistTracksAsync(source.Context,
            new(new("spotify", ProviderResourceKind.Playlist, "liked-songs"), new(1)));
        if (!likes.IsSuccess) throw new InvalidOperationException("Liked Songs are temporarily unavailable.");
        var discovered = page.RequireValue().Items.Append(likes.RequireValue().Playlist).DistinctBy(item => item.Id.Value).ToArray();
        foreach (var item in discovered)
        {
            var row = await db.Set<CollectionMigrationCollectionRecord>().SingleOrDefaultAsync(value => value.RunId == id && value.SourceId == item.Id.Value, token);
            if (row != null) continue;
            db.Add(new CollectionMigrationCollectionRecord
            {
                Id = Guid.CreateVersion7(),
                RunId = id,
                SourceId = item.Id.Value,
                PayloadJson = JsonSerializer.Serialize(new MigrationCollection
                {
                    Name = item.Name,
                    Expected = item.TrackCount,
                    Kind = item.Id.Value == "liked-songs" ? "liked-songs" : "playlist"
                })
            });
        }
        await db.SaveChangesAsync(token);
        return new
        {
            items = discovered.Select(item => new
            {
                sourceId = item.Id.Value,
                item.Name,
                count = item.TrackCount,
                kind = item.Id.Value == "liked-songs" ? "liked-songs" : "playlist",
                support = "available"
            }),
            page.RequireValue().NextCursor,
            unsupported = new[] { new { kind = "saved-albums", support = "not-supported-yet", count = (int?)null },
                new { kind = "followed-artists", support = "not-supported-yet", count = (int?)null } }
        };
    }

    public async Task ChangeAsync(ProtocolExecutionContext execution, Guid id, string action, IReadOnlyList<MigrationSelection>? selections, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var (record, run) = await LoadAsync(db, execution, id, token);
        if (action == "apply" && run.State == "applying") return;
        if (action == "cancel")
        {
            run.State = "cancelled";
            await SaveAsync(db, record, run, token);
            return;
        }
        if (action == "choose")
        {
            if (run.State != "choosing" || selections is not { Count: > 0 and <= 1000 } ||
                selections.Select(item => item.SourceId).Distinct().Count() != selections.Count) throw new InvalidOperationException("Choose discovered collections once.");
            var rows = await db.Set<CollectionMigrationCollectionRecord>().Where(item => item.RunId == id).ToArrayAsync(token);
            if (selections.Any(item => !rows.Any(row => row.SourceId == item.SourceId) || item.SourceId == "liked-songs" && item.KeepInSync))
                throw new ArgumentException("Select discovered playlists or Liked Songs.");
            foreach (var row in rows)
            {
                var collection = Read<MigrationCollection>(row.PayloadJson);
                var choice = selections.SingleOrDefault(item => item.SourceId == row.SourceId);
                collection.State = choice == null ? "skipped" : "collecting";
                collection.KeepInSync = choice?.KeepInSync == true;
                row.PayloadJson = JsonSerializer.Serialize(collection);
            }
            run.State = "collecting";
            run.Phase = "collecting";
        }
        else if (action == "resume")
        {
            var jobStopped = run.JobId != null && await db.Jobs.AnyAsync(item => item.Id == run.JobId &&
                (item.State == DurableJobState.Failed || item.State == DurableJobState.Cancelled), token);
            if (run.State is not ("waiting" or "cancelled") && !jobStopped) throw new InvalidOperationException("This run is not paused.");
            var source = await SourceAsync(execution, await AccountAsync(db, record.OwnerUserId, run.ProviderAccountId, token), token);
            await VerifySourceAsync(source, run, token, allowRefresh: true);
            run.AccountRevision = source.Context.Account!.Revision;
            run.State = run.Phase;
        }
        else if (action == "apply")
        {
            if (run.State is not ("review" or "completed")) throw new InvalidOperationException("Wait for the collection summary.");
            run.State = "applying";
            run.Phase = "applying";
            run.CompletedAt = null;
            foreach (var row in await db.Set<CollectionMigrationCollectionRecord>().Where(item => item.RunId == id).ToArrayAsync(token))
            {
                var collection = Read<MigrationCollection>(row.PayloadJson);
                if (collection.State is "review" or "applied") { collection.State = "applying"; collection.AppliedPosition = 0; }
                row.PayloadJson = JsonSerializer.Serialize(collection);
            }
        }
        else throw new ArgumentException("Unknown action.");
        if (run.State == "choosing") { await SaveAsync(db, record, run, token); return; }
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var job = await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<Guid>(Type,
            $"{id:N}:{record.Revision}", id, record.OwnerUserId, Priority: -100, MaxDeferrals: 10000,
            CorrelationId: execution.CorrelationId), token);
        run.JobId = job.JobId;
        await SaveAsync(db, record, run, token);
        await transaction.CommitAsync(token);
    }

    public async Task<MigrationStatus> StatusAsync(ProtocolExecutionContext execution, Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var (_, run) = await LoadAsync(db, execution, id, token);
        var result = new List<MigrationCollectionStatus>();
        foreach (var row in await db.Set<CollectionMigrationCollectionRecord>().AsNoTracking().Where(item => item.RunId == id).ToArrayAsync(token))
        {
            var collection = Read<MigrationCollection>(row.PayloadJson);
            int selected = 0, local = 0, streamed = 0, review = 0, unmatched = 0, unsupported = 0, skipped = 0;
            if (collection.SnapshotId != null)
                for (var offset = 0; offset < collection.Read; offset += 100)
                {
                    var page = await playlists.ReadPreviewPageAsync(execution, collection.LinkId!.Value, collection.SnapshotId.Value, offset, 100, token);
                    foreach (var entry in page.Entries)
                    {
                        selected++;
                        if (entry.State == TrackMatchState.Rejected) skipped++;
                        else if (entry.Status is PlaylistPreviewEntryStatus.Included or PlaylistPreviewEntryStatus.Duplicate && entry.ResolvedRoute?.Kind == TrackRouteKind.Local) local++;
                        else if (entry.Status == PlaylistPreviewEntryStatus.Streamed && entry.ResolvedRoute?.Kind == TrackRouteKind.External) streamed++;
                        else if (entry.SourceMetadata?.Title == null) unsupported++;
                        else if (entry.State is TrackMatchState.Suggested or TrackMatchState.Ambiguous or TrackMatchState.Accepted or TrackMatchState.Pinned) review++;
                        else unmatched++;
                    }
                }
            result.Add(new(row.Id, row.SourceId, collection, new(selected, local, streamed, review, unmatched, unsupported, skipped)));
        }
        return new(id, run, result, new(result.Sum(item => item.Counts.Selected), result.Sum(item => item.Counts.Local),
            result.Sum(item => item.Counts.Streamed), result.Sum(item => item.Counts.Review), result.Sum(item => item.Counts.Unmatched),
            result.Sum(item => item.Counts.Unsupported), result.Sum(item => item.Counts.Skipped)), run.State == "choosing" || result.Any(item => item.Collection.State is "collecting" or "matching" or "verifying"));
    }

    public async Task<IReadOnlyList<MigrationReviewItem>> ReviewAsync(ProtocolExecutionContext execution, Guid id,
        Guid collectionId, int offset, int limit, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await LoadAsync(db, execution, id, token);
        var row = await db.Set<CollectionMigrationCollectionRecord>().SingleOrDefaultAsync(item => item.Id == collectionId && item.RunId == id, token)
            ?? throw new KeyNotFoundException();
        var collection = Read<MigrationCollection>(row.PayloadJson);
        var result = new List<MigrationReviewItem>();
        var preview = collection.SnapshotId == null ? null : await playlists.ReadPreviewPageAsync(execution,
            collection.LinkId!.Value, collection.SnapshotId.Value, offset, limit, token);
        foreach (var entry in await Entries(db, collection).OrderBy(item => item.SourcePosition).Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 100)).ToArrayAsync(token))
        {
            var metadata = await db.ExternalMetadataSnapshots.SingleAsync(item => item.Id == entry.ExternalMetadataSnapshotId, token);
            var match = await db.TrackMatches.Where(item => item.ExternalSnapshotId == metadata.Id).OrderByDescending(item => item.DecisionVersion).FirstOrDefaultAsync(token);
            var receipt = await db.PlaylistSyncEntryResults.FirstOrDefaultAsync(item => item.PlaylistSyncRunId == collection.ReceiptRunId && item.PlaylistSourceEntryId == entry.Id, token);
            result.Add(new(row.Id, entry.Id, entry.SourcePosition, metadata.Id, JsonSerializer.Deserialize<JsonElement>(metadata.PayloadJson), match, receipt,
                preview?.Entries.Single(item => item.SourceEntryId == entry.Id)));
        }
        return result;
    }

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var id = context.Claim.Payload.GetGuid();
        var record = await db.Set<CollectionMigrationRunRecord>().SingleOrDefaultAsync(item => item.Id == id && item.OwnerUserId == context.Claim.OwnerUserId, token);
        if (record == null) return DurableJobCompletion.Cancelled();
        var run = Read<MigrationRun>(record.PayloadJson);
        if (run.JobId != context.Claim.JobId || run.State is not ("collecting" or "applying")) return DurableJobCompletion.Cancelled();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == record.OwnerUserId && item.Enabled, token);
        if (user == null || user.BackendType != run.Protocol || user.BackendInstanceId != run.BackendInstanceId || user.BackendPrincipalId != run.BackendPrincipalId)
            return DurableJobCompletion.Failure("migration_listener_changed", "The destination listener changed.");
        var execution = Execution(user, context.Claim.CorrelationId, token);
        var rows = await db.Set<CollectionMigrationCollectionRecord>().Where(item => item.RunId == id).OrderBy(item => item.Id).ToArrayAsync(token);
        var row = rows.FirstOrDefault(item => Read<MigrationCollection>(item.PayloadJson).State is "collecting" or "matching" or "verifying" or "applying");
        if (row == null)
        {
            run.State = run.State == "applying" ? "completed" : "review";
            if (run.State == "completed") run.CompletedAt = clock.UtcNow;
            await SaveAsync(db, record, run, token);
            return DurableJobCompletion.Success();
        }
        var collection = Read<MigrationCollection>(row.PayloadJson);
        try
        {
            if (!await context.ReportProgressAsync(new(Type, "Moving your Spotify collection.", collection.Read, collection.Expected, "spotify", collection.Name), token))
                return DurableJobCompletion.Cancelled();
            if (collection.State is "collecting" or "verifying")
            {
                var source = await SourceAsync(execution, await AccountAsync(db, user.Id, run.ProviderAccountId, token), token);
                await VerifySourceAsync(source, run, token);
                if (collection.State == "collecting") await CollectAsync(db, execution, run, row, collection, source, token);
                else
                {
                    var page = await collector.CollectPageAsync(source.Implementation, source.Context,
                        new(new("spotify", ProviderResourceKind.Playlist, row.SourceId), 100, collection.SourceRevision), collection.VerifyCursor, collection.Verified);
                    var saved = await Entries(db, collection).OrderBy(item => item.SourcePosition).Skip(collection.Verified).Take(page.Snapshot.Entries.Count)
                        .Join(db.ExternalMetadataSnapshots, entry => entry.ExternalMetadataSnapshotId, metadata => metadata.Id,
                            (entry, metadata) => metadata.PayloadSha256).ToArrayAsync(token);
                    if (page.Total != collection.Expected || !saved.SequenceEqual(page.Snapshot.Entries.Select(entry => Hash(Payload(entry)))))
                        throw new InvalidDataException("The source changed during collection.");
                    collection.Verified += page.Snapshot.Entries.Count;
                    collection.VerifyCursor = page.NextCursor;
                    if (page.NextCursor == null) collection.State = "matching";
                }
            }
            else if (collection.State == "matching")
            {
                var batch = await Entries(db, collection).OrderBy(item => item.SourcePosition).Skip(collection.Matched).Take(25).ToArrayAsync(token);
                foreach (var entry in batch)
                {
                    var metadata = await db.ExternalMetadataSnapshots.SingleAsync(item => item.Id == entry.ExternalMetadataSnapshotId, token);
                    if (PlaylistPersistenceService.ReadSourceMetadata(metadata.PayloadJson).Title == null) { collection.Matched++; continue; }
                    if (!await db.TrackMatches.AnyAsync(item => item.ExternalSnapshotId == entry.ExternalMetadataSnapshotId, token))
                    {
                        var result = await matches.RematchSnapshotAsync(execution, entry.ExternalMetadataSnapshotId, execution.CorrelationId, "playlist-policy-v1", token);
                        if (!result.Succeeded && result.Failure == TrackMatchCommandFailure.Conflict)
                            await matches.RecordDecisionAsync(execution, new(metadata.Id, null, null, TrackMatchState.Ambiguous, 0, 1, 1,
                                metadata.SnapshotVersion, 0, TrackMatchDecisionEngine.AlgorithmVersion, "playlist-policy-v1", "[]", "[\"source_identity_conflict\"]", "[]"), token);
                        else if (!result.Succeeded) throw new InvalidOperationException("A track could not be matched. Resume after restoring access.");
                    }
                    collection.Matched++;
                }
                if (collection.Matched == collection.Read) collection.State = "review";
            }
            else await ApplyAsync(db, execution, record, run, row, collection, token);
            collection.Error = null;
            row.PayloadJson = JsonSerializer.Serialize(collection);
            await SaveAsync(db, record, run, token);
            return DurableJobCompletion.Defer("migration_batch", "Progress saved.", TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (DbUpdateConcurrencyException) { return DurableJobCompletion.Cancelled(); }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or InvalidDataException or PlaylistSourceUnavailableException or HttpRequestException)
        {
            foreach (var pending in db.ChangeTracker.Entries<PlaylistSourceEntryRecord>().Where(item => item.State == EntityState.Added).ToArray())
                pending.State = EntityState.Detached;
            collection.Error = error is InvalidDataException
                ? "Spotify changed during collection. Start another run; completed work is retained."
                : "Progress saved. Restore the same Spotify connection or destination access, then resume.";
            row.PayloadJson = JsonSerializer.Serialize(collection);
            run.State = "waiting";
            await SaveAsync(db, record, run, token);
            return DurableJobCompletion.Failure("migration_waiting", collection.Error);
        }
    }

    private async Task CollectAsync(AllstarrDbContext db, ProtocolExecutionContext execution, MigrationRun run,
        CollectionMigrationCollectionRecord row, MigrationCollection collection, ProviderRouteCandidate<IProviderPlaylistCapability> source, CancellationToken token)
    {
        if (collection.Pages >= 1000) throw new InvalidDataException();
        var page = await collector.CollectPageAsync(source.Implementation, source.Context,
            new(new("spotify", ProviderResourceKind.Playlist, row.SourceId), 100, collection.SourceRevision), collection.Cursor, collection.Read);
        if (collection.SourceRevision != null && (collection.Expected != page.Total || collection.ETag != page.Snapshot.SourceETag || collection.Name != page.Snapshot.Name)) throw new InvalidDataException();
        collection.SourceRevision = page.Snapshot.SourceRevision;
        collection.ETag = page.Snapshot.SourceETag;
        collection.Expected = page.Total;
        if (collection.LinkId == null)
        {
            collection.Name = page.Snapshot.Name;
            var link = await playlists.CreateLinkAsync(execution, new(run.ProviderAccountId, "spotify", row.SourceId, Hash(row.SourceId),
                run.Protocol, run.BackendInstanceId, PlaylistLinkMode.Hybrid, PlaylistMaterializationMode.Reconcile,
                "playlist-rules-v1", "playlist-policy-v1", MirrorStaleEntries: true, ImportMode: collection.KeepInSync ? PlaylistImportMode.Linked : PlaylistImportMode.OneTime,
                SyncArtwork: false), token);
            collection.LinkId = link.Id;
            var version = (await db.PlaylistSourceSnapshots.Where(item => item.PlaylistLinkId == link.Id).MaxAsync(item => (int?)item.SnapshotVersion, token) ?? 0) + 1;
            var snapshot = await playlists.CaptureSourceSnapshotAsync(execution, link.Id, new(version, page.Snapshot.SourceRevision,
                page.Snapshot.SourceETag, collection.Name, page.Snapshot.Description, page.Snapshot.ArtworkReferenceKey,
                Hash($"{row.Id:N}:{page.Snapshot.SourceRevision}"), []), token);
            collection.SnapshotId = snapshot.Id;
        }
        foreach (var entry in page.Snapshot.Entries)
        {
            var trackHash = Hash(entry.TrackId!.Value);
            await identities.RememberSourceAsync(source.Context, entry.TrackId, $"ext-spotify-song-{entry.TrackId.Value}", ProviderIdentityScope.Account, entry.Isrc, token);
            var identity = await db.ProviderTrackIdentities.AsNoTracking().FirstAsync(item => item.ProviderId == "spotify" && item.ExternalIdHash == trackHash && item.ProviderAccountId == run.ProviderAccountId, token);
            var payload = Payload(entry);
            var prior = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == execution.RequireActor().EffectiveUserId &&
                item.ProviderAccountId == run.ProviderAccountId && item.ProviderId == "spotify" && item.ExternalIdHash == trackHash && item.ResourceKind == "track")
                .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(token);
            var external = prior?.PayloadSha256 == Hash(payload) && prior.Protocol == run.Protocol &&
                prior.BackendInstanceId == run.BackendInstanceId && prior.BackendPrincipalId == run.BackendPrincipalId ? prior : await matches.CaptureSnapshotAsync(execution,
                new(run.ProviderAccountId, "spotify", "track", trackHash, (prior?.SnapshotVersion ?? 0) + 1,
                    page.Snapshot.SourceRevision, payload, Hash(payload), identity.Id), token);
            db.PlaylistSourceEntries.Add(new()
            {
                Id = Guid.CreateVersion7(),
                PlaylistSourceSnapshotId = collection.SnapshotId!.Value,
                ExternalMetadataSnapshotId = external.Id,
                SourcePosition = entry.SourcePosition,
                SourceEntryIdHash = entry.SourceEntryIdHash
            });
        }
        collection.Read += page.Snapshot.Entries.Count;
        collection.Cursor = page.NextCursor;
        collection.Pages++;
        if (page.NextCursor == null) collection.State = "verifying";
    }

    private async Task ApplyAsync(AllstarrDbContext db, ProtocolExecutionContext execution, CollectionMigrationRunRecord record,
        MigrationRun run, CollectionMigrationCollectionRecord row, MigrationCollection collection, CancellationToken token)
    {
        if (collection.Kind == "playlist")
        {
            var link = await playlists.GetLinkAsync(execution, collection.LinkId!.Value, token);
            if (link.TargetPlaylistId == null)
            {
                var target = targets.Resolve(run.Protocol);
                var targetContext = new BackendPlaylistTargetContext(run.BackendInstanceId, run.BackendPrincipalId, link.TargetCredentialReferenceId?.ToString());
                var candidates = new List<BackendPlaylistSummary>();
                for (var offset = 0; ; offset += 100)
                {
                    var page = await target.ListPageAsync(targetContext, collection.Name, offset, 100, token);
                    if (!page.IsSuccess || offset >= 1000) throw new InvalidOperationException("The destination could not be reconciled.");
                    candidates.AddRange(page.Value!.Where(item => item.Name == collection.Name && item.Writable));
                    if (page.Value!.Count < 100) break;
                }
                if (collection.CreateStartedAt == null || !await db.PlaylistSyncRuns.AnyAsync(item => item.PlaylistLinkId == link.Id &&
                    item.PlaylistSourceSnapshotId == collection.SnapshotId, token))
                {
                    collection.DestinationIdsBefore = candidates.Select(item => item.BackendPlaylistId).ToArray();
                    collection.CreateStartedAt = clock.UtcNow;
                }
                else
                {
                    var destinationPreview = await playlists.ReadPreviewAsync(execution, link.Id, collection.SnapshotId!.Value, token);
                    var expected = destinationPreview.Entries.Where(item => item.TargetEligible).Select(item => item.ResolvedRoute?.BackendItemId)
                        .OfType<string>().Distinct().Order().ToArray();
                    var recovered = new List<string>();
                    foreach (var candidate in candidates.Where(item => !collection.DestinationIdsBefore!.Contains(item.BackendPlaylistId) &&
                        item.CreatedAt >= collection.CreateStartedAt.Value.AddSeconds(-1)))
                    {
                        var read = await target.ReadAsync(targetContext, candidate.BackendPlaylistId, token);
                        if (!read.IsSuccess) throw new InvalidOperationException("The destination could not be read.");
                        if (read.Value!.Members.Select(item => item.BackendItemId).Order().SequenceEqual(expected)) recovered.Add(candidate.BackendPlaylistId);
                    }
                    if (recovered.Count != 1) throw new InvalidOperationException("The destination creation needs reconciliation before another attempt.");
                    await db.PlaylistLinks.Where(item => item.Id == link.Id).ExecuteUpdateAsync(update => update.SetProperty(item => item.TargetPlaylistId, recovered[0]), token);
                }
                row.PayloadJson = JsonSerializer.Serialize(collection);
            }
            await SaveAsync(db, record, run, token);
            var sourceIds = Entries(db, collection).Select(entry => entry.ExternalMetadataSnapshotId);
            var generation = 1L + (await db.TrackMatches.Where(item => sourceIds.Contains(item.ExternalSnapshotId))
                .SumAsync(item => (long?)item.DecisionVersion, token) ?? 0);
            var sourceHashes = db.ExternalMetadataSnapshots.Where(item => sourceIds.Contains(item.Id)).Select(item => item.ExternalIdHash);
            generation += await db.ManualTrackOverrides.Where(item => (item.OwnerUserId == record.OwnerUserId || item.OwnerUserId == null) &&
                item.SourceProviderId == "spotify" && sourceHashes.Contains(item.SourceExternalIdHash))
                .SumAsync(item => (long?)(item.Revision + 1), token) ?? 0;
            var result = await orchestration.RunAsync(execution, new(collection.LinkId.Value, generation, collection.SnapshotId, run.JobId), token);
            if (result.State is PlaylistSyncState.Failed or PlaylistSyncState.Conflicted) throw new InvalidOperationException("The playlist needs destination access.");
            collection.ReceiptRunId = result.RunId;
            collection.State = "applied";
            if (collection.KeepInSync && link.ScheduleId == null)
            {
                var schedule = new JobScheduleRecord
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = record.OwnerUserId,
                    JobType = DurableScheduleEngine.PlaylistSyncJobType,
                    CronExpression = "0 3 * * *",
                    TimeZoneId = "UTC",
                    Enabled = true,
                    NextRunAt = DurableScheduleEngine.GetNextOccurrence("0 3 * * *", "UTC", clock.UtcNow),
                    CreatedAt = clock.UtcNow,
                    UpdatedAt = clock.UtcNow
                };
                db.Add(schedule);
                var tracked = await db.PlaylistLinks.SingleAsync(item => item.Id == link.Id, token);
                tracked.ScheduleId = schedule.Id;
                tracked.Revision++;
            }
            return;
        }
        var receipt = collection.ReceiptRunId == null ? new PlaylistSyncRunRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = record.OwnerUserId,
            PlaylistLinkId = collection.LinkId!.Value,
            PlaylistSourceSnapshotId = collection.SnapshotId!.Value,
            Generation = 1,
            IdempotencyKey = row.Id.ToString("N"),
            RuleVersion = "playlist-rules-v1",
            State = PlaylistSyncState.Running,
            StartedAt = clock.UtcNow
        }
            : await db.PlaylistSyncRuns.SingleAsync(item => item.Id == collection.ReceiptRunId, token);
        if (collection.ReceiptRunId == null) { db.Add(receipt); collection.ReceiptRunId = receipt.Id; }
        row.PayloadJson = JsonSerializer.Serialize(collection);
        await SaveAsync(db, record, run, token);
        var preview = await playlists.ReadPreviewPageAsync(execution, collection.LinkId!.Value, collection.SnapshotId!.Value, collection.AppliedPosition, 50, token);
        var ids = preview.Entries.Select(item => item.SourceEntryId).ToArray();
        var done = await db.PlaylistSyncEntryResults.Where(item => item.PlaylistSyncRunId == receipt.Id && ids.Contains(item.PlaylistSourceEntryId)).Select(item => item.PlaylistSourceEntryId).ToArrayAsync(token);
        var batch = preview.Entries.Where(item => !done.Contains(item.SourceEntryId) &&
            item.Status is PlaylistPreviewEntryStatus.Included or PlaylistPreviewEntryStatus.Duplicate or PlaylistPreviewEntryStatus.Streamed &&
            item.ResolvedRoute?.Kind is TrackRouteKind.Local or TrackRouteKind.External).Take(50).ToArray();
        foreach (var entry in batch)
        {
            var route = entry.ResolvedRoute!;
            var itemId = route.Kind == TrackRouteKind.Local ? route.BackendItemId! : $"ext-{route.ProviderId}-song-{route.ExternalId}";
            var imported = await favorites.ImportMigrationLikesAsync(execution, [(itemId, $"migration:{row.Id:N}:{entry.SourceEntryId:N}")], run.CreatedAt, token);
            var sourceRevision = $"migration:{row.Id:N}:{entry.SourceEntryId:N}";
            if (route.Kind == TrackRouteKind.Local && await (from state in db.FavoriteStates
                                                             join intent in db.FavoriteEvents on state.LastEventId equals intent.Id
                                                             where state.OwnerUserId == record.OwnerUserId && state.Protocol == run.Protocol && state.BackendInstanceId == run.BackendInstanceId &&
                                                                 state.ItemId == itemId && state.IsFavorite && intent.SourceRevision == sourceRevision
                                                             select state.Id).AnyAsync(token) &&
                !await targets.Resolve(run.Protocol).FavoriteAsync(new(run.BackendInstanceId, run.BackendPrincipalId), itemId, token))
                throw new InvalidOperationException("The native heart could not be saved.");
            db.PlaylistSyncEntryResults.Add(new()
            {
                Id = Guid.CreateVersion7(),
                PlaylistSyncRunId = receipt.Id,
                PlaylistSourceEntryId = entry.SourceEntryId,
                SourcePosition = entry.Position,
                LibraryTrackId = entry.LibraryTrackId,
                Outcome = imported[0] ? PlaylistEntryOutcome.Added : PlaylistEntryOutcome.Skipped,
                OutcomeCode = imported[0] ? "membership-recorded" : "existing-or-later-intent",
                DetailsJson = JsonSerializer.Serialize(new { itemId, sourceRevision })
            });
        }
        collection.AppliedPosition += preview.Entries.Count;
        if (collection.AppliedPosition == collection.Read) { collection.State = "applied"; receipt.State = PlaylistSyncState.Succeeded; receipt.CompletedAt = clock.UtcNow; }
    }

    private static IQueryable<PlaylistSourceEntryRecord> Entries(AllstarrDbContext db, MigrationCollection collection) =>
        db.PlaylistSourceEntries.Where(item => item.PlaylistSourceSnapshotId == collection.SnapshotId);
    private async Task<(CollectionMigrationRunRecord Record, MigrationRun Run)> LoadAsync(AllstarrDbContext db, ProtocolExecutionContext execution, Guid id, CancellationToken token)
    {
        var user = await UserAsync(db, execution, token);
        var record = await db.Set<CollectionMigrationRunRecord>().SingleOrDefaultAsync(item => item.Id == id, token) ?? throw new KeyNotFoundException();
        var run = Read<MigrationRun>(record.PayloadJson);
        if (record.OwnerUserId != user.Id || run.Protocol != user.BackendType || run.BackendInstanceId != user.BackendInstanceId || run.BackendPrincipalId != user.BackendPrincipalId)
            throw new UnauthorizedAccessException();
        return (record, run);
    }
    private static async Task SaveAsync(AllstarrDbContext db, CollectionMigrationRunRecord record, MigrationRun run, CancellationToken token)
    {
        record.PayloadJson = JsonSerializer.Serialize(run);
        record.Revision++;
        await db.SaveChangesAsync(token);
    }
    private static async Task<UserRecord> UserAsync(AllstarrDbContext db, ProtocolExecutionContext execution, CancellationToken token) =>
        await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == execution.RequireActor().EffectiveUserId && item.Enabled &&
            item.BackendType == execution.Protocol.ToString().ToLowerInvariant() && item.BackendInstanceId == execution.BackendInstanceId &&
            item.BackendPrincipalId == execution.VerifiedBackendPrincipalId, token) ?? throw new UnauthorizedAccessException();
    private static async Task<ProviderAccountRecord> AccountAsync(AllstarrDbContext db, Guid owner, Guid id, CancellationToken token) =>
        await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.Enabled && item.OwnerUserId == owner && item.ProviderId == "spotify", token)
            ?? throw new UnauthorizedAccessException();
    private async Task<ProviderRouteCandidate<IProviderPlaylistCapability>> SourceAsync(ProtocolExecutionContext execution, ProviderAccountRecord account, CancellationToken token)
    {
        var policy = new ProviderExecutionPolicy(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false), ProviderExplicitContentPolicy.Allow, false, false, false, ["spotify"]);
        return (await router.PlanAsync<IProviderPlaylistCapability>(new(ProviderCapabilityKind.Playlist, execution.RequireActor(), policy,
            Type, execution.CorrelationId, execution.Deadline, ["spotify"], [new("spotify", requestedAccountId: account.Id, expectedAccountRevision: account.Revision)], cancellationToken: token)))
            .Candidates.FirstOrDefault() ?? throw new InvalidOperationException("Restore the Spotify connection.");
    }
    private static async Task VerifySourceAsync(ProviderRouteCandidate<IProviderPlaylistCapability> source, MigrationRun run, CancellationToken token, bool allowRefresh = false)
    {
        token.ThrowIfCancellationRequested();
        if (!allowRefresh && source.Context.Account!.Revision != run.AccountRevision) throw new InvalidOperationException("Reconnect and resume.");
        var identity = await source.Implementation.GetSourceUserAsync(source.Context);
        if (!identity.IsSuccess) throw new InvalidOperationException("Restore the Spotify connection.");
        if (identity.RequireValue() != run.SourceUserId) throw new UnauthorizedAccessException();
    }
    public ProtocolExecutionContext Execution(UserRecord user, string correlation, CancellationToken token) => new(
        user.BackendType == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic, user.BackendInstanceId, user.BackendPrincipalId,
        new AllstarrPrincipal(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, user.IsAdmin), correlation, clock.UtcNow.AddMinutes(5), token);
}
