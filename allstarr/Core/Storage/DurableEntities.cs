using allstarr.Core.Capabilities;

namespace allstarr.Core.Storage;

public enum ProviderAccountScope
{
    Shared,
    Personal
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

public sealed class UserRecord
{
    public Guid Id { get; set; }
    public string BackendType { get; set; } = string.Empty;
    public string BackendInstanceId { get; set; } = string.Empty;
    public string BackendPrincipalId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class OnboardingStateRecord
{
    public Guid Id { get; set; }
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
    public Guid? OwnerUserId { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ProviderAccountScope Scope => OwnerUserId.HasValue
        ? ProviderAccountScope.Personal
        : ProviderAccountScope.Shared;
    public Guid? SecretReferenceId { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class SecretReferenceRecord
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
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
    public Guid? OwnerUserId { get; set; }
    public Guid? ProviderAccountId { get; set; }
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

public sealed class CanonicalRecordingRecord
{
    public Guid Id { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Disambiguation { get; set; }
    public long? DurationMilliseconds { get; set; }
    public bool? IsExplicit { get; set; }
    public bool IsProvisional { get; set; }
    public string? Isrc { get; set; }
    public string? MusicBrainzRecordingId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class ProviderTrackIdentityRecord
{
    public Guid Id { get; set; }
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
    public string SourceSha256 { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = "legacy-env-import-v1";
    public Guid? ActorUserId { get; set; }
    public Guid AuditEventId { get; set; }
    public string ResultJson { get; set; } = "{}";
    public string ProvenanceJson { get; set; } = """{"settings":[],"providerAccounts":[]}""";
    public DateTimeOffset AppliedAt { get; set; }
}
