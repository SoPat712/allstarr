using System.Text;
using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Download;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Local;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;
using IOFile = System.IO.File;
using Microsoft.Extensions.Logging;

namespace allstarr.Services.SquidWTF;

/// <summary>
/// Handles track downloading from tidal.squid.wtf (no encryption, no auth required)
/// Downloads are direct from Tidal's CDN via the squid.wtf proxy
/// </summary>
public class SquidWTFDownloadService : BaseDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly SquidWTFSettings _squidwtfSettings;
	
    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly int _minRequestIntervalMs = 200;
    
	// Primary and backup endpoints (base64 encoded to avoid detection)
	private const string PrimaryEndpoint = "aHR0cHM6Ly90cml0b24uc3F1aWQud3RmLw=="; // triton.squid.wtf
	
	private static readonly string[] BackupEndpoints = new[]
	{
		"aHR0cHM6Ly93b2xmLnFxZGwuc2l0ZS8=",      // wolf
		"aHR0cHM6Ly9tYXVzLnFxZGwuc2l0ZS8=",      // maus
		"aHR0cHM6Ly92b2dlbC5xcWRsLnNpdGUv",      // vogel
		"aHR0cHM6Ly9rYXR6ZS5xcWRsLnNpdGUv",      // katze
		"aHR0cHM6Ly9odW5kLnFxZGwuc2l0ZS8="       // hund
	};
	
	private string _currentApiBase;
	private int _currentEndpointIndex = -1;

    protected override string ProviderName => "squidwtf";

    public SquidWTFDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<SquidWTFSettings> SquidWTFSettings,
		IServiceProvider serviceProvider,
        ILogger<SquidWTFDownloadService> logger)
        : base(configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _squidwtfSettings = SquidWTFSettings.Value;
		_currentApiBase = DecodeEndpoint(PrimaryEndpoint);
    }
	
	private string DecodeEndpoint(string base64)
	{
		var bytes = Convert.FromBase64String(base64);
		return Encoding.UTF8.GetString(bytes).TrimEnd('/');
	}
	
	private async Task<bool> TryNextEndpointAsync()
	{
		_currentEndpointIndex++;
		
		if (_currentEndpointIndex >= BackupEndpoints.Length)
		{
			Logger.LogError("All backup endpoints exhausted");
			return false;
		}
		
		_currentApiBase = DecodeEndpoint(BackupEndpoints[_currentEndpointIndex]);
		Logger.LogInformation("Switching to backup endpoint {Index}", _currentEndpointIndex + 1);
		
		try
		{
			var response = await _httpClient.GetAsync(_currentApiBase);
			if (response.IsSuccessStatusCode)
			{
				Logger.LogInformation("Backup endpoint {Index} is available", _currentEndpointIndex + 1);
				return true;
			}
		}
		catch (Exception ex)
		{
			Logger.LogWarning(ex, "Backup endpoint {Index} failed", _currentEndpointIndex + 1);
		}
		
		return await TryNextEndpointAsync();
	}

    #region BaseDownloadService Implementation

    public override async Task<bool> IsAvailableAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync(_currentApiBase);
			Console.WriteLine($"Response code from is available async: {response.IsSuccessStatusCode}");
            
			if (!response.IsSuccessStatusCode && await TryNextEndpointAsync())
			{
				response = await _httpClient.GetAsync(_currentApiBase);
			}
			
			return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "SquidWTF service not available, trying backup");
			
			if (await TryNextEndpointAsync())
			{
				return await IsAvailableAsync();
			}
			
            return false;
        }
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
        
		Logger.LogInformation("Track token obtained: {Url}", downloadInfo.DownloadUrl);
        Logger.LogInformation("Using format: {Format}", downloadInfo.MimeType);

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
        var outputPath = PathHelper.BuildTrackPath(DownloadPath, artistForPath, song.Album, song.Title, song.Track, extension);
        
        // Create directories if they don't exist
        var albumFolder = Path.GetDirectoryName(outputPath)!;
        EnsureDirectoryExists(albumFolder);
        
        // Resolve unique path if file already exists
        outputPath = PathHelper.ResolveUniquePath(outputPath);

        // Download from Tidal CDN (no authentication needed, token is in URL)
        var response = await QueueRequestAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadInfo.DownloadUrl);
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
        
        // Write metadata and cover art
        await WriteMetadataAsync(outputPath, song, cancellationToken);

        return outputPath;
    }

    #endregion	
	
	#region SquidWTF API Methods
	
	private async Task<DownloadResult> GetTrackDownloadInfoAsync(string trackId, CancellationToken cancellationToken)
    {
        return await QueueRequestAsync(async () =>
        {
            // Map quality settings to Tidal's quality levels
            var quality = _squidwtfSettings.Quality?.ToUpperInvariant() switch
            {
                "FLAC" => "LOSSLESS",
                "HI_RES" => "HI_RES_LOSSLESS",
                "LOSSLESS" => "LOSSLESS",
                "HIGH" => "HIGH",
                "LOW" => "LOW",
                _ => "LOSSLESS" // Default to lossless
            };
            
            var url = $"{_currentApiBase}track/?id={trackId}&quality={quality}";

            Console.WriteLine($"%%%%%%%%%%%%%%%%%%% URL For downloads??: {url}");

            try
			{
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
				
				Logger.LogDebug("Decoded manifest - URL: {Url}, MIME: {MimeType}, Quality: {Quality}", 
					downloadUrl, mimeType, audioQuality);
				
				return new DownloadResult
				{
					DownloadUrl = downloadUrl,
					MimeType = mimeType ?? "audio/flac",
					AudioQuality = audioQuality ?? "LOSSLESS"
				};
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to get track info, trying backup endpoint");
				
				if (await TryNextEndpointAsync())
				{
					return await GetTrackDownloadInfoAsync(trackId, cancellationToken);
				}
				
				throw;
			}
        });
    }
	
	#endregion
	
    #region Utility Methods

    private async Task<T> QueueRequestAsync<T>(Func<Task<T>> action)
    {
        await _requestLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            var timeSinceLastRequest = (now - _lastRequestTime).TotalMilliseconds;
            
            if (timeSinceLastRequest < _minRequestIntervalMs)
            {
                await Task.Delay((int)(_minRequestIntervalMs - timeSinceLastRequest));
            }

            _lastRequestTime = DateTime.UtcNow;
            return await action();
        }
        finally
        {
            _requestLock.Release();
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