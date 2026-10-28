using System.Xml.Linq;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Services.Subsonic;

public partial class SubsonicResponseBuilder
{
    private static void AddSongDescription(Song song, Dictionary<string, object> item)
    {
        if (song.IsLocal) return;
        var artists = MusicDescriptiveMetadata.AlbumArtists(song).Select(ArtistCredit).ToArray();
        if (artists.Length > 0)
        {
            item["albumArtists"] = artists;
            item["displayAlbumArtist"] = string.Join(", ", MusicDescriptiveMetadata.AlbumArtists(song).Select(artist => artist.Name));
        }
        var contributors = MusicDescriptiveMetadata.Contributors(song).Select(person => new
        {
            role = person.Role,
            artist = new { name = person.Name }
        }).ToArray();
        if (contributors.Length > 0) item["contributors"] = contributors;
        var id = MusicDescriptiveMetadata.Identifier(song.ProviderIds, "MusicBrainzTrack", "MusicBrainzRecording", "MusicBrainz");
        if (id != null) item["musicBrainzId"] = id;
        if (MusicDescriptiveMetadata.ReleaseDate(song.ReleaseDate, song.Year) is { } release) item["releaseDate"] = release;
    }

    private static void AddSongDescription(Song song, XElement item)
    {
        if (song.IsLocal) return;
        var ns = item.Name.Namespace;
        var artists = MusicDescriptiveMetadata.AlbumArtists(song);
        foreach (var artist in artists) item.Add(CreditElement(ns + "albumArtists", artist));
        if (artists.Count > 0) item.SetAttributeValue("displayAlbumArtist", string.Join(", ", artists.Select(artist => artist.Name)));
        foreach (var person in MusicDescriptiveMetadata.Contributors(song))
            item.Add(new XElement(ns + "contributors", new XAttribute("role", person.Role),
                new XElement(ns + "artist", new XAttribute("name", person.Name))));
        var id = MusicDescriptiveMetadata.Identifier(song.ProviderIds, "MusicBrainzTrack", "MusicBrainzRecording", "MusicBrainz");
        if (id != null) item.SetAttributeValue("musicBrainzId", id);
        AddReleaseDate(item, song.ReleaseDate, song.Year);
    }

    private static void AddAlbumDescription(Album album, Dictionary<string, object?> item)
    {
        if (album.IsLocal) return;
        if (album.IsExplicit is { } rating) item["explicitStatus"] = rating ? "explicit" : "clean";
        var artists = MusicDescriptiveMetadata.AlbumArtists(album).Select(ArtistCredit).ToArray();
        if (artists.Length > 0) item["artists"] = artists;
        if (!string.IsNullOrWhiteSpace(album.Label)) item["recordLabels"] = new[] { new { name = album.Label } };
        var id = MusicDescriptiveMetadata.Identifier(album.ProviderIds, "MusicBrainzAlbum", "MusicBrainz");
        if (id != null) item["musicBrainzId"] = id;
        if (MusicDescriptiveMetadata.ReleaseDate(album.ReleaseDate, album.Year) is { } release) item["releaseDate"] = release;
    }

    private static void AddAlbumDescription(Album album, XElement item)
    {
        if (album.IsLocal) return;
        if (album.IsExplicit is { } rating) item.SetAttributeValue("explicitStatus", rating ? "explicit" : "clean");
        var ns = item.Name.Namespace;
        foreach (var artist in MusicDescriptiveMetadata.AlbumArtists(album)) item.Add(CreditElement(ns + "artists", artist));
        if (!string.IsNullOrWhiteSpace(album.Label)) item.Add(new XElement(ns + "recordLabels", new XAttribute("name", album.Label)));
        var id = MusicDescriptiveMetadata.Identifier(album.ProviderIds, "MusicBrainzAlbum", "MusicBrainz");
        if (id != null) item.SetAttributeValue("musicBrainzId", id);
        AddReleaseDate(item, album.ReleaseDate, album.Year);
    }

    private static Dictionary<string, string> ArtistCredit(Artist artist) => new()
    {
        ["id"] = artist.Id,
        ["name"] = artist.Name
    };

    private static XElement CreditElement(XName name, Artist artist) =>
        new(name, new XAttribute("id", artist.Id), new XAttribute("name", artist.Name));

    private static void AddReleaseDate(XElement item, string? date, int? year)
    {
        if (MusicDescriptiveMetadata.ReleaseDate(date, year) is { } release)
            item.Add(new XElement(item.Name.Namespace + "releaseDate", release.Select(pair => new XAttribute(pair.Key, pair.Value))));
    }
}
