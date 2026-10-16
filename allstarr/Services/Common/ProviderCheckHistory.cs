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

public sealed class ProviderCheckHistory(IDbContextFactory<AllstarrDbContext> factory, TimeProvider time)
{
    public const string Category = "provider-check";

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
                if (result?.Status.TestedAt != null) results.Add(result);
            }
            catch (JsonException) { }
        }
        return results;
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
