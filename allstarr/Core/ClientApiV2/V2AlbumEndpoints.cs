using System.Text.Json;
using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using Microsoft.AspNetCore.WebUtilities;
using static allstarr.Core.ClientApiV2.V2TrackReader;

namespace allstarr.Core.ClientApiV2;

/// <summary>Native album reads shared by the standalone route and batch dispatcher.</summary>
public sealed class V2AlbumEndpoints(V2TrackReader tracks, IProtocolProviderGateway providers, JellyfinProxyService jellyfin,
    SimilarAlbumsReader similar, V2LibraryAlbums? libraryAlbums = null)
{
    private static readonly IReadOnlySet<string> Includes = new HashSet<string>(StringComparer.Ordinal) { "tracks" };
    private static readonly IReadOnlySet<string> Views = new HashSet<string>(StringComparer.Ordinal) { "more-by-artist" };

    public async Task<V2Result> SimilarAsync(HttpContext http, ProtocolExecutionContext execution, string id, int limit)
    {
        if (limit is < 1 or > 24 || !V2Ids.TryDecode(id, V2IdKind.Album, out var decoded)) return new(400, null);
        return await V2TrackResources.ReadAsync<V2SimilarAlbumsResponse>(async () =>
        {
            var result = await similar.ReadAsync(http, execution, decoded.Source!, limit);
            return new(V2Ids.Album(decoded.Source!), result.BasedOn, result.Albums.Select(album => new V2SimilarAlbum(
                V2Ids.Album(album.Id), album.Title,
                [new(album.ArtistId is { } artist && V2Ids.IsValidSource(artist, V2IdKind.Artist) ? V2Ids.Artist(artist) : null, album.Artist)],
                album.Year, album.IsExternal, SimilarArtwork(album), album.SongCount, album.Source,
                album.Provider == null ? null : new(album.Provider.Id, album.Provider.Name, album.Provider.IconUrl, album.Provider.GlyphUrl))).ToArray());
        });
    }

    private static V2Artwork? SimilarArtwork(ClientSimilarAlbum album)
    {
        var queryStart = album.CoverUrl?.IndexOf('?') ?? -1;
        return V2ArtworkUrls.For(album.Id, queryStart < 0 ? null : QueryHelpers.ParseQuery(album.CoverUrl![queryStart..])["tag"].ToString());
    }

    public async Task<V2Result> GetAsync(HttpContext http, ProtocolExecutionContext execution, string id, string? include, string? views)
    {
        if (!V2Query.TryNames(include, Includes, out var included) || !V2Query.TryNames(views, Views, out var requested)) return new(400, null);
        if (!V2Ids.TryDecode(id, V2IdKind.Album, out var decoded)) return new(404, null);
        var source = decoded.Source!;
        var parsed = LocalLibraryService.ParseExternalResource(source);
        V2AlbumSummary? album;
        IReadOnlyList<string> songIds = [];
        Album? service = null;
        if (parsed.isExternal)
        {
            var metadata = await providers.GetStoredAlbumAsync(execution, parsed.provider!, parsed.externalId!);
            // A search or an artist page stores an album's line without its songs; those come with the album itself.
            if (metadata == null || metadata.Songs.Count == 0 && included.Contains("tracks"))
                metadata = await providers.GetAlbumAsync(execution, parsed.provider!, parsed.externalId!) ?? metadata;
            if (metadata == null) return new(404, null);
            album = Summary(source, metadata);
            service = metadata;
            songIds = SongIds(execution, metadata);
        }
        else
        {
            // The media server answers an id it cannot read with its first albums, so only the album asked for counts.
            album = (await LibraryAlbumsAsync(http, execution, new() { ["ids"] = source }))
                .FirstOrDefault(item => SameItem(V2Ids.LibrarySource(item.Id, V2IdKind.Album), source));
            if (album == null) return new(404, null);
            if (included.Contains("tracks"))
            {
                var ids = new List<string>();
                for (var offset = 0; ; offset += 1000)
                {
                    using var document = await LibraryAsync(http, execution, new()
                    {
                        ["parentId"] = source,
                        ["includeItemTypes"] = "Audio",
                        ["recursive"] = "true",
                        ["sortBy"] = "ParentIndexNumber,IndexNumber",
                        ["sortOrder"] = "Ascending",
                        ["limit"] = "1000",
                        ["startIndex"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    });
                    var page = Items(document).Select(item => String(item, "Id")).OfType<string>().ToArray();
                    ids.AddRange(page);
                    if (page.Length < 1000) break;
                }
                songIds = ids;
            }
        }
        var response = new V2AlbumResponse(album);
        V2TracksResponse? read = null;
        if (included.Contains("tracks"))
        {
            read = await tracks.ReadAsync(http, execution, songIds, false);
            response = response with { Tracks = read.Tracks, Missing = read.Missing };
        }
        // The listener's own album this one is, or could be. Its songs are read only when the library has an album of
        // this name, so the answer is the same with and without include=tracks.
        if (service is { } stored && libraryAlbums != null)
        {
            var (library, couldBe) = await libraryAlbums.IdentifyAsync(http, execution, this, stored, async () =>
            {
                if (read != null) return (stored, read.Tracks);
                var full = stored.Songs.Count > 0 ? stored
                    : await providers.GetAlbumAsync(execution, parsed.provider!, parsed.externalId!) ?? stored;
                return (full, (await tracks.ReadAsync(http, execution, SongIds(execution, full), false)).Tracks);
            });
            response = response with { LibraryAlbum = library, CouldBe = couldBe };
        }
        if (requested.Contains("more-by-artist"))
        {
            IReadOnlyList<V2AlbumSummary> more = [];
            if (V2Ids.TryDecode(album.Artists.FirstOrDefault(artist => artist.Id != null)?.Id, V2IdKind.Artist, out var artist))
            {
                var related = LocalLibraryService.ParseExternalResource(artist.Source!);
                more = related.isExternal
                    ? (await providers.GetArtistAlbumsAsync(execution, related.provider!, related.externalId!, 20))
                        .Where(item => V2Ids.IsValidSource(item.Id, V2IdKind.Album)).Select(item => Summary(item.Id, item)).ToArray()
                    : await LibraryAlbumsAsync(http, execution, new() { ["albumArtistIds"] = artist.Source!, ["recursive"] = "true" });
            }
            response = response with { Views = new(more.Where(item => item.Id != album.Id).DistinctBy(item => item.Id).Take(20).ToArray()) };
        }
        return new(200, response);
    }

    private static bool SameItem(string? left, string right) => left != null &&
        (Guid.TryParse(left, out var first) && Guid.TryParse(right, out var second) ? first == second : string.Equals(left, right, StringComparison.Ordinal));

    private static string[] SongIds(ProtocolExecutionContext execution, Album album) =>
        album.Songs.Where(song => execution.Policy?.Includes(song) != false).Select(song => song.Id).ToArray();

    internal static V2AlbumSummary Summary(string source, Album album)
    {
        var artists = album.Artists.Where(artist => !string.IsNullOrWhiteSpace(artist.Name))
            .Select(artist => new V2Ref(V2Ids.IsValidSource(artist.Id, V2IdKind.Artist) ? V2Ids.Artist(artist.Id) : null, artist.Name)).ToArray();
        if (artists.Length == 0 && !string.IsNullOrWhiteSpace(album.Artist))
            artists = [new(album.ArtistId is { } id && V2Ids.IsValidSource(id, V2IdKind.Artist) ? V2Ids.Artist(id) : null, album.Artist)];
        return new(V2Ids.Album(source), album.Title, artists, album.Year, true,
            V2ArtworkUrls.For(source, JellyfinItemArtwork.AlbumImageTag(source, false)))
        { Explicit = album.IsExplicit };
    }

    internal async Task<IReadOnlyList<V2AlbumSummary>> LibraryAlbumsAsync(HttpContext http, ProtocolExecutionContext execution,
        Dictionary<string, string> query, int limit = 20)
    {
        query["includeItemTypes"] = "MusicAlbum";
        query["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var document = await LibraryAsync(http, execution, query);
        return Items(document).Where(item => String(item, "Type") == "MusicAlbum" && String(item, "Id") is { } id && V2Ids.IsValidSource(id, V2IdKind.Album))
            .Select(item => new V2AlbumSummary(V2Ids.Album(String(item, "Id")!), String(item, "Name") ?? "", Refs(item, "AlbumArtists"),
                Int(item, "ProductionYear"), false, V2ArtworkUrls.For(String(item, "Id"), item.TryGetProperty("ImageTags", out var tags) ? String(tags, "Primary") : null))).ToArray();
    }

    internal async Task<JsonDocument?> LibraryAsync(HttpContext http, ProtocolExecutionContext execution, Dictionary<string, string> query)
    {
        var (document, status) = await jellyfin.GetJsonAsync($"Users/{Uri.EscapeDataString(execution.VerifiedBackendPrincipalId)}/Items",
            query, http.Request.Headers, execution.CancellationToken);
        if (status == 200) return document;
        document?.Dispose();
        if (status is 400 or 401 or 403 or 404) return null;
        throw new HttpRequestException("Library albums are unavailable.");
    }

    internal static IEnumerable<JsonElement> Items(JsonDocument? document) =>
        document?.RootElement.TryGetProperty("Items", out var items) == true && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];
}
