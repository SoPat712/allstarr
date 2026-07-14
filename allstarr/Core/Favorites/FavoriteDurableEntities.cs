namespace allstarr.Core.Favorites;

public enum FavoriteOperation
{
    Favorite,
    Unfavorite
}

public enum FavoriteEventState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public enum FavoriteActionState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed class FavoriteEventRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Protocol { get; set; } = string.Empty;
    public string BackendInstanceId { get; set; } = string.Empty;
    public string BackendPrincipalId { get; set; } = string.Empty;
    public string? LibraryScopeId { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public FavoriteOperation Operation { get; set; }
    public string SourceRevision { get; set; } = string.Empty;
    public string EventKey { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string PolicySnapshotJson { get; set; } = "{}";
    public Guid? TargetCredentialReferenceId { get; set; }
    public Guid JobId { get; set; }
    public FavoriteEventState State { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class FavoriteActionRecord
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public bool Reversible { get; set; }
    public FavoriteActionState State { get; set; }
    public int AttemptCount { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class FavoriteStateRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Protocol { get; set; } = string.Empty;
    public string BackendInstanceId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    public Guid LastEventId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}
