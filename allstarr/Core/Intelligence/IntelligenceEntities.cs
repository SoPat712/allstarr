namespace allstarr.Core.Intelligence;

public enum RecommendationRunState { Pending, Running, Succeeded, Failed, Cancelled }
public enum GeneratedSetMaterializationState { Pending, Running, Succeeded, Failed, Unsupported, Cancelled }
public enum ListeningEventState { Playing, Completed, Skipped, Abandoned }

public sealed class IntelligencePolicyRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Protocol { get; set; } = ""; public string BackendInstanceId { get; set; } = "";
    public string LibraryScopeId { get; set; } = ""; public bool Enabled { get; set; }
    public Guid? TargetCredentialReferenceId { get; set; }
    public int RetentionDays { get; set; } = 30; public string AllowedSignalTypesJson { get; set; } = "[]";
    public string EnabledProvidersJson { get; set; } = "[]"; public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}
public sealed class ListeningSignalRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Protocol { get; set; } = ""; public string BackendInstanceId { get; set; } = "";
    public string LibraryScopeId { get; set; } = ""; public string SignalType { get; set; } = "";
    public string TrackKeyHash { get; set; } = ""; public double Value { get; set; }
    public string TrackReference { get; set; } = "";
    public string? SignalKey { get; set; }
    public Guid? SourceJobId { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
public sealed class ListeningEventRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Protocol { get; set; } = "";
    public string BackendInstanceId { get; set; } = "";
    public string LibraryScopeId { get; set; } = "";
    public string OccurrenceKey { get; set; } = "";
    public ListeningEventState State { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ListenedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long? PositionTicks { get; set; }
    public long? DurationMilliseconds { get; set; }
    public string? ClientClass { get; set; }
    public string? DeviceClass { get; set; }
    public string SourceKind { get; set; } = "protocol";
    public string? ImportProvenance { get; set; }
    public string TrackReference { get; set; } = "";
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public Guid? CanonicalRecordingId { get; set; }
    public Guid? LibraryTrackId { get; set; }
    public string? ProviderId { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public Guid? ProviderTrackIdentityId { get; set; }
    public string? ProviderTrackReference { get; set; }
}
public sealed class ListeningProfileRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Protocol { get; set; } = ""; public string BackendInstanceId { get; set; } = "";
    public string LibraryScopeId { get; set; } = ""; public string ProfileJson { get; set; } = "{}";
    public DateTimeOffset WindowStart { get; set; }
    public DateTimeOffset WindowEnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class RecommendationRunRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Protocol { get; set; } = ""; public string BackendInstanceId { get; set; } = "";
    public string LibraryScopeId { get; set; } = ""; public Guid JobId { get; set; }
    public string IdempotencyKey { get; set; } = ""; public string PolicySnapshotJson { get; set; } = "{}";
    public string SeedTrackKeysJson { get; set; } = "[]"; public int Limit { get; set; }
    public Guid? TargetCredentialReferenceId { get; set; }
    public Guid? ScheduleId { get; set; }
    public DateTimeOffset? ScheduledFor { get; set; }
    public RecommendationRunState State { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; }
}
public sealed class RecommendationCandidateRecord
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public int Position { get; set; }
    public string TrackKey { get; set; } = "";
    public double Score { get; set; }
    public string Source { get; set; } = "";
    public string SignalsJson { get; set; } = "[]"; public DateTimeOffset CreatedAt { get; set; }
    public string IdentityJson { get; set; } = "{}";
    public Guid? CanonicalRecordingId { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public string SourceRevision { get; set; } = "legacy";
    public string ExclusionsJson { get; set; } = "[]";
    public long Revision { get; set; }
}
public sealed class RecommendationFeedbackRecord
{
    public Guid Id { get; set; }
    public Guid CandidateId { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Protocol { get; set; } = "";
    public string BackendInstanceId { get; set; } = "";
    public string LibraryScopeId { get; set; } = "";
    public string TrackKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? ReasonCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}
public sealed class GeneratedSetRecord
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Protocol { get; set; } = "";
    public string BackendInstanceId { get; set; } = ""; public string LibraryScopeId { get; set; } = "";
    public string Name { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; }
    public Guid? TargetCredentialReferenceId { get; set; }
    public Guid? ScheduleId { get; set; }
    public GeneratedSetMaterializationState MaterializationState { get; set; }
    public string? BackendPlaylistId { get; set; }
    public string? TargetRevision { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset? MaterializedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}
public sealed class GeneratedSetEntryRecord
{
    public Guid Id { get; set; }
    public Guid GeneratedSetId { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public int Position { get; set; }
    public string TrackKey { get; set; } = "";
    public string ExplanationJson { get; set; } = "[]";
    public string IdentityJson { get; set; } = "{}";
    public double Score { get; set; }
    public string Source { get; set; } = "";
}
