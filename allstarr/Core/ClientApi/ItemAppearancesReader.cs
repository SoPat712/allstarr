using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApi;

// Which of the listener's mixes and synced playlists hold an album's or a song's songs.
public sealed class ItemAppearancesReader(PersonalMixService mixes, IDbContextFactory<AllstarrDbContext> factory,
    IProtocolProviderGateway providers, SavedLibraryReader saved, JellyfinProxyService jellyfin, IProviderRegistry registry)
{
    private sealed record SongKey(string? LocalId, string? Provider, string? ExternalId);

    public async Task<ClientItemAppearances> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string itemId)
    {
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var token = execution.CancellationToken;
        var songs = await SongsAsync(http, execution, itemId);
        var locals = songs.Where(song => song.LocalId != null).Select(song => Native(song.LocalId!)).ToHashSet(StringComparer.Ordinal);
        var externals = songs.Where(song => song.Provider != null).Select(song => External(song.Provider!, song.ExternalId!))
            .ToHashSet(StringComparer.Ordinal);
        var result = new List<ClientItemAppearance>();
        var stored = await mixes.ListAsync(execution);
        foreach (var mix in stored)
        {
            var matched = mix.Songs.Select(item => item.Song).Count(song => song.IsLocal
                ? locals.Contains(Native(song.Id))
                : song.ExternalProvider != null && song.ExternalId != null &&
                  externals.Contains(External(song.ExternalProvider, song.ExternalId)));
            if (matched == 0) continue;
            var inApps = mix.Added && mix.Generation > 0;
            result.Add(new("mix", mix.Id.ToString("D"), mix.Name, mix.SourceLabel ?? "Made for you",
                $"/allstarr/v1/mixes/{mix.Id}/cover", matched, inApps ? PlaylistVirtualizationService.CreateProtocolId(mix.Id) : null));
        }

        var reader = new SongPlaylistMembershipReader(factory);
        var counts = new Dictionary<Guid, int>();
        foreach (var song in songs)
        {
            var identity = song.LocalId != null
                ? new SongPlaylistIdentity(Protocol: execution.Protocol.ToString().ToLowerInvariant(),
                    BackendInstanceId: execution.BackendInstanceId, BackendItemId: song.LocalId)
                : new SongPlaylistIdentity(ProviderId: song.Provider, ExternalId: song.ExternalId);
            foreach (var membership in (await reader.ReadAsync(owner, identity, token)).DistinctBy(item => item.Id))
                counts[membership.Id] = counts.GetValueOrDefault(membership.Id) + 1;
        }
        var mixIds = stored.Select(mix => mix.Id).ToHashSet();
        var linkIds = counts.Keys.Where(id => !mixIds.Contains(id)).ToArray();
        if (linkIds.Length > 0)
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var links = await db.PlaylistLinks.AsNoTracking()
                .Where(link => link.OwnerUserId == owner && linkIds.Contains(link.Id)).ToArrayAsync(token);
            var hidden = (await PlaylistAppRemoval.Rows(db).Where(item => item.OwnerUserId == owner)
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
        return new(itemId, songs.Count, result
            .OrderByDescending(item => item.MatchedSongCount)
            .ThenBy(item => item.Kind == "mix" ? 0 : 1)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private async Task<IReadOnlyList<SongKey>> SongsAsync(HttpContext http, ProtocolExecutionContext execution, string itemId)
    {
        var parsed = LocalLibraryService.ParseExternalResource(itemId);
        if (parsed.isExternal)
        {
            if (parsed.type == "song") return [new(null, parsed.provider, parsed.externalId)];
            if (parsed.type != "album") throw new ArgumentException("Only albums and songs have appearances.");
            var album = await providers.GetAlbumAsync(execution, parsed.provider!, parsed.externalId!) ??
                await saved.AlbumAsync(execution, $"ext-{parsed.provider}-album-{parsed.externalId}", execution.CancellationToken) ??
                throw new KeyNotFoundException();
            return album.Songs.Select(song =>
            {
                if (song.IsLocal) return new SongKey(song.Id, null, null);
                var item = LocalLibraryService.ParseExternalResource(song.Id);
                return new SongKey(null, song.ExternalProvider ?? item.provider, song.ExternalId ?? item.externalId);
            }).Where(song => song.LocalId != null || song.Provider != null && song.ExternalId != null).ToArray();
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
            if (type == "Audio") return [new(body.RootElement.GetProperty("Id").GetString(), null, null)];
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
            return children.RootElement.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().Select(item => item.TryGetProperty("Id", out var id) ? id.GetString() : null)
                    .OfType<string>().Select(id => new SongKey(id, null, null)).ToArray()
                : [];
        }
    }

    private static string Native(string id) => Guid.TryParse(id, out var guid) ? guid.ToString("N") : id;

    // Collected playlist songs carry the hash of the source's id, so both forms compare as the hash.
    private static string External(string provider, string externalId) =>
        $"{provider}:{CanonicalCatalogKeys.StoredKeys(externalId)[^1]}";
}
