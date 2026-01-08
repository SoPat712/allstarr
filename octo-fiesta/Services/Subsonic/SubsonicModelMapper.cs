using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using octo_fiesta.Models.Search;

namespace octo_fiesta.Services.Subsonic;

/// <summary>
/// Handles parsing Subsonic API responses and merging local with external search results.
/// </summary>
public class SubsonicModelMapper
{
    private readonly SubsonicResponseBuilder _responseBuilder;
    private readonly ILogger<SubsonicModelMapper> _logger;

    public SubsonicModelMapper(
        SubsonicResponseBuilder responseBuilder,
        ILogger<SubsonicModelMapper> logger)
    {
        _responseBuilder = responseBuilder;
        _logger = logger;
    }

    /// <summary>
    /// Parses a Subsonic search response and extracts songs, albums, and artists.
    /// </summary>
    public (List<object> Songs, List<object> Albums, List<object> Artists) ParseSearchResponse(
        byte[] responseBody,
        string? contentType)
    {
        var songs = new List<object>();
        var albums = new List<object>();
        var artists = new List<object>();

        try
        {
            var content = Encoding.UTF8.GetString(responseBody);
            
            if (contentType?.Contains("json") == true)
            {
                var jsonDoc = JsonDocument.Parse(content);
                if (jsonDoc.RootElement.TryGetProperty("subsonic-response", out var response) &&
                    response.TryGetProperty("searchResult3", out var searchResult))
                {
                    if (searchResult.TryGetProperty("song", out var songElements))
                    {
                        foreach (var song in songElements.EnumerateArray())
                        {
                            songs.Add(_responseBuilder.ConvertSubsonicJsonElement(song, true));
                        }
                    }
                    if (searchResult.TryGetProperty("album", out var albumElements))
                    {
                        foreach (var album in albumElements.EnumerateArray())
                        {
                            albums.Add(_responseBuilder.ConvertSubsonicJsonElement(album, true));
                        }
                    }
                    if (searchResult.TryGetProperty("artist", out var artistElements))
                    {
                        foreach (var artist in artistElements.EnumerateArray())
                        {
                            artists.Add(_responseBuilder.ConvertSubsonicJsonElement(artist, true));
                        }
                    }
                }
            }
            else
            {
                var xmlDoc = XDocument.Parse(content);
                var ns = xmlDoc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                var searchResult = xmlDoc.Descendants(ns + "searchResult3").FirstOrDefault();
                
                if (searchResult != null)
                {
                    foreach (var song in searchResult.Elements(ns + "song"))
                    {
                        songs.Add(_responseBuilder.ConvertSubsonicXmlElement(song, "song"));
                    }
                    foreach (var album in searchResult.Elements(ns + "album"))
                    {
                        albums.Add(_responseBuilder.ConvertSubsonicXmlElement(album, "album"));
                    }
                    foreach (var artist in searchResult.Elements(ns + "artist"))
                    {
                        artists.Add(_responseBuilder.ConvertSubsonicXmlElement(artist, "artist"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing Subsonic search response");
        }

        return (songs, albums, artists);
    }

    /// <summary>
    /// Merges local search results with external search results, deduplicating by name.
    /// </summary>
    public (List<object> MergedSongs, List<object> MergedAlbums, List<object> MergedArtists) MergeSearchResults(
        List<object> localSongs,
        List<object> localAlbums,
        List<object> localArtists,
        SearchResult externalResult,
        bool isJson)
    {
        if (isJson)
        {
            return MergeSearchResultsJson(localSongs, localAlbums, localArtists, externalResult);
        }
        else
        {
            return MergeSearchResultsXml(localSongs, localAlbums, localArtists, externalResult);
        }
    }

    private (List<object> MergedSongs, List<object> MergedAlbums, List<object> MergedArtists) MergeSearchResultsJson(
        List<object> localSongs,
        List<object> localAlbums,
        List<object> localArtists,
        SearchResult externalResult)
    {
        var mergedSongs = localSongs
            .Concat(externalResult.Songs.Select(s => _responseBuilder.ConvertSongToJson(s)))
            .ToList();
        
        var mergedAlbums = localAlbums
            .Concat(externalResult.Albums.Select(a => _responseBuilder.ConvertAlbumToJson(a)))
            .ToList();
        
        // Deduplicate artists by name - prefer local artists over external ones
        var localArtistNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artist in localArtists)
        {
            if (artist is Dictionary<string, object> dict && dict.TryGetValue("name", out var nameObj))
            {
                localArtistNames.Add(nameObj?.ToString() ?? "");
            }
        }
        
        var mergedArtists = localArtists.ToList();
        foreach (var externalArtist in externalResult.Artists)
        {
            // Only add external artist if no local artist with same name exists
            if (!localArtistNames.Contains(externalArtist.Name))
            {
                mergedArtists.Add(_responseBuilder.ConvertArtistToJson(externalArtist));
            }
        }

        return (mergedSongs, mergedAlbums, mergedArtists);
    }

    private (List<object> MergedSongs, List<object> MergedAlbums, List<object> MergedArtists) MergeSearchResultsXml(
        List<object> localSongs,
        List<object> localAlbums,
        List<object> localArtists,
        SearchResult externalResult)
    {
        var ns = XNamespace.Get("http://subsonic.org/restapi");
        
        // Deduplicate artists by name - prefer local artists over external ones
        var localArtistNamesXml = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mergedArtists = new List<object>();
        
        foreach (var artist in localArtists.Cast<XElement>())
        {
            var name = artist.Attribute("name")?.Value;
            if (!string.IsNullOrEmpty(name))
            {
                localArtistNamesXml.Add(name);
            }
            artist.Name = ns + "artist";
            mergedArtists.Add(artist);
        }
        
        foreach (var artist in externalResult.Artists)
        {
            // Only add external artist if no local artist with same name exists
            if (!localArtistNamesXml.Contains(artist.Name))
            {
                mergedArtists.Add(_responseBuilder.ConvertArtistToXml(artist, ns));
            }
        }
        
        // Albums
        var mergedAlbums = new List<object>();
        foreach (var album in localAlbums.Cast<XElement>())
        {
            album.Name = ns + "album";
            mergedAlbums.Add(album);
        }
        foreach (var album in externalResult.Albums)
        {
            mergedAlbums.Add(_responseBuilder.ConvertAlbumToXml(album, ns));
        }
        
        // Songs
        var mergedSongs = new List<object>();
        foreach (var song in localSongs.Cast<XElement>())
        {
            song.Name = ns + "song";
            mergedSongs.Add(song);
        }
        foreach (var song in externalResult.Songs)
        {
            mergedSongs.Add(_responseBuilder.ConvertSongToXml(song, ns));
        }

        return (mergedSongs, mergedAlbums, mergedArtists);
    }
}
