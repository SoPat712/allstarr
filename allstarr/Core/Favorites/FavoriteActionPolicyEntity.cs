namespace allstarr.Core.Favorites;

public enum FavoriteActionPolicyScope { Global, User }

public sealed class FavoriteActionPolicyRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public FavoriteActionPolicyScope Scope { get; set; }
    public string Protocol { get; set; } = string.Empty;
    public string BackendInstanceId { get; set; } = string.Empty;
    public string? LibraryScopeId { get; set; }
    public bool? AddToVirtualLiked { get; set; }
    public bool? MatchLocalLibrary { get; set; }
    public bool? AutoDownload { get; set; }
    public bool? EnrichMetadata { get; set; }
    public bool? PlaceManagedFile { get; set; }
    public bool? RefreshBackendLibrary { get; set; }
    public Guid? TargetCredentialReferenceId { get; set; }
    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}
