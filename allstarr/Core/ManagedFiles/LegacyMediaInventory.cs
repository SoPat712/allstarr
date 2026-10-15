using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using allstarr.Core.Configuration;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Core.ManagedFiles;

public sealed partial class LegacyMediaInventory(IConfiguration configuration, IServiceScopeFactory scopes,
    IDbContextFactory<AllstarrDbContext>? factory = null)
{
    public LegacyMediaPlan Preview(LegacyEnvDocument document, CancellationToken token = default)
    {
        var allowed = (configuration["Legacy:AllowedRoots"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath).ToArray();
        if (allowed.Length == 0) return LegacyMediaPlan.Empty;
        string? Value(string key) => document.Entries.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
        var download = configuration["Legacy:DownloadPath"];
        var kept = configuration["Legacy:KeptPath"];
        var candidates = new List<(string? Path, bool Kept)>
        {
            (Value("LIBRARY_KEPT_PATH"), true), (kept, true),
            (download == null ? null : Path.Combine(download, "kept"), true),
            (Value("LIBRARY_DOWNLOAD_PATH"), false), (download, false)
        };
        var found = new Dictionary<string, LegacyMediaFileSnapshot>(StringComparer.Ordinal);
        var notices = new HashSet<string>(StringComparer.Ordinal);
        using var scope = scopes.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<FilePlacementService>();
        var inspected = 0;
        foreach (var (candidate, isKept) in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (!Path.IsPathRooted(candidate)) { notices.Add("A relative legacy media path needs a mounted container path before it can be scanned."); continue; }
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (!allowed.Any(mount => root == mount || root.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal)))
            { notices.Add("A legacy media path is outside the approved mounts and was left untouched."); continue; }
            if (!Directory.Exists(root)) { notices.Add("A legacy media folder is missing. Mount it and preview again to include its files."); continue; }
            if (!isKept && !string.Equals(Value("STORAGE_MODE"), "Permanent", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).Equals("cache", StringComparison.OrdinalIgnoreCase))
            { notices.Add("Temporary download cache files are left behind; kept files are still included."); continue; }
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                if (++inspected > 50000) throw new LegacyEnvMigrationException("media_inventory_limit", "The legacy media inventory is too large. Select smaller roots and preview again.");
                if (directory.Depth > 32) { notices.Add("Deeply nested media folders need review and were skipped."); continue; }
                try
                {
                    for (var ancestor = new DirectoryInfo(directory.Path); ancestor != null; ancestor = ancestor.Parent)
                        if (ancestor.LinkTarget != null) throw new UnauthorizedAccessException();
                    foreach (var entry in new DirectoryInfo(directory.Path).EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal))
                    {
                        token.ThrowIfCancellationRequested();
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        { notices.Add("Symbolic links were skipped; original files were not followed or modified."); continue; }
                        if ((entry.Attributes & FileAttributes.Directory) != 0)
                        {
                            if (!isKept && entry.Name is "cache" or "transcoded") continue;
                            pending.Push((entry.FullName, directory.Depth + 1));
                            continue;
                        }
                        if (!AudioExtensions.Contains(Path.GetExtension(entry.Name))) continue;
                        if (found.ContainsKey(entry.FullName)) continue;
                        if (found.Count >= 10000) throw new LegacyEnvMigrationException("media_inventory_limit", "More than 10,000 legacy audio files need review. Select smaller roots and preview again.");
                        var info = (FileInfo)entry;
                        if (info.Length <= 0) { notices.Add("Empty audio files were skipped."); continue; }
                        var identity = files.InspectLegacyFile(root, info.FullName);
                        var stem = Path.GetFileNameWithoutExtension(info.Name);
                        var match = SourceSuffix().Match(stem);
                        string? provider = match.Success ? match.Groups["provider"].Value.ToLowerInvariant() : null;
                        var external = match.Success ? match.Groups["id"].Value : null;
                        provider = provider is "applemusic" or "apple-download" ? "apple-musickit" : provider;
                        var title = TrackPrefix().Replace(match.Success ? stem[..match.Index].Trim() : stem, "");
                        var parts = Path.GetRelativePath(root, info.FullName).Split(Path.DirectorySeparatorChar);
                        var metadata = new KeptRecordingMetadata(title.Length == 0 ? "Saved track" : title,
                            [parts.Length >= 3 ? parts[^3] : "Unknown artist"], parts.Length >= 2 ? parts[^2] : null);
                        var rootId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("legacy-media:" + root)).AsSpan(0, 16));
                        found.Add(info.FullName, new(rootId, root, info.FullName, info.Length, identity.DeviceId,
                            identity.FileId, info.LastWriteTimeUtc.Ticks, isKept, provider, external, metadata));
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { notices.Add("An unavailable or unsafe media entry was skipped. Check the mounted roots and preview again."); }
            }
        }
        if (factory != null && found.Count > 0)
        {
            using var db = factory.CreateDbContext();
            var paths = found.Keys.ToArray();
            foreach (var path in db.ManagedFiles.AsNoTracking().Where(item => paths.Contains(item.CanonicalPath) &&
                         item.PlacementMethod != ManagedFilePlacementMethod.Adopted).Select(item => item.CanonicalPath))
            {
                token.ThrowIfCancellationRequested();
                found.Remove(path);
            }
        }
        return new(found.Values.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray(), notices.Order(StringComparer.Ordinal).ToArray());
    }

    internal bool IsAllowed(string root)
    {
        var allowed = (configuration["Legacy:AllowedRoots"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Any(mount => root == Path.GetFullPath(mount) || root.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(mount)) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static readonly HashSet<string> AudioExtensions = new([".flac", ".mp3", ".m4a", ".mp4", ".aac", ".ogg", ".opus", ".wav", ".aiff"], StringComparer.OrdinalIgnoreCase);
    [GeneratedRegex(@"\s+\[(?<provider>deezer|qobuz|spotify|applemusic|apple-musickit|apple-download|squidwtf)-(?<id>[a-zA-Z0-9.\-]{1,100})\](?: \(\d+\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex SourceSuffix();
    [GeneratedRegex(@"^\d+\s*-\s*")]
    private static partial Regex TrackPrefix();
}
