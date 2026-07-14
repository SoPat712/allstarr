using allstarr.Core.Protocols;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    #region Playlists

    /// <summary>
    /// Gets playlist tracks displayed as an album.
    /// </summary>
    private async Task<IActionResult> GetPlaylistAsAlbum(string playlistId)
    {
        try
        {
            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);

            var playlist = await _metadataService.GetPlaylistAsync(provider, externalId);
            if (playlist == null)
            {
                return _responseBuilder.CreateError(404, "Playlist not found");
            }

            var tracks = await _metadataService.GetPlaylistTracksAsync(provider, externalId);

            // Cache tracks for playlist sync
            if (_playlistSyncService != null)
            {
                foreach (var track in tracks)
                {
                    if (!string.IsNullOrEmpty(track.ExternalId))
                    {
                        var trackId = $"ext-{provider}-{track.ExternalId}";
                        _playlistSyncService.AddTrackToPlaylistCache(trackId, playlistId);
                    }
                }

                _logger.LogDebug("Cached {Count} tracks for playlist {PlaylistId}", tracks.Count, playlistId);
            }

            return _responseBuilder.CreatePlaylistAsAlbumResponse(playlist, tracks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting playlist {PlaylistId}", playlistId);
            return _responseBuilder.CreateError(500, "Failed to get playlist");
        }
    }

    /// <summary>
    /// Gets playlist tracks as child items.
    /// </summary>
    private async Task<IActionResult> GetPlaylistTracks(string playlistId)
    {
        try
        {
            _logger.LogDebug("=== GetPlaylistTracks called === PlaylistId: {PlaylistId}", playlistId);

            if (_virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(playlistId))
            {
                return await _virtualPlaylistProtocolAdapter.ReadItemsAsync(
                           HttpContext.RequireProtocolExecutionContext(), playlistId, HttpContext.RequestAborted)
                       ?? _responseBuilder.CreateError(404, "Playlist not found");
            }

            // Check if this is an external playlist (Deezer/Qobuz) first
            if (PlaylistIdHelper.IsExternalPlaylist(playlistId))
            {
                var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);
                var tracks = await _metadataService.GetPlaylistTracksAsync(provider, externalId);

                // Convert tracks to Jellyfin items and override ParentId/AlbumId to be the playlist
                var items = tracks.Select(track =>
                {
                    var item = _responseBuilder.ConvertSongToJellyfinItem(track);
                    // Override ParentId and AlbumId to be the playlist ID
                    // This makes all tracks appear to be from the same "album" (the playlist)
                    item["ParentId"] = playlistId;
                    item["AlbumId"] = playlistId;
                    item["AlbumPrimaryImageTag"] = playlistId;
                    item["ParentLogoItemId"] = playlistId;
                    item["ParentLogoImageTag"] = playlistId;
                    item["ParentBackdropItemId"] = playlistId;
                    return item;
                }).ToList();

                return new JsonResult(new
                {
                    Items = items,
                    TotalRecordCount = items.Count,
                    StartIndex = 0
                });
            }

            // Check if this is a Spotify playlist (by ID)
            _logger.LogDebug("Spotify Import Enabled: {Enabled}, Configured Playlists: {Count}",
                _spotifySettings.Enabled, _spotifySettings.Playlists.Count);

            if (_spotifySettings.Enabled && _spotifySettings.IsSpotifyPlaylist(playlistId))
            {
                // Get playlist info from Jellyfin to get the name for matching missing tracks
                _logger.LogDebug("Fetching playlist info from Jellyfin for ID: {PlaylistId}", playlistId);
                var (playlistInfo, _) = await _proxyService.GetJsonAsync($"Items/{playlistId}", null, Request.Headers);

                if (playlistInfo != null && playlistInfo.RootElement.TryGetProperty("Name", out var nameElement))
                {
                    var playlistName = nameElement.GetString() ?? "";
                    _logger.LogInformation(
                        "Intercepting Spotify playlist: {PlaylistName} (ID: {PlaylistId})",
                        playlistName, playlistId);
                    return await GetSpotifyPlaylistTracksAsync(playlistName, playlistId);
                }
                else
                {
                    _logger.LogWarning("Could not get playlist name from Jellyfin for ID: {PlaylistId}", playlistId);
                }
            }

            // Regular Jellyfin playlist - proxy through
            var endpoint = $"Playlists/{playlistId}/Items";
            if (Request.QueryString.HasValue)
            {
                endpoint = $"{endpoint}{Request.QueryString.Value}";
            }

            _logger.LogDebug("Proxying to Jellyfin: {Endpoint}", endpoint);
            var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);

            return HandleProxyResponse(result, statusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting playlist tracks {PlaylistId}", playlistId);
            return _responseBuilder.CreateError(500, "Failed to get playlist tracks");
        }
    }

    /// <summary>
    /// Gets a playlist cover image.
    /// </summary>
    private async Task<IActionResult> GetPlaylistImage(string playlistId)
    {
        try
        {
            // Check cache first (1 hour TTL for playlist images since they can change)
            var cacheKey = $"playlist:image:{playlistId}";
            var cachedImage = await _cache.GetAsync<byte[]>(cacheKey);

            if (cachedImage != null)
            {
                _logger.LogDebug("Serving cached playlist image for {PlaylistId}", playlistId);
                return File(cachedImage, "image/jpeg");
            }

            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);
            var playlist = await _metadataService.GetPlaylistAsync(provider, externalId);

            if (playlist == null || string.IsNullOrEmpty(playlist.CoverUrl))
            {
                return NotFound();
            }

            if (!OutboundRequestGuard.TryCreateSafeHttpUri(playlist.CoverUrl, out var validatedCoverUri,
                    out var validationReason) || validatedCoverUri == null)
            {
                _logger.LogWarning("Blocked playlist image URL fetch for {PlaylistId}: {Reason}",
                    playlistId, validationReason);
                return NotFound();
            }

            var coverUri = validatedCoverUri!;
            var response = await _proxyService.HttpClient.GetAsync(coverUri);
            if (!response.IsSuccessStatusCode)
            {
                return NotFound();
            }

            var imageBytes = await response.Content.ReadAsByteArrayAsync();
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";

            // Cache for configurable duration (playlists can change)
            await _cache.SetAsync(cacheKey, imageBytes, CacheExtensions.PlaylistImagesTTL);
            _logger.LogDebug("Cached playlist image for {PlaylistId}", playlistId);

            return File(imageBytes, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get playlist image {PlaylistId}", playlistId);
            return NotFound();
        }
    }

    #endregion
}
