using System.Text;
using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Download;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Local;
using allstarr.Services.Common;
using allstarr.Services.Lyrics;
using Microsoft.Extensions.Options;
using IOFile = System.IO.File;
using Microsoft.Extensions.Logging;

namespace allstarr.Services.SquidWTF;

/// <summary>
/// Handles track downloading from tidal.squid.wtf (no encryption, no auth required).
/// 
/// Downloads are direct from Tidal's CDN via the squid.wtf proxy. The service:
/// 1. Fetches download info from hifi-api /track/ endpoint
/// 2. Decodes base64 manifest to get actual Tidal CDN URL
/// 3. Downloads directly from Tidal CDN (no decryption needed)
/// 4. Converts Tidal track ID to Spotify ID in parallel (for lyrics matching)
/// 5. Writes ID3/FLAC metadata tags and embeds cover art
/// 
/// Per hifi-api spec, the /track/ endpoint returns:
/// { "version": "2.0", "data": { 
///     trackId, assetPresentation, audioMode, audioQuality,
///     manifestMimeType: "application/vnd.tidal.bts",
///     manifest: "base64-encoded-json",
///     albumReplayGain, trackReplayGain, bitDepth, sampleRate
/// }}
/// 
/// The manifest decodes to:
/// { "mimeType": "audio/flac", "codecs": "flac", "encryptionType": "NONE",
///   "urls": ["https://lgf.audio.tidal.com/mediatracks/..."] }
/// 
/// Quality Mapping:
/// - HI_RES → HI_RES_LOSSLESS (24-bit/192kHz FLAC)
/// - FLAC/LOSSLESS → LOSSLESS (16-bit/44.1kHz FLAC)
/// - HIGH → HIGH (320kbps AAC)
/// - LOW → LOW (96kbps AAC)
/// 
/// Features:
/// - Racing multiple endpoints for fastest download
/// - Automatic failover to backup endpoints
/// - Parallel Spotify ID conversion via Odesli
/// - Organized folder structure: Artist/Album/Track
/// - Unique filename resolution for duplicates
/// - Support for both cache and permanent storage modes
/// </summary>
public class SquidWTFDownloadService : BaseDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly SquidWTFSettings _squidwtfSettings;
    private readonly OdesliService _odesliService;
    private readonly RoundRobinFallbackHelper _fallbackHelper;
    private readonly IServiceProvider _serviceProvider;

    protected override string ProviderName => "squidwtf";

    public SquidWTFDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<SquidWTFSettings> SquidWTFSettings,
		IServiceProvider serviceProvider,
        ILogger<SquidWTFDownloadService> logger,
        OdesliService odesliService,
        List<string> apiUrls)
        : base(configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {
        _httpClient = httpClientFactory.CreateClient("SquidWTF");
        _squidwtfSettings = SquidWTFSettings.Value;
        _odesliService = odesliService;
        _fallbackHelper = new RoundRobinFallbackHelper(apiUrls, logger, "SquidWTF");
        _serviceProvider = serviceProvider;
        
        // Increase timeout for large downloads and slow endpoints
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
        _minRequestIntervalMs = _squidwtfSettings.MinRequestIntervalMs;
    }
    
	
    #region BaseDownloadService Implementation

    public override async Task<bool> IsAvailableAsync()
    {
        return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
        {
            var response = await _httpClient.GetAsync(baseUrl);
            return response.IsSuccessStatusCode;
        });
	}


    private async Task<string> RunDownloadWithFallbackAsync(string trackId, Song song, string quality, string basePath, CancellationToken cancellationToken)
    {
        var songId = BuildTrackedSongId(trackId);
        var raceCount = Math.Min(3, _fallbackHelper.EndpointCount);

        if (raceCount > 1)
        {
            Logger.LogInformation(
                "Racing top {EndpointCount} SquidWTF endpoints for track {TrackId} manifest resolution",
                raceCount, trackId);
        }

        var downloadInfo = await _fallbackHelper.RaceTopEndpointsAsync(
            Math.Max(1, raceCount),
            (baseUrl, ct) => FetchTrackDownloadInfoAsync(baseUrl, trackId, quality, ct),
            cancellationToken);

        Logger.LogInformation(
            "Track download info resolved via {Endpoint} (Format: {Format}, Quality: {Quality})",
            downloadInfo.Endpoint, downloadInfo.MimeType, downloadInfo.AudioQuality);
        Logger.LogDebug("Resolved SquidWTF CDN download URL: {Url}", downloadInfo.DownloadUrl);

        var extension = downloadInfo.MimeType?.ToLower() switch
        {
            "audio/flac" => ".flac", "audio/mpeg" => ".mp3", "audio/mp4" => ".m4a", _ => ".flac"
        };

        var artistForPath = song.AlbumArtist ?? song.Artist;
        var outputPath = PathHelper.BuildTrackPath(basePath, artistForPath, song.Album, song.Title, song.Track, extension, "squidwtf", trackId);

        var albumFolder = Path.GetDirectoryName(outputPath)!;
        EnsureDirectoryExists(albumFolder);

        if (basePath.EndsWith("transcoded") && IOFile.Exists(outputPath))
        {
            IOFile.SetLastWriteTime(outputPath, DateTime.UtcNow);
            Logger.LogInformation("Quality override cache hit: {Path}", outputPath);
            return outputPath;
        }

        outputPath = PathHelper.ResolveUniquePath(outputPath);

        using var req = new HttpRequestMessage(HttpMethod.Get, downloadInfo.DownloadUrl);
        req.Headers.Add("User-Agent", "Mozilla/5.0");
        req.Headers.Add("Accept", "*/*");
        var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        res.EnsureSuccessStatusCode();

        await using var responseStream = await res.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputFile = IOFile.Create(outputPath);
        var totalBytes = res.Content.Headers.ContentLength;
        var buffer = new byte[81920];
        long totalBytesRead = 0;

        while (true)
        {
            var bytesRead = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead <= 0)
            {
                break;
            }

            await outputFile.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalBytesRead += bytesRead;

            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                SetDownloadProgress(songId, (double)totalBytesRead / totalBytes.Value);
            }
        }

        await outputFile.DisposeAsync();
        SetDownloadProgress(songId, 1.0);

        _ = Task.Run(async () =>
        {
            try
            {
                var spotifyId = await _odesliService.ConvertTidalToSpotifyIdAsync(trackId, CancellationToken.None);
                if (!string.IsNullOrEmpty(spotifyId))
                {
                    Logger.LogDebug("Background Spotify ID obtained for Tidal/{TrackId}: {SpotifyId}", trackId, spotifyId);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Background Spotify ID conversion failed for Tidal/{TrackId}", trackId);
            }
        });

        await WriteMetadataAsync(outputPath, song, cancellationToken);
        return outputPath;
    }

    protected override async Task<string> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        return await QueueRequestAsync(async () =>
        {
            Exception? lastException = null;
            var qualityOrder = BuildQualityFallbackOrder(_squidwtfSettings.Quality);
            var basePath = CurrentStorageMode == StorageMode.Cache 
                ? Path.Combine(DownloadPath, "cache") : Path.Combine(DownloadPath, "permanent");

            foreach (var quality in qualityOrder)
            {
                try
                {
                    return await RunDownloadWithFallbackAsync(trackId, song, quality, basePath, cancellationToken);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    if (!string.Equals(quality, qualityOrder[^1], StringComparison.Ordinal))
                    {
                        Logger.LogWarning("Track {TrackId} unavailable at SquidWTF quality {Quality}: {Error}. Trying lower quality", trackId, quality, DescribeException(ex));
                    }
                }
            }
            throw lastException ?? new Exception($"Unable to download track {trackId}");
        });
    }

    #endregion	
	
    #region Quality Override Support
    
    /// <summary>
    /// Downloads a track at a specific quality tier, capped at the .env quality ceiling.
    /// The .env quality is the maximum — client requests can only go equal or lower.
    /// 
    /// Quality hierarchy (highest to lowest): HI_RES_LOSSLESS > LOSSLESS > HIGH > LOW
    /// 
    /// Examples:
    ///   env=HI_RES_LOSSLESS: Original→HI_RES_LOSSLESS, High→HIGH, Low→LOW
    ///   env=LOSSLESS:        Original→LOSSLESS,         High→HIGH, Low→LOW
    ///   env=HIGH:            Original→HIGH,             High→HIGH, Low→LOW
    ///   env=LOW:             Original→LOW,              High→LOW,  Low→LOW
    /// </summary>
    protected override async Task<string> DownloadTrackWithQualityAsync(
        string trackId, Song song, StreamQuality quality, CancellationToken cancellationToken)
    {
        if (quality == StreamQuality.Original)
        {
            return await DownloadTrackAsync(trackId, song, cancellationToken);
        }

        // Map StreamQuality to SquidWTF quality string, capped at .env ceiling
        var envQuality = NormalizeSquidWTFQuality(_squidwtfSettings.Quality);
        var squidQuality = MapStreamQualityToSquidWTF(quality, envQuality);

        Logger.LogInformation(
            "Quality override: StreamQuality.{Quality} → SquidWTF quality '{SquidQuality}' (env ceiling: {EnvQuality}) for track {TrackId}",
            quality, squidQuality, envQuality, trackId);

        var basePath = Path.Combine("downloads", "transcoded");

        return await QueueRequestAsync(async () =>
        {
            return await RunDownloadWithFallbackAsync(trackId, song, squidQuality, basePath, cancellationToken);
        });
    }

    /// <summary>
    /// Normalizes the .env quality string to a standard SquidWTF quality level.
    /// Maps various aliases (HI_RES, FLAC, etc.) to canonical names.
    /// </summary>
    private static string NormalizeSquidWTFQuality(string? quality)
    {
        if (string.IsNullOrEmpty(quality)) return "LOSSLESS";
        
        return quality.ToUpperInvariant() switch
        {
            "HI_RES" or "HI_RES_LOSSLESS" => "HI_RES_LOSSLESS",
            "FLAC" or "LOSSLESS" => "LOSSLESS",
            "HIGH" => "HIGH",
            "LOW" => "LOW",
            _ => "LOSSLESS"
        };
    }

    /// <summary>
    /// Maps a StreamQuality tier to a SquidWTF quality string, capped at the .env ceiling.
    /// The .env quality is the maximum — client requests can only go equal or lower.
    /// </summary>
    private static string MapStreamQualityToSquidWTF(StreamQuality streamQuality, string envQuality)
    {
        // Quality ranking from highest to lowest
        var ranking = new[] { "HI_RES_LOSSLESS", "LOSSLESS", "HIGH", "LOW" };
        var envIndex = Array.IndexOf(ranking, envQuality);
        if (envIndex < 0) envIndex = 1; // Default to LOSSLESS if unknown

        // Map StreamQuality to the "ideal" SquidWTF quality
        var idealQuality = streamQuality switch
        {
            StreamQuality.Original => envQuality,  // Lossless client selection → use .env setting
            StreamQuality.High => "HIGH",           // 320/256/192K → HIGH (320kbps AAC)
            StreamQuality.Low => "LOW",             // 128/64K → LOW (96kbps AAC)
            _ => envQuality
        };

        // Cap: if the ideal quality is higher than env, clamp down to env
        // Lower array index = higher quality
        var idealIndex = Array.IndexOf(ranking, idealQuality);
        if (idealIndex < 0) idealIndex = envIndex;
        
        if (idealIndex < envIndex)
        {
            return envQuality;
        }

        return idealQuality;
    }
    
    #endregion
	
	#region SquidWTF API Methods
	
    // Removed GetTrackDownloadInfoAsync as it's now integrated inside RunDownloadWithFallbackAsync

    private async Task<DownloadResult> FetchTrackDownloadInfoAsync(
        string baseUrl,
        string trackId,
        string quality,
        CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}/track/?id={trackId}&quality={quality}";

        Logger.LogInformation("Requesting SquidWTF track manifest for track {TrackId} from {Endpoint} at quality {Quality}",
            trackId, baseUrl, quality);
        Logger.LogDebug("Fetching SquidWTF track download info from: {Url}", url);

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            response.EnsureSuccessStatusCode();
        }
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        
        if (!doc.RootElement.TryGetProperty("data", out var data))
        {
            throw new Exception("Invalid response from API");
        }
        
        // Get the manifest (base64 encoded JSON containing the actual CDN URL)
        var manifestBase64 = data.GetProperty("manifest").GetString()
            ?? throw new Exception("No manifest in response");
        
        // Decode the manifest
        var manifestJson = Encoding.UTF8.GetString(Convert.FromBase64String(manifestBase64));
        using var manifest = JsonDocument.Parse(manifestJson);
        
        // Extract the download URL from the manifest
        if (!manifest.RootElement.TryGetProperty("urls", out var urls) || urls.GetArrayLength() == 0)
        {
            throw new Exception("No download URLs in manifest");
        }
        
        var downloadUrl = urls[0].GetString()
            ?? throw new Exception("Download URL is null");
        
        var mimeType = manifest.RootElement.TryGetProperty("mimeType", out var mimeTypeEl)
            ? mimeTypeEl.GetString()
            : "audio/flac";
        
        var audioQuality = data.TryGetProperty("audioQuality", out var audioQualityEl)
            ? audioQualityEl.GetString()
            : quality;

        Logger.LogInformation("SquidWTF track manifest resolved for track {TrackId} via {Endpoint} (mimeType={MimeType}, audioQuality={AudioQuality})",
            trackId, baseUrl, mimeType ?? "audio/flac", audioQuality ?? quality);

        return new DownloadResult
        {
            Endpoint = baseUrl,
            DownloadUrl = downloadUrl,
            MimeType = mimeType ?? "audio/flac",
            AudioQuality = audioQuality ?? quality
        };
    }

    private static IReadOnlyList<string> BuildQualityFallbackOrder(string? configuredQuality)
    {
        return NormalizeQuality(configuredQuality) switch
        {
            "HI_RES_LOSSLESS" => ["HI_RES_LOSSLESS", "LOSSLESS", "HIGH", "LOW"],
            "LOSSLESS" => ["LOSSLESS", "HIGH", "LOW"],
            "HIGH" => ["HIGH", "LOW"],
            "LOW" => ["LOW"],
            _ => ["LOSSLESS", "HIGH", "LOW"]
        };
    }

    private static string NormalizeQuality(string? configuredQuality)
    {
        return configuredQuality?.ToUpperInvariant() switch
        {
            "FLAC" => "LOSSLESS",
            "HI_RES" => "HI_RES_LOSSLESS",
            "HI_RES_LOSSLESS" => "HI_RES_LOSSLESS",
            "LOSSLESS" => "LOSSLESS",
            "HIGH" => "HIGH",
            "LOW" => "LOW",
            _ => "LOSSLESS"
        };
    }

    private static string DescribeException(Exception ex)
    {
        if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode.HasValue)
        {
            var statusCode = (int)httpRequestException.StatusCode.Value;
            return $"{statusCode}: {httpRequestException.StatusCode.Value}";
        }

        return ex.Message;
    }

	
	#endregion
	
    #region Utility Methods

    /// <summary>
    /// Converts Tidal track ID to Spotify ID for lyrics support.
    /// Called in background after streaming starts.
    /// Also prefetches lyrics immediately after conversion.
    /// </summary>
    protected override async Task ConvertToSpotifyIdAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "squidwtf")
        {
            return;
        }

        var spotifyId = await _odesliService.ConvertTidalToSpotifyIdAsync(externalId, CancellationToken.None);
        if (!string.IsNullOrEmpty(spotifyId))
        {
            Logger.LogDebug("Background Spotify ID obtained for Tidal/{TrackId}: {SpotifyId}", externalId, spotifyId);
            
            // Immediately prefetch lyrics now that we have the Spotify ID
            // This ensures lyrics are cached and ready when the client requests them
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var spotifyLyricsService = scope.ServiceProvider.GetService<SpotifyLyricsService>();
                    
                    if (spotifyLyricsService != null)
                    {
                        var lyrics = await spotifyLyricsService.GetLyricsByTrackIdAsync(spotifyId);
                        if (lyrics != null && lyrics.Lines.Count > 0)
                        {
                            Logger.LogDebug("Background lyrics prefetched for Spotify/{SpotifyId}: {LineCount} lines", 
                                spotifyId, lyrics.Lines.Count);
                        }
                        else
                        {
                            Logger.LogDebug("No lyrics available for Spotify/{SpotifyId}", spotifyId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Background lyrics prefetch failed for Spotify/{SpotifyId}", spotifyId);
                }
            });
        }
    }

    #endregion

    private class DownloadResult
    {
        public string Endpoint { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string MimeType { get; set; } = string.Empty;
        public string AudioQuality { get; set; } = string.Empty;
    }
}	
