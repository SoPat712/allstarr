using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Models.Spotify;

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
        _envFilePath = environment.IsDevelopment() 
            ? Path.Combine(environment.ContentRootPath, "..", ".env")
            : "/app/.env";
    }

    public string GetJellyfinAuthHeader()
    {
        return $"MediaBrowser Client=\"Allstarr\", Device=\"Server\", DeviceId=\"allstarr-admin\", Version=\"1.0.3\", Token=\"{_jellyfinSettings.ApiKey}\"";
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
                            if (arr.Length >= 2)
                            {
                                playlists.Add(new SpotifyPlaylistConfig
                                {
                                    Name = arr[0].Trim(),
                                    Id = arr[1].Trim(),
                                    JellyfinId = arr.Length >= 3 ? arr[2].Trim() : "",
                                    LocalTracksPosition = arr.Length >= 4 && 
                                        arr[3].Trim().Equals("last", StringComparison.OrdinalIgnoreCase)
                                        ? LocalTracksPosition.Last
                                        : LocalTracksPosition.First,
                                    SyncSchedule = arr.Length >= 5 ? arr[4].Trim() : "0 8 * * *"
                                });
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

    public void InvalidatePlaylistSummaryCache()
    {
        try
        {
            var cacheFile = "/app/cache/admin_playlists_summary.json";
            if (File.Exists(cacheFile))
            {
                File.Delete(cacheFile);
                _logger.LogDebug("🗑️ Invalidated playlist summary cache");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate playlist summary cache");
        }
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
            return new ObjectResult(new { error = "Failed to update configuration", details = ex.Message })
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
            
            var playlistsJson = JsonSerializer.Serialize(
                currentPlaylists.Select(p => new[] { 
                    p.Name, 
                    p.Id, 
                    p.JellyfinId, 
                    p.LocalTracksPosition.ToString().ToLower(),
                    p.SyncSchedule ?? "0 8 * * *"
                }).ToArray()
            );
            
            var updates = new Dictionary<string, string>
            {
                ["SPOTIFY_IMPORT_PLAYLISTS"] = playlistsJson
            };
            
            return await UpdateEnvConfigAsync(updates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove playlist {Name}", playlistName);
            return new ObjectResult(new { error = "Failed to remove playlist", details = ex.Message })
            {
                StatusCode = 500
            };
        }
    }

    public async Task SaveManualMappingToFileAsync(
        string playlistName, 
        string spotifyId, 
        string? jellyfinId, 
        string? externalProvider, 
        string? externalId)
    {
        try
        {
            var mappingsDir = "/app/cache/mappings";
            Directory.CreateDirectory(mappingsDir);
            
            var safeName = SanitizeFileName(playlistName);
            var filePath = Path.Combine(mappingsDir, $"{safeName}_mappings.json");
            
            // Load existing mappings
            var mappings = new Dictionary<string, Models.Admin.ManualMappingEntry>();
            if (File.Exists(filePath))
            {
                var json = await File.ReadAllTextAsync(filePath);
                mappings = JsonSerializer.Deserialize<Dictionary<string, Models.Admin.ManualMappingEntry>>(json) 
                    ?? new Dictionary<string, Models.Admin.ManualMappingEntry>();
            }
            
            // Add or update mapping
            mappings[spotifyId] = new Models.Admin.ManualMappingEntry
            {
                SpotifyId = spotifyId,
                JellyfinId = jellyfinId,
                ExternalProvider = externalProvider,
                ExternalId = externalId,
                CreatedAt = DateTime.UtcNow
            };
            
            // Save back to file
            var updatedJson = JsonSerializer.Serialize(mappings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, updatedJson);
            
            _logger.LogDebug("💾 Saved manual mapping to file: {Playlist} - {SpotifyId}", playlistName, spotifyId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save manual mapping to file for {Playlist}", playlistName);
        }
    }

    public async Task SaveLyricsMappingToFileAsync(
        string artist,
        string title,
        string album,
        int durationSeconds,
        int lyricsId)
    {
        try
        {
            var mappingsDir = "/app/cache/lyrics_mappings";
            Directory.CreateDirectory(mappingsDir);
            
            var safeName = SanitizeFileName($"{artist}_{title}");
            var filePath = Path.Combine(mappingsDir, $"{safeName}.json");
            
            var mapping = new
            {
                artist,
                title,
                album,
                durationSeconds,
                lyricsId,
                createdAt = DateTime.UtcNow
            };
            
            var json = JsonSerializer.Serialize(mapping, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
            
            _logger.LogDebug("💾 Saved lyrics mapping to file: {Artist} - {Title} → {LyricsId}", artist, title, lyricsId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save lyrics mapping to file for {Artist} - {Title}", artist, title);
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
