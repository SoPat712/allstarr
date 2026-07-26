namespace allstarr.Core.Storage;

public enum TrackMatchState { Unresolved, Suggested, Accepted, Rejected, Ambiguous, Pinned }
public enum ManualOverrideDecision { Pin, Reject }
public enum PlaylistLinkMode { Virtual, Materialized, Hybrid }
public enum PlaylistMaterializationMode { Reconcile, Recreate }
public enum PlaylistSyncState { Pending, Running, Succeeded, PartiallySucceeded, Conflicted, Failed, Cancelled }
public enum PlaylistEntryOutcome { Matched, Reused, Added, Reordered, Skipped, Rejected, Unsupported, Failed }
public enum ScheduleOverlapPolicy { Skip, Queue }
public enum ScheduleMisfirePolicy { Skip, RunOnce }

public sealed class LibraryTrackRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid BackendIdentityId { get; set; }
    public Guid? CanonicalRecordingId { get; set; }
    public string LibraryScopeId { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string BackendInstanceId { get; set; } = string.Empty;
    public string BackendItemId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public long? DurationMilliseconds { get; set; }
    public string? DurationProvenance { get; set; }
    public DateTimeOffset? DurationRetrievedAt { get; set; }
    public string? Isrc { get; set; }
    public string? MusicBrainzRecordingId { get; set; }
    public string? MusicBrainzReleaseId { get; set; }
    public string? MusicBrainzArtistId { get; set; }
    public string ProviderIdsJson { get; set; } = "{}";
    // Stable provider/backend key only. Never persist signed or expiring artwork URLs here.
    public string? CoverArtReference { get; set; }
    public int? AcceptedDecisionVersion { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
    public DateTimeOffset SourceModifiedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class ExternalMetadataSnapshotRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid ProviderAccountId { get; set; }
    public Guid? ProviderTrackIdentityId { get; set; }
    public Guid? SourceJobId { get; set; }
    public string LibraryScopeId { get; set; } = string.Empty;
    public string BackendInstanceId { get; set; } = string.Empty;
    public string BackendPrincipalId { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string ResourceKind { get; set; } = string.Empty;
    public string ExternalIdHash { get; set; } = string.Empty;
    public int SnapshotVersion { get; set; }
    public string ProviderRevision { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string PayloadSha256 { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset RetrievedAt { get; set; }
}

public sealed class TrackMatchRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid ExternalSnapshotId { get; set; }
    public Guid? LibraryTrackId { get; set; }
    public Guid? CanonicalRecordingId { get; set; }
    public string LibraryScopeId { get; set; } = string.Empty;
    public TrackMatchState State { get; set; }
    public double Confidence { get; set; }
    public double Threshold { get; set; }
    public int DecisionVersion { get; set; }
    public int SourceSnapshotVersion { get; set; }
    public long LibraryIndexRevision { get; set; }
    public string MatcherVersion { get; set; } = "legacy";
    public string PolicyVersion { get; set; } = string.Empty;
    public string CandidateResultsJson { get; set; } = "[]";
    public string ReasonsJson { get; set; } = "[]";
    public string WarningsJson { get; set; } = "[]";
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset DecidedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class ManualTrackOverrideRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid ExternalSnapshotId { get; set; }
    public Guid? LibraryTrackId { get; set; }
    public string LibraryScopeId { get; set; } = string.Empty;
    public ManualOverrideDecision Decision { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int DecisionVersion { get; set; }
    public string MatcherVersion { get; set; } = "legacy";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class JobScheduleRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string LibraryScopeId { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "UTC";
    public ScheduleOverlapPolicy OverlapPolicy { get; set; }
    public ScheduleMisfirePolicy MisfirePolicy { get; set; }
    public string RetryPolicyJson { get; set; } = "{}";
    public string PayloadTemplateJson { get; set; } = "{}";
    public DateTimeOffset? NextRunAt { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class PlaylistLinkRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid ProviderAccountId { get; set; }
    public Guid? ScheduleId { get; set; }
    public bool Enabled { get; set; } = true;
    public string LibraryScopeId { get; set; } = string.Empty;
    public string SourceProviderId { get; set; } = string.Empty;
    public string SourcePlaylistId { get; set; } = string.Empty;
    public string SourcePlaylistIdHash { get; set; } = string.Empty;
    public string TargetProtocol { get; set; } = string.Empty;
    public string TargetBackendInstanceId { get; set; } = string.Empty;
    public Guid? TargetCredentialReferenceId { get; set; }
    public string? TargetPlaylistId { get; set; }
    public PlaylistLinkMode Mode { get; set; }
    public PlaylistMaterializationMode MaterializationMode { get; set; }
    public bool MirrorStaleEntries { get; set; }
    public bool PreserveManualEntries { get; set; } = true;
    public bool SyncName { get; set; } = true;
    public bool SyncDescription { get; set; } = true;
    public bool SyncArtwork { get; set; } = true;
    public string RuleVersion { get; set; } = string.Empty;
    public string PolicyVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class PlaylistSourceSnapshotRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid PlaylistLinkId { get; set; }
    public Guid ProviderAccountId { get; set; }
    public Guid? SourceJobId { get; set; }
    public int SnapshotVersion { get; set; }
    public string ProviderRevision { get; set; } = string.Empty;
    public string? ETag { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    // Stable provider/backend key only. Never persist signed or expiring artwork URLs here.
    public string? ArtworkReferenceKey { get; set; }
    public string PayloadSha256 { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset RetrievedAt { get; set; }
}

public sealed class PlaylistSourceEntryRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PlaylistSourceSnapshotId { get; set; }
    public Guid ExternalMetadataSnapshotId { get; set; }
    public int SourcePosition { get; set; }
    public string SourceEntryIdHash { get; set; } = string.Empty;
}

public sealed class PlaylistSyncRunRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid PlaylistLinkId { get; set; }
    public Guid PlaylistSourceSnapshotId { get; set; }
    public Guid? ScheduleId { get; set; }
    public Guid? JobId { get; set; }
    public long Generation { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RuleVersion { get; set; } = string.Empty;
    public PlaylistMaterializationMode MaterializationMode { get; set; }
    public PlaylistSyncState State { get; set; }
    public string? TargetRevisionBefore { get; set; }
    public string? TargetRevisionAfter { get; set; }
    public string? ConflictCode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class PlaylistSyncEntryResultRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PlaylistSyncRunId { get; set; }
    public Guid PlaylistSourceEntryId { get; set; }
    public Guid? TrackMatchId { get; set; }
    public Guid? LibraryTrackId { get; set; }
    public int SourcePosition { get; set; }
    public int? TargetPosition { get; set; }
    public PlaylistEntryOutcome Outcome { get; set; }
    public string? OutcomeCode { get; set; }
    public string DetailsJson { get; set; } = "{}";
}

public sealed class PlaylistTargetMembershipRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PlaylistLinkId { get; set; }
    public Guid LibraryTrackId { get; set; }
    public Guid CreatedBySyncRunId { get; set; }
    public string TargetEntryId { get; set; } = string.Empty;
    public int LastKnownPosition { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}
