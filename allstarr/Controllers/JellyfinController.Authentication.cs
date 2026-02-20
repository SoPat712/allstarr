using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    #region Authentication

    /// <summary>
    /// Authenticates a user by username and password.
    /// This is the primary login endpoint for Jellyfin clients.
    /// </summary>
    [HttpPost("Users/AuthenticateByName")]
    public async Task<IActionResult> AuthenticateByName()
    {
        try
        {
            // Enable buffering to allow multiple reads of the request body
            Request.EnableBuffering();

            // Read the request body
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();

            // Reset stream position
            Request.Body.Position = 0;

            _logger.LogDebug("Authentication request received");
            // DO NOT log request body or detailed headers - contains password

            // Forward to Jellyfin server with client headers - completely transparent proxy
            var (result, statusCode) =
                await _proxyService.PostJsonAsync("Users/AuthenticateByName", body, Request.Headers);

            // Pass through Jellyfin's response exactly as-is (transparent proxy)
            if (result != null)
            {
                var responseJson = result.RootElement.GetRawText();

                // On successful auth, extract access token and post session capabilities in background
                if (statusCode == 200)
                {
                    _logger.LogInformation("Authentication successful");

                    // Extract access token from response for session capabilities
                    string? accessToken = null;
                    if (result.RootElement.TryGetProperty("AccessToken", out var tokenEl))
                    {
                        accessToken = tokenEl.GetString();
                    }

                    // Post session capabilities in background if we have a token
                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        // Capture token in closure - don't use Request.Headers (will be disposed)
                        var token = accessToken;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                _logger.LogDebug("🔧 Posting session capabilities after authentication");

                                // Build auth header with the new token
                                var authHeaders = new HeaderDictionary
                                {
                                    ["X-Emby-Token"] = token
                                };

                                var capabilities = new
                                {
                                    PlayableMediaTypes = new[] { "Audio" },
                                    SupportedCommands = Array.Empty<string>(),
                                    SupportsMediaControl = false,
                                    SupportsPersistentIdentifier = true,
                                    SupportsSync = false
                                };

                                var capabilitiesJson = JsonSerializer.Serialize(capabilities);
                                var (capResult, capStatus) =
                                    await _proxyService.PostJsonAsync("Sessions/Capabilities/Full", capabilitiesJson,
                                        authHeaders);

                                if (capStatus == 204 || capStatus == 200)
                                {
                                    _logger.LogDebug("✓ Session capabilities posted after auth ({StatusCode})",
                                        capStatus);
                                }
                                else
                                {
                                    _logger.LogDebug("⚠ Session capabilities returned {StatusCode} after auth",
                                        capStatus);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to post session capabilities after auth");
                            }
                        });
                    }
                }
                else
                {
                    _logger.LogError("Authentication failed - status {StatusCode}", statusCode);
                }

                // Return Jellyfin's exact response
                return Content(responseJson, "application/json");
            }

            // No response body from Jellyfin - return status code only
            _logger.LogWarning("Authentication request returned {StatusCode} with no response body", statusCode);
            return StatusCode(statusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during authentication");
            return StatusCode(500, new { error = $"Authentication error: {ex.Message}" });
        }
    }

    #endregion
}