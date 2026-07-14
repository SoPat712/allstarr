using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace allstarr.Core.Playlists.Targets;

public enum BackendPlaylistFamily
{
    Jellyfin,
    Subsonic
}

public enum BackendPlaylistWriteMode
{
    Reconcile,
    Recreate
}

public enum BackendPlaylistTargetStatus
{
    Success,
    NotFound,
    Conflict,
    Unsupported,
    Unauthorized,
    BackendFailure,
    Cancelled
}

public sealed record BackendPlaylistTargetCapabilities(
    bool CanCreate,
    bool CanReadMembership,
    bool CanReconcileMembership,
    bool PreservesRequestedOrder,
    bool CanWriteName,
    bool CanWriteDescription,
    bool CanWriteArtwork,
    bool HasNativeRevision,
    bool HasStagedReplacement);

public sealed record BackendPlaylistTargetContext
{
    public BackendPlaylistTargetContext(
        string backendInstanceId,
        string verifiedPrincipalId,
        string? credentialReference = null,
        Guid? tenantId = null)
    {
        BackendInstanceId = Required(backendInstanceId, nameof(backendInstanceId));
        VerifiedPrincipalId = Required(verifiedPrincipalId, nameof(verifiedPrincipalId));
        CredentialReference = string.IsNullOrWhiteSpace(credentialReference) ? null : credentialReference.Trim();
        TenantId = tenantId;
    }

    public string BackendInstanceId { get; }
    public string VerifiedPrincipalId { get; }
    public string? CredentialReference { get; }
    public Guid? TenantId { get; }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();
}

public sealed record BackendPlaylistMember
{
    public BackendPlaylistMember(string backendItemId, string? entryId = null)
    {
        BackendItemId = string.IsNullOrWhiteSpace(backendItemId)
            ? throw new ArgumentException("A backend item ID is required.", nameof(backendItemId))
            : backendItemId;
        EntryId = entryId;
    }

    public string BackendItemId { get; }
    public string? EntryId { get; }
}

/// <summary>
/// Resolves backend authentication just in time. Durable requests retain only a credential reference;
/// the returned values are ephemeral and must never be copied into job state or logs.
/// </summary>
public interface IBackendPlaylistAuthenticationResolver
{
    ValueTask<BackendPlaylistAuthentication> ResolveAsync(
        BackendPlaylistTargetContext context,
        CancellationToken cancellationToken);
}

public sealed record BackendPlaylistAuthentication(
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<KeyValuePair<string, string>> FormParameters)
{
    public static BackendPlaylistAuthentication None { get; } = new(
        new Dictionary<string, string>(), []);
}

public sealed record BackendPlaylistSnapshot(
    string BackendPlaylistId,
    string Name,
    IReadOnlyList<BackendPlaylistMember> Members,
    string Fingerprint,
    string? NativeRevision = null,
    string? Description = null,
    string? ArtworkReference = null)
{
    public static string ComputeFingerprint(
        string playlistId,
        string name,
        IEnumerable<BackendPlaylistMember> members,
        string? description = null,
        string? artworkReference = null)
    {
        var canonical = string.Join('\n', new[] { playlistId, name, description ?? "", artworkReference ?? "" }
            .Concat(members.Select(member => $"{member.BackendItemId}\u001f{member.EntryId}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed record BackendPlaylistMetadata(
    string Name,
    string? Description = null,
    byte[]? Artwork = null,
    string? ArtworkContentType = null);

public sealed record BackendPlaylistWriteRequest
{
    public BackendPlaylistWriteRequest(
        BackendPlaylistWriteMode mode,
        BackendPlaylistMetadata metadata,
        IEnumerable<string> orderedBackendItemIds,
        string idempotencyKey,
        string? backendPlaylistId = null,
        string? expectedRevision = null,
        string? expectedFingerprint = null,
        IEnumerable<string>? syncOwnedBackendItemIds = null,
        bool removeStaleSyncOwnedItems = false,
        string? recoveryPlaylistId = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        if (string.IsNullOrWhiteSpace(metadata.Name)) throw new ArgumentException("A playlist name is required.", nameof(metadata));
        Mode = mode;
        OrderedBackendItemIds = orderedBackendItemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
            ? throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey))
            : idempotencyKey;
        BackendPlaylistId = backendPlaylistId;
        ExpectedRevision = expectedRevision;
        ExpectedFingerprint = expectedFingerprint;
        SyncOwnedBackendItemIds = (syncOwnedBackendItemIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        RemoveStaleSyncOwnedItems = removeStaleSyncOwnedItems;
        RecoveryPlaylistId = string.IsNullOrWhiteSpace(recoveryPlaylistId) ? null : recoveryPlaylistId;
    }

    public BackendPlaylistWriteMode Mode { get; }
    public BackendPlaylistMetadata Metadata { get; }
    public IReadOnlyList<string> OrderedBackendItemIds { get; }
    public string IdempotencyKey { get; }
    public string? BackendPlaylistId { get; }
    public string? ExpectedRevision { get; }
    public string? ExpectedFingerprint { get; }
    public IReadOnlyList<string> SyncOwnedBackendItemIds { get; }
    public bool RemoveStaleSyncOwnedItems { get; }
    public string? RecoveryPlaylistId { get; }
}

public sealed record BackendPlaylistTargetResult<T>(
    BackendPlaylistTargetStatus Status,
    T? Value = default,
    HttpStatusCode? UpstreamStatus = null,
    string? ErrorCode = null,
    string? RecoveryPlaylistId = null)
{
    public bool IsSuccess => Status == BackendPlaylistTargetStatus.Success;
}

public sealed record BackendPlaylistWriteReceipt(
    BackendPlaylistSnapshot Snapshot,
    bool Changed,
    IReadOnlyList<string> UnsupportedMetadataFields,
    bool ReplacementRequiresCleanup = false,
    string? ReplacedPlaylistId = null);

public interface IBackendPlaylistTarget
{
    BackendPlaylistFamily Family { get; }
    BackendPlaylistTargetCapabilities Capabilities { get; }

    Task<BackendPlaylistTargetResult<BackendPlaylistSnapshot?>> FindByNameAsync(
        BackendPlaylistTargetContext context,
        string name,
        CancellationToken cancellationToken);

    Task<BackendPlaylistTargetResult<BackendPlaylistSnapshot>> ReadAsync(
        BackendPlaylistTargetContext context,
        string backendPlaylistId,
        CancellationToken cancellationToken);

    Task<BackendPlaylistTargetResult<BackendPlaylistWriteReceipt>> WriteAsync(
        BackendPlaylistTargetContext context,
        BackendPlaylistWriteRequest request,
        CancellationToken cancellationToken);
}
