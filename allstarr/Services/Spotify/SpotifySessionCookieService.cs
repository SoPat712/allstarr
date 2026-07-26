using allstarr.Models.Settings;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Spotify;

/// <summary>
/// Stores and resolves Spotify session cookies in a user-scoped model.
/// </summary>
public class SpotifySessionCookieService
{
    private readonly SpotifyApiSettings _spotifyApiSettings;

    public SpotifySessionCookieService(IOptions<SpotifyApiSettings> spotifyApiSettings)
    {
        _spotifyApiSettings = spotifyApiSettings.Value;
    }

    public Task<string?> ResolveSessionCookieAsync(string? userId) =>
        Task.FromResult(string.IsNullOrWhiteSpace(_spotifyApiSettings.SessionCookie)
            ? null
            : _spotifyApiSettings.SessionCookie);

    public Task<bool> HasAnyConfiguredCookieAsync() =>
        Task.FromResult(!string.IsNullOrWhiteSpace(_spotifyApiSettings.SessionCookie));

    public Task<(bool HasCookie, bool UsingGlobalFallback)> GetCookieStatusAsync(string? userId) =>
        Task.FromResult((
            !string.IsNullOrWhiteSpace(_spotifyApiSettings.SessionCookie),
            !string.IsNullOrWhiteSpace(_spotifyApiSettings.SessionCookie)));

    public Task<DateTime?> GetCookieSetDateAsync(string userId) => Task.FromResult<DateTime?>(null);

}
