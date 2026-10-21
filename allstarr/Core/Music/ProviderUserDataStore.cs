using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Intelligence;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed record ProviderItemUserData(int Rating = 0, int PlayCount = 0,
    DateTimeOffset? LastPlayedDate = null, long PlaybackPositionTicks = 0)
{
    public bool Played => PlayCount > 0;
}

public sealed class ProviderUserDataStore(IDbContextFactory<AllstarrDbContext> factory)
{
    public static bool IsProviderItem(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 500 &&
        FavoriteDisplaySnapshot.Resource(id)?.ResourceKind is
            ProviderResourceKind.Track or ProviderResourceKind.Album or ProviderResourceKind.Artist;

    public Task<IReadOnlyDictionary<string, ProviderItemUserData>> ReadForRequestAsync(
        HttpContext http, IEnumerable<string> ids)
    {
        var context = http.GetProtocolExecutionContext();
        if (context == null ||
            http.Request.RouteValues.TryGetValue("userId", out var routeUser) &&
            !string.Equals(routeUser?.ToString(), context.VerifiedBackendPrincipalId, StringComparison.Ordinal) ||
            http.Request.Query.TryGetValue("UserId", out var queryUsers) &&
            queryUsers.Any(user => !string.Equals(user, context.VerifiedBackendPrincipalId, StringComparison.Ordinal)))
            return Task.FromResult<IReadOnlyDictionary<string, ProviderItemUserData>>(
                new Dictionary<string, ProviderItemUserData>(StringComparer.Ordinal));
        return ReadAsync(context, ids, http.RequestAborted);
    }

    public async Task<IReadOnlyDictionary<string, ProviderItemUserData>> ReadAsync(
        ProtocolExecutionContext context, IEnumerable<string> ids, CancellationToken token = default)
    {
        var result = new Dictionary<string, ProviderItemUserData>(StringComparer.Ordinal);
        var requested = ids.Where(IsProviderItem).Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length == 0) return result;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, token);
        token = cancellation.Token;
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await IsOwnerAsync(db, context, token)) return result;
        var owner = context.Actor!.UserId!.Value;
        var protocol = context.Protocol.ToString().ToLowerInvariant();
        foreach (var batch in requested.Chunk(400))
        {
            var states = await db.ProviderUserData.AsNoTracking()
                .Where(item => item.OwnerUserId == owner && item.Protocol == protocol &&
                    item.BackendInstanceId == context.BackendInstanceId && batch.Contains(item.ItemId))
                .ToDictionaryAsync(item => item.ItemId, StringComparer.Ordinal, token);
            var events = await db.ListeningEvents.AsNoTracking()
                .Where(item => item.OwnerUserId == owner && item.Protocol == protocol &&
                    item.BackendInstanceId == context.BackendInstanceId && batch.Contains(item.TrackReference))
                .Select(item => new
                {
                    item.Id,
                    item.TrackReference,
                    item.State,
                    item.ListenedAt,
                    item.UpdatedAt,
                    item.ResumePositionTicks
                }).ToListAsync(token);
            var history = events.ToLookup(item => item.TrackReference, StringComparer.Ordinal);
            foreach (var id in batch)
            {
                states.TryGetValue(id, out var state);
                var itemEvents = history[id].ToArray();
                var completed = itemEvents.Where(item => item.State == ListeningEventState.Completed).ToArray();
                var counted = state?.Played != null && state.HistoryResetAt is { } reset
                    ? completed.Where(item => item.ListenedAt > reset).ToArray() : completed;
                var laterCount = counted.Length;
                var playCount = laterCount + (state?.Played == true ? 1 : 0);
                var lastPlayed = state?.Played != null ? state.PlayedAt : null;
                if (laterCount > 0)
                {
                    var latestCompletion = counted.Select(item => item.ListenedAt).Max();
                    if (lastPlayed == null || latestCompletion > lastPlayed) lastPlayed = latestCompletion;
                }
                var latest = itemEvents.OrderByDescending(item => item.UpdatedAt).ThenByDescending(item => item.Id).FirstOrDefault();
                var resume = latest?.State == ListeningEventState.Completed ||
                    state?.HistoryResetAt is { } resumeReset && latest?.UpdatedAt <= resumeReset
                    ? 0 : Math.Max(0, latest?.ResumePositionTicks ?? 0);
                result[id] = new(state?.Rating ?? 0, playCount, lastPlayed, resume);
            }
        }
        return result;
    }

    public Task<bool> SetRatingAsync(ProtocolExecutionContext context, string itemId, int rating,
        CancellationToken token = default) => rating is < 0 or > 5
        ? Task.FromResult(false)
        : MutateAsync(context, itemId, rating, null, default, token);

    public Task<bool> SetPlayedAsync(ProtocolExecutionContext context, string itemId, bool played,
        DateTimeOffset playedAt, CancellationToken token = default) =>
        MutateAsync(context, itemId, null, played, playedAt, token);

    private async Task<bool> MutateAsync(ProtocolExecutionContext context, string itemId, int? rating,
        bool? played, DateTimeOffset playedAt, CancellationToken token)
    {
        if (!IsProviderItem(itemId) || context.IsReadOnly) return false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, token);
        token = cancellation.Token;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(token);
            if (!await IsOwnerAsync(db, context, token)) return false;
            var owner = context.Actor!.UserId!.Value;
            var protocol = context.Protocol.ToString().ToLowerInvariant();
            var state = await db.ProviderUserData.SingleOrDefaultAsync(item =>
                item.OwnerUserId == owner && item.Protocol == protocol &&
                item.BackendInstanceId == context.BackendInstanceId && item.ItemId == itemId, token);
            var inserted = state == null;
            if (state == null)
            {
                state = new()
                {
                    OwnerUserId = owner,
                    Protocol = protocol,
                    BackendInstanceId = context.BackendInstanceId,
                    ItemId = itemId
                };
                db.ProviderUserData.Add(state);
            }
            if (rating.HasValue) state.Rating = rating;
            if (played.HasValue)
            {
                var completed = await db.ListeningEvents.AsNoTracking().Where(item =>
                    item.OwnerUserId == owner && item.Protocol == protocol &&
                    item.BackendInstanceId == context.BackendInstanceId && item.TrackReference == itemId &&
                    item.State == ListeningEventState.Completed).Select(item => item.ListenedAt).ToArrayAsync(token);
                if (state.Played != played || completed.Any(at => at > state.HistoryResetAt))
                {
                    state.Played = played;
                    state.HistoryResetAt = DateTimeOffset.UtcNow;
                    state.PlayedAt = played.Value ? playedAt : null;
                }
            }
            try
            {
                await db.SaveChangesAsync(token);
                return true;
            }
            catch (DbUpdateException error) when (attempt == 0 && inserted &&
                error.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 })
            {
                // Reload the concurrent insert before applying this operation's fields.
            }
        }
        return false;
    }

    private static Task<bool> IsOwnerAsync(AllstarrDbContext db, ProtocolExecutionContext context,
        CancellationToken token)
    {
        var actor = context.Actor;
        var principal = context.Principal;
        var protocol = context.Protocol.ToString().ToLowerInvariant();
        if (actor?.UserId is not { } owner || principal == null || principal.UserId != owner ||
            actor.EffectiveUserId != owner || actor.BackendPrincipal is not { } backend ||
            backend.BackendType != protocol || backend.BackendInstanceId != context.BackendInstanceId ||
            backend.PrincipalId != context.VerifiedBackendPrincipalId || principal.BackendType != protocol ||
            principal.BackendInstanceId != context.BackendInstanceId ||
            principal.BackendPrincipalId != context.VerifiedBackendPrincipalId)
            return Task.FromResult(false);
        return db.Users.AsNoTracking().AnyAsync(item => item.Id == owner && item.Enabled &&
            item.BackendType == protocol && item.BackendInstanceId == context.BackendInstanceId &&
            item.BackendPrincipalId == context.VerifiedBackendPrincipalId, token);
    }
}
