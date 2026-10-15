using System.Text.RegularExpressions;

namespace allstarr.Core.Storage;

public static partial class ContainerStorageLayout
{
    public static void Configure(IConfiguration configuration, bool containerized, IReadOnlyList<string> mounts)
    {
        var configured = configuration["Storage:DataDirectory"];
        if (!containerized) return;
        var requested = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? "/app/data" : configured);
        var legacy = mounts.Contains("/app/kept") || mounts.Contains("/app/downloads");
        if (!IsPersistent(requested, mounts))
        {
            if (requested != "/app/data" || !IsPersistent("/app/cache", mounts))
                throw new InvalidOperationException("Mount a persistent local data folder at /app/data before starting Allstarr.");
            requested = "/app/cache/allstarr-v3";
            legacy = true;
        }
        for (var directory = new DirectoryInfo(requested); directory != null; directory = directory.Parent)
        {
            if (directory.LinkTarget != null)
                throw new InvalidOperationException("The persistent data folder cannot contain symbolic links.");
        }
        configuration["Storage:DataDirectory"] = requested;
        if (!legacy) return;
        configuration["Legacy:KeptPath"] = configuration["Library:KeptPath"] ?? "/app/kept";
        configuration["Legacy:DownloadPath"] = configuration["Library:DownloadPath"] ?? "/app/downloads";
        configuration["Library:KeptPath"] = Path.Combine(requested, "kept");
        configuration["Library:DownloadPath"] = Path.Combine(requested, "downloads");
        configuration["Cache:MediaDirectory"] = Path.Combine(requested, "cache", "media");
        configuration["Storage:LegacyLayout"] = "true";
    }

    public static IReadOnlyList<string> PersistentMounts(string mountInfo) => mountInfo.Split('\n')
        .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Where(fields => fields.Length >= 8 && fields[5].Split(',').Contains("rw") && Array.IndexOf(fields, "-") is var separator &&
                         separator >= 6 && separator + 1 < fields.Length &&
                         fields[separator + 1] is not ("overlay" or "tmpfs" or "ramfs" or "proc" or "sysfs"))
        .Select(fields => MountEscape().Replace(fields[4], match =>
            ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString()))
        .Where(path => path != "/")
        .Distinct(StringComparer.Ordinal).ToArray();

    private static bool IsPersistent(string path, IReadOnlyList<string> mounts) => mounts.Any(mount =>
        mount != "/" && (path == mount || path.StartsWith(Path.TrimEndingDirectorySeparator(mount) + "/", StringComparison.Ordinal)));

    [GeneratedRegex(@"\\([0-7]{3})")]
    private static partial Regex MountEscape();
}
