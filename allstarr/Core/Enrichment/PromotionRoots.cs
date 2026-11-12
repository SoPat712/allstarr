using System.Text.Json;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed record PromotionRoot(Guid Id, string Name, string Path, string? BackendPath = null, string? BackendLibraryId = null);
public sealed record PromotionSetup(bool Ready, bool AllowedBaseConfigured, string? Reason, Guid? RootId);

public sealed class PromotionRoots(IDurableRuntimeSettings settings, IDbContextFactory<AllstarrDbContext> factory,
    IConfiguration configuration)
{
    private readonly Func<string, long?>? readFreeBytes;
    private readonly SemaphoreSlim gate = new(1, 1);
    public const string Key = "Promotion:Roots";

    internal PromotionRoots(IDurableRuntimeSettings settings, IDbContextFactory<AllstarrDbContext> factory,
        IConfiguration configuration, Func<string, long?> readFreeBytes) : this(settings, factory, configuration) =>
        this.readFreeBytes = readFreeBytes;

    public async Task<PromotionRoot[]> ReadAsync(CancellationToken token = default) =>
        JsonSerializer.Deserialize<PromotionRoot[]>((string)(await settings.GetAsync(Key, token)).Value) ?? [];

    public async Task SaveAsync(Guid actor, PromotionRoot[] roots, CancellationToken token = default,
        IReadOnlySet<string>? discoveredBackendPaths = null)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            if (!await db.Users.AnyAsync(item => item.Id == actor && item.Enabled && item.IsAdmin, token))
                throw new UnauthorizedAccessException("Administrator permissions required.");
            if (roots.Length > 16 || roots.Select(item => item.Id).Distinct().Count() != roots.Length)
                throw new ArgumentException("Choose at most sixteen distinct library roots.");
            var approved = await ReadAsync(token);
            foreach (var root in roots)
            {
                if ((!AllowedBaseConfigured || !string.IsNullOrWhiteSpace(root.BackendPath)) &&
                    (string.IsNullOrWhiteSpace(root.BackendPath) || discoveredBackendPaths?.Contains(root.BackendPath) != true) &&
                    !approved.Any(item => SameLocation(item, root)))
                    throw new ArgumentException("Choose a discovered music folder before approving its local path.");
                Validate(root);
            }
            var normalized = roots.Select(root => root with { Path = System.IO.Path.GetFullPath(root.Path) }).ToArray();
            await settings.ApplyBatchAsync([new(Key, JsonSerializer.Serialize(normalized))], "promotion", actor, token);
        }
        finally { gate.Release(); }
    }

    internal async Task<PromotionRoot[]> LinkDiscoveredAsync(PromotionRoot[] discovered, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var approved = (await ReadAsync(token)).ToList();
            var count = approved.Count;
            foreach (var root in discovered)
            {
                if (approved.Any(item => PromotionLibraryFolders.WithoutTrailingSeparator(item.BackendPath ?? item.Path) == root.BackendPath ||
                    item.Path == root.Path)) continue;
                Validate(root);
                approved.Add(root);
            }
            if (approved.Count != count)
                await settings.ApplyBatchAsync([new(Key, JsonSerializer.Serialize(approved))], "promotion", null, token);
            return approved.ToArray();
        }
        finally { gate.Release(); }
    }

    internal static bool SameLocation(PromotionRoot left, PromotionRoot right) =>
        left.Id == right.Id && left.Path == right.Path && left.BackendPath == right.BackendPath;

    public async Task<PromotionRoot> RequireAsync(Guid id, CancellationToken token, bool checkFreeSpace = true,
        string? backendLibraryId = null)
    {
        var approved = await ReadAsync(token);
        if (approved.Length == 0 || id == Guid.Empty && approved.Length != 1)
            throw new PromotionException(PromotionFailure.LibraryFolderNotSet);
        var root = (id == Guid.Empty ? approved.FirstOrDefault() : approved.SingleOrDefault(item => item.Id == id))
            ?? throw new PromotionException(PromotionFailure.LibraryFolderUnavailable);
        if (backendLibraryId != null && root.BackendLibraryId != backendLibraryId)
            throw new PromotionException(PromotionFailure.LibraryFolderMismatch);
        try { Validate(root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new PromotionException(PromotionFailure.LibraryFolderUnavailable); }
        if (checkFreeSpace) RequireFreeSpace(root.Path, 0);
        return root;
    }

    // Free space as df reports it: Allstarr runs as root and could otherwise fill the file system's reserved blocks.
    internal void RequireFreeSpace(string path, long length)
    {
        if (FreeBytes(path) is { } available && available - length < MinimumFreeBytes)
            throw new PromotionException(PromotionFailure.LibraryFolderFull);
    }

    internal long? FreeBytes(string path)
    {
        if (readFreeBytes != null) return readFreeBytes(path);
        var full = System.IO.Path.GetFullPath(path);
        var drive = DriveInfo.GetDrives().Where(item => Contains(item.RootDirectory.FullName, full))
            .OrderByDescending(item => item.RootDirectory.FullName.Length).FirstOrDefault();
        try { return drive is { IsReady: true } ? drive.AvailableFreeSpace : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal long MinimumFreeBytes => configuration.GetValue("Promotion:MinimumFreeBytes", 20L * 1024 * 1024 * 1024);

    private static bool Contains(string mount, string path)
    {
        var root = System.IO.Path.TrimEndingDirectorySeparator(mount);
        return path == root || path.StartsWith(root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? root : root + System.IO.Path.DirectorySeparatorChar,
            StringComparison.Ordinal);
    }

    private bool AllowedBaseConfigured => configuration["Promotion:AllowedBase"] is { } allowed &&
        !string.IsNullOrWhiteSpace(allowed) && System.IO.Path.IsPathFullyQualified(allowed);

    public async Task<PromotionSetup> SetupAsync(Guid? rootId = null, CancellationToken token = default)
    {
        if (rootId == Guid.Empty || rootId == null && (await ReadAsync(token)).Length != 1)
            return new(false, AllowedBaseConfigured, "library-folder-not-set", null);
        try
        {
            var root = await RequireAsync(rootId ?? Guid.Empty, token);
            return new(true, AllowedBaseConfigured, null, root.Id);
        }
        catch (PromotionException error)
        { return new(false, AllowedBaseConfigured, error.Reason, null); }
    }

    private void Validate(PromotionRoot root)
    {
        if (root.Id == Guid.Empty || string.IsNullOrWhiteSpace(root.Name) || root.Name.Length > 100 ||
            string.IsNullOrWhiteSpace(root.Path) ||
            !System.IO.Path.IsPathFullyQualified(root.Path))
            throw new ArgumentException("A named, fully qualified library root is required.");
        ValidateDirectory(root.Path);
    }

    internal void ValidateDirectory(string path)
    {
        var allowed = configuration["Promotion:AllowedBase"];
        if (!string.IsNullOrWhiteSpace(allowed))
        {
            if (!System.IO.Path.IsPathFullyQualified(allowed))
                throw new ArgumentException("The optional allowed base must be fully qualified.");
            path = Contained(allowed, path);
        }
        else path = System.IO.Path.GetFullPath(path);
        RejectLinks(path);
        if (!Directory.Exists(path)) throw new ArgumentException("The library root must already exist.");
        var probe = System.IO.Path.Combine(path, $".write-check-{Guid.NewGuid():N}");
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    internal bool IsWithinAllowedBase(string path)
    {
        var allowed = configuration["Promotion:AllowedBase"];
        if (string.IsNullOrWhiteSpace(allowed)) return true;
        if (!System.IO.Path.IsPathFullyQualified(allowed)) return false;
        try { Contained(allowed, path); return true; }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    internal static string Contained(string root, string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var prefix = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root)) + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The destination must stay inside its approved base.");
        return full;
    }

    // Jellyfin can read a music folder through a link; Allstarr writes to the folder the link leads to.
    internal static string FollowLinks(string path)
    {
        try
        {
            for (var pass = 0; pass < 8; pass++)
            {
                var current = System.IO.Path.GetPathRoot(path)!;
                foreach (var part in path[current.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = System.IO.Path.Combine(current, part);
                    if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                        current = System.IO.Path.GetFullPath(target.FullName);
                }
                if (current == path) return path;
                path = current;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        return path;
    }

    internal static void RejectLinks(string path)
    {
        var current = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path))!;
        foreach (var part in System.IO.Path.GetFullPath(path)[current.Length..].Split(System.IO.Path.DirectorySeparatorChar))
        {
            current = System.IO.Path.Combine(current, part);
            if (new FileInfo(current).LinkTarget != null || new DirectoryInfo(current).LinkTarget != null ||
                System.IO.Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Promotion paths may not traverse symbolic links.");
        }
    }
}
