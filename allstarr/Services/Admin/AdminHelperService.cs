using System.Text.Json;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Services.Common;

namespace allstarr.Services.Admin;

public class AdminHelperService
{
    private readonly JellyfinSettings _jellyfinSettings;

    public AdminHelperService(IOptions<JellyfinSettings> jellyfinSettings)
    {
        _jellyfinSettings = jellyfinSettings.Value;
    }

    public string GetJellyfinAuthHeader()
    {
        return $"MediaBrowser Client=\"Allstarr\", Device=\"Server\", DeviceId=\"allstarr-admin\", Version=\"{AppVersion.Version}\", Token=\"{_jellyfinSettings.ApiKey}\"";
    }

    public static string MaskValue(string? value, int showLast = 0)
    {
        if (string.IsNullOrEmpty(value)) return "(not set)";
        if (value.Length <= showLast) return "***";
        return showLast > 0 ? "***" + value[^showLast..] : value[..8] + "...";
    }

    public static string SanitizeFileName(string name)
    {
        return string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
    }

    /// <summary>
    /// Truncates a string for safe logging, adding ellipsis if truncated.
    /// </summary>
    public static string TruncateForLogging(string? str, int maxLength)
    {
        if (string.IsNullOrEmpty(str))
            return str ?? string.Empty;

        if (str.Length <= maxLength)
            return str;

        return str[..maxLength] + "...";
    }

    /// <summary>
    /// Validates if a username is safe (no control characters or shell metacharacters).
    /// </summary>
    public static bool IsValidUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return false;

        // Reject control characters and dangerous shell metacharacters
        var dangerousChars = new[] { '\n', '\r', '\t', ';', '|', '&', '`', '$', '(', ')' };
        return !username.Any(c => char.IsControl(c) || dangerousChars.Contains(c));
    }

    /// <summary>
    /// Validates if a password is safe (no control characters).
    /// </summary>
    public static bool IsValidPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return false;

        // Reject control characters (except space which is allowed)
        return !password.Any(c => char.IsControl(c));
    }

    /// <summary>
    /// Validates if a URL is safe (http or https only).
    /// </summary>
    public static bool IsValidUrl(string? urlString)
    {
        if (string.IsNullOrWhiteSpace(urlString))
            return false;

        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri))
            return false;

        // Only allow http and https
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    /// <summary>
    /// Validates if a file path is safe (no shell metacharacters or control characters).
    /// </summary>
    public static bool IsValidPath(string? pathString)
    {
        if (string.IsNullOrWhiteSpace(pathString))
            return false;

        // Reject control characters and dangerous shell metacharacters
        var dangerousChars = new[] { '\n', '\r', '\0', ';', '|', '&', '`', '$' };
        return !pathString.Any(c => char.IsControl(c) || dangerousChars.Contains(c));
    }

    /// <summary>
    /// Sanitizes HTML by escaping special characters to prevent XSS.
    /// </summary>
    public static string SanitizeHtml(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return html ?? string.Empty;

        return html
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    /// <summary>
    /// Removes control characters from a string for safe logging/display.
    /// </summary>
    public static string RemoveControlCharacters(string? str)
    {
        if (string.IsNullOrEmpty(str))
            return str ?? string.Empty;

        return new string(str.Where(c => !char.IsControl(c)).ToArray());
    }

    public static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    public static bool HasValue(object? obj)
    {
        if (obj == null) return false;
        if (obj is JsonElement jsonEl) return jsonEl.ValueKind != JsonValueKind.Null && jsonEl.ValueKind != JsonValueKind.Undefined;
        return true;
    }

    /// <summary>
    /// Create an authenticated HTTP request to Jellyfin API
    /// </summary>
    public HttpRequestMessage CreateJellyfinRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Emby-Authorization", GetJellyfinAuthHeader());
        return request;
    }

}
