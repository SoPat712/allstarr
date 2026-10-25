namespace allstarr.Core.Favorites;

public sealed class ProviderFavoriteLinkRecord
{
    public Guid Id { get; set; }
    public string PairKey { get; set; } = "";
    public Guid ConfirmedByUserId { get; set; }
    public int ConfirmationVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid UnionJobId { get; set; }
    public long UnionAttempt { get; set; }
    public string UnionAdministratorFingerprint { get; set; } = "";
    public int Phase { get; set; }
    public DateTimeOffset? CursorAt { get; set; }
    public Guid? CursorId { get; set; }
    public long ProcessedRows { get; set; }
    public long SeededItems { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; }
}
public sealed class ProviderFavoriteLinkMemberRecord
{
    public Guid UserId { get; set; }
    public Guid LinkId { get; set; }
    public string Protocol { get; set; } = "";
    public string BackendInstanceId { get; set; } = "";
    public string BackendPrincipalId { get; set; } = "";
    public string IdentityFingerprint { get; set; } = "";
    public string ConfirmedDisplayName { get; set; } = "";
    public DateTimeOffset? UpperAt { get; set; }
    public Guid? UpperId { get; set; }
}
public sealed class LinkedProviderFavoriteStateRecord
{
    public Guid LinkId { get; set; }
    public string ProviderId { get; set; } = "";
    public string ResourceKind { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public bool IsFavorite { get; set; }
    public long LifecycleVersion { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public Guid? LastIntentEventId { get; set; }
}
public sealed class ProviderFavoriteLinkRequestRecord
{
    public Guid AdministratorUserId { get; set; }
    public Guid RequestId { get; set; }
    public string Operation { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public Guid LinkId { get; set; }
    public Guid JobId { get; set; }
    public bool Created { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
