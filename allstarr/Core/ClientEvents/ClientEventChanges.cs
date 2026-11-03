using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Favorites;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientEvents;

public interface IClientEventChanges
{
    Task LyricsAsync(Guid recordingId, Guid? owner, bool automatic = false);
    Task MatchAsync(Guid snapshotId, Guid? owner, bool automatic = false);
    Task KeepAsync(Guid owner, string itemId, double? progress = null);
    Task KeptAsync(Guid recordingId);
    Task KeepProgressAsync(Guid jobId, double? progress);
}

public sealed class ClientEventChanges(IDbContextFactory<AllstarrDbContext> factory, ClientEventHub hub,
    IServiceScopeFactory scopes, ILogger<ClientEventChanges> logger) : IClientEventChanges
{
    public Task LyricsAsync(Guid recordingId, Guid? owner, bool automatic = false) => SafelyAsync(async () =>
    {
        foreach (var listener in hub.Listeners.Where(id => owner == null || id == owner))
        {
            await using var db = await factory.CreateDbContextAsync();
            var rows = await db.RecordingLyricsChoices.AsNoTracking().Where(item => item.CanonicalRecordingId == recordingId &&
                item.RevokedAt == null && (item.OwnerUserId == listener || item.OwnerUserId == null)).ToArrayAsync();
            var personal = rows.SingleOrDefault(item => item.OwnerUserId == listener);
            if (owner == null && personal != null || automatic && rows.Length != 0) continue;
            var household = rows.SingleOrDefault(item => item.OwnerUserId == null);
            var revision = new ClientLyricsRevision(Stamp(personal), Stamp(household));
            foreach (var itemId in await ItemIdsAsync(db, listener, recordingId)) hub.Publish(listener, ClientEvent.Lyrics(itemId, revision));
        }
    });

    public Task MatchAsync(Guid snapshotId, Guid? owner, bool automatic = false) => SafelyAsync(async () =>
    {
        await using var db = await factory.CreateDbContextAsync();
        var source = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.Id == snapshotId);
        foreach (var listener in hub.Listeners.Where(id => owner == null || id == owner))
        {
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == listener && item.Enabled && item.BackendType == "jellyfin");
            if (user == null) continue;
            var snapshot = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == listener &&
                item.Protocol == user.BackendType && item.BackendInstanceId == user.BackendInstanceId && item.BackendPrincipalId == user.BackendPrincipalId &&
                item.ProviderId == source.ProviderId && item.ExternalIdHash == source.ExternalIdHash && item.ResourceKind == "track")
                .OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion).ThenByDescending(item => item.Id).FirstOrDefaultAsync();
            if (snapshot == null) continue;
            var layers = await ManualTrackOverrides.ReadAsync(db, snapshot, listener, default);
            if (owner == null && layers.Personal != null) continue;
            var decision = await db.TrackMatches.AsNoTracking().Where(item => item.OwnerUserId == listener && item.ExternalSnapshotId == snapshot.Id)
                .OrderByDescending(item => item.DecisionVersion).FirstOrDefaultAsync();
            var sourceIdentity = await db.ProviderTrackIdentities.AsNoTracking().FirstOrDefaultAsync(item => item.ProviderId == snapshot.ProviderId &&
                item.ExternalIdHash == snapshot.ExternalIdHash && (item.ProviderAccountId == null || db.ProviderAccounts.Any(account =>
                    account.Id == item.ProviderAccountId && (account.OwnerUserId == null || account.OwnerUserId == listener))));
            var recording = decision?.CanonicalRecordingId ?? sourceIdentity?.CanonicalRecordingId;
            var identities = recording == null ? [] : await db.ProviderTrackIdentities.AsNoTracking().Where(item => item.CanonicalRecordingId == recording &&
                (item.ProviderAccountId == null || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId &&
                    (account.OwnerUserId == null || account.OwnerUserId == listener)))).ToArrayAsync();
            await using var scope = scopes.CreateAsyncScope();
            var access = await scope.ServiceProvider.GetRequiredService<IBackendLibraryAccessResolver>().ResolveUserAsync(listener);
            var localIds = new[] { decision?.LibraryTrackId, layers.Effective?.LibraryTrackId }.OfType<Guid>().ToArray();
            var tracks = await LibraryTrackAccess.Query(db, access).Where(item => localIds.Contains(item.Id) ||
                recording != null && item.CanonicalRecordingId == recording).ToArrayAsync();
            var actor = new ProviderActorContext(ProviderActorKind.User, listener, new(user.BackendType, user.BackendInstanceId, user.BackendPrincipalId));
            var policy = await scope.ServiceProvider.GetRequiredService<IEffectiveProviderPolicyResolver>().ResolveForUserAsync(listener);
            var order = await scope.ServiceProvider.GetRequiredService<IProtocolProviderGateway>().GetPlayableProviderOrderAsync(actor);
            var projection = TrackRouteProjector.Project(snapshot, decision, layers.Effective, sourceIdentity, tracks, identities, order, policy);
            if (automatic && layers.Effective != null && projection.ReviewState is TrackMatchState.Pinned or TrackMatchState.Rejected) continue;
            var state = projection.ReviewState.ToString().ToLowerInvariant();
            HashSet<string> ids = [];
            if (sourceIdentity != null) ids.Add($"ext-{source.ProviderId}-song-{sourceIdentity.ExternalId}");
            if (recording is { } id)
            {
                ids.Add($"ext-allstarr-song-{id:D}");
                foreach (var track in tracks.Where(item => item.CanonicalRecordingId == id)) ids.Add(track.BackendItemId);
            }
            foreach (var itemId in ids) hub.Publish(listener, ClientEvent.Match(itemId, state));
        }
    });

    public Task KeepAsync(Guid owner, string itemId, double? progress = null) => SafelyAsync(async () =>
    {
        if (!hub.Listeners.Contains(owner)) return;
        await using var db = await factory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == owner && item.Enabled && item.BackendType == "jellyfin");
        if (user == null) return;
        var state = await (from candidate in db.FavoriteStates.AsNoTracking()
                           join accepted in db.FavoriteEvents.AsNoTracking() on candidate.LastEventId equals accepted.Id
                           where candidate.OwnerUserId == owner && candidate.ItemId == itemId && candidate.Protocol == user.BackendType &&
                               candidate.BackendInstanceId == user.BackendInstanceId && accepted.OwnerUserId == owner && accepted.ItemId == itemId &&
                               accepted.Protocol == user.BackendType && accepted.BackendInstanceId == user.BackendInstanceId &&
                               accepted.BackendPrincipalId == user.BackendPrincipalId &&
                               accepted.Operation == (candidate.IsFavorite ? FavoriteOperation.Favorite : FavoriteOperation.Unfavorite)
                           select candidate).SingleOrDefaultAsync();
        var value = "none";
        if (state?.IsFavorite == true)
        {
            var reference = $"heart:{state.Id:N}";
            var files = await (from kept in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                               join file in db.ManagedFiles.AsNoTracking() on kept.ManagedFileId equals file.Id
                               where file.IsManaged && file.RemovedAt == null && file.OwnerUserId == null && file.SourceJobId == null && file.Length > 0 &&
                                   db.ManagedFileReferences.Any(item => item.ManagedFileId == file.Id && item.ReferenceKey == reference && item.ReleasedAt == null)
                               select file.CanonicalPath).ToArrayAsync();
            if (files.Any(File.Exists)) value = "kept";
            else
            {
                var action = await db.FavoriteActions.AsNoTracking().SingleOrDefaultAsync(item => item.EventId == state.LastEventId && item.ActionType == "download");
                value = action?.State switch { FavoriteActionState.Pending or FavoriteActionState.Running => "inProgress", FavoriteActionState.Failed => "failed", _ => "none" };
            }
        }
        hub.Publish(owner, ClientEvent.Keep(itemId, value, value == "inProgress" ? progress : null));
    });

    public Task KeepProgressAsync(Guid jobId, double? progress) => SafelyAsync(async () =>
    {
        await using var db = await factory.CreateDbContextAsync();
        var favorite = await db.FavoriteEvents.AsNoTracking().SingleOrDefaultAsync(item => item.JobId == jobId);
        if (favorite != null) await KeepAsync(favorite.OwnerUserId, favorite.ItemId, progress);
    });

    public Task KeptAsync(Guid recordingId) => SafelyAsync(async () =>
    {
        await using var db = await factory.CreateDbContextAsync();
        foreach (var listener in hub.Listeners)
        {
            foreach (var itemId in await ItemIdsAsync(db, listener, recordingId)) await KeepAsync(listener, itemId);
        }
    });

    private async Task<string[]> ItemIdsAsync(AllstarrDbContext db, Guid listener, Guid recordingId)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == listener && item.Enabled && item.BackendType == "jellyfin");
        if (user == null) return [];
        await using var scope = scopes.CreateAsyncScope();
        var actor = new ProviderActorContext(ProviderActorKind.User, listener, new(user.BackendType, user.BackendInstanceId, user.BackendPrincipalId));
        if (await scope.ServiceProvider.GetRequiredService<ITrackIdentityService>().ResolveRecordingAsync(actor, recordingId.ToString("D")) == null) return [];
        var identities = await db.ProviderTrackIdentities.AsNoTracking().Where(item => item.CanonicalRecordingId == recordingId && item.ResourceKind == ProviderResourceKind.Track &&
            (item.ProviderAccountId == null || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId &&
                (account.OwnerUserId == null || account.OwnerUserId == listener)))).ToArrayAsync();
        var access = await scope.ServiceProvider.GetRequiredService<IBackendLibraryAccessResolver>().ResolveUserAsync(listener);
        var native = await LibraryTrackAccess.Query(db, access).Where(item => item.CanonicalRecordingId == recordingId).Select(item => item.BackendItemId).ToArrayAsync();
        return identities.Select(item => $"ext-{item.ProviderId}-song-{item.ExternalId}").Concat(native)
            .Append($"ext-allstarr-song-{recordingId:D}").Distinct().ToArray();
    }

    private static ClientLyricsStamp? Stamp(RecordingLyricsChoiceRecord? row) => row == null ? null : new(row.Id, row.Revision);
    private async Task SafelyAsync(Func<Task> publish)
    {
        if (hub.ConnectionCount == 0) return;
        try { await publish(); }
        catch (Exception exception) { logger.LogWarning(exception, "Could not publish a client update"); }
    }
}
