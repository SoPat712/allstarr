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
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspaceId,
            TenantId = request.TenantId,
            OwnerUserId = request.OwnerUserId,
            DurableJobId = request.DurableJobId,
            LibraryScopeId = request.LibraryScopeId,
            ProviderId = request.ProviderId.Trim().ToLowerInvariant(),
            ProviderAccountId = request.ProviderAccountId,
            IdempotencyKey = request.IdempotencyKey,
            CreatedAt = DateTimeOffset.UtcNow,
            Revision = 1
        }, cancellationToken);
        return new(entity.Id, new ProviderManagedWorkspaceReference(entity.WorkspaceId));
    }

    /// <summary>
    /// Copies provider bytes into a registered private workspace. Providers never receive
    /// a host filesystem path, and the returned contract is derived from bytes the host wrote.
    /// </summary>
    public async Task<ProviderDownloadArtifactWriteResult> WriteAsync(
        ProviderDownloadArtifactWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DurableJobId == Guid.Empty || string.IsNullOrWhiteSpace(request.ProviderId) ||
            request.MaximumBytes < 1 || request.ExpectedBytes is < 0)
            throw new ArgumentException("Download artifact write constraints are invalid.", nameof(request));
        if (request.ExpectedBytes > request.MaximumBytes)
            throw new InvalidDataException("The provider download exceeds the managed artifact size limit.");

        var persistedWorkspace = await store.GetWorkspaceAsync(request.Workspace.WorkspaceId, cancellationToken)
            ?? throw new InvalidOperationException("The provider download workspace is not registered.");
        var providerId = request.ProviderId.Trim().ToLowerInvariant();
        if (persistedWorkspace.DurableJobId != request.DurableJobId ||
            !persistedWorkspace.ProviderId.Equals(providerId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The provider download does not belong to this workspace.");

        var relative = NormalizeArtifactReference(request.ArtifactId);
        var workspaceRoot = Contained(WorkspaceRoot(), persistedWorkspace.WorkspaceId);
        RejectSymlink(workspaceRoot);
        var destination = Contained(workspaceRoot, relative);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        RejectPathSymlinks(workspaceRoot, parent);

        if (File.Exists(destination))
            return await DescribeExistingAsync(relative, destination, request.MaximumBytes, cancellationToken);

        var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[128 * 1024];
                long written = 0;
                while (true)
                {
                    var read = await request.Content.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    written = checked(written + read);
                    if (written > request.MaximumBytes)
                        throw new InvalidDataException("The provider download exceeds the managed artifact size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hasher.AppendData(buffer, 0, read);
                    request.Progress?.Invoke(written, request.ExpectedBytes);
                }
                if (written < 1)
                    throw new InvalidDataException("The provider returned an empty download artifact.");
                if (request.ExpectedBytes.HasValue && written != request.ExpectedBytes.Value)
                    throw new InvalidDataException("The provider download length does not match its response contract.");
                await output.FlushAsync(cancellationToken);
                output.Close();
                File.Move(partial, destination, overwrite: false);
                return new(relative, Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(), written);
            }
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
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
            Id = Guid.CreateVersion7(),
            WorkspaceRecordId = persistedWorkspace.Id,
            WorkspaceId = persistedWorkspace.WorkspaceId,
            TenantId = persistedWorkspace.TenantId,
            OwnerUserId = persistedWorkspace.OwnerUserId,
            DurableJobId = persistedWorkspace.DurableJobId,
            LibraryScopeId = persistedWorkspace.LibraryScopeId,
            ProviderId = persistedWorkspace.ProviderId,
            ProviderAccountId = persistedWorkspace.ProviderAccountId,
            ProviderArtifactId = output.ArtifactId,
            RelativePath = relative,
            ContentSha256 = hash,
            Length = info.Length,
            State = ProviderDownloadArtifactState.Verified,
            CreatedAt = DateTimeOffset.UtcNow,
            VerifiedAt = DateTimeOffset.UtcNow,
            Revision = 1
        }, cancellationToken);
        return Result(stored, path);
    }

    public async Task<VerifiedProviderDownloadArtifact?> FindByJobAsync(Guid tenantId, Guid jobId, string providerId, CancellationToken cancellationToken = default)
    {
        var item = await store.FindByJobAsync(tenantId, jobId, providerId, cancellationToken);
        if (item is null) return null;
        if (item.State != ProviderDownloadArtifactState.Verified)
            return Result(item, Contained(Contained(WorkspaceRoot(), item.WorkspaceId), item.RelativePath));

        var workspaceRoot = Contained(WorkspaceRoot(), item.WorkspaceId);
        RejectSymlink(workspaceRoot);
        var path = Contained(workspaceRoot, NormalizeArtifactReference(item.RelativePath));
        RejectPathSymlinks(workspaceRoot, path);
        if (!File.Exists(path))
            throw new InvalidOperationException("The verified provider download artifact is missing.");
        var info = new FileInfo(path);
        if (info.Length != item.Length)
            throw new InvalidOperationException("The verified provider download artifact length changed.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(item.ContentSha256);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The stored provider download artifact checksum is invalid.", exception);
        }
        if (expected.Length != SHA256.HashSizeInBytes || !CryptographicOperations.FixedTimeEquals(hash, expected))
            throw new InvalidOperationException("The verified provider download artifact content changed.");
        return Result(item, path);
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

    private static async Task<ProviderDownloadArtifactWriteResult> DescribeExistingAsync(
        string relative,
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        RejectSymlink(path);
        var info = new FileInfo(path);
        if (info.Length < 1 || info.Length > maximumBytes)
            throw new InvalidDataException("The existing managed artifact has an invalid size.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new(relative, Convert.ToHexString(hash).ToLowerInvariant(), info.Length);
    }

    private static VerifiedProviderDownloadArtifact Result(ProviderDownloadArtifactEntity item, string path) => new(
        item.Id, item.WorkspaceRecordId, path, item.ContentSha256, item.Length, item.TenantId, item.OwnerUserId,
        item.DurableJobId, item.ProviderId, item.ProviderAccountId, item.State, item.ManagedFileId)
    { LibraryScopeId = item.LibraryScopeId };
}
