using allstarr.Models.Settings;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Spotify;

/// <summary>
/// Creates SpotifyApiClient instances bound to a specific session cookie.
/// </summary>
public class SpotifyApiClientFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly SpotifyApiSettings _baseSettings;

    public SpotifyApiClientFactory(
        ILoggerFactory loggerFactory,
        IOptions<SpotifyApiSettings> settings)
    {
        _loggerFactory = loggerFactory;
        _baseSettings = settings.Value;
    }

    public SpotifyApiClient Create(string sessionCookie)
    {
        var scopedSettings = new SpotifyApiSettings
        {
            Enabled = _baseSettings.Enabled,
            SessionCookie = sessionCookie,
            CacheDurationMinutes = _baseSettings.CacheDurationMinutes,
            RateLimitDelayMs = _baseSettings.RateLimitDelayMs,
            PreferIsrcMatching = _baseSettings.PreferIsrcMatching,
            SessionCookieSetDate = _baseSettings.SessionCookieSetDate,
            LyricsApiUrl = _baseSettings.LyricsApiUrl
        };

        return new SpotifyApiClient(
            _loggerFactory.CreateLogger<SpotifyApiClient>(),
            Options.Create(scopedSettings));
    }
}
