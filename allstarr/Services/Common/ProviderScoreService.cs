using System.Text.Json;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Common;

public sealed record ProviderCapabilityScore(string Capability, int? Score, double? UptimePercent, int Checks, int Failures,
    long? TypicalLatencyMilliseconds, string? LastFailureCode);

public sealed record ProviderScoreDay(DateOnly Day, int Passed, int Failed);

public sealed record ProviderScore(string ProviderId, int? Score, double? UptimePercent, int Checks,
    IReadOnlyList<ProviderCapabilityScore> Capabilities, IReadOnlyList<ProviderScoreDay> Days);

/// <summary>Turns the last 30 days of recorded provider checks into one comparable score per provider.</summary>
public sealed class ProviderScoreService(IDbContextFactory<AllstarrDbContext> factory, TimeProvider time)
{
    internal const int MinimumChecks = 5;
    private const int WindowDays = 30;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<ProviderScore>? cached;
    private DateTimeOffset cachedAt;

    private readonly record struct Check(string Provider, string Capability, DateTimeOffset At, bool Passed, long? Latency, string? Reason);

    public async Task<IReadOnlyList<ProviderScore>> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var now = time.GetUtcNow();
            if (cached != null && now - cachedAt < CacheFor) return cached;
            var cutoff = now.AddDays(-WindowDays);
            await using var db = await factory.CreateDbContextAsync(token);
            var details = await db.AuditEvents.AsNoTracking()
                .Where(row => row.Category == ProviderCheckHistory.Category && row.CreatedAt >= cutoff)
                .Select(row => row.DetailsJson).ToListAsync(token);
            var checks = new List<Check>(details.Count);
            foreach (var json in details)
                if (Parse(json) is { } check) checks.Add(check);
            cachedAt = now;
            return cached = Summarize(checks, now);
        }
        finally { gate.Release(); }
    }

    private static Check? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 16384) return null;
        try
        {
            var result = JsonSerializer.Deserialize<ProviderCheckResult>(json);
            if (result?.Status is not { HasCheckResult: true, TestedAt: { } at } status) return null;
            return new(result.Key.Provider, result.Key.Capability, at, status.Health == ProviderHealthState.Healthy,
                status.LatencyMilliseconds is >= 0 ? status.LatencyMilliseconds : null,
                ProviderCheckHistory.SafeReasonCode(status.ReasonCode));
        }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyList<ProviderScore> Summarize(IEnumerable<Check> checks, DateTimeOffset now) =>
        checks.Where(check => !allstarr.Core.Settings.RetiredProviderSettings.IsRetired(check.Provider))
            .GroupBy(check => check.Provider, StringComparer.OrdinalIgnoreCase).Select(provider =>
            {
                var rows = provider.ToArray();
                return new ProviderScore(provider.Key, Score(rows, now), Uptime(rows), rows.Length,
                    rows.GroupBy(check => check.Capability, StringComparer.OrdinalIgnoreCase).Select(group =>
                    {
                        var capability = group.ToArray();
                        return new ProviderCapabilityScore(group.Key, Score(capability, now), Uptime(capability), capability.Length,
                            capability.Count(check => !check.Passed), Typical(capability),
                            capability.Where(check => !check.Passed).OrderByDescending(check => check.At).FirstOrDefault().Reason);
                    }).OrderBy(item => item.Capability, StringComparer.Ordinal).ToArray(),
                    rows.GroupBy(check => DateOnly.FromDateTime(check.At.UtcDateTime))
                        .Select(day => new ProviderScoreDay(day.Key, day.Count(check => check.Passed), day.Count(check => !check.Passed)))
                        .OrderBy(day => day.Day).ToArray());
            }).OrderBy(item => item.ProviderId, StringComparer.Ordinal).ToArray();

    private static double? Uptime(IReadOnlyCollection<Check> rows) =>
        rows.Count == 0 ? null : Math.Round(100d * rows.Count(check => check.Passed) / rows.Count, 1);

    private static long? Typical(IEnumerable<Check> rows)
    {
        var latencies = rows.Where(check => check.Passed && check.Latency != null).Select(check => check.Latency!.Value).Order().ToArray();
        return latencies.Length == 0 ? null : latencies[latencies.Length / 2];
    }

    // Four fifths reliability, with the last week counting double, and one fifth speed.
    private static int? Score(IReadOnlyCollection<Check> rows, DateTimeOffset now)
    {
        if (rows.Count < MinimumChecks) return null;
        var recent = now.AddDays(-7);
        double earned = 0, possible = 0;
        foreach (var check in rows)
        {
            var weight = check.At >= recent ? 2 : 1;
            possible += weight;
            if (check.Passed) earned += weight;
        }
        var speed = Typical(rows) is { } typical ? Math.Clamp(1 - (typical - 500) / 7500d, 0, 1) : 1;
        return (int)Math.Round(100 * (0.8 * earned / possible + 0.2 * speed));
    }

    internal static IReadOnlyList<ProviderScore> ForTest(IEnumerable<(string Provider, string Capability, DateTimeOffset At, bool Passed, long? Latency, string? Reason)> rows, DateTimeOffset now) =>
        Summarize(rows.Select(row => new Check(row.Provider, row.Capability, row.At, row.Passed, row.Latency, row.Reason)), now);
}
