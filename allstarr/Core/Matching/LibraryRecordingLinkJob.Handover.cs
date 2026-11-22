using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class LibraryRecordingLinkJobHandler
{
    // A song that was deleted from the library and has now been added again: the index still holds the copy that is
    // gone, and everything that pointed at it. The scan just made did not see that copy; once the media server confirms
    // it is gone, the file this add placed (or another copy still there) takes over what pointed at it. False while
    // the new copy has not reached the media server.
    private async Task<bool> ReplaceGoneCopiesAsync(IQueryable<LibraryTrackRecord> accessible, ProtocolExecutionContext viewer,
        string libraryId, Guid recordingId, string? placed, DateTimeOffset scanned, string correlationId, CancellationToken token)
    {
        var copies = await accessible.AsNoTracking().Where(item => item.Protocol == "jellyfin" &&
                item.BackendInstanceId == viewer.BackendInstanceId && item.BackendLibraryId == libraryId &&
                item.CanonicalRecordingId == recordingId)
            .Select(item => new { item.Id, item.BackendItemId, item.IndexedAt }).ToArrayAsync(token);
        var unseen = copies.Where(item => item.IndexedAt < scanned).ToArray();
        if (unseen.Length == 0) return true;
        // A copy already found gone needs no second word from the media server.
        var asked = unseen.Where(item => !LibraryTrackRecord.IsGone(item.IndexedAt)).Select(item => item.BackendItemId).ToArray();
        var missing = asked.Length == 0 ? new HashSet<string>() : await scanners!.Resolve(ProtocolKind.Jellyfin).GoneAsync(viewer, libraryId, asked, token);
        var gone = unseen.Where(item => LibraryTrackRecord.IsGone(item.IndexedAt) || missing.Contains(item.BackendItemId)).ToArray();
        if (gone.Length == 0) return true;
        var replacement = Guid.Empty;
        if (!string.IsNullOrWhiteSpace(placed))
        {
            var (forward, backward) = PlacedSuffixes(placed);
            replacement = (await accessible.AsNoTracking().Where(item => item.Protocol == "jellyfin" &&
                    item.BackendInstanceId == viewer.BackendInstanceId && item.BackendLibraryId == libraryId &&
                    (item.FilePath.EndsWith(forward) || item.FilePath.EndsWith(backward)))
                .Select(item => new { item.Id, item.IndexedAt }).ToArrayAsync(token))
                .Where(item => item.IndexedAt >= scanned).Select(item => item.Id).FirstOrDefault();
        }
        if (replacement == Guid.Empty)
            replacement = copies.Where(item => !gone.Contains(item)).OrderBy(item => item.BackendItemId, StringComparer.Ordinal)
                .Select(item => item.Id).FirstOrDefault();
        return replacement != Guid.Empty && index is LibraryIndexService library &&
            await library.ReplaceGoneAsync(replacement, gone.Select(item => item.Id).ToArray(), correlationId, token);
    }

    // The media server may mount the music folder elsewhere, so the path is compared from the library's folder down.
    private static (string Forward, string Backward) PlacedSuffixes(string placed)
    {
        var forward = "/" + placed.Replace('\\', '/').TrimStart('/');
        return (forward, forward.Replace('/', '\\'));
    }

    // The file this add placed is the song that was added, and so is the copy the add found the library to hold already.
    // The library's own pairing goes by tags and names, and may have put the file under another identity of the same
    // song, one that came from a different playlist or service and was never known to be the same. The file settles
    // it: the two are one song.
    private async Task<CanonicalRecordingIdentity> ClaimLibraryFileAsync(IQueryable<LibraryTrackRecord> accessible,
        ProtocolExecutionContext viewer, string libraryId, string? placed, string? known, DateTimeOffset scanned,
        CanonicalRecordingIdentity recording, ProviderActorContext actor, string correlationId, CancellationToken token)
    {
        var files = accessible.AsNoTracking().Where(item => item.Protocol == "jellyfin" &&
            item.BackendInstanceId == viewer.BackendInstanceId && item.BackendLibraryId == libraryId && item.CanonicalRecordingId != null);
        if (placed != null)
        {
            var (forward, backward) = PlacedSuffixes(placed);
            files = files.Where(item => item.FilePath.EndsWith(forward) || item.FilePath.EndsWith(backward));
        }
        else files = files.Where(item => item.BackendItemId == known);
        // A placed file counts once the scan just made has seen it; before that the path may still name a copy that is gone.
        var paired = (await files.Select(item => new { item.CanonicalRecordingId, item.IndexedAt }).ToArrayAsync(token))
            .Where(item => placed == null || item.IndexedAt >= scanned).Select(item => item.CanonicalRecordingId!.Value).FirstOrDefault();
        if (paired == Guid.Empty || paired == recording.Id) return recording;
        try { return await identities.MergeRecordingsAsync(actor, recording.Id, paired, correlationId, token); }
        // Two songs that MusicBrainz knows apart stay apart, and the add waits for someone to look.
        catch (Exception error) when (error is RecordingMergeConflictException or KeyNotFoundException) { return recording; }
    }

    private async Task<bool> HandoverAsync(Guid recordingId, UserRecord listener, string libraryId,
        DurableJobExecutionContext execution, bool removeKeptCopy, Dictionary<Guid, Dictionary<string, Guid?>> favorites, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var users = await db.Users.AsNoTracking().Where(item => item.Enabled && item.BackendType == "jellyfin" &&
            item.BackendInstanceId == listener.BackendInstanceId).ToArrayAsync(token);
        var recordingAliases = (await db.CanonicalRecordings.AsNoTracking().Where(item => item.Id == recordingId || item.MergedIntoId == recordingId)
            .Select(item => item.Id).ToArrayAsync(token)).ToHashSet();
        var pending = false;
        foreach (var user in users)
        {
            var access = libraryAccess == null ? null : await libraryAccess.ResolveUserAsync(user.Id, token);
            var tracks = access == null ? db.LibraryTracks.Where(item => item.OwnerUserId == user.Id) : LibraryTrackAccess.Query(db, access);
            var track = await tracks.AsNoTracking().Where(item => item.Protocol == "jellyfin" &&
                item.BackendInstanceId == listener.BackendInstanceId && item.BackendLibraryId == libraryId &&
                item.CanonicalRecordingId == recordingId).OrderBy(item => item.BackendItemId).FirstOrDefaultAsync(token);
            if (track == null) continue;
            var context = LibraryIndexExecution.Create(user, null, execution.Claim.CorrelationId, clock, token);
            if (!favorites.TryGetValue(user.Id, out var sources))
            {
                var ids = (await new FavoriteViewReader(factory).ReadAsync(context, token)).Where(item => item.Resource.ResourceKind == ProviderResourceKind.Track)
                    .Select(item => item.ItemId).ToArray();
                var recordings = ids.Length == 0 ? null : await identities.ResolveRecordingsAsync(context.RequireActor(), ids, token);
                sources = favorites[user.Id] = ids.ToDictionary(id => id, id => recordings?.GetValueOrDefault(id)?.Id, StringComparer.Ordinal);
            }
            var moved = sources.Where(item => item.Value.HasValue && recordingAliases.Contains(item.Value.Value)).Select(item => item.Key).ToArray();
            if (!await MoveFavoritesAsync(db, user, context, recordingId, track.BackendItemId, moved, token)) { pending = true; continue; }
            foreach (var id in moved) sources.Remove(id);
            var aliases = await db.ProviderTrackIdentities.AsNoTracking().Where(item => item.CanonicalRecordingId == recordingId &&
                item.ResourceKind == ProviderResourceKind.Track && (item.Scope == ProviderIdentityScope.Catalog ||
                    db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId && account.Enabled &&
                        (account.OwnerUserId == null || account.OwnerUserId == user.Id))))
                .Select(item => "ext-" + item.ProviderId + "-song-" + item.ExternalId).ToArrayAsync(token);
            var changed = await db.ListeningEvents.Where(item => item.OwnerUserId == user.Id && item.Protocol == "jellyfin" &&
                    item.BackendInstanceId == user.BackendInstanceId &&
                    (item.CanonicalRecordingId == recordingId || item.CanonicalRecordingId == null && aliases.Contains(item.TrackReference)) &&
                    (item.LibraryTrackId != track.Id || item.TrackReference != track.BackendItemId))
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.CanonicalRecordingId, recordingId)
                    .SetProperty(item => item.LibraryTrackId, track.Id).SetProperty(item => item.TrackReference, track.BackendItemId)
                    .SetProperty(item => item.UpdatedAt, clock.UtcNow).SetProperty(item => item.Revision, item => item.Revision + 1), token);
            if (changed > 0)
            {
                db.AuditEvents.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    ActorUserId = user.Id,
                    Category = "library-promotion",
                    Action = "promotion.history-linked",
                    Outcome = "succeeded",
                    CorrelationId = execution.Claim.CorrelationId,
                    DetailsJson = JsonSerializer.Serialize(new { RecordingId = recordingId, LibraryTrackId = track.Id, Count = changed }),
                    CreatedAt = clock.UtcNow
                });
                await db.SaveChangesAsync(token);
            }
            if (removeKeptCopy && kept != null)
                await kept.ReleasePromotedCopyAsync(user.Id, recordingId, libraryId, execution.Claim.JobId, execution.Claim.CorrelationId, token,
                    releaseExplicitKeep: user.Id == listener.Id);
        }
        return !pending;
    }

    private async Task<bool> MoveFavoritesAsync(AllstarrDbContext db, UserRecord user, ProtocolExecutionContext context,
        Guid recordingId, string nativeId, string[] itemIds, CancellationToken token)
    {
        var fingerprint = FavoriteLinkScope.Fingerprint(user);
        var subject = recordingId.ToString("N");
        var pending = await db.AuditEvents.Where(item => item.ActorUserId == user.Id && item.Action == "promotion.favorite-moved" &&
            item.SubjectId == subject && item.Outcome == "pending" && item.DetailsJson.Contains(fingerprint)).FirstOrDefaultAsync(token);
        if (pending == null && itemIds.Length == 0) return true;
        var reader = new FavoriteViewReader(factory);
        var keys = itemIds.Select(FavoriteLinkScope.Key).ToHashSet();
        var current = (await reader.ReadAsync(context, token)).Where(item => keys.Contains(FavoriteLinkScope.Key(item.ItemId))).ToArray();
        if (pending == null && current.Length == 0) return true;
        if (targets == null) return false;
        var target = targets.Resolve("jellyfin");
        var backend = new BackendPlaylistTargetContext(user.BackendInstanceId, user.BackendPrincipalId) { UseConfiguredAuthentication = true };
        var nativeState = await db.FavoriteStates.AsNoTracking().SingleOrDefaultAsync(item => item.OwnerUserId == user.Id &&
            item.Protocol == "jellyfin" && item.BackendInstanceId == user.BackendInstanceId && item.ItemId == nativeId &&
            db.FavoriteEvents.Any(accepted => accepted.Id == item.LastEventId && accepted.BackendPrincipalId == user.BackendPrincipalId), token);
        if (pending == null)
        {
            pending = new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = user.Id,
                Category = "library-promotion",
                Action = "promotion.favorite-moved",
                Outcome = "pending",
                SubjectType = "recording",
                SubjectId = subject,
                CorrelationId = context.CorrelationId,
                CreatedAt = clock.UtcNow,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    RecordingId = recordingId,
                    BackendItemId = nativeId,
                    IdentityFingerprint = fingerprint,
                    NativeFavorite = nativeState?.IsFavorite == true,
                    NativeEventId = nativeState?.LastEventId,
                    Versions = current.ToDictionary(item => item.ItemId, item => item.HandoverVersion)
                })
            };
            db.AuditEvents.Add(pending);
            await db.SaveChangesAsync(token);
        }
        using var saved = JsonDocument.Parse(pending.DetailsJson);
        var versions = JsonSerializer.Deserialize<Dictionary<string, string?>>(saved.RootElement.GetProperty("Versions"))!;
        keys = versions.Keys.Select(FavoriteLinkScope.Key).ToHashSet();
        current = (await reader.ReadAsync(context, token)).Where(item => keys.Contains(FavoriteLinkScope.Key(item.ItemId))).ToArray();
        if (current.Length > 0 && !await target.FavoriteAsync(backend, nativeId, token)) return false;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (await db.Users.Where(item => item.Id == user.Id && item.Enabled && item.BackendType == "jellyfin" &&
                item.BackendInstanceId == user.BackendInstanceId && item.BackendPrincipalId == user.BackendPrincipalId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt), token) != 1) return false;
        current = (await reader.ReadAsync(context, token)).Where(item => keys.Contains(FavoriteLinkScope.Key(item.ItemId))).ToArray();
        nativeState = await db.FavoriteStates.AsNoTracking().SingleOrDefaultAsync(item => item.OwnerUserId == user.Id &&
            item.Protocol == "jellyfin" && item.BackendInstanceId == user.BackendInstanceId && item.ItemId == nativeId &&
            db.FavoriteEvents.Any(accepted => accepted.Id == item.LastEventId && accepted.BackendPrincipalId == user.BackendPrincipalId), token);
        var nativeChanged = (nativeState?.LastEventId.ToString() ?? "") != saved.RootElement.GetProperty("NativeEventId").GetString();
        var revoked = current.Length == 0 && !saved.RootElement.GetProperty("NativeFavorite").GetBoolean() && nativeState?.IsFavorite != true ||
            nativeChanged && nativeState?.IsFavorite == false;
        if (revoked && !await target.UnfavoriteAsync(backend, nativeId, token)) return false;
        var moved = current.Select(item => item.ItemId).ToArray();
        await db.FavoriteStates.Where(item => item.OwnerUserId == user.Id && item.Protocol == "jellyfin" &&
                item.BackendInstanceId == user.BackendInstanceId && moved.Contains(item.ItemId) && item.IsFavorite)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.IsFavorite, false)
                .SetProperty(item => item.UpdatedAt, clock.UtcNow).SetProperty(item => item.Revision, item => item.Revision + 1), token);
        if (current.Length > 0) versions = current.ToDictionary(item => item.ItemId, item => item.HandoverVersion);
        pending.DetailsJson = JsonSerializer.Serialize(new { RecordingId = recordingId, BackendItemId = nativeId, IdentityFingerprint = fingerprint, Versions = versions });
        pending.Outcome = "succeeded";
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }
}
