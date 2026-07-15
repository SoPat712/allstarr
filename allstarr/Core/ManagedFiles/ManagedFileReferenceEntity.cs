namespace allstarr.Core.ManagedFiles;

public sealed class ManagedFileReferenceEntity
{
    public Guid Id { get; set; }
    public Guid ManagedFileId { get; set; }
    public Guid TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string ScopeKey { get; set; } = string.Empty;
    public string ReferenceKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public long Revision { get; set; }
}
