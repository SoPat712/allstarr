using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed class FavoriteViewReader(IDbContextFactory<AllstarrDbContext> factory)
{
    internal static IEqualityComparer<string> ItemComparer(IReadOnlyList<FavoriteViewItem> view) =>
        ItemComparer(view.Any(item => item.SharedScope));

    internal static IEqualityComparer<string> ItemComparer(bool sharedScope) =>
        sharedScope ? SharedItemComparer.Instance : StringComparer.Ordinal;

    private sealed class SharedItemComparer : IEqualityComparer<string>
    {
        internal static readonly SharedItemComparer Instance = new();
        public bool Equals(string? first, string? second) => first == second || first != null && second != null &&
            FavoriteLinkScope.Key(first) is { } key && key == FavoriteLinkScope.Key(second);
        public int GetHashCode(string value) => FavoriteLinkScope.Key(value)?.GetHashCode() ?? StringComparer.Ordinal.GetHashCode(value);
    }

    public Task<IReadOnlyList<FavoriteViewItem>> ReadForRequestAsync(HttpContext http)
    {
        var execution = http.GetProtocolExecutionContext();
        if (execution == null) return Task.FromResult<IReadOnlyList<FavoriteViewItem>>([]);
        var requestedUsers = new[] { http.Request.RouteValues.GetValueOrDefault("userId")?.ToString(),
            http.Request.Query["UserId"].ToString() };
        if (requestedUsers.Any(user => !string.IsNullOrEmpty(user) && user != execution.VerifiedBackendPrincipalId))
            return Task.FromResult<IReadOnlyList<FavoriteViewItem>>([]);
        if (http.Items[typeof(FavoriteViewReader)] is Task<IReadOnlyList<FavoriteViewItem>> cached) return cached;
        var read = ReadAsync(execution, http.RequestAborted);
        http.Items[typeof(FavoriteViewReader)] = read;
        return read;
    }

    public async Task<IReadOnlyList<FavoriteViewItem>> ReadAsync(ProtocolExecutionContext execution,
        CancellationToken cancellationToken = default)
    {
        if (execution.Principal is not { } principal || execution.Actor?.EffectiveUserId is not { } userId) return [];
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == userId && item.Enabled &&
            item.BackendType == principal.BackendType && item.BackendInstanceId == execution.BackendInstanceId &&
            item.BackendPrincipalId == execution.VerifiedBackendPrincipalId, cancellationToken);
        if (user == null) return [];
        var rows = await (from state in db.Set<FavoriteStateRecord>().AsNoTracking()
                          join accepted in db.Set<FavoriteEventRecord>() on state.LastEventId equals accepted.Id
                          where state.OwnerUserId == userId && state.Protocol == principal.BackendType &&
                                state.BackendInstanceId == execution.BackendInstanceId &&
                                accepted.OwnerUserId == userId && accepted.Protocol == state.Protocol &&
                                accepted.BackendInstanceId == state.BackendInstanceId && accepted.ItemId == state.ItemId &&
                                accepted.BackendPrincipalId == execution.VerifiedBackendPrincipalId &&
                                accepted.Operation == (state.IsFavorite ? FavoriteOperation.Favorite : FavoriteOperation.Unfavorite)
                          orderby accepted.CreatedAt descending, state.Id
                          select new { state.ItemId, state.DisplayMetadataJson, accepted.CreatedAt, state.IsFavorite })
            .ToArrayAsync(cancellationToken);
        var member = await FavoriteLinkScope.MemberAsync(db, user, cancellationToken);
        if (member != null)
        {
            var link = await db.Set<ProviderFavoriteLinkRecord>().AsNoTracking().SingleAsync(item => item.Id == member.LinkId, cancellationToken);
            var shared = await db.Set<LinkedProviderFavoriteStateRecord>().AsNoTracking().Where(item => item.LinkId == member.LinkId).ToArrayAsync(cancellationToken);
            var values = new Dictionary<FavoriteResourceKey, DateTimeOffset>();
            if (link.CompletedAt == null)
                foreach (var original in await FavoriteLinkScope.BaselineAsync(db, member.LinkId, cancellationToken))
                    if (FavoriteLinkScope.Key(original.ItemId) is { } key) values.TryAdd(key, link.CreatedAt);
            foreach (var state in shared)
            {
                var key = new FavoriteResourceKey(state.ProviderId, state.ResourceKind, state.ExternalId);
                if (state.IsFavorite) values[key] = state.EffectiveAt;
                else values.Remove(key);
            }
            return values.OrderByDescending(item => item.Value).ThenBy(item => item.Key.ProviderId, StringComparer.Ordinal)
                .ThenBy(item => item.Key.Kind, StringComparer.Ordinal).ThenBy(item => item.Key.ExternalId, StringComparer.Ordinal).Select(pair =>
                {
                    var own = rows.FirstOrDefault(item => FavoriteLinkScope.Key(item.ItemId) == pair.Key);
                    var id = own?.ItemId ?? pair.Key.ItemId;
                    var resource = FavoriteDisplaySnapshot.Resource(id)!;
                    return new FavoriteViewItem(id, resource, pair.Value, FavoriteDisplaySnapshot.Read(id, own?.DisplayMetadataJson)
                        ?? FavoriteDisplaySnapshot.Placeholder(id, resource.ResourceKind))
                    { SharedScope = true };
                }).ToArray();
        }
        var result = new List<FavoriteViewItem>();
        foreach (var row in rows.Where(item => item.IsFavorite))
        {
            if (FavoriteDisplaySnapshot.Resource(row.ItemId) is not { } resource) continue;
            result.Add(new(row.ItemId, resource, row.CreatedAt, FavoriteDisplaySnapshot.Read(row.ItemId, row.DisplayMetadataJson)
                ?? FavoriteDisplaySnapshot.Placeholder(row.ItemId, resource.ResourceKind)));
        }
        return result;
    }
}
