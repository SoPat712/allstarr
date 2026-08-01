using System.Text.Json;
using allstarr.Core.Downloads;
using Microsoft.Extensions.Options;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services;

namespace allstarr.Services.Local;

/// <summary>
/// Local library service implementation
/// </summary>
public class LocalLibraryService : ILocalLibraryService
{
    private readonly string _downloadDirectory;
    private readonly HttpClient _httpClient;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly ILogger<LocalLibraryService> _logger;
    private readonly IDownloadedSongMappingStore _downloadedSongs;

    // Debounce to avoid triggering too many scans
    private DateTime _lastScanTrigger = DateTime.MinValue;
    private readonly TimeSpan _scanDebounceInterval = TimeSpan.FromSeconds(30);

    public LocalLibraryService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IOptions<SubsonicSettings> subsonicSettings,
        IDownloadedSongMappingStore downloadedSongs,
        ILogger<LocalLibraryService> logger)
    {
        _downloadDirectory = configuration["Library:DownloadPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "downloads");
        _httpClient = httpClientFactory.CreateClient();
        _subsonicSettings = subsonicSettings.Value;
        _downloadedSongs = downloadedSongs;
        _logger = logger;

        if (!Directory.Exists(_downloadDirectory))
        {
            Directory.CreateDirectory(_downloadDirectory);
        }
    }

    public async Task<string?> GetLocalPathForExternalSongAsync(string externalProvider, string externalId)
    {
        var mapping = await _downloadedSongs.FindAsync(
            NormalizeProvider(externalProvider),
            externalId.Trim());
        if (mapping is not null)
        {
            if (File.Exists(mapping.LocalPath))
            {
                return mapping.LocalPath;
            }

            await _downloadedSongs.RemoveAsync(mapping.Id, mapping.Revision);
            _logger.LogDebug(
                "Removed stale downloaded-song mapping for {ProviderId}:{ExternalId} because its file is missing",
                mapping.ProviderId,
                mapping.ExternalId);
        }

        return null;
    }

    public async Task RegisterDownloadedSongAsync(Song song, string localPath)
    {
        if (song.ExternalProvider == null || song.ExternalId == null) return;

        await _downloadedSongs.UpsertAsync(new DownloadedSongMappingEntity
        {
            Id = Guid.CreateVersion7(),
            ProviderId = NormalizeProvider(song.ExternalProvider),
            ExternalId = song.ExternalId.Trim(),
            LocalPath = Path.GetFullPath(localPath),
            Title = song.Title,
            Artist = song.Artist,
            Album = song.Album,
            DownloadedAt = DateTimeOffset.UtcNow,
            Revision = 1
        });
    }

    public (bool isExternal, string? provider, string? externalId) ParseSongId(string songId)
    {
        var (isExternal, provider, _, externalId) = ParseExternalId(songId);
        return (isExternal, provider, externalId);
    }

    public (bool isExternal, string? provider, string? type, string? externalId) ParseExternalId(string id)
    {
        if (!id.StartsWith("ext-"))
        {
            return (false, null, null, null);
        }

        var remainder = id[4..];

        // Provider IDs may contain hyphens, so locate the typed resource marker instead
        // of assuming the provider occupies one dash-delimited segment.
        foreach (var type in new[] { "song", "album", "artist", "playlist" })
        {
            var marker = $"-{type}-";
            var markerIndex = remainder.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex > 0 && markerIndex + marker.Length < remainder.Length)
            {
                return (true,
                    remainder[..markerIndex],
                    type,
                    remainder[(markerIndex + marker.Length)..]);
            }
        }

        // Legacy format: ext-{provider}-{id} (assumes "song" type for backward compatibility)
        // This handles both 3-part IDs and 4+ part IDs where parts[2] is NOT a known type
        var firstSeparator = remainder.IndexOf('-');
        if (firstSeparator > 0 && firstSeparator + 1 < remainder.Length)
        {
            var provider = remainder[..firstSeparator];
            var externalId = remainder[(firstSeparator + 1)..];
            return (true, provider, "song", externalId);
        }

        return (false, null, null, null);
    }

    private static string NormalizeProvider(string providerId) =>
        providerId.Trim().ToLowerInvariant();

    public string GetDownloadDirectory() => _downloadDirectory;

    public async Task<bool> TriggerLibraryScanAsync()
    {
        // Debounce: avoid triggering too many successive scans
        var now = DateTime.UtcNow;
        if (now - _lastScanTrigger < _scanDebounceInterval)
        {
            _logger.LogDebug("Scan debounced - last scan was {Elapsed}s ago",
                (now - _lastScanTrigger).TotalSeconds);
            return true;
        }

        _lastScanTrigger = now;

        try
        {
            // Call Subsonic API to trigger a scan
            // Note: This endpoint works without authentication on most Subsonic/Navidrome servers
            // when called from localhost. For remote servers requiring auth, this would need
            // to be refactored to accept credentials from the controller layer.
            var url = $"{_subsonicSettings.Url}/rest/startScan?f=json";

            _logger.LogInformation("Triggering Subsonic library scan...");

            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                _logger.LogInformation("Subsonic scan triggered successfully: {Response}", content);
                return true;
            }
            else
            {
                _logger.LogError("Failed to trigger Subsonic scan: {StatusCode} - Server may require authentication", response.StatusCode);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering Subsonic library scan");
            return false;
        }
    }

    public async Task<ScanStatus?> GetScanStatusAsync()
    {
        try
        {
            // Note: This endpoint works without authentication on most Subsonic/Navidrome servers
            // when called from localhost.
            var url = $"{_subsonicSettings.Url}/rest/getScanStatus?f=json";

            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(content);

                if (doc.RootElement.TryGetProperty("subsonic-response", out var subsonicResponse) &&
                    subsonicResponse.TryGetProperty("scanStatus", out var scanStatus))
                {
                    return new ScanStatus
                    {
                        Scanning = scanStatus.TryGetProperty("scanning", out var scanning) && scanning.GetBoolean(),
                        Count = scanStatus.TryGetProperty("count", out var count) ? count.GetInt32() : null
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Subsonic scan status");
        }

        return null;
    }
}
