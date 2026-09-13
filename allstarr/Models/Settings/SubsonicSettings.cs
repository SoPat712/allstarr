namespace allstarr.Models.Settings;

public enum BackendType
{
    Subsonic,
    Jellyfin
}

public enum DownloadMode
{
    Track,
    Album
}

public enum ExplicitFilter
{
    All,
    ExplicitOnly,
    CleanOnly
}

public enum StorageMode
{
    Permanent,
    Cache
}

public enum MusicService
{
    None,
    Deezer,
    Qobuz,
    AppleMusic
}

public abstract class MediaBackendSettings
{
    public ExplicitFilter ExplicitFilter { get; set; } = ExplicitFilter.All;
    public DownloadMode DownloadMode { get; set; } = DownloadMode.Track;
    public MusicService MusicService { get; set; } = MusicService.None;
    public StorageMode StorageMode { get; set; } = StorageMode.Permanent;
    public int CacheDurationHours { get; set; } = 1;
    public bool EnableExternalPlaylists { get; set; } = true;
    public string PlaylistsDirectory { get; set; } = "playlists";
}

public sealed class SubsonicSettings : MediaBackendSettings
{
    public string? Url { get; set; }

    // Encrypted JSON username/password reference used by background playlist writes.
    public string? PlaylistCredentialReference { get; set; }
}
