namespace allstarr.Core.ManagedFiles;

// Persistence shape is intentionally separate from the immutable placement result.
// Phase 6 migration wiring is consolidated by the storage owner.
public sealed class ManagedFileOwnershipEntity
{
    public Guid Id { get; set; }
    public Guid RootId { get; set; }
    public string TargetRootPath { get; set; } = string.Empty;
    public string CanonicalPath { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public long Length { get; set; }
    public string? FileSystemDeviceId { get; set; }
    public string? FileSystemFileId { get; set; }
    public uint? FileSystemLinkCount { get; set; }
    public ManagedFilePlacementMethod PlacementMethod { get; set; }
    public Guid TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string? LibraryScopeId { get; set; }
    public Guid? SourceJobId { get; set; }
    public string ScopeKey { get; set; } = string.Empty;
    public int ReferenceCount { get; set; }
    public bool IsManaged { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public long Revision { get; set; }
}
