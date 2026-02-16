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
            
            _logger.LogDebug("📂 Checking kept folder: {Path}", keptPath);
            _logger.LogInformation("📂 Directory exists: {Exists}", Directory.Exists(keptPath));
            
            if (!Directory.Exists(keptPath))
            {
                _logger.LogWarning("Kept folder does not exist: {Path}", keptPath);
                return Ok(new { files = new List<object>(), totalSize = 0, count = 0 });
            }
            
            var files = new List<object>();
            long totalSize = 0;
            
            // Recursively get all audio files from kept folder
            var audioExtensions = new[] { ".flac", ".mp3", ".m4a", ".opus" };
            
            var allFiles = Directory.GetFiles(keptPath, "*.*", SearchOption.AllDirectories)
                .Where(f => audioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();
            
            _logger.LogDebug("📂 Found {Count} audio files in kept folder", allFiles.Count);
            
            foreach (var filePath in allFiles)
            {
                _logger.LogDebug("📂 Processing file: {Path}", filePath);
                
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
            
            _logger.LogDebug("📂 Returning {Count} kept files, total size: {Size}", files.Count, AdminHelperService.FormatFileSize(totalSize));
            
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
            
            var keptPath = Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept");
            var fullPath = Path.Combine(keptPath, path);
            
            _logger.LogDebug("🗑️ Delete request for: {Path}", fullPath);
            
            // Security: Ensure the path is within the kept directory
            var normalizedFullPath = Path.GetFullPath(fullPath);
            var normalizedKeptPath = Path.GetFullPath(keptPath);
            
            if (!normalizedFullPath.StartsWith(normalizedKeptPath))
            {
                _logger.LogWarning("🗑️ Invalid path (outside kept folder): {Path}", normalizedFullPath);
                return BadRequest(new { error = "Invalid path" });
            }
            
            if (!System.IO.File.Exists(fullPath))
            {
                _logger.LogWarning("🗑️ File not found: {Path}", fullPath);
                return NotFound(new { error = "File not found" });
            }
            
            System.IO.File.Delete(fullPath);
            _logger.LogDebug("🗑️ Deleted file: {Path}", fullPath);
            
            // Clean up empty directories (Album folder, then Artist folder if empty)
            var directory = Path.GetDirectoryName(fullPath);
            while (directory != null && directory != keptPath && directory.StartsWith(keptPath))
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                    _logger.LogInformation("🗑️ Deleted empty directory: {Dir}", directory);
                    directory = Path.GetDirectoryName(directory);
                }
                else
                {
                    _logger.LogInformation("🗑️ Directory not empty or doesn't exist, stopping cleanup: {Dir}", directory);
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
            
            var keptPath = Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept");
            var fullPath = Path.Combine(keptPath, path);
            
            // Security: Ensure the path is within the kept directory
            var normalizedFullPath = Path.GetFullPath(fullPath);
            var normalizedKeptPath = Path.GetFullPath(keptPath);
            
            if (!normalizedFullPath.StartsWith(normalizedKeptPath))
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
    /// Gets all Spotify track mappings (paginated)
    /// </summary>
}
