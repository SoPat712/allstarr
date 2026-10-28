using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Health;

public sealed record ProblemDiskSample(string Filesystem, string[] Roots, long? FreeBytes, long? TotalBytes);

public interface IProblemDiskSampler
{
    IReadOnlyList<ProblemDiskSample> Sample();
}

public sealed class ProblemDiskSampler(StorageOptions storage, IConfiguration configuration) : IProblemDiskSampler
{
    public IReadOnlyList<ProblemDiskSample> Sample()
    {
        var roots = new Dictionary<string, string?>
        {
            ["database"] = storage.DataDirectory,
            ["cache"] = configuration["Cache:MediaDirectory"],
            ["kept"] = configuration["Library:KeptPath"],
            ["downloads"] = configuration["Library:DownloadPath"],
            ["staging"] = configuration["Downloads:Workspace:RootPath"]
        };
        var samples = new List<ProblemDiskSample>();
        var drives = DriveInfo.GetDrives().OrderByDescending(drive => drive.Name.Length).ToArray();
        var mounts = OperatingSystem.IsLinux() && File.Exists("/proc/self/mountinfo")
            ? File.ReadAllLines("/proc/self/mountinfo").Select(line => line.Split(' ')).Where(parts => parts.Length > 5)
                .Select(parts => (Device: parts[2], Path: parts[4].Replace("\\040", " ").Replace("\\134", "\\")))
                .OrderByDescending(mount => mount.Path.Length).ToArray() : [];
        foreach (var (label, path) in roots)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var full = Path.GetFullPath(path);
                if (!Directory.Exists(full)) { samples.Add(new(label, [label], null, null)); continue; }
                var drive = drives.FirstOrDefault(item => Contains(item.Name, full));
                var mount = mounts.FirstOrDefault(item => Contains(item.Path, full));
                var key = mount.Device ?? drive?.Name ?? label;
                if (drive == null || !drive.IsReady) { samples.Add(new(key, [label], null, null)); continue; }
                samples.Add(new(key, [label], drive.AvailableFreeSpace, drive.TotalSize));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                samples.Add(new(label, [label], null, null));
            }
        }
        return Group(samples);
    }

    internal static IReadOnlyList<ProblemDiskSample> Group(IEnumerable<ProblemDiskSample> samples) => samples
        .GroupBy(sample => sample.Filesystem, StringComparer.Ordinal).Select(group => new ProblemDiskSample(group.Key,
            group.SelectMany(item => item.Roots).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            group.Any(item => item.FreeBytes == null) ? null : group.Min(item => item.FreeBytes),
            group.Any(item => item.TotalBytes == null) ? null : group.Min(item => item.TotalBytes))).ToArray();

    private static bool Contains(string root, string path) => root == "/" || path == root.TrimEnd(Path.DirectorySeparatorChar) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}

public sealed class ProblemAlertMonitor(IDbContextFactory<AllstarrDbContext> factory, ProblemAlertService alerts,
    ProviderRuntimeHealth health, ProviderStatusManager statuses, IProblemDiskSampler disks, IPlatformClock clock,
    ILogger<ProblemAlertMonitor> logger, PlatformReadinessService? readiness = null) : BackgroundService
{
    private readonly DateTimeOffset _startedAt = clock.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { await SampleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Problem alert observations are temporarily unavailable."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task SampleAsync(CancellationToken token)
    {
        if (readiness != null && clock.UtcNow >= _startedAt.AddMinutes(1) &&
            ReadinessEvidence(await readiness.CheckAsync(token), clock.UtcNow) is { } readinessEvidence)
            await alerts.ObserveAsync(readinessEvidence, token);
        await using var db = await factory.CreateDbContextAsync(token);
        var accounts = await db.ProviderAccounts.AsNoTracking().Where(item => item.Enabled).ToDictionaryAsync(item => item.Id, token);
        var observations = health.Snapshot().Where(item => statuses.IsAlertObservationEnabled(item.Key) &&
            (item.Key.ProviderAccountId is not { } id || accounts.TryGetValue(id, out var account) && account.Revision == item.Value.AccountRevision)).ToArray();
        var currentByKey = observations.ToDictionary(item => item.Key, item => item.Value);
        var recoveryBlocked = observations.Where(item => ProviderEvidence(Resource(item.Key), null, item.Value)?.Kind == "source-failure")
            .Select(item => Resource(item.Key)).ToHashSet(StringComparer.Ordinal);
        var samples = observations.SelectMany(item => health.AlertSamples(item.Key, item.Value.AccountRevision).Append(item.Value)
                .DistinctBy(sample => sample.ObservedAt).Select(sample => (Key: item.Key, Sample: sample)))
            .OrderBy(item => item.Sample.ObservedAt);
        foreach (var (key, sample) in samples)
        {
            var owner = key.ProviderAccountId is { } id ? accounts[id].OwnerUserId : null;
            var resource = Resource(key);
            var currentTime = ReferenceEquals(currentByKey[key], sample) ? clock.UtcNow : (DateTimeOffset?)null;
            if (ProviderEvidence(resource, owner, sample, currentTime) is { } evidence &&
                (evidence.Active || statuses.CanProveAccountAuthentication(key)))
                await alerts.ObserveAsync(evidence, token);
            if (sample.Health == ProviderHealthState.Healthy && !recoveryBlocked.Contains(resource))
                await alerts.ObserveAsync(new("source-failure", resource, owner, false, "warning", sample.ObservedAt, RecoverAfter: 2), token);
        }
        if (health.AppleDownloadSnapshot is { } apple && health.AppleDownloadObservedAt is { } appleAt &&
            apple.State is Services.AppleMusic.AppleDownloadEndpointState.Available or Services.AppleMusic.AppleDownloadEndpointState.NeedsAuthentication)
            await alerts.ObserveAsync(new("provider-login", "apple-download", null,
                apple.State == Services.AppleMusic.AppleDownloadEndpointState.NeedsAuthentication, "warning", appleAt), token);
        var thresholds = ProblemAlertService.ParseThresholds(await db.ProblemAlertConfigurations.AsNoTracking().SingleOrDefaultAsync(token));
        foreach (var sample in disks.Sample())
        {
            var open = await db.ProblemIncidents.AsNoTracking().AnyAsync(item => item.Kind == "disk-space" &&
                item.Resource == sample.Filesystem && item.State == "open", token);
            if (DiskEvidence(sample.Filesystem, sample.FreeBytes, sample.TotalBytes, thresholds, clock.UtcNow, open) is { } evidence)
                await alerts.ObserveAsync(evidence with { Detail = new(sample.Roots, sample.FreeBytes, sample.TotalBytes) }, token);
        }
    }

    private static string Resource(ProviderRuntimeStatusKey key) => key.ProviderAccountId?.ToString("N") ?? key.Provider;

    public static ProblemAlertEvidence? ReadinessEvidence(PlatformReadinessSnapshot snapshot, DateTimeOffset observedAt)
    {
        if (snapshot.Ready) return new("health-check", "readiness", null, false, "warning", observedAt, RecoverAfter: 2);
        var failures = snapshot.Components.Where(component => component.Required && component.State != "ready").ToArray();
        if (failures.Length == 0 || failures.All(component => component.State is "initializing" or "unknown" ||
            component.ErrorCode is "required_directory_space_low" or "required_directory_space_unknown")) return null;
        return new("health-check", "readiness", null, true, "warning", observedAt, OpenAfter: 2, RecoverAfter: 2);
    }

    public static ProblemAlertEvidence? ProviderEvidence(string resource, Guid? owner, ProviderHealthObservation sample, DateTimeOffset? now = null)
    {
        if (sample.Health == ProviderHealthState.Healthy)
            return new("provider-login", resource, owner, false, "warning", sample.ObservedAt);
        if (sample.Health != ProviderHealthState.Degraded) return null;
        if (sample.ReauthenticationRequired || sample.ReasonCode is "unauthorized" or "account-needs-reauthentication" or "authentication-required" or "apple-download-sign-in-required")
            return new("provider-login", resource, owner, true, "warning", sample.ObservedAt);
        if (sample.ReasonCode is not ("timeout" or "transient-failure" or "permanent-failure" or "probe_failed" or
            "service_unavailable" or "unreachable" or "sidecar_unreachable" or "sidecar_timeout")) return null;
        var sustainedPause = now.HasValue && sample.PausedAt?.AddMinutes(10) <= now && sample.RetryAfter > now;
        return new("source-failure", resource, owner, true, "warning", sustainedPause ? now!.Value : sample.ObservedAt,
            OpenAfter: sustainedPause ? 1 : 3, RecoverAfter: 2);
    }

    public static ProblemAlertEvidence? DiskEvidence(string filesystem, long? free, long? total,
        ProblemAlertThresholds thresholds, DateTimeOffset observedAt, bool open)
    {
        if (free == null || total is null or <= 0 || free < 0 || free > total) return null;
        var percent = 100d * free.Value / total.Value;
        var critical = free < thresholds.CriticalBytes || percent < thresholds.CriticalPercent;
        var warning = free < thresholds.WarningBytes + (open ? thresholds.HysteresisBytes : 0) ||
            percent < thresholds.WarningPercent + (open ? thresholds.HysteresisPercent : 0);
        return new("disk-space", filesystem, null, critical || warning, critical ? "critical" : "warning", observedAt,
            OpenAfter: critical ? 1 : 2, RecoverAfter: 2);
    }
}
