using allstarr.Core.Operations;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApi;

public sealed class MixReader(PersonalMixService mixes, IDbContextFactory<AllstarrDbContext> factory,
    IBackendLibraryAccessResolver libraryAccess, IDurableRuntimeSettings settings, IProviderRegistry registry,
    IProtocolProviderGateway providers, JellyfinProxyService jellyfin, LibraryProviderResolver libraryProvider,
    IApplicationCache cache, ApplicationCacheRequestCoalescer readCoalescer, IPlatformClock clock)
{
    private sealed record CoverInputEntry(MixCoverInput Input, long LibraryRevision);

    public async Task<ClientMixes> ListAsync(HttpContext http, ProtocolExecutionContext execution, string? timeZoneId)
    {
        using var building = RequestTiming.Current?.Stage("build");
        var access = await libraryAccess.ResolveAsync(execution, execution.CancellationToken);
        return (await FeedReadCache.ReadAsync<ClientMixes>(factory, cache, readCoalescer, clock, execution,
            "mixes:" + timeZoneId, access, http, async (request, context) =>
                await request!.RequestServices.GetRequiredService<MixReader>().ListCoreAsync(request!, context, timeZoneId)))!;
    }

    private async Task<ClientMixes> ListCoreAsync(HttpContext http, ProtocolExecutionContext execution, string? timeZoneId)
    {
        var token = execution.CancellationToken;
        var stored = await mixes.ListAsync(execution, timeZoneId);
        var publish = (bool)(await settings.GetAsync(PersonalMixService.PublishKey, token)).Value;
        stored = stored.Where(mix => mix.Kind != "external" || IsInApps(mix, publish)).ToArray();
        var allowed = await AllowedLocalIdsAsync(execution, stored);
        var artworkSongs = stored.Where(mix => mix.Kind != "external" || mix.ArtworkUrl == null)
            .Select(mix => Playable(mix, allowed).Select(item => item.Song).ToArray()).ToArray();
        var native = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var firstIds = artworkSongs.SelectMany(songs => songs.DistinctBy(song =>
                (song.IsLocal, Id: song.IsLocal ? song.Id : ArtworkUrl(song, native))).Take(4))
            .Where(song => song.IsLocal).Select(song => song.Id).Distinct().ToArray();
        native = await ReadNativeAsync(http, execution, firstIds);
        var attempted = firstIds.ToHashSet(StringComparer.Ordinal);
        bool NeedsMore(Song[] songs)
        {
            var pictures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var song in songs)
            {
                if (song.IsLocal && !attempted.Contains(song.Id)) return true;
                if (ArtworkUrl(song, native) is { } url && pictures.Add(url) && pictures.Count == 4) return false;
            }
            return false;
        }
        var remainingIds = artworkSongs.Where(NeedsMore).SelectMany(songs => songs)
            .Where(song => song.IsLocal && !attempted.Contains(song.Id)).Select(song => song.Id).Distinct().ToArray();
        foreach (var item in await ReadNativeAsync(http, execution, remainingIds)) native[item.Key] = item.Value;
        var covers = await CoverInputsAsync(execution, stored);
        using var serialization = RequestTiming.Current?.Stage("serialize");
        return new(stored.Select(mix =>
        {
            var songs = Playable(mix, allowed);
            var inApps = IsInApps(mix, publish);
            return new ClientMix(mix.Id, mix.Kind, mix.Name, mix.Description, State(mix), mix.SongCount ?? songs.Count,
                songs.Sum(item => item.Song.Duration * 1000L ?? 0), mix.UpdatedAt,
                $"/allstarr/v1/mixes/{mix.Id}/cover?v={MixCoverImageService.Version(covers[mix.Id])}", inApps,
                inApps ? PlaylistVirtualizationService.CreateProtocolId(mix.Id) : null,
                mix.Kind == "external" && mix.ArtworkUrl != null
                    ? [$"/allstarr/v1/mixes/{mix.Id}/cover?v={MixCoverImageService.Version(covers[mix.Id])}"]
                    : songs.Select(item => ArtworkUrl(item.Song, native)).OfType<string>().Distinct(StringComparer.Ordinal).Take(4).ToArray(), mix.Section, mix.Source, mix.SourcePlaylistId);
        }).ToArray());
    }

    public async Task<ClientMixItems> ItemsAsync(HttpContext http, ProtocolExecutionContext execution, Guid mixId)
    {
        using var building = RequestTiming.Current?.Stage("build");
        var access = await libraryAccess.ResolveAsync(execution, execution.CancellationToken);
        return (await FeedReadCache.ReadAsync<ClientMixItems>(factory, cache, readCoalescer, clock, execution,
            "mix-items:" + mixId, access, http, async (request, context) =>
                await request!.RequestServices.GetRequiredService<MixReader>().ItemsCoreAsync(request!, context, mixId)))!;
    }

    private async Task<ClientMixItems> ItemsCoreAsync(HttpContext http, ProtocolExecutionContext execution, Guid mixId)
    {
        var mix = await mixes.GetAsync(execution.RequireActor().EffectiveUserId!.Value, mixId, execution.CancellationToken);
        if (mix.Kind == "external")
        {
            var publish = (bool)(await settings.GetAsync(PersonalMixService.PublishKey, execution.CancellationToken)).Value;
            if (!IsInApps(mix, publish)) throw new KeyNotFoundException();
        }
        var songs = Playable(mix, await AllowedLocalIdsAsync(execution, [mix]));
        var native = await ReadNativeAsync(http, execution, songs.Where(item => item.Song.IsLocal).Select(item => item.Song.Id).Distinct().ToArray());
        var providerIds = songs.Where(item => !item.Song.IsLocal).Select(item => item.Song.ExternalProvider)
            .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var icons = (await ProviderArtworkResolver.ResolveIconsAsync(http.RequestServices, providerIds, execution.CancellationToken))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var library = songs.Any(item => item.Song.IsLocal) ? await libraryProvider.ResolveAsync(http, execution) : null;
        (string Provider, string? Id) MetadataKey(Song song)
        {
            var parsed = LocalLibraryService.ParseExternalResource(song.Id);
            return parsed is { isExternal: true, provider: "allstarr", type: "song" }
                ? (parsed.provider!, parsed.externalId) : (song.ExternalProvider!, song.ExternalId);
        }
        var metadata = new Dictionary<(string, string?), Song?>();
        foreach (var batch in songs.Where(item => !item.Song.IsLocal && item.Song.ExternalProvider != null)
            .Select(item => MetadataKey(item.Song)).Where(key => key.Id != null).Distinct().Chunk(4))
            foreach (var entry in await Task.WhenAll(batch.Select(async key =>
                (Key: key, Song: await providers.GetStoredSongAsync(execution, key.Provider, key.Id!)))))
                metadata[entry.Key] = entry.Song;
        var streaming = new Dictionary<string, string?>();
        foreach (var batch in songs.Where(item => item.Song.ExternalProvider == "allstarr" && item.Song.ExternalId != null)
            .Select(item => item.Song.ExternalId!).Distinct().Chunk(4))
            foreach (var entry in await Task.WhenAll(batch.Select(async id =>
                (Id: id, Provider: await providers.GetStreamingProviderAsync(execution, "allstarr", id)))))
                streaming[entry.Id] = entry.Provider;
        using var serialization = RequestTiming.Current?.Stage("serialize");
        var result = new List<ClientMixItem>(songs.Count);
        foreach (var item in songs)
        {
            var song = item.Song;
            var itemId = song.Id;
            var albumId = song.AlbumId;
            var artistId = song.ArtistId ?? song.ArtistIds.FirstOrDefault();
            string? artworkUrl = null;
            ItemDetailsProvider? provider = null;
            if (song.IsLocal)
            {
                if (!native.TryGetValue(song.Id, out var root)) continue;
                provider = library;
                itemId = root.GetProperty("Id").GetString()!;
                albumId = root.TryGetProperty("AlbumId", out var album) ? album.GetString() : null;
                artworkUrl = JellyfinItemArtwork.PrimaryImageUrl(root, itemId);
                artistId = root.TryGetProperty("ArtistItems", out var artists) && artists.ValueKind == JsonValueKind.Array &&
                    artists.GetArrayLength() > 0 && artists[0].TryGetProperty("Id", out var artist) ? artist.GetString() : null;
            }
            else if (song.ExternalProvider is { } source)
            {
                if (metadata.GetValueOrDefault(MetadataKey(song)) is { } stored)
                {
                    itemId = string.IsNullOrWhiteSpace(stored.Id) ? song.Id : stored.Id;
                    albumId = ClientCatalogIds.Normalize(stored.AlbumId) ?? albumId;
                    artistId = stored.ArtistId ?? stored.ArtistIds.FirstOrDefault() ?? artistId;
                    source = stored.ExternalProvider ?? source;
                }
                if (song.ExternalProvider == "allstarr" && song.ExternalId is { } recordingId)
                    source = streaming.GetValueOrDefault(recordingId) ?? "allstarr";
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
            if (!song.IsLocal)
                artworkUrl = JellyfinItemArtwork.PrimaryImageUrl(itemId, JellyfinItemArtwork.PrimaryImageTag(itemId, false));
            result.Add(new(itemId, song.Title, song.Artist, song.Album, albumId, artworkUrl, artistId,
                song.Duration * 1000L, !song.IsLocal, provider, item.Reason));
        }
        return new(mix.Id, mix.UpdatedAt, result);
    }

    private sealed record CoverAccessEntry(bool Allowed);

    public async Task EnsureCoverAccessAsync(ProtocolExecutionContext execution, Guid mixId)
    {
        using var saved = RequestTiming.Current?.Stage("saved");
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        await using var db = await factory.CreateDbContextAsync(execution.CancellationToken);
        var key = db.FeedReadKey(execution, "mix-cover-access:" + mixId);
        if (await cache.GetAsync<CoverAccessEntry>(key) is { Allowed: true }) return;
        await readCoalescer.RunAsync(key, async () =>
        {
            if (await cache.GetAsync<CoverAccessEntry>(key) is { Allowed: true } shared) return shared;
            await mixes.EnsureCoverAccessAsync(owner, mixId, execution.CancellationToken);
            var entry = new CoverAccessEntry(true);
            if (key == db.FeedReadKey(execution, "mix-cover-access:" + mixId))
                await cache.SetAsync(key, entry, TimeSpan.FromSeconds(60));
            return entry;
        }, execution.CancellationToken);
    }

    public async Task<MixCoverInput> CoverAsync(ProtocolExecutionContext execution, Guid mixId)
    {
        await using var db = await factory.CreateDbContextAsync(execution.CancellationToken);
        var access = await libraryAccess.ResolveAsync(execution, execution.CancellationToken);
        var key = db.FeedReadKey(execution, "mix-cover-input:" + mixId, access);
        if (await CachedAsync() is { } cached) return cached;
        return await readCoalescer.RunAsync(key, async () =>
        {
            if (await CachedAsync() is { } shared) return shared;
            using var resolving = RequestTiming.Current?.Stage("resolve");
            using var building = RequestTiming.Current?.Stage("build");
            var owner = execution.RequireActor().EffectiveUserId!.Value;
            var mix = await mixes.GetCoverAsync(owner, mixId, execution.CancellationToken);
            if (mix.Kind == "external")
            {
                var publish = (bool)(await settings.GetAsync(PersonalMixService.PublishKey, execution.CancellationToken)).Value;
                if (!IsInApps(mix, publish)) throw new KeyNotFoundException();
            }
            var input = (await CoverInputsAsync(execution, [mix]))[mix.Id];
            if (mix.Kind == "external" && input.Source.Length == 0) throw new KeyNotFoundException("The synced mix's playlist is unavailable.");
            return input;
        }, CancellationToken.None);

        async Task<MixCoverInput?> CachedAsync() => await cache.GetAsync<CoverInputEntry>(key) is { } entry &&
            entry.LibraryRevision == db.FeedReadLibraryRevisionFor(execution.Actor?.EffectiveUserId) ? entry.Input : null;
    }

    private async Task RememberCoverInputsAsync(ProtocolExecutionContext execution, Dictionary<Guid, MixCoverInput> inputs)
    {
        await using var db = await factory.CreateDbContextAsync(execution.CancellationToken);
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var access = await libraryAccess.ResolveAsync(execution, execution.CancellationToken);
        foreach (var (id, input) in inputs)
        {
            await cache.SetAsync(db.FeedReadKey(execution, "mix-cover-input:" + id, access),
                new CoverInputEntry(input, db.FeedReadLibraryRevisionFor(owner)), TimeSpan.FromSeconds(60));
            await MixCoverImageService.RememberInputAsync(cache, input, owner);
        }
    }

    // The list and the cover itself describe a mix's cover the same way, so the list's versioned URL matches the image served.
    private async Task<Dictionary<Guid, MixCoverInput>> CoverInputsAsync(ProtocolExecutionContext execution, IReadOnlyList<PersonalMix> stored)
    {
        var inputs = stored.ToDictionary(mix => mix.Id, MixCoverInput.From);
        var external = stored.Where(mix => mix.Kind == "external").Select(mix => mix.Id).ToArray();
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        if (external.Length > 0)
        {
            await using var db = await factory.CreateDbContextAsync(execution.CancellationToken);
            var links = await db.PlaylistLinks.AsNoTracking().Where(item => external.Contains(item.Id) && item.OwnerUserId == owner)
                .Select(item => new { item.Id, item.SourceProviderId, item.SourcePlaylistId }).ToArrayAsync(execution.CancellationToken);
            foreach (var link in links)
                inputs[link.Id] = inputs[link.Id] with { Source = link.SourceProviderId, SourcePlaylistId = link.SourcePlaylistId };
        }
        await RememberCoverInputsAsync(execution, inputs);
        return inputs;
    }

    private async Task<HashSet<string>> AllowedLocalIdsAsync(ProtocolExecutionContext execution, IReadOnlyList<PersonalMix> stored)
    {
        using var matching = RequestTiming.Current?.Stage("match");
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
                new()
                {
                    ["ids"] = string.Join(',', batch),
                    ["limit"] = "100",
                    ["enableImages"] = "true",
                    ["fields"] = "AlbumId,AlbumPrimaryImageTag,ImageTags"
                },
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

    private static string? ArtworkUrl(Song song, Dictionary<string, JsonElement> native)
    {
        if (song.IsLocal)
            return native.TryGetValue(song.Id, out var item)
                ? JellyfinItemArtwork.PrimaryImageUrl(item, item.GetProperty("Id").GetString()!) : null;
        var itemId = ClientCatalogIds.Normalize(song.Id)!;
        return JellyfinItemArtwork.PrimaryImageUrl(itemId, JellyfinItemArtwork.PrimaryImageTag(itemId, false));
    }

    internal static bool IsInApps(PersonalMix mix, bool publish) =>
        mix.Added && (mix.Source is "spotify" or "apple-musickit" || publish && mix.Generation > 0);

    private static bool SameNativeId(string? first, string second) => first == second ||
        Guid.TryParse(first, out var firstId) && Guid.TryParse(second, out var secondId) && firstId == secondId;

    private static ClientMixState State(PersonalMix mix)
    {
        if (mix.Readiness.Ready) return ClientMixState.Ready;
        var message = mix.Readiness.Message;
        if (message.StartsWith("Matching songs", StringComparison.OrdinalIgnoreCase) || message.StartsWith("Preparing songs", StringComparison.OrdinalIgnoreCase)) return ClientMixState.Building;
        if (message.StartsWith("No playable songs", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Refresh this mix", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Fewer than", StringComparison.OrdinalIgnoreCase)) return ClientMixState.Empty;
        if (message.Contains("listening", StringComparison.OrdinalIgnoreCase) || message.Contains("history", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Play about", StringComparison.OrdinalIgnoreCase)) return ClientMixState.NeedsListening;
        return ClientMixState.Empty;
    }
}
