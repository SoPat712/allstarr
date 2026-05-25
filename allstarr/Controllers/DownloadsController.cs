using Microsoft.AspNetCore.Mvc;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Lyrics;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public class DownloadsController : ControllerBase
{
    private static readonly string[] AudioExtensions = [".flac", ".mp3", ".m4a", ".opus"];

    private readonly ILogger<DownloadsController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IKeptLyricsSidecarService? _keptLyricsSidecarService;

    public DownloadsController(
        ILogger<DownloadsController> logger,
        IConfiguration configuration,
        IKeptLyricsSidecarService? keptLyricsSidecarService = null)
    {
        _logger = logger;
        _configuration = configuration;
        _keptLyricsSidecarService = keptLyricsSidecarService;
    }

    [HttpGet("downloads")]
    public IActionResult GetDownloads()
    {
        try
        {
            var keptPath = Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept");

            if (!Directory.Exists(keptPath))
            {
                return Ok(new { files = new List<object>(), totalSize = 0, count = 0 });
            }

            var files = new List<object>();
            long totalSize = 0;

            // Recursively get all audio files from kept folder
            var allFiles = Directory.GetFiles(keptPath, "*.*", SearchOption.AllDirectories)
                .Where(IsSupportedAudioFile)
                .ToList();

            foreach (var filePath in allFiles)
            {

                var fileInfo = new FileInfo(filePath);
                var relativePath = Path.GetRelativePath(keptPath, filePath);

                // Parse artist/album/track from path structure
                var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var artist = parts.Length > 0 ? parts[0] : "";
                var album = parts.Length > 1 ? parts[1] : "";
                var fileName = parts.Length > 2 ? parts[^1] : Path.GetFileName(filePath);

                files.Add(new
                {
                    path = relativePath,
                    fullPath = filePath,
                    artist,
                    album,
                    fileName,
                    size = fileInfo.Length,
                    sizeFormatted = AdminHelperService.FormatFileSize(fileInfo.Length),
                    lastModified = fileInfo.LastWriteTimeUtc,
                    extension = fileInfo.Extension
                });

                totalSize += fileInfo.Length;
            }

            return Ok(new
            {
                files = files.OrderBy(f => ((dynamic)f).artist).ThenBy(f => ((dynamic)f).album).ThenBy(f => ((dynamic)f).fileName),
                totalSize,
                totalSizeFormatted = AdminHelperService.FormatFileSize(totalSize),
                count = files.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list kept downloads");
            return StatusCode(500, new { error = "Failed to list kept downloads" });
        }
    }

    /// <summary>
    /// DELETE /api/admin/downloads
    /// Deletes a specific kept file and cleans up empty folders
    /// </summary>
    [HttpDelete("downloads")]
    public IActionResult DeleteDownload([FromQuery] string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                return BadRequest(new { error = "Path is required" });
            }

            var keptPath = Path.GetFullPath(Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept"));

            if (!TryResolvePathUnderRoot(keptPath, path, out var fullPath))
            {
                return BadRequest(new { error = "Invalid path" });
            }

            if (!System.IO.File.Exists(fullPath))
            {
                return NotFound(new { error = "File not found" });
            }

            System.IO.File.Delete(fullPath);
            var sidecarPath = _keptLyricsSidecarService?.GetSidecarPath(fullPath) ?? Path.ChangeExtension(fullPath, ".lrc");
            if (System.IO.File.Exists(sidecarPath))
            {
                System.IO.File.Delete(sidecarPath);
            }

            // Clean up empty directories (Album folder, then Artist folder if empty)
            var directory = Path.GetDirectoryName(fullPath);
            while (directory != null &&
                   !string.Equals(directory, keptPath, GetPathComparison()) &&
                   IsPathUnderRoot(directory, keptPath))
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                    directory = Path.GetDirectoryName(directory);
                }
                else
                {
                    break;
                }
            }

            return Ok(new { success = true, message = "File deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete file: {Path}", path);
            return StatusCode(500, new { error = "Failed to delete file" });
        }
    }

    /// <summary>
    /// DELETE /api/admin/downloads/all
    /// Deletes all kept audio files and removes empty folders
    /// </summary>
    [HttpDelete("downloads/all")]
    public IActionResult DeleteAllDownloads()
    {
        try
        {
            var keptPath = Path.GetFullPath(Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept"));
            if (!Directory.Exists(keptPath))
            {
                return Ok(new { success = true, deletedCount = 0, message = "No kept downloads found" });
            }

            var allFiles = Directory.GetFiles(keptPath, "*.*", SearchOption.AllDirectories)
                .Where(IsSupportedAudioFile)
                .ToList();

            foreach (var filePath in allFiles)
            {
                System.IO.File.Delete(filePath);
            }

            var sidecarFiles = Directory.GetFiles(keptPath, "*.lrc", SearchOption.AllDirectories);
            foreach (var sidecarFile in sidecarFiles)
            {
                System.IO.File.Delete(sidecarFile);
            }

            // Clean up empty directories under kept root (deepest first)
            var allDirectories = Directory.GetDirectories(keptPath, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length);
            foreach (var directory in allDirectories)
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }

            return Ok(new
            {
                success = true,
                deletedCount = allFiles.Count,
                message = $"Deleted {allFiles.Count} kept download(s)"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete all kept downloads");
            return StatusCode(500, new { error = "Failed to delete all kept downloads" });
        }
    }

    /// <summary>
    /// GET /api/admin/downloads/file
    /// Downloads a specific file from the kept folder
    /// </summary>
    [HttpGet("downloads/file")]
    public async Task<IActionResult> DownloadFile([FromQuery] string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                return BadRequest(new { error = "Path is required" });
            }

            var keptPath = Path.GetFullPath(Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept"));

            if (!TryResolvePathUnderRoot(keptPath, path, out var fullPath))
            {
                return BadRequest(new { error = "Invalid path" });
            }

            if (!System.IO.File.Exists(fullPath))
            {
                return NotFound(new { error = "File not found" });
            }

            var fileName = Path.GetFileName(fullPath);
            if (IsSupportedAudioFile(fullPath))
            {
                var sidecarPath = await EnsureLyricsSidecarIfPossibleAsync(fullPath, HttpContext.RequestAborted);
                if (System.IO.File.Exists(sidecarPath))
                {
                    return await CreateSingleTrackArchiveAsync(fullPath, sidecarPath, fileName);
                }
            }

            var fileStream = System.IO.File.OpenRead(fullPath);
            return File(fileStream, "application/octet-stream", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download file: {Path}", path);
            return StatusCode(500, new { error = "Failed to download file" });
        }
    }

    /// <summary>
    /// GET /api/admin/downloads/all
    /// Downloads all kept files as a zip archive
    /// </summary>
    [HttpGet("downloads/all")]
    public async Task<IActionResult> DownloadAllFiles()
    {
        try
        {
            var keptPath = Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept");

            if (!Directory.Exists(keptPath))
            {
                return NotFound(new { error = "No kept files found" });
            }

            var allFiles = Directory.GetFiles(keptPath, "*.*", SearchOption.AllDirectories)
                .Where(IsSupportedAudioFile)
                .ToList();

            if (allFiles.Count == 0)
            {
                return NotFound(new { error = "No audio files found in kept folder" });
            }

            _logger.LogInformation("📦 Creating zip archive with {Count} files", allFiles.Count);

            // Create zip in memory
            var memoryStream = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(memoryStream, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                var addedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var filePath in allFiles)
                {
                    var relativePath = Path.GetRelativePath(keptPath, filePath);
                    await AddFileToArchiveAsync(archive, filePath, relativePath, addedEntries);

                    var sidecarPath = await EnsureLyricsSidecarIfPossibleAsync(filePath, HttpContext.RequestAborted);
                    if (System.IO.File.Exists(sidecarPath))
                    {
                        var sidecarRelativePath = Path.GetRelativePath(keptPath, sidecarPath);
                        await AddFileToArchiveAsync(archive, sidecarPath, sidecarRelativePath, addedEntries);
                    }
                }
            }

            memoryStream.Position = 0;
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return File(memoryStream, "application/zip", $"allstarr_kept_{timestamp}.zip");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create zip archive");
            return StatusCode(500, new { error = "Failed to create zip archive" });
        }
    }

    private static bool TryResolvePathUnderRoot(string rootPath, string requestedPath, out string resolvedPath)
    {
        resolvedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return false;
        }

        try
        {
            var normalizedRoot = Path.GetFullPath(rootPath);
            var normalizedRootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;

            var candidatePath = Path.GetFullPath(Path.Combine(normalizedRoot, requestedPath));
            if (!candidatePath.StartsWith(normalizedRootWithSeparator, GetPathComparison()))
            {
                return false;
            }

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
        var normalizedCandidate = Path.GetFullPath(candidatePath);

        return normalizedCandidate.StartsWith(normalizedRootWithSeparator, GetPathComparison());
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private async Task<string> EnsureLyricsSidecarIfPossibleAsync(string audioFilePath, CancellationToken cancellationToken)
    {
        var sidecarPath = _keptLyricsSidecarService?.GetSidecarPath(audioFilePath) ?? Path.ChangeExtension(audioFilePath, ".lrc");
        if (System.IO.File.Exists(sidecarPath) || _keptLyricsSidecarService == null)
        {
            return sidecarPath;
        }

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
        var downloadName = $"{Path.GetFileNameWithoutExtension(fileName)}.zip";
        return File(archiveStream, "application/zip", downloadName);
    }

    private static async Task AddFileToArchiveAsync(
        System.IO.Compression.ZipArchive archive,
        string filePath,
        string entryPath,
        HashSet<string>? addedEntries)
    {
        if (addedEntries != null && !addedEntries.Add(entryPath))
        {
            return;
        }

        var entry = archive.CreateEntry(entryPath, System.IO.Compression.CompressionLevel.NoCompression);
        await using var entryStream = entry.Open();
        await using var fileStream = System.IO.File.OpenRead(filePath);
        await fileStream.CopyToAsync(entryStream);
    }

    private static bool IsSupportedAudioFile(string path)
    {
        return AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    }

    /// <summary>
    /// Gets all Spotify track mappings (paginated)
    /// </summary>
}
