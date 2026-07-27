using System.Text.Json;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Services.Validation;

namespace allstarr.Services.Lyrics;

/// <summary>
/// Validates lyrics services (LRCLib, Spotify Lyrics Sidecar, Spotify API) at startup
/// Tests with "22" by Taylor Swift (Spotify ID: 3yII7UwgLF6K5zW3xad3MP)
/// </summary>
public class LyricsStartupValidator : BaseStartupValidator
{
    private readonly SpotifyApiSettings _spotifySettings;

    // Test song: "22" by Taylor Swift
    private const string TestSongTitle = "22";
    private const string TestArtist = "Taylor Swift";
    private const string TestAlbum = "Red";
    private const int TestDuration = 232; // seconds
    private const string TestSpotifyId = "3yII7UwgLF6K5zW3xad3MP";

    public override string ServiceName => "Lyrics Services";

    public LyricsStartupValidator(
        IOptions<SpotifyApiSettings> spotifySettings,
        IHttpClientFactory httpClientFactory)
        : base(httpClientFactory.CreateClient())
    {
        _spotifySettings = spotifySettings.Value;
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        var allSuccess = await TestLrclibAsync(cancellationToken);
        allSuccess &= await TestSpotifyLyricsSidecarAsync(cancellationToken);

        return allSuccess
            ? ValidationResult.Success("Lyrics services validation completed")
            : ValidationResult.Failure("PARTIAL", "Some lyrics services had issues", ConsoleColor.Yellow);
    }

    private async Task<bool> TestLrclibAsync(CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(TestArtist)}&track_name={Uri.EscapeDataString(TestSongTitle)}&album_name={Uri.EscapeDataString(TestAlbum)}&duration={TestDuration}";

            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonDocument.Parse(json).Dispose();
                return true;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return true; // Service is working, just no lyrics
            }
            else
            {
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TestSpotifyLyricsSidecarAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(_spotifySettings.LyricsApiUrl))
            {
                return true; // Not an error, just not configured
            }

            var url = $"{_spotifySettings.LyricsApiUrl}/?trackid={TestSpotifyId}&format=id3";

            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonDocument.Parse(json);

                var hasError = doc.RootElement.TryGetProperty("error", out var error) && error.GetBoolean();

                if (hasError)
                {
                    return false;
                }

                return true;
            }
            else
            {
                return false;
            }
        }
        catch
        {
            return false;
        }
    }
}
