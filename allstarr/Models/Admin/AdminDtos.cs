namespace allstarr.Models.Admin;

public class ManualMappingRequest
{
    public string SpotifyId { get; set; } = "";
    public string? JellyfinId { get; set; }
    public string? ExternalProvider { get; set; }
    public string? ExternalId { get; set; }
}

public class LyricsMappingRequest
{
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Album { get; set; }
    public int DurationSeconds { get; set; }
    public int LyricsId { get; set; }
}

public class ManualMappingEntry
{
    public string SpotifyId { get; set; } = "";
    public string? JellyfinId { get; set; }
    public string? ExternalProvider { get; set; }
    public string? ExternalId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class LyricsMappingEntry
{
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Album { get; set; }
    public int DurationSeconds { get; set; }
    public int LyricsId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ConfigUpdateRequest
{
    public Dictionary<string, string> Updates { get; set; } = new();
}

public class AddPlaylistRequest
{
    public string Name { get; set; } = string.Empty;
    public string SpotifyId { get; set; } = string.Empty;
    public string LocalTracksPosition { get; set; } = "first";
}

public class LinkPlaylistRequest
{
    public string Name { get; set; } = string.Empty;
    public string SpotifyPlaylistId { get; set; } = string.Empty;
    public string SyncSchedule { get; set; } = "0 8 * * *";
}

public class UpdateScheduleRequest
{
    public string SyncSchedule { get; set; } = string.Empty;
}
public class SpotifyMappingRequest
{
    public required string SpotifyId { get; set; }
    public required string TargetType { get; set; } // "local" or "external"
    public string? LocalId { get; set; }
    public string? ExternalProvider { get; set; }
    public string? ExternalId { get; set; }
    public TrackMetadataRequest? Metadata { get; set; }
}

public class TrackMetadataRequest
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? ArtworkUrl { get; set; }
    public int? DurationMs { get; set; }
}

/// <summary>
/// Request model for updating configuration
/// </summary>
