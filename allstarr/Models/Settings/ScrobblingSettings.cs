namespace allstarr.Models.Settings;

/// <summary>
/// Settings for scrobbling services (Last.fm, ListenBrainz, etc.).
/// </summary>
public class ScrobblingSettings
{
    /// <summary>
    /// Whether scrobbling is enabled globally.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether to scrobble local library tracks.
    /// Recommended: Keep disabled and use native Jellyfin plugins instead.
    /// </summary>
    public bool LocalTracksEnabled { get; set; }

    /// <summary>
    /// Emits a synthetic local "played" signal from progress events when local scrobbling is disabled.
    /// Default is false to avoid duplicate local scrobbles with Jellyfin plugins.
    /// </summary>
    public bool SyntheticLocalPlayedSignalEnabled { get; set; }

    /// <summary>
    /// Last.fm settings.
    /// </summary>
    public LastFmSettings LastFm { get; set; } = new();

    /// <summary>
    /// ListenBrainz settings (future).
    /// </summary>
    public ListenBrainzSettings ListenBrainz { get; set; } = new();
}

/// <summary>
/// Last.fm scrobbling settings.
/// </summary>
public class LastFmSettings
{
    /// <summary>
    /// Whether Last.fm scrobbling is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Last.fm API key (32-character hex string). Required when Last.fm is enabled.
    /// Create an application at https://www.last.fm/api/account/create
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Last.fm shared secret (32-character hex string). Required when Last.fm is enabled.
    /// </summary>
    public string SharedSecret { get; set; } = string.Empty;

    /// <summary>
    /// Last.fm session key (obtained via Mobile Authentication).
    /// This is user-specific and has infinite lifetime (unless revoked by user).
    /// </summary>
    public string SessionKey { get; set; } = string.Empty;

    /// <summary>
    /// Last.fm username.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Last.fm password (stored for automatic re-authentication if needed).
    /// Only used for authentication, not stored in plaintext in production.
    /// </summary>
    public string? Password { get; set; }

}

/// <summary>
/// ListenBrainz scrobbling settings (future implementation).
/// </summary>
public class ListenBrainzSettings
{
    /// <summary>
    /// Whether ListenBrainz scrobbling is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// ListenBrainz user token.
    /// Get from: https://listenbrainz.org/profile/
    /// </summary>
    public string UserToken { get; set; } = string.Empty;
}
