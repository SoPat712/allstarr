using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Services.Jellyfin;

public partial class JellyfinResponseBuilder
{
    private static void AddSongDescription(Song song, Dictionary<string, object?> item)
    {
        var credits = MusicDescriptiveMetadata.AlbumArtists(song);
        item["AlbumArtists"] = ArtistCredits(credits, song.ExternalProvider);
        if (credits.Count > 0)
            item["AlbumArtist"] = AppendExternalSourceLabel(string.Join(", ", credits.Select(artist => artist.Name)), song.ExternalProvider);
        item["People"] = MusicDescriptiveMetadata.Contributors(song).Select(person => (object)new Dictionary<string, object?>
        {
            ["Name"] = person.Name,
            ["Type"] = person.Role == "composer" ? "Composer" : "Unknown",
            ["Role"] = person.Role
        }).ToArray();
        AddIdentifiers(item, song.ProviderIds, "MusicBrainzTrack");
    }

    private static void AddAlbumDescription(Album album, Dictionary<string, object?> item)
    {
        var credits = ArtistCredits(MusicDescriptiveMetadata.AlbumArtists(album), album.ExternalProvider);
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

    private static Dictionary<string, object?>[] ArtistCredits(IReadOnlyList<Artist> artists, string? provider) => artists
        .Where(artist => !string.IsNullOrWhiteSpace(artist.Id) && !string.IsNullOrWhiteSpace(artist.Name))
        .Select(artist => new Dictionary<string, object?>
        {
            ["Id"] = artist.Id,
            ["Name"] = AppendExternalSourceLabel(artist.Name, provider)
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
