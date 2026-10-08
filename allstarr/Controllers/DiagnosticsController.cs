using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Filters;
using allstarr.Services.Jellyfin;
using allstarr.Services.Common;
using allstarr.Services.Admin;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public class DiagnosticsController : ControllerBase
{
    private readonly ILogger<DiagnosticsController> _logger;
    private readonly SpotifyImportSettings _spotifyImportSettings;
    private readonly JellyfinSettings _jellyfinSettings;
    private readonly BackendSelectionAuthority _backendSelection;

    public DiagnosticsController(
        ILogger<DiagnosticsController> logger,
        IConfiguration configuration,
        IOptions<SpotifyImportSettings> spotifyImportSettings,
        IOptions<JellyfinSettings> jellyfinSettings,
        BackendSelectionAuthority? backendSelection = null)
    {
        _logger = logger;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _jellyfinSettings = jellyfinSettings.Value;
        _backendSelection = backendSelection ?? new BackendSelectionAuthority(
            Enum.TryParse<BackendType>(
                configuration["Backend:Type"],
                ignoreCase: true,
                out var configuredBackend)
                ? configuredBackend
                : BackendType.Jellyfin,
            configuration["Backend:Type"] ?? BackendType.Jellyfin.ToString(),
            "test-or-legacy-construction",
            false,
            false,
            null);
    }

    [HttpGet("media-probe")]
    public async Task<IActionResult> ProbeMediaPipeline(CancellationToken cancellationToken = default)
    {
        var backendType = _backendSelection.EffectiveValue;
        if (!backendType.Equals("Jellyfin", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new
            {
                success = false,
                backend = backendType,
                code = "probe_not_supported",
                message = "The media probe currently supports Jellyfin backends."
            });
        }

        var proxy = HttpContext.RequestServices.GetService<JellyfinProxyService>();
        if (proxy == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                success = false,
                backend = "Jellyfin",
                code = "proxy_unavailable",
                message = "The Jellyfin proxy service is unavailable."
            });
        }

        try
        {
            var itemsEndpoint = string.IsNullOrWhiteSpace(_jellyfinSettings.UserId)
                ? "Items"
                : $"Users/{Uri.EscapeDataString(_jellyfinSettings.UserId)}/Items";
            var (itemsDocument, statusCode) = await proxy.GetJsonAsyncInternal(
                itemsEndpoint,
                new Dictionary<string, string>
                {
                    ["Recursive"] = "true",
                    ["IncludeItemTypes"] = "Audio",
                    ["Limit"] = "25",
                    ["Fields"] = "PrimaryImageAspectRatio,ProviderIds"
                });
            using (itemsDocument)
            {
                if (statusCode < 200 || statusCode >= 300 || itemsDocument == null)
                {
                    return StatusCode(StatusCodes.Status502BadGateway, new
                    {
                        success = false,
                        backend = "Jellyfin",
                        code = "metadata_probe_failed",
                        metadataStatus = statusCode,
                        message = "Jellyfin did not return library metadata."
                    });
                }

                if (!itemsDocument.RootElement.TryGetProperty("Items", out var items) ||
                    items.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    return StatusCode(StatusCodes.Status502BadGateway, new
                    {
                        success = false,
                        backend = "Jellyfin",
                        code = "metadata_shape_invalid",
                        metadataStatus = statusCode,
                        message = "Jellyfin returned an unexpected library response."
                    });
                }

                var candidate = items.EnumerateArray().FirstOrDefault(item =>
                    item.TryGetProperty("Id", out var id) &&
                    id.ValueKind == System.Text.Json.JsonValueKind.String &&
                    item.TryGetProperty("ImageTags", out var tags) &&
                    tags.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    tags.TryGetProperty("Primary", out var primary) &&
                    primary.ValueKind == System.Text.Json.JsonValueKind.String);

                if (candidate.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                {
                    return Ok(new
                    {
                        success = false,
                        backend = "Jellyfin",
                        code = "no_artwork_candidate",
                        metadataStatus = statusCode,
                        checkedItems = items.GetArrayLength(),
                        message = "No audio item with primary artwork was found in the probe sample."
                    });
                }

                var itemId = candidate.GetProperty("Id").GetString()!;
                var imageTag = candidate.GetProperty("ImageTags").GetProperty("Primary").GetString();
                var (imageBytes, contentType) = await proxy.GetBytesAsync(
                    $"Items/{Uri.EscapeDataString(itemId)}/Images/Primary",
                    new Dictionary<string, string>
                    {
                        ["maxWidth"] = "300",
                        ["maxHeight"] = "300",
                        ["tag"] = imageTag ?? string.Empty
                    });
                var validImage = imageBytes is { Length: > 0 } &&
                                 contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
                var playerRouteTested = false;
                var playerRouteAvailable = false;
                string? playerRouteContentType = null;
                var playerRouteBytes = 0;
                var playerStreamTested = false;
                var playerStreamAvailable = false;
                var playerStreamStatus = 0;
                var playerStreamBytes = 0;
                string? playerStreamContentType = null;
                if (HttpContext.Items.TryGetValue(
                        AdminAuthSessionService.HttpContextSessionItemKey,
                        out var sessionValue) &&
                    sessionValue is AdminAuthSession session &&
                    !string.IsNullOrWhiteSpace(session.JellyfinAccessToken))
                {
                    playerRouteTested = true;
                    var playerHeaders = new HeaderDictionary
                    {
                        ["X-Emby-Token"] = session.JellyfinAccessToken
                    };
                    var playerResult = await proxy.GetBytesSafeAsync(
                        $"Items/{Uri.EscapeDataString(itemId)}/Images/Primary",
                        new Dictionary<string, string>
                        {
                            ["maxWidth"] = "300",
                            ["maxHeight"] = "300",
                            ["tag"] = imageTag ?? string.Empty
                        },
                        playerHeaders);
                    playerRouteContentType = playerResult.ContentType;
                    playerRouteBytes = playerResult.Body?.Length ?? 0;
                    playerRouteAvailable = playerResult.Success &&
                                           playerRouteBytes > 0 &&
                                           playerRouteContentType?.StartsWith(
                                               "image/",
                                               StringComparison.OrdinalIgnoreCase) == true;

                    playerStreamTested = true;
                    var streamResult = await proxy.ProbeAudioStreamAsync(
                        itemId,
                        playerHeaders,
                        cancellationToken: cancellationToken);
                    playerStreamStatus = streamResult.StatusCode;
                    playerStreamBytes = streamResult.BytesRead;
                    playerStreamContentType = streamResult.ContentType;
                    playerStreamAvailable = streamResult.Success;
                }

                var successfulPipeline = validImage &&
                                         (!playerRouteTested || playerRouteAvailable) &&
                                         (!playerStreamTested || playerStreamAvailable);

                return Ok(new
                {
                    success = successfulPipeline,
                    backend = "Jellyfin",
                    code = successfulPipeline ? "media_pipeline_healthy" : "artwork_probe_failed",
                    metadataStatus = statusCode,
                    checkedItems = items.GetArrayLength(),
                    artwork = new
                    {
                        available = validImage,
                        contentType = validImage ? contentType : null,
                        bytes = imageBytes?.Length ?? 0
                    },
                    playerArtwork = new
                    {
                        tested = playerRouteTested,
                        available = playerRouteAvailable,
                        contentType = playerRouteAvailable ? playerRouteContentType : null,
                        bytes = playerRouteBytes
                    },
                    playerStreaming = new
                    {
                        tested = playerStreamTested,
                        available = playerStreamAvailable,
                        status = playerStreamStatus,
                        contentType = playerStreamAvailable ? playerStreamContentType : null,
                        bytes = playerStreamBytes
                    },
                    message = successfulPipeline
                        ? playerRouteTested
                            ? "Jellyfin metadata, authenticated player artwork, and audio streaming are available through Allstarr."
                            : "Jellyfin metadata and album artwork are available through Allstarr."
                        : playerStreamTested && !playerStreamAvailable
                            ? "Jellyfin metadata and artwork worked, but an authenticated player could not read audio bytes."
                        : playerRouteTested && !playerRouteAvailable
                            ? "Jellyfin metadata worked, but an authenticated player could not retrieve the selected artwork."
                            : "Jellyfin metadata worked, but Allstarr could not retrieve the selected artwork."
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyfin media pipeline probe failed");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                success = false,
                backend = "Jellyfin",
                code = "media_probe_failed",
                message = "The Jellyfin media pipeline probe failed."
            });
        }
    }

    [HttpGet("playlist-readiness")]
    public IActionResult ProbePlaylistReadiness()
    {
        var configured = _spotifyImportSettings.Playlists
            .Where(playlist => !string.IsNullOrWhiteSpace(playlist.Name))
            .ToList();

        const bool sourceReady = false;
        var success = configured.Count == 0 || sourceReady;
        var code = configured.Count == 0
            ? "no_playlists_configured"
            : sourceReady
                ? "playlist_pipeline_healthy"
                : "playlist_source_unavailable";
        var message = code switch
        {
            "no_playlists_configured" => "No provider playlists are configured yet.",
            "playlist_pipeline_healthy" => "Provider playlist access is ready.",
            _ => "Reconnect Spotify, then refresh and match the configured playlists."
        };

        return Ok(new
        {
            success,
            code,
            message,
            configuredPlaylists = configured.Count,
            sourcePlaylists = 0,
            renderedPlaylists = 0,
            sourceTracks = 0,
            playableItems = 0,
            unavailableItems = 0,
            affectedPlaylists = Array.Empty<string>(),
            source = new
            {
                provider = "spotify",
                ready = sourceReady,
                health = "unknown",
                reasonCode = configured.Count == 0 ? null : "provider_account_required"
            }
        });
    }
}
