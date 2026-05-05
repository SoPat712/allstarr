namespace allstarr.Models.Spotify;

/// <summary>
/// Represents a global mapping from a Spotify track ID to either a local Jellyfin track or an external provider track.
/// This is a permanent mapping that speeds up playlist matching.
/// </summary>
public class SpotifyTrackMapping
{
    /// <summary>
    /// Spotify track ID (e.g., "3n3Ppam7vgaVa1iaRUc9Lp")
    /// </summary>
    public required string SpotifyId { get; set; }
    
    /// <summary>
    /// Target type: "local" or "external"
    /// </summary>
    public required string TargetType { get; set; }
    
    /// <summary>
    /// Jellyfin item ID (if TargetType is "local")
    /// </summary>
    public string? LocalId { get; set; }
    
    /// <summary>
    /// External provider name (if TargetType is "external")
    /// </summary>
    public string? ExternalProvider { get; set; }
    
    /// <summary>
    /// External provider track ID (if TargetType is "external")
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// Multi-provider external mappings for this Spotify track.
    /// Keeps additional external provider IDs so rematch/rebuild can add
    /// provider-specific mappings without replacing existing ones.
    /// </summary>
    public List<ExternalTrackMapping> ExternalMappings { get; set; } = new();
    
    /// <summary>
    /// Track metadata for display purposes
    /// </summary>
    public TrackMetadata? Metadata { get; set; }
    
    /// <summary>
    /// How this mapping was created: "auto" or "manual"
    /// </summary>
    public required string Source { get; set; }
    
    /// <summary>
    /// When this mapping was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// When this mapping was last updated (for manual overrides)
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
    
    /// <summary>
    /// When this mapping was last validated (checked if target still exists)
    /// </summary>
    public DateTime? LastValidatedAt { get; set; }
    
    /// <summary>
    /// Whether this mapping needs validation
    /// Local: every 7 days, External: every playlist sync
    /// </summary>
    public bool NeedsValidation(bool isPlaylistSync = false)
    {
        if (!LastValidatedAt.HasValue) return true;
        
        var timeSinceValidation = DateTime.UtcNow - LastValidatedAt.Value;
        
        if (TargetType == "local")
        {
            // Local mappings: validate every 7 days
            return timeSinceValidation.TotalDays >= 7;
        }
        else if (TargetType == "external")
        {
            // External mappings: validate on every playlist sync
            return isPlaylistSync;
        }
        
        return false;
    }

    /// <summary>
    /// Resolves the best external mapping target, preferring the requested provider when available.
    /// </summary>
    public bool TryGetExternalTarget(string? preferredProvider, out string provider, out string externalId)
    {
        provider = string.Empty;
        externalId = string.Empty;

        if (!string.IsNullOrWhiteSpace(preferredProvider))
        {
            var preferred = ExternalMappings.FirstOrDefault(m =>
                string.Equals(m.Provider, preferredProvider, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(m.ExternalId));
            if (preferred != null)
            {
                provider = preferred.Provider;
                externalId = preferred.ExternalId;
                return true;
            }
        }

        var first = ExternalMappings.FirstOrDefault(m =>
            !string.IsNullOrWhiteSpace(m.Provider) && !string.IsNullOrWhiteSpace(m.ExternalId));
        if (first != null)
        {
            provider = first.Provider;
            externalId = first.ExternalId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ExternalProvider) && !string.IsNullOrWhiteSpace(ExternalId))
        {
            provider = ExternalProvider;
            externalId = ExternalId;
            return true;
        }

        return false;
    }
}

public class ExternalTrackMapping
{
    public required string Provider { get; set; }
    public required string ExternalId { get; set; }
    public string Source { get; set; } = "auto";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Track metadata for display in Admin UI
/// </summary>
public class TrackMetadata
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? ArtworkUrl { get; set; }
    public int? DurationMs { get; set; }
}
