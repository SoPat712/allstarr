using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using static allstarr.Core.ClientApiV2.V2TrackReader;

namespace allstarr.Core.ClientApiV2;

/// <summary>
/// Says which library album a service album is. Albums join on their songs only: songs of the service album that are
/// the listener's own songs on a library album of that title, by recording, or every song of the shorter of the two
/// found on the other by title, version and length. Album titles and song counts alone prove nothing (an artist can
/// have two albums of one name), so a library album that only shares the artist and title is offered as one it could be.
/// </summary>
public sealed class V2LibraryAlbums(ListeningLibraries? libraries = null)
{
    private const int Candidates = 20;
    private const int Offered = 5;
    // Copies of one song differ by seconds between pressings and services, as the matcher allows for library copies.
    private const int SameSongSeconds = 12;

    internal sealed record LibrarySong(string Title, string Artist, long? Milliseconds, string? Id = null);

    private sealed record Candidate(Album Album, string? Tag)
    {
        /// <summary>Songs of the service album that are the listener's own songs on this album, by recording.</summary>
        public int Owned { get; set; }

        /// <summary>This album's songs found on the service album by title and length, and how many it has.</summary>
        public int Matched { get; set; }

        public int? Songs { get; set; }

        /// <summary>Whether its songs were read, so they could be compared.</summary>
        public bool Compared { get; set; }

        public bool Proven { get; set; }

        /// <summary>Each shared song: the row's id in the service album's track list, and the listener's own song.</summary>
        public Dictionary<string, string> Pairs { get; } = new(StringComparer.Ordinal);
    }

    internal async Task<(V2LibraryAlbumRef? Library, IReadOnlyList<V2LibraryAlbumRef>? CouldBe)> IdentifyAsync(HttpContext http,
        ProtocolExecutionContext execution, V2AlbumEndpoints albums, Album album, Func<Task<(Album Album, IReadOnlyList<V2Track> Tracks)>> songs)
    {
        var title = FuzzyMatcher.StripDecorators(album.Title ?? "");
        var artistKey = FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(album.Artist).FirstOrDefault() ??
            album.Artists.FirstOrDefault()?.Name ?? "");
        if (string.IsNullOrWhiteSpace(title) || artistKey.Length == 0) return (null, null);
        var owned = new List<Candidate>();
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
                    owned.Add(new(new Album
                    {
                        Id = id,
                        Title = name,
                        Artist = artist,
                        IsLocal = true,
                        Year = Int(item, "ProductionYear"),
                        SongCount = Int(item, "ChildCount") is > 0 and var count ? count : null
                    }, item.TryGetProperty("ImageTags", out var tags) && tags.ValueKind == JsonValueKind.Object ? String(tags, "Primary") : null));
                }
            }
            if (owned.Count == 0) return (null, null);

            // The album as the service gives it, with its songs. The listener's own songs among them, by recording or
            // ISRC, name their library album outright.
            var (full, tracks) = await songs();
            foreach (var group in tracks.Where(track => track.LibraryItemId != null && track.Album?.LibraryId != null)
                         .GroupBy(track => Key(track.Album!.LibraryId!), StringComparer.Ordinal))
                if (owned.FirstOrDefault(item => Key(item.Album.Id) == group.Key) is { } mine)
                {
                    mine.Owned = group.Count();
                    foreach (var track in group) mine.Pairs[track.Id] = track.LibraryItemId!;
                }
            // A row's id as the track list gives it, for a song the service names by its own id.
            string Row(Song song) => tracks.FirstOrDefault(track => track.Id == song.Id || track.Aliases.Contains(song.Id, StringComparer.Ordinal))?.Id ??
                V2Ids.TrackSource(song.Id);
            // Otherwise the songs themselves are compared: every song of the shorter album must be on the other.
            foreach (var item in owned.Take(Offered))
            {
                var own = await LibrarySongsAsync(http, execution, albums, item.Album.Id);
                item.Songs = own.Count > 0 ? own.Count : item.Album.SongCount;
                item.Compared = own.Count > 0;
                var left = full.Songs.ToList();
                foreach (var song in own)
                    if (left.FindIndex(candidate => SameSong(song, item.Album.Title, candidate, full.Title)) is >= 0 and var found)
                    {
                        if (song.Id != null) item.Pairs.TryAdd(Row(left[found]), song.Id);
                        left.RemoveAt(found);
                        item.Matched++;
                    }
                item.Proven = item.Owned > 0 || item.Matched > 0 && item.Matched == Math.Min(own.Count, full.Songs.Count);
            }
            V2LibraryAlbumRef Shape(Candidate item) => new(V2Ids.Album(item.Album.Id), item.Album.Title, V2ArtworkUrls.For(item.Album.Id, item.Tag))
            {
                Year = item.Album.Year,
                SongCount = item.Songs ?? item.Album.SongCount
            };
            var proven = owned.Where(item => item.Proven).ToArray();
            if (proven.Length == 1)
                return (Shape(proven[0]) with
                {
                    By = proven[0].Owned > 0 ? "songs" : "titles",
                    OwnedSongs = Math.Max(proven[0].Pairs.Count, Math.Max(proven[0].Owned, proven[0].Matched)),
                    OwnedTracks = proven[0].Pairs.Select(pair => new V2OwnedTrack(pair.Key, pair.Value)).ToArray()
                }, null);
            // Several library albums fit, or only the name does: a person can tell them apart, so they are offered, the closest first.
            var here = full.SongCount is > 0 ? full.SongCount : full.Songs.Count > 0 ? full.Songs.Count : null;
            return (null, owned.OrderByDescending(item => item.Proven).ThenByDescending(item => Math.Max(item.Owned, item.Matched)).Take(Offered)
                .Select(item => Shape(item) with { Reason = Reason(item, here, proven.Length > 1, full.Songs.Count > 0) }).ToArray());
        }
        // The library not answering leaves the album without a pointer; it never fails the album read.
        catch (Exception error) when (error is HttpRequestException or JsonException) { return (null, null); }
    }

    private static async Task<IReadOnlyList<LibrarySong>> LibrarySongsAsync(HttpContext http, ProtocolExecutionContext execution,
        V2AlbumEndpoints albums, string albumId)
    {
        using var document = await albums.LibraryAsync(http, execution, new()
        {
            ["parentId"] = albumId,
            ["includeItemTypes"] = "Audio",
            ["recursive"] = "true",
            ["enableUserData"] = "false",
            ["limit"] = "300"
        });
        return V2AlbumEndpoints.Items(document).Where(item => String(item, "Type") == "Audio" && String(item, "Name") is { Length: > 0 })
            .Select(item => new LibrarySong(String(item, "Name")!,
                string.Join(", ", Refs(item, "ArtistItems").Select(artist => artist.Name)) is { Length: > 0 } artists ? artists : String(item, "AlbumArtist") ?? "",
                item.TryGetProperty("RunTimeTicks", out var ticks) && ticks.TryGetInt64(out var value) && value > 0 ? value / TimeSpan.TicksPerMillisecond : null,
                String(item, "Id")))
            .ToArray();
    }

    // The same title and the same version of it, on albums already of one name and artist: a live take, a remix or a
    // re-recording is another song. Lengths must be close where both are stated; where one is not, as in some
    // services' album lists, the song's own artists must agree instead.
    internal static bool SameSong(LibrarySong own, string? ownAlbum, Song song, string? album) =>
        FuzzyMatcher.RecordingVersionsMatch(own.Title, ownAlbum, song.Title, album) &&
        FuzzyMatcher.TitleAnnotationsMatch(own.Title, own.Artist, song.Title, song.Artist) &&
        FuzzyMatcher.NormalizeTitleForMatching(own.Title, own.Artist, song.Artist) ==
        FuzzyMatcher.NormalizeTitleForMatching(song.Title, song.Artist, own.Artist) &&
        (own.Milliseconds is > 0 && song.Duration is > 0
            ? Math.Abs(own.Milliseconds.Value - song.Duration.Value * 1000L) <= SameSongSeconds * 1000L
            : FuzzyMatcher.SplitCredits(own.Artist).Select(FuzzyMatcher.NormalizeForMatching)
                .Intersect(FuzzyMatcher.SplitCredits(song.Artist).Select(FuzzyMatcher.NormalizeForMatching), StringComparer.Ordinal).Any());

    private static string Key(string id) => Guid.TryParse(id, out var parsed) ? parsed.ToString("N") : id;

    // One line under a name on a phone.
    private static string Reason(Candidate item, int? here, bool several, bool listed)
    {
        if (item.Proven && several) return "Same album. Your library has it more than once.";
        if (item.Songs is > 0 && item.Matched > 0) return $"Same title and artist. Shares {item.Matched} of its {Songs(item.Songs.Value)}.";
        // Only where the songs could be compared is finding none of them worth saying.
        if (item.Compared && listed) return "Same title and artist. None of its songs match by title and length.";
        return item.Songs is > 0 && here is > 0 && item.Songs != here
            ? $"Same title and artist. {Songs(item.Songs.Value)} there, {here} here."
            : "Same title and artist.";
    }

    private static string Songs(int count) => count == 1 ? "1 song" : $"{count} songs";
}
