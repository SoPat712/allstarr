using allstarr.Core.Capabilities;

namespace allstarr.Core.Storage;

public enum PlatformUserStatus
{
    Active,
    Disabled
}

public enum ProviderAccountScope
{
    Global,
    User,
    Library
}

public enum DurableJobState
{
    Pending,
    Running,
    RetryScheduled,
    Succeeded,
    Failed,
    Cancelled
}

public enum OutboxMessageState
{
    Pending,
    Delivering,
    Delivered,
    Failed
}

public enum ProviderHealthState
{
    Unknown,
    Healthy,
    Degraded,
    Unavailable,
    Unauthorized
}

public enum ProviderCircuitState
{
    Closed,
    Open,
    HalfOpen
}

public enum ProviderIdentityScope
{
    Unknown = 0,
    Catalog = 1,
    Account = 2
}

public enum ProviderIdentityVerification
{
    Unknown = 0,
    Verified = 1,
    Pinned = 2
}

public sealed class TenantRecord
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PlatformUserRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public PlatformUserStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class BackendIdentityRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string BackendType { get; set; } = string.Empty;
    public string BackendInstanceId { get; set; } = string.Empty;
    public string PrincipalId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class OnboardingStateRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public string CompletedStepsJson { get; set; } = "[]";
    public string CompletionSource { get; set; } = string.Empty;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ReopenedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class AdminAuthSessionRecord
{
    public string Id { get; set; } = string.Empty;
    public string ProtectedPayload { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class ProviderAccountRecord
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ProviderAccountScope Scope { get; set; }
    public string? LibraryScopeId { get; set; }
    public Guid? SecretReferenceId { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class SecretReferenceRecord
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public int ActiveVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class SecretVersionRecord
{
    public Guid Id { get; set; }
    public Guid SecretReferenceId { get; set; }
    public int Version { get; set; }
    public string KeyId { get; set; } = string.Empty;
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] AuthenticationTag { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}

public sealed class DurableJobRecord
{
    public Guid Id { get; set; }
    public string ScopeKey { get; set; } = string.Empty;
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public string? LibraryScopeId { get; set; }
    public string? ProviderCapability { get; set; }
    public string PolicySnapshotJson { get; set; } = "{}";
    public string RequestFingerprint { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string IdempotencyKey { get; set; } = string.Empty;
    public DurableJobState State { get; set; }
    public int Priority { get; set; }
    public int AttemptCount { get; set; }
    public int FailureCount { get; set; }
    public int DeferralCount { get; set; }
    public int MaxAttempts { get; set; }
    public int MaxDeferrals { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? CancellationRequestedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class JobAttemptRecord
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public int AttemptNumber { get; set; }
    public string WorkerId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Outcome { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class OutboxMessageRecord
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public OutboxMessageState State { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 20;
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class ProviderHealthSampleRecord
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid ProviderAccountId { get; set; }
    public string Capability { get; set; } = string.Empty;
    public ProviderHealthState State { get; set; }
    public long? LatencyMilliseconds { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ProviderHealthRollupRecord
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid ProviderAccountId { get; set; }
    public string Capability { get; set; } = string.Empty;
    public DateTimeOffset WindowStart { get; set; }
    public DateTimeOffset WindowEnd { get; set; }
    public int SampleCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public double SuccessRate { get; set; }
    public long? P50LatencyMilliseconds { get; set; }
    public long? P95LatencyMilliseconds { get; set; }
    public ProviderHealthState LastState { get; set; }
    public string? LastFailureCode { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class ProviderCircuitRecord
{
    public Guid Id { get; set; }
    public Guid ProviderAccountId { get; set; }
    public string Capability { get; set; } = string.Empty;
    public ProviderCircuitState State { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }
    public DateTimeOffset? RetryAfter { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class CanonicalRecordingRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string? Isrc { get; set; }
    public string? MusicBrainzRecordingId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class ProviderTrackIdentityRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CanonicalRecordingId { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public ProviderResourceKind ResourceKind { get; set; }
    public string CatalogNamespace { get; set; } = "default";
    public ProviderIdentityScope Scope { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string ExternalIdHash { get; set; } = string.Empty;
    public ProviderIdentityVerification Verification { get; set; }
    public string VerificationMethod { get; set; } = string.Empty;
    public int DecisionVersion { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class AuditEventRecord
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LegacyEnvImportRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string SourceSha256 { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = "legacy-env-import-v1";
    public Guid? ActorUserId { get; set; }
    public Guid AuditEventId { get; set; }
    public string ResultJson { get; set; } = "{}";
    public string ProvenanceJson { get; set; } = """{"settings":[],"providerAccounts":[]}""";
    public DateTimeOffset AppliedAt { get; set; }
}

public sealed class BackupRecord
{
    public Guid Id { get; set; }
    public string StorageProvider { get; set; } = string.Empty;
    public string ArtifactPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public string ApplicationVersion { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? RestoreStatus { get; set; }
    public DateTimeOffset? RestoreVerifiedAt { get; set; }
}
