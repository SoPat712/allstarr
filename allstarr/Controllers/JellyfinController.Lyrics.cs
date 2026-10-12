using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
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

        var protocol = HttpContext.RequireProtocolExecutionContext();
        LyricsInfo? lyrics;
        if (isExternal)
        {
            var found = await MusicLyrics.FindForProviderSongAsync(
                _providerGateway, _protocolLyricsResolver, protocol, provider!, externalId!);
            if (found == null)
            {
                return NotFound(new { error = "Song not found" });
            }

            lyrics = found.Lyrics;
        }
        else
        {
            var (item, _) = await _proxyService.GetItemAsync(itemId, Request.Headers);
            if (item == null || !item.RootElement.TryGetProperty("Type", out var typeEl) ||
                typeEl.GetString() != "Audio")
            {
                return NotFound(new { error = "Song not found" });
            }

            var song = new Song
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
            string? spotifyTrackId = null;
            if (item.RootElement.TryGetProperty("ProviderIds", out var providerIds) &&
                providerIds.TryGetProperty("Spotify", out var spotifyId))
            {
                spotifyTrackId = spotifyId.GetString();
            }

            lyrics = await MusicLyrics.FindForLibrarySongAsync(
                _protocolLyricsResolver, protocol, song, itemId, spotifyTrackId);
        }

        if (lyrics == null)
        {
            return NotFound(new { error = "Lyrics not found" });
        }

        var isSynced = !string.IsNullOrEmpty(lyrics.SyncedLyrics);

        _logger.LogInformation(
            "Lyrics for {Artist} - {Track}: synced={HasSynced}, plainLength={PlainLen}, syncedLength={SyncLen}",
            lyrics.ArtistName, lyrics.TrackName, isSynced, lyrics.PlainLyrics?.Length ?? 0, lyrics.SyncedLyrics?.Length ?? 0);

        var response = _lyricsProtocolAdapter.Shape(lyrics);
        _logger.LogDebug("Returning lyrics response: synced={IsSynced}", isSynced);
        return Content(response.Body, response.ContentType, System.Text.Encoding.UTF8);
    }

    #endregion
}
