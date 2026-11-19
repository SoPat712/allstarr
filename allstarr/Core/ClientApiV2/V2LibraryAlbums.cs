using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using static allstarr.Core.ClientApiV2.V2TrackReader;

namespace allstarr.Core.ClientApiV2;

/// <summary>
/// Says which library album a service album is. Albums join on proof only: songs of the service album that are the
/// listener's own songs on one library album of that title, or the same artist, title, edition and song count. A library
/// album that only shares the artist and title is offered as one it could be, such as another edition.
/// </summary>
public sealed class V2LibraryAlbums(ListeningLibraries? libraries = null)
{
    private const int Candidates = 20;
    private const int Offered = 5;

    internal async Task<(V2LibraryAlbumRef? Library, IReadOnlyList<V2LibraryAlbumRef>? CouldBe)> IdentifyAsync(HttpContext http,
        ProtocolExecutionContext execution, V2AlbumEndpoints albums, Album album, Func<Task<IReadOnlyList<V2Track>>> tracks)
    {
        var title = FuzzyMatcher.StripDecorators(album.Title ?? "");
        var artistKey = FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(album.Artist).FirstOrDefault() ??
            album.Artists.FirstOrDefault()?.Name ?? "");
        if (string.IsNullOrWhiteSpace(title) || artistKey.Length == 0) return (null, null);
        var owned = new List<(Album Album, string? Tag)>();
        try
        {
            // Only the libraries the listener chose to listen from, as search reads them.
            var narrowed = libraries == null ? null : await libraries.NarrowedAsync(execution, execution.CancellationToken);
            IReadOnlyList<string?> chosen = narrowed is { Count: > 0 } ? [.. narrowed] : [null];
            foreach (var library in chosen)
            {
                var query = new Dictionary<string, string>
                {
                    ["includeItemTypes"] = "MusicAlbum",
                    ["recursive"] = "true",
                    ["searchTerm"] = title,
                    ["fields"] = "ChildCount",
                    ["limit"] = Candidates.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
                if (library != null) query["parentId"] = library;
                using var document = await albums.LibraryAsync(http, execution, query);
                foreach (var item in V2AlbumEndpoints.Items(document))
                {
                    if (String(item, "Type") != "MusicAlbum" || String(item, "Id") is not { } id || !V2Ids.IsValidSource(id, V2IdKind.Album) ||
                        owned.Any(known => known.Album.Id == id) ||
                        String(item, "Name") is not { Length: > 0 } name || !MusicCatalogMerge.IsSameAlbum(album.Title, name)) continue;
                    var artist = String(item, "AlbumArtist") ?? Refs(item, "AlbumArtists").FirstOrDefault()?.Name ?? "";
                    if (!MusicCatalogMerge.LeadArtistMatches(artist, artistKey)) continue;
                    owned.Add((new Album
                    {
                        Id = id,
                        Title = name,
                        Artist = artist,
                        IsLocal = true,
                        Year = Int(item, "ProductionYear"),
                        SongCount = Int(item, "ChildCount") is > 0 and var count ? count : null,
                        IsExplicit = String(item, "OfficialRating")?.ToLowerInvariant() switch { "explicit" => true, "clean" or "cleaned" => false, _ => null }
                    }, item.TryGetProperty("ImageTags", out var tags) && tags.ValueKind == JsonValueKind.Object ? String(tags, "Primary") : null));
                }
            }
        }
        // The library not answering leaves the album without a pointer; it never fails the album read.
        catch (Exception error) when (error is HttpRequestException or JsonException) { return (null, null); }
        if (owned.Count == 0) return (null, null);
        V2LibraryAlbumRef Shape((Album Album, string? Tag) item) => new(V2Ids.Album(item.Album.Id), item.Album.Title,
            V2ArtworkUrls.For(item.Album.Id, item.Tag))
        { Year = item.Album.Year, SongCount = item.Album.SongCount };

        // The listener's own songs among this album's, by recording or ISRC, name their library album outright.
        var bySongs = (await tracks()).Where(track => track.LibraryItemId != null && track.Album?.LibraryId != null)
            .GroupBy(track => Key(track.Album!.LibraryId!), StringComparer.Ordinal)
            .Select(group => (Owned: owned.FirstOrDefault(item => Key(item.Album.Id) == group.Key), Songs: group.Count()))
            .Where(item => item.Owned.Album != null).OrderByDescending(item => item.Songs).ToArray();
        if (bySongs.Length == 1) return (Shape(bySongs[0].Owned) with { By = "songs", OwnedSongs = bySongs[0].Songs }, null);
        var same = owned.Where(item => MusicAlbumReleases.Compatible(item.Album, album)).ToArray();
        if (bySongs.Length == 0 && same.Length == 1) return (Shape(same[0]) with { By = "album" }, null);
        // Several library albums fit, or only the name does: a person can tell them apart, so they are offered, the closest first.
        return (null, owned.OrderByDescending(item => bySongs.FirstOrDefault(entry => entry.Owned.Album == item.Album).Songs)
            .ThenByDescending(item => same.Contains(item)).Take(Offered)
            .Select(item => Shape(item) with { Reason = Reason(item.Album, album, same.Length > 1 || bySongs.Length > 1) }).ToArray());
    }

    private static string Key(string id) => Guid.TryParse(id, out var parsed) ? parsed.ToString("N") : id;

    // One line under a name on a phone.
    private static string Reason(Album owned, Album album, bool several)
    {
        var here = album.SongCount is > 0 ? album.SongCount : album.Songs.Count > 0 ? album.Songs.Count : null;
        if (owned.SongCount is > 0 && here is > 0 && owned.SongCount != here)
            return $"Same title and artist. {owned.SongCount} songs there, {here} here.";
        return several ? "Same album. Your library has it more than once." : "Same title and artist. May be another edition.";
    }
}
