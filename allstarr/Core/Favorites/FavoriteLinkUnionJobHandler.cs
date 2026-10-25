using System.Data;
using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed record FavoriteLinkUnionPayload(Guid LinkId, long Attempt);

public sealed class FavoriteLinkUnionJobHandler(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock) : IDurableJobHandler
{
    public const string Type = "favorite.link-union";
    public string JobType => Type;
    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken token)
    {
        FavoriteLinkUnionPayload? payload;
        try { payload = execution.Claim.Payload.Deserialize<FavoriteLinkUnionPayload>(); }
        catch (JsonException) { payload = null; }
        var claim = execution.Claim;
        if (payload == null || payload.LinkId == Guid.Empty || payload.Attempt < 1 || claim.OwnerUserId == null || claim.Type != Type)
            return DurableJobCompletion.Failure("favorite_link_union_invalid", "The shared provider hearts union request is invalid.");
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var link = await db.Set<ProviderFavoriteLinkRecord>().SingleOrDefaultAsync(item => item.Id == payload.LinkId, token);
        if (link == null || link.UnionJobId != claim.JobId || link.UnionAttempt != payload.Attempt) return DurableJobCompletion.Cancelled();
        if (link.CompletedAt.HasValue) return DurableJobCompletion.Success();
        var admin = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == claim.OwnerUserId && item.Enabled && item.IsAdmin, token);
        if (admin == null || FavoriteLinkScope.Fingerprint(admin) != link.UnionAdministratorFingerprint)
            return DurableJobCompletion.Failure("favorite_link_union_administrator", "The union administrator is no longer authorized. An administrator can retry it.");
        if (await db.Users.Where(item => item.Id == admin.Id && item.Enabled && item.IsAdmin &&
                item.BackendType == admin.BackendType && item.BackendInstanceId == admin.BackendInstanceId && item.BackendPrincipalId == admin.BackendPrincipalId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt), token) != 1)
            return DurableJobCompletion.Cancelled();
        if (await db.Jobs.Where(item => item.Id == claim.JobId && item.Type == Type && item.OwnerUserId == claim.OwnerUserId &&
                item.State == DurableJobState.Running && item.AttemptCount == claim.AttemptNumber && item.CancellationRequestedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Revision, item => item.Revision), token) != 1)
            return DurableJobCompletion.Cancelled();
        var protocol = link.Phase == 0 ? "jellyfin" : "subsonic";
        var member = await db.Set<ProviderFavoriteLinkMemberRecord>().AsNoTracking().SingleOrDefaultAsync(item => item.LinkId == link.Id && item.Protocol == protocol, token);
        if (member == null) return DurableJobCompletion.Failure("favorite_link_union_members", "The confirmed union membership is unavailable.");
        var source = FavoriteLinkScope.Legacy(db, member);
        var query = source;
        if (link.CursorAt.HasValue) query = query.Where(item => item.CreatedAt > link.CursorAt || item.CreatedAt == link.CursorAt && item.Id.CompareTo(link.CursorId!.Value) > 0);
        var page = await query.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).Take(100).ToArrayAsync(token);
        foreach (var candidate in page)
        {
            var latest = await source.Where(item => item.ItemId == candidate.ItemId).OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
                .Select(item => item.Id).FirstAsync(token);
            if (candidate.Id != latest || candidate.Operation != FavoriteOperation.Favorite || FavoriteLinkScope.Key(candidate.ItemId) is not { } key) continue;
            link.SeededItems += await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO linked_provider_favorite_states ("LinkId","ProviderId","ResourceKind","ExternalId","IsFavorite","LifecycleVersion","Revision","EffectiveAt","LastIntentEventId")
                VALUES ({link.Id},{key.ProviderId},{key.Kind},{key.ExternalId},1,0,1,{link.CreatedAt.UtcTicks},NULL)
                ON CONFLICT ("LinkId","ProviderId","ResourceKind","ExternalId") DO NOTHING
                """, token);
        }
        link.ProcessedRows += page.Length;
        if (page.Length < 100) { link.Phase++; link.CursorAt = null; link.CursorId = null; }
        else { link.CursorAt = page[^1].CreatedAt; link.CursorId = page[^1].Id; }
        if (link.Phase == 2) link.CompletedAt = clock.UtcNow;
        link.Revision++;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return link.CompletedAt.HasValue ? DurableJobCompletion.Success() :
            DurableJobCompletion.Defer("favorite_link_union_continue", "The shared provider hearts union will continue from its checkpoint.", TimeSpan.FromMilliseconds(100));
    }
}
