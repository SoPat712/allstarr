using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
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
            song = await _metadataService.GetSongAsync(provider!, externalId!);

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

        // Strip [S] suffix from title, artist, and album for lyrics search
        // The [S] tag is added to external tracks but shouldn't be used in lyrics queries
        var searchTitle = song.Title.Replace(" [S]", "").Trim();
        var searchArtist = song.Artist?.Replace(" [S]", "").Trim() ?? "";
        var searchAlbum = song.Album?.Replace(" [S]", "").Trim() ?? "";
        var searchArtists = song.Artists.Select(a => a.Replace(" [S]", "").Trim()).ToList();

        if (searchArtists.Count == 0 && !string.IsNullOrEmpty(searchArtist))
        {
            searchArtists.Add(searchArtist);
        }

        // Use orchestrator for clean, modular lyrics fetching
        LyricsInfo? lyrics = null;

        if (_lyricsOrchestrator != null)
        {
            lyrics = await _lyricsOrchestrator.GetLyricsAsync(
                trackName: searchTitle,
                artistNames: searchArtists.ToArray(),
                albumName: searchAlbum,
                durationSeconds: song.Duration ?? 0,
                spotifyTrackId: spotifyTrackId);
        }
        else
        {
            // Fallback to manual fetching if orchestrator not available
            _logger.LogWarning("LyricsOrchestrator not available, using fallback method");

            // Try Spotify lyrics ONLY if we have a valid Spotify track ID
            if (_spotifyLyricsService != null && _spotifyApiSettings.Enabled && !string.IsNullOrEmpty(spotifyTrackId))
            {
                var cleanSpotifyId = spotifyTrackId.Replace("spotify:track:", "").Trim();

                if (cleanSpotifyId.Length == 22 && !cleanSpotifyId.Contains(":") && !cleanSpotifyId.Contains("local"))
                {
                    var spotifyLyrics = await _spotifyLyricsService.GetLyricsByTrackIdAsync(cleanSpotifyId);

                    if (spotifyLyrics != null && spotifyLyrics.Lines.Count > 0)
                    {
                        lyrics = _spotifyLyricsService.ToLyricsInfo(spotifyLyrics);
                    }
                }
            }

            // Fall back to LyricsPlus
            if (lyrics == null && _lyricsPlusService != null)
            {
                lyrics = await _lyricsPlusService.GetLyricsAsync(
                    searchTitle,
                    searchArtists.ToArray(),
                    searchAlbum,
                    song.Duration ?? 0);
            }

            // Fall back to LRCLIB
            if (lyrics == null && _lrclibService != null)
            {
                lyrics = await _lrclibService.GetLyricsAsync(
                    searchTitle,
                    searchArtists.ToArray(),
                    searchAlbum,
                    song.Duration ?? 0);
            }
        }

        if (lyrics == null)
        {
            return NotFound(new { error = "Lyrics not found" });
        }

        // Prefer synced lyrics, fall back to plain
        var lyricsText = lyrics.SyncedLyrics ?? lyrics.PlainLyrics ?? "";
        var isSynced = !string.IsNullOrEmpty(lyrics.SyncedLyrics);

        _logger.LogInformation(
            "Lyrics for {Artist} - {Track}: synced={HasSynced}, plainLength={PlainLen}, syncedLength={SyncLen}",
            song.Artist, song.Title, isSynced, lyrics.PlainLyrics?.Length ?? 0, lyrics.SyncedLyrics?.Length ?? 0);

        // Parse LRC format into individual lines for Jellyfin
        var lyricLines = new List<Dictionary<string, object>>();

        if (isSynced && !string.IsNullOrEmpty(lyrics.SyncedLyrics))
        {
            _logger.LogDebug("Parsing synced lyrics (LRC format)");
            // Parse LRC format: [mm:ss.xx] text
            // Skip ID tags like [ar:Artist], [ti:Title], etc.
            var lines = lyrics.SyncedLyrics.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                // Match timestamp format [mm:ss.xx] or [mm:ss.xxx]
                var match = System.Text.RegularExpressions.Regex.Match(line, @"^\[(\d+):(\d+)\.(\d+)\]\s*(.*)$");
                if (match.Success)
                {
                    var minutes = int.Parse(match.Groups[1].Value);
                    var seconds = int.Parse(match.Groups[2].Value);
                    var centiseconds = int.Parse(match.Groups[3].Value);
                    var text = match.Groups[4].Value;

                    // Convert to ticks (100 nanoseconds)
                    var totalMilliseconds = (minutes * 60 + seconds) * 1000 + centiseconds * 10;
                    var ticks = totalMilliseconds * 10000L;

                    // For synced lyrics, include Start timestamp
                    lyricLines.Add(new Dictionary<string, object>
                    {
                        ["Text"] = text,
                        ["Start"] = ticks
                    });
                }
                // Skip ID tags like [ar:Artist], [ti:Title], [length:2:23], etc.
            }

            _logger.LogDebug("Parsed {Count} synced lyric lines (skipped ID tags)", lyricLines.Count);
        }
        else if (!string.IsNullOrEmpty(lyricsText))
        {
            _logger.LogInformation("Splitting plain lyrics into lines (no timestamps)");
            // Plain lyrics - split by newlines and return each line separately
            // IMPORTANT: Do NOT include "Start" field at all for unsynced lyrics
            // Including it (even as null) causes clients to treat it as synced with timestamp 0:00
            var lines = lyricsText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                lyricLines.Add(new Dictionary<string, object>
                {
                    ["Text"] = line.Trim()
                });
            }

            _logger.LogDebug("Split into {Count} plain lyric lines", lyricLines.Count);
        }
        else
        {
            _logger.LogWarning("No lyrics text available");
            // No lyrics at all
            lyricLines.Add(new Dictionary<string, object>
            {
                ["Text"] = ""
            });
        }

        var response = new
        {
            Metadata = new
            {
                Artist = lyrics.ArtistName,
                Album = lyrics.AlbumName,
                Title = lyrics.TrackName,
                Length = lyrics.Duration,
                IsSynced = isSynced
            },
            Lyrics = lyricLines
        };

        _logger.LogDebug("Returning lyrics response: {LineCount} lines, synced={IsSynced}", lyricLines.Count, isSynced);

        // Log a sample of the response for debugging
        if (lyricLines.Count > 0)
        {
            var sampleLine = lyricLines[0];
            var hasStart = sampleLine.ContainsKey("Start");
            _logger.LogDebug("Sample line: Text='{Text}', HasStart={HasStart}",
                sampleLine.GetValueOrDefault("Text"), hasStart);
        }

        return Ok(response);
    }

    /// <summary>
    /// Proactively fetches and caches lyrics for a track in the background.
    /// Called when playback starts to ensure lyrics are ready when requested.
    /// </summary>
    private async Task PrefetchLyricsForTrackAsync(string itemId, bool isExternal, string? provider, string? externalId, CancellationToken cancellationToken = default)
    {
        try
        {
            Song? song = null;
            string? spotifyTrackId = null;

            if (isExternal && !string.IsNullOrEmpty(provider) && !string.IsNullOrEmpty(externalId))
            {
                // Get external track metadata
                song = await _metadataService.GetSongAsync(provider, externalId);

                // Try to find Spotify ID from matched tracks cache
                if (song != null)
                {
                    spotifyTrackId = await FindSpotifyIdForExternalTrackAsync(song);

                    // If no cached Spotify ID, try Odesli conversion
                    if (string.IsNullOrEmpty(spotifyTrackId) && provider == "squidwtf")
                    {
                        spotifyTrackId =
                            await _odesliService.ConvertTidalToSpotifyIdAsync(externalId, cancellationToken);
                    }
                }
            }
            else
            {
                // Get local track metadata from Jellyfin
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
                _logger.LogDebug("Could not get song metadata for lyrics prefetch: {ItemId}", itemId);
                return;
            }

            // Strip [S] suffix for lyrics search
            var searchTitle = song.Title.Replace(" [S]", "").Trim();
            var searchArtist = song.Artist?.Replace(" [S]", "").Trim() ?? "";
            var searchAlbum = song.Album?.Replace(" [S]", "").Trim() ?? "";
            var searchArtists = song.Artists.Select(a => a.Replace(" [S]", "").Trim()).ToList();

            if (searchArtists.Count == 0 && !string.IsNullOrEmpty(searchArtist))
            {
                searchArtists.Add(searchArtist);
            }

            _logger.LogDebug("🎵 Prefetching lyrics for: {Artist} - {Title}", searchArtist, searchTitle);

            // Use orchestrator for prefetching
            if (_lyricsOrchestrator != null)
            {
                await _lyricsOrchestrator.PrefetchLyricsAsync(
                    trackName: searchTitle,
                    artistNames: searchArtists.ToArray(),
                    albumName: searchAlbum,
                    durationSeconds: song.Duration ?? 0,
                    spotifyTrackId: spotifyTrackId);
                return;
            }

            // Fallback to manual prefetching if orchestrator not available
            _logger.LogWarning("LyricsOrchestrator not available for prefetch, using fallback method");

            // Try Spotify lyrics if we have a valid Spotify track ID
            if (_spotifyLyricsService != null && _spotifyApiSettings.Enabled && !string.IsNullOrEmpty(spotifyTrackId))
            {
                var cleanSpotifyId = spotifyTrackId.Replace("spotify:track:", "").Trim();

                if (cleanSpotifyId.Length == 22 && !cleanSpotifyId.Contains(":") && !cleanSpotifyId.Contains("local"))
                {
                    var spotifyLyrics = await _spotifyLyricsService.GetLyricsByTrackIdAsync(cleanSpotifyId);

                    if (spotifyLyrics != null && spotifyLyrics.Lines.Count > 0)
                    {
                        _logger.LogDebug("✓ Prefetched Spotify lyrics for {Artist} - {Title} ({LineCount} lines)",
                            searchArtist, searchTitle, spotifyLyrics.Lines.Count);
                        return; // Success, lyrics are now cached
                    }
                }
            }

            // Fall back to LyricsPlus
            if (_lyricsPlusService != null)
            {
                var lyrics = await _lyricsPlusService.GetLyricsAsync(
                    searchTitle,
                    searchArtists.ToArray(),
                    searchAlbum,
                    song.Duration ?? 0);

                if (lyrics != null)
                {
                    _logger.LogDebug("✓ Prefetched LyricsPlus lyrics for {Artist} - {Title}", searchArtist,
                        searchTitle);
                    return; // Success, lyrics are now cached
                }
            }

            // Fall back to LRCLIB
            if (_lrclibService != null)
            {
                var lyrics = await _lrclibService.GetLyricsAsync(
                    searchTitle,
                    searchArtists.ToArray(),
                    searchAlbum,
                    song.Duration ?? 0);

                if (lyrics != null)
                {
                    _logger.LogDebug("✓ Prefetched LRCLIB lyrics for {Artist} - {Title}", searchArtist, searchTitle);
                }
                else
                {
                    _logger.LogDebug("No lyrics found for {Artist} - {Title}", searchArtist, searchTitle);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to prefetch lyrics for track {ItemId}: {Message}", itemId, ex.Message);
        }
    }

    #endregion
}