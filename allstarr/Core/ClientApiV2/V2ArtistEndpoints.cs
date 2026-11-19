using System.Text.Json;
using allstarr.Core.ClientApi;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using static allstarr.Core.ClientApiV2.V2TrackReader;

namespace allstarr.Core.ClientApiV2;

/// <summary>Native artist reads shared by the standalone route and batch dispatcher.</summary>
public sealed class V2ArtistEndpoints(V2TrackReader tracks, IProtocolProviderGateway providers, V2AlbumEndpoints albums, ReleaseReader releases,
    V2LibraryArtists? libraryArtists = null, StreamingArtistCatalog? catalog = null)
{
    private const int AlbumLimit = 50;
    private const int TrackLimit = 20;
    private static readonly IReadOnlySet<string> Includes = new HashSet<string>(StringComparer.Ordinal) { "albums", "tracks" };

    public async Task<V2Result> GetAsync(HttpContext http, ProtocolExecutionContext execution, string id, string? include)
    {
        if (!V2Query.TryNames(include, Includes, out var included)) return new(400, null);
        if (!V2Ids.TryDecode(id, V2IdKind.Artist, out var decoded)) return new(404, null);
        return await V2TrackResources.ReadAsync<V2ArtistResponse>(async () =>
        {
            var source = decoded.Source!;
            var parsed = LocalLibraryService.ParseExternalResource(source);
            V2ArtistSummary artist;
            IReadOnlyList<V2AlbumSummary>? artistAlbums = null;
            IReadOnlyList<string> songIds = [];
            var following = await releases.IsFollowingAsync(execution, source);
            if (parsed.isExternal)
            {
                if (parsed.type != "artist") return null;
                var metadata = await providers.GetStoredArtistAsync(execution, parsed.provider!, parsed.externalId!) ??
                    await providers.GetArtistAsync(execution, parsed.provider!, parsed.externalId!);
                if (string.IsNullOrWhiteSpace(metadata?.Name)) return null;
                artist = new(V2Ids.Artist(source), metadata.Name, true,
                    V2ArtworkUrls.For(source, JellyfinItemArtwork.PrimaryImageTag(source, false)), metadata.Genres, following)
                { Provider = libraryArtists?.ServiceProvider(V2Ids.Artist(source), V2IdKind.Artist) };
                // A service artist's page also lists the same artist's albums from the listener's other services.
                if (included.Contains("albums"))
                    artistAlbums = (catalog != null
                            ? await catalog.AlbumsAsync(execution, parsed.provider!, parsed.externalId!, Task.FromResult<Artist?>(metadata))
                            : await providers.GetArtistAlbumsAsync(execution, parsed.provider!, parsed.externalId!, AlbumLimit))
                        .Where(item => V2Ids.IsValidSource(item.Id, V2IdKind.Album)).Select(item => V2AlbumEndpoints.Summary(item.Id, item))
                        .DistinctBy(item => item.Id).Take(AlbumLimit).ToArray();
                if (included.Contains("tracks"))
                    songIds = (await providers.GetArtistTracksAsync(execution, parsed.provider!, parsed.externalId!, TrackLimit))
                        .Where(song => execution.Policy?.Includes(song) != false).Select(song => song.Id).ToArray();
            }
            else
            {
                using (var document = await albums.LibraryAsync(http, execution, new() { ["ids"] = source }))
                {
                    var item = V2AlbumEndpoints.Items(document).FirstOrDefault(entry => String(entry, "Type") == "MusicArtist");
                    if (item.ValueKind != JsonValueKind.Object || String(item, "Name") is not { Length: > 0 } name) return null;
                    artist = new(V2Ids.Artist(source), name, false,
                        V2ArtworkUrls.For(source, item.TryGetProperty("ImageTags", out var tags) ? String(tags, "Primary") : null), Genres(item), following);
                }
                if (included.Contains("albums"))
                    artistAlbums = await albums.LibraryAlbumsAsync(http, execution, new()
                    {
                        ["albumArtistIds"] = source,
                        ["recursive"] = "true",
                        ["sortBy"] = "ProductionYear,SortName",
                        ["sortOrder"] = "Descending,Ascending"
                    }, AlbumLimit);
                if (included.Contains("tracks"))
                {
                    using var document = await albums.LibraryAsync(http, execution, new()
                    {
                        ["artistIds"] = source,
                        ["includeItemTypes"] = "Audio",
                        ["recursive"] = "true",
                        ["sortBy"] = "PlayCount,SortName",
                        ["sortOrder"] = "Descending,Ascending",
                        ["limit"] = TrackLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    });
                    songIds = V2AlbumEndpoints.Items(document).Select(item => String(item, "Id")).OfType<string>().ToArray();
                }
            }
            var response = new V2ArtistResponse(artist) { Albums = artistAlbums };
            if (!included.Contains("tracks")) return response;
            var read = await tracks.ReadAsync(http, execution, songIds, false);
            return response with { Tracks = read.Tracks, Missing = read.Missing };
        });
    }

    private static IReadOnlyList<string> Genres(JsonElement item) =>
        item.TryGetProperty("Genres", out var genres) && genres.ValueKind == JsonValueKind.Array
            ? genres.EnumerateArray().Where(genre => genre.ValueKind == JsonValueKind.String).Select(genre => genre.GetString()!).ToArray() : [];
}
