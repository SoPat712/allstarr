using System.Security.Cryptography;
using System.Collections.Concurrent;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Downloads;

public sealed class ProviderDownloadArtifactResolver(IProviderDownloadArtifactStore store, ProviderDownloadWorkspaceOptions options)
{
    private readonly ConcurrentDictionary<string, ProviderDownloadWorkspaceRequest> transientWorkspaces = new(StringComparer.Ordinal);

    public ProviderTransientDownloadWorkspace CreateTransientWorkspace(ProviderDownloadWorkspaceRequest request)
    {
        if (request.TenantId == Guid.Empty || request.DurableJobId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.ProviderId) || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Download workspace lineage is incomplete.", nameof(request));

        var workspaceId = $"transient-{Guid.NewGuid():N}";
        if (!transientWorkspaces.TryAdd(workspaceId, request))
            throw new InvalidOperationException("Could not reserve a transient provider workspace.");
        var directory = Contained(WorkspaceRoot(), workspaceId);
        try
        {
            Directory.CreateDirectory(directory);
            RejectSymlink(directory);
            return new(new ProviderManagedWorkspaceReference(workspaceId), request.DurableJobId);
        }
        catch
        {
            transientWorkspaces.TryRemove(workspaceId, out _);
            throw;
        }
    }
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
        CancellationToken cancellationToken = default) =>
        await WriteProducedAsync(new(
            request.Workspace,
            request.DurableJobId,
            request.ProviderId,
            request.ArtifactId,
            request.MaximumBytes,
            (output, token) => request.Content.CopyToAsync(output, token))
        {
            ExpectedBytes = request.ExpectedBytes,
            Progress = request.Progress
        }, cancellationToken);

    public async Task<ProviderDownloadArtifactWriteResult> WriteProducedAsync(
        ProviderDownloadArtifactProduceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Produce);
        if (request.DurableJobId == Guid.Empty || string.IsNullOrWhiteSpace(request.ProviderId) ||
            request.MaximumBytes < 1 || request.ExpectedBytes is < 0)
            throw new ArgumentException("Download artifact write constraints are invalid.", nameof(request));
        if (request.ExpectedBytes > request.MaximumBytes)
            throw new InvalidDataException("The provider download exceeds the managed artifact size limit.");

        var providerId = request.ProviderId.Trim().ToLowerInvariant();
        var persistedWorkspace = await store.GetWorkspaceAsync(request.Workspace.WorkspaceId, cancellationToken);
        var transientWorkspace = persistedWorkspace == null &&
                                 transientWorkspaces.TryGetValue(request.Workspace.WorkspaceId, out var transient)
            ? transient
            : null;
        if (persistedWorkspace == null && transientWorkspace == null)
            throw new InvalidOperationException("The provider download workspace is not registered.");
        if ((persistedWorkspace?.DurableJobId ?? transientWorkspace!.DurableJobId) != request.DurableJobId ||
            !(persistedWorkspace?.ProviderId ?? transientWorkspace!.ProviderId)
                .Equals(providerId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The provider download does not belong to this workspace.");

        var relative = NormalizeArtifactReference(request.ArtifactId);
        var workspaceRoot = Contained(WorkspaceRoot(), request.Workspace.WorkspaceId);
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
            long written;
            string hash;
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var bounded = new BoundedHashingWriteStream(
                    output, hasher, request.MaximumBytes, request.ExpectedBytes, request.Progress);
                await request.Produce(bounded, cancellationToken);
                written = bounded.BytesWritten;
                if (written < 1)
                    throw new InvalidDataException("The provider returned an empty download artifact.");
                if (request.ExpectedBytes.HasValue && written != request.ExpectedBytes.Value)
                    throw new InvalidDataException("The provider download length does not match its response contract.");
                await bounded.FlushAsync(cancellationToken);
                hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }
            File.Move(partial, destination, overwrite: false);
            return new(relative, hash, written);
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
            MimeType = output.Media.MimeType,
            Container = output.Media.Container,
            Codec = output.Media.Codec,
            Bitrate = output.Media.Bitrate,
            SampleRate = output.Media.SampleRate,
            BitDepth = output.Media.BitDepth,
            Channels = output.Media.Channels,
            State = ProviderDownloadArtifactState.Verified,
            CreatedAt = DateTimeOffset.UtcNow,
            VerifiedAt = DateTimeOffset.UtcNow,
            Revision = 1
        }, cancellationToken);
        return Result(stored, path);
    }

    public async Task<ProviderTransientDownloadArtifact> ResolveTransientAsync(
        ProviderTransientDownloadWorkspace workspace,
        ProviderDownloadedArtifact output,
        CancellationToken cancellationToken = default)
    {
        if (!transientWorkspaces.ContainsKey(workspace.Reference.WorkspaceId))
            throw new InvalidOperationException("The transient provider workspace is not registered.");
        var workspaceRoot = Contained(WorkspaceRoot(), workspace.Reference.WorkspaceId);
        RejectSymlink(workspaceRoot);
        var path = Contained(workspaceRoot, NormalizeArtifactReference(output.ArtifactId));
        RejectPathSymlinks(workspaceRoot, path);
        if (!File.Exists(path)) throw new InvalidOperationException("The provider download artifact is missing.");
        var info = new FileInfo(path);
        if (info.Length != output.SizeBytes)
            throw new InvalidOperationException("The provider download artifact length does not match its contract.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        byte[] expected;
        try { expected = Convert.FromHexString(output.Sha256); }
        catch (FormatException exception)
        { throw new InvalidOperationException("The provider download artifact checksum is invalid.", exception); }
        if (expected.Length != SHA256.HashSizeInBytes || !CryptographicOperations.FixedTimeEquals(hash, expected))
            throw new InvalidOperationException("The provider download artifact checksum does not match its contract.");
        return new(path, info.Length, output.Media);
    }

    public void DeleteTransientWorkspace(ProviderTransientDownloadWorkspace workspace)
    {
        if (!transientWorkspaces.TryRemove(workspace.Reference.WorkspaceId, out _)) return;
        var directory = Contained(WorkspaceRoot(), workspace.Reference.WorkspaceId);
        if (!Directory.Exists(directory)) return;
        RejectSymlink(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
            RejectSymlink(path);
        Directory.Delete(directory, recursive: true);
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
    {
        LibraryScopeId = item.LibraryScopeId,
        MimeType = item.MimeType,
        Container = item.Container,
        Codec = item.Codec,
        Bitrate = item.Bitrate,
        SampleRate = item.SampleRate,
        BitDepth = item.BitDepth,
        Channels = item.Channels
    };

    private sealed class BoundedHashingWriteStream(
        Stream output,
        IncrementalHash hasher,
        long maximumBytes,
        long? expectedBytes,
        Action<long, long?>? progress) : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Validate(buffer.Length);
            output.Write(buffer);
            hasher.AppendData(buffer);
            Complete(buffer.Length);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Validate(buffer.Length);
            await output.WriteAsync(buffer, cancellationToken);
            hasher.AppendData(buffer.Span);
            Complete(buffer.Length);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            output.FlushAsync(cancellationToken);
        public override void Flush() => output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void Validate(int count)
        {
            if (count < 0 || BytesWritten > maximumBytes - count)
                throw new InvalidDataException("The provider download exceeds the managed artifact size limit.");
        }

        private void Complete(int count)
        {
            BytesWritten += count;
            progress?.Invoke(BytesWritten, expectedBytes);
        }
    }
}
