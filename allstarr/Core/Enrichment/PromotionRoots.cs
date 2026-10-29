using System.Text.Json;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed record PromotionRoot(Guid Id, string Name, string Path);

public sealed class PromotionRoots(IDurableRuntimeSettings settings, IDbContextFactory<AllstarrDbContext> factory,
    IConfiguration configuration)
{
    public const string Key = "Promotion:Roots";

    public async Task<PromotionRoot[]> ReadAsync(CancellationToken token = default) =>
        JsonSerializer.Deserialize<PromotionRoot[]>((string)(await settings.GetAsync(Key, token)).Value) ?? [];

    public async Task SaveAsync(Guid actor, PromotionRoot[] roots, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.Users.AnyAsync(item => item.Id == actor && item.Enabled && item.IsAdmin, token))
            throw new UnauthorizedAccessException("Administrator permissions required.");
        if (roots.Length > 16 || roots.Select(item => item.Id).Distinct().Count() != roots.Length)
            throw new ArgumentException("Choose at most sixteen distinct library roots.");
        foreach (var root in roots) Validate(root);
        await settings.ApplyBatchAsync([new(Key, JsonSerializer.Serialize(roots))], "promotion", actor, token);
    }

    public async Task<PromotionRoot> RequireAsync(Guid id, CancellationToken token)
    {
        var root = (await ReadAsync(token)).SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("This library root is no longer approved.");
        Validate(root);
        return root;
    }

    private void Validate(PromotionRoot root)
    {
        var allowed = configuration["Promotion:AllowedBase"];
        if (root.Id == Guid.Empty || string.IsNullOrWhiteSpace(root.Name) || root.Name.Length > 100 ||
            string.IsNullOrWhiteSpace(allowed) || !System.IO.Path.IsPathFullyQualified(allowed) ||
            !System.IO.Path.IsPathFullyQualified(root.Path))
            throw new ArgumentException("A named root inside the configured allowed base is required.");
        var path = Contained(allowed, root.Path);
        RejectLinks(path);
        if (!Directory.Exists(path)) throw new ArgumentException("The library root must already exist.");
        var probe = System.IO.Path.Combine(path, $".write-check-{Guid.NewGuid():N}");
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    internal static string Contained(string root, string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var prefix = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root)) + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The destination must stay inside its approved base.");
        return full;
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
