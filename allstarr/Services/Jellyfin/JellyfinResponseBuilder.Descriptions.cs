using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Services.Jellyfin;

public partial class JellyfinResponseBuilder
{
    private static void AddSongDescription(Song song, Dictionary<string, object?> item, ListeningPreferences? preferences)
    {
        var credits = MusicDescriptiveMetadata.AlbumArtists(song);
        item["AlbumArtists"] = ArtistCredits(credits, preferences);
        if (credits.Count > 0)
            item["AlbumArtist"] = ExternalTrackPresentation.Name(string.Join(", ", credits.Select(artist => artist.Name)), preferences);
        item["People"] = MusicDescriptiveMetadata.Contributors(song).Select(person => (object)new Dictionary<string, object?>
        {
            ["Name"] = person.Name,
            ["Type"] = person.Role == "composer" ? "Composer" : "Unknown",
            ["Role"] = person.Role
        }).ToArray();
        AddIdentifiers(item, song.ProviderIds, "MusicBrainzTrack");
        if (!string.IsNullOrWhiteSpace(song.Upc))
        {
            if (item.GetValueOrDefault("ProviderIds") is not Dictionary<string, string> ids)
                item["ProviderIds"] = ids = new();
            ids["UPC"] = song.Upc;
        }
        if (!string.IsNullOrWhiteSpace(song.Notes)) item["Overview"] = song.Notes;
        if (!string.IsNullOrWhiteSpace(song.Label)) item["Studios"] = new[] { new Dictionary<string, string> { ["Name"] = song.Label } };
        if (!string.IsNullOrWhiteSpace(song.Copyright)) item["Copyright"] = song.Copyright;
        if (song.TotalTracks is > 0) item["IndexNumberEnd"] = song.TotalTracks.Value;
        if (song.TotalDiscs is > 0) item["TotalDiscs"] = song.TotalDiscs.Value;
        if (song.Bpm is > 0) item["BeatsPerMinute"] = song.Bpm.Value;
        if (!string.IsNullOrWhiteSpace(song.MusicalKey)) item["Key"] = song.MusicalKey;
        if (song.OriginalItemId != null) item["OriginalItemId"] = song.OriginalItemId;
        if (song.ExplicitContentLyrics is 0 or 1 or 3) item["OfficialRating"] = song.ExplicitContentLyrics == 1 ? "Explicit" : "Clean";
        var genres = (song.Genres.Count > 0 ? song.Genres : string.IsNullOrWhiteSpace(song.Genre) ? [] : new[] { song.Genre }.ToList())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (genres.Length > 0)
        {
            item["Genres"] = genres;
            item["GenreItems"] = genres.Select(name => new Dictionary<string, string> { ["Name"] = name, ["Id"] = "genre-" + name.ToLowerInvariant() }).ToArray();
        }
        if (song.ReplayGain is { } gain)
        {
            if (gain.TrackGain is { } track && double.IsFinite(track)) item["NormalizationGain"] = track;
            if (gain.AlbumGain is { } album && double.IsFinite(album)) item["AlbumGain"] = album;
            if (gain.TrackPeak is >= 0 and { } peak && double.IsFinite(peak)) item["TrackPeak"] = peak;
            if (gain.AlbumPeak is >= 0 and { } albumPeak && double.IsFinite(albumPeak)) item["AlbumPeak"] = albumPeak;
        }
    }

    private static void AddAlbumDescription(Album album, Dictionary<string, object?> item, ListeningPreferences? preferences)
    {
        if (album.IsExplicit is { } rating) item["OfficialRating"] = rating ? "Explicit" : "Clean";
        var credits = ArtistCredits(MusicDescriptiveMetadata.AlbumArtists(album), preferences);
        item["AlbumArtists"] = credits;
        item["ArtistItems"] = credits;
        if (!string.IsNullOrWhiteSpace(album.Notes)) item["Overview"] = album.Notes;
        if (!string.IsNullOrWhiteSpace(album.Label)) item["Studios"] = new[] { new Dictionary<string, string> { ["Name"] = album.Label } };
        AddIdentifiers(item, album.ProviderIds, "MusicBrainzAlbum");
    }

    private static void AddArtistDescription(Artist artist, Dictionary<string, object?> item)
    {
        if (!string.IsNullOrWhiteSpace(artist.Biography)) item["Overview"] = artist.Biography;
        AddIdentifiers(item, artist.ProviderIds, "MusicBrainzArtist");
    }

    private static Dictionary<string, object?>[] ArtistCredits(IReadOnlyList<Artist> artists, ListeningPreferences? preferences) => artists
        .Where(artist => !string.IsNullOrWhiteSpace(artist.Id) && !string.IsNullOrWhiteSpace(artist.Name))
        .Select(artist => new Dictionary<string, object?>
        {
            ["Id"] = artist.Id,
            ["Name"] = ExternalTrackPresentation.Name(artist.Name, preferences)
        }).ToArray();

    private static void AddIdentifiers(Dictionary<string, object?> item, IReadOnlyDictionary<string, string> identifiers, string musicBrainzKey)
    {
        var result = item.GetValueOrDefault("ProviderIds") is Dictionary<string, string> existing
            ? new Dictionary<string, string>(existing) : new Dictionary<string, string>();
        foreach (var pair in identifiers.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))) result[pair.Key] = pair.Value;
        if (MusicDescriptiveMetadata.Identifier(identifiers, musicBrainzKey, "MusicBrainz") is { } id) result[musicBrainzKey] = id;
        if (result.Count > 0) item["ProviderIds"] = result;
    }
}
