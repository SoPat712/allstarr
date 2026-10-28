using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed class SavedLibraryReader(IDbContextFactory<AllstarrDbContext> factory,
    IBackendPlaylistTargetResolver? targets = null, IEffectiveProviderPolicyResolver? effectivePolicies = null,
    ITrackIdentityService? identities = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public Task<IReadOnlyList<FavoriteViewItem>> ReadForRequestAsync(HttpContext http)
    {
        var execution = http.GetProtocolExecutionContext();
        if (execution == null || new[] { http.Request.RouteValues.GetValueOrDefault("userId")?.ToString(),
                http.Request.Query["UserId"].ToString() }.Any(user => !string.IsNullOrEmpty(user) && user != execution.VerifiedBackendPrincipalId))
            return Task.FromResult<IReadOnlyList<FavoriteViewItem>>([]);
        if (http.Items[typeof(SavedLibraryReader)] is Task<IReadOnlyList<FavoriteViewItem>> cached) return cached;
        var read = ReadAsync(execution, http.RequestAborted);
        http.Items[typeof(SavedLibraryReader)] = read;
        return read;
    }

    public async Task<IReadOnlyList<FavoriteViewItem>> ReadAsync(ProtocolExecutionContext execution,
        CancellationToken cancellationToken = default)
    {
        if (execution.Principal is not { } principal || execution.Actor?.EffectiveUserId is not { } owner) return [];
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == owner && user.Enabled &&
            user.BackendType == principal.BackendType && user.BackendInstanceId == execution.BackendInstanceId &&
            user.BackendPrincipalId == execution.VerifiedBackendPrincipalId, cancellationToken);
        if (user == null) return [];
        var sharedScope = await FavoriteLinkScope.MemberAsync(db, user, cancellationToken) != null;
        var favorites = await new FavoriteViewReader(factory).ReadAsync(execution, cancellationToken);
        var states = await db.Set<FavoriteStateRecord>().AsNoTracking().Where(item => item.OwnerUserId == owner &&
            item.Protocol == principal.BackendType && item.BackendInstanceId == execution.BackendInstanceId).ToArrayAsync(cancellationToken);
        var displays = states.Select(item => FavoriteDisplaySnapshot.Read(item.ItemId, item.DisplayMetadataJson))
            .OfType<FavoriteDisplaySnapshot>().ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        foreach (var favorite in favorites) displays[favorite.ItemId] = favorite.Display;
        var currentEvents = states.Select(item => item.LastEventId).ToHashSet();
        var changes = new List<MembershipChange>();
        var events = await db.Set<FavoriteEventRecord>().AsNoTracking().Where(item => item.OwnerUserId == owner &&
            item.Protocol == principal.BackendType && item.BackendInstanceId == execution.BackendInstanceId &&
            item.BackendPrincipalId == execution.VerifiedBackendPrincipalId).ToArrayAsync(cancellationToken);
        foreach (var item in events)
        {
            var resource = FavoriteDisplaySnapshot.Resource(item.ItemId);
            if (resource == null || resource.ResourceKind == ProviderResourceKind.Playlist) continue;
            var display = displays.GetValueOrDefault(item.ItemId) ?? FavoriteDisplaySnapshot.Placeholder(item.ItemId, resource.ResourceKind);
            changes.Add(new(item.CreatedAt, (currentEvents.Contains(item.Id) ? "1" : "0") + item.Id, "heart:" + item.ItemId,
                item.Operation == FavoriteOperation.Favorite ? [display] : []));
        }
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item =>
            item.Protocol == principal.BackendType && item.BackendInstanceId == execution.BackendInstanceId &&
            item.ResourceKind == "playlist-membership" &&
            item.ProviderId == "allstarr").ToArrayAsync(cancellationToken);
        var visible = snapshots.Length == 0 ? new HashSet<string>(StringComparer.Ordinal) :
            await VisiblePlaylistKeysAsync(execution, cancellationToken);
        var excludedSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            var owned = snapshot.OwnerUserId == owner && snapshot.BackendPrincipalId == execution.VerifiedBackendPrincipalId;
            var accessible = targets == null ? owned : visible.Contains(snapshot.ExternalIdHash);
            if (!owned && !accessible) continue;
            if (!accessible) excludedSources.Add("playlist:" + snapshot.ExternalIdHash);
            try
            {
                var state = JsonSerializer.Deserialize<WritablePlaylistState>(snapshot.PayloadJson, JsonOptions);
                var members = state?.Members?.Where(member => member.Song != null).Select(member => Display(member.ItemId, member.Song!)).ToArray() ?? [];
                changes.Add(new(snapshot.RetrievedAt, snapshot.SnapshotVersion.ToString("D10"), "playlist:" + snapshot.ExternalIdHash, members));
            }
            catch (JsonException) { }
        }
        await ReadSourceChangesAsync(db, execution, owner, changes, excludedSources, cancellationToken);
        var archives = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == owner &&
            item.Protocol == principal.BackendType && item.BackendInstanceId == execution.BackendInstanceId &&
            item.BackendPrincipalId == execution.VerifiedBackendPrincipalId && item.ProviderId == "allstarr" &&
            item.ResourceKind == "library-membership-history").ToArrayAsync(cancellationToken);
        foreach (var archive in archives)
        {
            try { changes.AddRange(JsonSerializer.Deserialize<MembershipChange[]>(archive.PayloadJson, JsonOptions) ?? []); }
            catch (JsonException) { }
        }
        var sources = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var sourceDisplays = new Dictionary<string, FavoriteDisplaySnapshot[]>(StringComparer.Ordinal);
        var active = new Dictionary<string, int>(StringComparer.Ordinal);
        var dates = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        void Apply(MembershipChange change)
        {
            var members = Expand(change.Items).GroupBy(item => item.ItemId, StringComparer.Ordinal)
                .Select(group => group.First() with { Genres = group.SelectMany(item => item.Genres).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() }).ToArray();
            var current = members.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
            foreach (var display in members) displays[display.ItemId] = display;
            var previous = sources.GetValueOrDefault(change.Source) ?? [];
            foreach (var id in previous.Except(current))
            {
                if (--active[id] == 0) dates.Remove(id);
            }
            foreach (var id in current.Except(previous))
            {
                if (active.GetValueOrDefault(id) == 0) dates[id] = change.At;
                active[id] = active.GetValueOrDefault(id) + 1;
            }
            sources[change.Source] = current;
            sourceDisplays[change.Source] = members;
        }
        foreach (var change in changes.OrderBy(item => item.At).ThenBy(item => item.Order, StringComparer.Ordinal)) Apply(change);
        foreach (var favorite in favorites.Where(item => item.SharedScope))
            Apply(new(favorite.FavoritedAt, "current", "heart:" + favorite.ItemId, [favorite.Display]));
        // Current accepted state is authoritative even when old event rows were retained out of order.
        var hearted = favorites.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
        foreach (var source in sources.Where(pair => pair.Key.StartsWith("heart:", StringComparison.Ordinal) && !hearted.Contains(pair.Key[6..])))
            foreach (var id in source.Value)
                if (--active[id] == 0) dates.Remove(id);
        foreach (var source in sources.Where(pair => excludedSources.Contains(pair.Key)))
            foreach (var id in source.Value)
                if (--active[id] == 0) dates.Remove(id);
        foreach (var group in sourceDisplays.Where(pair => !excludedSources.Contains(pair.Key) &&
                (!pair.Key.StartsWith("heart:", StringComparison.Ordinal) || hearted.Contains(pair.Key[6..])))
            .SelectMany(pair => pair.Value).GroupBy(item => item.ItemId, StringComparer.Ordinal))
        {
            var best = group.OrderByDescending(item => item.Title != FavoriteDisplaySnapshot.Placeholder(item.ItemId,
                FavoriteDisplaySnapshot.Resource(item.ItemId)!.ResourceKind).Title)
                .ThenByDescending(item => item.Upc != null).ThenByDescending(item => item.IsExplicit.HasValue)
                .ThenByDescending(item => item.ArtistIds.Length).First();
            var ratings = group.Where(item => item.IsExplicit.HasValue).Select(item => item.IsExplicit).Distinct().ToArray();
            displays[group.Key] = best with
            {
                IsExplicit = ratings.Length == 1 ? ratings[0] : null,
                Genres = group.SelectMany(item => item.Genres).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }
        var itemComparer = FavoriteViewReader.ItemComparer(sharedScope);
        if (sharedScope)
        {
            foreach (var group in dates.Keys.ToArray().GroupBy(id => id, itemComparer).Where(group => group.Count() > 1))
            {
                var best = group.OrderByDescending(id => displays[id].Title != FavoriteDisplaySnapshot.Placeholder(id,
                    FavoriteDisplaySnapshot.Resource(id)!.ResourceKind).Title)
                    .ThenByDescending(id => displays[id].ArtistIds.Length + displays[id].AlbumArtistIds.Length)
                    .ThenByDescending(id => displays[id].Album != null).ThenBy(id => id, StringComparer.Ordinal).First();
                var added = group.Min(id => dates[id]);
                displays[best] = displays[best] with { Genres = group.SelectMany(id => displays[id].Genres).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
                foreach (var id in group.Where(id => id != best)) dates.Remove(id);
                dates[best] = added;
            }
            var visibleIds = dates.Keys.ToDictionary(id => id, id => id, itemComparer);
            string Visible(string id) => visibleIds.GetValueOrDefault(id) ?? id;
            foreach (var id in dates.Keys)
            {
                var display = displays[id];
                displays[id] = display with
                {
                    AlbumId = display.AlbumId == null ? null : Visible(display.AlbumId),
                    ArtistIds = display.ArtistIds.Select(Visible).ToArray(),
                    AlbumArtistIds = display.AlbumArtistIds.Select(Visible).ToArray()
                };
            }
        }
        var rememberedAlbums = states.Select(item => FavoriteDisplaySnapshot.Read(item.ItemId, item.DisplayMetadataJson))
            .Where(item => item?.Kind == "album").OfType<FavoriteDisplaySnapshot>()
            .ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        foreach (var id in dates.Keys)
        {
            if (displays[id].Kind != "album" || !rememberedAlbums.TryGetValue(id, out var remembered)) continue;
            var current = displays[id];
            displays[id] = current with
            {
                Upc = current.Upc ?? remembered.Upc,
                IsExplicit = current.IsExplicit ?? remembered.IsExplicit,
                Edition = current.Edition ?? remembered.Edition,
                ProviderIds = current.ProviderIds.Count > 0 ? current.ProviderIds : remembered.ProviderIds
            };
        }
        var currentDisplays = dates.Keys.Select(id => displays[id]).ToArray();
        var songCounts = currentDisplays.Where(item => item.Kind == "song" && item.AlbumId != null)
            .GroupBy(item => item.AlbumId!, itemComparer).ToDictionary(group => group.Key, group => group.Count(), itemComparer);
        var albumCounts = currentDisplays.Where(item => item.Kind == "album")
            .SelectMany(item => item.ArtistIds.Concat(item.AlbumArtistIds).Distinct(itemComparer))
            .GroupBy(id => id, itemComparer).ToDictionary(group => group.Key, group => group.Count(), itemComparer);
        foreach (var id in dates.Keys)
        {
            var display = displays[id];
            if (display.Kind == "album") displays[id] = display with
            {
                Count = songCounts.GetValueOrDefault(id)
            };
            else if (display.Kind == "artist") displays[id] = display with
            {
                Count = albumCounts.GetValueOrDefault(id)
            };
        }
        var saved = dates.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal)
            .Where(item => FavoriteDisplaySnapshot.Resource(item.Key) != null)
            .Select(item => new FavoriteViewItem(item.Key, FavoriteDisplaySnapshot.Resource(item.Key)!, item.Value, displays[item.Key])
            { SharedScope = sharedScope }).ToArray();
        var policy = effectivePolicies == null ? null : await effectivePolicies.ResolveForUserAsync(owner, cancellationToken);
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var replacements = new Dictionary<string, FavoriteViewItem>(StringComparer.Ordinal);
        var candidates = saved.Where(item => item.Resource.ResourceKind == ProviderResourceKind.Track &&
                     (item.Resource.ProviderId == "allstarr" || policy?.IsAutomatic(ProviderKey(item.Resource.ProviderId)) != false))
                 .OrderByDescending(item => hearted.Contains(item.ItemId)).ThenBy(item => item.FavoritedAt)
                 .ThenBy(item => item.ItemId, StringComparer.Ordinal).ToArray();
        var displaysBySong = candidates.ToDictionary(item => item.Song(), item => item);
        foreach (var group in MusicSearchService.PartitionMetadataRecordings(displaysBySong.Keys).Where(group => group.Count > 1))
        {
            var members = group.Select(song => displaysBySong[song]).ToArray();
            var chosen = members.OrderByDescending(item => hearted.Contains(item.ItemId)).ThenBy(item => item.FavoritedAt)
                .ThenBy(item => item.ItemId, StringComparer.Ordinal).First();
            var anchor = members.First(item => item.Display.DurationMilliseconds is > 0);
            replacements[chosen.ItemId] = chosen with
            {
                Display = chosen.Display with
                {
                    Album = string.IsNullOrWhiteSpace(chosen.Display.Album) ? anchor.Display.Album : chosen.Display.Album,
                    AlbumId = chosen.Display.AlbumId ?? anchor.Display.AlbumId,
                    DurationMilliseconds = chosen.Display.DurationMilliseconds is > 0 ? chosen.Display.DurationMilliseconds : anchor.Display.DurationMilliseconds
                }
            };
            foreach (var item in members.Where(item => item.ItemId != chosen.ItemId)) hidden.Add(item.ItemId);
        }
        var albumViews = saved.Where(item => item.Resource.ResourceKind == ProviderResourceKind.Album)
            .ToDictionary(item => item.Album(), item => item);
        foreach (var album in albumViews.Keys) album.SongCount = null;
        var albumParents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in MusicAlbumReleases.Partition(albumViews.Keys).Where(group => group.Count > 1))
        {
            var members = group.Select(album => albumViews[album]).ToArray();
            var chosen = members.OrderByDescending(item => hearted.Contains(item.ItemId)).ThenBy(item => item.FavoritedAt)
                .ThenBy(item => item.ItemId, StringComparer.Ordinal).First();
            foreach (var item in members.Where(item => item.ItemId != chosen.ItemId))
            {
                hidden.Add(item.ItemId);
                albumParents[item.ItemId] = chosen.ItemId;
            }
        }
        foreach (var item in saved.Where(item => item.Display.AlbumId != null && albumParents.ContainsKey(item.Display.AlbumId)))
        {
            var current = replacements.GetValueOrDefault(item.ItemId, item);
            replacements[item.ItemId] = current with
            { Display = current.Display with { AlbumId = albumParents[item.Display.AlbumId!] } };
        }
        var visibleSaved = saved.Where(item => !hidden.Contains(item.ItemId))
            .Select(item => replacements.GetValueOrDefault(item.ItemId, item)).ToArray();
        var visibleSongCounts = visibleSaved.Where(item => item.Resource.ResourceKind == ProviderResourceKind.Track && item.Display.AlbumId != null)
            .GroupBy(item => item.Display.AlbumId!, itemComparer).ToDictionary(group => group.Key, group => group.Count(), itemComparer);
        var albumLabels = MusicAlbumReleases.Collapse(visibleSaved.Where(item => item.Resource.ResourceKind == ProviderResourceKind.Album)
            .Select(item => item.Album())).ToDictionary(album => album.Id, album => album.Title, StringComparer.Ordinal);
        var visibleAlbumCounts = visibleSaved.Where(item => item.Resource.ResourceKind == ProviderResourceKind.Album)
            .SelectMany(item => item.Display.ArtistIds.Concat(item.Display.AlbumArtistIds).Distinct(itemComparer))
            .GroupBy(id => id, itemComparer).ToDictionary(group => group.Key, group => group.Count(), itemComparer);
        return visibleSaved.Select(item => item.Resource.ResourceKind == ProviderResourceKind.Album
            ? item with { Display = item.Display with { Count = visibleSongCounts.GetValueOrDefault(item.ItemId), Title = albumLabels[item.ItemId] } }
            : item.Resource.ResourceKind == ProviderResourceKind.Artist
                ? item with { Display = item.Display with { Count = visibleAlbumCounts.GetValueOrDefault(item.ItemId) } }
                : item).ToArray();
    }

    internal Task PreferSavedSongIdsAsync(ProtocolExecutionContext execution, IReadOnlyList<Song> songs,
        CancellationToken cancellationToken) => PreferSavedIdsAsync(execution, songs, [], cancellationToken);

    internal async Task PreferSavedIdsAsync(ProtocolExecutionContext execution, IReadOnlyList<Song> songs,
        IReadOnlyList<Album> albums, CancellationToken cancellationToken)
    {
        if (songs.Count == 0 && albums.Count == 0 || execution.Actor?.EffectiveUserId is not { } owner) return;
        var saved = await ReadAsync(execution, cancellationToken);
        var policy = effectivePolicies == null ? null : await effectivePolicies.ResolveForUserAsync(owner, cancellationToken);
        foreach (var song in songs.Where(song => policy?.IsAutomatic(ProviderKey(song.ExternalProvider ?? "")) != false))
        {
            var match = saved.FirstOrDefault(item => item.Resource.ResourceKind == ProviderResourceKind.Track &&
                (item.Resource.ProviderId == "allstarr" || policy?.IsAutomatic(ProviderKey(item.Resource.ProviderId)) != false) &&
                (song.ExplicitContentLyrics is not (0 or 1 or 3) || item.Display.IsExplicit != null || item.ItemId == song.Id ||
                 ProviderKey(item.Resource.ProviderId) == ProviderKey(song.ExternalProvider ?? "") && item.Resource.Value == song.ExternalId) &&
                (item.ItemId == song.Id ||
                 (ProviderKey(item.Resource.ProviderId) != ProviderKey(song.ExternalProvider ?? "") || item.Resource.Value == song.ExternalId) &&
                 (item.Display.DurationMilliseconds is > 0 || song.PreciseDuration?.TotalSeconds is > 0 || song.Duration is > 0) &&
                 MusicSearchService.MetadataRecordingsMatch(item.Song(), song, allowSparse: true)));
            if (match == null || match.ItemId == song.Id) continue;
            var recording = identities == null ? null : await identities.ResolveRecordingAsync(execution.Actor!, match.ItemId, cancellationToken);
            song.Id = match.ItemId;
            song.CanonicalRecordingId = recording?.Id ??
                (match.Resource.ProviderId == "allstarr" && Guid.TryParse(match.Resource.Value, out var id) ? id : null);
            if (song.PreciseDuration?.TotalSeconds is not > 0 && song.Duration is not > 0 && match.Display.DurationMilliseconds is > 0)
            {
                song.PreciseDuration = TimeSpan.FromMilliseconds(match.Display.DurationMilliseconds.Value);
                song.Duration = (int)(match.Display.DurationMilliseconds.Value / 1000);
            }
            if (string.IsNullOrWhiteSpace(song.Album)) song.Album = match.Display.Album ?? string.Empty;
        }
        foreach (var album in albums.Where(album => policy?.IsAutomatic(ProviderKey(album.ExternalProvider ?? "")) != false))
        {
            var match = saved.Where(item => item.Resource.ResourceKind == ProviderResourceKind.Album).FirstOrDefault(item =>
            {
                if (policy?.IsAutomatic(ProviderKey(item.Resource.ProviderId)) == false) return false;
                var known = item.Album();
                known.SongCount = null;
                return MusicAlbumReleases.SameRelease(known, album) &&
                    (known.IsExplicit != null || album.IsExplicit == null || MusicAlbumReleases.SameCatalog(known, album) ||
                     !albums.Any(other => other.IsExplicit != null && other.IsExplicit != album.IsExplicit &&
                         MusicAlbumReleases.NormalizeUpc(album.Upc) is { } upc && MusicAlbumReleases.NormalizeUpc(other.Upc) == upc));
            });
            if (match != null) album.Id = match.ItemId;
        }
    }

    private static string ProviderKey(string provider) =>
        allstarr.Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId(provider);

    private async Task<HashSet<string>> VisiblePlaylistKeysAsync(ProtocolExecutionContext execution, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (targets == null) return keys;
        var target = targets.Resolve(execution.Principal!.BackendType);
        var context = new BackendPlaylistTargetContext(execution.BackendInstanceId, execution.VerifiedBackendPrincipalId);
        for (var offset = 0; ; offset += 200)
        {
            var page = await target.ListPageAsync(context, null, offset, 200, cancellationToken);
            if (!page.IsSuccess || page.Value == null) return [];
            foreach (var playlist in page.Value) keys.Add(WritablePlaylistMembership.Key(execution, playlist.BackendPlaylistId));
            if (page.Value.Count < 200) return keys;
        }
    }

    private async Task ReadSourceChangesAsync(AllstarrDbContext db, ProtocolExecutionContext execution,
        Guid owner, List<MembershipChange> changes, HashSet<string> excludedSources, CancellationToken cancellationToken)
    {
        var candidates = await db.PlaylistLinks.AsNoTracking().Where(link =>
            link.TargetBackendInstanceId == execution.BackendInstanceId &&
            (execution.Protocol == ProtocolKind.Jellyfin ? link.TargetProtocol == "jellyfin" :
                link.TargetProtocol == "subsonic" || link.TargetProtocol == "opensubsonic" || link.TargetProtocol == "navidrome"))
            .ToArrayAsync(cancellationToken);
        var links = new List<PlaylistLinkRecord>();
        foreach (var link in candidates)
        {
            if (link.TargetPlaylistId == null)
            {
                if (link.OwnerUserId == owner)
                {
                    links.Add(link);
                    if (!link.Enabled) changes.Add(new(link.UpdatedAt, "removed", "source:" + link.Id, []));
                }
                continue;
            }
            var accessible = false;
            if (targets != null && link.Enabled)
            {
                var authorized = await targets.Resolve(link.TargetProtocol).ReadAsync(
                    new BackendPlaylistTargetContext(execution.BackendInstanceId, execution.VerifiedBackendPrincipalId),
                    link.TargetPlaylistId, cancellationToken);
                accessible = authorized.IsSuccess && authorized.Value != null;
            }
            if (!accessible && link.OwnerUserId != owner) continue;
            links.Add(link);
            if (!accessible) excludedSources.Add("source:" + link.Id);
            if (!link.Enabled) changes.Add(new(link.UpdatedAt, "removed", "source:" + link.Id, []));
        }
        await LoadSourceChangesAsync(db, links.Select(link => link.Id).ToArray(), changes, cancellationToken);
    }

    private static async Task LoadSourceChangesAsync(AllstarrDbContext db, Guid[] linkIds,
        List<MembershipChange> changes, CancellationToken cancellationToken)
    {
        var snapshots = await db.PlaylistSourceSnapshots.AsNoTracking().Where(snapshot => linkIds.Contains(snapshot.PlaylistLinkId) &&
            snapshot.PublishedAt != null).ToArrayAsync(cancellationToken);
        var snapshotIds = snapshots.Select(snapshot => snapshot.Id).ToArray();
        var rows = await (from entry in db.PlaylistSourceEntries.AsNoTracking()
                          join metadata in db.ExternalMetadataSnapshots.AsNoTracking() on entry.ExternalMetadataSnapshotId equals metadata.Id
                          join identity in db.ProviderTrackIdentities.AsNoTracking() on metadata.ProviderTrackIdentityId equals identity.Id
                          join snapshot in db.PlaylistSourceSnapshots.AsNoTracking() on entry.PlaylistSourceSnapshotId equals snapshot.Id
                          join link in db.PlaylistLinks.AsNoTracking() on snapshot.PlaylistLinkId equals link.Id
                          where snapshotIds.Contains(entry.PlaylistSourceSnapshotId) && metadata.ResourceKind == "track" &&
                              metadata.OwnerUserId == link.OwnerUserId && snapshot.OwnerUserId == link.OwnerUserId &&
                              metadata.ProviderAccountId == link.ProviderAccountId && metadata.Protocol == link.TargetProtocol &&
                              metadata.BackendInstanceId == link.TargetBackendInstanceId && identity.ProviderId == metadata.ProviderId &&
                              identity.ResourceKind == ProviderResourceKind.Track && identity.ExternalIdHash == metadata.ExternalIdHash &&
                              (identity.ProviderAccountId == metadata.ProviderAccountId || identity.ProviderAccountId == null && identity.Scope == ProviderIdentityScope.Catalog) &&
                              (identity.Verification == ProviderIdentityVerification.Verified || identity.Verification == ProviderIdentityVerification.Pinned)
                          select new { entry.PlaylistSourceSnapshotId, metadata.PayloadJson, identity.ProviderId, identity.ExternalId })
            .ToArrayAsync(cancellationToken);
        foreach (var snapshot in snapshots)
        {
            var members = new List<FavoriteDisplaySnapshot>();
            foreach (var row in rows.Where(row => row.PlaylistSourceSnapshotId == snapshot.Id))
            {
                try
                {
                    var song = JsonSerializer.Deserialize<Song>(row.PayloadJson, JsonOptions);
                    if (song == null || string.IsNullOrWhiteSpace(song.Title)) continue;
                    var id = $"ext-{row.ProviderId}-song-{row.ExternalId}";
                    var display = Display(id, song);
                    using var payload = JsonDocument.Parse(row.PayloadJson);
                    if (payload.RootElement.TryGetProperty("DurationMilliseconds", out var duration) && duration.TryGetInt64(out var milliseconds))
                        display = display with { DurationMilliseconds = milliseconds };
                    if (payload.RootElement.TryGetProperty("IsExplicit", out var explicitValue) &&
                        explicitValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        display = display with { IsExplicit = explicitValue.GetBoolean() };
                    members.Add(display);
                }
                catch (JsonException) { }
            }
            changes.Add(new(snapshot.PublishedAt!.Value, snapshot.SnapshotVersion.ToString("D10"), "source:" + snapshot.PlaylistLinkId, members));
        }
    }

    internal static async Task ArchiveSourceAsync(AllstarrDbContext db, ProtocolExecutionContext execution,
        PlaylistLinkRecord link, DateTimeOffset removedAt, CancellationToken cancellationToken)
    {
        var changes = new List<MembershipChange>();
        await LoadSourceChangesAsync(db, [link.Id], changes, cancellationToken);
        if (changes.Count == 0) return;
        changes.Add(new(removedAt, "removed", "source:" + link.Id, []));
        var payload = JsonSerializer.Serialize(changes);
        static string Hash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = link.OwnerUserId,
            Protocol = execution.Protocol.ToString().ToLowerInvariant(),
            BackendInstanceId = link.TargetBackendInstanceId,
            BackendPrincipalId = execution.VerifiedBackendPrincipalId,
            ProviderId = "allstarr",
            ResourceKind = "library-membership-history",
            ExternalIdHash = Hash(link.Id.ToString()),
            SnapshotVersion = 1,
            ProviderRevision = "1",
            PayloadJson = payload,
            PayloadSha256 = Hash(payload),
            CorrelationId = execution.CorrelationId,
            RetrievedAt = removedAt
        });
    }

    private static FavoriteDisplaySnapshot Display(string id, Song song) => new(1, id, "song", song.Title,
        song.Artists is { Count: > 0 } ? song.Artists.ToArray() : string.IsNullOrWhiteSpace(song.Artist) ? [] : [song.Artist],
        song.Album, song.PreciseDuration is { } duration ? (long)duration.TotalMilliseconds : song.Duration * 1000L, song.Year)
    {
        ArtistIds = song.ArtistIds is { Count: > 0 } ? song.ArtistIds.ToArray() : song.ArtistId == null ? [] : [song.ArtistId],
        AlbumArtists = song.AlbumArtists is { Count: > 0 } ? song.AlbumArtists.Select(artist => artist.Name).ToArray()
            : string.IsNullOrWhiteSpace(song.AlbumArtist) ? [] : [song.AlbumArtist],
        AlbumArtistIds = song.AlbumArtists is { Count: > 0 } ? song.AlbumArtists.Select(artist => artist.Id).ToArray()
            : song.ArtistId == null ? [] : [song.ArtistId],
        AlbumId = song.AlbumId,
        Genres = song.Genre == null ? [] : [song.Genre],
        IsExplicit = song.ExplicitContentLyrics switch { 1 => true, 0 or 3 => false, _ => null },
        Upc = song.Upc
    };

    private static IEnumerable<FavoriteDisplaySnapshot> Expand(IEnumerable<FavoriteDisplaySnapshot> items)
    {
        foreach (var item in items)
        {
            if (FavoriteDisplaySnapshot.Resource(item.ItemId) is not { } resource || resource.ResourceKind == ProviderResourceKind.Playlist) continue;
            yield return item;
            if (resource.ResourceKind != ProviderResourceKind.Track) continue;
            if (item.AlbumId is { } album && FavoriteDisplaySnapshot.Resource(album)?.ResourceKind == ProviderResourceKind.Album)
                yield return new(1, album, "album", item.Album ?? "Saved album", item.AlbumArtists.Length > 0 ? item.AlbumArtists : item.Artists, Year: item.Year)
                {
                    ArtistIds = item.ArtistIds,
                    AlbumArtistIds = item.AlbumArtistIds,
                    AlbumArtists = item.AlbumArtists,
                    Genres = item.Genres,
                    Upc = item.Upc
                };
            foreach (var id in item.ArtistIds.Concat(item.AlbumArtistIds).Distinct(StringComparer.Ordinal))
                if (FavoriteDisplaySnapshot.Resource(id)?.ResourceKind == ProviderResourceKind.Artist)
                {
                    var index = Array.IndexOf(item.ArtistIds, id);
                    var albumIndex = Array.IndexOf(item.AlbumArtistIds, id);
                    var name = index >= 0 ? item.Artists.ElementAtOrDefault(index) : albumIndex >= 0 ? item.AlbumArtists.ElementAtOrDefault(albumIndex) : null;
                    yield return new(1, id, "artist", name ?? "Saved artist", []) { Genres = item.Genres };
                }
        }
    }

    private sealed record MembershipChange(DateTimeOffset At, string Order, string Source, IReadOnlyList<FavoriteDisplaySnapshot> Items);
}
