using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Lyrics;
using Microsoft.AspNetCore.Mvc;
using TagLib;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public class DownloadsController : ControllerBase
{
    private static readonly string[] AudioExtensions = [".flac", ".mp3", ".m4a", ".aac", ".opus", ".ogg"];
    private static readonly Regex ProviderSuffix = new(
        @"\s+\[(?<reference>[^\]]+)\]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<DownloadsController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IKeptLyricsSidecarService? _keptLyricsSidecarService;
    private readonly IProviderRegistry? _providerRegistry;

    public DownloadsController(
        ILogger<DownloadsController> logger,
        IConfiguration configuration,
        IKeptLyricsSidecarService? keptLyricsSidecarService = null,
        IProviderRegistry? providerRegistry = null)
    {
        _logger = logger;
        _configuration = configuration;
        _keptLyricsSidecarService = keptLyricsSidecarService;
        _providerRegistry = providerRegistry;
    }

    [HttpGet("downloads")]
    public IActionResult GetDownloads([FromQuery] string storage = "kept")
    {
        try
        {
            var roots = ResolveListRoots(storage);
            var qualifyPath = NormalizeStorage(storage) == "cache";
            var files = roots.SelectMany(root => EnumerateFiles(root.Path)
                    .Select(path => Describe(path, root, qualifyPath)))
                .OrderBy(item => item.Artist)
                .ThenBy(item => item.Album)
                .ThenBy(item => item.Title)
                .ToArray();
            var totalSize = files.Sum(item => item.Size);
            return Ok(new
            {
                storage = NormalizeStorage(storage),
                files,
                totalSize,
                totalSizeFormatted = AdminHelperService.FormatFileSize(totalSize),
                count = files.Length
            });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to list {Storage} downloads", storage);
            return StatusCode(500, new { error = "Failed to list downloads" });
        }
    }

    [HttpDelete("downloads")]
    public IActionResult DeleteDownload([FromQuery] string path, [FromQuery] string storage = "kept")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !IsSafeRequestedPath(storage, path))
                return BadRequest(new { error = string.IsNullOrWhiteSpace(path) ? "Path is required" : "Invalid path" });
            if (!TryResolveExistingFile(storage, path, out var root, out var fullPath))
                return NotFound(new { error = "File not found" });

            System.IO.File.Delete(fullPath);
            DeleteSidecar(fullPath);
            CleanEmptyDirectories(Path.GetDirectoryName(fullPath), root.Path);
            return Ok(new { success = true, message = "File deleted successfully" });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to delete {Storage} file: {Path}", storage, path);
            return StatusCode(500, new { error = "Failed to delete file" });
        }
    }

    [HttpDelete("downloads/all")]
    public IActionResult DeleteAllDownloads([FromQuery] string storage = "kept")
    {
        try
        {
            var roots = ResolveListRoots(storage);
            var deleted = 0;
            foreach (var root in roots)
            {
                if (!Directory.Exists(root.Path)) continue;
                foreach (var file in EnumerateFiles(root.Path))
                {
                    System.IO.File.Delete(file);
                    DeleteSidecar(file);
                    deleted++;
                }
                foreach (var sidecar in Directory.GetFiles(root.Path, "*.lrc", SearchOption.AllDirectories))
                    System.IO.File.Delete(sidecar);
                CleanAllEmptyDirectories(root.Path);
            }
            return Ok(new { success = true, deletedCount = deleted, message = $"Deleted {deleted} download(s)" });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to delete all {Storage} downloads", storage);
            return StatusCode(500, new { error = "Failed to delete downloads" });
        }
    }

    [HttpPost("downloads/promote")]
    public IActionResult PromoteCachedDownload([FromQuery] string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !IsSafeRequestedPath("cache", path))
                return BadRequest(new { error = string.IsNullOrWhiteSpace(path) ? "Path is required" : "Invalid path" });
            if (!TryResolveExistingFile("cache", path, out var cacheRoot, out var sourcePath))
                return NotFound(new { error = "Cached file not found" });

            var permanentRoot = Root("permanent");
            var targetPath = Path.GetFullPath(Path.Combine(permanentRoot.Path, Path.GetRelativePath(cacheRoot.Path, sourcePath)));
            if (!IsPathUnderRoot(targetPath, permanentRoot.Path))
                return BadRequest(new { error = "Invalid path" });
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            targetPath = ResolveUniquePath(targetPath);
            System.IO.File.Move(sourcePath, targetPath);

            var sourceSidecar = Path.ChangeExtension(sourcePath, ".lrc");
            if (System.IO.File.Exists(sourceSidecar))
            {
                var targetSidecar = Path.ChangeExtension(targetPath, ".lrc");
                System.IO.File.Move(sourceSidecar, targetSidecar, overwrite: false);
            }
            CleanEmptyDirectories(Path.GetDirectoryName(sourcePath), cacheRoot.Path);
            return Ok(new
            {
                success = true,
                storage = "permanent",
                path = Path.GetRelativePath(permanentRoot.Path, targetPath),
                message = "Cached track moved to Kept"
            });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to keep cached file: {Path}", path);
            return StatusCode(500, new { error = "Failed to keep cached file" });
        }
    }

    [HttpGet("downloads/file")]
    public async Task<IActionResult> DownloadFile([FromQuery] string path, [FromQuery] string storage = "kept")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !IsSafeRequestedPath(storage, path))
                return BadRequest(new { error = string.IsNullOrWhiteSpace(path) ? "Path is required" : "Invalid path" });
            if (!TryResolveExistingFile(storage, path, out _, out var fullPath))
                return NotFound(new { error = "File not found" });

            var fileName = Path.GetFileName(fullPath);
            if (IsSupportedAudioFile(fullPath))
            {
                var sidecarPath = await EnsureLyricsSidecarIfPossibleAsync(fullPath, HttpContext.RequestAborted);
                if (System.IO.File.Exists(sidecarPath))
                    return await CreateSingleTrackArchiveAsync(fullPath, sidecarPath, fileName);
            }
            return File(System.IO.File.OpenRead(fullPath), "application/octet-stream", fileName);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to download {Storage} file: {Path}", storage, path);
            return StatusCode(500, new { error = "Failed to download file" });
        }
    }

    [HttpGet("downloads/all")]
    public async Task<IActionResult> DownloadAllFiles([FromQuery] string storage = "kept")
    {
        try
        {
            var roots = ResolveListRoots(storage);
            var allFiles = roots.SelectMany(root => EnumerateFiles(root.Path).Select(path => (root, path))).ToArray();
            if (allFiles.Length == 0) return NotFound(new { error = "No audio files found" });

            var memoryStream = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(memoryStream, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                var addedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var includeRootPrefix = allFiles.Select(item => item.root.Key).Distinct().Count() > 1;
                foreach (var (root, filePath) in allFiles)
                {
                    var prefix = includeRootPrefix ? $"{root.Key}/" : string.Empty;
                    var relativePath = prefix + Path.GetRelativePath(root.Path, filePath).Replace('\\', '/');
                    await AddFileToArchiveAsync(archive, filePath, relativePath, addedEntries);
                    var sidecarPath = await EnsureLyricsSidecarIfPossibleAsync(filePath, HttpContext.RequestAborted);
                    if (System.IO.File.Exists(sidecarPath))
                    {
                        var sidecarRelativePath = prefix + Path.GetRelativePath(root.Path, sidecarPath).Replace('\\', '/');
                        await AddFileToArchiveAsync(archive, sidecarPath, sidecarRelativePath, addedEntries);
                    }
                }
            }
            memoryStream.Position = 0;
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return File(memoryStream, "application/zip", $"allstarr_{NormalizeStorage(storage)}_{timestamp}.zip");
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to create {Storage} download archive", storage);
            return StatusCode(500, new { error = "Failed to create download archive" });
        }
    }

    private IReadOnlyList<StorageRoot> ResolveListRoots(string storage) => NormalizeStorage(storage) switch
    {
        "cache" => [Root("cache"), Root("transcoded")],
        "kept" => [Root("permanent"), Root("legacy")],
        "permanent" => [Root("permanent")],
        "legacy" => [Root("legacy")],
        _ => throw new ArgumentException("Storage must be cache, kept, permanent, or legacy")
    };

    private StorageRoot Root(string key)
    {
        var basePath = _configuration["Library:DownloadPath"] ?? "./downloads";
        var directory = key switch
        {
            "cache" => "cache",
            "transcoded" => "transcoded",
            "permanent" => "permanent",
            "legacy" => "kept",
            _ => throw new ArgumentException("Unsupported storage root")
        };
        return new(key, Path.GetFullPath(Path.Combine(basePath, directory)));
    }

    private static string NormalizeStorage(string? storage) =>
        string.IsNullOrWhiteSpace(storage) ? "kept" : storage.Trim().ToLowerInvariant();

    private static IEnumerable<string> EnumerateFiles(string root) =>
        Directory.Exists(root)
            ? Directory.GetFiles(root, "*.*", SearchOption.AllDirectories).Where(IsSupportedAudioFile)
            : [];

    private ManagedDownloadFile Describe(string filePath, StorageRoot root, bool qualifyPath = false)
    {
        var info = new FileInfo(filePath);
        var relativePath = Path.GetRelativePath(root.Path, filePath);
        var requestedPath = qualifyPath
            ? $"{root.Key}/{relativePath.Replace('\\', '/')}"
            : relativePath;
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fallbackArtist = parts.Length > 0 ? parts[0] : string.Empty;
        var fallbackAlbum = parts.Length > 1 ? parts[1] : string.Empty;
        var fileName = Path.GetFileName(filePath);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var identity = ParseProviderIdentity(stem);
        var cleanStem = ProviderSuffix.Replace(stem, string.Empty);
        cleanStem = Regex.Replace(cleanStem, @"^\d+\s*-\s*", string.Empty);

        var title = cleanStem;
        var artist = fallbackArtist;
        var album = fallbackAlbum;
        int? bitrate = null;
        int? sampleRate = null;
        int? bitDepth = null;
        int? channels = null;
        long? durationMilliseconds = null;
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            title = string.IsNullOrWhiteSpace(tagFile.Tag.Title) ? title : tagFile.Tag.Title;
            artist = tagFile.Tag.Performers.FirstOrDefault() ?? artist;
            album = string.IsNullOrWhiteSpace(tagFile.Tag.Album) ? album : tagFile.Tag.Album;
            bitrate = Positive(tagFile.Properties.AudioBitrate);
            sampleRate = Positive(tagFile.Properties.AudioSampleRate);
            bitDepth = Positive(tagFile.Properties.BitsPerSample);
            channels = Positive(tagFile.Properties.AudioChannels);
            durationMilliseconds = tagFile.Properties.Duration > TimeSpan.Zero
                ? checked((long)Math.Round(tagFile.Properties.Duration.TotalMilliseconds))
                : null;
        }
        catch (Exception)
        {
            // Partially written or legacy files remain manageable with path-derived metadata.
        }

        var codec = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant() switch
        {
            "M4A" or "AAC" => "AAC",
            "OGG" => "Vorbis",
            var value => value
        };
        var quality = bitDepth.HasValue && sampleRate.HasValue
            ? $"{bitDepth}-bit / {FormatSampleRate(sampleRate.Value)}"
            : bitrate.HasValue
                ? $"{bitrate} kbps"
                : codec;

        return new(
            requestedPath,
            root.Key,
            artist,
            album,
            title,
            fileName,
            info.Length,
            AdminHelperService.FormatFileSize(info.Length),
            info.LastWriteTimeUtc,
            codec,
            bitrate,
            sampleRate,
            bitDepth,
            channels,
            durationMilliseconds,
            quality,
            identity.Provider,
            identity.ExternalId,
            identity.Provider == null || identity.ExternalId == null
                ? null
                : $"/api/admin/downloads/artwork/{Uri.EscapeDataString(
                    $"ext-{identity.Provider}-song-{identity.ExternalId}")}");
    }

    private (string? Provider, string? ExternalId) ParseProviderIdentity(string stem)
    {
        var match = ProviderSuffix.Match(stem);
        if (!match.Success) return (null, null);
        var reference = match.Groups["reference"].Value;
        var provider = _providerRegistry?.Providers
            .Select(item => item.Id)
            .OrderByDescending(item => item.Length)
            .FirstOrDefault(item => reference.StartsWith($"{item}-", StringComparison.OrdinalIgnoreCase));
        var separator = provider?.Length ?? reference.IndexOf('-');
        return separator > 0 && separator < reference.Length - 1
            ? (provider ?? reference[..separator].ToLowerInvariant(), reference[(separator + 1)..])
            : (null, null);
    }

    private static int? Positive(int value) => value > 0 ? value : null;

    private static string FormatSampleRate(int sampleRate) =>
        sampleRate % 1000 == 0 ? $"{sampleRate / 1000} kHz" : $"{sampleRate / 1000d:0.0} kHz";

    private bool TryResolveExistingFile(string storage, string requestedPath, out StorageRoot root, out string resolvedPath)
    {
        root = default!;
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(requestedPath)) return false;
        foreach (var candidateRoot in ResolveListRoots(storage))
        {
            if (!TryGetRelativeRequestPath(storage, candidateRoot, requestedPath, out var relativePath) ||
                !TryResolvePathUnderRoot(candidateRoot.Path, relativePath, out var candidatePath) ||
                !System.IO.File.Exists(candidatePath) ||
                !IsSupportedAudioFile(candidatePath)) continue;
            root = candidateRoot;
            resolvedPath = candidatePath;
            return true;
        }
        return false;
    }

    private bool IsSafeRequestedPath(string storage, string requestedPath) =>
        ResolveListRoots(storage).Any(root =>
            TryGetRelativeRequestPath(storage, root, requestedPath, out var relativePath) &&
            TryResolvePathUnderRoot(root.Path, relativePath, out _));

    private static bool TryGetRelativeRequestPath(
        string storage,
        StorageRoot root,
        string requestedPath,
        out string relativePath)
    {
        relativePath = requestedPath;
        if (!NormalizeStorage(storage).Equals("cache", StringComparison.Ordinal)) return true;

        var normalized = requestedPath.Replace('\\', '/');
        foreach (var prefix in new[] { "cache", "transcoded" })
        {
            if (!normalized.StartsWith($"{prefix}/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!root.Key.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            relativePath = normalized[(prefix.Length + 1)..];
            return relativePath.Length > 0;
        }
        return true;
    }

    private static bool TryResolvePathUnderRoot(string rootPath, string requestedPath, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        try
        {
            var normalizedRoot = Path.GetFullPath(rootPath);
            var candidatePath = Path.GetFullPath(Path.Combine(normalizedRoot, requestedPath));
            if (!IsPathUnderRoot(candidatePath, normalizedRoot)) return false;
            resolvedPath = candidatePath;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsPathUnderRoot(string candidatePath, string rootPath)
    {
        var normalizedRoot = Path.GetFullPath(rootPath);
        var normalizedRootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidatePath).StartsWith(normalizedRootWithSeparator, GetPathComparison());
    }

    private static string ResolveUniquePath(string path)
    {
        if (!System.IO.File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!System.IO.File.Exists(candidate)) return candidate;
        }
        throw new IOException("Unable to create a unique kept file name");
    }

    private void DeleteSidecar(string audioPath)
    {
        var sidecarPath = _keptLyricsSidecarService?.GetSidecarPath(audioPath) ?? Path.ChangeExtension(audioPath, ".lrc");
        if (System.IO.File.Exists(sidecarPath)) System.IO.File.Delete(sidecarPath);
    }

    private static void CleanEmptyDirectories(string? directory, string root)
    {
        while (directory != null &&
               !string.Equals(directory, root, GetPathComparison()) &&
               IsPathUnderRoot(directory, root))
        {
            if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any()) break;
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private static void CleanAllEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(item => item.Length))
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private async Task<string> EnsureLyricsSidecarIfPossibleAsync(string audioFilePath, CancellationToken cancellationToken)
    {
        var sidecarPath = _keptLyricsSidecarService?.GetSidecarPath(audioFilePath) ?? Path.ChangeExtension(audioFilePath, ".lrc");
        if (System.IO.File.Exists(sidecarPath) || _keptLyricsSidecarService == null) return sidecarPath;
        var generatedSidecar = await _keptLyricsSidecarService.EnsureSidecarAsync(audioFilePath, cancellationToken: cancellationToken);
        return generatedSidecar ?? sidecarPath;
    }

    private async Task<IActionResult> CreateSingleTrackArchiveAsync(string audioFilePath, string sidecarPath, string fileName)
    {
        var archiveStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(archiveStream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            await AddFileToArchiveAsync(archive, audioFilePath, Path.GetFileName(audioFilePath), null);
            await AddFileToArchiveAsync(archive, sidecarPath, Path.GetFileName(sidecarPath), null);
        }
        archiveStream.Position = 0;
        return File(archiveStream, "application/zip", $"{Path.GetFileNameWithoutExtension(fileName)}.zip");
    }

    private static async Task AddFileToArchiveAsync(
        System.IO.Compression.ZipArchive archive,
        string filePath,
        string entryPath,
        HashSet<string>? addedEntries)
    {
        if (addedEntries != null && !addedEntries.Add(entryPath)) return;
        var entry = archive.CreateEntry(entryPath, System.IO.Compression.CompressionLevel.NoCompression);
        await using var entryStream = entry.Open();
        await using var fileStream = System.IO.File.OpenRead(filePath);
        await fileStream.CopyToAsync(entryStream);
    }

    private static bool IsSupportedAudioFile(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private sealed record StorageRoot(string Key, string Path);

    private sealed record ManagedDownloadFile(
        string Path,
        string Storage,
        string Artist,
        string Album,
        string Title,
        string FileName,
        long Size,
        string SizeFormatted,
        DateTime LastModified,
        string Codec,
        int? BitrateKbps,
        int? SampleRateHz,
        int? BitDepth,
        int? Channels,
        long? DurationMilliseconds,
        string Quality,
        string? Provider,
        string? ExternalId,
        string? ArtworkUrl);
}
