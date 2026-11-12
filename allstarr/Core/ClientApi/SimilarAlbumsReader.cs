using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApi;

// Albums by the artists a source relates to an album's artist, from the library when it has one of theirs.
public sealed class SimilarAlbumsReader(IProtocolProviderGateway providers, SavedLibraryReader saved,
    JellyfinProxyService jellyfin, IProviderRegistry registry, LibraryProviderResolver libraryProvider, LibraryArtistIndex? artistIndex = null, TimeProvider? timeProvider = null, IApplicationCache? applicationCache = null,
    IDbContextFactory<AllstarrDbContext>? contexts = null, IBackendLibraryAccessResolver? libraryAccess = null)
{
    private const int RelatedArtists = 24;
    private const string RelatedArtistsSource = "deezer";
    private const int Lookups = 4;
    private const int ArtistAlbumSample = 24;
    internal const int MaxCacheEntries = 500;
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);
    private static readonly object NativeItemsKey = new();
    private static readonly ConcurrentDictionary<string, CachedSimilar> Recent = new();
    private sealed record CachedSimilar(DateTimeOffset At, ClientSimilarAlbums Value, Dictionary<string, JsonElement> Native);

    private sealed record Seed(string Title, string Artist, Dictionary<string, string> ArtistIds);

    public async Task<ClientSimilarAlbums> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string itemId, int limit)
    {
        limit = Math.Clamp(limit, 1, 24);
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var clock = timeProvider ?? TimeProvider.System;
        var metadata = providers.GetProviderOrder(ProviderCapabilityKind.Metadata) ?? [];
        var requested = http.Request.Query["Fields"].ToString();
        var fields = http.Request.Path.StartsWithSegments("/allstarr") ? "ChildCount,ProductionYear,AlbumArtist" :
            string.IsNullOrWhiteSpace(requested) ? "PrimaryImageAspectRatio,ProductionYear,ChildCount" : requested;
        var access = libraryAccess == null ? null : await libraryAccess.ResolveAsync(execution, execution.CancellationToken);
        await using var db = contexts == null ? null : await contexts.CreateDbContextAsync(execution.CancellationToken);
        var libraryKey = db?.FeedReadKey(execution, "library-similar", access);
        var key = CacheKey(owner, execution.BackendInstanceId, itemId, limit, execution.Policy, metadata) +
            $"|v2|{execution.Protocol}|{execution.VerifiedBackendPrincipalId}|{fields}|{libraryKey}";
        if (access?.Succeeded != false && Recent.TryGetValue(key, out var cached) && clock.GetUtcNow() - cached.At < FreshFor)
        {
            RequestTiming.Current?.NoteCache(true);
            http.Items[NativeItemsKey] = cached.Native;
            return cached.Value;
        }

        var persistedKey = CacheKeyBuilder.BuildProtocolMetadataKey("allstarr", key, "similar-albums", itemId);
        if (access?.Succeeded != false && applicationCache != null && await applicationCache.GetAsync<CachedSimilar>(persistedKey) is { } retained &&
            clock.GetUtcNow() - retained.At < FreshFor)
        {
            RequestTiming.Current?.NoteCache(true);
            http.Items[NativeItemsKey] = retained.Native;
            return retained.Value;
        }
        RequestTiming.Current?.NoteCache(false);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(2700), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(execution.CancellationToken, timeout.Token);
        var token = linked.Token;
        var original = execution;
        execution = new ProtocolExecutionContext(original.Protocol, original.BackendInstanceId, original.VerifiedBackendPrincipalId,
            original.Principal, original.CorrelationId, clock.GetUtcNow().AddMilliseconds(2700), token, original.Client)
        { Policy = original.Policy, IsReadOnly = false };
        Seed seed;
        string? basedOn;
        IReadOnlyList<Artist> related;
        ItemDetailsProvider library;
        JsonElement[] libraryAlbums;
        (string Id, string Name)[]? libraryArtists;
        bool complete;
        try
        {
            var libraryTask = LibraryAlbumsAsync(http, execution, fields);
            var artistsTask = LibraryArtistsAsync(http, execution);
            seed = await SeedAsync(http, execution, itemId, libraryTask).WaitAsync(token);
            var relatedTask = RelatedArtistsAsync(execution, seed);
            var providerTask = libraryProvider.ResolveAsync(http, execution);
            (basedOn, related) = await relatedTask.WaitAsync(token);
            var rows = await libraryTask.WaitAsync(token);
            libraryAlbums = rows ?? [];
            libraryArtists = await artistsTask.WaitAsync(token);
            complete = rows != null && libraryArtists != null &&
                (rows.Length == 0 || libraryArtists.Length > 0 && rows.All(album => AlbumArtists(album).Any()));
            library = await providerTask.WaitAsync(token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !original.CancellationToken.IsCancellationRequested)
        { return new(itemId, null, []); }
        var icons = new ConcurrentDictionary<string, ItemDetailsProvider?>(StringComparer.Ordinal);
        using var slots = new SemaphoreSlim(Lookups);
        var work = related.Take(Math.Min(RelatedArtists, limit * 2)).Select(async artist =>
        {
            try { await slots.WaitAsync(token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return null; }
            try { return await PickAsync(http, execution, artist, library, icons, libraryAlbums, libraryArtists).WaitAsync(token); }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException ||
                exception is OperationCanceledException && timeout.IsCancellationRequested)
            { return null; }
            finally { slots.Release(); }
        }).ToArray();
        var picks = await Task.WhenAll(work);
        var albums = picks.OfType<ClientSimilarAlbum>()
            .Where(album => !SameName(album.Artist, seed.Artist))
            .DistinctBy(album => album.Id).Take(limit).ToArray();
        var result = new ClientSimilarAlbums(itemId, basedOn, albums);
        var selected = albums.Where(album => !album.IsExternal).Select(album => album.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var native = libraryAlbums.Where(album => album.TryGetProperty("Id", out var id) && selected.Contains(id.GetString() ?? ""))
            .DistinctBy(album => album.GetProperty("Id").GetString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(album => album.GetProperty("Id").GetString()!, StringComparer.OrdinalIgnoreCase);
        http.Items[NativeItemsKey] = native;
        if (!token.IsCancellationRequested && complete && access?.Succeeded != false && libraryKey == db?.FeedReadKey(execution, "library-similar", access))
        {
            var entry = new CachedSimilar(clock.GetUtcNow(), result, native);
            Recent[key] = entry;
            TrimCache(clock.GetUtcNow());
            if (applicationCache != null) await applicationCache.SetAsync(persistedKey, entry, FreshFor);
        }
        return result;
    }

    internal static IReadOnlyDictionary<string, JsonElement> NativeAlbums(HttpContext http) =>
        http.Items.TryGetValue(NativeItemsKey, out var value) && value is Dictionary<string, JsonElement> rows ? rows : new Dictionary<string, JsonElement>();

    // Clean-only and the metadata sources this read consults are part of the answer, so a settings change misses.
    internal static string CacheKey(Guid owner, string backendId, string itemId, int limit,
        EffectiveProviderPolicySnapshot? policy, IReadOnlyList<string> metadataOrder) =>
        $"{owner:N}|{backendId}|{itemId}|{limit}|{SettingsFingerprint(policy, metadataOrder)}";

    internal static string SettingsFingerprint(EffectiveProviderPolicySnapshot? policy, IReadOnlyList<string> metadataOrder)
    {
        var clean = policy?.Preferences.ExplicitFilter == "CleanOnly" ? "1" : "0";
        var consulted = policy?.ApplyAutomaticProviderAvailability(ProviderCapabilityKind.Metadata, metadataOrder) ?? metadataOrder;
        var deezer = policy == null ? "Automatic" : policy.GetProviderMode(RelatedArtistsSource).ToString();
        return $"{clean}|{deezer}|{string.Join(',', consulted)}";
    }

    private static void TrimCache(DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        foreach (var stale in Recent.Where(item => now - item.Value.At >= FreshFor).Select(item => item.Key).ToArray())
            Recent.TryRemove(stale, out _);
        var overflow = Recent.Count - MaxCacheEntries;
        if (overflow <= 0) return;
        foreach (var oldest in Recent.OrderBy(item => item.Value.At).Take(overflow).Select(item => item.Key).ToArray())
            Recent.TryRemove(oldest, out _);
    }

    private async Task<Seed> SeedAsync(HttpContext http, ProtocolExecutionContext execution, string itemId, Task<JsonElement[]?> library)
    {
        var parsed = LocalLibraryService.ParseExternalResource(itemId);
        if (parsed.isExternal)
        {
            if (parsed.type != "album") throw new ArgumentException("Only albums have similar albums.");
            var album = await providers.GetAlbumAsync(execution, parsed.provider!, parsed.externalId!).WaitAsync(execution.CancellationToken) ??
                await saved.AlbumAsync(execution, $"ext-{parsed.provider}-album-{parsed.externalId}", execution.CancellationToken) ??
                throw new KeyNotFoundException();
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            var artist = LocalLibraryService.ParseExternalResource(album.ArtistId ?? "");
            if (artist is { isExternal: true, type: "artist" }) ids[artist.provider!] = artist.externalId!;
            return new(album.Title, album.Artist, ids);
        }
        var existing = (await library ?? []).FirstOrDefault(album => album.TryGetProperty("Id", out var id) &&
            string.Equals(id.GetString(), itemId, StringComparison.OrdinalIgnoreCase));
        if (existing.ValueKind == JsonValueKind.Object) return LibrarySeed(existing);
        var (body, status) = await jellyfin.GetJsonAsync(
            $"Users/{Uri.EscapeDataString(execution.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(itemId)}",
            clientHeaders: http.Request.Headers, cancellationToken: execution.CancellationToken);
        using (body)
        {
            if (status is 400 or 404) throw new KeyNotFoundException();
            if (status is 401 or 403) throw new UnauthorizedAccessException();
            if (status != 200 || body == null) throw new HttpRequestException("Library details are unavailable.");
            return LibrarySeed(body.RootElement);
        }
    }

    private static Seed LibrarySeed(JsonElement root)
    {
        if (!root.TryGetProperty("Type", out var type) || type.GetString() != "MusicAlbum")
            throw new ArgumentException("Only albums have similar albums.");
        var name = root.TryGetProperty("AlbumArtist", out var albumArtist) ? albumArtist.GetString() : null;
        return new(root.TryGetProperty("Name", out var title) ? title.GetString() ?? "" : "", name ?? "", new(StringComparer.Ordinal));
    }

    private async Task<JsonElement[]?> LibraryAlbumsAsync(HttpContext http, ProtocolExecutionContext execution, string fields)
    {
        using var stage = RequestTiming.Current?.Stage("library");
        try
        {
            var (body, status) = await jellyfin.GetJsonAsync($"Users/{Uri.EscapeDataString(execution.VerifiedBackendPrincipalId)}/Items", new()
            {
                ["includeItemTypes"] = "MusicAlbum",
                ["recursive"] = "true",
                ["sortBy"] = "PlayCount,CommunityRating,ProductionYear",
                ["sortOrder"] = "Descending",
                ["fields"] = fields,
                ["enableImages"] = "true"
            }, http.Request.Headers, execution.CancellationToken);
            using (body)
                return status == 200 && body?.RootElement.TryGetProperty("Items", out var items) == true && items.ValueKind == JsonValueKind.Array &&
                    (!body.RootElement.TryGetProperty("TotalRecordCount", out var count) || !count.TryGetInt32(out var total) || total <= items.GetArrayLength())
                    ? items.EnumerateArray().Select(item => item.Clone()).ToArray() : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException) { return null; }
    }

    private async Task<(string Id, string Name)[]?> LibraryArtistsAsync(HttpContext http, ProtocolExecutionContext execution)
    {
        try
        {
            var (body, status) = await jellyfin.GetJsonAsync("Artists/AlbumArtists", new()
            {
                ["userId"] = execution.VerifiedBackendPrincipalId,
                ["enableImages"] = "false"
            }, http.Request.Headers, execution.CancellationToken);
            using (body)
                return status == 200 && body?.RootElement.TryGetProperty("Items", out var items) == true && items.ValueKind == JsonValueKind.Array &&
                    (!body.RootElement.TryGetProperty("TotalRecordCount", out var count) || !count.TryGetInt32(out var total) || total <= items.GetArrayLength())
                    ? items.EnumerateArray().Where(item => item.TryGetProperty("Id", out _) && item.TryGetProperty("Name", out _))
                        .Select(item => (item.GetProperty("Id").GetString() ?? "", item.GetProperty("Name").GetString() ?? "")).ToArray() : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException) { return null; }
    }

    // The first source in the listener's order that relates other artists to this one.
    private async Task<(string? Source, IReadOnlyList<Artist> Artists)> RelatedArtistsAsync(ProtocolExecutionContext execution, Seed seed)
    {
        using var stage = RequestTiming.Current?.Stage("sources");
        if (string.IsNullOrWhiteSpace(seed.Artist)) return (null, []);
        // Deezer is the service that publishes related artists, so it is asked right after the album's own service,
        // then the household's other song-details sources the listener allows.
        var available = providers.GetProviderOrder(ProviderCapabilityKind.Metadata) ?? [];
        var order = execution.Policy?.ApplyAutomaticProviderAvailability(ProviderCapabilityKind.Metadata, available) ?? available;
        var sources = seed.ArtistIds.Keys.Append(RelatedArtistsSource).Concat(order)
            .Where(source => execution.Policy?.IsAutomatic(source) != false)
            .Distinct(StringComparer.Ordinal).Take(4);
        foreach (var source in sources)
        {
            try
            {
                foreach (var id in await ArtistCandidatesAsync(execution, seed, source))
                {
                    var related = await providers.GetSimilarArtistsAsync(execution, source, id, RelatedArtists).WaitAsync(execution.CancellationToken);
                    if (related.Count > 0) return (source, related);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException) { }
        }
        return (null, []);
    }

    // Many artists share a name, and a service's artist search can rank an obscure namesake first. The artist of the
    // same album on that service is the surest match; exact-name artists follow.
    private async Task<IReadOnlyList<string>> ArtistCandidatesAsync(ProtocolExecutionContext execution, Seed seed, string source)
    {
        if (seed.ArtistIds.GetValueOrDefault(source) is { } known) return [known];
        if (artistIndex != null && execution.Actor?.EffectiveUserId is { } owner &&
            (await artistIndex.ReadAsync(owner, seed.Artist, execution.CancellationToken)).FirstOrDefault(item => item.ProviderId == source) is { } indexed)
            return [indexed.ArtistId];
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(seed.Title))
        {
            try
            {
                var albums = await providers.SearchAsync(execution, $"{seed.Artist} {seed.Title}", 0, 5, 0, source).WaitAsync(execution.CancellationToken);
                var album = albums?.Albums.FirstOrDefault(item => item.ExternalProvider == source &&
                    SameName(item.Artist, seed.Artist) && SameName(item.Title, seed.Title));
                var artist = LocalLibraryService.ParseExternalResource(album?.ArtistId ?? "");
                if (artist is { isExternal: true, type: "artist" } && artist.provider == source) candidates.Add(artist.externalId!);
                else if (!string.IsNullOrWhiteSpace(album?.ArtistId) && !album.ArtistId.StartsWith("ext-", StringComparison.Ordinal)) candidates.Add(album.ArtistId);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException) { }
        }
        var found = await providers.SearchAsync(execution, seed.Artist, 0, 0, 10, source).WaitAsync(execution.CancellationToken);
        candidates.AddRange((found?.Artists ?? []).Where(item => item.ExternalProvider == source && SameName(item.Name, seed.Artist))
            .Select(item => item.ExternalId).OfType<string>().Take(5));
        return candidates.Distinct(StringComparer.Ordinal).ToArray();
    }

    private async Task<ClientSimilarAlbum?> PickAsync(HttpContext http, ProtocolExecutionContext execution, Artist artist,
        ItemDetailsProvider library, ConcurrentDictionary<string, ItemDetailsProvider?> icons, IReadOnlyList<JsonElement> libraryAlbums,
        (string Id, string Name)[]? libraryArtists)
    {
        if (await LibraryAlbumAsync(http, execution, artist.Name, libraryAlbums, libraryArtists) is { } owned) return owned with { Provider = library };
        if (artist.ExternalProvider is not { } source || artist.ExternalId is not { } artistId) return null;
        var albums = MusicAlbumReleases.Collapse(await providers.GetArtistAlbumsAsync(execution, source, artistId, ArtistAlbumSample).WaitAsync(execution.CancellationToken))
            .OrderByDescending(item => item.ReleaseDate ?? item.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal)
            .ToArray();
        var album = albums.FirstOrDefault(item => item.ReleaseType is not ("single" or "Single" or "compilation" or "Compilation")) ??
            albums.FirstOrDefault();
        if (album == null) return null;
        var id = ClientCatalogIds.Normalize(string.IsNullOrWhiteSpace(album.Id)
            ? ClientCatalogIds.Resource(source, ProviderResourceKind.Album, album.ExternalId ?? "")
            : album.Id)!;
        var provider = album.ExternalProvider ?? source;
        if (!icons.TryGetValue(provider, out var icon))
        {
            var resolved = registry.TryGet(provider, out var descriptor)
                ? (await ProviderArtworkResolver.ResolveIconsAsync(http.RequestServices, [provider], execution.CancellationToken))
                    .GetValueOrDefault(provider) is { } found ? found.Provider(provider, descriptor!.DisplayName) : new(provider, descriptor!.DisplayName, null)
                : null;
            icons[provider] = icon = resolved;
        }
        return new(id, album.Title, string.IsNullOrWhiteSpace(album.Artist) ? artist.Name : album.Artist,
            ClientCatalogIds.Normalize(album.ArtistId) ?? ClientCatalogIds.Resource(source, ProviderResourceKind.Artist, artistId),
            album.Year, album.SongCount, JellyfinItemArtwork.PrimaryImageUrl(id, JellyfinItemArtwork.AlbumImageTag(id, false)),
            provider, true, icon);
    }

    // The listener's best-rated album by this artist, if the library has one.
    private async Task<ClientSimilarAlbum?> LibraryAlbumAsync(HttpContext http, ProtocolExecutionContext execution, string artist,
        IReadOnlyList<JsonElement> libraryAlbums, (string Id, string Name)[]? libraryArtists)
    {
        var credits = libraryArtists ?? [];
        var matches = credits.Where(item => SameName(item.Name, artist)).ToArray();
        if (matches.Length == 1 && string.Equals(matches[0].Name, artist, StringComparison.OrdinalIgnoreCase) &&
            !credits.Any(item => item.Id != matches[0].Id && item.Name.Contains(artist, StringComparison.OrdinalIgnoreCase)))
        {
            var best = libraryAlbums.FirstOrDefault(album => AlbumArtists(album).Any(item => item.Id == matches[0].Id));
            if (best.ValueKind == JsonValueKind.Object) return LibraryAlbum(best, artist, matches[0].Id);
        }
        else if (matches.Length == 0 && libraryArtists is { Length: > 0 } && libraryAlbums.Count > 0 &&
            libraryAlbums.All(album => AlbumArtists(album).Any()))
            return null;

        // Ambiguous names keep the server's search limit and first exact match.
        var user = Uri.EscapeDataString(execution.VerifiedBackendPrincipalId);
        var (artists, status) = await jellyfin.GetJsonAsync("Artists/AlbumArtists", new()
        {
            ["userId"] = execution.VerifiedBackendPrincipalId,
            ["searchTerm"] = artist,
            ["limit"] = "5",
            ["enableImages"] = "false"
        }, http.Request.Headers, execution.CancellationToken);
        string? artistId;
        using (artists)
        {
            if (status != 200 || artists == null || !artists.RootElement.TryGetProperty("Items", out var items) ||
                items.ValueKind != JsonValueKind.Array) return null;
            artistId = items.EnumerateArray().FirstOrDefault(item => item.TryGetProperty("Name", out var name) &&
                SameName(name.GetString(), artist)) is { ValueKind: JsonValueKind.Object } match ? match.GetProperty("Id").GetString() : null;
        }
        if (artistId == null) return null;
        var ranked = libraryAlbums.FirstOrDefault(album => AlbumArtists(album).Any(item => item.Id == artistId));
        if (ranked.ValueKind == JsonValueKind.Object) return LibraryAlbum(ranked, artist, artistId);
        var (albums, albumStatus) = await jellyfin.GetJsonAsync($"Users/{user}/Items", new()
        {
            ["includeItemTypes"] = "MusicAlbum",
            ["recursive"] = "true",
            ["albumArtistIds"] = artistId,
            ["sortBy"] = "PlayCount,CommunityRating,ProductionYear",
            ["sortOrder"] = "Descending",
            ["limit"] = "1",
            ["fields"] = "ChildCount,ProductionYear,AlbumArtist",
            ["enableImages"] = "true"
        }, http.Request.Headers, execution.CancellationToken);
        using (albums)
        {
            if (albumStatus != 200 || albums == null || !albums.RootElement.TryGetProperty("Items", out var items) ||
                items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0) return null;
            return LibraryAlbum(items[0], artist, artistId);
        }
    }

    private static IEnumerable<(string Id, string Name)> AlbumArtists(JsonElement album) =>
        album.TryGetProperty("AlbumArtists", out var credits) && credits.ValueKind == JsonValueKind.Array
            ? credits.EnumerateArray().Where(item => item.TryGetProperty("Id", out _) && item.TryGetProperty("Name", out _))
                .Select(item => (item.GetProperty("Id").GetString() ?? "", item.GetProperty("Name").GetString() ?? "")) : [];

    private static ClientSimilarAlbum LibraryAlbum(JsonElement album, string artist, string artistId)
    {
        var id = album.GetProperty("Id").GetString()!;
        return new(id, album.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
                album.TryGetProperty("AlbumArtist", out var albumArtist) ? albumArtist.GetString() ?? artist : artist, artistId,
                album.TryGetProperty("ProductionYear", out var year) && year.TryGetInt32(out var value) ? value : null,
                album.TryGetProperty("ChildCount", out var count) && count.TryGetInt32(out var songs) ? songs : null,
                JellyfinItemArtwork.PrimaryImageUrl(album, id), "library", false, null);
    }

    private static bool SameName(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
        FuzzyMatcher.NormalizeForMatching(first) == FuzzyMatcher.NormalizeForMatching(second);

    internal static int CacheCountForTests() => Recent.Count;
    internal static bool ContainsCacheForTests(string key) => Recent.ContainsKey(key);
    internal static void PutCacheForTests(string key, DateTimeOffset at) => Recent[key] = new(at, new(key, null, []), new());
    internal static void RemoveCacheForTests(string key) => Recent.TryRemove(key, out _);
    internal static void TrimCacheForTests() => TrimCache();
}
