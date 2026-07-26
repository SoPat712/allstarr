using System.Text.Json;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Operations;

public sealed record EndpointUsageCount(string Endpoint, int Count);
public sealed record EndpointUsageSummary(
    int TotalEndpoints,
    int TotalRequests,
    IReadOnlyList<EndpointUsageCount> Endpoints);

public sealed class EndpointUsageAudit(
    IDbContextFactory<AllstarrDbContext> factory,
    TimeProvider timeProvider)
{
    public const string Category = "endpoint-usage";

    public async Task RecordAsync(
        string method,
        string path,
        Guid? tenantId,
        Guid? actorUserId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var safeMethod = method.Trim().ToUpperInvariant();
        var safePath = path.Split('?', '#')[0]
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        var endpoint = $"{safeMethod} /{safePath.TrimStart('/')}";
        if (endpoint.Length > 200) endpoint = endpoint[..200];

        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        database.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ActorUserId = actorUserId,
            Category = Category,
            Action = endpoint,
            Outcome = "observed",
            CorrelationId = correlationId.Length <= 100 ? correlationId : correlationId[..100],
            DetailsJson = JsonSerializer.Serialize(new { method = safeMethod, path = safePath }),
            CreatedAt = timeProvider.GetUtcNow()
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<EndpointUsageSummary> SummarizeAsync(
        int top,
        DateTimeOffset? since,
        CancellationToken cancellationToken = default)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var query = database.AuditEvents.AsNoTracking()
            .Where(item => item.Category == Category);
        if (since.HasValue) query = query.Where(item => item.CreatedAt >= since.Value);

        var counts = await query
            .GroupBy(item => item.Action)
            .Select(group => new { Endpoint = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Endpoint)
            .ToListAsync(cancellationToken);
        return new(
            counts.Count,
            counts.Sum(item => item.Count),
            counts.Take(Math.Clamp(top, 1, 1000))
                .Select(item => new EndpointUsageCount(item.Endpoint, item.Count))
                .ToArray());
    }

    public async Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        return await database.AuditEvents
            .Where(item => item.Category == Category)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
