using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
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

        var history = context.ListeningEvents.AsNoTracking().Where(item => context.Users.Any(user =>
            user.Id == item.OwnerUserId && user.Enabled && !user.IsSynthetic &&
            user.BackendType == item.Protocol && user.BackendInstanceId == item.BackendInstanceId));
        var activity = await history
            .Where(item => (item.ListenedAt ?? item.StartedAt) <= now)
            .GroupBy(item => item.OwnerUserId)
            .Select(group => new { UserId = group.Key, At = group.Max(item => item.ListenedAt ?? item.StartedAt) })
            .ToDictionaryAsync(item => item.UserId, item => item.At, cancellationToken);

        // Keep the same completed-or-thirty-second rule used by listening totals.
        var plays = history.Where(item =>
            (item.State == ListeningEventState.Completed ? item.ListenedAt != null :
                (item.State == ListeningEventState.Abandoned || item.State == ListeningEventState.Skipped) &&
                item.StartedAt != null && item.PositionTicks >= 30 * TimeSpan.TicksPerSecond) &&
            (item.SourceKind == "protocol" || item.SourceKind == "listenbrainz-api" ||
             item.SourceKind == "import" || item.ImportProvenance != null) &&
            (item.ListenedAt ?? item.StartedAt) <= now);
        var weeklyCounts = await plays.Where(item => (item.ListenedAt ?? item.StartedAt) >= since)
            .GroupBy(item => item.OwnerUserId)
            .Select(group => new { UserId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.UserId, item => item.Count, cancellationToken);

        var summaries = new List<object>(users.Count);
        foreach (var user in users)
        {
            DateTimeOffset? lastActiveAt = user.LastSeenAt != default && user.LastSeenAt <= now
                ? user.LastSeenAt : null;
            if (activity.GetValueOrDefault(user.Id) is { } playedAt &&
                (!lastActiveAt.HasValue || playedAt > lastActiveAt.Value))
                lastActiveAt = playedAt;
            var recentPlays = await plays.Where(item => item.OwnerUserId == user.Id)
                .OrderByDescending(item => item.ListenedAt ?? item.StartedAt)
                .ThenByDescending(item => item.Id)
                .Take(5)
                .Select(item => new
                {
                    title = item.Title,
                    artist = item.Artist,
                    playedAt = item.ListenedAt ?? item.StartedAt
                })
                .ToListAsync(cancellationToken);
            summaries.Add(new
            {
                user.Id,
                user.DisplayName,
                isAdministrator = user.IsAdmin,
                isTrustedListener = user.IsTrustedListener,
                lastActiveAt,
                playsThisWeek = weeklyCounts.GetValueOrDefault(user.Id),
                user.playlistEditingAllowed,
                recentPlays
            });
        }

        return summaries;
    }
}
