using System.Security.Cryptography;

namespace allstarr.Core.ManagedFiles;

public sealed partial class FilePlacementService
{
    internal ManagedFileSystemIdentity InspectLegacyFile(string rootPath, string path, long? length = null)
    {
        var root = ValidateRoot(rootPath);
        for (var parent = new DirectoryInfo(root); parent != null; parent = parent.Parent)
            if (parent.LinkTarget != null) throw new UnauthorizedAccessException("The legacy root traverses a symbolic link.");
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The legacy file is outside its selected root.");
        RejectSymlinksUnder(root, full);
        var info = new FileInfo(full);
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0 || (length.HasValue && info.Length != length))
            throw new IOException("The legacy file changed after preview.");
        if (!files.TryGetFileIdentity(full, out var identity))
            throw new IOException("The legacy file has no stable filesystem identity.");
        return identity;
    }

    internal async Task<ManagedFileRecord> PrepareLegacyAdoptionAsync(LegacyMediaFileSnapshot snapshot,
        DateTimeOffset now, CancellationToken token)
    {
        ValidateLegacySnapshot(snapshot);
        await using var stream = files.OpenRead(snapshot.Path, sequential: true);
        if (!files.TryGetFileIdentity(stream, out var opened) || opened.DeviceId != snapshot.DeviceId || opened.FileId != snapshot.FileId)
            throw new IOException("The opened legacy file changed after preview.");
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
        ValidateLegacySnapshot(snapshot);
        if (stream.Length != snapshot.Length) throw new IOException("The legacy file changed during verification.");
        return new(Guid.CreateVersion7(), snapshot.RootId, snapshot.Path, hash, snapshot.Length,
            ManagedFilePlacementMethod.Adopted, null, null, ManagedFileScopeKey.Create(null, snapshot.RootId), 1, true, now)
        {
            TargetRootPath = snapshot.RootPath,
            FileSystemDeviceId = snapshot.DeviceId,
            FileSystemFileId = snapshot.FileId,
            FileSystemLinkCount = opened.LinkCount
        };
    }

    internal void ValidateLegacySnapshot(LegacyMediaFileSnapshot snapshot)
    {
        var identity = InspectLegacyFile(snapshot.RootPath, snapshot.Path, snapshot.Length);
        if (identity.DeviceId != snapshot.DeviceId || identity.FileId != snapshot.FileId ||
            File.GetLastWriteTimeUtc(snapshot.Path).Ticks != snapshot.LastWriteUtcTicks)
            throw new IOException("The legacy file changed after preview. Preview the files again before importing.");
    }

    internal async Task<ManagedFileRecord> AdoptLegacyFileAsync(ManagedFileRecord prepared,
        LegacyMediaFileSnapshot snapshot, string referenceKey, CancellationToken token)
    {
        ValidateLegacySnapshot(snapshot);
        var existing = await ownership.FindByPathAsync(prepared.CanonicalPath, token);
        if (existing != null)
        {
            if (existing.PlacementMethod != ManagedFilePlacementMethod.Adopted || existing.RootId != prepared.RootId ||
                existing.ContentSha256 != prepared.ContentSha256 || existing.Length != prepared.Length ||
                existing.FileSystemDeviceId != prepared.FileSystemDeviceId || existing.FileSystemFileId != prepared.FileSystemFileId)
                throw new IOException("The legacy path already has a different ownership record.");
            return existing;
        }
        if (await ownership.IsPathReservedAsync(prepared.CanonicalPath, token))
            throw new IOException("The legacy path is reserved by an earlier ownership record.");
        return await ownership.AddAsync(prepared, new(Guid.CreateVersion7(), prepared.Id, null,
            prepared.ScopeKey, referenceKey, prepared.CreatedAt), token);
    }
}
