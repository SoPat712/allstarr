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

    // Native and provider lyrics use the same configured source selection.
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
            var (item, _) = await _proxyService.GetItemAsync(itemId, Request.Headers, protocol.CancellationToken);
            using var nativeItem = item;
            if (item == null || item.RootElement.ValueKind != JsonValueKind.Object ||
                !item.RootElement.TryGetProperty("Id", out var nativeId) || nativeId.ValueKind != JsonValueKind.String ||
                !SameNativeItem(nativeId.GetString(), itemId) || !item.RootElement.TryGetProperty("Type", out var typeEl) ||
                typeEl.GetString() != "Audio")
            {
                return NotFound(new { error = "Song not found" });
            }

            var song = new Song
            {
                Id = itemId,
                IsLocal = true,
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

            song.SpotifyId = spotifyTrackId;
            JsonDocument? backendLyrics = null;
            try
            {
                lyrics = await _protocolLyricsResolver.FindAsync(protocol, MusicLyrics.ForLookup(song), itemId,
                    new ProtocolLyricsSources(Backend: async token =>
                    {
                        var (body, status) = await _proxyService.GetJsonAsync(
                            $"Audio/{Uri.EscapeDataString(itemId)}/Lyrics", null, Request.Headers, token);
                        if (status != StatusCodes.Status200OK || body == null)
                        {
                            body?.Dispose();
                            return null;
                        }
                        backendLyrics = body;
                        return BackendLyrics.ParseJellyfin(body.RootElement);
                    }));
                if (lyrics?.Source == "backend" && backendLyrics != null)
                    return Content(backendLyrics.RootElement.GetRawText(), "application/json", System.Text.Encoding.UTF8);
            }
            finally { backendLyrics?.Dispose(); }
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

    private static bool SameNativeItem(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.Ordinal) ||
        Guid.TryParse(actual, out var actualGuid) && Guid.TryParse(expected, out var expectedGuid) && actualGuid == expectedGuid;
}
