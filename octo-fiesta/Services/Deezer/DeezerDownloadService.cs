using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using octo_fiesta.Models;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Common;
using Microsoft.Extensions.Options;
using IOFile = System.IO.File;

namespace octo_fiesta.Services.Deezer;

/// <summary>
/// C# port of the DeezerDownloader JavaScript
/// Handles Deezer authentication, track downloading and decryption
/// </summary>
public class DeezerDownloadService : BaseDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    
    private readonly string? _arl;
    private readonly string? _arlFallback;
    private readonly string? _preferredQuality;
    
    private string? _apiToken;
    private string? _licenseToken;
    
    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly int _minRequestIntervalMs = 200;
    
    private const string DeezerApiBase = "https://api.deezer.com";
    
    // Deezer's standard Blowfish CBC encryption key for track decryption
    // This is a well-known constant used by the Deezer API, not a user-specific secret
    private const string BfSecret = "g4el58wc0zvf9na1";

    protected override string ProviderName => "deezer";

    public DeezerDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<DeezerSettings> deezerSettings,
        ILogger<DeezerDownloadService> logger)
        : base(configuration, localLibraryService, metadataService, subsonicSettings.Value, logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        
        var deezer = deezerSettings.Value;
        _arl = deezer.Arl;
        _arlFallback = deezer.ArlFallback;
        _preferredQuality = deezer.Quality;
    }

    #region BaseDownloadService Implementation

    public override async Task<bool> IsAvailableAsync()
    {
        if (string.IsNullOrEmpty(_arl))
        {
            Logger.LogWarning("Deezer ARL not configured");
            return false;
        }

        try
        {
            await InitializeAsync();
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Deezer service not available");
            return false;
        }
    }

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
    {
        const string prefix = "ext-deezer-album-";
        if (albumId.StartsWith(prefix))
        {
            return albumId[prefix.Length..];
        }
        return null;
    }

    protected override async Task<string> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        var downloadInfo = await GetTrackDownloadInfoAsync(trackId, cancellationToken);
        
        Logger.LogInformation("Track token obtained for: {Title} - {Artist}", downloadInfo.Title, downloadInfo.Artist);
        Logger.LogInformation("Using format: {Format}", downloadInfo.Format);

        // Determine extension based on format
        var extension = downloadInfo.Format?.ToUpper() switch
        {
            "FLAC" => ".flac",
            _ => ".mp3"
        };

        // Build organized folder structure: Artist/Album/Track using AlbumArtist (fallback to Artist for singles)
        var artistForPath = song.AlbumArtist ?? song.Artist;
        var outputPath = PathHelper.BuildTrackPath(DownloadPath, artistForPath, song.Album, song.Title, song.Track, extension);
        
        // Create directories if they don't exist
        var albumFolder = Path.GetDirectoryName(outputPath)!;
        EnsureDirectoryExists(albumFolder);
        
        // Resolve unique path if file already exists
        outputPath = PathHelper.ResolveUniquePath(outputPath);

        // Download the encrypted file
        var response = await RetryWithBackoffAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadInfo.DownloadUrl);
            request.Headers.Add("User-Agent", "Mozilla/5.0");
            request.Headers.Add("Accept", "*/*");
            
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        });

        response.EnsureSuccessStatusCode();

        // Download and decrypt
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputFile = IOFile.Create(outputPath);
        
        await DecryptAndWriteStreamAsync(responseStream, outputFile, trackId, cancellationToken);
        
        // Close file before writing metadata
        await outputFile.DisposeAsync();
        
        // Write metadata and cover art
        await WriteMetadataAsync(outputPath, song, cancellationToken);

        return outputPath;
    }

    #endregion

    #region Deezer API Methods

    private async Task InitializeAsync(string? arlOverride = null)
    {
        var arl = arlOverride ?? _arl;
        if (string.IsNullOrEmpty(arl))
        {
            throw new Exception("ARL token required for Deezer downloads");
        }

        await RetryWithBackoffAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, 
                "https://www.deezer.com/ajax/gw-light.php?method=deezer.getUserData&input=3&api_version=1.0&api_token=null");
            
            request.Headers.Add("Cookie", $"arl={arl}");
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("results", out var results) &&
                results.TryGetProperty("checkForm", out var checkForm))
            {
                _apiToken = checkForm.GetString();
                
                if (results.TryGetProperty("USER", out var user) &&
                    user.TryGetProperty("OPTIONS", out var options) &&
                    options.TryGetProperty("license_token", out var licenseToken))
                {
                    _licenseToken = licenseToken.GetString();
                }
                
                Logger.LogInformation("Deezer token refreshed successfully");
                return true;
            }

            throw new Exception("Invalid ARL token");
        });
    }

    private async Task<DownloadResult> GetTrackDownloadInfoAsync(string trackId, CancellationToken cancellationToken)
    {
        var tryDownload = async (string arl) =>
        {
            // Refresh token with specific ARL
            await InitializeAsync(arl);

            return await QueueRequestAsync(async () =>
            {
                // Get track info
                var trackResponse = await _httpClient.GetAsync($"{DeezerApiBase}/track/{trackId}", cancellationToken);
                trackResponse.EnsureSuccessStatusCode();
                
                var trackJson = await trackResponse.Content.ReadAsStringAsync(cancellationToken);
                var trackDoc = JsonDocument.Parse(trackJson);
                
                if (!trackDoc.RootElement.TryGetProperty("track_token", out var trackTokenElement))
                {
                    throw new Exception("Track not found or track_token missing");
                }

                var trackToken = trackTokenElement.GetString();
                var title = trackDoc.RootElement.GetProperty("title").GetString() ?? "";
                var artist = trackDoc.RootElement.TryGetProperty("artist", out var artistEl) 
                    ? artistEl.GetProperty("name").GetString() ?? "" 
                    : "";

                // Get download URL via media API
                // Build format list based on preferred quality
                var formatsList = BuildFormatsList(_preferredQuality);
                
                var mediaRequest = new
                {
                    license_token = _licenseToken,
                    media = new[]
                    {
                        new
                        {
                            type = "FULL",
                            formats = formatsList
                        }
                    },
                    track_tokens = new[] { trackToken }
                };

                var mediaHttpRequest = new HttpRequestMessage(HttpMethod.Post, "https://media.deezer.com/v1/get_url");
                mediaHttpRequest.Content = new StringContent(
                    JsonSerializer.Serialize(mediaRequest), 
                    Encoding.UTF8, 
                    "application/json");

                using (mediaHttpRequest)
                {
                    var mediaResponse = await _httpClient.SendAsync(mediaHttpRequest, cancellationToken);
                    mediaResponse.EnsureSuccessStatusCode();

                    var mediaJson = await mediaResponse.Content.ReadAsStringAsync(cancellationToken);
                    var mediaDoc = JsonDocument.Parse(mediaJson);

                    if (!mediaDoc.RootElement.TryGetProperty("data", out var data) || 
                        data.GetArrayLength() == 0)
                    {
                        throw new Exception("No download URL available");
                    }

                    var firstData = data[0];
                    if (!firstData.TryGetProperty("media", out var media) || 
                        media.GetArrayLength() == 0)
                    {
                        throw new Exception("No media sources available - track may be unavailable in your region");
                    }

                    // Build a dictionary of available formats
                    var availableFormats = new Dictionary<string, string>();
                    foreach (var mediaItem in media.EnumerateArray())
                    {
                        if (mediaItem.TryGetProperty("format", out var formatEl) &&
                            mediaItem.TryGetProperty("sources", out var sources) && 
                            sources.GetArrayLength() > 0)
                        {
                            var fmt = formatEl.GetString();
                            var url = sources[0].GetProperty("url").GetString();
                            if (!string.IsNullOrEmpty(fmt) && !string.IsNullOrEmpty(url))
                            {
                                availableFormats[fmt] = url;
                            }
                        }
                    }

                    if (availableFormats.Count == 0)
                    {
                        throw new Exception("No download URL found in media sources - track may be region locked");
                    }

                    // Log available formats for debugging
                    Logger.LogInformation("Available formats from Deezer: {Formats}", string.Join(", ", availableFormats.Keys));

                    // Quality priority order (highest to lowest)
                    var qualityPriority = new[] { "FLAC", "MP3_320", "MP3_128" };
                    
                    string? selectedFormat = null;
                    string? downloadUrl = null;

                    // Select the best available quality from what Deezer returned
                    foreach (var quality in qualityPriority)
                    {
                        if (availableFormats.TryGetValue(quality, out var url))
                        {
                            selectedFormat = quality;
                            downloadUrl = url;
                            break;
                        }
                    }

                    if (string.IsNullOrEmpty(downloadUrl))
                    {
                        throw new Exception("No compatible format found in available media sources");
                    }

                    Logger.LogInformation("Selected quality: {Format}", selectedFormat);

                    return new DownloadResult
                    {
                        DownloadUrl = downloadUrl,
                        Format = selectedFormat ?? "MP3_128",
                        Title = title,
                        Artist = artist
                    };
                }
            });
        };

        try
        {
            return await tryDownload(_arl!);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(_arlFallback))
            {
                Logger.LogWarning(ex, "Primary ARL failed, trying fallback ARL...");
                return await tryDownload(_arlFallback);
            }
            throw;
        }
    }

    #endregion

    #region Decryption

    private byte[] GetBlowfishKey(string trackId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(trackId));
        var hashHex = Convert.ToHexString(hash).ToLower();
        
        var bfKey = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            bfKey[i] = (byte)(hashHex[i] ^ hashHex[i + 16] ^ BfSecret[i]);
        }
        
        return bfKey;
    }

    private async Task DecryptAndWriteStreamAsync(
        Stream input, 
        Stream output, 
        string trackId, 
        CancellationToken cancellationToken)
    {
        var bfKey = GetBlowfishKey(trackId);
        var iv = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        
        var buffer = new byte[2048];
        int chunkIndex = 0;
        
        while (true)
        {
            var bytesRead = await ReadExactAsync(input, buffer, cancellationToken);
            if (bytesRead == 0) break;

            var chunk = buffer.AsSpan(0, bytesRead).ToArray();

            // Every 3rd chunk (index % 3 == 0) is encrypted
            if (chunkIndex % 3 == 0 && bytesRead == 2048)
            {
                chunk = DecryptBlowfishCbc(chunk, bfKey, iv);
            }

            await output.WriteAsync(chunk, cancellationToken);
            chunkIndex++;
        }
    }

    private async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (bytesRead == 0) break;
            totalRead += bytesRead;
        }
        return totalRead;
    }

    private byte[] DecryptBlowfishCbc(byte[] data, byte[] key, byte[] iv)
    {
        // Use BouncyCastle for native Blowfish CBC decryption
        var engine = new BlowfishEngine();
        var cipher = new CbcBlockCipher(engine);
        cipher.Init(false, new ParametersWithIV(new KeyParameter(key), iv));
        
        var output = new byte[data.Length];
        var blockSize = cipher.GetBlockSize(); // 8 bytes for Blowfish
        
        for (int offset = 0; offset < data.Length; offset += blockSize)
        {
            cipher.ProcessBlock(data, offset, output, offset);
        }
        
        return output;
    }

    #endregion

    #region Utility Methods

    /// <summary>
    /// Builds the list of formats to request from Deezer based on preferred quality.
    /// </summary>
    private static object[] BuildFormatsList(string? preferredQuality)
    {
        var allFormats = new[]
        {
            new { cipher = "BF_CBC_STRIPE", format = "FLAC" },
            new { cipher = "BF_CBC_STRIPE", format = "MP3_320" },
            new { cipher = "BF_CBC_STRIPE", format = "MP3_128" }
        };

        if (string.IsNullOrEmpty(preferredQuality))
        {
            return allFormats;
        }

        var preferred = preferredQuality.ToUpperInvariant();
        
        return preferred switch
        {
            "FLAC" => allFormats,
            "MP3_320" => new object[]
            {
                new { cipher = "BF_CBC_STRIPE", format = "MP3_320" },
                new { cipher = "BF_CBC_STRIPE", format = "MP3_128" }
            },
            "MP3_128" => new object[]
            {
                new { cipher = "BF_CBC_STRIPE", format = "MP3_128" }
            },
            _ => allFormats
        };
    }

    private async Task<T> RetryWithBackoffAsync<T>(Func<Task<T>> action, int maxRetries = 3, int initialDelayMs = 1000)
    {
        Exception? lastException = null;
        
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                return await action();
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                                                   ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                lastException = ex;
                if (attempt < maxRetries - 1)
                {
                    var delay = initialDelayMs * (int)Math.Pow(2, attempt);
                    Logger.LogWarning("Retry attempt {Attempt}/{MaxRetries} after {Delay}ms ({Message})", 
                        attempt + 1, maxRetries, delay, ex.Message);
                    await Task.Delay(delay);
                }
            }
            catch
            {
                throw;
            }
        }

        throw lastException!;
    }

    private async Task RetryWithBackoffAsync(Func<Task<bool>> action, int maxRetries = 3, int initialDelayMs = 1000)
    {
        await RetryWithBackoffAsync<bool>(action, maxRetries, initialDelayMs);
    }

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
        public string Format { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
    }
}

/// <summary>
/// Helper class for path building and sanitization.
/// Extracted for testability.
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// Builds the output path for a downloaded track following the Artist/Album/Track structure.
    /// </summary>
    public static string BuildTrackPath(string downloadPath, string artist, string album, string title, int? trackNumber, string extension)
    {
        var safeArtist = SanitizeFolderName(artist);
        var safeAlbum = SanitizeFolderName(album);
        var safeTitle = SanitizeFileName(title);
        
        var artistFolder = Path.Combine(downloadPath, safeArtist);
        var albumFolder = Path.Combine(artistFolder, safeAlbum);
        
        var trackPrefix = trackNumber.HasValue ? $"{trackNumber:D2} - " : "";
        var fileName = $"{trackPrefix}{safeTitle}{extension}";
        
        return Path.Combine(albumFolder, fileName);
    }

    /// <summary>
    /// Sanitizes a file name by removing invalid characters.
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "Unknown";
        }
        
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName
            .Select(c => invalidChars.Contains(c) ? '_' : c)
            .ToArray());
        
        if (sanitized.Length > 100)
        {
            sanitized = sanitized[..100];
        }
        
        return sanitized.Trim();
    }

    /// <summary>
    /// Sanitizes a folder name by removing invalid path characters.
    /// </summary>
    public static string SanitizeFolderName(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return "Unknown";
        }
        
        var invalidChars = Path.GetInvalidFileNameChars()
            .Concat(Path.GetInvalidPathChars())
            .Distinct()
            .ToArray();
            
        var sanitized = new string(folderName
            .Select(c => invalidChars.Contains(c) ? '_' : c)
            .ToArray());
        
        // Remove leading/trailing dots and spaces (Windows folder restrictions)
        sanitized = sanitized.Trim().TrimEnd('.');
        
        if (sanitized.Length > 100)
        {
            sanitized = sanitized[..100].TrimEnd('.');
        }
        
        // Ensure we have a valid name
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "Unknown";
        }
        
        return sanitized;
    }

    /// <summary>
    /// Resolves a unique file path by appending a counter if the file already exists.
    /// </summary>
    public static string ResolveUniquePath(string basePath)
    {
        if (!IOFile.Exists(basePath))
        {
            return basePath;
        }
        
        var directory = Path.GetDirectoryName(basePath)!;
        var extension = Path.GetExtension(basePath);
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(basePath);
        
        var counter = 1;
        string uniquePath;
        do
        {
            uniquePath = Path.Combine(directory, $"{fileNameWithoutExt} ({counter}){extension}");
            counter++;
        } while (IOFile.Exists(uniquePath));
        
        return uniquePath;
    }
}
