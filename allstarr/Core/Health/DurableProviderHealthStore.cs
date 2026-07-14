using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Health;

public sealed class ProviderHealthOptions
{
    public const string SectionName = "ProviderHealth";

    public int FailureThreshold { get; set; } = 3;
    public int CircuitOpenSeconds { get; set; } = 60;
    public int SampleTtlSeconds { get; set; } = 300;
    public int RollupWindowMinutes { get; set; } = 15;
    public int SampleRetentionDays { get; set; } = 7;

    public void Validate()
    {
        if (FailureThreshold is < 1 or > 100)
        {
            throw new InvalidOperationException("ProviderHealth:FailureThreshold must be between 1 and 100.");
        }

        if (CircuitOpenSeconds is < 5 or > 86400 || SampleTtlSeconds is < 5 or > 86400)
        {
            throw new InvalidOperationException("Provider health durations must be between 5 and 86400 seconds.");
        }

        if (RollupWindowMinutes is < 1 or > 1440 || SampleRetentionDays is < 1 or > 365)
        {
            throw new InvalidOperationException(
                "Provider health rollup and retention settings are outside the supported range.");
        }
    }
}

public sealed record DurableProviderHealthSnapshot(
    string ProviderId,
    string AccountKey,
    Guid ProviderAccountId,
    string Capability,
    ProviderHealthState State,
    DateTimeOffset ObservedAt,
    DateTimeOffset ExpiresAt,
    long? LatencyMilliseconds,
    string? FailureCode);

public sealed record DurableProviderCircuitSnapshot(
    Guid ProviderAccountId,
    string Capability,
    ProviderCircuitState State,
    int ConsecutiveFailures,
    DateTimeOffset? RetryAfter);

public sealed record DurableProviderHealthRollupSnapshot(
    Guid ProviderAccountId,
    string Capability,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    int SampleCount,
    int SuccessCount,
    int FailureCount,
    double SuccessRate,
    long? P50LatencyMilliseconds,
    long? P95LatencyMilliseconds,
    ProviderHealthState LastState,
    string? LastFailureCode);

public interface IDurableProviderHealthObservationStore
{
    Task<DurableProviderHealthSnapshot?> RecordAsync(
        string providerId,
        string accountKey,
        string capability,
        ProviderHealthState state,
        long? latencyMilliseconds = null,
        string? failureCode = null,
        CancellationToken cancellationToken = default);

    bool IsCircuitOpen(string providerId, string accountKey, string capability);
}

public sealed class DurableProviderHealthStore : IDurableProviderHealthObservationStore
{
    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly DurableStorageState _storageState;
    private readonly ProviderHealthOptions _options;
    private readonly IPlatformClock _clock;
    private readonly ConcurrentDictionary<HealthKey, DurableProviderHealthSnapshot> _latest = new();
    private readonly ConcurrentDictionary<CircuitKey, DurableProviderCircuitSnapshot> _circuits = new();

    public DurableProviderHealthStore(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableStorageState storageState,
        ProviderHealthOptions options,
        IPlatformClock clock)
    {
        _contextFactory = contextFactory;
        _storageState = storageState;
        _options = options;
        _clock = clock;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
        {
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.ProviderAccounts.AsNoTracking()
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var latestSamples = await context.ProviderHealthSamples.AsNoTracking()
            .GroupBy(item => new { item.ProviderAccountId, item.Capability })
            .Select(group => group.OrderByDescending(item => item.ObservedAt).First())
            .ToListAsync(cancellationToken);
        foreach (var sample in latestSamples)
        {
            if (!accounts.TryGetValue(sample.ProviderAccountId, out var account))
            {
                continue;
            }

            var accountKey = AccountKey(account);
            _latest[new HealthKey(account.ProviderId, accountKey, sample.Capability)] = new(
                account.ProviderId,
                accountKey,
                account.Id,
                sample.Capability,
                sample.State,
                sample.ObservedAt,
                sample.ExpiresAt,
                sample.LatencyMilliseconds,
                sample.FailureCode);
        }

        foreach (var circuit in await context.ProviderCircuits.AsNoTracking().ToListAsync(cancellationToken))
        {
            _circuits[new CircuitKey(circuit.ProviderAccountId, circuit.Capability)] = new(
                circuit.ProviderAccountId,
                circuit.Capability,
                circuit.State,
                circuit.ConsecutiveFailures,
                circuit.RetryAfter);
        }
    }

    public async Task<DurableProviderHealthSnapshot?> RecordAsync(
        string providerId,
        string accountKey,
        string capability,
        ProviderHealthState state,
        long? latencyMilliseconds = null,
        string? failureCode = null,
        CancellationToken cancellationToken = default)
    {
        if (_storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
        {
            return null;
        }

        providerId = Normalize(providerId);
        accountKey = Normalize(accountKey);
        capability = Normalize(capability);
        var now = _clock.UtcNow;
        using var activity = PlatformDiagnostics.ActivitySource.StartActivity("provider-health.record");
        activity?.SetTag("provider.id", providerId);
        activity?.SetTag("provider.capability", capability);
        var accountId = ResolveAccountId(providerId, accountKey);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var account = await context.ProviderAccounts.SingleOrDefaultAsync(
            item => item.Id == accountId,
            cancellationToken);
        if (account == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (!account.Enabled ||
            !account.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var sample = new ProviderHealthSampleRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = account.TenantId,
            ProviderAccountId = account.Id,
            Capability = capability,
            State = state,
            LatencyMilliseconds = latencyMilliseconds,
            FailureCode = SafeOperationalText.Sanitize(failureCode, 100),
            ObservedAt = now,
            ExpiresAt = now.AddSeconds(_options.SampleTtlSeconds)
        };
        context.ProviderHealthSamples.Add(sample);
        var circuit = await context.ProviderCircuits.SingleOrDefaultAsync(
            item => item.ProviderAccountId == account.Id && item.Capability == capability,
            cancellationToken);
        if (circuit == null)
        {
            circuit = new ProviderCircuitRecord
            {
                Id = Guid.CreateVersion7(),
                ProviderAccountId = account.Id,
                Capability = capability,
                State = ProviderCircuitState.Closed,
                UpdatedAt = now
            };
            context.ProviderCircuits.Add(circuit);
        }

        ApplyCircuitObservation(circuit, state, now);
        await context.SaveChangesAsync(cancellationToken);
        await UpdateRollupAsync(context, account, capability, now, cancellationToken);
        var retentionCutoff = now.AddDays(-_options.SampleRetentionDays);
        await context.ProviderHealthSamples
            .Where(item => item.ObservedAt < retentionCutoff)
            .ExecuteDeleteAsync(cancellationToken);
        await context.ProviderHealthRollups
            .Where(item => item.WindowEnd < retentionCutoff)
            .ExecuteDeleteAsync(cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var snapshot = new DurableProviderHealthSnapshot(
            providerId,
            accountKey,
            account.Id,
            capability,
            state,
            sample.ObservedAt,
            sample.ExpiresAt,
            sample.LatencyMilliseconds,
            sample.FailureCode);
        _latest[new HealthKey(providerId, accountKey, capability)] = snapshot;
        _circuits[new CircuitKey(account.Id, capability)] = ToSnapshot(circuit);
        PlatformDiagnostics.ProviderHealthSamples.Add(
            1,
            new("provider.id", providerId),
            new("provider.capability", capability),
            new("provider.state", state.ToString().ToLowerInvariant()));
        if (latencyMilliseconds.HasValue)
        {
            PlatformDiagnostics.ProviderProbeLatency.Record(
                latencyMilliseconds.Value,
                new("provider.id", providerId),
                new("provider.capability", capability));
        }
        return snapshot;
    }

    public async Task<DurableProviderHealthRollupSnapshot?> GetLatestRollupAsync(
        Guid providerAccountId,
        string capability,
        CancellationToken cancellationToken = default)
    {
        capability = Normalize(capability);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var rollup = await context.ProviderHealthRollups.AsNoTracking()
            .Where(item => item.ProviderAccountId == providerAccountId && item.Capability == capability)
            .OrderByDescending(item => item.WindowStart)
            .FirstOrDefaultAsync(cancellationToken);
        return rollup == null ? null : ToSnapshot(rollup);
    }

    public bool TryGetLatest(
        string providerId,
        string accountKey,
        string capability,
        out DurableProviderHealthSnapshot snapshot)
    {
        if (!_latest.TryGetValue(
            new HealthKey(Normalize(providerId), Normalize(accountKey), Normalize(capability)),
            out snapshot!) || snapshot.ExpiresAt <= _clock.UtcNow)
        {
            snapshot = null!;
            return false;
        }

        return true;
    }

    public bool TryGetLatestByAccountId(
        string providerId,
        Guid providerAccountId,
        string capability,
        out DurableProviderHealthSnapshot snapshot)
    {
        var normalizedProvider = Normalize(providerId);
        var normalizedCapability = Normalize(capability);
        snapshot = _latest.Values.FirstOrDefault(item =>
            item.ProviderAccountId == providerAccountId &&
            item.ProviderId.Equals(normalizedProvider, StringComparison.Ordinal) &&
            item.Capability.Equals(normalizedCapability, StringComparison.Ordinal) &&
            item.ExpiresAt > _clock.UtcNow)!;
        return snapshot != null;
    }

    public bool IsCircuitOpen(string providerId, string accountKey, string capability)
    {
        var accountId = ResolveAccountId(Normalize(providerId), Normalize(accountKey));
        if (!_circuits.TryGetValue(
                new CircuitKey(accountId, Normalize(capability)),
                out var circuit))
        {
            return false;
        }

        return circuit.State == ProviderCircuitState.Open && circuit.RetryAfter > _clock.UtcNow;
    }

    private void ApplyCircuitObservation(
        ProviderCircuitRecord circuit,
        ProviderHealthState state,
        DateTimeOffset now)
    {
        if (state == ProviderHealthState.Healthy)
        {
            circuit.State = ProviderCircuitState.Closed;
            circuit.ConsecutiveFailures = 0;
            circuit.OpenedAt = null;
            circuit.RetryAfter = null;
        }
        else if (state is ProviderHealthState.Degraded or
                 ProviderHealthState.Unavailable or
                 ProviderHealthState.Unauthorized)
        {
            circuit.ConsecutiveFailures++;
            var threshold = state == ProviderHealthState.Unauthorized
                ? 1
                : _options.FailureThreshold;
            if (circuit.ConsecutiveFailures >= threshold)
            {
                circuit.State = ProviderCircuitState.Open;
                circuit.OpenedAt = now;
                circuit.RetryAfter = now.AddSeconds(_options.CircuitOpenSeconds);
            }
        }

        circuit.UpdatedAt = now;
        circuit.Revision++;
    }

    private async Task UpdateRollupAsync(
        AllstarrDbContext context,
        ProviderAccountRecord account,
        string capability,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var window = TimeSpan.FromMinutes(_options.RollupWindowMinutes);
        var windowStartTicks = now.UtcTicks - (now.UtcTicks % window.Ticks);
        var windowStart = new DateTimeOffset(windowStartTicks, TimeSpan.Zero);
        var windowEnd = windowStart.Add(window);
        var samples = await context.ProviderHealthSamples.AsNoTracking()
            .Where(item =>
                item.ProviderAccountId == account.Id &&
                item.Capability == capability &&
                item.ObservedAt >= windowStart &&
                item.ObservedAt < windowEnd)
            .OrderBy(item => item.ObservedAt)
            .ToListAsync(cancellationToken);
        var rollup = await context.ProviderHealthRollups.SingleOrDefaultAsync(
            item => item.ProviderAccountId == account.Id &&
                    item.Capability == capability &&
                    item.WindowStart == windowStart,
            cancellationToken);
        if (rollup == null)
        {
            rollup = new ProviderHealthRollupRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = account.TenantId,
                ProviderAccountId = account.Id,
                Capability = capability,
                WindowStart = windowStart,
                WindowEnd = windowEnd
            };
            context.ProviderHealthRollups.Add(rollup);
        }

        var latencies = samples
            .Where(item => item.LatencyMilliseconds.HasValue)
            .Select(item => item.LatencyMilliseconds!.Value)
            .Order()
            .ToArray();
        rollup.SampleCount = samples.Count;
        rollup.SuccessCount = samples.Count(item => item.State == ProviderHealthState.Healthy);
        rollup.FailureCount = samples.Count(item => item.State is
            ProviderHealthState.Degraded or
            ProviderHealthState.Unavailable or
            ProviderHealthState.Unauthorized);
        rollup.SuccessRate = samples.Count == 0
            ? 0
            : (double)rollup.SuccessCount / samples.Count;
        rollup.P50LatencyMilliseconds = Percentile(latencies, 0.50);
        rollup.P95LatencyMilliseconds = Percentile(latencies, 0.95);
        rollup.LastState = samples[^1].State;
        rollup.LastFailureCode = samples[^1].FailureCode;
        rollup.UpdatedAt = now;
        rollup.Revision++;
    }

    private static long? Percentile(long[] values, double percentile)
    {
        if (values.Length == 0)
        {
            return null;
        }

        var index = Math.Max(0, (int)Math.Ceiling(percentile * values.Length) - 1);
        return values[index];
    }

    private static DurableProviderCircuitSnapshot ToSnapshot(ProviderCircuitRecord circuit) => new(
        circuit.ProviderAccountId,
        circuit.Capability,
        circuit.State,
        circuit.ConsecutiveFailures,
        circuit.RetryAfter);

    private static DurableProviderHealthRollupSnapshot ToSnapshot(
        ProviderHealthRollupRecord rollup) => new(
        rollup.ProviderAccountId,
        rollup.Capability,
        rollup.WindowStart,
        rollup.WindowEnd,
        rollup.SampleCount,
        rollup.SuccessCount,
        rollup.FailureCount,
        rollup.SuccessRate,
        rollup.P50LatencyMilliseconds,
        rollup.P95LatencyMilliseconds,
        rollup.LastState,
        rollup.LastFailureCode);

    private static string AccountKey(ProviderAccountRecord account) =>
        account.DisplayName.StartsWith("Legacy global ", StringComparison.Ordinal)
            ? "legacy-global"
            : account.Id.ToString("N");

    private static Guid ResolveAccountId(string providerId, string accountKey)
    {
        if (Guid.TryParse(accountKey, out var parsed))
        {
            return parsed;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"allstarr-provider-account|{providerId}|{accountKey}"));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes);
    }

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Provider health keys cannot be empty.")
            : value.Trim().ToLowerInvariant();

    private readonly record struct HealthKey(string ProviderId, string AccountKey, string Capability);
    private readonly record struct CircuitKey(Guid ProviderAccountId, string Capability);
}

public sealed class DurableProviderHealthInitializer(DurableProviderHealthStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.InitializeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
