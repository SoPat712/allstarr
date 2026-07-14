using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace allstarr.Services.Common;

/// <summary>
/// Utility class for handling Jellyfin/Emby authentication headers.
/// Centralizes logic for extracting and forwarding authentication headers.
/// </summary>
public static class AuthHeaderHelper
{
    /// <summary>
    /// Forwards authentication headers from HTTP request to HttpRequestMessage.
    /// Handles both X-Emby-Authorization and Authorization headers.
    /// </summary>
    /// <param name="sourceHeaders">Source headers (from HttpRequest or IHeaderDictionary)</param>
    /// <param name="targetRequest">Target HttpRequestMessage</param>
    /// <returns>True if auth header was added, false otherwise</returns>
    public static bool ForwardAuthHeaders(IHeaderDictionary sourceHeaders, HttpRequestMessage targetRequest)
    {
        // Try X-Emby-Authorization first (case-insensitive)
        foreach (var header in sourceHeaders)
        {
            if (header.Key.Equals("X-Emby-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                var headerValue = header.Value.ToString();
                targetRequest.Headers.TryAddWithoutValidation("X-Emby-Authorization", headerValue);
                return true;
            }
        }

        // Some Jellyfin clients send the raw token separately instead of a MediaBrowser auth header.
        foreach (var header in sourceHeaders)
        {
            if (header.Key.Equals("X-Emby-Token", StringComparison.OrdinalIgnoreCase))
            {
                var headerValue = header.Value.ToString();
                targetRequest.Headers.TryAddWithoutValidation("X-Emby-Token", headerValue);
                return true;
            }
        }

        // If no X-Emby-Authorization, check if Authorization header contains MediaBrowser format
        foreach (var header in sourceHeaders)
        {
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                var headerValue = header.Value.ToString();

                // Check if it's a MediaBrowser/Jellyfin auth header
                if (headerValue.Contains("MediaBrowser", StringComparison.OrdinalIgnoreCase) ||
                    headerValue.Contains("Client=", StringComparison.OrdinalIgnoreCase) ||
                    headerValue.Contains("Token=", StringComparison.OrdinalIgnoreCase))
                {
                    // Forward as X-Emby-Authorization (Jellyfin's expected header)
                    targetRequest.Headers.TryAddWithoutValidation("X-Emby-Authorization", headerValue);
                    return true;
                }
                else
                {
                    // Standard Bearer token
                    targetRequest.Headers.TryAddWithoutValidation("Authorization", headerValue);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts device ID from X-Emby-Authorization header.
    /// </summary>
    /// <param name="headers">Request headers</param>
    /// <returns>Device ID if found, null otherwise</returns>
    public static string? ExtractDeviceId(IHeaderDictionary headers)
    {
        if (headers.TryGetValue("X-Emby-Authorization", out var authHeader))
        {
            var authValue = authHeader.ToString();
            return ExtractDeviceIdFromAuthString(authValue);
        }

        if (headers.TryGetValue("Authorization", out var authHeader2))
        {
            var authValue = authHeader2.ToString();
            if (authValue.Contains("MediaBrowser", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractDeviceIdFromAuthString(authValue);
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts device ID from MediaBrowser auth string.
    /// Format: MediaBrowser Client="...", Device="...", DeviceId="...", Version="...", Token="..."
    /// </summary>
    private static string? ExtractDeviceIdFromAuthString(string authValue)
    {
        var deviceIdMatch = System.Text.RegularExpressions.Regex.Match(
            authValue,
            @"DeviceId=""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (deviceIdMatch.Success)
        {
            return deviceIdMatch.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Extracts client name from MediaBrowser auth string.
    /// </summary>
    public static string? ExtractClientName(IHeaderDictionary headers)
    {
        if (headers.TryGetValue("X-Emby-Authorization", out var authHeader))
        {
            var authValue = authHeader.ToString();
            return ExtractClientNameFromAuthString(authValue);
        }

        if (headers.TryGetValue("Authorization", out var authHeader2))
        {
            var authValue = authHeader2.ToString();
            if (authValue.Contains("MediaBrowser", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractClientNameFromAuthString(authValue);
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts client name from MediaBrowser auth string.
    /// </summary>
    private static string? ExtractClientNameFromAuthString(string authValue)
    {
        var clientMatch = System.Text.RegularExpressions.Regex.Match(
            authValue,
            @"Client=""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (clientMatch.Success)
        {
            return clientMatch.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Creates a MediaBrowser auth header string.
    /// </summary>
    public static string CreateAuthHeader(string token, string? client = null, string? device = null, string? deviceId = null, string? version = null)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(client))
            parts.Add($"Client=\"{client}\"");

        if (!string.IsNullOrEmpty(device))
            parts.Add($"Device=\"{device}\"");

        if (!string.IsNullOrEmpty(deviceId))
            parts.Add($"DeviceId=\"{deviceId}\"");

        if (!string.IsNullOrEmpty(version))
            parts.Add($"Version=\"{version}\"");

        parts.Add($"Token=\"{token}\"");

        return $"MediaBrowser {string.Join(", ", parts)}";
    }
}
