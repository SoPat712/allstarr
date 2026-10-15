using System.Security.Cryptography;

namespace allstarr.Core.ManagedFiles;

public interface IManagedFileRemovalStore
{
    Task<ManagedFileRecord?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task MarkRemovedAsync(Guid id, CancellationToken cancellationToken);

    // Implementations that have not adopted unpublished cleanup yet remain safe:
    // callers must treat a false result as "nothing was revoked".
    Task<bool> TryMarkUnpublishedRemovedAsync(
        ManagedFileRecord expected,
        CancellationToken cancellationToken) => Task.FromResult(false);

    // Implementations can opt into crash recovery by returning a durably revoked
    // household record. Until then, a resume request must remain a no-op.
    Task<ManagedFileRecord?> GetRevokedUnpublishedAsync(
        Guid id,
        CancellationToken cancellationToken) => Task.FromResult<ManagedFileRecord?>(null);
}

public sealed class ManagedFileRemovalService(
    IManagedFileRemovalStore store,
    IManagedFileOperations? files = null,
    FilePlacementService? placement = null)
{
    public async Task<bool> RemoveUnpublishedAsync(
        Guid id,
        string requestingScopeKey,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(requestingScopeKey)) return false;
        var record = await store.GetAsync(id, cancellationToken);
        if (record is null || !record.IsManaged || record.ReferenceCount != 0 ||
            !StringComparer.Ordinal.Equals(record.ScopeKey, requestingScopeKey)) return false;

        var existedBeforeValidation = File.Exists(record.CanonicalPath);
        if (!await IsSafeCurrentFileAsync(record, existedBeforeValidation, cancellationToken)) return false;

        if (!await store.TryMarkUnpublishedRemovedAsync(record, cancellationToken)) return false;

        // A file which was absent while ownership was revoked must not be deleted
        // if an unrelated file appears at the same path during the transaction.
        if (!existedBeforeValidation || !File.Exists(record.CanonicalPath)) return true;
        if (!await IsSafeCurrentFileAsync(record, true, cancellationToken)) return true;
        // Durable revocation precedes this delete. If deletion fails, the record
        // remains revoked and an operator can repair the orphan safely.
        File.Delete(record.CanonicalPath);
        return true;
    }

    public async Task<bool> ResumeUnpublishedRemovalAsync(
        Guid id,
        string requestingScopeKey,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(requestingScopeKey)) return false;
        var record = await store.GetRevokedUnpublishedAsync(id, cancellationToken);
        if (record is null || !record.IsManaged || record.OwnerUserId is not null ||
            record.SourceJobId is not null || record.ReferenceCount != 0 ||
            !StringComparer.Ordinal.Equals(record.ScopeKey, requestingScopeKey)) return false;

        var exists = File.Exists(record.CanonicalPath);
        if (!exists)
            return await IsSafeCurrentFileAsync(record, false, cancellationToken);

        // Existing bytes require the identity captured at placement time. Without
        // it, a resumed cleanup cannot distinguish the owned file from a replacement.
        if (string.IsNullOrWhiteSpace(record.FileSystemDeviceId) ||
            string.IsNullOrWhiteSpace(record.FileSystemFileId) ||
            !await IsSafeCurrentFileAsync(record, true, cancellationToken)) return false;

        File.Delete(record.CanonicalPath);
        return true;
    }

    public async Task RemoveAsync(Guid id, string requestingScopeKey, bool explicitlyConfirmed, CancellationToken cancellationToken = default)
    {
        if (!explicitlyConfirmed)
            throw new InvalidOperationException("Managed-file removal requires explicit confirmation.");
        var record = await store.GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException("Managed file not found.");
        if (!record.IsManaged || !StringComparer.Ordinal.Equals(record.ScopeKey, requestingScopeKey))
            throw new UnauthorizedAccessException("The file is not owned by this managed scope.");
        if (record.ReferenceCount > 1)
            throw new InvalidOperationException("The managed file still has protected references.");
        ValidateManagedPath(record);
        if (!File.Exists(record.CanonicalPath))
        {
            await store.MarkRemovedAsync(id, cancellationToken);
            return;
        }
        if ((File.GetAttributes(record.CanonicalPath) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Refusing to remove a symbolic-link target.");
        ValidateFileIdentity(record);

        // Revalidate and revoke durable ownership first. If the physical delete then
        // fails, an orphan remains for explicit operator repair; no live record can
        // accidentally authorize a later retry to delete a different file.
        await store.MarkRemovedAsync(id, cancellationToken);
        File.Delete(record.CanonicalPath);
    }

    private void ValidateFileIdentity(ManagedFileRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.FileSystemDeviceId) ||
            string.IsNullOrWhiteSpace(record.FileSystemFileId)) return;
        if (files is null || !files.TryGetFileIdentity(record.CanonicalPath, out var identity) ||
            !StringComparer.Ordinal.Equals(record.FileSystemDeviceId, identity.DeviceId) ||
            !StringComparer.Ordinal.Equals(record.FileSystemFileId, identity.FileId))
            throw new UnauthorizedAccessException("The managed file identity changed after placement.");
    }

    private async Task<bool> IsSafeCurrentFileAsync(
        ManagedFileRecord record,
        bool fileExists,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateManagedPath(record);
            if (!fileExists) return true;
            if ((File.GetAttributes(record.CanonicalPath) & FileAttributes.ReparsePoint) != 0)
                return false;
            if (placement is not null)
            {
                await placement.VerifyOwnedFileAsync(record, cancellationToken);
                return true;
            }

            ValidateFileIdentity(record);
            var info = new FileInfo(record.CanonicalPath);
            if (info.Length != record.Length) return false;
            byte[] expected;
            try { expected = Convert.FromHexString(record.ContentSha256); }
            catch (FormatException) { return false; }
            if (expected.Length != SHA256.HashSizeInBytes) return false;
            await using var stream = new FileStream(record.CanonicalPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = await SHA256.HashDataAsync(stream, cancellationToken);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            return false;
        }
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
