using allstarr.Core.Protocols;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    #region Playlists

    [HttpGet("Playlists/{playlistId}", Order = 1)]
    public async Task<IActionResult> GetPlaylistDefinition(string playlistId)
    {
        if (_virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(playlistId))
        {
            var targetId = await ResolveWritablePlaylistTargetAsync(playlistId);
            if (targetId != null)
            {
                return await RelayCurrentRequestToPlaylistTargetAsync(
                    Request.Path.Value!.TrimStart('/'), playlistId, targetId);
            }

            return await _virtualPlaylistProtocolAdapter.ReadDefinitionAsync(
                       HttpContext.RequireProtocolExecutionContext(), playlistId, HttpContext.RequestAborted)
                   ?? NotFound();
        }

        if (PlaylistIdHelper.IsExternalPlaylist(playlistId))
        {
            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);
            var tracks = _providerGateway != null
                ? await _providerGateway.GetPlaylistTracksAsync(
                    HttpContext.RequireProtocolExecutionContext(), provider, externalId)
                : await _metadataService.GetPlaylistTracksAsync(provider, externalId);
            return new JsonResult(new
            {
                OpenAccess = false,
                Shares = Array.Empty<object>(),
                ItemIds = tracks.Select(track => track.Id).ToArray()
            });
        }

        var endpoint = $"Playlists/{playlistId}{Request.QueryString.Value}";
        var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);
        return HandleProxyResponse(result, statusCode);
    }

    /// <summary>
    /// Gets playlist tracks displayed as an album.
    /// </summary>
    private async Task<IActionResult> GetPlaylistAsAlbum(string playlistId)
    {
        try
        {
            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);

            var protocol = HttpContext.RequireProtocolExecutionContext();
            var playlist = _providerGateway != null
                ? await _providerGateway.GetPlaylistAsync(protocol, provider, externalId)
                : await _metadataService.GetPlaylistAsync(provider, externalId);
            if (playlist == null)
            {
                return _responseBuilder.CreateError(404, "Playlist not found");
            }

            var tracks = _providerGateway != null
                ? await _providerGateway.GetPlaylistTracksAsync(protocol, provider, externalId)
                : await _metadataService.GetPlaylistTracksAsync(provider, externalId);

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
                var targetId = await ResolveWritablePlaylistTargetAsync(playlistId);
                if (targetId != null)
                {
                    return await RelayCurrentRequestToPlaylistTargetAsync(
                        Request.Path.Value!.TrimStart('/'), playlistId, targetId);
                }

                return await _virtualPlaylistProtocolAdapter.ReadItemsAsync(
                           HttpContext.RequireProtocolExecutionContext(), playlistId, HttpContext.RequestAborted)
                       ?? _responseBuilder.CreateError(404, "Playlist not found");
            }

            // Check if this is an external playlist (Deezer/Qobuz) first
            if (PlaylistIdHelper.IsExternalPlaylist(playlistId))
            {
                var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);
                var tracks = _providerGateway != null
                    ? await _providerGateway.GetPlaylistTracksAsync(
                        HttpContext.RequireProtocolExecutionContext(), provider, externalId)
                    : await _metadataService.GetPlaylistTracksAsync(provider, externalId);

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
    private async Task<IActionResult> GetPlaylistImage(
        string playlistId,
        int? width = null,
        int? height = null,
        string? requestedFormat = null)
    {
        try
        {
            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);
            var protocol = HttpContext.GetProtocolExecutionContext();
            var playlist = _providerGateway != null && protocol != null
                ? await _providerGateway.GetPlaylistAsync(protocol, provider, externalId)
                : await _metadataService.GetPlaylistAsync(provider, externalId);

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

            var asset = await ResolveExternalImageAsync(
                provider, "playlist", externalId, validatedCoverUri,
                width: width, height: height);
            return asset == null ? NotFound() : CreateFormattedImageResponse(asset, requestedFormat);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get playlist image {PlaylistId}", playlistId);
            return NotFound();
        }
    }

    #endregion
}
