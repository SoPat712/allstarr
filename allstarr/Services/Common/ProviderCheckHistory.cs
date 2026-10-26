using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Common;

public sealed record ProviderCheckResult(ProviderRuntimeStatusKey Key, long? Revision,
    ProviderRuntimeStatus Status, string? SeekRung = null)
{
    [JsonPropertyName("providerId")]
    public string ProviderId => Key.Provider;
    [JsonPropertyName("message")]
    public string Message => $"{Key.Capability} check: {Status.Health.ToString().ToLowerInvariant()}.";
}

public sealed record ProviderCheckHistoryTarget(
    ProviderRuntimeStatusKey Key,
    long? Revision,
    string AccountLabel,
    string AccountScope)
{
    public string SubjectId =>
        $"{Key.Provider}/{Key.Capability}/{Key.ProviderAccountId?.ToString("N") ?? "public"}";
}

public sealed record ProviderCheckHistoryItem(
    ProviderCheckResult Result,
    string AccountLabel,
    string AccountScope,
    DateTimeOffset RecordedAt);

public sealed class ProviderCheckHistory(IDbContextFactory<AllstarrDbContext> factory, TimeProvider time)
{
    public const string Category = "provider-check";

    private const int MaximumReadAhead = 96;

    public static string? SafeReasonCode(string? reason) => reason switch
    {
        null or "" => null,
        "RateLimited" => "rate-limited",
        "TransientFailure" => "transient-failure",
        "AccountNeedsReauthentication" => "account-needs-reauthentication",
        "Unauthorized" => "unauthorized",
        "IncompatibleMedia" => "incompatible-media",
        "apple-web-token-unavailable" or "provider-contract-changed" or "capability-unavailable" or
        "account-needs-configuration" or "account-needs-reauthentication" or "unauthorized" or
        "rate-limited" or "transient-failure" or "permanent-failure" or "incompatible-media" or
        "probe_failed" or "timeout" or "sample-unavailable" or "service_unavailable" or
        "download-not-verified" or "probe_not_available" or "container-conversion-failed" or
        "media-tools-unavailable" or "verification-required" or "authentication-required" or
        "access-denied" or "not-found" or "metadata-not-found" or "metadata-invalid" or
        "playlist-not-found" or "playlist-empty" or "playlist-invalid" or
        "playlist-sample-unavailable" or "probe-limit-exceeded" or "blocked-by-challenge" or
        "lyrics-upstream-busy-or-timed-out" or "unreachable" or "sidecar_not_installed" or
        "sidecar_unreachable" or "sidecar_timeout" or "sidecar_authentication_required" or
        "sidecar_version_mismatch" or "local-capacity-unavailable" or "not_found" or "NotFound" or
        "http-404" or "http-410" or "circuit_open" or "missing_provider_account_secret" or
        "unsupported_capability" or "provider_disabled" or "provider_account_required" or
        "account_disabled" or "apple-download-sign-in-required" or
        "apple-download-gateway-update-required" => reason,
        _ => "probe_failed"
    };

    public async Task<IReadOnlyList<ProviderCheckResult>> ReadLatestAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var cutoff = time.GetUtcNow().AddDays(-30);
        var rows = await db.AuditEvents.AsNoTracking().Where(row => row.Category == Category && row.CreatedAt >= cutoff)
            .GroupBy(row => row.SubjectId)
            .Select(group => group.OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id).First())
            .ToListAsync(token);
        var results = new List<ProviderCheckResult>();
        foreach (var row in rows)
        {
            try
            {
                var result = JsonSerializer.Deserialize<ProviderCheckResult>(row.DetailsJson);
                if (result?.Status.TestedAt != null && !allstarr.Core.Settings.RetiredProviderSettings.IsRetired(result.Key.Provider))
                    results.Add(result);
            }
            catch (JsonException) { }
        }
        return results;
    }

    public async Task<IReadOnlyList<ProviderCheckHistoryItem>> ReadRecentAsync(
        IReadOnlyCollection<ProviderCheckHistoryTarget> targets,
        int limit,
        CancellationToken token)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 24);
        var targetBySubject = targets
            .GroupBy(item => item.SubjectId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (targetBySubject.Count == 0) return [];

        var now = time.GetUtcNow();
        var cutoff = now.AddDays(-30);
        var subjects = targetBySubject.Keys.ToArray();
        await using var db = await factory.CreateDbContextAsync(token);
        var rows = await db.AuditEvents.AsNoTracking()
            .Where(row => row.Category == Category && row.SubjectType == Category && row.SubjectId != null &&
                subjects.Contains(row.SubjectId!) && row.CreatedAt >= cutoff && row.CreatedAt <= now)
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .Take(Math.Min(effectiveLimit * 4, MaximumReadAhead))
            .ToListAsync(token);

        var results = new List<(ProviderCheckHistoryItem Item, Guid Id)>();
        foreach (var row in rows)
        {
            if (row.SubjectId is not { } subject || !targetBySubject.TryGetValue(subject, out var target) ||
                row.DetailsJson is null || row.DetailsJson.Length > 16384) continue;
            try
            {
                var result = JsonSerializer.Deserialize<ProviderCheckResult>(row.DetailsJson);
                if (result == null || result.Status is not { } status || result.Key != target.Key ||
                    result.Revision != target.Revision || status.Provider != target.Key.Provider ||
                    status.Capability != target.Key.Capability || status.TestedAt is not { } testedAt ||
                    testedAt < cutoff || testedAt > now) continue;
                results.Add((new(result, target.AccountLabel, target.AccountScope, row.CreatedAt), row.Id));
            }
            catch (JsonException) { }
        }

        return results.OrderByDescending(item => item.Item.Result.Status.TestedAt)
            .ThenByDescending(item => item.Item.RecordedAt)
            .ThenByDescending(item => item.Id)
            .Take(effectiveLimit)
            .Select(item => item.Item)
            .ToArray();
    }

    public async Task RecordAsync(ProviderCheckResult result, string correlationId, CancellationToken token,
        ProviderHealthState? previousHealth = null)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var subject = $"{result.Key.Provider}/{result.Key.Capability}/{result.Key.ProviderAccountId?.ToString("N") ?? "public"}";
        var previous = await db.AuditEvents.AsNoTracking().Where(row => row.Category == Category && row.SubjectId == subject)
            .OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id).FirstOrDefaultAsync(token);
        ProviderCheckResult? last = null;
        if (previous != null)
            try { last = JsonSerializer.Deserialize<ProviderCheckResult>(previous.DetailsJson); }
            catch (JsonException) { }
        var now = time.GetUtcNow();
        db.AuditEvents.Add(new()
        {
            Id = Guid.CreateVersion7(),
            Category = Category,
            Action = "check-completed",
            Outcome = "recorded",
            SubjectType = Category,
            SubjectId = subject,
            CorrelationId = correlationId,
            DetailsJson = JsonSerializer.Serialize(result),
            CreatedAt = now
        });
        if ((last?.Status.Health == ProviderHealthState.Healthy || previousHealth == ProviderHealthState.Healthy) &&
            result.Status.Health == ProviderHealthState.Degraded)
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                Category = "provider-status",
                Action = "provider-degraded",
                Outcome = "unhealthy",
                SubjectType = Category,
                SubjectId = subject,
                CorrelationId = correlationId,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    providerId = result.Key.Provider,
                    message = $"{result.Key.Capability} check failed. Open the source to review its connection."
                }),
                CreatedAt = now
            });
        await db.SaveChangesAsync(token);
        var cutoff = now.AddDays(-30);
        await db.AuditEvents.Where(row => row.Category == Category && row.CreatedAt < cutoff).ExecuteDeleteAsync(token);
    }
}
