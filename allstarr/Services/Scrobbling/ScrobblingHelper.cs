using System.Text.Json;
using allstarr.Models.Scrobbling;
using allstarr.Services.Jellyfin;

namespace allstarr.Services.Scrobbling;

public class ScrobblingHelper
{
    private readonly JellyfinProxyService _proxyService;
    private readonly ILogger<ScrobblingHelper> _logger;

    public ScrobblingHelper(
        JellyfinProxyService proxyService,
        ILogger<ScrobblingHelper> logger)
    {
        _proxyService = proxyService;
        _logger = logger;
    }

    public async Task<ScrobbleTrack?> GetScrobbleTrackFromItemIdAsync(
        string itemId,
        Microsoft.AspNetCore.Http.IHeaderDictionary headers)
    {
        try
        {
            var (itemResult, statusCode) = await _proxyService.GetJsonAsync($"Items/{itemId}", null, headers);

            if (itemResult == null || statusCode != 200)
            {
                _logger.LogWarning("Failed to fetch item details for scrobbling: {ItemId} (status: {StatusCode})",
                    itemId, statusCode);
                return null;
            }

            return ExtractScrobbleTrackFromJson(itemResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching item for scrobbling: {ItemId}", itemId);
            return null;
        }
    }

    public ScrobbleTrack? ExtractScrobbleTrackFromJson(JsonDocument itemJson)
    {
        try
        {
            var item = itemJson.RootElement;

            var title = item.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
            var artist = ExtractArtist(item);

            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(artist))
            {
                _logger.LogDebug("Cannot create scrobble track - missing title or artist");
                return null;
            }

            var album = item.TryGetProperty("Album", out var albumProp) ? albumProp.GetString() : null;
            var albumArtist = ExtractAlbumArtist(item);
            var durationSeconds = ExtractDurationSeconds(item);
            var musicBrainzId = ExtractMusicBrainzId(item);

            var path = item.TryGetProperty("Path", out var pathProp) ? pathProp.GetString() : null;
            var isExternal = path?.StartsWith("ext-") == true ||
                             path?.Contains("/kept/") == true ||
                             path?.Contains("\\kept\\") == true;

            return new ScrobbleTrack
            {
                Title = title,
                Artist = artist,
                Album = album,
                AlbumArtist = albumArtist,
                DurationSeconds = durationSeconds,
                MusicBrainzId = musicBrainzId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                IsExternal = isExternal,
                StartPositionSeconds = 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting scrobble track from JSON");
            return null;
        }
    }

    public static bool IsTrackLongEnoughToScrobble(int durationSeconds) => durationSeconds >= 30;

    public static bool HasListenedEnoughToScrobble(int trackDurationSeconds, int playedSeconds) =>
        playedSeconds >= Math.Min(trackDurationSeconds / 2.0, 240);

    public static bool HasRequiredMetadata(string? title, string? artist) =>
        !string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist);

    public static string FormatTrackForDisplay(string title, string artist) => $"{title} - {artist}";

    private static string? ExtractArtist(JsonElement item)
    {
        if (item.TryGetProperty("Artists", out var artistsProp) && artistsProp.ValueKind == JsonValueKind.Array)
        {
            var firstArtist = artistsProp.EnumerateArray().FirstOrDefault();
            if (firstArtist.ValueKind == JsonValueKind.String)
            {
                return firstArtist.GetString();
            }
        }

        if (item.TryGetProperty("AlbumArtist", out var albumArtistProp))
        {
            return albumArtistProp.GetString();
        }

        if (item.TryGetProperty("ArtistItems", out var artistItemsProp) && artistItemsProp.ValueKind == JsonValueKind.Array)
        {
            var firstArtistItem = artistItemsProp.EnumerateArray().FirstOrDefault();
            if (firstArtistItem.TryGetProperty("Name", out var artistNameProp))
            {
                return artistNameProp.GetString();
            }
        }

        return null;
    }

    private static string? ExtractAlbumArtist(JsonElement item)
    {
        if (item.TryGetProperty("AlbumArtist", out var albumArtistProp))
        {
            return albumArtistProp.GetString();
        }

        return ExtractArtist(item);
    }

    private static int? ExtractDurationSeconds(JsonElement item)
    {
        if (item.TryGetProperty("RunTimeTicks", out var ticksProp))
        {
            var ticks = ticksProp.GetInt64();
            return (int)(ticks / TimeSpan.TicksPerSecond);
        }

        return null;
    }

    private static string? ExtractMusicBrainzId(JsonElement item)
    {
        if (item.TryGetProperty("ProviderIds", out var providerIdsProp))
        {
            if (providerIdsProp.TryGetProperty("MusicBrainzTrack", out var mbidProp))
            {
                return mbidProp.GetString();
            }
        }

        return null;
    }
}
