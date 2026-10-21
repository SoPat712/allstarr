using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    #region Playlists

    [HttpGet("Playlists/{playlistId}", Order = 1)]
    public async Task<IActionResult> GetPlaylistDefinition(string playlistId)
    {
        var writable = await ReadWritablePlaylistDefinitionAsync(playlistId);
        if (writable != null) return writable;
        if (_virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(playlistId))
        {
            return await _virtualPlaylistProtocolAdapter.ReadDefinitionAsync(
                       HttpContext.RequireProtocolExecutionContext(), playlistId, HttpContext.RequestAborted)
                   ?? NotFound();
        }

        var durable = await _virtualPlaylistProtocolAdapter.ReadDefinitionAsync(
            HttpContext.RequireProtocolExecutionContext(), playlistId, HttpContext.RequestAborted);
        if (durable != null) return durable;

        var linkedPlaylist = _spotifySettings.Enabled
            ? _spotifySettings.GetPlaylistByJellyfinId(playlistId)
            : null;
        if (linkedPlaylist != null)
        {
            var projection = await _virtualPlaylistProtocolAdapter.ReadDefinitionBySourceAsync(
                HttpContext.RequireProtocolExecutionContext(),
                "spotify",
                linkedPlaylist.Id,
                HttpContext.RequestAborted);
            if (projection != null) return projection;
        }

        if (PlaylistIdHelper.IsExternalPlaylist(playlistId))
        {
            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);
            var tracks = await _providerGateway.GetPlaylistTracksAsync(
                HttpContext.RequireProtocolExecutionContext(), provider, externalId);
            return new JsonResult(new
            {
                OpenAccess = false,
                CanEdit = false,
                Shares = Array.Empty<object>(),
                ItemIds = tracks.Select(track => track.Id).ToArray()
            });
        }

        var endpoint = $"Playlists/{playlistId}{Request.QueryString.Value}";
        var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);
        return HandleProxyResponse(result, statusCode);
    }

    private async Task<IActionResult> GetPlaylistAsAlbum(string playlistId)
    {
        try
        {
            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);

            var protocol = HttpContext.RequireProtocolExecutionContext();
            var playlist = await _providerGateway.GetPlaylistAsync(protocol, provider, externalId);
            if (playlist == null)
            {
                return _responseBuilder.CreateError(404, "Playlist not found");
            }

            var tracks = await _providerGateway.GetPlaylistTracksAsync(protocol, provider, externalId);

            return _responseBuilder.CreatePlaylistAsAlbumResponse(playlist, tracks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting playlist {PlaylistId}", playlistId);
            return _responseBuilder.CreateError(500, "Failed to get playlist");
        }
    }

    private async Task<IActionResult> GetPlaylistTracks(string playlistId)
    {
        try
        {
            _logger.LogDebug("=== GetPlaylistTracks called === PlaylistId: {PlaylistId}", playlistId);

            if (_virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(playlistId))
            {
                return await _virtualPlaylistProtocolAdapter.ReadItemsAsync(
                           HttpContext.RequireProtocolExecutionContext(), playlistId,
                           Request.Headers, Request.Query, HttpContext.RequestAborted)
                       ?? _responseBuilder.CreateError(404, "Playlist not found");
            }

            var durable = await _virtualPlaylistProtocolAdapter.ReadItemsAsync(
                HttpContext.RequireProtocolExecutionContext(), playlistId,
                Request.Headers, Request.Query, HttpContext.RequestAborted);
            if (durable != null) return durable;

            var linkedPlaylist = _spotifySettings.Enabled
                ? _spotifySettings.GetPlaylistByJellyfinId(playlistId)
                : null;
            if (linkedPlaylist != null)
            {
                var projection = await _virtualPlaylistProtocolAdapter.ReadItemsBySourceAsync(
                    HttpContext.RequireProtocolExecutionContext(),
                    "spotify",
                    linkedPlaylist.Id,
                    playlistId,
                    Request.Headers,
                    Request.Query,
                    HttpContext.RequestAborted);
                if (projection != null) return projection;

                _logger.LogWarning(
                    "Configured Spotify playlist {PlaylistId} has no visible durable projection; using Jellyfin",
                    playlistId);
            }

            if (PlaylistIdHelper.IsExternalPlaylist(playlistId))
            {
                var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);
                var tracks = await _providerGateway.GetPlaylistTracksAsync(
                    HttpContext.RequireProtocolExecutionContext(), provider, externalId);

                var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
                var fields = new JellyfinItemFields(Request.Query);
                // Provider rows have no entry token; repeated copies use their ordinal within the same track.
                var items = tracks.Select(track =>
                {
                    var ordinal = occurrences.GetValueOrDefault(track.Id);
                    occurrences[track.Id] = ordinal + 1;
                    var identity = JsonSerializer.Serialize(new object[] { playlistId, track.Id, ordinal });
                    var entryId = "allstarr-entry-" + Convert.ToHexStringLower(
                        SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                    var item = QuerySong(track);
                    if (fields.Includes("ParentId")) item["ParentId"] = playlistId;
                    item["PlaylistItemId"] = entryId;
                    item["AlbumId"] = playlistId;
                    item["AlbumPrimaryImageTag"] = playlistId;
                    return item;
                }).ToList();

                return await ShapeProviderQueryAsync(items, deduplicate: false, totalRecordCount: tracks.Count);
            }

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

    private async Task<IActionResult> GetPlaylistImage(
        string playlistId,
        int? width = null,
        int? height = null,
        string? requestedFormat = null)
    {
        try
        {
            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);
            var asset = await ResolveExternalImageAsync(
                provider, "playlist", externalId, width: width, height: height);
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
