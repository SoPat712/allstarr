namespace allstarr.Core.Storage;

public enum MetadataEnrichmentApplicationState { Pending, Applied, Failed }

public sealed class MetadataEnrichmentPlanRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid LineageJobId { get; set; }
    public Guid ManagedArtifactId { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public int PlanVersion { get; set; }
    public string SourceRevisionsJson { get; set; } = "[]";
    public string DecisionsJson { get; set; } = "[]";
    public string TagsJson { get; set; } = "{}";
    public string PathValuesJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MetadataEnrichmentApplicationRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid PlanId { get; set; }
    public Guid ManagedArtifactId { get; set; }
    public Guid LineageJobId { get; set; }
    public string ArtifactContentSha256 { get; set; } = string.Empty;
    public MetadataEnrichmentApplicationState State { get; set; }
    public string? ErrorCode { get; set; }
    public string? SafeErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}
