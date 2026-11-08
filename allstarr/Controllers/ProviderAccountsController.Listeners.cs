using allstarr.Core.Identity;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

public sealed partial class ProviderAccountsController
{
    [HttpPut("listeners/{userId:guid}/trust")]
    public async Task<IActionResult> SetListenerTrust(
        Guid userId,
        [FromBody] UpdateListenerTrustRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetSession(out var session, out var error)) return error!;
        if (!session.IsAdministrator || session.AllstarrUserId is not { } administratorId ||
            session.ExpiresAtUtc <= DateTime.UtcNow)
            return ManagementForbidden();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var backendType = session.BackendType.ToLowerInvariant();
        var administratorExists = await context.Users.AsNoTracking().AnyAsync(item =>
            item.Id == administratorId && item.Enabled && item.IsAdmin &&
            item.BackendType == backendType && item.BackendInstanceId == session.BackendInstanceId &&
            item.BackendPrincipalId == session.UserId, cancellationToken);
        if (!administratorExists) return ManagementForbidden();

        var listener = await context.Users.SingleOrDefaultAsync(item => item.Id == userId && item.Enabled,
            cancellationToken);
        if (listener == null) return NotFound();
        listener.IsTrustedListener = request.IsTrustedListener;
        listener.UpdatedAt = DateTimeOffset.UtcNow;
        AddAudit(context, session, "listener.trust-updated", "succeeded", new
        {
            userId = listener.Id,
            isTrustedListener = listener.IsTrustedListener
        });
        await context.SaveChangesAsync(cancellationToken);
        return Ok(new { id = listener.Id, isTrustedListener = listener.IsTrustedListener });
    }

    public sealed class UpdateListenerTrustRequest
    {
        public bool IsTrustedListener { get; set; }
    }

    private static async Task<List<object>> ReadListenerSummariesAsync(
        AllstarrDbContext context, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-7);
        var users = await context.Users.AsNoTracking()
            .Where(item => item.Enabled)
            .OrderBy(item => item.DisplayName)
            .Select(item => new
            {
                item.Id,
                item.DisplayName,
                item.IsAdmin,
                item.IsTrustedListener,
                item.LastSeenAt,
                playlistEditingAllowed = context.SecretReferences.Any(secret => secret.UserId == item.Id &&
                    secret.RevokedAt == null &&
                    (item.BackendType == "jellyfin" && secret.Purpose == BackendCredentialScope.JellyfinPurpose ||
                     item.BackendType == "subsonic" && secret.Purpose == BackendCredentialScope.SubsonicPurpose))
            })
            .ToListAsync(cancellationToken);
        if (users.Count == 0) return [];

        var activity = (await context.Database.SqlQueryRaw<ListenerActivityRow>(ListenerActivitySql,
                now.UtcTicks, since.UtcTicks).ToListAsync(cancellationToken))
            .ToDictionary(item => item.UserId);
        var recentPlays = (await context.Database.SqlQueryRaw<ListenerPlayRow>(RecentPlaysSql, now.UtcTicks)
                .ToListAsync(cancellationToken))
            .ToLookup(item => item.UserId, item => new
            {
                title = item.Title,
                artist = item.Artist,
                playedAt = item.PlayedTicks is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : (DateTimeOffset?)null
            });

        var summaries = new List<object>(users.Count);
        foreach (var user in users)
        {
            DateTimeOffset? lastActiveAt = user.LastSeenAt != default && user.LastSeenAt <= now
                ? user.LastSeenAt : null;
            var listener = activity.GetValueOrDefault(user.Id);
            if (listener?.LastPlayedTicks is { } ticks && (!lastActiveAt.HasValue || ticks > lastActiveAt.Value.UtcTicks))
                lastActiveAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            summaries.Add(new
            {
                user.Id,
                user.DisplayName,
                isAdministrator = user.IsAdmin,
                isTrustedListener = user.IsTrustedListener,
                lastActiveAt,
                playsThisWeek = listener?.PlaysThisWeek ?? 0,
                user.playlistEditingAllowed,
                recentPlays = recentPlays[user.Id].ToList()
            });
        }

        return summaries;
    }

    // Each listener's own history, newest first through IX_listening_event_scope_time. Times are stored as UTC ticks.
    private const string ListenerHistory = """
        e."OwnerUserId" = u."Id" AND e."Protocol" = u."BackendType" AND e."BackendInstanceId" = u."BackendInstanceId"
            AND coalesce(e."ListenedAt", e."StartedAt") <= {0}
        """;

    // Keep the same completed-or-thirty-second rule used by listening totals.
    private const string CountsAsPlay = """
        ((e."State" = 'Completed' AND e."ListenedAt" IS NOT NULL) OR (e."State" IN ('Abandoned', 'Skipped')
            AND e."StartedAt" IS NOT NULL AND e."PositionTicks" >= 300000000))
            AND (e."SourceKind" IN ('protocol', 'listenbrainz-api', 'import') OR e."ImportProvenance" IS NOT NULL)
        """;

    internal const string ListenerActivitySql = $$"""
        SELECT u."Id" AS "UserId",
            (SELECT max(coalesce(e."ListenedAt", e."StartedAt")) FROM listening_events e
                WHERE {{ListenerHistory}}) AS "LastPlayedTicks",
            (SELECT count(*) FROM listening_events e
                WHERE {{ListenerHistory}} AND coalesce(e."ListenedAt", e."StartedAt") >= {1} AND {{CountsAsPlay}}) AS "PlaysThisWeek"
        FROM users u WHERE u."Enabled" AND NOT u."IsSynthetic"
        """;

    internal const string RecentPlaysSql = $$"""
        SELECT p."OwnerUserId" AS "UserId", p."Title", p."Artist", coalesce(p."ListenedAt", p."StartedAt") AS "PlayedTicks"
        FROM users u JOIN listening_events p ON p."Id" IN (
            SELECT e."Id" FROM listening_events e WHERE {{ListenerHistory}} AND {{CountsAsPlay}}
            ORDER BY coalesce(e."ListenedAt", e."StartedAt") DESC, e."Id" DESC LIMIT 5)
        WHERE u."Enabled" AND NOT u."IsSynthetic"
        ORDER BY p."OwnerUserId", coalesce(p."ListenedAt", p."StartedAt") DESC, p."Id" DESC
        """;

    internal sealed class ListenerActivityRow
    {
        public Guid UserId { get; set; }
        public long? LastPlayedTicks { get; set; }
        public int PlaysThisWeek { get; set; }
    }

    internal sealed class ListenerPlayRow
    {
        public Guid UserId { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public long? PlayedTicks { get; set; }
    }
}
