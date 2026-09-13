using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using allstarr.Services.Common;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    #region Authentication

    [HttpPost("Users/AuthenticateByName")]
    public async Task<IActionResult> AuthenticateByName()
    {
        try
        {
            Request.EnableBuffering();

            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();

            Request.Body.Position = 0;

            // Never log request content or detailed headers; they contain credentials.
            _logger.LogDebug("Authentication request received");

            // Preserve client headers and Jellyfin's response verbatim for login compatibility.
            var (result, statusCode) =
                await _proxyService.PostJsonAsync("Users/AuthenticateByName", body, Request.Headers);

            if (result != null)
            {
                var responseJson = result.RootElement.GetRawText();

                if (statusCode == 200)
                {
                    _logger.LogInformation("Authentication successful");
                    if (_configuration.GetValue<bool>("Debug:LogAllRequests"))
                    {
                        var userHasPrimaryImage = result.RootElement.TryGetProperty("User", out var user) &&
                                                  user.TryGetProperty("PrimaryImageTag", out var primaryImageTag) &&
                                                  !string.IsNullOrWhiteSpace(primaryImageTag.GetString());
                        _logger.LogInformation(
                            "AUTH RESPONSE TRACE: user profile image advertised={UserHasPrimaryImage}",
                            userHasPrimaryImage);
                    }
                }
                else
                {
                    _logger.LogError("Authentication failed - status {StatusCode}", statusCode);
                }

                return new ContentResult
                {
                    Content = responseJson,
                    ContentType = "application/json",
                    StatusCode = statusCode
                };
            }

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
