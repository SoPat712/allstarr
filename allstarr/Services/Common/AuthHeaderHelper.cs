using Microsoft.AspNetCore.Http;

namespace allstarr.Services.Common;

public static class AuthHeaderHelper
{
    public static bool HasAuthentication(IHeaderDictionary headers) =>
        headers.ContainsKey("X-Emby-Authorization") ||
        headers.ContainsKey("X-Emby-Token") ||
        headers.ContainsKey("Authorization");

    public static bool ForwardAuthHeaders(IHeaderDictionary sourceHeaders, HttpRequestMessage targetRequest)
    {
        var authorization = BuildForwardedAuthorization(sourceHeaders);
        return authorization is not null &&
               targetRequest.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    public static string? BuildForwardedAuthorization(
        IHeaderDictionary sourceHeaders,
        string? fallbackDeviceId = null)
    {
        if (FirstValue(sourceHeaders, "Authorization") is { } authorization)
        {
            return authorization;
        }

        if (FirstValue(sourceHeaders, "X-Emby-Authorization") is { } legacyAuthorization)
        {
            return legacyAuthorization;
        }

        return FirstValue(sourceHeaders, "X-Emby-Token") is { } token
            ? CreateAuthHeader(
                token,
                "Allstarr",
                "Proxy",
                string.IsNullOrWhiteSpace(fallbackDeviceId) ? "allstarr-proxy" : fallbackDeviceId,
                AppVersion.Version)
            : null;
    }

    public static string? ExtractDeviceId(IHeaderDictionary headers) =>
        MediaBrowserAuthorization(headers) is { } value ? ExtractDeviceIdFromAuthString(value) : null;

    public static string? ExtractUserId(IHeaderDictionary headers)
    {
        foreach (var name in new[] { "X-Emby-Authorization", "Authorization" })
        {
            if (!headers.TryGetValue(name, out var values)) continue;
            foreach (var value in values)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    value ?? string.Empty,
                    @"(?:^|[,\s])UserId\s*=\s*""([^""]+)""",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value;
            }
        }

        return null;
    }

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

    public static string? ExtractClientName(IHeaderDictionary headers) =>
        MediaBrowserAuthorization(headers) is { } value ? ExtractClientNameFromAuthString(value) : null;

    public static string? ExtractToken(IHeaderDictionary headers)
    {
        if (headers.TryGetValue("X-Emby-Token", out var directToken))
        {
            return directToken.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        foreach (var name in new[] { "X-Emby-Authorization", "Authorization" })
        {
            if (!headers.TryGetValue(name, out var values))
            {
                continue;
            }

            foreach (var value in values)
            {
                if (ExtractTokenFromAuthorizationValue(value) is { Length: > 0 } token)
                {
                    return token;
                }
            }
        }

        return null;
    }

    private static string? ExtractTokenFromAuthorizationValue(string? value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            value ?? string.Empty,
            @"(?:^|[,\s])Token\s*=\s*""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return value?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? value["Bearer ".Length..].Trim()
            : null;
    }

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

    private static string? MediaBrowserAuthorization(IHeaderDictionary headers)
    {
        if (FirstValue(headers, "Authorization") is { } authorization &&
            authorization.Contains("MediaBrowser", StringComparison.OrdinalIgnoreCase))
        {
            return authorization;
        }

        return FirstValue(headers, "X-Emby-Authorization");
    }

    private static string? FirstValue(IHeaderDictionary headers, string name) =>
        headers.TryGetValue(name, out var values)
            ? values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : null;
}
