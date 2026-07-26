namespace allstarr.Models.Admin;

public class LyricsMappingRequest
{
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Album { get; set; }
    public int DurationSeconds { get; set; }
    public int LyricsId { get; set; }
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
    public string? Name { get; set; }
    public string SpotifyPlaylistId { get; set; } = string.Empty;
    public string SyncSchedule { get; set; } = "0 8 * * *";
    public string? UserId { get; set; }
}

public class UpdateScheduleRequest
{
    public string SyncSchedule { get; set; } = string.Empty;
}
/// <summary>
/// Request model for updating configuration
/// </summary>
