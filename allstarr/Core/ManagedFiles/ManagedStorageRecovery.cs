using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed record ManagedStorageRecoveryPayload(Guid Generation);
public sealed record ManagedStorageVerificationStatus(bool Available, bool Pending, bool NeedsReview, Guid? JobId,
    string? JobState, DateTimeOffset? VerifiedAt, int Files);

public sealed class ManagedStorageRecovery(IDbContextFactory<AllstarrDbContext> factory, IServiceScopeFactory scopes,
    DurableJobQueue jobs, StorageOptions options, DurableStorageState storage, IPlatformClock clock)
{
    public const string JobType = "storage.verify-files";
    private sealed record Pending(Guid Id);
    private Pending? pending;

    public async Task InitializeAsync(CancellationToken token = default) => await InitializeCoreAsync(null, false, token);

    public Task<Guid?> RequestVerificationAsync(AdminAuthSession actor, CancellationToken token = default) => InitializeCoreAsync(actor, true, token);

    private async Task<Guid?> InitializeCoreAsync(AdminAuthSession? actor, bool force, CancellationToken token)
    {
        if (storage.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
        {
            if (force) throw new InvalidOperationException("The data folder is not ready. Check its mount and restart Allstarr.");
            return null;
        }
        if (actor != null)
        {
            await using var authorized = await factory.CreateDbContextAsync(token);
            await AdminSettingsAccess.RequireActiveAsync(authorized, actor, token);
        }
        await using var scope = scopes.CreateAsyncScope();
        var marker = scope.ServiceProvider.GetRequiredService<FilePlacementService>().InspectStorageMarker(options.DataDirectory);
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (actor != null) await AdminSettingsAccess.RequireActiveAsync(db, actor, token);
        var saved = await db.Set<ManagedStorageIdentityRecord>().SingleOrDefaultAsync(token);
        var active = saved?.VerificationJobId is { } prior && await db.Jobs.AnyAsync(item => item.Id == prior &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.Running || item.State == DurableJobState.RetryScheduled), token);
        if (saved?.MarkerDeviceId == marker.DeviceId && saved.MarkerFileId == marker.FileId && (!force || active))
        {
            Volatile.Write(ref pending, saved.VerificationJobId is { } existing ? new(existing) : null);
            return saved.VerificationJobId;
        }
        var generation = Guid.CreateVersion7();
        if (saved == null)
        {
            saved = new() { Revision = 1 };
            db.Add(saved);
        }
        else saved.Revision++;
        saved.MarkerDeviceId = marker.DeviceId;
        saved.MarkerFileId = marker.FileId;
        saved.Generation = generation;
        saved.UpdatedAt = clock.UtcNow;
        saved.VerificationJobId = null;
        if (await db.ManagedFiles.AnyAsync(item => item.RemovedAt == null, token))
            saved.VerificationJobId = (await jobs.EnqueueInExistingTransactionAsync(db,
                new DurableJobEnqueueRequest<ManagedStorageRecoveryPayload>(JobType, "storage-copy:" + generation.ToString("N"),
                    new(generation), Priority: 1000, MaxAttempts: 12, CorrelationId: "storage-verification"), token)).JobId;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        Volatile.Write(ref pending, saved.VerificationJobId is { } job ? new(job) : null);
        return saved.VerificationJobId;
    }

    public async Task<ManagedStorageVerificationStatus> GetStatusAsync(AdminAuthSession actor, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await AdminSettingsAccess.RequireActiveAsync(db, actor, token);
        var identity = await db.Set<ManagedStorageIdentityRecord>().AsNoTracking().SingleOrDefaultAsync(token);
        var job = identity?.VerificationJobId is { } id ? await db.Jobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token) : null;
        var waiting = job?.State is DurableJobState.Pending or DurableJobState.Running or DurableJobState.RetryScheduled;
        return new(storage.GetSnapshot().Readiness == DurableStorageReadiness.Ready, waiting,
            job?.State is DurableJobState.Failed or DurableJobState.Cancelled, job?.Id,
            job?.State.ToString(), job?.State == DurableJobState.Succeeded ? job.CompletedAt : null,
            await db.ManagedFiles.CountAsync(item => item.RemovedAt == null, token));
    }

    internal async Task<bool> IsPendingAsync(CancellationToken token)
    {
        if (Volatile.Read(ref pending) is not { } current) return false;
        await using var db = await factory.CreateDbContextAsync(token);
        if (await db.Jobs.AsNoTracking().AnyAsync(item => item.Id == current.Id &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.Running || item.State == DurableJobState.RetryScheduled), token)) return true;
        Interlocked.CompareExchange(ref pending, null, current);
        return false;
    }

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken token)
    {
        var generation = execution.Claim.Payload.Deserialize<ManagedStorageRecoveryPayload>()?.Generation ?? Guid.Empty;
        if (generation == Guid.Empty) return DurableJobCompletion.Failure("storage_verification_invalid", "Restart Allstarr with the complete data folder.");
        await using var scope = scopes.CreateAsyncScope();
        var placement = scope.ServiceProvider.GetRequiredService<FilePlacementService>();
        var marker = placement.InspectStorageMarker(options.DataDirectory);
        var verified = 0;
        var unavailable = 0;
        Guid? after = null;
        int total;
        await using (var db = await factory.CreateDbContextAsync(token))
        {
            if (!await db.Set<ManagedStorageIdentityRecord>().AnyAsync(item => item.Id == 1 && item.Generation == generation &&
                item.VerificationJobId == execution.Claim.JobId, token)) return DurableJobCompletion.Cancelled();
            await RequireCurrentAsync(db, execution.Claim, generation, marker, token);
            total = await db.ManagedFiles.CountAsync(item => item.RemovedAt == null, token);
        }
        while (true)
        {
            ManagedFileOwnershipEntity[] batch;
            await using (var read = await factory.CreateDbContextAsync(token))
            {
                var query = read.ManagedFiles.AsNoTracking().Where(item => item.RemovedAt == null);
                if (after.HasValue) query = query.Where(item => item.Id.CompareTo(after.Value) > 0);
                batch = await query.OrderBy(item => item.Id).Take(100).ToArrayAsync(token);
            }
            if (batch.Length == 0) break;
            foreach (var file in batch)
            {
                token.ThrowIfCancellationRequested();
                try { await VerifyAsync(file, placement, execution.Claim, generation, marker, token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
                { unavailable++; }
                await execution.ReportProgressAsync(new("verifying-files", "Verifying saved files after a data-folder copy.", ++verified, total), token);
            }
            after = batch[^1].Id;
        }
        return unavailable == 0 ? DurableJobCompletion.Success() : DurableJobCompletion.Failure("storage_files_need_review",
            $"{unavailable} saved files are missing, changed or unsafe. Restore their original bytes and paths, then recheck saved files in Settings → Storage. Other verified files remain available.");
    }

    private async Task VerifyAsync(ManagedFileOwnershipEntity before, FilePlacementService placement, DurableJobClaim claim,
        Guid generation, ManagedFileSystemIdentity marker, CancellationToken token)
    {
        var record = EfManagedFileOwnershipStore.Map(before)!;
        try { placement.VerifyOwnedFileIdentity(record, token); return; }
        catch (IOException) { }
        var prepared = await placement.PrepareCopiedFileAsync(record, token);
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await RequireCurrentAsync(db, claim, generation, marker, token);
        if (!await db.ManagedFiles.AnyAsync(item => item.Id == before.Id && item.Revision == before.Revision && item.RemovedAt == null &&
            item.RootId == before.RootId && item.CanonicalPath == before.CanonicalPath && item.TargetRootPath == before.TargetRootPath &&
            item.OwnerUserId == before.OwnerUserId && item.ScopeKey == before.ScopeKey && item.ContentSha256 == before.ContentSha256 && item.Length == before.Length &&
            item.FileSystemDeviceId == before.FileSystemDeviceId && item.FileSystemFileId == before.FileSystemFileId, token))
            throw new DurableJobExecutionChangedException();
        placement.ValidatePreparedCopy(prepared, token);
        var currentMarker = placement.InspectStorageMarker(options.DataDirectory);
        if (currentMarker.DeviceId != marker.DeviceId || currentMarker.FileId != marker.FileId)
            throw new IOException("The data folder changed during verification.");
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            Category = "managed-storage",
            Action = "file.verify-copy",
            Outcome = "succeeded",
            CorrelationId = $"file-identity:{before.Id:N}:{before.Revision}",
            CreatedAt = clock.UtcNow,
            DetailsJson = JsonSerializer.Serialize(new
            {
                fileId = before.Id,
                rootId = before.RootId,
                scopeKey = before.ScopeKey,
                sha256 = before.ContentSha256,
                length = before.Length,
                oldRevision = before.Revision,
                oldDevice = before.FileSystemDeviceId,
                oldFile = before.FileSystemFileId,
                newDevice = prepared.FileSystemDeviceId,
                newFile = prepared.FileSystemFileId,
                newLinks = prepared.FileSystemLinkCount,
                jobId = claim.JobId,
                attempt = claim.AttemptNumber,
                generation
            })
        });
        await db.SaveChangesAsync(token);
        var updated = await db.ManagedFiles.Where(item => item.Id == before.Id && item.Revision == before.Revision)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.FileSystemDeviceId, prepared.FileSystemDeviceId)
                .SetProperty(item => item.FileSystemFileId, prepared.FileSystemFileId)
                .SetProperty(item => item.FileSystemLinkCount, prepared.FileSystemLinkCount)
                .SetProperty(item => item.Revision, before.Revision + 1), token);
        if (updated != 1) throw new DurableJobExecutionChangedException();
        placement.ValidatePreparedCopy(prepared, token);
        await RequireCurrentAsync(db, claim, generation, marker, token);
        await transaction.CommitAsync(token);
    }

    private static async Task RequireCurrentAsync(AllstarrDbContext db, DurableJobClaim claim, Guid generation,
        ManagedFileSystemIdentity marker, CancellationToken token)
    {
        if (!await (from identity in db.Set<ManagedStorageIdentityRecord>().AsNoTracking()
                    join job in db.Jobs.AsNoTracking() on identity.VerificationJobId equals (Guid?)job.Id
                    where identity.Id == 1 && identity.Generation == generation && identity.MarkerDeviceId == marker.DeviceId && identity.MarkerFileId == marker.FileId &&
                        job.Id == claim.JobId && job.Type == JobType && job.State == DurableJobState.Running && job.AttemptCount == claim.AttemptNumber &&
                        job.OwnerUserId == null && job.ProviderAccountId == null && job.ProviderCapability == null && job.CancellationRequestedAt == null
                    select job.Id).AnyAsync(token)) throw new DurableJobExecutionChangedException();
    }
}

public sealed class ManagedStorageRecoveryStartup(ManagedStorageRecovery recovery) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => recovery.InitializeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class ManagedStorageRecoveryJobHandler(ManagedStorageRecovery recovery) : IDurableJobHandler
{
    public string JobType => ManagedStorageRecovery.JobType;
    public Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken) => recovery.ExecuteAsync(context, cancellationToken);
}
