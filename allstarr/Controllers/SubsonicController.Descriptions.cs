using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class SubsonicController
{
    [HttpGet, HttpPost]
    [Route("rest/getAlbumInfo")]
    [Route("rest/getAlbumInfo.view")]
    [Route("rest/getAlbumInfo2")]
    [Route("rest/getAlbumInfo2.view")]
    public async Task<IActionResult> GetAlbumInfo()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        var (external, provider, type, id) = _localLibraryService.ParseExternalId(parameters.GetValueOrDefault("id", ""));
        if (!external)
        {
            var native = await _proxyService.RelayRawAsync(Request.Path.Value!.TrimStart('/'), parameters,
                HttpContext.RequestAborted, Request.Headers);
            return _relayProtocolAdapter.CreateResult(native, $"application/{format}");
        }
        var description = new Dictionary<string, string>();
        if (type == "album" && await _providerGateway.GetStoredAlbumAsync(CurrentProtocolContext, provider!, id!) is { } album)
        {
            AddDescription(description, "notes", album.Notes);
            AddDescription(description, "musicBrainzId", MusicDescriptiveMetadata.Identifier(album.ProviderIds, "MusicBrainzAlbum", "MusicBrainz"));
            AddDescription(description, "lastFmUrl", album.LastFmUrl);
            AddDescription(description, "smallImageUrl", album.CoverArtUrl);
            AddDescription(description, "mediumImageUrl", album.CoverArtUrl);
            AddDescription(description, "largeImageUrl", album.CoverArtUrl);
        }
        return CreateSongListResponse(format, "albumInfo", [], songsElement: null, description: description);
    }

    private async Task<IActionResult> GetProviderTopSongsAsync(IReadOnlyDictionary<string, string> parameters, string format,
        string provider, string type, string id)
    {
        var count = int.TryParse(parameters.GetValueOrDefault("count"), out var requested) ? Math.Clamp(requested, 0, 500) : 50;
        IReadOnlyList<Song> songs = [];
        if (type == "artist" && count > 0)
        {
            try
            {
                songs = await _providerGateway.GetArtistTracksAsync(CurrentProtocolContext with { IsReadOnly = true }, provider, id);
            }
            catch (Exception error) when (error is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
            {
                _logger.LogWarning("Top songs for {Provider} were unavailable ({ExceptionType})", provider, error.GetType().Name);
            }
        }
        var ns = System.Xml.Linq.XNamespace.Get("http://subsonic.org/restapi");
        var entries = songs.DistinctBy(song => song.Id).Take(count).Select(song => format == "json"
            ? (object)_responseBuilder.ConvertSongToJson(song) : _responseBuilder.ConvertSongToXml(song, ns)).ToArray();
        return CreateSongListResponse(format, "topSongs", entries);
    }

    private static Dictionary<string, string> ArtistDescription(Artist? artist)
    {
        var result = new Dictionary<string, string>();
        if (artist == null) return result;
        AddDescription(result, "biography", artist.Biography);
        AddDescription(result, "musicBrainzId", MusicDescriptiveMetadata.Identifier(artist.ProviderIds, "MusicBrainzArtist", "MusicBrainz"));
        AddDescription(result, "lastFmUrl", artist.LastFmUrl);
        AddDescription(result, "smallImageUrl", artist.ImageUrl);
        AddDescription(result, "mediumImageUrl", artist.ImageUrl);
        AddDescription(result, "largeImageUrl", artist.ImageUrl);
        return result;
    }

    private static void AddDescription(Dictionary<string, string> result, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) result[key] = value;
    }
}
