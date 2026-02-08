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
			Console.WriteLine($"Response code from is available async: {response.IsSuccessStatusCode}");
            return response.IsSuccessStatusCode;
        });
	}

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
    {
        const string prefix = "ext-squidwtf-album-";
        if (albumId.StartsWith(prefix))
        {
			Console.WriteLine(albumId[prefix.Length..]);
            return albumId[prefix.Length..];
        }
        return null;
    }

    protected override async Task<string> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        var downloadInfo = await GetTrackDownloadInfoAsync(trackId, cancellationToken);
        
        Logger.LogInformation("Track download URL obtained from hifi-api: {Url}", downloadInfo.DownloadUrl);
        Logger.LogInformation("Using format: {Format} (Quality: {Quality})", downloadInfo.MimeType, downloadInfo.AudioQuality);

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
        var outputPath = PathHelper.BuildTrackPath(basePath, artistForPath, song.Album, song.Title, song.Track, extension);
        
        // Create directories if they don't exist
        var albumFolder = Path.GetDirectoryName(outputPath)!;
        EnsureDirectoryExists(albumFolder);
        
        // Resolve unique path if file already exists
        outputPath = PathHelper.ResolveUniquePath(outputPath);

        // Use round-robin with fallback for downloads to reduce CPU usage
        Logger.LogDebug("Using round-robin endpoint selection for download");
        
        var response = await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
        {
            // Map quality settings to Tidal's quality levels per hifi-api spec
            var quality = _squidwtfSettings.Quality?.ToUpperInvariant() switch
            {
                "FLAC" => "LOSSLESS",
                "HI_RES" => "HI_RES_LOSSLESS",
                "LOSSLESS" => "LOSSLESS",
                "HIGH" => "HIGH",
                "LOW" => "LOW",
                _ => "LOSSLESS"
            };
            
            var url = $"{baseUrl}/track/?id={trackId}&quality={quality}";
            
            // Get download info from this endpoint
            var infoResponse = await _httpClient.GetAsync(url, cancellationToken);
            infoResponse.EnsureSuccessStatusCode();
            
            var json = await infoResponse.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);
            
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                throw new Exception("Invalid response from API");
            }
            
            var manifestBase64 = data.GetProperty("manifest").GetString()
                ?? throw new Exception("No manifest in response");
            
            // Decode base64 manifest to get actual CDN URL
            var manifestJson = Encoding.UTF8.GetString(Convert.FromBase64String(manifestBase64));
            var manifest = JsonDocument.Parse(manifestJson);
            
            if (!manifest.RootElement.TryGetProperty("urls", out var urls) || urls.GetArrayLength() == 0)
            {
                throw new Exception("No download URLs in manifest");
            }
            
            var downloadUrl = urls[0].GetString()
                ?? throw new Exception("Download URL is null");
            
            // Start the actual download from Tidal CDN (no encryption - squid.wtf handles everything)
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            request.Headers.Add("User-Agent", "Mozilla/5.0");
            request.Headers.Add("Accept", "*/*");
            
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        });

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
            // Use round-robin with fallback instead of racing to reduce CPU usage
            return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
            {
                // Map quality settings to Tidal's quality levels per hifi-api spec
                var quality = _squidwtfSettings.Quality?.ToUpperInvariant() switch
                {
                    "FLAC" => "LOSSLESS",
                    "HI_RES" => "HI_RES_LOSSLESS",
                    "LOSSLESS" => "LOSSLESS",
                    "HIGH" => "HIGH",
                    "LOW" => "LOW",
                    _ => "LOSSLESS" // Default to lossless
                };
                
                var url = $"{baseUrl}/track/?id={trackId}&quality={quality}";

                Logger.LogDebug("Fetching track download info from: {Url}", url);

                var response = await _httpClient.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonDocument.Parse(json);
                
                if (!doc.RootElement.TryGetProperty("data", out var data))
                {
                    throw new Exception("Invalid response from API");
                }
                
                // Get the manifest (base64 encoded JSON containing the actual CDN URL)
                var manifestBase64 = data.GetProperty("manifest").GetString()
                    ?? throw new Exception("No manifest in response");
                
                // Decode the manifest
                var manifestJson = Encoding.UTF8.GetString(Convert.FromBase64String(manifestBase64));
                var manifest = JsonDocument.Parse(manifestJson);
                
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
                    : "LOSSLESS";
                
                Logger.LogInformation("Track download URL obtained from hifi-api: {Url}", downloadUrl);
                
                return new DownloadResult
                {
                    DownloadUrl = downloadUrl,
                    MimeType = mimeType ?? "audio/flac",
                    AudioQuality = audioQuality ?? "LOSSLESS"
                };
            });
        });
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
        public string DownloadUrl { get; set; } = string.Empty;
        public string MimeType { get; set; } = string.Empty;
        public string AudioQuality { get; set; } = string.Empty;
    }
}	