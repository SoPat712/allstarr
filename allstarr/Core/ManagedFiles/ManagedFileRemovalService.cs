namespace allstarr.Core.ManagedFiles;

public interface IManagedFileRemovalStore
{
    Task<ManagedFileRecord?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task MarkRemovedAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class ManagedFileRemovalService(IManagedFileRemovalStore store)
{
    public async Task RemoveAsync(Guid id, string requestingScopeKey, bool explicitlyConfirmed, CancellationToken cancellationToken = default)
    {
        if (!explicitlyConfirmed)
            throw new InvalidOperationException("Managed-file removal requires explicit confirmation.");
        var record = await store.GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException("Managed file not found.");
        if (!record.IsManaged || !StringComparer.Ordinal.Equals(record.ScopeKey, requestingScopeKey))
            throw new UnauthorizedAccessException("The file is not owned by this managed scope.");
        if (record.ReferenceCount != 1)
            throw new InvalidOperationException("The managed file still has protected references.");
        ValidateManagedPath(record);
        if (!File.Exists(record.CanonicalPath))
        {
            await store.MarkRemovedAsync(id, cancellationToken);
            return;
        }
        if ((File.GetAttributes(record.CanonicalPath) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Refusing to remove a symbolic-link target.");

        // Revalidate and revoke durable ownership first. If the physical delete then
        // fails, an orphan remains for explicit operator repair; no live record can
        // accidentally authorize a later retry to delete a different file.
        await store.MarkRemovedAsync(id, cancellationToken);
        File.Delete(record.CanonicalPath);
    }

    private static void ValidateManagedPath(ManagedFileRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.TargetRootPath) || !Path.IsPathRooted(record.TargetRootPath) ||
            string.IsNullOrWhiteSpace(record.CanonicalPath) || !Path.IsPathRooted(record.CanonicalPath))
            throw new UnauthorizedAccessException("The managed file does not have a valid recorded root.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(record.TargetRootPath));
        var path = Path.GetFullPath(record.CanonicalPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new UnauthorizedAccessException("The managed file is outside its recorded root.");

        RejectSymlink(root);
        var current = root;
        foreach (var part in Path.GetRelativePath(root, path)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current)) RejectSymlink(current);
        }
    }

    private static void RejectSymlink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Refusing to remove a managed file through a symbolic link.");
    }
}
