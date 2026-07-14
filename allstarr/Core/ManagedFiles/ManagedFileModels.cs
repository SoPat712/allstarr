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
    long? ExpectedLength = null);

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
}

public sealed record ManagedFilePlacementResult(ManagedFileRecord File, bool Reused);

public interface IManagedFileOwnershipStore
{
    Task<ManagedFileRecord?> FindByPathAsync(string canonicalPath, CancellationToken cancellationToken);
    Task<ManagedFileRecord?> FindCompatibleAsync(Guid rootId, string contentSha256, string scopeKey, CancellationToken cancellationToken);
    Task<ManagedFileRecord> AddAsync(ManagedFileRecord record, CancellationToken cancellationToken);
    Task<ManagedFileRecord> AddReferenceAsync(Guid id, CancellationToken cancellationToken);
}

public interface IManagedFileOperations
{
    bool TryCreateHardLink(string linkPath, string existingPath);
    bool TryCreateReflink(string destinationPath, string sourcePath);
    Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);
    void MoveNoReplace(string sourcePath, string destinationPath);
}
