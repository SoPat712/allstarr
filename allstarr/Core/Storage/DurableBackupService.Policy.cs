using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Settings;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed partial class DurableBackupService
{
    public const string JobType = "storage.backup";
    internal static readonly Guid ScheduleId = Guid.Parse("93e9bb8f-f2af-4c60-bc7c-d622f20f7611");
    internal static readonly string[] PolicyKeys = ["Storage:BackupEnabled", "Storage:BackupTimeUtc", "Storage:BackupDirectory",
        "Storage:BackupDailyRetentionCount", "Storage:BackupWeeklyRetentionCount", "Storage:BackupRetentionCount"];
    private readonly allstarr.Core.Operations.IPlatformClock backupClock = clock ?? new allstarr.Core.Operations.SystemPlatformClock();
    private readonly IManagedFileOperations physical = fileOperations ?? new PhysicalManagedFileOperations();
    private readonly object pinGate = new();
    private readonly Dictionary<Guid, int> readers = [];

    public async Task<BackupPolicy> GetPolicyAsync(CancellationToken token = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(token);
        return await ReadPolicyAsync(db, token);
    }

    private async Task<BackupPolicy> ReadPolicyAsync(AllstarrDbContext db, CancellationToken token)
    {
        var rows = await db.RuntimeSettings.AsNoTracking().Where(item => item.OwnerUserId == null && PolicyKeys.Contains(item.Key))
            .ToDictionaryAsync(item => item.Key, token);
        string Raw(string key, string fallback) => rows.TryGetValue(key, out var row)
            ? JsonSerializer.Deserialize<JsonElement>(row.ValueJson).ToString() : configuration?[key] ?? fallback;
        string[] values = [Raw(PolicyKeys[0], "true"), Raw(PolicyKeys[1], "03:00"), Raw(PolicyKeys[2], ""),
            Raw(PolicyKeys[3], "14"), Raw(PolicyKeys[4], "8"), Raw(PolicyKeys[5], options.BackupRetentionCount.ToString(CultureInfo.InvariantCulture))];
        var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', PolicyKeys.Select((key, index) =>
            key + ":" + (rows.GetValueOrDefault(key)?.Revision ?? 0) + ":" + values[index])))));
        return ValidatePolicy(new(bool.Parse(values[0]), values[1], values[2], int.Parse(values[3], CultureInfo.InvariantCulture),
            int.Parse(values[4], CultureInfo.InvariantCulture), int.Parse(values[5], CultureInfo.InvariantCulture), revision));
    }

    internal static BackupPolicy ValidatePolicy(BackupPolicy policy)
    {
        ValidatePolicyString(PolicyKeys[1], policy.TimeUtc);
        ValidatePolicyString(PolicyKeys[2], policy.Directory);
        if (policy.DailyRetentionCount is < 1 or > 90 || policy.WeeklyRetentionCount is < 0 or > 52 || policy.ManualRetentionCount is < 1 or > 1000)
            throw new ArgumentException("Backup retention values are outside their supported bounds.");
        return policy;
    }
    internal static void ValidatePolicyString(string key, string value)
    {
        if (key == PolicyKeys[1] && (value.Length != 5 || !TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))) throw new ArgumentException("Use a UTC backup time in HH:mm format.");
        if (key == PolicyKeys[2] && (value.Length > 2000 || value.Any(char.IsControl) || value.Contains('\'') ||
            value.Length > 0 && (!Path.IsPathFullyQualified(value) || value.Contains("://", StringComparison.Ordinal))))
            throw new ArgumentException("Use an existing absolute local backup directory, or leave it blank for the default.");
    }
    private string ResolveDirectory(BackupPolicy policy) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(policy.Directory.Length == 0 ? options.BackupDirectory : policy.Directory));

    public async Task<BackupConfigurationResult> ConfigureAsync(BackupConfiguration request, AdminAuthSession actor, CancellationToken token = default)
    {
        ValidatePolicy(new(request.Enabled, request.TimeUtc, request.Directory, request.DailyRetentionCount,
            request.WeeklyRetentionCount, request.ManualRetentionCount, ""));
        if (runtimeSettings == null) throw new InvalidOperationException("Backup settings are unavailable.");
        await _gate.WaitAsync(token);
        try
        {
            if (HasPendingRestore) throw new RestorePendingException();
            await using var db = await contextFactory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            await AdminSettingsAccess.RequireActiveAsync(db, actor, token);
            var current = await ReadPolicyAsync(db, token);
            if (request.ExpectedRevision != current.Revision) throw new BackupConfigurationConflictException();
            using var requestedRoot = request.Directory.Length == 0 ? null : OpenDirectory(Path.GetFullPath(request.Directory), retain: true);
            var saved = await db.RuntimeSettings.Where(item => item.OwnerUserId == null && PolicyKeys.Contains(item.Key))
                .ToDictionaryAsync(item => item.Key, token);
            string[] values = [request.Enabled ? "true" : "false", request.TimeUtc, request.Directory,
                request.DailyRetentionCount.ToString(CultureInfo.InvariantCulture), request.WeeklyRetentionCount.ToString(CultureInfo.InvariantCulture),
                request.ManualRetentionCount.ToString(CultureInfo.InvariantCulture)];
            await runtimeSettings.StageBatchAsync(db, PolicyKeys.Select((key, index) =>
                new RuntimeSettingWrite(key, values[index], saved.GetValueOrDefault(key)?.Revision)).ToArray(), "backup-settings", actor.AllstarrUserId, token);
            await db.SaveChangesAsync(token);
            var updated = await ReadPolicyAsync(db, token);
            var binding = requestedRoot == null ? null : new BackupRootProof(ResolveDirectory(updated), await StorageBindingAsync(db, token),
                requestedRoot.Identity.DeviceId, requestedRoot.Identity.FileId);
            await ReconcileScheduleRowAsync(db, updated, token, binding);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        catch (RuntimeSettingConflictException) { throw new BackupConfigurationConflictException(); }
        finally { _gate.Release(); }
        return new(await GetPolicyAsync(token), await GetStatusAsync(token));
    }

    public async Task ReconcileScheduleAsync(CancellationToken token = default)
    {
        if (storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready) return;
        await using var db = await contextFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var policy = await ReadPolicyAsync(db, token);
        await ReconcileScheduleRowAsync(db, policy, token);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    private async Task ReconcileScheduleRowAsync(AllstarrDbContext db, BackupPolicy policy, CancellationToken token, BackupRootProof? binding = null)
    {
        var row = await db.JobSchedules.SingleOrDefaultAsync(item => item.Id == ScheduleId, token);
        var enabled = policy.Enabled && !HasPendingRestore;
        var priorRoot = row == null ? null : JsonSerializer.Deserialize<BackupScheduleTemplate>(row.PayloadTemplateJson)?.Root;
        if (priorRoot != null && priorRoot.Directory == ResolveDirectory(policy) && priorRoot.StorageBinding == await StorageBindingAsync(db, token))
        {
            if (binding != null && binding != priorRoot) throw new BackupDirectoryException(true);
            binding = priorRoot;
        }
        var template = JsonSerializer.Serialize(new BackupScheduleTemplate(1, policy.Revision, binding));
        var time = TimeOnly.ParseExact(policy.TimeUtc, "HH:mm", CultureInfo.InvariantCulture);
        var cron = $"{time.Minute} {time.Hour} * * *";
        if (row == null)
        {
            row = new() { Id = ScheduleId, JobType = JobType, OwnerUserId = null, CreatedAt = backupClock.UtcNow, Revision = 1 };
            db.JobSchedules.Add(row);
        }
        else if (row.JobType != JobType || row.OwnerUserId != null) throw new InvalidOperationException("The backup schedule is unavailable.");
        else if (row.Enabled == enabled && row.PayloadTemplateJson == template && row.CronExpression == cron)
        { return; }
        else row.Revision++;
        row.Enabled = enabled;
        row.CronExpression = cron;
        row.TimeZoneId = "UTC";
        row.OverlapPolicy = ScheduleOverlapPolicy.Skip;
        row.MisfirePolicy = ScheduleMisfirePolicy.RunOnce;
        row.PayloadTemplateJson = template;
        row.NextRunAt = enabled ? DurableScheduleEngine.GetNextOccurrence(cron, "UTC", backupClock.UtcNow) : null;
        row.UpdatedAt = backupClock.UtcNow;
    }

    internal static Task<Guid?> ActiveJobAsync(AllstarrDbContext db, CancellationToken token) => db.Jobs.AsNoTracking()
        .Where(item => item.Type == JobType && (item.State == DurableJobState.Pending || item.State == DurableJobState.Running || item.State == DurableJobState.RetryScheduled))
        .OrderBy(item => item.CreatedAt).Select(item => (Guid?)item.Id).FirstOrDefaultAsync(token);

    public async Task<BackupQueued> EnqueueAsync(AdminAuthSession actor, CancellationToken token = default)
    {
        if (jobs == null) throw new InvalidOperationException("Backup jobs are unavailable.");
        await _gate.WaitAsync(token);
        try
        {
            if (HasPendingRestore) throw new RestorePendingException();
            if (storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready) throw new InvalidOperationException("Durable storage is unavailable.");
            await using var db = await contextFactory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            await AdminSettingsAccess.RequireActiveAsync(db, actor, token);
            if (await ActiveJobAsync(db, token) is { } running) throw new BackupRunningException(running);
            var result = await ReserveJobAsync(db, "manual", null, "manual:" + Guid.NewGuid().ToString("N"), token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return result;
        }
        finally { _gate.Release(); }
    }

    internal async Task<bool> ReserveScheduledAsync(AllstarrDbContext db, JobScheduleRecord schedule, DateTimeOffset dueAt, CancellationToken token)
    {
        if (schedule.Id != ScheduleId || schedule.OwnerUserId != null || HasPendingRestore) return false;
        var policy = await ReadPolicyAsync(db, token);
        var template = JsonSerializer.Deserialize<BackupScheduleTemplate>(schedule.PayloadTemplateJson);
        if (!policy.Enabled || template is not { Version: 1 } || template.ConfigurationRevision != policy.Revision) return false;
        return (await ReserveJobAsync(db, "scheduled", dueAt, $"schedule:{ScheduleId:N}:{dueAt.UtcTicks}", token)).Created;
    }

    private async Task<BackupQueued> ReserveJobAsync(AllstarrDbContext db, string origin, DateTimeOffset? dueAt, string idempotency, CancellationToken token)
    {
        var existing = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(item => item.Type == JobType && item.IdempotencyKey == idempotency && item.OwnerUserId == null, token);
        if (existing != null)
        {
            if (!await db.BackupArtifacts.AnyAsync(item => item.JobId == existing.Id && item.Origin == origin && item.ScheduledFor == dueAt, token))
                throw new InvalidOperationException("The existing backup request is unavailable.");
            return new(existing.Id, false);
        }
        var policy = await ReadPolicyAsync(db, token);
        var row = await NewArtifactAsync(db, policy, origin, dueAt, token);
        var queued = await jobs!.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<BackupJobPayload>(JobType,
            idempotency, new(row.Id), MaxAttempts: 3, CorrelationId: "database-backup"), token);
        if (!queued.Created) throw new InvalidOperationException("The backup request changed during admission.");
        row.JobId = queued.JobId;
        return new(queued.JobId, queued.Created);
    }

    private async Task<string> StorageBindingAsync(AllstarrDbContext db, CancellationToken token)
    {
        var identity = await db.Set<ManagedStorageIdentityRecord>().AsNoTracking().SingleOrDefaultAsync(token);
        if (identity != null) return identity.Generation.ToString("N");
        if (!physical.TryOpenDirectory(Path.GetFullPath(options.DataDirectory), out var handle, out var actual))
        { handle.Dispose(); throw new BackupDirectoryException(); }
        using (handle) return actual.DeviceId + ":" + actual.FileId;
    }

    private async Task<BackupArtifactRecord> NewArtifactAsync(AllstarrDbContext db, BackupPolicy policy, string origin,
        DateTimeOffset? dueAt, CancellationToken token)
    {
        var id = Guid.NewGuid();
        var now = backupClock.UtcNow;
        var row = new BackupArtifactRecord
        {
            Id = id,
            Origin = origin,
            ScheduledFor = dueAt,
            CreatedAt = now,
            Directory = ResolveDirectory(policy),
            PolicyJson = JsonSerializer.Serialize(policy),
            StorageBinding = await StorageBindingAsync(db, token),
            FileName = $"allstarr-backup-{now.UtcDateTime:yyyyMMdd-HHmmss}-{id:N}.zip"
        };
        var schedule = await db.JobSchedules.AsNoTracking().SingleOrDefaultAsync(item => item.Id == ScheduleId, token);
        var proof = schedule == null ? null : JsonSerializer.Deserialize<BackupScheduleTemplate>(schedule.PayloadTemplateJson)?.Root;
        if (proof?.Directory == row.Directory && proof.StorageBinding == row.StorageBinding)
        { row.DirectoryDeviceId = proof.DeviceId; row.DirectoryFileId = proof.FileId; }
        db.BackupArtifacts.Add(row);
        return row;
    }
}
