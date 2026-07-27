using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    #region Lyrics

    /// <summary>
    /// Gets lyrics for an item.
    /// Priority: 1. Jellyfin embedded lyrics, 2. Spotify synced lyrics, 3. LRCLIB
    /// </summary>
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

        // For local tracks, check if Jellyfin already has embedded lyrics
        if (!isExternal)
        {
            _logger.LogDebug("Checking Jellyfin for embedded lyrics for local track: {ItemId}", itemId);

            // Try to get lyrics from Jellyfin first (it reads embedded lyrics from files)
            var (jellyfinLyrics, statusCode) =
                await _proxyService.GetJsonAsync($"Audio/{itemId}/Lyrics", null, Request.Headers);

            _logger.LogDebug("Jellyfin lyrics check result: statusCode={StatusCode}, hasLyrics={HasLyrics}",
                statusCode, jellyfinLyrics != null);

            if (jellyfinLyrics != null && statusCode == 200)
            {
                _logger.LogInformation("Found embedded lyrics in Jellyfin for track {ItemId}", itemId);
                return new JsonResult(JsonSerializer.Deserialize<object>(jellyfinLyrics.RootElement.GetRawText()));
            }

            _logger.LogWarning("No embedded lyrics found in Jellyfin (status: {StatusCode}), trying Spotify/LRCLIB",
                statusCode);
        }

        // Get song metadata for lyrics search
        Song? song = null;
        string? spotifyTrackId = null;

        if (isExternal)
        {
            song = await GetProviderSongAsync(provider!, externalId!);

            // Use Spotify ID from song metadata if available (populated during GetSongAsync)
            if (song != null && !string.IsNullOrEmpty(song.SpotifyId))
            {
                spotifyTrackId = song.SpotifyId;
                _logger.LogInformation("Using Spotify ID {SpotifyId} from song metadata for {Provider}/{ExternalId}",
                    spotifyTrackId, provider, externalId);
            }
            // Fallback: Try to find Spotify ID from matched tracks cache
            else if (song != null)
            {
                spotifyTrackId = await FindSpotifyIdForExternalTrackAsync(song);
                if (!string.IsNullOrEmpty(spotifyTrackId))
                {
                    _logger.LogDebug(
                        "Found Spotify ID {SpotifyId} for external track {Provider}/{ExternalId} from cache",
                        spotifyTrackId, provider, externalId);
                }
                else
                {
                    // Last resort: Try to convert via Odesli/song.link
                    if (provider == "squidwtf")
                    {
                        spotifyTrackId =
                            await _odesliService.ConvertTidalToSpotifyIdAsync(externalId!, HttpContext.RequestAborted);
                    }
                    else
                    {
                        // For other providers, build the URL and convert
                        var sourceUrl = provider?.ToLowerInvariant() switch
                        {
                            "deezer" => $"https://www.deezer.com/track/{externalId}",
                            "qobuz" => $"https://www.qobuz.com/us-en/album/-/-/{externalId}",
                            "applemusic" or "apple-download" =>
                                $"https://music.apple.com/us/song/{externalId}",
                            _ => null
                        };

                        if (!string.IsNullOrEmpty(sourceUrl))
                        {
                            spotifyTrackId =
                                await _odesliService.ConvertUrlToSpotifyIdAsync(sourceUrl, HttpContext.RequestAborted);
                        }
                    }

                    if (!string.IsNullOrEmpty(spotifyTrackId))
                    {
                        _logger.LogDebug("Converted {Provider}/{ExternalId} to Spotify ID {SpotifyId} via Odesli",
                            provider, externalId, spotifyTrackId);
                    }
                }
            }
        }
        else
        {
            // For local songs, get metadata from Jellyfin
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

                // Check for Spotify ID in provider IDs
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

        // Strip external track labels from lyrics search terms.
        var searchTitle = StripTrackDecorators(song.Title);
        var searchArtist = StripTrackDecorators(song.Artist);
        var searchAlbum = StripTrackDecorators(song.Album);
        var searchArtists = song.Artists.Select(StripTrackDecorators).ToList();

        if (searchArtists.Count == 0 && !string.IsNullOrEmpty(searchArtist))
        {
            searchArtists.Add(searchArtist);
        }

        var lyrics = await FetchLyricsInConfiguredOrderAsync(
            song,
            searchTitle,
            searchArtists,
            searchAlbum,
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

    private async Task<LyricsInfo?> FetchLyricsInConfiguredOrderAsync(
        Song song,
        string trackTitle,
        IReadOnlyList<string> artistNames,
        string albumTitle,
        string? sourceProvider,
        string? sourceExternalId,
        string? spotifyTrackId)
    {
        var order = _providerGateway?.GetProviderOrder(ProviderCapabilityKind.Lyrics) ??
                    (_configuration["Providers:LyricsOrder"] ??
                     _configuration["MULTI_PROVIDER_LYRICS_ORDER"] ??
                     "spotify,apple-download,lyricsplus,lrclib")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var configuredProvider in order.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var lyrics = await TryLyricsProviderAsync(
                configuredProvider,
                song,
                trackTitle,
                artistNames,
                albumTitle,
                sourceProvider,
                sourceExternalId,
                spotifyTrackId);
            if (lyrics != null) return lyrics;
        }
        return null;
    }

    private async Task<LyricsInfo?> TryLyricsProviderAsync(
        string configuredProvider,
        Song song,
        string trackTitle,
        IReadOnlyList<string> artistNames,
        string albumTitle,
        string? sourceProvider,
        string? sourceExternalId,
        string? spotifyTrackId)
    {
        var providerId = configuredProvider.Trim().ToLowerInvariant();
        try
        {
            if (providerId == "spotify")
            {
                // The Spotify lyrics sidecar is independent of direct Spotify API
                // playlist mode. A configured sidecar remains a valid fallback for
                // Apple Music, local, and other provider tracks with a Spotify identity.
                if (_spotifyLyricsService == null ||
                    string.IsNullOrWhiteSpace(_spotifyApiSettings.LyricsApiUrl) ||
                    string.IsNullOrWhiteSpace(spotifyTrackId))
                    return null;
                var cleanSpotifyId = spotifyTrackId.Replace("spotify:track:", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (cleanSpotifyId.Length != 22 || cleanSpotifyId.Contains(':') || cleanSpotifyId.Contains("local", StringComparison.OrdinalIgnoreCase))
                    return null;
                var spotifyLyrics = await _spotifyLyricsService.GetLyricsByTrackIdAsync(cleanSpotifyId);
                return spotifyLyrics is { Lines.Count: > 0 } ? _spotifyLyricsService.ToLyricsInfo(spotifyLyrics) : null;
            }
            if (providerId == "lyricsplus")
                return _lyricsPlusService == null ? null : await _lyricsPlusService.GetLyricsAsync(
                    trackTitle, artistNames.ToArray(), albumTitle, song.Duration ?? 0);
            if (providerId == "lrclib")
                return _lrclibService == null ? null : await _lrclibService.GetLyricsAsync(
                    trackTitle, artistNames.ToArray(), albumTitle, song.Duration ?? 0);

            if (_providerGateway == null || HttpContext.GetProtocolExecutionContext() is not { } protocol)
                return null;

            var compatibleExternalId = ResolveLyricsExternalId(
                providerId, sourceProvider, sourceExternalId, spotifyTrackId);
            if (string.IsNullOrWhiteSpace(compatibleExternalId))
                compatibleExternalId = sourceExternalId ?? song.Id;
            if (string.IsNullOrWhiteSpace(compatibleExternalId)) return null;

            var providerLyrics = await _providerGateway.GetLyricsAsync(
                protocol,
                providerId,
                compatibleExternalId,
                ProviderLyricsFormat.LineTimed,
                trackTitle,
                artistNames,
                albumTitle,
                song.Duration);
            if (string.IsNullOrWhiteSpace(providerLyrics?.Content)) return null;
            return new LyricsInfo
            {
                TrackName = trackTitle,
                ArtistName = string.Join(", ", artistNames),
                AlbumName = albumTitle,
                Duration = song.Duration ?? 0,
                PlainLyrics = providerLyrics.Format == ProviderLyricsFormat.PlainText ? providerLyrics.Content : null,
                SyncedLyrics = providerLyrics.Format != ProviderLyricsFormat.PlainText ? providerLyrics.Content : null
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception,
                "Lyrics provider {Provider} failed for {Artist} - {Track}; continuing in configured order",
                providerId, string.Join(", ", artistNames), trackTitle);
            return null;
        }
    }

    private static string? ResolveLyricsExternalId(
        string providerId,
        string? sourceProvider,
        string? sourceExternalId,
        string? spotifyTrackId)
    {
        if (providerId == "spotify") return spotifyTrackId;
        if (providerId.Equals(sourceProvider, StringComparison.OrdinalIgnoreCase)) return sourceExternalId;
        var sourceIsApple = sourceProvider is not null &&
                            (sourceProvider.Equals("applemusic", StringComparison.OrdinalIgnoreCase) ||
                             sourceProvider.Equals("apple-download", StringComparison.OrdinalIgnoreCase) ||
                             sourceProvider.Equals("spotiflac-apple-music", StringComparison.OrdinalIgnoreCase));
        return sourceIsApple && providerId is "apple-download" or "spotiflac-apple-music"
            ? sourceExternalId
            : null;
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
