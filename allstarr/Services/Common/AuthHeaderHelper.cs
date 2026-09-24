using Microsoft.AspNetCore.Http;

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
        var auth = GetForwardAuthHeader(sourceHeaders);
        return auth is { } selected &&
               targetRequest.Headers.TryAddWithoutValidation(selected.Name, selected.Value);
    }

    public static (string Name, string Value)? GetForwardAuthHeader(IHeaderDictionary sourceHeaders)
    {
        if (sourceHeaders.TryGetValue("Authorization", out var authorization) &&
            !string.IsNullOrWhiteSpace(authorization))
        {
            return ("Authorization", authorization.ToString());
        }

        if (sourceHeaders.TryGetValue("X-Emby-Authorization", out var legacyAuthorization) &&
            !string.IsNullOrWhiteSpace(legacyAuthorization))
        {
            var value = legacyAuthorization.ToString();
            return value.StartsWith("MediaBrowser ", StringComparison.OrdinalIgnoreCase)
                ? ("Authorization", value)
                : ("X-Emby-Authorization", value);
        }

        if (sourceHeaders.TryGetValue("X-Emby-Token", out var legacyToken) &&
            !string.IsNullOrWhiteSpace(legacyToken))
        {
            return ("X-Emby-Token", legacyToken.ToString());
        }

        return null;
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
