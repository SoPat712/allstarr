using allstarr.Core.Capabilities;
using System.Text.Json.Serialization;

namespace allstarr.Core.Downloads;

public enum ProviderDownloadArtifactState { Pending, Verified, Placed, Failed }

public sealed class ProviderDownloadWorkspaceEntity
{
    public Guid Id { get; set; }
    public string WorkspaceId { get; set; } = string.Empty;
    public Guid TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string? LibraryScopeId { get; set; }
    public Guid DurableJobId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public Guid? ProviderAccountId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class ProviderDownloadArtifactEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceRecordId { get; set; }
    public string WorkspaceId { get; set; } = string.Empty;
    public Guid TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string? LibraryScopeId { get; set; }
    public Guid DurableJobId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public Guid? ProviderAccountId { get; set; }
    public string ProviderArtifactId { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public long Length { get; set; }
    public string? MimeType { get; set; }
    public string? Container { get; set; }
    public string? Codec { get; set; }
    public int? Bitrate { get; set; }
    public int? SampleRate { get; set; }
    public int? BitDepth { get; set; }
    public int? Channels { get; set; }
    public ProviderDownloadArtifactState State { get; set; }
    public Guid? ManagedFileId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
    public DateTimeOffset? PlacedAt { get; set; }
    public long Revision { get; set; }
}

public sealed record ProviderDownloadWorkspaceRequest(Guid TenantId, Guid? OwnerUserId, Guid DurableJobId,
    string ProviderId, Guid? ProviderAccountId, string IdempotencyKey)
{
    public string? LibraryScopeId { get; init; }
}

public sealed record ProviderDownloadWorkspace(Guid RecordId, ProviderManagedWorkspaceReference Reference);

public sealed record ProviderDownloadArtifactWriteRequest(
    ProviderManagedWorkspaceReference Workspace,
    Guid DurableJobId,
    string ProviderId,
    string ArtifactId,
    Stream Content,
    long MaximumBytes)
{
    public long? ExpectedBytes { get; init; }
    public Action<long, long?>? Progress { get; init; }
}

public sealed record ProviderDownloadArtifactWriteResult(
    string ArtifactId,
    string Sha256,
    long SizeBytes);

public sealed record VerifiedProviderDownloadArtifact(Guid Id, Guid WorkspaceRecordId, [property: JsonIgnore] string SourcePath,
    string ContentSha256, long Length, Guid TenantId, Guid? OwnerUserId, Guid DurableJobId,
    string ProviderId, Guid? ProviderAccountId, ProviderDownloadArtifactState State, Guid? ManagedFileId)
{
    public string? LibraryScopeId { get; init; }
    public string? MimeType { get; init; }
    public string? Container { get; init; }
    public string? Codec { get; init; }
    public int? Bitrate { get; init; }
    public int? SampleRate { get; init; }
    public int? BitDepth { get; init; }
    public int? Channels { get; init; }
}

public sealed class ProviderDownloadWorkspaceOptions
{
    public const string SectionName = "Downloads:Workspace";
    public string RootPath { get; set; } = "./downloads/workspaces";
    public long MaximumArtifactBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}

public interface IProviderDownloadArtifactStore
{
    Task<ProviderDownloadWorkspaceEntity> CreateWorkspaceAsync(ProviderDownloadWorkspaceEntity workspace, CancellationToken cancellationToken);
    Task<ProviderDownloadWorkspaceEntity?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken);
    Task<ProviderDownloadArtifactEntity> AddVerifiedAsync(ProviderDownloadArtifactEntity artifact, CancellationToken cancellationToken);
    Task<ProviderDownloadArtifactEntity?> FindByJobAsync(Guid tenantId, Guid durableJobId, string providerId, CancellationToken cancellationToken);
    Task MarkPlacedAsync(Guid artifactId, Guid managedFileId, CancellationToken cancellationToken);
}
