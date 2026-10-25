using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Storage;
using allstarr.Core.Providers.AppleMusicKit;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

internal sealed record FavoriteResourceKey(string ProviderId, string Kind, string ExternalId)
{
    public string ItemId => $"ext-{ProviderId}-{(Kind == "track" ? "song" : Kind)}-{ExternalId}";
}

internal static class FavoriteLinkScope
{
    internal static string Fingerprint(UserRecord user) => Hash("member-v1", user.Id.ToString("N"),
        user.BackendType.ToLowerInvariant(), user.BackendInstanceId, user.BackendPrincipalId);

    internal static string Hash(params string[] values) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Concat(values.Select(value => $"{Encoding.UTF8.GetByteCount(value)}:{value}")))));

    internal static FavoriteResourceKey? Key(string itemId)
    {
        var resource = FavoriteDisplaySnapshot.Resource(itemId);
        return resource == null ? null : new(AppleMusicClient.NormalizeProviderId(resource.ProviderId),
            resource.ResourceKind.ToString().ToLowerInvariant(), resource.Value);
    }

    internal static async Task<ProviderFavoriteLinkMemberRecord?> MemberAsync(AllstarrDbContext db, UserRecord user, CancellationToken token)
    {
        if (!user.Enabled || user.IsSynthetic) return null;
        var member = await db.Set<ProviderFavoriteLinkMemberRecord>().AsNoTracking().SingleOrDefaultAsync(item => item.UserId == user.Id, token);
        return member?.IdentityFingerprint == Fingerprint(user) ? member : null;
    }

    internal static IQueryable<FavoriteEventRecord> Legacy(AllstarrDbContext db, ProviderFavoriteLinkMemberRecord member)
    {
        var query = db.FavoriteEvents.AsNoTracking().Where(item => item.OwnerUserId == member.UserId &&
            item.Protocol == member.Protocol && item.BackendInstanceId == member.BackendInstanceId &&
            item.BackendPrincipalId == member.BackendPrincipalId && item.IsRealProviderIntent && item.SharedLinkId == null);
        return member.UpperAt == null ? query.Where(_ => false) : query.Where(item => item.CreatedAt < member.UpperAt ||
            item.CreatedAt == member.UpperAt && item.Id.CompareTo(member.UpperId!.Value) <= 0);
    }

    internal static bool WithinBound(FavoriteEventRecord item, ProviderFavoriteLinkMemberRecord member) =>
        item.OwnerUserId == member.UserId && item.Protocol == member.Protocol && item.BackendInstanceId == member.BackendInstanceId &&
        item.BackendPrincipalId == member.BackendPrincipalId && member.UpperAt.HasValue &&
        (item.CreatedAt < member.UpperAt || item.CreatedAt == member.UpperAt && item.Id.CompareTo(member.UpperId!.Value) <= 0);

    internal static IQueryable<LinkedProviderFavoriteStateRecord> StateQuery(AllstarrDbContext db, Guid linkId, FavoriteResourceKey key) =>
        db.Set<LinkedProviderFavoriteStateRecord>().Where(item => item.LinkId == linkId && item.ProviderId == key.ProviderId &&
            item.ResourceKind == key.Kind && item.ExternalId == key.ExternalId);

    internal static LinkedProviderFavoriteStateRecord NewState(Guid linkId, FavoriteResourceKey key, bool favorite, DateTimeOffset at) =>
        new()
        {
            LinkId = linkId,
            ProviderId = key.ProviderId,
            ResourceKind = key.Kind,
            ExternalId = key.ExternalId,
            IsFavorite = favorite,
            LifecycleVersion = 0,
            Revision = 1,
            EffectiveAt = at
        };

    internal static async Task<bool> ProveAsync(AllstarrDbContext db, FavoriteEventRecord favorite, bool writeFence,
        bool allowSynthetic, CancellationToken token)
    {
        var key = Key(favorite.ItemId);
        if (key == null) return true;
        var userQuery = db.Users.Where(user => user.Id == favorite.OwnerUserId && user.Enabled &&
            user.BackendType == favorite.Protocol && user.BackendInstanceId == favorite.BackendInstanceId &&
            user.BackendPrincipalId == favorite.BackendPrincipalId);
        var user = await userQuery.AsNoTracking().SingleOrDefaultAsync(token);
        if (user == null) return false;
        if (writeFence)
        {
            if (db.Database.CurrentTransaction == null) throw new InvalidOperationException("Favorite publication requires its write transaction.");
            if (await userQuery.Where(item => item.IsSynthetic == user.IsSynthetic)
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt), token) != 1) return false;
        }
        if (user.IsSynthetic)
        {
            if (!allowSynthetic || favorite.SharedLinkId != null) return false;
            var frozen = await db.Set<ProviderFavoriteLinkMemberRecord>().AsNoTracking().SingleOrDefaultAsync(item => item.UserId == user.Id, token);
            return !favorite.IsRealProviderIntent || frozen == null || frozen.IdentityFingerprint != Fingerprint(user) || !WithinBound(favorite, frozen);
        }
        var member = await MemberAsync(db, user, token);
        if (member == null) return favorite.SharedLinkId == null;
        var legacy = favorite.SharedLinkId == null && favorite.IsRealProviderIntent && WithinBound(favorite, member);
        if (!legacy && (favorite.SharedLinkId != member.LinkId || !favorite.SharedLifecycleVersion.HasValue)) return false;
        var own = await db.FavoriteStates.AsNoTracking().SingleOrDefaultAsync(item => item.OwnerUserId == user.Id &&
            item.Protocol == favorite.Protocol && item.BackendInstanceId == favorite.BackendInstanceId && item.ItemId == favorite.ItemId, token);
        var expected = favorite.Operation == FavoriteOperation.Favorite;
        if (own == null || own.LastEventId != favorite.Id || own.IsFavorite != expected) return false;
        var query = StateQuery(db, member.LinkId, key);
        var state = db.Set<LinkedProviderFavoriteStateRecord>().Local.FirstOrDefault(item => item.LinkId == member.LinkId && item.ProviderId == key.ProviderId && item.ResourceKind == key.Kind && item.ExternalId == key.ExternalId);
        if (state == null || db.Entry(state).State != EntityState.Added)
        {
            var fresh = await query.AsNoTracking().SingleOrDefaultAsync(token);
            if (writeFence && state != null && fresh != null)
            {
                db.Entry(state).CurrentValues.SetValues(fresh);
                db.Entry(state).OriginalValues.SetValues(fresh);
            }
            else state = fresh;
            if (writeFence && state != null && db.Entry(state).State == EntityState.Detached) db.Attach(state);
        }
        if (state == null)
        {
            if (!legacy || !expected) return false;
            if (!writeFence) return true;
            var linkedAt = await db.Set<ProviderFavoriteLinkRecord>().Where(item => item.Id == member.LinkId).Select(item => item.CreatedAt).SingleAsync(token);
            state = NewState(member.LinkId, key, true, linkedAt);
            db.Add(state);
        }
        if (state.IsFavorite != expected || state.LifecycleVersion != (legacy ? 0 : favorite.SharedLifecycleVersion)) return false;
        if (writeFence && db.Entry(state).State != EntityState.Added) db.Entry(state).Property(item => item.Revision).IsModified = true;
        return true;
    }

    internal static async Task<IReadOnlyList<FavoriteEventRecord>> BaselineAsync(AllstarrDbContext db, Guid linkId, CancellationToken token)
    {
        var members = await db.Set<ProviderFavoriteLinkMemberRecord>().AsNoTracking().Where(item => item.LinkId == linkId).ToArrayAsync(token);
        var result = new List<FavoriteEventRecord>();
        foreach (var member in members)
        {
            var source = Legacy(db, member);
            var latest = source.Where(item => item.Operation == FavoriteOperation.Favorite && !source.Any(newer => newer.ItemId == item.ItemId &&
                (newer.CreatedAt > item.CreatedAt || newer.CreatedAt == item.CreatedAt && newer.Id.CompareTo(item.Id) > 0)));
            Guid? cursor = null;
            while (true)
            {
                var query = latest;
                if (cursor.HasValue) query = query.Where(item => item.Id.CompareTo(cursor.Value) > 0);
                var page = await query.OrderBy(item => item.Id).Select(item => new FavoriteEventRecord
                { Id = item.Id, ItemId = item.ItemId, CreatedAt = item.CreatedAt }).Take(128).ToArrayAsync(token);
                result.AddRange(page.Where(item => Key(item.ItemId) != null));
                if (page.Length < 128) break;
                cursor = page[^1].Id;
            }
        }
        return result;
    }

    internal static async Task<IReadOnlyList<FavoriteStateRecord>> TargetStatesAsync(AllstarrDbContext db, Guid linkId,
        FavoriteResourceKey key, CancellationToken token)
    {
        var members = await db.Set<ProviderFavoriteLinkMemberRecord>().AsNoTracking().Where(item => item.LinkId == linkId).ToArrayAsync(token);
        if (members.Length != 2) return [];
        var first = members[0];
        var second = members[1];
        const string whitespace = " \t\r\n\v\f\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000";
        var query = db.FavoriteStates.FromSqlInterpolated($"""
            SELECT * FROM favorite_states WHERE
              (("OwnerUserId"={first.UserId} AND "Protocol"={first.Protocol} AND "BackendInstanceId"={first.BackendInstanceId}) OR
               ("OwnerUserId"={second.UserId} AND "Protocol"={second.Protocol} AND "BackendInstanceId"={second.BackendInstanceId}))
              AND substr(rtrim("ItemId",{whitespace}),-length({key.ExternalId}))={key.ExternalId} COLLATE BINARY
            """);
        var result = new List<FavoriteStateRecord>();
        Guid? cursor = null;
        while (true)
        {
            var remaining = query;
            if (cursor.HasValue) remaining = remaining.Where(item => item.Id.CompareTo(cursor.Value) > 0);
            var page = await remaining.OrderBy(item => item.Id).Select(item => new FavoriteStateRecord
            { Id = item.Id, OwnerUserId = item.OwnerUserId, Protocol = item.Protocol, BackendInstanceId = item.BackendInstanceId, ItemId = item.ItemId, LastEventId = item.LastEventId }).Take(128).ToArrayAsync(token);
            foreach (var state in page.Where(item => Key(item.ItemId) == key))
            {
                var member = members.Single(item => item.UserId == state.OwnerUserId);
                if (state.Protocol != member.Protocol || state.BackendInstanceId != member.BackendInstanceId) continue;
                if (!await db.FavoriteEvents.AnyAsync(item => item.Id == state.LastEventId && item.OwnerUserId == member.UserId &&
                        item.Protocol == member.Protocol && item.BackendInstanceId == member.BackendInstanceId &&
                        item.BackendPrincipalId == member.BackendPrincipalId && item.ItemId == state.ItemId, token)) continue;
                if (await db.FavoriteEvents.AnyAsync(item => item.OwnerUserId == member.UserId && item.Protocol == member.Protocol &&
                        item.BackendInstanceId == member.BackendInstanceId && item.BackendPrincipalId == member.BackendPrincipalId &&
                        item.ItemId == state.ItemId && item.IsRealProviderIntent && (item.SharedLinkId == linkId ||
                            item.SharedLinkId == null && member.UpperAt != null && (item.CreatedAt < member.UpperAt ||
                                item.CreatedAt == member.UpperAt && item.Id.CompareTo(member.UpperId!.Value) <= 0)), token)) result.Add(state);
            }
            if (page.Length < 128) break;
            cursor = page[^1].Id;
        }
        return result;
    }
}
