using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed record KeptRecordingCleanupPayload(Guid RecordingId, Guid ManagedFileId,
    Guid? ReservationJobId = null);

public sealed partial class RecordingKeptFiles
{
    public const string KeptCleanupJobType = "recording.kept-cleanup";

    public async Task<DurableJobCompletion> CleanupAsync(DurableJobClaim claim,
        CancellationToken cancellationToken = default)
    {
        if (jobs == null || claim.Type != KeptCleanupJobType ||
            !(await jobs.ReauthorizeAsync(claim, cancellationToken)).Authorized)
            return DurableJobCompletion.Cancelled();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
        KeptRecordingCleanupPayload? payload;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var job = await db.Jobs.SingleOrDefaultAsync(item => item.Id == claim.JobId &&
                item.Type == KeptCleanupJobType && item.State == DurableJobState.Running &&
                item.AttemptCount == claim.AttemptNumber && item.CancellationRequestedAt == null &&
                item.OwnerUserId == claim.OwnerUserId && item.ProviderAccountId == null &&
                item.ProviderCapability == null, cancellationToken);
            if (job == null || job.PolicySnapshotJson != claim.PolicySnapshot.GetRawText())
                return DurableJobCompletion.Cancelled();
            try { payload = JsonSerializer.Deserialize<KeptRecordingCleanupPayload>(job.PayloadJson); }
            catch (JsonException) { payload = null; }
            if (payload == null || payload.ManagedFileId == Guid.Empty || payload.RecordingId == Guid.Empty)
                return DurableJobCompletion.Failure("kept_cleanup_payload_invalid", "The saved cleanup target is invalid.");
            var file = await db.ManagedFiles.AsNoTracking().SingleOrDefaultAsync(item =>
                item.Id == payload.ManagedFileId, cancellationToken);
            if (file == null || !file.IsManaged || file.OwnerUserId != null || file.SourceJobId != null ||
                file.RootId != placement.RootId || file.ScopeKey != ManagedFileScopeKey.Create(null, placement.RootId))
                return DurableJobCompletion.Failure("kept_cleanup_scope_changed", "The kept file no longer belongs to this storage scope.");
            if (file.RemovedAt == null)
            {
                var references = await db.ManagedFileReferences.AsNoTracking().Where(item =>
                    item.ManagedFileId == file.Id).ToArrayAsync(cancellationToken);
                if (references.Any(item => item.ReleasedAt == null)) return DurableJobCompletion.Success();
                var lastRelease = references.Max(item => item.ReleasedAt);
                if (lastRelease == null)
                    return DurableJobCompletion.Failure("kept_cleanup_release_missing", "The file has no recorded release time.");
                var due = lastRelease.Value.AddHours(24);
                if (clock.UtcNow < due)
                    return DurableJobCompletion.Defer("kept_cleanup_grace", "The kept file is still in its grace period.", due - clock.UtcNow);
                var unpublished = false;
                if (payload.ReservationJobId is { } sourceJobId)
                {
                    var sourceJob = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == sourceJobId,
                        cancellationToken);
                    if (sourceJob != null && !TerminalReservationJob(sourceJob))
                        return DurableJobCompletion.Defer("kept_reservation_active", "The original work is active again.", TimeSpan.FromHours(1));
                    unpublished = sourceJob != null && await HasReservationProofAsync(db, file, sourceJob,
                        payload.RecordingId, cancellationToken);
                    if (!unpublished)
                        return DurableJobCompletion.Failure("kept_reservation_proof_missing",
                            "The abandoned placement no longer has its original verification record.");
                }
                if (!await new EfManagedFileOwnershipStore(db).StageKeptRemovalAsync(file.Id,
                        payload.RecordingId, clock.UtcNow, cancellationToken, unpublished))
                    return DurableJobCompletion.Success();
            }
            db.Entry(job).Property(item => item.Revision).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        var removed = await scope.ServiceProvider.GetRequiredService<ManagedFileRemovalService>()
            .ResumeUnpublishedRemovalAsync(payload.ManagedFileId,
                ManagedFileScopeKey.Create(null, placement.RootId), cancellationToken);
        if (removed && payload.ReservationJobId is { } reservationJobId && claim.OwnerUserId is { } owner)
            await artifacts.DiscardSupersededWorkspacesAsync(owner, reservationJobId, null, cancellationToken);
        return removed ? DurableJobCompletion.Success() : DurableJobCompletion.Failure(
            "kept_cleanup_file_changed", "The kept file changed. Check storage before removing it.");
    }

    internal async Task StageFavoriteInterestAsync(AllstarrDbContext db, FavoriteStateRecord state,
        FavoriteEventRecord favoriteEvent, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Kept interests require the favorite acceptance transaction.");
        var store = new EfManagedFileOwnershipStore(db);
        var key = HeartKey(state.Id);
        if (state.IsFavorite)
        {
            var actor = new ProviderActorContext(ProviderActorKind.User, state.OwnerUserId,
                new(favoriteEvent.Protocol, favoriteEvent.BackendInstanceId, favoriteEvent.BackendPrincipalId));
            var recordingId = await ResolveKeptRecordingIdAsync(actor, state.ItemId, cancellationToken);
            if (recordingId == null) return;
            var candidate = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                                   join recording in db.CanonicalRecordings.AsNoTracking()
                                       on route.CanonicalRecordingId equals recording.Id
                                   join file in db.ManagedFiles.AsNoTracking() on route.ManagedFileId equals file.Id
                                   where (recording.Id == recordingId || recording.MergedIntoId == recordingId) &&
                                         file.RemovedAt == null
                                   orderby route.CanonicalRecordingId == recordingId descending, route.CreatedAt, route.ManagedFileId
                                   select file.Id).FirstOrDefaultAsync(cancellationToken);
            if (candidate == Guid.Empty) return;
            await store.StageReferenceAsync(candidate, key, favoriteEvent.UpdatedAt, cancellationToken);
            await CancelCleanupAsync(db, candidate, favoriteEvent.UpdatedAt, cancellationToken);
            return;
        }

        await StageReleaseInterestAsync(db, key, state.OwnerUserId, favoriteEvent.Id, favoriteEvent.UpdatedAt,
            favoriteEvent.CorrelationId, cancellationToken);
    }

    private async Task StageReleaseInterestAsync(AllstarrDbContext db, string key, Guid ownerUserId,
        Guid operationId, DateTimeOffset releasedAt, string correlationId, CancellationToken cancellationToken,
        Guid? onlyFileId = null)
    {
        var store = new EfManagedFileOwnershipStore(db);
        var released = await store.StageReleaseAsync(key, releasedAt, cancellationToken, onlyFileId);
        foreach (var fileId in released)
        {
            if (await store.HasStagedActiveReferencesAsync(fileId, cancellationToken)) continue;
            var route = await db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                .Where(item => item.ManagedFileId == fileId).OrderBy(item => item.CanonicalRecordingId)
                .FirstOrDefaultAsync(cancellationToken);
            if (route == null) continue;
            if (jobs == null) throw new InvalidOperationException("The kept cleanup queue is unavailable.");
            await jobs.EnqueueInExistingTransactionAsync(db,
                new DurableJobEnqueueRequest<KeptRecordingCleanupPayload>(KeptCleanupJobType,
                    $"kept:{fileId:N}:{operationId:N}:{releasedAt.UtcTicks}", new(route.CanonicalRecordingId, fileId),
                    ownerUserId, AvailableAt: releasedAt.AddHours(24),
                    CorrelationId: correlationId), cancellationToken);
        }
    }

    private static Task CancelCleanupAsync(AllstarrDbContext db, Guid fileId, DateTimeOffset now,
        CancellationToken cancellationToken) => CancelCleanupsAsync(db, [fileId], now, cancellationToken);

    private static async Task CancelCleanupsAsync(AllstarrDbContext db, IReadOnlyCollection<Guid> fileIds, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0) return;
        // Narrow in SQL to payloads naming these files, then confirm each typed payload.
        var fileText = fileIds.Select(item => item.ToString("D")).ToArray();
        var targets = fileIds.ToHashSet();
        var pending = await db.Jobs.Where(item => item.Type == KeptCleanupJobType &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.RetryScheduled ||
             item.State == DurableJobState.Running) &&
            fileText.Any(id => item.PayloadJson.Contains(id))).ToArrayAsync(cancellationToken);
        foreach (var job in pending)
        {
            KeptRecordingCleanupPayload? payload;
            try { payload = JsonSerializer.Deserialize<KeptRecordingCleanupPayload>(job.PayloadJson); }
            catch (JsonException) { continue; }
            if (payload == null || !targets.Contains(payload.ManagedFileId)) continue;
            job.CancellationRequestedAt = now;
            if (job.State != DurableJobState.Running)
            {
                job.State = DurableJobState.Cancelled;
                job.CompletedAt = now;
            }
            job.UpdatedAt = now;
            job.Revision++;
        }
    }
}

public sealed class KeptRecordingCleanupJobHandler(RecordingKeptFiles kept) : IDurableJobHandler, IDurableJobRecoveryHandler
{
    public string JobType => RecordingKeptFiles.KeptCleanupJobType;
    public Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context,
        CancellationToken cancellationToken) => kept.CleanupAsync(context.Claim, cancellationToken);
    public async Task RecoverAsync(CancellationToken cancellationToken) =>
        await kept.RecoverReservationsAsync(cancellationToken);
    public async Task RecoverCompletedAsync(DurableJobClaim claim, CancellationToken cancellationToken) =>
        await kept.RecoverCompletedReservationsAsync(claim, cancellationToken);
}
