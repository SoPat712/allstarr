using System.Text.RegularExpressions;
using IOFile = System.IO.File;

namespace allstarr.Services.Common;

public static class PathHelper
{
    public static string GetCachePath() => Path.Combine(Path.GetTempPath(), "allstarr-cache");

    public static string BuildTrackPath(
        string downloadPath,
        string artist,
        string album,
        string title,
        int? trackNumber,
        string extension,
        string? provider = null,
        string? externalId = null)
    {
        var prefix = trackNumber is { } number ? $"{number:D2} - " : string.Empty;
        var suffix = string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(externalId)
            ? string.Empty
            : $" [{SanitizeFileName(provider)}-{SanitizeFileName(externalId)}]";
        return Path.Combine(
            downloadPath,
            SanitizeFolderName(artist),
            SanitizeFolderName(album),
            $"{prefix}{SanitizeFileName(title)}{suffix}{extension}");
    }

    public static string SanitizeFileName(string fileName) => SanitizePathSegment(fileName);

    public static string SanitizeFolderName(string folderName) => SanitizePathSegment(folderName);

    public static string ResolveUniquePath(string basePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(basePath);
        if (!IOFile.Exists(basePath)) return basePath;

        var directory = Path.GetDirectoryName(basePath);
        if (string.IsNullOrEmpty(directory)) directory = Directory.GetCurrentDirectory();
        var extension = Path.GetExtension(basePath);
        var fileName = Path.GetFileNameWithoutExtension(basePath);
        for (var counter = 1; counter < 10_000; counter++)
        {
            var candidate = Path.Combine(directory, $"{fileName} ({counter}){extension}");
            if (!IOFile.Exists(candidate)) return candidate;
        }

        throw new IOException("Unable to determine a unique file path after 9,999 attempts.");
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";

        var invalid = Path.GetInvalidFileNameChars()
            .Concat(Path.GetInvalidPathChars())
            .Append('/')
            .Append('\\')
            .ToHashSet();
        var sanitized = new string(value.Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray());
        sanitized = Regex.Replace(sanitized.Trim().Trim('.'), "\\.{2,}", "_");
        if (sanitized.Length > 100) sanitized = sanitized[..100].TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }
}
