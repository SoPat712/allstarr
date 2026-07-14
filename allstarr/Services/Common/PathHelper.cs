using IOFile = System.IO.File;

namespace allstarr.Services.Common;

/// <summary>
/// Helper class for path building and sanitization.
/// Provides utilities for creating safe file and folder paths for downloaded music files.
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// Gets the cache directory path for temporary file storage.
    /// Uses system temp directory combined with allstarr-cache subfolder.
    /// Respects TMPDIR environment variable on Linux/macOS.
    /// </summary>
    /// <returns>Full path to the cache directory.</returns>
    public static string GetCachePath()
    {
        return Path.Combine(Path.GetTempPath(), "allstarr-cache");
    }

    /// <summary>
    /// Builds the output path for a downloaded track following the Artist/Album/Track structure.
    /// </summary>
    /// <param name="downloadPath">Base download directory path.</param>
    /// <param name="artist">Artist name (will be sanitized).</param>
    /// <param name="album">Album name (will be sanitized).</param>
    /// <param name="title">Track title (will be sanitized).</param>
    /// <param name="trackNumber">Optional track number for prefix.</param>
    /// <param name="extension">File extension (e.g., ".flac", ".mp3").</param>
    /// <param name="provider">Optional provider name (e.g., "squidwtf", "deezer").</param>
    /// <param name="externalId">Optional external ID from the provider.</param>
    /// <returns>Full path for the track file.</returns>
    public static string BuildTrackPath(string downloadPath, string artist, string album, string title, int? trackNumber, string extension, string? provider = null, string? externalId = null)
    {
        var safeArtist = SanitizeFolderName(artist);
        var safeAlbum = SanitizeFolderName(album);
        var safeTitle = SanitizeFileName(title);

        var artistFolder = Path.Combine(downloadPath, safeArtist);
        var albumFolder = Path.Combine(artistFolder, safeAlbum);

        var trackPrefix = trackNumber.HasValue ? $"{trackNumber:D2} - " : "";
        // Sanitize provider and external id to avoid path traversal or invalid filename segments
        string? safeProvider = null;
        string? safeExternalId = null;
        if (!string.IsNullOrEmpty(provider))
        {
            safeProvider = SanitizeFileName(provider);
        }

        if (!string.IsNullOrEmpty(externalId))
        {
            safeExternalId = SanitizeFileName(externalId);
        }

        // If both provider and external id are present, append a sanitized id suffix
        var idSuffix = (!string.IsNullOrEmpty(safeProvider) && !string.IsNullOrEmpty(safeExternalId))
            ? $" [{safeProvider}-{safeExternalId}]"
            : "";
        var fileName = $"{trackPrefix}{safeTitle}{idSuffix}{extension}";

        return Path.Combine(albumFolder, fileName);
    }

    /// <summary>
    /// Sanitizes a file name by removing invalid characters.
    /// </summary>
    /// <param name="fileName">Original file name.</param>
    /// <returns>Sanitized file name safe for all file systems.</returns>
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

        // Collapse sequences of two or more dots to a single underscore to avoid
        // creating ".." which can be interpreted as parent directory tokens.
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, "\\.{2,}", "_");

        // Remove any remaining path separators just in case
        sanitized = sanitized.Replace('/', '_').Replace('\\', '_');

        // Trim whitespace and trailing/leading dots
        sanitized = sanitized.Trim().TrimEnd('.').TrimStart('.');

        if (sanitized.Length > 100)
        {
            sanitized = sanitized[..100].TrimEnd('.');
        }

        if (string.IsNullOrWhiteSpace(sanitized)) return "Unknown";

        return sanitized;
    }

    /// <summary>
    /// Sanitizes a folder name by removing invalid path characters.
    /// </summary>
    /// <param name="folderName">Original folder name.</param>
    /// <returns>Sanitized folder name safe for all file systems.</returns>
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
    /// <param name="basePath">Desired file path.</param>
    /// <returns>Unique file path that does not exist yet.</returns>
    public static string ResolveUniquePath(string basePath)
    {
        if (string.IsNullOrEmpty(basePath))
        {
            throw new ArgumentException("basePath must be provided", nameof(basePath));
        }

        if (!IOFile.Exists(basePath))
        {
            return basePath;
        }

        var directory = Path.GetDirectoryName(basePath);
        if (string.IsNullOrEmpty(directory))
        {
            // If no directory part is present, use current directory
            directory = Directory.GetCurrentDirectory();
        }
        var extension = Path.GetExtension(basePath);
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(basePath);

        var counter = 1;
        string uniquePath;
        // Limit attempts to avoid infinite loop in pathological cases
        const int maxAttempts = 10000;
        do
        {
            uniquePath = Path.Combine(directory, $"{fileNameWithoutExt} ({counter}){extension}");
            counter++;

            if (counter > maxAttempts)
            {
                throw new IOException("Unable to determine unique file path after many attempts");
            }
        } while (IOFile.Exists(uniquePath));

        return uniquePath;
    }
}
