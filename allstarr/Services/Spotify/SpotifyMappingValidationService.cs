using allstarr.Models.Domain;
using allstarr.Models.Spotify;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using System.Text.Json;

namespace allstarr.Services.Spotify;

/// <summary>
/// Validates Spotify track mappings to ensure they're still accurate.
/// - Local mappings: Checks if Jellyfin track still exists (every 7 days)
/// - External mappings: Searches for local match to upgrade (every playlist sync)
/// </summary>
public class SpotifyMappingValidationService
{
    private readonly SpotifyMappingService _mappingService;
    private readonly JellyfinProxyService _jellyfinProxy;
    private readonly JellyfinSettings _jellyfinSettings;
    private readonly ILogger<SpotifyMappingValidationService> _logger;

    public SpotifyMappingValidationService(
        SpotifyMappingService mappingService,
        JellyfinProxyService jellyfinProxy,
        IOptions<JellyfinSettings> jellyfinSettings,
        ILogger<SpotifyMappingValidationService> logger)
    {
        _mappingService = mappingService;
        _jellyfinProxy = jellyfinProxy;
        _jellyfinSettings = jellyfinSettings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Validates a single mapping. Returns updated mapping or null if should be deleted.
    /// </summary>
    public async Task<SpotifyTrackMapping?> ValidateMappingAsync(SpotifyTrackMapping mapping, bool isPlaylistSync = false)
    {
        if (!mapping.NeedsValidation(isPlaylistSync))
        {
            _logger.LogDebug("Mapping {SpotifyId} doesn't need validation yet", mapping.SpotifyId);
            return mapping;
        }

        _logger.LogInformation("🔍 Validating mapping: {SpotifyId} → {TargetType} {TargetId}",
            mapping.SpotifyId,
            mapping.TargetType,
            mapping.TargetType == "local" ? mapping.LocalId : $"{mapping.ExternalProvider}:{mapping.ExternalId}");

        if (mapping.TargetType == "local")
        {
            return await ValidateLocalMappingAsync(mapping);
        }
        else if (mapping.TargetType == "external")
        {
            return await ValidateExternalMappingAsync(mapping);
        }

        return mapping;
    }

    /// <summary>
    /// Validates a local mapping - checks if Jellyfin track still exists.
    /// If deleted: clear mapping and search for new local match, fallback to external.
    /// </summary>
    private async Task<SpotifyTrackMapping?> ValidateLocalMappingAsync(SpotifyTrackMapping mapping)
    {
        if (string.IsNullOrEmpty(mapping.LocalId))
        {
            _logger.LogWarning("Local mapping has no LocalId, deleting: {SpotifyId}", mapping.SpotifyId);
            await _mappingService.DeleteMappingAsync(mapping.SpotifyId);
            return null;
        }

        // Check if Jellyfin track still exists
        try
        {
            var (response, _) = await _jellyfinProxy.GetJsonAsyncInternal(
                $"Items/{mapping.LocalId}",
                new Dictionary<string, string>());

            if (response != null && response.RootElement.TryGetProperty("Name", out _))
            {
                // Track still exists, update validation timestamp
                mapping.LastValidatedAt = DateTime.UtcNow;
                await _mappingService.SaveMappingAsync(mapping);

                _logger.LogInformation("✓ Local track still exists: {LocalId}", mapping.LocalId);
                return mapping;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local track not found or error checking: {LocalId}", mapping.LocalId);
        }

        // Track doesn't exist anymore - clear mapping and re-search
        _logger.LogWarning("❌ Local track deleted: {LocalId}, re-searching for {Title} - {Artist}",
            mapping.LocalId,
            mapping.Metadata?.Title ?? "Unknown",
            mapping.Metadata?.Artist ?? "Unknown");

        await _mappingService.DeleteMappingAsync(mapping.SpotifyId);

        // Try to find new local match
        if (mapping.Metadata != null)
        {
            var newLocalMatch = await SearchJellyfinForTrackAsync(
                mapping.Metadata.Title ?? "",
                mapping.Metadata.Artist ?? "");

            if (newLocalMatch != null)
            {
                _logger.LogInformation("✓ Found new local match: {Title} → {NewLocalId}",
                    mapping.Metadata.Title,
                    newLocalMatch.Id);

                // Create new local mapping
                await _mappingService.SaveLocalMappingAsync(
                    mapping.SpotifyId,
                    newLocalMatch.Id,
                    mapping.Metadata);

                mapping.LocalId = newLocalMatch.Id;
                mapping.LastValidatedAt = DateTime.UtcNow;
                return mapping;
            }
            else
            {
                _logger.LogInformation("❌ No local match found, will fallback to external on next match");
            }
        }

        return null; // Mapping deleted, will re-match from scratch
    }

    /// <summary>
    /// Validates an external mapping - searches Jellyfin to see if track is now local.
    /// If found locally: upgrade mapping from external → local.
    /// </summary>
    private async Task<SpotifyTrackMapping?> ValidateExternalMappingAsync(SpotifyTrackMapping mapping)
    {
        if (mapping.Metadata == null)
        {
            _logger.LogWarning("⚠️ External mapping has NO METADATA, cannot search for local match: {SpotifyId}", mapping.SpotifyId);
            mapping.LastValidatedAt = DateTime.UtcNow;
            await _mappingService.SaveMappingAsync(mapping);
            return mapping;
        }

        // Search Jellyfin for local match
        var localMatch = await SearchJellyfinForTrackAsync(
            mapping.Metadata.Title ?? "",
            mapping.Metadata.Artist ?? "");

        if (localMatch != null)
        {
            // Found in local library! Upgrade mapping
            _logger.LogInformation("🎉 UPGRADE: External → Local for {Title} - {Artist}",
                mapping.Metadata.Title,
                mapping.Metadata.Artist);
            _logger.LogInformation("   Old: {Provider}:{ExternalId}",
                mapping.ExternalProvider,
                mapping.ExternalId);
            _logger.LogInformation("   New: Jellyfin:{LocalId}",
                localMatch.Id);

            // Update mapping to local
            mapping.TargetType = "local";
            mapping.LocalId = localMatch.Id;
            mapping.ExternalProvider = null;
            mapping.ExternalId = null;
            mapping.LastValidatedAt = DateTime.UtcNow;
            mapping.UpdatedAt = DateTime.UtcNow;

            await _mappingService.SaveMappingAsync(mapping);
            return mapping;
        }
        else
        {
            // Still not in local library, keep external mapping
            _logger.LogDebug("External track not yet in local library: {Title} - {Artist}",
                mapping.Metadata.Title,
                mapping.Metadata.Artist);

            mapping.LastValidatedAt = DateTime.UtcNow;
            await _mappingService.SaveMappingAsync(mapping);
            return mapping;
        }
    }

    /// <summary>
    /// Searches Jellyfin for a track using fuzzy matching (same algorithm as playlist matching).
    /// Uses greedy algorithm + Levenshtein distance.
    /// </summary>
    private async Task<Song?> SearchJellyfinForTrackAsync(string title, string artist)
    {
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(artist))
        {
            return null;
        }

        try
        {
            // Jellyfin search is much more reliable with a title query than a single
            // concatenated title+artist term. Query both forms, then score the union.
            var resultItems = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var query in new[] { FuzzyMatcher.StripDecorators(title), $"{title} {artist}" }
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var searchParams = new Dictionary<string, string>
                {
                    ["searchTerm"] = query,
                    ["includeItemTypes"] = "Audio",
                    ["recursive"] = "true",
                    ["limit"] = "25",
                    ["fields"] = "Artists,AlbumArtist,Album,RunTimeTicks,ProviderIds"
                };

                if (!string.IsNullOrEmpty(_jellyfinSettings.LibraryId))
                {
                    searchParams["parentId"] = _jellyfinSettings.LibraryId;
                }

                var (response, _) = await _jellyfinProxy.GetJsonAsyncInternal("Items", searchParams);
                if (response == null || !response.RootElement.TryGetProperty("Items", out var items)) continue;
                foreach (var item in items.EnumerateArray())
                {
                    var id = item.TryGetProperty("Id", out var idElement) ? idElement.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(id)) resultItems[id] = item.Clone();
                }
            }

            var candidates = new List<(Song Song, double Score)>();

            foreach (var item in resultItems.Values)
            {
                var itemTitle = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var itemArtists = item.TryGetProperty("Artists", out var artistsElement) && artistsElement.ValueKind == JsonValueKind.Array
                    ? artistsElement.EnumerateArray().Select(value => value.GetString() ?? string.Empty).Where(value => value.Length > 0).ToList()
                    : new List<string>();
                var itemArtist = itemArtists.FirstOrDefault()
                    ?? (item.TryGetProperty("AlbumArtist", out var artistEl) ? artistEl.GetString() ?? "" : "");
                var itemId = item.TryGetProperty("Id", out var idEl) ? idEl.GetString() ?? "" : "";

                if (string.IsNullOrEmpty(itemId)) continue;

                // Calculate similarity using aggressive matching (same as playlist matching)
                var titleScore = FuzzyMatcher.CalculateSimilarityAggressive(title, itemTitle);
                var artistScore = FuzzyMatcher.CalculateArtistMatchScore([artist], itemArtist, itemArtists);

                // Weight: 70% title, 30% artist (same as playlist matching)
                var totalScore = (titleScore * 0.7) + (artistScore * 0.3);

                // Validation must not silently replace a route with a weak candidate.
                if (totalScore >= 70 && titleScore >= 60 && artistScore >= 45)
                {
                    var song = new Song
                    {
                        Id = itemId,
                        Title = itemTitle,
                        Artist = itemArtist,
                        Album = item.TryGetProperty("Album", out var albumEl) ? albumEl.GetString() ?? "" : "",
                        IsLocal = true
                    };

                    candidates.Add((song, totalScore));
                }
            }

            // Return best match (highest score)
            var bestMatch = candidates.OrderByDescending(c => c.Score).FirstOrDefault();

            if (bestMatch.Song != null)
            {
                _logger.LogDebug("Found local match: {Title} - {Artist} (score: {Score:F1})",
                    bestMatch.Song.Title,
                    bestMatch.Song.Artist,
                    bestMatch.Score);
                return bestMatch.Song;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching Jellyfin for track: {Title} - {Artist}", title, artist);
            return null;
        }
    }

    /// <summary>
    /// Validates all mappings for tracks in active playlists.
    /// Processes in batches, oldest-first.
    /// </summary>
    public async Task ValidateMappingsForPlaylistAsync(
        List<SpotifyPlaylistTrack> tracks,
        bool isPlaylistSync = false,
        int batchSize = 50)
    {
        var spotifyIds = tracks.Select(t => t.SpotifyId).Distinct().ToList();

        _logger.LogInformation("Validating mappings for {Count} tracks from playlist (isPlaylistSync: {IsSync})",
            spotifyIds.Count,
            isPlaylistSync);

        var validatedCount = 0;
        var upgradedCount = 0;
        var deletedCount = 0;

        foreach (var spotifyId in spotifyIds)
        {
            var mapping = await _mappingService.GetMappingAsync(spotifyId);
            if (mapping == null) continue;

            var originalType = mapping.TargetType;
            var validatedMapping = await ValidateMappingAsync(mapping, isPlaylistSync);

            if (validatedMapping == null)
            {
                deletedCount++;
            }
            else if (validatedMapping.TargetType != originalType)
            {
                upgradedCount++;
            }

            validatedCount++;

            // Rate limiting to avoid overwhelming Jellyfin
            if (validatedCount % batchSize == 0)
            {
                await Task.Delay(100); // 100ms pause every 50 validations
            }
        }

        _logger.LogInformation("✓ Validation complete: {Validated} checked, {Upgraded} upgraded to local, {Deleted} deleted",
            validatedCount,
            upgradedCount,
            deletedCount);
    }
}
