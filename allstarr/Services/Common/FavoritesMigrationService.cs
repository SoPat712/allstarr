using System.Text.Json;
using System.Globalization;
using allstarr.Models.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace allstarr.Services.Common;

/// <summary>
/// Handles one-time migration of favorites and pending deletions from old JSON files to Redis.
/// </summary>
public class FavoritesMigrationService
{
    private readonly RedisCacheService _cache;
    private readonly ILogger<FavoritesMigrationService> _logger;
    private readonly string _cacheDir;

    public FavoritesMigrationService(
        RedisCacheService cache,
        IConfiguration configuration,
        ILogger<FavoritesMigrationService> logger)
    {
        _cache = cache;
        _logger = logger;
        _cacheDir = "/app/cache"; // This matches the path in JellyfinController
    }

    public async Task MigrateAsync()
    {
        if (!_cache.IsEnabled) return;

        await MigrateFavoritesAsync();
        await MigratePendingDeletionsAsync();
    }

    private async Task MigrateFavoritesAsync()
    {
        var filePath = Path.Combine(_cacheDir, "favorites.json");
        var migrationMark = Path.Combine(_cacheDir, "favorites.json.migrated");

        if (!File.Exists(filePath) || File.Exists(migrationMark)) return;

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            _logger.LogInformation("🚀 Starting one-time migration of favorites from {Path} to Redis...", filePath);
            
            var json = await File.ReadAllTextAsync(filePath);
            var favorites = JsonSerializer.Deserialize<Dictionary<string, FavoriteTrackInfo>>(json, options);

            if (favorites == null || favorites.Count == 0)
            {
                File.Move(filePath, migrationMark);
                return;
            }

            int count = 0;
            foreach (var fav in favorites.Values)
            {
                await _cache.SetAsync($"favorites:{fav.ItemId}", fav);
                count++;
            }

            File.Move(filePath, migrationMark);
            _logger.LogInformation("✅ Successfully migrated {Count} favorites to Redis cached storage.", count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to migrate favorites from JSON to Redis");
        }
    }

    private async Task MigratePendingDeletionsAsync()
    {
        var filePath = Path.Combine(_cacheDir, "pending_deletions.json");
        var migrationMark = Path.Combine(_cacheDir, "pending_deletions.json.migrated");

        if (!File.Exists(filePath) || File.Exists(migrationMark)) return;

        try
        {
            _logger.LogInformation("🚀 Starting one-time migration of pending deletions from {Path} to Redis...", filePath);
            
            var json = await File.ReadAllTextAsync(filePath);
            var deletions = ParsePendingDeletions(json, DateTime.UtcNow);

            if (deletions == null || deletions.Count == 0)
            {
                File.Move(filePath, migrationMark);
                return;
            }

            int count = 0;
            foreach (var (itemId, deleteAt) in deletions)
            {
                await _cache.SetStringAsync($"pending_deletion:{itemId}", deleteAt.ToUniversalTime().ToString("O"));
                count++;
            }

            File.Move(filePath, migrationMark);
            _logger.LogInformation("✅ Successfully migrated {Count} pending deletions to Redis cached storage.", count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to migrate pending deletions from JSON to Redis");
        }
    }

    private static Dictionary<string, DateTime> ParsePendingDeletions(string json, DateTime fallbackDeleteAtUtc)
    {
        var legacySchedule = TryDeserialize<Dictionary<string, DateTime>>(json);
        if (legacySchedule != null)
        {
            return legacySchedule.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Kind == DateTimeKind.Utc ? kvp.Value : kvp.Value.ToUniversalTime());
        }

        var legacyScheduleStrings = TryDeserialize<Dictionary<string, string>>(json);
        if (legacyScheduleStrings != null)
        {
            var parsed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            foreach (var (itemId, deleteAtRaw) in legacyScheduleStrings)
            {
                if (DateTime.TryParse(
                        deleteAtRaw,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                        out var deleteAt))
                {
                    parsed[itemId] = deleteAt.Kind == DateTimeKind.Utc ? deleteAt : deleteAt.ToUniversalTime();
                }
            }

            return parsed;
        }

        var deletionSet = TryDeserialize<HashSet<string>>(json) ?? TryDeserialize<List<string>>(json)?.ToHashSet();
        if (deletionSet != null)
        {
            return deletionSet.ToDictionary(itemId => itemId, _ => fallbackDeleteAtUtc, StringComparer.OrdinalIgnoreCase);
        }

        throw new JsonException("Unsupported pending_deletions.json format");
    }

    private static T? TryDeserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private class FavoriteTrackInfo
    {
        public string ItemId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public DateTime FavoritedAt { get; set; }
    }
}
