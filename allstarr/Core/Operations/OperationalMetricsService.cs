using System.Globalization;
using System.Text;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Operations;

public sealed class OperationalMetricsService
{
    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly DurableStorageState _storageState;
    private readonly DurableStorageOptions? _storageOptions;
    private readonly ReadinessOptions? _readinessOptions;
    private readonly OperationalRuntimeState? _runtimeState;
    private readonly PlatformTraceCollector? _traces;
    private readonly SidecarStatusCatalog? _sidecars;

    public OperationalMetricsService(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableStorageState storageState,
        DurableStorageOptions? storageOptions = null,
        ReadinessOptions? readinessOptions = null,
        OperationalRuntimeState? runtimeState = null,
        PlatformTraceCollector? traces = null,
        SidecarStatusCatalog? sidecars = null)
    {
        _contextFactory = contextFactory;
        _storageState = storageState;
        _storageOptions = storageOptions;
        _readinessOptions = readinessOptions;
        _runtimeState = runtimeState;
        _traces = traces;
        _sidecars = sidecars;
    }

    public async Task<string> RenderPrometheusAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _storageState.GetSnapshot();
        var output = new StringBuilder();
        output.AppendLine("# HELP allstarr_storage_ready Whether the selected durable storage is ready.");
        output.AppendLine("# TYPE allstarr_storage_ready gauge");
        output.Append("allstarr_storage_ready{provider=\"")
            .Append(Label(snapshot.Provider.ToString()))
            .Append("\"} ")
            .AppendLine(snapshot.Readiness == DurableStorageReadiness.Ready ? "1" : "0");
        if (!string.IsNullOrWhiteSpace(snapshot.SchemaVersion))
        {
            output.AppendLine("# HELP allstarr_storage_schema_info Selected durable schema version.");
            output.AppendLine("# TYPE allstarr_storage_schema_info gauge");
            output.Append("allstarr_storage_schema_info{version=\"")
                .Append(Label(snapshot.SchemaVersion))
                .AppendLine("\"} 1");
        }

        AppendRuntimeMetrics(output);
        AppendStorageFreeMetric(output);
        AppendSidecarMetrics(output);
        AppendTraceMetrics(output);
        if (snapshot.Readiness != DurableStorageReadiness.Ready)
        {
            return output.ToString();
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var jobs = await context.Jobs.AsNoTracking()
            .GroupBy(item => item.State)
            .Select(group => new { State = group.Key, Count = group.LongCount() })
            .ToListAsync(cancellationToken);
        output.AppendLine("# HELP allstarr_jobs Durable jobs by state.");
        output.AppendLine("# TYPE allstarr_jobs gauge");
        foreach (var item in jobs)
        {
            output.Append("allstarr_jobs{state=\"")
                .Append(Label(item.State.ToString()))
                .Append("\"} ")
                .AppendLine(item.Count.ToString(CultureInfo.InvariantCulture));
        }

        var attempts = await context.JobAttempts.AsNoTracking()
            .Where(item => item.Outcome != null)
            .GroupBy(item => item.Outcome!)
            .Select(group => new { Outcome = group.Key, Count = group.LongCount() })
            .ToListAsync(cancellationToken);
        output.AppendLine("# HELP allstarr_job_attempts_total Durable job attempts by final outcome.");
        output.AppendLine("# TYPE allstarr_job_attempts_total gauge");
        foreach (var item in attempts)
        {
            output.Append("allstarr_job_attempts_total{outcome=\"")
                .Append(Label(item.Outcome))
                .Append("\"} ")
                .AppendLine(item.Count.ToString(CultureInfo.InvariantCulture));
        }

        var cancellationRequests = await context.Jobs.AsNoTracking()
            .LongCountAsync(item => item.CancellationRequestedAt != null, cancellationToken);
        var expiredLeases = await context.JobAttempts.AsNoTracking()
            .LongCountAsync(item => item.Outcome == "lease_expired", cancellationToken);
        output.AppendLine("# HELP allstarr_job_cancellation_requests_total Durable jobs with a cancellation request.");
        output.AppendLine("# TYPE allstarr_job_cancellation_requests_total gauge");
        output.Append("allstarr_job_cancellation_requests_total ")
            .AppendLine(cancellationRequests.ToString(CultureInfo.InvariantCulture));
        output.AppendLine("# HELP allstarr_job_lease_expirations_total Durable worker lease expirations.");
        output.AppendLine("# TYPE allstarr_job_lease_expirations_total gauge");
        output.Append("allstarr_job_lease_expirations_total ")
            .AppendLine(expiredLeases.ToString(CultureInfo.InvariantCulture));

        var outbox = await context.OutboxMessages.AsNoTracking()
            .GroupBy(item => item.State)
            .Select(group => new { State = group.Key, Count = group.LongCount() })
            .ToListAsync(cancellationToken);
        output.AppendLine("# HELP allstarr_outbox_messages Durable outbox messages by state.");
        output.AppendLine("# TYPE allstarr_outbox_messages gauge");
        foreach (var item in outbox)
        {
            output.Append("allstarr_outbox_messages{state=\"")
                .Append(Label(item.State.ToString()))
                .Append("\"} ")
                .AppendLine(item.Count.ToString(CultureInfo.InvariantCulture));
        }

        var outboxRetryCount = await context.OutboxMessages.AsNoTracking()
            .Select(item => item.AttemptCount > 1 ? (long)item.AttemptCount - 1 : 0)
            .SumAsync(cancellationToken);
        output.AppendLine("# HELP allstarr_outbox_retries_total Durable outbox retry attempts.");
        output.AppendLine("# TYPE allstarr_outbox_retries_total gauge");
        output.Append("allstarr_outbox_retries_total ")
            .AppendLine(outboxRetryCount.ToString(CultureInfo.InvariantCulture));

        var circuits = await context.ProviderCircuits.AsNoTracking()
            .GroupBy(item => new { item.Capability, item.State })
            .Select(group => new
            {
                group.Key.Capability,
                group.Key.State,
                Count = group.LongCount()
            })
            .ToListAsync(cancellationToken);
        output.AppendLine("# HELP allstarr_provider_circuits Provider-account circuits by capability and state.");
        output.AppendLine("# TYPE allstarr_provider_circuits gauge");
        foreach (var item in circuits)
        {
            output.Append("allstarr_provider_circuits{capability=\"")
                .Append(Label(item.Capability))
                .Append("\",state=\"")
                .Append(Label(item.State.ToString()))
                .Append("\"} ")
                .AppendLine(item.Count.ToString(CultureInfo.InvariantCulture));
        }

        var rollups = await context.ProviderHealthRollups.AsNoTracking()
            .OrderByDescending(item => item.WindowStart)
            .Take(5000)
            .ToListAsync(cancellationToken);
        var accountProviders = await context.ProviderAccounts.AsNoTracking()
            .Where(item => rollups.Select(rollup => rollup.ProviderAccountId).Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, item => item.ProviderId, cancellationToken);
        var latestRollups = rollups
            .GroupBy(item => new { item.ProviderAccountId, item.Capability })
            .Select(group => group.OrderByDescending(item => item.WindowStart).First())
            .Where(item => accountProviders.ContainsKey(item.ProviderAccountId))
            .GroupBy(item => new
            {
                ProviderId = accountProviders[item.ProviderAccountId],
                item.Capability
            })
            .ToList();
        output.AppendLine("# HELP allstarr_provider_success_rate Latest provider health success rate aggregated across accounts.");
        output.AppendLine("# TYPE allstarr_provider_success_rate gauge");
        output.AppendLine("# HELP allstarr_provider_latency_p50_milliseconds Latest provider p50 latency aggregated across accounts.");
        output.AppendLine("# TYPE allstarr_provider_latency_p50_milliseconds gauge");
        output.AppendLine("# HELP allstarr_provider_latency_p95_milliseconds Latest provider p95 latency aggregated across accounts.");
        output.AppendLine("# TYPE allstarr_provider_latency_p95_milliseconds gauge");
        foreach (var group in latestRollups)
        {
            var labels = $"provider=\"{Label(group.Key.ProviderId)}\",capability=\"{Label(group.Key.Capability)}\"";
            output.Append("allstarr_provider_success_rate{").Append(labels).Append("} ")
                .AppendLine(group.Average(item => item.SuccessRate).ToString("0.######", CultureInfo.InvariantCulture));
            var p50 = group.Where(item => item.P50LatencyMilliseconds.HasValue)
                .Select(item => item.P50LatencyMilliseconds!.Value)
                .DefaultIfEmpty(0)
                .Average();
            var p95 = group.Where(item => item.P95LatencyMilliseconds.HasValue)
                .Select(item => item.P95LatencyMilliseconds!.Value)
                .DefaultIfEmpty(0)
                .Max();
            output.Append("allstarr_provider_latency_p50_milliseconds{").Append(labels).Append("} ")
                .AppendLine(p50.ToString("0.###", CultureInfo.InvariantCulture));
            output.Append("allstarr_provider_latency_p95_milliseconds{").Append(labels).Append("} ")
                .AppendLine(p95.ToString(CultureInfo.InvariantCulture));
        }

        var oldestPending = await context.Jobs.AsNoTracking()
            .Where(item => item.State == DurableJobState.Pending || item.State == DurableJobState.RetryScheduled)
            .OrderBy(item => item.CreatedAt)
            .Select(item => (DateTimeOffset?)item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        output.AppendLine("# HELP allstarr_jobs_oldest_pending_age_seconds Age of the oldest pending durable job.");
        output.AppendLine("# TYPE allstarr_jobs_oldest_pending_age_seconds gauge");
        var pendingAge = oldestPending.HasValue
            ? Math.Max(0, (DateTimeOffset.UtcNow - oldestPending.Value).TotalSeconds)
            : 0;
        output.Append("allstarr_jobs_oldest_pending_age_seconds ")
            .AppendLine(pendingAge.ToString("0.###", CultureInfo.InvariantCulture));

        var latestBackup = await context.Backups.AsNoTracking()
            .Where(item => item.Status == "verified")
            .OrderByDescending(item => item.VerifiedAt)
            .Select(item => item.VerifiedAt)
            .FirstOrDefaultAsync(cancellationToken);
        output.AppendLine("# HELP allstarr_backup_age_seconds Age of the latest verified backup, or -1 when none exists.");
        output.AppendLine("# TYPE allstarr_backup_age_seconds gauge");
        var backupAge = latestBackup.HasValue
            ? Math.Max(0, (DateTimeOffset.UtcNow - latestBackup.Value).TotalSeconds)
            : -1;
        output.Append("allstarr_backup_age_seconds ")
            .AppendLine(backupAge.ToString("0.###", CultureInfo.InvariantCulture));
        var verifiedBackups = await context.Backups.AsNoTracking()
            .LongCountAsync(item => item.Status == "verified", cancellationToken);
        var latestRestore = await context.Backups.AsNoTracking()
            .Where(item => item.RestoreStatus != null)
            .OrderByDescending(item => item.RestoreVerifiedAt)
            .Select(item => new { item.RestoreStatus, item.RestoreVerifiedAt })
            .FirstOrDefaultAsync(cancellationToken);
        output.AppendLine("# HELP allstarr_backups_verified_total Successfully verified backup artifacts.");
        output.AppendLine("# TYPE allstarr_backups_verified_total gauge");
        output.Append("allstarr_backups_verified_total ")
            .AppendLine(verifiedBackups.ToString(CultureInfo.InvariantCulture));
        output.AppendLine("# HELP allstarr_restore_test_status Latest restore verification: 1 passed, 0 failed, -1 never tested.");
        output.AppendLine("# TYPE allstarr_restore_test_status gauge");
        var restoreStatus = latestRestore == null
            ? -1
            : latestRestore.RestoreStatus == "verified" ? 1 : 0;
        output.Append("allstarr_restore_test_status ")
            .AppendLine(restoreStatus.ToString(CultureInfo.InvariantCulture));
        return output.ToString();
    }

    private void AppendRuntimeMetrics(StringBuilder output)
    {
        var runtime = _runtimeState?.GetSnapshot();
        if (runtime == null)
        {
            return;
        }

        Gauge(output, "allstarr_migrations_total", "Schema migration attempts.", runtime.MigrationAttempts);
        Gauge(output, "allstarr_migration_failures_total", "Schema migration failures.", runtime.MigrationFailures);
        Gauge(output, "allstarr_migration_last_duration_milliseconds", "Duration of the latest schema migration attempt.", runtime.LastMigrationDurationMilliseconds);
        Gauge(output, "allstarr_valkey_configured", "Whether Valkey acceleration is configured.", runtime.ValkeyConfigured ? 1 : 0);
        Gauge(output, "allstarr_valkey_available", "Whether configured Valkey acceleration is connected.", runtime.ValkeyAvailable ? 1 : 0);
        Gauge(output, "allstarr_valkey_degradation_events_total", "Observed Valkey availability losses.", runtime.ValkeyDegradationEvents);
        Gauge(output, "allstarr_valkey_recovery_events_total", "Observed Valkey recoveries.", runtime.ValkeyRecoveryEvents);
        Gauge(output, "allstarr_sidecar_degradation_events_total", "Observed sidecar transitions away from ready.", runtime.SidecarDegradationEvents);
        Gauge(output, "allstarr_sidecar_recovery_events_total", "Observed sidecar transitions to ready.", runtime.SidecarRecoveryEvents);
    }

    private void AppendStorageFreeMetric(StringBuilder output)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(_storageOptions?.BackupDirectory))
        {
            paths.Add(_storageOptions.BackupDirectory);
        }

        paths.AddRange(_readinessOptions?.RequiredDirectories ?? []);
        var free = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(TryGetFreeSpace)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(-1)
            .Min();
        Gauge(output, "allstarr_storage_minimum_free_bytes", "Minimum available bytes across configured Allstarr storage roots, or -1 when unknown.", free);
    }

    private void AppendSidecarMetrics(StringBuilder output)
    {
        if (_sidecars == null)
        {
            return;
        }

        output.AppendLine("# HELP allstarr_sidecars Configured sidecars by provider and runtime state.");
        output.AppendLine("# TYPE allstarr_sidecars gauge");
        foreach (var group in _sidecars.GetAll().GroupBy(item => new { item.ProviderId, item.State }))
        {
            output.Append("allstarr_sidecars{provider=\"")
                .Append(Label(group.Key.ProviderId))
                .Append("\",state=\"")
                .Append(Label(group.Key.State.ToString()))
                .Append("\"} ")
                .AppendLine(group.LongCount().ToString(CultureInfo.InvariantCulture));
        }
    }

    private void AppendTraceMetrics(StringBuilder output)
    {
        if (_traces == null)
        {
            return;
        }

        output.AppendLine("# HELP allstarr_trace_spans Buffered correlated platform spans by operation and outcome.");
        output.AppendLine("# TYPE allstarr_trace_spans gauge");
        foreach (var group in _traces.GetSnapshot().GroupBy(item => new { item.Operation, item.Failed }))
        {
            output.Append("allstarr_trace_spans{operation=\"")
                .Append(Label(group.Key.Operation))
                .Append("\",outcome=\"")
                .Append(group.Key.Failed ? "failed" : "succeeded")
                .Append("\"} ")
                .AppendLine(group.LongCount().ToString(CultureInfo.InvariantCulture));
        }
    }

    private static long? TryGetFreeSpace(string configuredPath)
    {
        try
        {
            var path = Path.GetFullPath(configuredPath);
            var drive = DriveInfo.GetDrives()
                .Where(item => path.StartsWith(item.RootDirectory.FullName, StringComparison.Ordinal))
                .OrderByDescending(item => item.RootDirectory.FullName.Length)
                .FirstOrDefault();
            return drive is { IsReady: true } ? drive.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }

    private static void Gauge(StringBuilder output, string name, string help, double value)
    {
        output.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
        output.Append("# TYPE ").Append(name).AppendLine(" gauge");
        output.Append(name).Append(' ')
            .AppendLine(value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static string Label(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", string.Empty, StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .ToLowerInvariant();
}
