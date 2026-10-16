using System.Security.Cryptography;

namespace allstarr.Core.ManagedFiles;

public sealed partial class FilePlacementService
{
    private static readonly byte[] StorageMarkerContent = "allstarr-storage-v1\n"u8.ToArray();

    internal ManagedFileSystemIdentity InspectStorageMarker(string dataRoot)
    {
        var root = ValidateRoot(dataRoot);
        var path = ContainedPath(root, ".allstarr-storage");
        for (var parent = new DirectoryInfo(root); parent != null; parent = parent.Parent)
            if (parent.LinkTarget != null) throw new UnauthorizedAccessException("The data folder traverses a symbolic link.");
        if (new FileInfo(path).LinkTarget != null) throw new UnauthorizedAccessException("The storage marker is a symbolic link.");
        if (!File.Exists(path))
        {
            var createOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) createOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var created = new FileStream(path, createOptions);
            created.Write(StorageMarkerContent);
            created.Flush(true);
        }
        var identity = InspectLegacyFile(root, path, StorageMarkerContent.Length);
        using var opened = files.OpenRead(path);
        if (!files.TryGetFileIdentity(opened, out var current) || current.DeviceId != identity.DeviceId || current.FileId != identity.FileId)
            throw new IOException("The opened storage marker changed.");
        Span<byte> content = stackalloc byte[StorageMarkerContent.Length];
        opened.ReadExactly(content);
        if (!content.SequenceEqual(StorageMarkerContent)) throw new IOException("The data-folder marker is not recognized.");
        var after = InspectLegacyFile(root, path, StorageMarkerContent.Length);
        if (after.DeviceId != identity.DeviceId || after.FileId != identity.FileId) throw new IOException("The storage marker changed.");
        return identity;
    }

    internal async Task<ManagedFileRecord> PrepareCopiedFileAsync(ManagedFileRecord record, CancellationToken token)
    {
        if (!record.IsManaged || record.ScopeKey != ManagedFileScopeKey.Create(record.OwnerUserId, record.RootId) ||
            Path.GetFullPath(record.CanonicalPath) != record.CanonicalPath)
            throw new UnauthorizedAccessException("The saved file ownership is invalid.");
        var identity = InspectLegacyFile(record.TargetRootPath, record.CanonicalPath, record.Length);
        await using var opened = files.OpenRead(record.CanonicalPath, sequential: true);
        if (!files.TryGetFileIdentity(opened, out var current) || current.DeviceId != identity.DeviceId || current.FileId != identity.FileId)
            throw new IOException("The opened file changed before verification.");
        var fingerprint = await SHA256.HashDataAsync(opened, token);
        if (opened.Length != record.Length || !CryptographicOperations.FixedTimeEquals(fingerprint, Convert.FromHexString(record.ContentSha256)))
            throw new IOException("The copied file content does not match its saved ownership.");
        var prepared = record with
        {
            FileSystemDeviceId = identity.DeviceId,
            FileSystemFileId = identity.FileId,
            FileSystemLinkCount = identity.LinkCount
        };
        ValidatePreparedCopy(prepared, token);
        if (!files.TryGetFileIdentity(opened, out current) || current.DeviceId != identity.DeviceId || current.FileId != identity.FileId)
            throw new IOException("The opened file changed during verification.");
        return prepared;
    }

    internal void ValidatePreparedCopy(ManagedFileRecord record, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var identity = InspectLegacyFile(record.TargetRootPath, record.CanonicalPath, record.Length);
        if (identity.DeviceId != record.FileSystemDeviceId || identity.FileId != record.FileSystemFileId || identity.LinkCount != record.FileSystemLinkCount)
            throw new IOException("The verified file changed before ownership could be rebound.");
    }
}
