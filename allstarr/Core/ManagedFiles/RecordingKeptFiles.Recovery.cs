using System.Globalization;
using allstarr.Core.Downloads;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed partial class RecordingKeptFiles
{
    public Task<int> RecoverReservationsAsync(CancellationToken cancellationToken = default) =>
        RecoverReservationsAsync(null, cancellationToken);

    public Task<int> RecoverCompletedReservationsAsync(DurableJobClaim claim,
        CancellationToken cancellationToken = default) =>
        claim.JobId == Guid.Empty || !KnownReservationJobType(claim.Type)
            ? Task.FromResult(0) : RecoverReservationsAsync(claim, cancellationToken);

    private async Task<int> RecoverReservationsAsync(DurableJobClaim? completed,
        CancellationToken cancellationToken)
    {
        if (jobs == null) return 0;
        var released = 0;
        var prefix = completed == null ? "pending:kept:" : $"pending:kept:{completed.JobId:N}:";
        Guid? after = null;
        while (true)
        {
            await using var page = await factory.CreateDbContextAsync(cancellationToken);
            var query = page.ManagedFileReferences.AsNoTracking().Where(item =>
                item.ReleasedAt == null && item.ReferenceKey.StartsWith(prefix));
            if (after != null) query = query.Where(item => item.Id.CompareTo(after.Value) > 0);
            var candidates = await query.OrderBy(item => item.Id).Take(200).ToArrayAsync(cancellationToken);
            if (candidates.Length == 0) return released;
            after = candidates[^1].Id;
            foreach (var candidate in candidates)
            {
                var marker = ParseReservation(candidate.ReferenceKey);
                if (marker == null) continue;
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var reference = await db.ManagedFileReferences.AsNoTracking().SingleOrDefaultAsync(item =>
                    item.Id == candidate.Id && item.ReleasedAt == null, cancellationToken);
                if (reference == null || reference.ReferenceKey != candidate.ReferenceKey) continue;
                var sourceJob = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == marker.JobId,
                    cancellationToken);
                if (completed != null && (sourceJob == null || sourceJob.AttemptCount != completed.AttemptNumber ||
                    sourceJob.Type != completed.Type || sourceJob.OwnerUserId != completed.OwnerUserId)) continue;
                var file = await db.ManagedFiles.AsNoTracking().SingleOrDefaultAsync(item =>
                    item.Id == reference.ManagedFileId && item.RemovedAt == null, cancellationToken);
                if (sourceJob == null || marker.Attempt > sourceJob.AttemptCount || file == null ||
                    reference.OwnerUserId != null || reference.ScopeKey != file.ScopeKey ||
                    !await HasReservationProofAsync(db, file, sourceJob, marker.RecordingId, cancellationToken)) continue;
                var store = new EfManagedFileOwnershipStore(db);
                var now = clock.UtcNow;
                await store.StageReleaseAsync(reference.ReferenceKey, now, cancellationToken, file.Id);
                if (!await store.HasStagedActiveReferencesAsync(file.Id, cancellationToken))
                {
                    await jobs.EnqueueInExistingTransactionAsync(db,
                        new DurableJobEnqueueRequest<KeptRecordingCleanupPayload>(KeptCleanupJobType,
                            $"kept-reservation:{reference.Id:N}", new(marker.RecordingId, file.Id, sourceJob.Id),
                            sourceJob.OwnerUserId, AvailableAt: now.AddHours(24), CorrelationId: sourceJob.CorrelationId),
                        cancellationToken);
                }
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                released++;
            }
        }
    }

    private async Task<bool> HasReservationProofAsync(AllstarrDbContext db, ManagedFileOwnershipEntity file,
        DurableJobRecord job, Guid recordingId, CancellationToken cancellationToken)
    {
        if (!TerminalReservationJob(job) || job.OwnerUserId == null || job.OwnerUserId == Guid.Empty ||
            job.ScopeKey != $"user:{job.OwnerUserId:N}" || !file.IsManaged || file.OwnerUserId != null ||
            file.SourceJobId != null || file.RootId != placement.RootId ||
            file.ScopeKey != ManagedFileScopeKey.Create(null, placement.RootId) ||
            !await db.CanonicalRecordings.AnyAsync(item => item.Id == recordingId, cancellationToken)) return false;
        var keys = await db.ManagedFileReferences.AsNoTracking().Where(item => item.ManagedFileId == file.Id &&
            item.OwnerUserId == null && item.ScopeKey == file.ScopeKey && item.ReferenceKey.StartsWith("pending:kept:"))
            .Select(item => item.ReferenceKey).ToArrayAsync(cancellationToken);
        if (!keys.Select(ParseReservation).Any(item => item != null && item.JobId == job.Id &&
                item.RecordingId == recordingId && item.Attempt <= job.AttemptCount)) return false;
        return await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking().AnyAsync(item =>
            item.DurableJobId == job.Id && item.OwnerUserId == job.OwnerUserId &&
            (item.State == ProviderDownloadArtifactState.Verified || item.State == ProviderDownloadArtifactState.Placed) &&
            item.SourceExternalTrackId != null && item.SourceExternalTrackId != "" &&
            (item.ProviderAccountId == null && item.SourceAccountRevision == null ||
             item.ProviderAccountId != null && item.SourceAccountRevision >= 0) &&
            item.ContentSha256 == file.ContentSha256 && item.Length == file.Length, cancellationToken);
    }

    private static bool TerminalReservationJob(DurableJobRecord job) =>
        job.State is DurableJobState.Succeeded or DurableJobState.Failed or DurableJobState.Cancelled &&
        KnownReservationJobType(job.Type);

    private static bool KnownReservationJobType(string type) =>
        type is FavoriteActionPipeline.JobType or PlaylistTrackRetentionJobHandler.JobTypeName or "recording.keep";

    private static ReservationMarker? ParseReservation(string key)
    {
        var parts = key.Split(':');
        if (parts is not ["pending", "kept", var job, var recording, var attempt, var hash] ||
            !Guid.TryParseExact(job, "N", out var jobId) || jobId == Guid.Empty ||
            !Guid.TryParseExact(recording, "N", out var recordingId) || recordingId == Guid.Empty ||
            !int.TryParse(attempt, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) || generation < 1 ||
            hash.Length != 64 || hash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return null;
        return new(jobId, recordingId, generation);
    }

    private sealed record ReservationMarker(Guid JobId, Guid RecordingId, int Attempt);
}
