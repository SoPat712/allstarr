using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using allstarr.Services.Common;

namespace allstarr.Services.Admin;

public class AdminHelperService
{
    private readonly ILogger<AdminHelperService> _logger;
    private readonly JellyfinSettings _jellyfinSettings;
    private readonly string _envFilePath;

    public AdminHelperService(
        ILogger<AdminHelperService> logger,
        IOptions<JellyfinSettings> jellyfinSettings,
        IWebHostEnvironment environment)
    {
        _logger = logger;
        _jellyfinSettings = jellyfinSettings.Value;
        _envFilePath = RuntimeEnvConfiguration.ResolveEnvFilePath(environment);
    }

    public string GetJellyfinAuthHeader()
    {
        return $"MediaBrowser Client=\"Allstarr\", Device=\"Server\", DeviceId=\"allstarr-admin\", Version=\"{AppVersion.Version}\", Token=\"{_jellyfinSettings.ApiKey}\"";
    }

    public async Task<List<SpotifyPlaylistConfig>> ReadPlaylistsFromEnvFileAsync()
    {
        var playlists = new List<SpotifyPlaylistConfig>();

        if (!File.Exists(_envFilePath))
        {
            return playlists;
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(_envFilePath);
            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("SPOTIFY_IMPORT_PLAYLISTS="))
                {
                    var value = line.Substring(line.IndexOf('=') + 1).Trim();

                    if (string.IsNullOrWhiteSpace(value) || value == "[]")
                    {
                        return playlists;
                    }

                    var playlistArrays = JsonSerializer.Deserialize<string[][]>(value);
                    if (playlistArrays != null)
                    {
                        foreach (var arr in playlistArrays)
                        {
                            var parsed = ParsePlaylistConfigEntry(arr);
                            if (parsed != null)
                            {
                                playlists.Add(parsed);
                            }
                        }
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read playlists from .env file");
        }

        return playlists;
    }

    public static string SerializePlaylistsForEnv(IEnumerable<SpotifyPlaylistConfig> playlists)
    {
        var playlistArrays = playlists
            .Select(ToEnvPlaylistArray)
            .ToArray();

        return JsonSerializer.Serialize(playlistArrays);
    }

    private static string[] ToEnvPlaylistArray(SpotifyPlaylistConfig playlist)
    {
        var values = new List<string>
        {
            playlist.Name ?? string.Empty,
            playlist.Id ?? string.Empty,
            playlist.JellyfinId ?? string.Empty,
            playlist.LocalTracksPosition.ToString().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(playlist.SyncSchedule) ? "0 8 * * *" : playlist.SyncSchedule.Trim()
        };

        if (!string.IsNullOrWhiteSpace(playlist.UserId))
        {
            values.Add(playlist.UserId.Trim());
        }

        return values.ToArray();
    }

    private static SpotifyPlaylistConfig? ParsePlaylistConfigEntry(string[] arr)
    {
        if (arr.Length < 2)
        {
            return null;
        }

        var config = new SpotifyPlaylistConfig
        {
            Name = arr[0].Trim(),
            Id = arr[1].Trim(),
            JellyfinId = string.Empty,
            LocalTracksPosition = LocalTracksPosition.First,
            SyncSchedule = "0 8 * * *"
        };

        // Legacy format: ["Name","SpotifyId","first|last"]
        if (arr.Length >= 3)
        {
            var third = arr[2].Trim();
            if (IsLocalTracksPositionToken(third))
            {
                config.LocalTracksPosition = ParseLocalTracksPosition(third);
                if (arr.Length >= 4 && !string.IsNullOrWhiteSpace(arr[3]))
                {
                    config.SyncSchedule = arr[3].Trim();
                }
                if (arr.Length >= 5 && !string.IsNullOrWhiteSpace(arr[4]))
                {
                    config.UserId = arr[4].Trim();
                }
                return config;
            }

            config.JellyfinId = third;
        }

        if (arr.Length >= 4)
        {
            config.LocalTracksPosition = ParseLocalTracksPosition(arr[3]);
        }

        if (arr.Length >= 5 && !string.IsNullOrWhiteSpace(arr[4]))
        {
            config.SyncSchedule = arr[4].Trim();
        }

        if (arr.Length >= 6 && !string.IsNullOrWhiteSpace(arr[5]))
        {
            config.UserId = arr[5].Trim();
        }

        return config;
    }

    private static bool IsLocalTracksPositionToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().Equals("first", StringComparison.OrdinalIgnoreCase) ||
               value.Trim().Equals("last", StringComparison.OrdinalIgnoreCase);
    }

    private static LocalTracksPosition ParseLocalTracksPosition(string? value)
    {
        return string.Equals(value?.Trim(), "last", StringComparison.OrdinalIgnoreCase)
            ? LocalTracksPosition.Last
            : LocalTracksPosition.First;
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

    public static bool IsValidEnvKey(string key)
    {
        return Regex.IsMatch(key, @"^[A-Z_][A-Z0-9_]*$", RegexOptions.IgnoreCase);
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

    /// <summary>
    /// Quotes a value if it's not already quoted (for .env file values).
    /// </summary>
    public static string QuoteIfNeeded(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value ?? string.Empty;

        if (value.StartsWith("\"") && value.EndsWith("\""))
            return value;

        return $"\"{value}\"";
    }

    /// <summary>
    /// Strips surrounding quotes from a value (for reading .env file values).
    /// </summary>
    public static string StripQuotes(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        if (value.StartsWith("\"") && value.EndsWith("\"") && value.Length >= 2)
            return value[1..^1];

        return value;
    }

    /// <summary>
    /// Parses a line from .env file and returns key-value pair.
    /// </summary>
    public static (string key, string value) ParseEnvLine(string line)
    {
        var eqIndex = line.IndexOf('=');
        if (eqIndex <= 0)
            return (string.Empty, string.Empty);

        var key = line[..eqIndex].Trim();
        var value = line[(eqIndex + 1)..].Trim();

        // Strip quotes from value
        value = StripQuotes(value);

        return (key, value);
    }

    /// <summary>
    /// Checks if an .env line should be skipped (comment or empty).
    /// </summary>
    public static bool ShouldSkipEnvLine(string line)
    {
        return string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');
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

    public string GetEnvFilePath() => _envFilePath;

    public async Task<IActionResult> UpdateEnvConfigAsync(Dictionary<string, string> updates)
    {
        if (updates == null || updates.Count == 0)
        {
            return new BadRequestObjectResult(new { error = "No updates provided" });
        }

        _logger.LogInformation("Config update requested: {Count} changes", updates.Count);

        try
        {
            if (!File.Exists(_envFilePath))
            {
                _logger.LogWarning(".env file not found at {Path}, creating new file", _envFilePath);
            }

            var envContent = new Dictionary<string, string>();

            if (File.Exists(_envFilePath))
            {
                var lines = await File.ReadAllLinesAsync(_envFilePath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                        continue;

                    var eqIndex = line.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        var key = line[..eqIndex].Trim();
                        var value = line[(eqIndex + 1)..].Trim();
                        envContent[key] = value;
                    }
                }
            }

            var appliedUpdates = new List<string>();
            foreach (var (key, value) in updates)
            {
                if (!IsValidEnvKey(key))
                {
                    _logger.LogWarning("Invalid env key rejected: {Key}", key);
                    return new BadRequestObjectResult(new { error = $"Invalid environment variable key: {key}" });
                }

                envContent[key] = value;
                appliedUpdates.Add(key);

                if (key == "SPOTIFY_API_SESSION_COOKIE" && !string.IsNullOrEmpty(value))
                {
                    var dateKey = "SPOTIFY_API_SESSION_COOKIE_SET_DATE";
                    var dateValue = DateTime.UtcNow.ToString("o");
                    envContent[dateKey] = dateValue;
                    appliedUpdates.Add(dateKey);
                }
            }

            var newContent = string.Join("\n", envContent.Select(kv => $"{kv.Key}={kv.Value}"));
            await File.WriteAllTextAsync(_envFilePath, newContent + "\n");

            _logger.LogInformation("Config file updated successfully at {Path}", _envFilePath);

            return new OkObjectResult(new
            {
                message = "Configuration updated. Restart container to apply changes.",
                updatedKeys = appliedUpdates,
                requiresRestart = true,
                envFilePath = _envFilePath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update configuration at {Path}", _envFilePath);
            return new ObjectResult(new { error = "Failed to update configuration" })
            {
                StatusCode = 500
            };
        }
    }

    public async Task<IActionResult> RemovePlaylistFromConfigAsync(string playlistName)
    {
        try
        {
            var currentPlaylists = await ReadPlaylistsFromEnvFileAsync();
            var playlist = currentPlaylists.FirstOrDefault(p => p.Name.Equals(playlistName, StringComparison.OrdinalIgnoreCase));

            if (playlist == null)
            {
                return new NotFoundObjectResult(new { error = $"Playlist '{playlistName}' not found" });
            }

            currentPlaylists.Remove(playlist);

            var playlistsJson = SerializePlaylistsForEnv(currentPlaylists);

            var updates = new Dictionary<string, string>
            {
                ["SPOTIFY_IMPORT_PLAYLISTS"] = playlistsJson
            };

            return await UpdateEnvConfigAsync(updates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove playlist {Name}", playlistName);
            return new ObjectResult(new { error = "Failed to remove playlist" })
            {
                StatusCode = 500
            };
        }
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

    /// <summary>
    /// Read and deserialize a JSON file
    /// </summary>
    public async Task<T?> ReadJsonFileAsync<T>(string filePath) where T : class
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read JSON file: {Path}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Write object to JSON file
    /// </summary>
    public async Task<bool> WriteJsonFileAsync<T>(string filePath, T data, bool createDirectory = true)
    {
        try
        {
            if (createDirectory)
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write JSON file: {Path}", filePath);
            return false;
        }
    }
}
