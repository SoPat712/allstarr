using System.Security.Cryptography;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Downloads;

public sealed class ProviderDownloadArtifactResolver(IProviderDownloadArtifactStore store, ProviderDownloadWorkspaceOptions options)
{
    public async Task<ProviderDownloadWorkspace> CreateWorkspaceAsync(ProviderDownloadWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        if (request.TenantId == Guid.Empty || request.DurableJobId == Guid.Empty || string.IsNullOrWhiteSpace(request.ProviderId) || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Download workspace lineage is incomplete.", nameof(request));
        var workspaceId = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{request.TenantId:N}|{request.DurableJobId:N}|{request.ProviderId}|{request.ProviderAccountId:N}|{request.IdempotencyKey}"))).ToLowerInvariant();
        var root = WorkspaceRoot();
        var directory = Contained(root, workspaceId);
        Directory.CreateDirectory(directory);
        RejectSymlink(directory);
        var entity = await store.CreateWorkspaceAsync(new()
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, TenantId = request.TenantId,
            OwnerUserId = request.OwnerUserId, DurableJobId = request.DurableJobId,
            LibraryScopeId = request.LibraryScopeId,
            ProviderId = request.ProviderId.Trim().ToLowerInvariant(), ProviderAccountId = request.ProviderAccountId,
            IdempotencyKey = request.IdempotencyKey, CreatedAt = DateTimeOffset.UtcNow, Revision = 1
        }, cancellationToken);
        return new(entity.Id, new ProviderManagedWorkspaceReference(entity.WorkspaceId));
    }

    public async Task<VerifiedProviderDownloadArtifact> ResolveAsync(ProviderManagedWorkspaceReference workspace,
        ProviderDownloadedArtifact output, CancellationToken cancellationToken = default)
    {
        var persistedWorkspace = await store.GetWorkspaceAsync(workspace.WorkspaceId, cancellationToken)
            ?? throw new InvalidOperationException("The provider download workspace is not registered.");
        var workspaceRoot = Contained(WorkspaceRoot(), persistedWorkspace.WorkspaceId);
        RejectSymlink(workspaceRoot);
        var relative = NormalizeArtifactReference(output.ArtifactId);
        var path = Contained(workspaceRoot, relative);
        RejectPathSymlinks(workspaceRoot, path);
        if (!File.Exists(path)) throw new InvalidOperationException("The provider download artifact is missing.");
        var info = new FileInfo(path);
        if (info.Length != output.SizeBytes) throw new InvalidOperationException("The provider download artifact length does not match its contract.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(output.Sha256)))
            throw new InvalidOperationException("The provider download artifact checksum does not match its contract.");
        var stored = await store.AddVerifiedAsync(new()
        {
            Id = Guid.CreateVersion7(), WorkspaceRecordId = persistedWorkspace.Id, WorkspaceId = persistedWorkspace.WorkspaceId, TenantId = persistedWorkspace.TenantId,
            OwnerUserId = persistedWorkspace.OwnerUserId, DurableJobId = persistedWorkspace.DurableJobId,
            LibraryScopeId = persistedWorkspace.LibraryScopeId,
            ProviderId = persistedWorkspace.ProviderId, ProviderAccountId = persistedWorkspace.ProviderAccountId,
            ProviderArtifactId = output.ArtifactId, RelativePath = relative, ContentSha256 = hash, Length = info.Length,
            State = ProviderDownloadArtifactState.Verified, CreatedAt = DateTimeOffset.UtcNow,
            VerifiedAt = DateTimeOffset.UtcNow, Revision = 1
        }, cancellationToken);
        return Result(stored, path);
    }

    public async Task<VerifiedProviderDownloadArtifact?> FindByJobAsync(Guid tenantId, Guid jobId, string providerId, CancellationToken cancellationToken = default)
    {
        var item = await store.FindByJobAsync(tenantId, jobId, providerId, cancellationToken);
        if (item is null) return null;
        return Result(item, Contained(Contained(WorkspaceRoot(), item.WorkspaceId), item.RelativePath));
    }

    public Task MarkPlacedAsync(Guid artifactId, Guid managedFileId, CancellationToken cancellationToken = default) =>
        store.MarkPlacedAsync(artifactId, managedFileId, cancellationToken);

    private string WorkspaceRoot()
    {
        if (string.IsNullOrWhiteSpace(options.RootPath)) throw new InvalidOperationException("Download workspace root is not configured.");
        var root = Path.GetFullPath(options.RootPath);
        Directory.CreateDirectory(root);
        RejectSymlink(root);
        return root;
    }

    private static string NormalizeArtifactReference(string artifactId)
    {
        if (string.IsNullOrWhiteSpace(artifactId) || Path.IsPathRooted(artifactId) || artifactId.IndexOf('\0') >= 0)
            throw new InvalidOperationException("The provider artifact reference is invalid.");
        var normalized = artifactId.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
            throw new InvalidOperationException("The provider artifact reference is invalid.");
        return string.Join('/', parts);
    }

    private static string Contained(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The provider artifact escapes its managed workspace.");
        return path;
    }

    private static void RejectSymlink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Provider artifact paths may not traverse symbolic links.");
    }

    private static void RejectPathSymlinks(string root, string path)
    {
        var current = root;
        RejectSymlink(current);
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current)) RejectSymlink(current);
        }
    }

    private static VerifiedProviderDownloadArtifact Result(ProviderDownloadArtifactEntity item, string path) => new(
        item.Id, item.WorkspaceRecordId, path, item.ContentSha256, item.Length, item.TenantId, item.OwnerUserId,
        item.DurableJobId, item.ProviderId, item.ProviderAccountId, item.State, item.ManagedFileId)
        { LibraryScopeId = item.LibraryScopeId };
}
