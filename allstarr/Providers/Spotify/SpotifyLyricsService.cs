using System.Text.Json;
using allstarr.Models.Lyrics;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Lyrics;

public class SpotifyLyricsService
{
    private readonly ILogger<SpotifyLyricsService> _logger;
    private readonly SpotifyApiSettings _settings;
    private readonly HttpClient _httpClient;

    public SpotifyLyricsService(
        ILogger<SpotifyLyricsService> logger,
        IOptions<SpotifyApiSettings> settings,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _settings = settings.Value;

        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<SpotifyLyricsResult?> GetLyricsByTrackIdAsync(string spotifyTrackId)
    {
        if (string.IsNullOrEmpty(_settings.LyricsApiUrl))
        {
            _logger.LogInformation("Spotify lyrics API URL not configured");
            return null;
        }

        spotifyTrackId = ExtractTrackId(spotifyTrackId);

        try
        {
            var url = $"{_settings.LyricsApiUrl}/?trackid={spotifyTrackId}&format=id3";

            _logger.LogDebug("Fetching lyrics from sidecar API: {Url}", url);

            using var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Sidecar API returned {StatusCode} for track {TrackId}",
                    response.StatusCode, spotifyTrackId);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = ParseSidecarResponse(json, spotifyTrackId);

            if (result != null)
            {
                _logger.LogDebug("Got Spotify lyrics from sidecar for track {TrackId} ({LineCount} lines)",
                    spotifyTrackId, result.Lines.Count);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching lyrics from sidecar API for track {TrackId}", spotifyTrackId);
            return null;
        }
    }

    public LyricsInfo? ToLyricsInfo(SpotifyLyricsResult spotifyLyrics)
    {
        if (spotifyLyrics.Lines.Count == 0)
        {
            return null;
        }

        var lrcLines = new List<string>();
        foreach (var line in spotifyLyrics.Lines)
        {
            var timestamp = TimeSpan.FromMilliseconds(line.StartTimeMs);
            var mm = (int)timestamp.TotalMinutes;
            var ss = timestamp.Seconds;
            var ms = timestamp.Milliseconds / 10;

            lrcLines.Add($"[{mm:D2}:{ss:D2}.{ms:D2}]{line.Words}");
        }

        return new LyricsInfo
        {
            TrackName = spotifyLyrics.TrackName ?? "",
            ArtistName = spotifyLyrics.ArtistName ?? "",
            AlbumName = spotifyLyrics.AlbumName ?? "",
            Duration = (int)(spotifyLyrics.DurationMs / 1000),
            Instrumental = spotifyLyrics.Lines.Count == 0,
            SyncedLyrics = string.Join("\n", lrcLines),
            PlainLyrics = string.Join("\n", spotifyLyrics.Lines.Select(l => l.Words)),
            Source = "spotify"
        };
    }

    private SpotifyLyricsResult? ParseSidecarResponse(string json, string trackId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var error) && error.GetBoolean())
            {
                _logger.LogError("Sidecar API returned error for track {TrackId}", trackId);
                return null;
            }

            var result = new SpotifyLyricsResult
            {
                SpotifyTrackId = trackId
            };

            if (root.TryGetProperty("lines", out var lines))
            {
                foreach (var line in lines.EnumerateArray())
                {
                    var lyricsLine = new SpotifyLyricsLine
                    {
                        StartTimeMs = ReadMilliseconds(line, "startTimeMs"),
                        Words = line.TryGetProperty("words", out var words)
                            ? words.GetString() ?? "" : "",
                        EndTimeMs = ReadMilliseconds(line, "endTimeMs")
                    };

                    result.Lines.Add(lyricsLine);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing sidecar API response");
            return null;
        }
    }

    private static long ReadMilliseconds(JsonElement line, string propertyName)
    {
        if (!line.TryGetProperty(propertyName, out var value)) return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number,
            _ => 0
        };
    }

    private static string ExtractTrackId(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        const string uriPrefix = "spotify:track:";
        if (input.StartsWith(uriPrefix)) return input[uriPrefix.Length..];

        const string path = "/track/";
        var marker = input.IndexOf(path, StringComparison.Ordinal);
        if (marker < 0) return input;

        var start = marker + path.Length;
        var end = input.IndexOf('?', start);
        return end > 0 ? input[start..end] : input[start..];
    }
}

public class SpotifyLyricsResult
{
    public string SpotifyTrackId { get; set; } = string.Empty;
    public string? TrackName { get; set; }
    public string? ArtistName { get; set; }
    public string? AlbumName { get; set; }
    public long DurationMs { get; set; }

    public List<SpotifyLyricsLine> Lines { get; set; } = new();
}

public class SpotifyLyricsLine
{
    public long StartTimeMs { get; set; }
    public long EndTimeMs { get; set; }
    public string Words { get; set; } = string.Empty;
}
