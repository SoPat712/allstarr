using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using allstarr.Services.Common;

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

                if (statusCode == 200)
                {
                    _logger.LogInformation("Authentication successful");
                }
                else
                {
                    _logger.LogError("Authentication failed - status {StatusCode}", statusCode);
                }

                // Return Jellyfin's exact response
                return new ContentResult
                {
                    Content = responseJson,
                    ContentType = "application/json",
                    StatusCode = statusCode
                };
            }

            // No response body from Jellyfin - return status code only
            _logger.LogWarning("Authentication request returned {StatusCode} with no response body", statusCode);
            return StatusCode(statusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during authentication");
            return StatusCode(500, new { error = "Authentication error" });
        }
    }

    #endregion
}
