using Microsoft.AspNetCore.Mvc;
using allstarr.Filters;
using allstarr.Services.Admin;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public class DownloadsController : ControllerBase
{
    private readonly ILogger<DownloadsController> _logger;
    private readonly IConfiguration _configuration;

    public DownloadsController(
        ILogger<DownloadsController> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
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
            var audioExtensions = new[] { ".flac", ".mp3", ".m4a", ".opus" };

            var allFiles = Directory.GetFiles(keptPath, "*.*", SearchOption.AllDirectories)
                .Where(f => audioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
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

            var audioExtensions = new[] { ".flac", ".mp3", ".m4a", ".opus" };
            var allFiles = Directory.GetFiles(keptPath, "*.*", SearchOption.AllDirectories)
                .Where(f => audioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            foreach (var filePath in allFiles)
            {
                System.IO.File.Delete(filePath);
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
    public IActionResult DownloadFile([FromQuery] string path)
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
    public IActionResult DownloadAllFiles()
    {
        try
        {
            var keptPath = Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept");

            if (!Directory.Exists(keptPath))
            {
                return NotFound(new { error = "No kept files found" });
            }

            var audioExtensions = new[] { ".flac", ".mp3", ".m4a", ".opus" };
            var allFiles = Directory.GetFiles(keptPath, "*.*", SearchOption.AllDirectories)
                .Where(f => audioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
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
                foreach (var filePath in allFiles)
                {
                    var relativePath = Path.GetRelativePath(keptPath, filePath);
                    var entry = archive.CreateEntry(relativePath, System.IO.Compression.CompressionLevel.NoCompression);

                    using var entryStream = entry.Open();
                    using var fileStream = System.IO.File.OpenRead(filePath);
                    fileStream.CopyTo(entryStream);
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

    /// <summary>
    /// Gets all Spotify track mappings (paginated)
    /// </summary>
}
