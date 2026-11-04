using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApi;

public sealed class MixReader(PersonalMixService mixes, IDbContextFactory<AllstarrDbContext> factory,
    IBackendLibraryAccessResolver libraryAccess, IDurableRuntimeSettings settings, IProviderRegistry registry,
    IProtocolProviderGateway providers, JellyfinProxyService jellyfin, LibraryProviderResolver libraryProvider)
{
    public async Task<ClientMixes> ListAsync(ProtocolExecutionContext execution, string? timeZoneId)
    {
        var token = execution.CancellationToken;
        var stored = await mixes.ListAsync(execution, timeZoneId);
        var allowed = await AllowedLocalIdsAsync(execution, stored);
        var publish = (bool)(await settings.GetAsync(PersonalMixService.PublishKey, token)).Value;
        return new(stored.Select(mix =>
        {
            var songs = Playable(mix, allowed);
            var inApps = publish && mix.Added && mix.Generation > 0;
            return new ClientMix(mix.Id, mix.Kind, mix.Name, mix.Description, State(mix), songs.Count,
                songs.Sum(item => item.Song.Duration * 1000L ?? 0), mix.UpdatedAt,
                $"/allstarr/v1/mixes/{mix.Id}/cover", inApps,
                inApps ? PlaylistVirtualizationService.CreateProtocolId(mix.Id) : null);
        }).ToArray());
    }

    public async Task<ClientMixItems> ItemsAsync(HttpContext http, ProtocolExecutionContext execution, Guid mixId)
    {
        var mix = await mixes.GetAsync(execution.RequireActor().EffectiveUserId!.Value, mixId, execution.CancellationToken);
        var songs = Playable(mix, await AllowedLocalIdsAsync(execution, [mix]));
        var native = await ReadNativeAsync(http, execution, songs.Where(item => item.Song.IsLocal).Select(item => item.Song.Id).Distinct().ToArray());
        var providerIds = songs.Where(item => !item.Song.IsLocal).Select(item => item.Song.ExternalProvider)
            .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var icons = (await ProviderArtworkResolver.ResolveIconsAsync(http.RequestServices, providerIds, execution.CancellationToken))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var library = songs.Any(item => item.Song.IsLocal) ? await libraryProvider.ResolveAsync(http, execution) : null;
        var result = new List<ClientMixItem>(songs.Count);
        foreach (var item in songs)
        {
            var song = item.Song;
            var itemId = song.Id;
            var albumId = song.AlbumId;
            var artistId = song.ArtistId ?? song.ArtistIds.FirstOrDefault();
            ItemDetailsProvider? provider = null;
            if (song.IsLocal)
            {
                if (!native.TryGetValue(song.Id, out var root)) continue;
                provider = library;
                itemId = root.GetProperty("Id").GetString()!;
                albumId = root.TryGetProperty("AlbumId", out var album) ? album.GetString() : null;
                artistId = root.TryGetProperty("ArtistItems", out var artists) && artists.ValueKind == JsonValueKind.Array &&
                    artists.GetArrayLength() > 0 && artists[0].TryGetProperty("Id", out var artist) ? artist.GetString() : null;
            }
            else if (song.ExternalProvider is { } source)
            {
                if (song.ExternalId is { } externalId &&
                    await providers.GetStoredSongAsync(execution, source, externalId) is { } metadata)
                {
                    itemId = string.IsNullOrWhiteSpace(metadata.Id) ? song.Id : metadata.Id;
                    albumId = metadata.AlbumId ?? albumId;
                    artistId = metadata.ArtistId ?? metadata.ArtistIds.FirstOrDefault() ?? artistId;
                    source = metadata.ExternalProvider ?? source;
                }
                if (song.ExternalProvider == "allstarr" && song.ExternalId is { } recordingId)
                    source = await providers.GetStreamingProviderAsync(execution, "allstarr", recordingId) ?? "allstarr";
                if (registry.TryGet(source, out var descriptor))
                {
                    if (!icons.TryGetValue(source, out var icon))
                        icons[source] = icon = (await ProviderArtworkResolver.ResolveIconsAsync(http.RequestServices, [source], execution.CancellationToken)).GetValueOrDefault(source);
                    provider = icon?.Provider(source, descriptor!.DisplayName) ?? new(source, descriptor!.DisplayName, null);
                }
                itemId = ClientCatalogIds.Normalize(itemId)!;
                albumId = ClientCatalogIds.Normalize(albumId);
                artistId = ClientCatalogIds.Normalize(artistId);
            }
            result.Add(new(itemId, song.Title, song.Artist, song.Album, albumId, artistId,
                song.Duration * 1000L, !song.IsLocal, provider, item.Reason));
        }
        return new(mix.Id, mix.UpdatedAt, result);
    }

    public async Task<MixCoverInput> CoverAsync(ProtocolExecutionContext execution, Guid mixId)
    {
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var mix = await mixes.GetAsync(owner, mixId, execution.CancellationToken);
        var input = MixCoverInput.From(mix);
        if (mix.Kind != "external") return input;
        await using var db = await factory.CreateDbContextAsync(execution.CancellationToken);
        var link = await db.PlaylistLinks.AsNoTracking().SingleAsync(item => item.Id == mixId && item.OwnerUserId == owner, execution.CancellationToken);
        return input with { Source = link.SourceProviderId, SourcePlaylistId = link.SourcePlaylistId };
    }

    private async Task<HashSet<string>> AllowedLocalIdsAsync(ProtocolExecutionContext execution, IReadOnlyList<PersonalMix> stored)
    {
        var ids = stored.SelectMany(mix => mix.Songs).Where(item => item.Song.IsLocal).Select(item => item.Song.Id).Distinct().ToArray();
        if (ids.Length == 0) return [];
        await using var db = await factory.CreateDbContextAsync(execution.CancellationToken);
        return (await LibraryTrackAccess.Query(db, execution, await libraryAccess.ResolveAsync(execution, execution.CancellationToken))
            .Where(item => ids.Contains(item.BackendItemId)).Select(item => item.BackendItemId).ToArrayAsync(execution.CancellationToken)).ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlyList<PersonalMixItem> Playable(PersonalMix mix, HashSet<string> allowed)
    {
        var songs = mix.Songs.Where(item => !item.Song.IsLocal || allowed.Contains(item.Song.Id));
        return (mix.Kind == "external" ? songs.DistinctBy(item => item.Song.IsLocal
            ? (Local: true, Provider: (string?)null, Id: item.Song.Id)
            : (Local: false, Provider: item.Song.ExternalProvider, Id: item.Song.ExternalId ?? item.Song.Id)) : songs).ToArray();
    }

    private async Task<Dictionary<string, JsonElement>> ReadNativeAsync(HttpContext http, ProtocolExecutionContext execution, string[] ids)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var batch in ids.Chunk(100))
        {
            var (body, status) = await jellyfin.GetJsonAsync($"Users/{Uri.EscapeDataString(execution.VerifiedBackendPrincipalId)}/Items",
                new() { ["ids"] = string.Join(',', batch), ["limit"] = "100" },
                http.Request.Headers, execution.CancellationToken);
            using var response = body;
            if (status is 400 or 401 or 403 or 404) continue;
            if (status != 200 || body == null) throw new HttpRequestException("Library details are unavailable.");
            if (!body.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("Id", out var id) || !item.TryGetProperty("Type", out var type) || type.GetString() != "Audio") continue;
                foreach (var requested in batch.Where(requested => SameNativeId(id.GetString(), requested)))
                    result[requested] = item.Clone();
            }
        }
        return result;
    }

    private static bool SameNativeId(string? first, string second) => first == second ||
        Guid.TryParse(first, out var firstId) && Guid.TryParse(second, out var secondId) && firstId == secondId;

    private static ClientMixState State(PersonalMix mix)
    {
        if (mix.Readiness.Ready) return ClientMixState.Ready;
        var message = mix.Readiness.Message;
        if (message.StartsWith("Matching songs", StringComparison.OrdinalIgnoreCase)) return ClientMixState.Building;
        if (message.StartsWith("No playable songs", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Refresh this mix", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Fewer than", StringComparison.OrdinalIgnoreCase)) return ClientMixState.Empty;
        if (message.Contains("listening", StringComparison.OrdinalIgnoreCase) || message.Contains("history", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Play about", StringComparison.OrdinalIgnoreCase)) return ClientMixState.NeedsListening;
        return ClientMixState.Empty;
    }
}
