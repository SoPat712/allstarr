using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    #region Lyrics

    // Prefer Jellyfin's embedded lyrics for local tracks before provider fallbacks.
    [HttpGet("Audio/{itemId}/Lyrics")]
    [HttpGet("Items/{itemId}/Lyrics")]
    public async Task<IActionResult> GetLyrics(string itemId)
    {
        _logger.LogDebug("🎵 GetLyrics called for itemId: {ItemId}", itemId);

        if (string.IsNullOrWhiteSpace(itemId))
        {
            return NotFound();
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

        _logger.LogDebug(
            "🎵 Lyrics request: itemId={ItemId}, isExternal={IsExternal}, provider={Provider}, externalId={ExternalId}",
            itemId, isExternal, provider, externalId);

        if (!isExternal)
        {
            _logger.LogDebug("Checking Jellyfin for embedded lyrics for local track: {ItemId}", itemId);

            var (jellyfinLyrics, statusCode) =
                await _proxyService.GetJsonAsync($"Audio/{itemId}/Lyrics", null, Request.Headers);

            _logger.LogDebug("Jellyfin lyrics check result: statusCode={StatusCode}, hasLyrics={HasLyrics}",
                statusCode, jellyfinLyrics != null);

            if (jellyfinLyrics != null && statusCode == 200)
            {
                _logger.LogInformation("Found embedded lyrics in Jellyfin for track {ItemId}", itemId);
                return new JsonResult(JsonSerializer.Deserialize<object>(jellyfinLyrics.RootElement.GetRawText()));
            }

            _logger.LogWarning("No embedded lyrics found in Jellyfin (status: {StatusCode}), trying configured sources",
                statusCode);
        }

        Song? song = null;
        string? spotifyTrackId = null;

        if (isExternal)
        {
            song = await GetProviderSongAsync(provider!, externalId!);

            if (song != null && !string.IsNullOrEmpty(song.SpotifyId))
            {
                spotifyTrackId = song.SpotifyId;
                _logger.LogInformation("Using Spotify ID {SpotifyId} from song metadata for {Provider}/{ExternalId}",
                    spotifyTrackId, provider, externalId);
            }
        }
        else
        {
            var (item, _) = await _proxyService.GetItemAsync(itemId, Request.Headers);
            if (item != null && item.RootElement.TryGetProperty("Type", out var typeEl) &&
                typeEl.GetString() == "Audio")
            {
                song = new Song
                {
                    Title = item.RootElement.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
                    Artist = item.RootElement.TryGetProperty("AlbumArtist", out var artist)
                        ? artist.GetString() ?? ""
                        : "",
                    Album = item.RootElement.TryGetProperty("Album", out var album) ? album.GetString() ?? "" : "",
                    Duration = item.RootElement.TryGetProperty("RunTimeTicks", out var ticks)
                        ? (int)(ticks.GetInt64() / 10000000)
                        : 0
                };

                if (item.RootElement.TryGetProperty("ProviderIds", out var providerIds))
                {
                    if (providerIds.TryGetProperty("Spotify", out var spotifyId))
                    {
                        spotifyTrackId = spotifyId.GetString();
                    }
                }
            }
        }

        if (song == null)
        {
            return NotFound(new { error = "Song not found" });
        }

        // Provider labels are presentation-only and degrade lyric matching.
        var searchTitle = StripTrackDecorators(song.Title);
        var searchArtist = StripTrackDecorators(song.Artist);
        var searchAlbum = StripTrackDecorators(song.Album);
        var searchArtists = song.Artists.Select(StripTrackDecorators).ToList();

        if (searchArtists.Count == 0 && !string.IsNullOrEmpty(searchArtist))
        {
            searchArtists.Add(searchArtist);
        }

        song.Title = searchTitle;
        song.Artist = searchArtist;
        song.Artists = searchArtists;
        song.Album = searchAlbum;
        var lyrics = await _protocolLyricsResolver.FindAsync(
            HttpContext.RequireProtocolExecutionContext(),
            song,
            itemId,
            isExternal ? provider : null,
            isExternal ? externalId : null,
            spotifyTrackId);

        if (lyrics == null)
        {
            return NotFound(new { error = "Lyrics not found" });
        }

        var isSynced = !string.IsNullOrEmpty(lyrics.SyncedLyrics);

        _logger.LogInformation(
            "Lyrics for {Artist} - {Track}: synced={HasSynced}, plainLength={PlainLen}, syncedLength={SyncLen}",
            song.Artist, song.Title, isSynced, lyrics.PlainLyrics?.Length ?? 0, lyrics.SyncedLyrics?.Length ?? 0);

        var response = _lyricsProtocolAdapter.Shape(lyrics);
        _logger.LogDebug("Returning lyrics response: synced={IsSynced}", isSynced);
        return Content(response.Body, response.ContentType, System.Text.Encoding.UTF8);
    }

    private static string StripTrackDecorators(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value
            .Replace(" [S]", "", StringComparison.Ordinal)
            .Replace(" [D]", "", StringComparison.Ordinal)
            .Replace(" [Q]", "", StringComparison.Ordinal)
            .Replace(" [AM]", "", StringComparison.Ordinal)
            .Replace(" [E]", "", StringComparison.Ordinal)
            .Trim();
    }

    #endregion
}
