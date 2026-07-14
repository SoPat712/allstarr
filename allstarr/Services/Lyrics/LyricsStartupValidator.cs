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
        WriteStatus("Lyrics Test Song", $"{TestSongTitle} by {TestArtist}", ConsoleColor.Cyan);
        WriteDetail($"Spotify ID: {TestSpotifyId}");
        
        var allSuccess = true;

        // Test 1: LRCLib
        allSuccess &= await TestLrclibAsync(cancellationToken);
        
        // Test 2: Spotify Lyrics Sidecar
        allSuccess &= await TestSpotifyLyricsSidecarAsync(cancellationToken);
        
        // Test 3: Spotify API (if enabled)
        if (_spotifySettings.Enabled)
        {
            allSuccess &= await TestSpotifyApiAsync(cancellationToken);
        }
        else
        {
            WriteStatus("Spotify API", "DISABLED", ConsoleColor.Yellow);
            WriteDetail("Enable SpotifyApi__Enabled to test Spotify API lyrics");
        }

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
                var doc = JsonDocument.Parse(json);
                
                var hasSyncedLyrics = doc.RootElement.TryGetProperty("syncedLyrics", out var synced) && 
                                     !string.IsNullOrEmpty(synced.GetString());
                var hasPlainLyrics = doc.RootElement.TryGetProperty("plainLyrics", out var plain) && 
                                    !string.IsNullOrEmpty(plain.GetString());
                
                WriteStatus("LRCLib", "WORKING", ConsoleColor.Green);
                WriteDetail($"✓ Synced: {hasSyncedLyrics}, Plain: {hasPlainLyrics}");
                return true;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                WriteStatus("LRCLib", "NO LYRICS FOUND", ConsoleColor.Yellow);
                WriteDetail("Service is working but no lyrics available for test song");
                return true; // Service is working, just no lyrics
            }
            else
            {
                WriteStatus("LRCLib", $"HTTP {(int)response.StatusCode}", ConsoleColor.Red);
                return false;
            }
        }
        catch (Exception ex)
        {
            WriteStatus("LRCLib", "ERROR", ConsoleColor.Red);
            WriteDetail($"LRCLib validation failed ({ex.GetType().Name})");
            return false;
        }
    }

    private async Task<bool> TestSpotifyLyricsSidecarAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(_spotifySettings.LyricsApiUrl))
            {
                WriteStatus("Spotify Lyrics Sidecar", "NOT CONFIGURED", ConsoleColor.Yellow);
                WriteDetail("Set SpotifyApi__LyricsApiUrl to enable");
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
                    WriteStatus("Spotify Lyrics Sidecar", "API ERROR", ConsoleColor.Yellow);
                    WriteDetail("Check if sp_dc cookie is valid");
                    return false;
                }
                
                var syncType = doc.RootElement.TryGetProperty("syncType", out var st) 
                    ? st.GetString() 
                    : "UNKNOWN";
                var lineCount = doc.RootElement.TryGetProperty("lines", out var lines) 
                    ? lines.GetArrayLength() 
                    : 0;
                
                WriteStatus("Spotify Lyrics Sidecar", "WORKING", ConsoleColor.Green);
                WriteDetail($"✓ Type: {syncType}, Lines: {lineCount}");
                return true;
            }
            else
            {
                WriteStatus("Spotify Lyrics Sidecar", $"HTTP {(int)response.StatusCode}", ConsoleColor.Red);
                WriteDetail("Check if spotify-lyrics container is running");
                return false;
            }
        }
        catch (Exception ex)
        {
            WriteStatus("Spotify Lyrics Sidecar", "ERROR", ConsoleColor.Red);
            WriteDetail($"Lyrics sidecar validation failed ({ex.GetType().Name})");
            WriteDetail("Ensure spotify-lyrics container is running in docker-compose");
            return false;
        }
    }

    private async Task<bool> TestSpotifyApiAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_spotifySettings.Enabled)
            {
                WriteStatus("Spotify API", "DISABLED", ConsoleColor.Gray);
                return true;
            }

            WriteStatus("Spotify API", "CONFIGURED", ConsoleColor.Green);
            WriteDetail("Note: Spotify API is used for track matching and lyrics");
            return true;
        }
        catch (Exception ex)
        {
            WriteStatus("Spotify API", "ERROR", ConsoleColor.Red);
            WriteDetail($"Spotify validation failed ({ex.GetType().Name})");
            return false;
        }
    }
}
