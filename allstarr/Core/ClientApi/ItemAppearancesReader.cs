using allstarr.Core.Operations;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApi;

// Which of the listener's mixes and synced playlists hold an album's or a song's songs.
public sealed class ItemAppearancesReader(PersonalMixService mixes, IDbContextFactory<AllstarrDbContext> factory,
    IProtocolProviderGateway providers, SavedLibraryReader saved, JellyfinProxyService jellyfin, IProviderRegistry registry,
    ITrackIdentityService identities, IApplicationCache cache, ApplicationCacheRequestCoalescer readCoalescer,
    IBackendLibraryAccessResolver libraryAccess)
{
    private sealed record MixMembership(Guid Id, ClientItemAppearance Item, SongKey[] Songs);
    private sealed record SongKey(string? LocalId, string? Provider, string? ExternalId, Guid? CanonicalRecordingId);

    public async Task<ClientItemAppearances> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string itemId)
    {
        using var matching = RequestTiming.Current?.Stage("match");
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var token = execution.CancellationToken;
        var songs = await SongsAsync(http, execution, itemId);
        var recordings = songs.Select(song => song.CanonicalRecordingId).OfType<Guid>().ToHashSet();
        var locals = songs.Where(song => song.LocalId != null).Select(song => Native(song.LocalId!)).ToHashSet(StringComparer.Ordinal);
        var externals = songs.Where(song => song.Provider != null).Select(song => External(song.Provider!, song.ExternalId!))
            .ToHashSet(StringComparer.Ordinal);
        var mixMemberships = await MixMembershipsAsync(execution);
        var result = mixMemberships.Select(mix => mix.Item with
        { MatchedSongCount = mix.Songs.Count(song => Matches(song, recordings, locals, externals)) })
            .Where(item => item.MatchedSongCount > 0).ToList();

        var reader = new SongPlaylistMembershipReader(factory);
        var counts = new Dictionary<Guid, int>();
        var playlistSongs = songs.Select(song => song.LocalId != null
                ? new SongPlaylistIdentity(Protocol: execution.Protocol.ToString().ToLowerInvariant(),
                    BackendInstanceId: execution.BackendInstanceId, BackendItemId: song.LocalId)
                : new SongPlaylistIdentity(ProviderId: song.Provider, ExternalId: song.ExternalId)).ToArray();
        foreach (var memberships in await reader.ReadAsync(playlistSongs, owner, token))
            foreach (var membership in memberships.DistinctBy(item => item.Id))
                counts[membership.Id] = counts.GetValueOrDefault(membership.Id) + 1;
        var mixIds = mixMemberships.Select(mix => mix.Id).ToHashSet();
        var linkIds = counts.Keys.Where(id => !mixIds.Contains(id)).ToArray();
        if (linkIds.Length > 0)
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var links = await db.PlaylistLinks.AsNoTracking()
                .Where(link => link.OwnerUserId == owner && linkIds.Contains(link.Id)).ToArrayAsync(token);
            var hiddenKeys = linkIds.Select(PlaylistAppRemoval.Key).ToArray();
            var hidden = (await PlaylistAppRemoval.Rows(db).Where(item => item.OwnerUserId == owner && hiddenKeys.Contains(item.ExternalIdHash))
                .Select(item => item.ExternalIdHash).ToArrayAsync(token)).ToHashSet(StringComparer.Ordinal);
            var names = await db.PlaylistSourceSnapshots.AsNoTracking()
                .Where(item => item.OwnerUserId == owner && linkIds.Contains(item.PlaylistLinkId) && item.PublishedAt.HasValue)
                .GroupBy(item => item.PlaylistLinkId)
                .Select(group => new { Id = group.Key, Name = group.OrderByDescending(item => item.SnapshotVersion).Select(item => item.Name).First() })
                .ToDictionaryAsync(item => item.Id, item => item.Name, token);
            foreach (var link in links)
            {
                if (!link.Enabled || hidden.Contains(PlaylistAppRemoval.Key(link.Id)) || PersonalMixService.IsExternalLink(link)) continue;
                var playlistId = link.Mode == PlaylistLinkMode.Virtual
                    ? PlaylistVirtualizationService.CreateProtocolId(link.Id)
                    : link.TargetPlaylistId;
                if (string.IsNullOrWhiteSpace(playlistId)) continue;
                var source = registry.TryGet(link.SourceProviderId, out var descriptor) ? descriptor!.DisplayName : link.SourceProviderId;
                result.Add(new("playlist", playlistId, names.GetValueOrDefault(link.Id) ?? link.SourcePlaylistId, $"From {source}",
                    $"/Items/{Uri.EscapeDataString(playlistId)}/Images/Primary", counts[link.Id], playlistId));
            }
        }
        using var serialization = RequestTiming.Current?.Stage("serialize");
        return new(itemId, songs.Count, result
            .OrderByDescending(item => item.MatchedSongCount)
            .ThenBy(item => item.Kind == "mix" ? 0 : 1)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private async Task<MixMembership[]> MixMembershipsAsync(ProtocolExecutionContext execution)
    {
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var token = execution.CancellationToken;
        var access = await libraryAccess.ResolveAsync(execution, token);
        await using var db = await factory.CreateDbContextAsync(token);
        string Key() => db.FeedReadKey(execution, "mix-appearances", access) + ":" + db.FeedReadLibraryRevisionFor(owner);
        var key = Key();
        if (await cache.GetAsync<MixMembership[]>(key) is { } cached) return cached;
        return await readCoalescer.RunAsync(key, async () =>
        {
            if (await cache.GetAsync<MixMembership[]>(key) is { } retained) return retained;
            var stored = await mixes.ListAsync(execution);
            var value = stored.Select(mix => new MixMembership(mix.Id,
                new("mix", mix.Id.ToString("D"), mix.Name, mix.SourceLabel ?? "Made for you", $"/allstarr/v1/mixes/{mix.Id}/cover", 0,
                    mix.Added && mix.Generation > 0 ? PlaylistVirtualizationService.CreateProtocolId(mix.Id) : null),
                mix.Songs.Select(item =>
                {
                    var song = item.Song;
                    var parsed = LocalLibraryService.ParseExternalResource(song.Id);
                    return new SongKey(song.Id, song.ExternalProvider ?? (parsed.type == "song" ? parsed.provider : null),
                        song.ExternalId ?? (parsed.type == "song" ? parsed.externalId : null), song.CanonicalRecordingId);
                }).ToArray())).ToArray();
            if (access.Succeeded && key == Key()) await cache.SetAsync(key, value, TimeSpan.FromSeconds(30));
            return value;
        }, token);
    }

    private async Task<IReadOnlyList<SongKey>> SongsAsync(HttpContext http, ProtocolExecutionContext execution, string itemId)
    {
        var parsed = LocalLibraryService.ParseExternalResource(itemId);
        if (parsed.isExternal)
        {
            if (parsed.type == "song")
            {
                Song? song;
                // A disabled or briefly failing service still leaves the listener's stored songs to match.
                try { song = await providers.GetSongAsync(execution, parsed.provider!, parsed.externalId!); }
                catch (Exception exception) when (exception is UnauthorizedAccessException or HttpRequestException) { song = null; }
                if (song == null)
                {
                    if (execution.Actor?.EffectiveUserId is not { } owner ||
                        !await KnownExternalSongAsync(owner, parsed.provider!, parsed.externalId!, execution.CancellationToken))
                        throw new KeyNotFoundException();
                    return await WithRecordingsAsync(execution, [new(null, parsed.provider, parsed.externalId, null)]);
                }
                return song.IsLocal
                    ? await WithRecordingsAsync(execution, [new(song.Id, null, null, song.CanonicalRecordingId)])
                    : await WithRecordingsAsync(execution, [new(null, song.ExternalProvider ?? parsed.provider,
                        song.ExternalId ?? parsed.externalId, song.CanonicalRecordingId)]);
            }
            if (parsed.type != "album") throw new ArgumentException("Only albums and songs have appearances.");
            var album = await providers.GetAlbumAsync(execution, parsed.provider!, parsed.externalId!) ??
                await saved.AlbumAsync(execution, $"ext-{parsed.provider}-album-{parsed.externalId}", execution.CancellationToken) ??
                throw new KeyNotFoundException();
            return await WithRecordingsAsync(execution, album.Songs.Select(song =>
            {
                if (song.IsLocal) return new SongKey(song.Id, null, null, song.CanonicalRecordingId);
                var item = LocalLibraryService.ParseExternalResource(song.Id);
                return new SongKey(null, song.ExternalProvider ?? item.provider, song.ExternalId ?? item.externalId, song.CanonicalRecordingId);
            }).Where(song => song.LocalId != null || song.Provider != null && song.ExternalId != null).ToArray());
        }

        var user = Uri.EscapeDataString(execution.VerifiedBackendPrincipalId);
        var (body, status) = await jellyfin.GetJsonAsync($"Users/{user}/Items/{Uri.EscapeDataString(itemId)}", null,
            http.Request.Headers, execution.CancellationToken);
        using (body)
        {
            if (status is 400 or 404) throw new KeyNotFoundException();
            if (status is 401 or 403) throw new UnauthorizedAccessException();
            if (status != 200 || body == null) throw new HttpRequestException("Library details are unavailable.");
            var type = body.RootElement.TryGetProperty("Type", out var value) ? value.GetString() : null;
            if (type == "Audio") return await WithRecordingsAsync(execution, [new(body.RootElement.GetProperty("Id").GetString(), null, null, null)]);
            if (type != "MusicAlbum") throw new ArgumentException("Only albums and songs have appearances.");
        }
        var (children, childStatus) = await jellyfin.GetJsonAsync($"Users/{user}/Items", new()
        {
            ["parentId"] = itemId,
            ["includeItemTypes"] = "Audio",
            ["recursive"] = "true",
            ["enableImages"] = "false",
            ["limit"] = "500"
        }, http.Request.Headers, execution.CancellationToken);
        using (children)
        {
            if (childStatus != 200 || children == null) throw new HttpRequestException("Library details are unavailable.");
            return await WithRecordingsAsync(execution, children.RootElement.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().Select(item => item.TryGetProperty("Id", out var id) ? id.GetString() : null)
                    .OfType<string>().Select(id => new SongKey(id, null, null, null)).ToArray()
                : []);
        }
    }

    private async Task<IReadOnlyList<SongKey>> WithRecordingsAsync(ProtocolExecutionContext execution, IReadOnlyList<SongKey> songs)
    {
        if (songs.Count == 0 || execution.Actor?.EffectiveUserId is not { } owner) return songs;
        var token = execution.CancellationToken;
        var external = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var local = new Dictionary<string, Guid>(StringComparer.Ordinal);
        try
        {
            var publicIds = songs.Where(song => song.CanonicalRecordingId == null && song.Provider != null && song.ExternalId != null)
                .Select(song => ClientCatalogIds.Resource(song.Provider!, ProviderResourceKind.Track, song.ExternalId!))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (publicIds.Length > 0)
                foreach (var (id, recording) in await identities.ResolveRecordingsAsync(execution.RequireActor(), publicIds, token))
                    if (recording != null) external[id] = recording.Id;
            var localIds = songs.Where(song => song.CanonicalRecordingId == null && song.LocalId != null)
                .Select(song => song.LocalId!).Distinct(StringComparer.Ordinal).ToArray();
            if (localIds.Length > 0)
            {
                await using var db = await factory.CreateDbContextAsync(token);
                var protocol = execution.Protocol.ToString().ToLowerInvariant();
                var rows = await db.LibraryTracks.AsNoTracking().Where(track =>
                        track.OwnerUserId == owner && track.Protocol == protocol &&
                        track.BackendInstanceId == execution.BackendInstanceId && track.CanonicalRecordingId != null &&
                        EF.Parameter(localIds).Contains(track.BackendItemId))
                    .Select(track => new { track.BackendItemId, Recording = track.CanonicalRecordingId!.Value })
                    .ToArrayAsync(token);
                foreach (var group in rows.GroupBy(row => row.BackendItemId, StringComparer.Ordinal))
                {
                    var ids = group.Select(row => row.Recording).Distinct().Take(2).ToArray();
                    if (ids.Length != 1) continue;
                    local[group.Key] = ids[0];
                    local.TryAdd(Native(group.Key), ids[0]);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
        return songs.Select(song =>
        {
            if (song.CanonicalRecordingId != null) return song;
            if (song.Provider != null && song.ExternalId != null &&
                external.TryGetValue(ClientCatalogIds.Resource(song.Provider, ProviderResourceKind.Track, song.ExternalId), out var recording))
                return song with { CanonicalRecordingId = recording };
            if (song.LocalId != null && (local.TryGetValue(song.LocalId, out var native) || local.TryGetValue(Native(song.LocalId), out native)))
                return song with { CanonicalRecordingId = native };
            return song;
        }).ToArray();
    }

    private async Task<bool> KnownExternalSongAsync(Guid owner, string provider, string externalId, CancellationToken token)
    {
        var keys = CanonicalCatalogKeys.StoredKeys(externalId);
        await using var db = await factory.CreateDbContextAsync(token);
        if (await db.ExternalMetadataSnapshots.AsNoTracking().AnyAsync(item => item.OwnerUserId == owner &&
                item.ResourceKind == "track" && item.ProviderId == provider && EF.Parameter(keys).Contains(item.ExternalIdHash), token))
            return true;
        return await db.ProviderTrackIdentities.AsNoTracking().AnyAsync(identity =>
            identity.ResourceKind == ProviderResourceKind.Track &&
            (identity.Verification == ProviderIdentityVerification.Verified || identity.Verification == ProviderIdentityVerification.Pinned) &&
            identity.VerificationMethod != "automatic-suggestion" &&
            identity.VerificationMethod != ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod &&
            identity.VerificationMethod != ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod &&
            identity.VerificationMethod != CatalogIdentityPolicy.SupersededVerificationMethod &&
            identity.ProviderId == provider && EF.Parameter(keys).Contains(identity.ExternalIdHash) &&
            (identity.Scope == ProviderIdentityScope.Catalog || db.ProviderAccounts.Any(account =>
                account.Id == identity.ProviderAccountId && account.OwnerUserId == owner)), token);
    }

    private static bool Matches(SongKey song, HashSet<Guid> recordings, HashSet<string> locals, HashSet<string> externals)
    {
        if (song.CanonicalRecordingId is { } recording && recordings.Contains(recording)) return true;
        if (song.Provider != null && song.ExternalId != null && externals.Contains(External(song.Provider, song.ExternalId))) return true;
        return song.LocalId != null && locals.Contains(Native(song.LocalId));
    }

    private static string Native(string id) => Guid.TryParse(id, out var guid) ? guid.ToString("N") : id;

    // Collected playlist songs carry the hash of the source's id, so both forms compare as the hash.
    private static string External(string provider, string externalId) =>
        $"{provider}:{CanonicalCatalogKeys.StoredKeys(externalId)[^1]}";
}
