using System.Data;
using System.Globalization;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Admin;

public readonly record struct AdminUpdateScope(Guid TenantId, Guid? UserId, bool IsAdministrator);

public readonly record struct AdminUpdateCursor(
    DateTimeOffset OccurredAt,
    int Source,
    Guid ResourceId,
    long Revision)
{
    private const int LastSource = 5;

    public static AdminUpdateCursor Now() =>
        new(DateTimeOffset.UtcNow, LastSource, new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff"), long.MaxValue);

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{OccurredAt.UtcTicks}:{Source}:{ResourceId:N}:{Revision}");

    public static bool TryParse(string? value, out AdminUpdateCursor cursor)
    {
        cursor = default;
        var parts = value?.Split(':');
        if (parts is not { Length: 4 } ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks < DateTimeOffset.MinValue.UtcTicks ||
            ticks > DateTimeOffset.MaxValue.UtcTicks ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var source) ||
            source is < 0 or > LastSource ||
            !Guid.TryParseExact(parts[2], "N", out var resourceId) ||
            !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision < 0)
        {
            return false;
        }

        cursor = new AdminUpdateCursor(new DateTimeOffset(ticks, TimeSpan.Zero), source, resourceId, revision);
        return true;
    }
}

public sealed record AdminUpdateEvent(
    string EventId,
    string Resource,
    string Action,
    Guid ResourceId,
    long Revision,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    Guid? JobId,
    object Data);

public sealed class AdminUpdateFeed(IDbContextFactory<AllstarrDbContext> contextFactory)
{
    private const int AuditSource = 0;
    private const int JobSource = 1;
    private const int OutboxSource = 2;
    private const int TrackMatchSource = 3;
    private const int PlaylistSnapshotSource = 4;
    private const int ProviderHealthSource = 5;

    public async Task<IReadOnlyList<AdminUpdateEvent>> ReadAsync(
        AdminUpdateScope scope,
        AdminUpdateCursor cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 250);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        var jobs = await context.Jobs.AsNoTracking()
            .Where(item => item.TenantId == scope.TenantId &&
                (scope.IsAdministrator || item.OwnerUserId == scope.UserId) &&
                (item.UpdatedAt > cursor.OccurredAt ||
                 item.UpdatedAt == cursor.OccurredAt &&
                 (JobSource > cursor.Source ||
                  JobSource == cursor.Source &&
                  (item.Id.CompareTo(cursor.ResourceId) > 0 ||
                   item.Id == cursor.ResourceId && item.Revision > cursor.Revision))))
            .OrderBy(item => item.UpdatedAt).ThenBy(item => item.Id).ThenBy(item => item.Revision)
            .Take(limit)
            .Select(item => new
            {
                item.Id,
                item.UpdatedAt,
                item.Revision,
                item.CorrelationId,
                item.Type,
                item.State,
                item.LastErrorCode,
                item.AttemptCount,
                item.DeferralCount
            })
            .ToListAsync(cancellationToken);

        var audits = await context.AuditEvents.AsNoTracking()
            .Where(item => item.TenantId == scope.TenantId &&
                (scope.IsAdministrator ||
                 item.ActorUserId == scope.UserId ||
                 context.Jobs.Any(job =>
                     job.TenantId == scope.TenantId &&
                     job.OwnerUserId == scope.UserId &&
                     job.CorrelationId == item.CorrelationId)) &&
                (item.CreatedAt > cursor.OccurredAt ||
                 item.CreatedAt == cursor.OccurredAt &&
                 (AuditSource > cursor.Source ||
                  AuditSource == cursor.Source &&
                  item.Id.CompareTo(cursor.ResourceId) > 0)))
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
            .Take(limit)
            .Select(item => new
            {
                item.Id,
                item.CreatedAt,
                item.Category,
                item.Action,
                item.Outcome,
                item.CorrelationId
            })
            .ToListAsync(cancellationToken);

        var auditCorrelations = audits.Select(item => item.CorrelationId).Distinct().ToArray();
        var auditJobs = auditCorrelations.Length == 0
            ? new Dictionary<string, Guid>(StringComparer.Ordinal)
            : (await context.Jobs.AsNoTracking()
                .Where(item => item.TenantId == scope.TenantId &&
                    (scope.IsAdministrator || item.OwnerUserId == scope.UserId) &&
                    auditCorrelations.Contains(item.CorrelationId))
                .Select(item => new { item.CorrelationId, item.Id })
                .ToListAsync(cancellationToken))
                .GroupBy(item => item.CorrelationId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.MinBy(item => item.Id)!.Id,
                    StringComparer.Ordinal);

        var outbox = scope.IsAdministrator
            ? await context.OutboxMessages.AsNoTracking()
                .Where(item => item.TenantId == scope.TenantId &&
                    (item.UpdatedAt > cursor.OccurredAt ||
                     item.UpdatedAt == cursor.OccurredAt &&
                     (OutboxSource > cursor.Source ||
                      OutboxSource == cursor.Source &&
                      (item.Id.CompareTo(cursor.ResourceId) > 0 ||
                       item.Id == cursor.ResourceId && item.Revision > cursor.Revision))))
                .OrderBy(item => item.UpdatedAt).ThenBy(item => item.Id).ThenBy(item => item.Revision)
                .Take(limit)
                .Select(item => new
                {
                    item.Id,
                    item.UpdatedAt,
                    item.Revision,
                    item.Type,
                    item.State,
                    item.LastErrorCode,
                    item.AttemptCount
                })
                .ToListAsync(cancellationToken)
            : [];

        var matches = await context.TrackMatches.AsNoTracking()
            .Where(item => item.TenantId == scope.TenantId &&
                (scope.IsAdministrator || item.OwnerUserId == scope.UserId) &&
                (item.DecidedAt > cursor.OccurredAt ||
                 item.DecidedAt == cursor.OccurredAt &&
                 (TrackMatchSource > cursor.Source ||
                  TrackMatchSource == cursor.Source &&
                  (item.Id.CompareTo(cursor.ResourceId) > 0 ||
                   item.Id == cursor.ResourceId && item.Revision > cursor.Revision))))
            .OrderBy(item => item.DecidedAt).ThenBy(item => item.Id).ThenBy(item => item.Revision)
            .Take(limit)
            .Select(item => new
            {
                item.Id,
                item.DecidedAt,
                item.Revision,
                item.CorrelationId,
                item.State,
                item.Confidence,
                item.Threshold,
                item.DecisionVersion,
                item.LibraryScopeId
            })
            .ToListAsync(cancellationToken);

        var snapshots = await context.PlaylistSourceSnapshots.AsNoTracking()
            .Where(item => item.TenantId == scope.TenantId &&
                (scope.IsAdministrator || item.OwnerUserId == scope.UserId) &&
                (item.RetrievedAt > cursor.OccurredAt ||
                 item.RetrievedAt == cursor.OccurredAt &&
                 (PlaylistSnapshotSource > cursor.Source ||
                  PlaylistSnapshotSource == cursor.Source &&
                  item.Id.CompareTo(cursor.ResourceId) > 0)))
            .OrderBy(item => item.RetrievedAt).ThenBy(item => item.Id)
            .Take(limit)
            .Select(item => new
            {
                item.Id,
                item.RetrievedAt,
                item.CorrelationId,
                item.SourceJobId,
                item.PlaylistLinkId,
                item.SnapshotVersion,
                item.PublishedAt
            })
            .ToListAsync(cancellationToken);

        var health = await context.ProviderHealthSamples.AsNoTracking()
            .Where(item => item.TenantId == scope.TenantId &&
                (scope.IsAdministrator ||
                 context.ProviderAccounts.Any(account =>
                     account.Id == item.ProviderAccountId &&
                     account.TenantId == scope.TenantId &&
                     (account.Scope != ProviderAccountScope.User || account.OwnerUserId == scope.UserId))) &&
                (item.ObservedAt > cursor.OccurredAt ||
                 item.ObservedAt == cursor.OccurredAt &&
                 (ProviderHealthSource > cursor.Source ||
                  ProviderHealthSource == cursor.Source &&
                  item.Id.CompareTo(cursor.ResourceId) > 0)))
            .OrderBy(item => item.ObservedAt).ThenBy(item => item.Id)
            .Take(limit)
            .Select(item => new
            {
                item.Id,
                item.ObservedAt,
                item.ProviderAccountId,
                item.Capability,
                item.State,
                item.LatencyMilliseconds,
                item.FailureCode
            })
            .ToListAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return audits.Select(item => Event(
                AuditSource,
                item.Id,
                0,
                item.CreatedAt,
                "audit",
                item.Action,
                item.CorrelationId,
                auditJobs.GetValueOrDefault(item.CorrelationId),
                new { item.Category, item.Outcome }))
            .Concat(jobs.Select(item => Event(
                JobSource,
                item.Id,
                item.Revision,
                item.UpdatedAt,
                "job",
                "changed",
                item.CorrelationId,
                item.Id,
                new
                {
                    item.Type,
                    state = item.State.ToString(),
                    errorCode = item.LastErrorCode,
                    item.AttemptCount,
                    item.DeferralCount
                })))
            .Concat(outbox.Select(item => Event(
                OutboxSource,
                item.Id,
                item.Revision,
                item.UpdatedAt,
                "outbox",
                "changed",
                null,
                null,
                new
                {
                    item.Type,
                    state = item.State.ToString(),
                    errorCode = item.LastErrorCode,
                    item.AttemptCount
                })))
            .Concat(matches.Select(item => Event(
                TrackMatchSource,
                item.Id,
                item.Revision,
                item.DecidedAt,
                "track-match",
                "decided",
                item.CorrelationId,
                null,
                new
                {
                    state = item.State.ToString(),
                    item.Confidence,
                    item.Threshold,
                    item.DecisionVersion,
                    item.LibraryScopeId
                })))
            .Concat(snapshots.Select(item => Event(
                PlaylistSnapshotSource,
                item.Id,
                0,
                item.RetrievedAt,
                "playlist-source",
                item.PublishedAt.HasValue ? "published" : "retrieved",
                item.CorrelationId,
                item.SourceJobId,
                new { item.PlaylistLinkId, item.SnapshotVersion })))
            .Concat(health.Select(item => Event(
                ProviderHealthSource,
                item.Id,
                0,
                item.ObservedAt,
                "provider-health",
                "observed",
                null,
                null,
                new
                {
                    item.ProviderAccountId,
                    item.Capability,
                    state = item.State.ToString(),
                    item.LatencyMilliseconds,
                    item.FailureCode
                })))
            .OrderBy(item => item.OccurredAt)
            .ThenBy(item => SourceOf(item.Resource))
            .ThenBy(item => item.ResourceId)
            .ThenBy(item => item.Revision)
            .Take(limit)
            .ToList();
    }

    private static AdminUpdateEvent Event(
        int source,
        Guid resourceId,
        long revision,
        DateTimeOffset occurredAt,
        string resource,
        string action,
        string? correlationId,
        Guid? jobId,
        object data)
    {
        var cursor = new AdminUpdateCursor(occurredAt, source, resourceId, revision);
        return new AdminUpdateEvent(
            cursor.ToString(),
            resource,
            action,
            resourceId,
            revision,
            occurredAt,
            string.IsNullOrWhiteSpace(correlationId) ? null : correlationId,
            jobId,
            data);
    }

    private static int SourceOf(string resource) => resource switch
    {
        "audit" => AuditSource,
        "job" => JobSource,
        "outbox" => OutboxSource,
        "track-match" => TrackMatchSource,
        "playlist-source" => PlaylistSnapshotSource,
        _ => ProviderHealthSource
    };
}
