using System.Text.Json;
using allstarr.Models.Spotify;
using allstarr.Services.Common;

namespace allstarr.Services.Spotify;

/// <summary>
/// Manages global Spotify ID → Local/External track mappings.
/// Provides fast lookups and persistence via Redis.
/// </summary>
public class SpotifyMappingService
{
    private readonly RedisCacheService _cache;
    private readonly ILogger<SpotifyMappingService> _logger;

    public SpotifyMappingService(
        RedisCacheService cache,
        ILogger<SpotifyMappingService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Gets a mapping for a Spotify track ID.
    /// </summary>
    public async Task<SpotifyTrackMapping?> GetMappingAsync(string spotifyId)
    {
        var key = CacheKeyBuilder.BuildSpotifyGlobalMappingKey(spotifyId);
        var mapping = await _cache.GetAsync<SpotifyTrackMapping>(key);

        if (mapping != null)
        {
            EnsureExternalMappingsConsistency(mapping);
            _logger.LogDebug("Found mapping for Spotify ID {SpotifyId}: {TargetType}", spotifyId, mapping.TargetType);
        }

        return mapping;
    }

    /// <summary>
    /// Saves a mapping for a Spotify track ID.
    /// Local mappings are always preferred over external.
    /// Manual mappings are preserved unless explicitly overwritten.
    /// </summary>
    public async Task<bool> SaveMappingAsync(SpotifyTrackMapping mapping)
    {
        EnsureExternalMappingsConsistency(mapping);

        // Validate mapping
        if (string.IsNullOrEmpty(mapping.SpotifyId))
        {
            _logger.LogWarning("Cannot save mapping: SpotifyId is required");
            return false;
        }

        if (mapping.TargetType == "local" && string.IsNullOrEmpty(mapping.LocalId))
        {
            _logger.LogWarning("Cannot save local mapping: LocalId is required");
            return false;
        }

        if (mapping.TargetType == "external" &&
            !mapping.TryGetExternalTarget(preferredProvider: null, out _, out _))
        {
            _logger.LogWarning("Cannot save external mapping: at least one external provider/id is required");
            return false;
        }

        var key = CacheKeyBuilder.BuildSpotifyGlobalMappingKey(mapping.SpotifyId);

        // Check if mapping already exists
        var existingMapping = await GetMappingAsync(mapping.SpotifyId);

        // For external mappings, merge provider-specific mappings so rematch/rebuild
        // can retain multiple sources (e.g. SquidWTF + Deezer) for the same Spotify ID.
        if (mapping.TargetType == "external")
        {
            if (existingMapping != null && existingMapping.TargetType == "local")
            {
                var localMappingWithExternalAlternatives = existingMapping;
                MergeExternalMappings(localMappingWithExternalAlternatives, mapping);
                localMappingWithExternalAlternatives.UpdatedAt = DateTime.UtcNow;

                var saveLocalWithAlternatives = await _cache.SetAsync(key, localMappingWithExternalAlternatives, expiry: null);
                if (saveLocalWithAlternatives)
                {
                    await AddToAllMappingsSetAsync(localMappingWithExternalAlternatives.SpotifyId);
                    await InvalidateAllPlaylistStatsCachesAsync();
                    _logger.LogInformation(
                        "Saved external fallback for local mapping: Spotify {SpotifyId} -> {Provider}:{ExternalId}",
                        mapping.SpotifyId,
                        mapping.ExternalProvider,
                        mapping.ExternalId);
                }

                return saveLocalWithAlternatives;
            }

            if (existingMapping != null && existingMapping.TargetType == "external")
            {
                MergeExternalMappings(mapping, existingMapping);
            }
        }

        // RULE 1: Never overwrite manual mappings with auto mappings
        if (existingMapping != null &&
            existingMapping.Source == "manual" &&
            mapping.Source == "auto")
        {
            _logger.LogDebug("Skipping auto mapping for {SpotifyId} - manual mapping exists", mapping.SpotifyId);
            return false;
        }

        // RULE 2: Local always wins over external (even if existing is manual external)
        if (existingMapping != null &&
            existingMapping.TargetType == "external" &&
            mapping.TargetType == "local")
        {
            _logger.LogInformation("🎉 UPGRADING: External → Local for {SpotifyId}", mapping.SpotifyId);
            MergeExternalMappings(mapping, existingMapping);
            // Allow the upgrade to proceed
        }

        // RULE 3: Don't downgrade local to external
        if (existingMapping != null &&
            existingMapping.TargetType == "local" &&
            mapping.TargetType == "external")
        {
            _logger.LogDebug("Skipping external mapping for {SpotifyId} - local mapping exists", mapping.SpotifyId);
            return false;
        }

        // Set timestamps
        if (mapping.CreatedAt == default)
        {
            mapping.CreatedAt = DateTime.UtcNow;
        }

        // Preserve CreatedAt from existing mapping
        if (existingMapping != null)
        {
            mapping.CreatedAt = existingMapping.CreatedAt;
            if (mapping.TargetType == "local" || mapping.TargetType == "external")
            {
                MergeExternalMappings(mapping, existingMapping);
            }
        }

        EnsureExternalMappingsConsistency(mapping);

        // Save mapping (permanent - no TTL)
        var success = await _cache.SetAsync(key, mapping, expiry: null);

        if (success)
        {
            // Add to set of all mapping IDs for enumeration
            await AddToAllMappingsSetAsync(mapping.SpotifyId);

            // Invalidate ALL playlist stats caches since this mapping could affect any playlist
            // This ensures the stats are recalculated on next request
            await InvalidateAllPlaylistStatsCachesAsync();

            _logger.LogInformation(
                "Saved {Source} mapping: Spotify {SpotifyId} → {TargetType} {TargetId}",
                mapping.Source,
                mapping.SpotifyId,
                mapping.TargetType,
                mapping.TargetType == "local" ? mapping.LocalId : $"{mapping.ExternalProvider}:{mapping.ExternalId}"
            );
        }

        return success;
    }

    /// <summary>
    /// Saves a local mapping (auto-populated during matching).
    /// </summary>
    public async Task<bool> SaveLocalMappingAsync(
        string spotifyId,
        string localId,
        TrackMetadata? metadata = null)
    {
        var mapping = new SpotifyTrackMapping
        {
            SpotifyId = spotifyId,
            TargetType = "local",
            LocalId = localId,
            Metadata = metadata,
            Source = "auto",
            CreatedAt = DateTime.UtcNow
        };

        return await SaveMappingAsync(mapping);
    }

    /// <summary>
    /// Saves an external mapping (auto-populated during matching).
    /// </summary>
    public async Task<bool> SaveExternalMappingAsync(
        string spotifyId,
        string externalProvider,
        string externalId,
        TrackMetadata? metadata = null)
    {
        var mapping = new SpotifyTrackMapping
        {
            SpotifyId = spotifyId,
            TargetType = "external",
            ExternalProvider = externalProvider,
            ExternalId = externalId,
            ExternalMappings = new List<ExternalTrackMapping>
            {
                new()
                {
                    Provider = externalProvider,
                    ExternalId = externalId,
                    Source = "auto",
                    CreatedAt = DateTime.UtcNow
                }
            },
            Metadata = metadata,
            Source = "auto",
            CreatedAt = DateTime.UtcNow
        };

        return await SaveMappingAsync(mapping);
    }

    /// <summary>
    /// Saves a manual mapping (user override via Admin UI).
    /// </summary>
    public async Task<bool> SaveManualMappingAsync(
        string spotifyId,
        string targetType,
        string? localId = null,
        string? externalProvider = null,
        string? externalId = null,
        TrackMetadata? metadata = null)
    {
        var mapping = new SpotifyTrackMapping
        {
            SpotifyId = spotifyId,
            TargetType = targetType,
            LocalId = localId,
            ExternalProvider = externalProvider,
            ExternalId = externalId,
            ExternalMappings = targetType == "external" &&
                               !string.IsNullOrWhiteSpace(externalProvider) &&
                               !string.IsNullOrWhiteSpace(externalId)
                ? new List<ExternalTrackMapping>
                {
                    new()
                    {
                        Provider = externalProvider,
                        ExternalId = externalId,
                        Source = "manual",
                        CreatedAt = DateTime.UtcNow
                    }
                }
                : new List<ExternalTrackMapping>(),
            Metadata = metadata,
            Source = "manual",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        return await SaveMappingAsync(mapping);
    }

    /// <summary>
    /// Deletes a mapping for a Spotify track ID.
    /// </summary>
    public async Task<bool> DeleteMappingAsync(string spotifyId)
    {
        var key = CacheKeyBuilder.BuildSpotifyGlobalMappingKey(spotifyId);
        var success = await _cache.DeleteAsync(key);

        if (success)
        {
            await RemoveFromAllMappingsSetAsync(spotifyId);
            _logger.LogInformation("Deleted mapping for Spotify ID {SpotifyId}", spotifyId);
        }

        return success;
    }

    /// <summary>
    /// Removes a single external provider mapping for a Spotify track ID.
    /// Deletes the entire mapping when no external targets remain on an external-only mapping.
    /// </summary>
    public async Task<bool> RemoveExternalProviderAsync(string spotifyId, string provider)
    {
        if (string.IsNullOrWhiteSpace(spotifyId) || string.IsNullOrWhiteSpace(provider))
        {
            return false;
        }

        var mapping = await GetMappingAsync(spotifyId);
        if (mapping == null)
        {
            return false;
        }

        var normalizedProvider = provider.Trim().ToLowerInvariant();
        var removedCount = mapping.ExternalMappings.RemoveAll(m =>
            string.Equals(m.Provider, normalizedProvider, StringComparison.OrdinalIgnoreCase));

        var removedLegacy =
            string.Equals(mapping.ExternalProvider, normalizedProvider, StringComparison.OrdinalIgnoreCase);
        if (removedLegacy)
        {
            mapping.ExternalProvider = null;
            mapping.ExternalId = null;
            removedCount++;
        }

        if (removedCount == 0)
        {
            return false;
        }

        EnsureExternalMappingsConsistency(mapping);

        if (mapping.TargetType == "external" &&
            !mapping.TryGetExternalTarget(preferredProvider: null, out _, out _))
        {
            return await DeleteMappingAsync(spotifyId);
        }

        mapping.UpdatedAt = DateTime.UtcNow;
        var key = CacheKeyBuilder.BuildSpotifyGlobalMappingKey(spotifyId);
        var success = await _cache.SetAsync(key, mapping, expiry: null);

        if (success)
        {
            await InvalidateAllPlaylistStatsCachesAsync();
            _logger.LogInformation(
                "Removed external provider {Provider} from Spotify mapping {SpotifyId}",
                normalizedProvider,
                spotifyId);
        }

        return success;
    }

    /// <summary>
    /// Gets all Spotify IDs that have mappings.
    /// </summary>
    public async Task<List<string>> GetAllMappingIdsAsync()
    {
        var json = await _cache.GetStringAsync(CacheKeyBuilder.BuildSpotifyGlobalMappingsIndexKey());
        if (string.IsNullOrEmpty(json))
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize all mapping IDs");
            return new List<string>();
        }
    }

    /// <summary>
    /// Gets all mappings (paginated).
    /// </summary>
    public async Task<List<SpotifyTrackMapping>> GetAllMappingsAsync(int skip = 0, int take = 100)
    {
        var allIds = await GetAllMappingIdsAsync();
        var pagedIds = allIds.Skip(skip).Take(take).ToList();

        var mappings = new List<SpotifyTrackMapping>();

        foreach (var spotifyId in pagedIds)
        {
            var mapping = await GetMappingAsync(spotifyId);
            if (mapping != null)
            {
                mappings.Add(mapping);
            }
        }

        return mappings;
    }

    /// <summary>
    /// Gets count of all mappings.
    /// </summary>
    public async Task<int> GetMappingCountAsync()
    {
        var allIds = await GetAllMappingIdsAsync();
        return allIds.Count;
    }

    /// <summary>
    /// Gets statistics about mappings.
    /// </summary>
    public async Task<MappingStats> GetStatsAsync()
    {
        var allIds = await GetAllMappingIdsAsync();
        var stats = new MappingStats
        {
            TotalMappings = allIds.Count
        };

        // Sample first 1000 to get stats (avoid loading all mappings)
        var sampleIds = allIds.Take(1000).ToList();

        foreach (var spotifyId in sampleIds)
        {
            var mapping = await GetMappingAsync(spotifyId);
            if (mapping != null)
            {
                if (mapping.TargetType == "local")
                {
                    stats.LocalMappings++;
                }
                else if (mapping.TargetType == "external")
                {
                    stats.ExternalMappings++;
                }

                if (mapping.Source == "manual")
                {
                    stats.ManualMappings++;
                }
                else if (mapping.Source == "auto")
                {
                    stats.AutoMappings++;
                }
            }
        }

        // Extrapolate if we sampled
        if (allIds.Count > 1000)
        {
            var ratio = (double)allIds.Count / sampleIds.Count;
            stats.LocalMappings = (int)(stats.LocalMappings * ratio);
            stats.ExternalMappings = (int)(stats.ExternalMappings * ratio);
            stats.ManualMappings = (int)(stats.ManualMappings * ratio);
            stats.AutoMappings = (int)(stats.AutoMappings * ratio);
        }

        return stats;
    }

    private async Task AddToAllMappingsSetAsync(string spotifyId)
    {
        var allIds = await GetAllMappingIdsAsync();

        if (!allIds.Contains(spotifyId))
        {
            allIds.Add(spotifyId);
            var json = JsonSerializer.Serialize(allIds);
            await _cache.SetStringAsync(CacheKeyBuilder.BuildSpotifyGlobalMappingsIndexKey(), json, expiry: null);
        }
    }

    private async Task RemoveFromAllMappingsSetAsync(string spotifyId)
    {
        var allIds = await GetAllMappingIdsAsync();

        if (allIds.Remove(spotifyId))
        {
            var json = JsonSerializer.Serialize(allIds);
            await _cache.SetStringAsync(CacheKeyBuilder.BuildSpotifyGlobalMappingsIndexKey(), json, expiry: null);
        }
    }

    /// <summary>
    /// Invalidates all playlist stats caches.
    /// Called when a mapping is saved/deleted to ensure stats are recalculated.
    /// </summary>
    private async Task InvalidateAllPlaylistStatsCachesAsync()
    {
        try
        {
            // Delete all keys matching the pattern from CacheKeyBuilder (currently not enumerated).
            // Note: This is a simple implementation that deletes known patterns
            // In production, you might want to track playlist names or use Redis SCAN

            // For now, we'll just log that stats should be recalculated
            // The stats will be recalculated on next request since they check global mappings
            _logger.LogDebug("Mapping changed - playlist stats will be recalculated on next request");

            // Optionally: Delete the admin playlist summary cache to force immediate refresh
            var summaryFile = "/app/cache/admin_playlists_summary.json";
            if (File.Exists(summaryFile))
            {
                File.Delete(summaryFile);
                _logger.LogDebug("Deleted admin playlist summary cache");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate playlist stats caches");
        }
    }

    private static void MergeExternalMappings(SpotifyTrackMapping target, SpotifyTrackMapping source)
    {
        if (source.TryGetExternalTarget(preferredProvider: null, out var sourceProvider, out var sourceExternalId))
        {
            UpsertExternalMapping(
                target.ExternalMappings,
                sourceProvider,
                sourceExternalId,
                source.Source);
        }

        foreach (var mapping in source.ExternalMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.Provider) || string.IsNullOrWhiteSpace(mapping.ExternalId))
            {
                continue;
            }

            UpsertExternalMapping(
                target.ExternalMappings,
                mapping.Provider,
                mapping.ExternalId,
                mapping.Source);
        }
    }

    private static void UpsertExternalMapping(
        List<ExternalTrackMapping> externalMappings,
        string provider,
        string externalId,
        string source)
    {
        var normalizedProvider = provider.Trim().ToLowerInvariant();
        var existing = externalMappings.FirstOrDefault(m =>
            string.Equals(m.Provider, normalizedProvider, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            externalMappings.Add(new ExternalTrackMapping
            {
                Provider = normalizedProvider,
                ExternalId = externalId,
                Source = source,
                CreatedAt = DateTime.UtcNow
            });
            return;
        }

        if (!string.Equals(existing.ExternalId, externalId, StringComparison.Ordinal))
        {
            existing.ExternalId = externalId;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            existing.Source = source;
        }
    }

    private static void EnsureExternalMappingsConsistency(SpotifyTrackMapping mapping)
    {
        mapping.ExternalMappings ??= new List<ExternalTrackMapping>();

        if (!string.IsNullOrWhiteSpace(mapping.ExternalProvider) && !string.IsNullOrWhiteSpace(mapping.ExternalId))
        {
            UpsertExternalMapping(
                mapping.ExternalMappings,
                mapping.ExternalProvider,
                mapping.ExternalId,
                mapping.Source);
        }

        if (mapping.ExternalMappings.Count > 0)
        {
            var first = mapping.ExternalMappings[0];
            mapping.ExternalProvider = first.Provider;
            mapping.ExternalId = first.ExternalId;
        }
    }
}

/// <summary>
/// Statistics about Spotify track mappings.
/// </summary>
public class MappingStats
{
    public int TotalMappings { get; set; }
    public int LocalMappings { get; set; }
    public int ExternalMappings { get; set; }
    public int ManualMappings { get; set; }
    public int AutoMappings { get; set; }
}
