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
        _httpClient = httpClientFactory.CreateClient();
        _squidwtfSettings = SquidWTFSettings.Value;
        _odesliService = odesliService;
        _fallbackHelper = new RoundRobinFallbackHelper(apiUrls, logger, "SquidWTF");
        _serviceProvider = serviceProvider;
        
        // Increase timeout for large downloads and slow endpoints
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
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


    protected override async Task<string> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        var downloadInfo = await GetTrackDownloadInfoAsync(trackId, cancellationToken);
        
        Logger.LogInformation(
            "Track download info resolved via {Endpoint} (Format: {Format}, Quality: {Quality})",
            downloadInfo.Endpoint,
            downloadInfo.MimeType,
            downloadInfo.AudioQuality);
        Logger.LogDebug("Resolved SquidWTF CDN download URL: {Url}", downloadInfo.DownloadUrl);

        // Determine extension from MIME type
        var extension = downloadInfo.MimeType?.ToLower() switch
        {
            "audio/flac" => ".flac",
            "audio/mpeg" => ".mp3",
            "audio/mp4" => ".m4a",
            _ => ".flac" // Default to FLAC
        };
		
        // Build organized folder structure: Artist/Album/Track using AlbumArtist (fallback to Artist for singles)
        var artistForPath = song.AlbumArtist ?? song.Artist;
        // Cache mode uses downloads/cache/ folder, Permanent mode uses downloads/permanent/
        var basePath = SubsonicSettings.StorageMode == StorageMode.Cache 
            ? Path.Combine("downloads", "cache")
            : Path.Combine("downloads", "permanent");
        var outputPath = PathHelper.BuildTrackPath(basePath, artistForPath, song.Album, song.Title, song.Track, extension, "squidwtf", trackId);
        
        // Create directories if they don't exist
        var albumFolder = Path.GetDirectoryName(outputPath)!;
        EnsureDirectoryExists(albumFolder);
        
        // Resolve unique path if file already exists
        outputPath = PathHelper.ResolveUniquePath(outputPath);

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadInfo.DownloadUrl);
        request.Headers.Add("User-Agent", "Mozilla/5.0");
        request.Headers.Add("Accept", "*/*");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();
		
        // Download directly (no decryption needed - squid.wtf handles everything)
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputFile = IOFile.Create(outputPath);
        
		await responseStream.CopyToAsync(outputFile, cancellationToken);
        
        // Close file before writing metadata
        await outputFile.DisposeAsync();
        
		// Start Spotify ID conversion in background (for lyrics support)
		// This doesn't block streaming - lyrics endpoint will fetch it on-demand if needed
		_ = Task.Run(async () =>
		{
			try
			{
				var spotifyId = await _odesliService.ConvertTidalToSpotifyIdAsync(trackId, CancellationToken.None);
				if (!string.IsNullOrEmpty(spotifyId))
				{
					Logger.LogDebug("Background Spotify ID obtained for Tidal/{TrackId}: {SpotifyId}", trackId, spotifyId);
					// Spotify ID is cached by Odesli service for future lyrics requests
				}
			}
			catch (Exception ex)
			{
				Logger.LogDebug(ex, "Background Spotify ID conversion failed for Tidal/{TrackId}", trackId);
			}
		});

        // Write metadata and cover art (without Spotify ID - it's only needed for lyrics)
        await WriteMetadataAsync(outputPath, song, cancellationToken);

        return outputPath;
    }

    #endregion	
	
	#region SquidWTF API Methods
	
    /// <summary>
    /// Gets track download information from hifi-api /track/ endpoint.
    /// Per hifi-api spec: GET /track/?id={trackId}&quality={quality}
    /// Returns: { "version": "2.0", "data": { trackId, assetPresentation, audioMode, audioQuality,
    ///   manifestMimeType, manifestHash, manifest (base64), albumReplayGain, trackReplayGain, bitDepth, sampleRate } }
    /// The manifest is base64-encoded JSON containing: { mimeType, codecs, encryptionType, urls: [downloadUrl] }
    /// Quality options: HI_RES_LOSSLESS (24-bit/192kHz FLAC), LOSSLESS (16-bit/44.1kHz FLAC), HIGH (320kbps AAC), LOW (96kbps AAC)
    /// </summary>
    private async Task<DownloadResult> GetTrackDownloadInfoAsync(string trackId, CancellationToken cancellationToken)
    {
        return await QueueRequestAsync(async () =>
        {
            Exception? lastException = null;
            var qualityOrder = BuildQualityFallbackOrder(_squidwtfSettings.Quality);

            foreach (var quality in qualityOrder)
            {
                try
                {
                    return await _fallbackHelper.TryWithFallbackAsync(baseUrl =>
                        FetchTrackDownloadInfoAsync(baseUrl, trackId, quality, cancellationToken));
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    if (!string.Equals(quality, qualityOrder[^1], StringComparison.Ordinal))
                    {
                        Logger.LogWarning(
                            "Track {TrackId} unavailable at SquidWTF quality {Quality}: {Error}. Trying lower quality",
                            trackId,
                            quality,
                            DescribeException(ex));
                        Logger.LogDebug(ex,
                            "Detailed SquidWTF quality failure for track {TrackId} at quality {Quality}",
                            trackId,
                            quality);
                    }
                }
            }

            throw lastException ?? new Exception($"Unable to fetch SquidWTF download info for track {trackId}");
        });
    }

    private async Task<DownloadResult> FetchTrackDownloadInfoAsync(
        string baseUrl,
        string trackId,
        string quality,
        CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}/track/?id={trackId}&quality={quality}";

        Logger.LogDebug("Fetching track download info from: {Url}", url);

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
