namespace allstarr.Core.Storage;

public enum ExtensionPackageState
{
    Staged,
    ReviewRequired,
    Active,
    Disabled,
    RolledBack,
    Uninstalled,
    Failed
}

public enum ExtensionPermissionDecision
{
    Pending,
    Approved,
    Denied
}

public sealed class ExtensionRegistryRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RegistryUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class ExtensionPackageRecord
{
    public Guid Id { get; set; }
    public Guid? RegistryId { get; set; }
    public Guid? PreviousPackageId { get; set; }
    public string ExtensionId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public string PackagePath { get; set; } = string.Empty;
    public string ManifestJson { get; set; } = "{}";
    public ExtensionPackageState State { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset StagedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public long Revision { get; set; }
}

public sealed class ExtensionPermissionReviewRecord
{
    public Guid Id { get; set; }
    public Guid ExtensionPackageId { get; set; }
    public string PermissionKind { get; set; } = string.Empty;
    public string PermissionValue { get; set; } = string.Empty;
    public bool Required { get; set; }
    public ExtensionPermissionDecision Decision { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class ExtensionLogRecord
{
    public Guid Id { get; set; }
    public Guid ExtensionPackageId { get; set; }
    public string ExtensionId { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public string EventCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
