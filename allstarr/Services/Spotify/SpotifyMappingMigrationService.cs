using System.Text.Json;
using allstarr.Models.Spotify;
using allstarr.Services.Common;

namespace allstarr.Services.Spotify;

/// <summary>
/// Migrates legacy per-playlist manual mappings to global mappings.
/// Runs once on startup to convert old format to new format.
/// </summary>
public class SpotifyMappingMigrationService : IHostedService
{
    private readonly SpotifyMappingService _mappingService;
    private readonly RedisCacheService _cache;
    private readonly ILogger<SpotifyMappingMigrationService> _logger;
    private const string MappingsCacheDirectory = "/app/cache/mappings";
    private const string MigrationFlagKey = "spotify:mappings:migrated";

    public SpotifyMappingMigrationService(
        SpotifyMappingService mappingService,
        RedisCacheService cache,
        ILogger<SpotifyMappingMigrationService> logger)
    {
        _mappingService = mappingService;
        _cache = cache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Check if migration already completed
        var migrated = await _cache.GetStringAsync(MigrationFlagKey);
        if (migrated == "true")
        {
            _logger.LogDebug("Mapping migration already completed, skipping");
            return;
        }

        _logger.LogInformation("🔄 Starting migration of legacy per-playlist mappings to global mappings...");

        try
        {
            var migratedCount = await MigrateLegacyMappingsAsync(cancellationToken);

            if (migratedCount > 0)
            {
                _logger.LogInformation("✅ Migrated {Count} legacy mappings to global format", migratedCount);
            }
            else
            {
                _logger.LogInformation("✅ No legacy mappings found to migrate");
            }

            // Set migration flag (permanent)
            await _cache.SetStringAsync(MigrationFlagKey, "true", expiry: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to migrate legacy mappings");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task<int> MigrateLegacyMappingsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(MappingsCacheDirectory))
        {
            return 0;
        }

        var files = Directory.GetFiles(MappingsCacheDirectory, "*_mappings.json");
        var migratedCount = 0;

        foreach (var file in files)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var legacyMappings = JsonSerializer.Deserialize<Dictionary<string, LegacyMappingEntry>>(json);

                if (legacyMappings == null || legacyMappings.Count == 0)
                    continue;

                var playlistName = Path.GetFileNameWithoutExtension(file).Replace("_mappings", "");
                _logger.LogInformation("Migrating {Count} mappings from playlist: {Playlist}", 
                    legacyMappings.Count, playlistName);

                foreach (var (spotifyId, legacyMapping) in legacyMappings)
                {
                    // Check if global mapping already exists
                    var existingMapping = await _mappingService.GetMappingAsync(spotifyId);
                    if (existingMapping != null)
                    {
                        _logger.LogDebug("Skipping {SpotifyId} - global mapping already exists", spotifyId);
                        continue;
                    }

                    // Convert legacy mapping to global mapping
                    var metadata = new TrackMetadata
                    {
                        Title = legacyMapping.Title,
                        Artist = legacyMapping.Artist,
                        Album = legacyMapping.Album
                    };

                    bool success;
                    if (!string.IsNullOrEmpty(legacyMapping.JellyfinId))
                    {
                        // Local mapping
                        success = await _mappingService.SaveManualMappingAsync(
                            spotifyId,
                            "local",
                            localId: legacyMapping.JellyfinId,
                            metadata: metadata);
                    }
                    else if (!string.IsNullOrEmpty(legacyMapping.ExternalProvider) && 
                             !string.IsNullOrEmpty(legacyMapping.ExternalId))
                    {
                        // External mapping
                        success = await _mappingService.SaveManualMappingAsync(
                            spotifyId,
                            "external",
                            externalProvider: legacyMapping.ExternalProvider,
                            externalId: legacyMapping.ExternalId,
                            metadata: metadata);
                    }
                    else
                    {
                        _logger.LogWarning("Invalid legacy mapping for {SpotifyId}, skipping", spotifyId);
                        continue;
                    }

                    if (success)
                    {
                        migratedCount++;
                        _logger.LogDebug("Migrated {SpotifyId} → {TargetType}", 
                            spotifyId, 
                            !string.IsNullOrEmpty(legacyMapping.JellyfinId) ? "local" : "external");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to migrate mappings from file: {File}", file);
            }
        }

        return migratedCount;
    }

    private class LegacyMappingEntry
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? JellyfinId { get; set; }
        public string? ExternalProvider { get; set; }
        public string? ExternalId { get; set; }
    }
}
