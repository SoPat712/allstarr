namespace allstarr.Core.ManagedFiles;

public enum ManagedFilePlacementMethod
{
    HardLink,
    Reflink,
    Copy
}

public sealed record ManagedFileRoot(
    Guid Id,
    string CanonicalPath,
    Guid? TenantId,
    Guid? OwnerUserId,
    string? LibraryScopeId);

public sealed record ManagedTrackPathValues(
    string Title,
    string Artist,
    string? Album = null,
    string? AlbumArtist = null,
    string? Genre = null,
    int? Year = null,
    int? Track = null,
    string Extension = ".flac");

public sealed record ManagedFilePlacementRequest(
    ManagedFileRoot Root,
    string SourcePath,
    string PathTemplate,
    ManagedTrackPathValues Track,
    Guid? SourceJobId,
    string ScopeKey,
    bool SourceIsAllstarrManaged,
    bool SourceIsImmutable = true,
    string? ExpectedContentSha256 = null,
    long? ExpectedLength = null)
{
    // A stable key makes a placement retry reuse its durable reference instead of
    // inflating the count. Callers without a durable operation key receive a new reference.
    public string? ReferenceKey { get; init; }

    // Hardlinks share an inode. They are permitted only when neither side will ever
    // be tagged, transcoded, or otherwise rewritten after placement.
    public bool DestinationIsImmutable { get; init; }
}

public sealed record ManagedFileSystemIdentity(string DeviceId, string FileId, uint LinkCount);

public static class ManagedFileScopeKey
{
    public static string Create(Guid tenantId, Guid? ownerUserId, Guid rootId, string? libraryScopeId)
    {
        if (tenantId == Guid.Empty || rootId == Guid.Empty)
            throw new ArgumentException("Managed-file scopes require tenant and root identities.");
        return $"managed:{tenantId:N}:{ownerUserId?.ToString("N") ?? "shared"}:{rootId:N}:" +
               (string.IsNullOrWhiteSpace(libraryScopeId) ? "default" : libraryScopeId.Trim());
    }
}

public sealed record ManagedFileReference(
    Guid Id,
    Guid ManagedFileId,
    Guid TenantId,
    Guid? OwnerUserId,
    string ScopeKey,
    string ReferenceKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReleasedAt = null);

public sealed record ManagedFileRecord(
    Guid Id,
    Guid RootId,
    string CanonicalPath,
    string ContentSha256,
    long Length,
    ManagedFilePlacementMethod PlacementMethod,
    Guid? TenantId,
    Guid? OwnerUserId,
    string? LibraryScopeId,
    Guid? SourceJobId,
    string ScopeKey,
    int ReferenceCount,
    bool IsManaged,
    DateTimeOffset CreatedAt)
{
    public string TargetRootPath { get; init; } = string.Empty;
    public string? FileSystemDeviceId { get; init; }
    public string? FileSystemFileId { get; init; }
    public uint? FileSystemLinkCount { get; init; }
}

public sealed record ManagedFilePlacementResult(ManagedFileRecord File, bool Reused);

public interface IManagedFileOwnershipStore
{
    Task<ManagedFileRecord?> FindByPathAsync(string canonicalPath, CancellationToken cancellationToken);
    Task<ManagedFileRecord?> FindCompatibleAsync(Guid rootId, string contentSha256, string scopeKey, CancellationToken cancellationToken);
    Task<ManagedFileRecord> AddAsync(ManagedFileRecord record, ManagedFileReference reference, CancellationToken cancellationToken);
    Task<ManagedFileRecord> AddReferenceAsync(Guid id, ManagedFileReference reference, CancellationToken cancellationToken);
    Task<ManagedFileRecord> ReleaseReferenceAsync(Guid id, string referenceKey, CancellationToken cancellationToken);
}

public interface IManagedFileOperations
{
    bool TryCreateHardLink(string linkPath, string existingPath);
    bool TryCreateReflink(string destinationPath, string sourcePath);
    bool TryGetFileIdentity(string path, out ManagedFileSystemIdentity identity);
    Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);
    void MoveNoReplace(string sourcePath, string destinationPath);
}
