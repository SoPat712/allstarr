namespace allstarr.Models.Settings;

public sealed class ScrobblingSettings
{
    public bool Enabled { get; set; }
    public bool LocalTracksEnabled { get; set; }
    public bool SyntheticLocalPlayedSignalEnabled { get; set; }
    public LastFmSettings LastFm { get; set; } = new();
    public ListenBrainzSettings ListenBrainz { get; set; } = new();
}

public sealed class LastFmSettings
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string SharedSecret { get; set; } = string.Empty;
    public string SessionKey { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class ListenBrainzSettings
{
    public bool Enabled { get; set; }
    public string UserToken { get; set; } = string.Empty;
}
