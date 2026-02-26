using System.Text.Json;
using System.Globalization;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Spotify;

/// <summary>
/// Stores and resolves Spotify session cookies in a user-scoped model.
/// </summary>
public class SpotifySessionCookieService
{
    private const string UserCookieMapKey = "SPOTIFY_API_SESSION_COOKIES";
    private const string UserCookieSetDatesKey = "SPOTIFY_API_SESSION_COOKIE_SET_DATES";

    private readonly SpotifyApiSettings _spotifyApiSettings;
    private readonly AdminHelperService _adminHelper;
    private readonly ILogger<SpotifySessionCookieService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SpotifySessionCookieService(
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        AdminHelperService adminHelper,
        ILogger<SpotifySessionCookieService> logger)
    {
        _spotifyApiSettings = spotifyApiSettings.Value;
        _adminHelper = adminHelper;
        _logger = logger;
    }

    public async Task<string?> ResolveSessionCookieAsync(string? userId)
    {
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var map = await ReadUserCookieMapAsync();
            var normalizedUserId = userId.Trim();
            if (map.TryGetValue(normalizedUserId, out var cookie) &&
                !string.IsNullOrWhiteSpace(cookie))
            {
                return cookie;
            }
        }

        return string.IsNullOrWhiteSpace(_spotifyApiSettings.SessionCookie)
            ? null
            : _spotifyApiSettings.SessionCookie;
    }

    public async Task<bool> HasAnyConfiguredCookieAsync()
    {
        if (!string.IsNullOrWhiteSpace(_spotifyApiSettings.SessionCookie))
        {
            return true;
        }

        var userCookieMap = await ReadUserCookieMapAsync();
        return userCookieMap.Values.Any(value => !string.IsNullOrWhiteSpace(value));
    }

    public async Task<(bool HasCookie, bool UsingGlobalFallback)> GetCookieStatusAsync(string? userId)
    {
        var userCookie = string.Empty;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var userCookieMap = await ReadUserCookieMapAsync();
            userCookieMap.TryGetValue(userId.Trim(), out userCookie);
        }

        if (!string.IsNullOrWhiteSpace(userCookie))
        {
            return (true, false);
        }

        if (!string.IsNullOrWhiteSpace(_spotifyApiSettings.SessionCookie))
        {
            return (true, true);
        }

        return (false, false);
    }

    public async Task<DateTime?> GetCookieSetDateAsync(string userId)
    {
        var setDateMap = await ReadUserCookieSetDateMapAsync();
        if (!setDateMap.TryGetValue(userId.Trim(), out var isoDate) ||
            string.IsNullOrWhiteSpace(isoDate))
        {
            return null;
        }

        return DateTime.TryParse(
            isoDate,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsedDate)
            ? parsedDate
            : null;
    }

    public async Task<IActionResult> SetUserSessionCookieAsync(string userId, string sessionCookie)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return new BadRequestObjectResult(new { error = "User ID is required" });
        }

        if (!AdminHelperService.IsValidPassword(sessionCookie))
        {
            return new BadRequestObjectResult(new { error = "Invalid session cookie format" });
        }

        var normalizedUserId = userId.Trim();

        await _lock.WaitAsync();
        try
        {
            var userCookieMap = await ReadUserCookieMapAsync();
            userCookieMap[normalizedUserId] = sessionCookie;

            var setDateMap = await ReadUserCookieSetDateMapAsync();
            setDateMap[normalizedUserId] = DateTime.UtcNow.ToString("o");

            var updates = new Dictionary<string, string>
            {
                [UserCookieMapKey] = JsonSerializer.Serialize(userCookieMap),
                [UserCookieSetDatesKey] = JsonSerializer.Serialize(setDateMap)
            };

            return await _adminHelper.UpdateEnvConfigAsync(updates);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadUserCookieMapAsync()
    {
        return await ReadEnvJsonMapAsync(UserCookieMapKey);
    }

    private async Task<Dictionary<string, string>> ReadUserCookieSetDateMapAsync()
    {
        return await ReadEnvJsonMapAsync(UserCookieSetDatesKey);
    }

    private async Task<Dictionary<string, string>> ReadEnvJsonMapAsync(string envKey)
    {
        try
        {
            var envPath = _adminHelper.GetEnvFilePath();
            if (!File.Exists(envPath))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var lines = await File.ReadAllLinesAsync(envPath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                {
                    continue;
                }

                var eqIndex = line.IndexOf('=');
                if (eqIndex <= 0)
                {
                    continue;
                }

                var key = line[..eqIndex].Trim();
                if (!key.Equals(envKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = AdminHelperService.StripQuotes(line[(eqIndex + 1)..].Trim());
                if (string.IsNullOrWhiteSpace(value))
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(value);
                return parsed != null
                    ? new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read Spotify user cookie map key {Key}", envKey);
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
