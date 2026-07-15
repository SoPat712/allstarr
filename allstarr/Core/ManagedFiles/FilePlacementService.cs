using System.Security.Cryptography;

namespace allstarr.Core.ManagedFiles;

public sealed class FilePlacementService(IManagedFileOwnershipStore ownership, IManagedFileOperations files)
{
    public async Task<ManagedFilePlacementResult> PlaceAsync(ManagedFilePlacementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Root.TenantId is null)
            throw new ArgumentException("Managed files require explicit tenant ownership.", nameof(request));
        var root = ValidateRoot(request.Root.CanonicalPath);
        var source = Path.GetFullPath(request.SourcePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("The placement source does not exist.", source);
        RejectSymlinkLeaf(source);

        var relative = ManagedPathTemplate.Render(request.PathTemplate, request.Track);
        var requestedTarget = ContainedPath(root, relative);
        var fingerprint = await FingerprintAsync(source, cancellationToken);
        var length = new FileInfo(source).Length;
        ValidateExpectedSource(request, fingerprint, length);
        var referenceKey = ResolveReferenceKey(request);
        var compatible = await ownership.FindCompatibleAsync(request.Root.Id, fingerprint, request.ScopeKey, cancellationToken);
        if (compatible is not null && File.Exists(compatible.CanonicalPath))
        {
            ValidateCompatibleOwnership(request, compatible);
            await ValidateExistingManagedFileAsync(root, compatible, fingerprint, length, cancellationToken);
            return new(await ownership.AddReferenceAsync(compatible.Id,
                CreateReference(request, compatible.Id, referenceKey), cancellationToken), true);
        }

        var requestedRecord = await ownership.FindByPathAsync(requestedTarget, cancellationToken);
        if (requestedRecord is not null && requestedRecord.ContentSha256 == fingerprint && File.Exists(requestedTarget))
        {
            ValidateCompatibleOwnership(request, requestedRecord);
            await ValidateExistingManagedFileAsync(root, requestedRecord, fingerprint, length, cancellationToken);
            return new(await ownership.AddReferenceAsync(requestedRecord.Id,
                CreateReference(request, requestedRecord.Id, referenceKey), cancellationToken), true);
        }

        Directory.CreateDirectory(root);
        RejectSymlinkLeaf(root);
        var target = await ResolveCollisionAsync(requestedTarget, fingerprint, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        RejectSymlinksUnder(root, Path.GetDirectoryName(target)!);

        var stagingDirectory = ContainedPath(root, ".allstarr-staging");
        Directory.CreateDirectory(stagingDirectory);
        RejectSymlinksUnder(root, stagingDirectory);
        var staging = Path.Combine(stagingDirectory, $"{Guid.NewGuid():N}.partial");
        ManagedFilePlacementMethod method;
        var finalized = false;
        try
        {
            method = await MaterializeAsync(request, source, staging, cancellationToken);
            var stagedFingerprint = await FingerprintAsync(staging, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(fingerprint), Convert.FromHexString(stagedFingerprint)))
                throw new IOException("The staged managed file failed content verification.");

            RejectSymlinksUnder(root, Path.GetDirectoryName(target)!);
            files.MoveNoReplace(staging, target);
            finalized = true;
            var identity = files.TryGetFileIdentity(target, out var currentIdentity) ? currentIdentity : null;
            var record = new ManagedFileRecord(Guid.NewGuid(), request.Root.Id, target, fingerprint, length, method,
                request.Root.TenantId, request.Root.OwnerUserId, request.Root.LibraryScopeId, request.SourceJobId,
                request.ScopeKey, 1, true, DateTimeOffset.UtcNow)
            {
                TargetRootPath = root,
                FileSystemDeviceId = identity?.DeviceId,
                FileSystemFileId = identity?.FileId,
                FileSystemLinkCount = identity?.LinkCount
            };
            try
            {
                return new(await ownership.AddAsync(record, CreateReference(request, record.Id, referenceKey), cancellationToken), false);
            }
            catch
            {
                File.Delete(target);
                finalized = false;
                throw;
            }
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
            // A finalized file is removed only above when its ownership record failed.
            _ = finalized;
        }
    }

    private async Task<ManagedFilePlacementMethod> MaterializeAsync(ManagedFilePlacementRequest request, string source, string staging, CancellationToken cancellationToken)
    {
        // Hardlinks remain disabled until immutability is represented by a durable
        // lease. A caller boolean cannot prevent later tagging of the shared inode.
        if (files.TryCreateReflink(staging, source))
            return ManagedFilePlacementMethod.Reflink;
        await files.CopyAsync(source, staging, cancellationToken);
        return ManagedFilePlacementMethod.Copy;
    }

    private async Task<string> ResolveCollisionAsync(string requested, string fingerprint, CancellationToken cancellationToken)
    {
        if (!File.Exists(requested) && !Directory.Exists(requested)) return requested;
        var directory = Path.GetDirectoryName(requested)!;
        var stem = Path.GetFileNameWithoutExtension(requested);
        var extension = Path.GetExtension(requested);
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var suffix = attempt == 0 ? fingerprint[..12] : $"{fingerprint[..12]}-{attempt}";
            var candidate = Path.Combine(directory, $"{stem} [{suffix}]{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Unable to resolve a safe managed-file name collision.");
    }

    private static string ValidateRoot(string configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot) || !Path.IsPathRooted(configuredRoot))
            throw new ArgumentException("A managed-file root must be an absolute path.", nameof(configuredRoot));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        if (Directory.Exists(root)) RejectSymlinkLeaf(root);
        return root;
    }

    private static string ContainedPath(string root, string relative)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The managed-file path escapes its configured root.");
        return candidate;
    }

    private static void RejectSymlinkLeaf(string path)
    {
        var full = Path.GetFullPath(path);
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Managed-file paths may not traverse symbolic links.");
    }

    private static void RejectSymlinksUnder(string root, string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, full);
        if (relative == ".")
        {
            RejectSymlinkLeaf(root);
            return;
        }
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        RejectSymlinkLeaf(current);
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Managed-file paths may not traverse symbolic links.");
        }
    }

    private static async Task<string> FingerprintAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static void ValidateExpectedSource(ManagedFilePlacementRequest request, string fingerprint, long length)
    {
        if (request.ExpectedLength.HasValue && request.ExpectedLength.Value != length)
            throw new IOException("The placement source length no longer matches its verified artifact.");
        if (request.ExpectedContentSha256 is null) return;

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(request.ExpectedContentSha256);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The expected placement checksum is invalid.", exception);
        }

        if (expected.Length != SHA256.HashSizeInBytes ||
            !CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(fingerprint)))
            throw new IOException("The placement source no longer matches its verified artifact.");
    }

    private async Task ValidateExistingManagedFileAsync(
        string root,
        ManagedFileRecord record,
        string expectedFingerprint,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(record.CanonicalPath);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The existing managed file is outside its recorded root.");
        RejectSymlinksUnder(root, path);
        var info = new FileInfo(path);
        if (info.Length != record.Length || info.Length != expectedLength)
            throw new IOException("The existing managed file length no longer matches its ownership record.");
        if (!string.IsNullOrWhiteSpace(record.FileSystemDeviceId) &&
            !string.IsNullOrWhiteSpace(record.FileSystemFileId) &&
            (!files.TryGetFileIdentity(path, out var identity) ||
             !StringComparer.Ordinal.Equals(record.FileSystemDeviceId, identity.DeviceId) ||
             !StringComparer.Ordinal.Equals(record.FileSystemFileId, identity.FileId)))
            throw new IOException("The existing managed file identity no longer matches its ownership record.");
        var actual = await FingerprintAsync(path, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedFingerprint), Convert.FromHexString(actual)) ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(record.ContentSha256), Convert.FromHexString(actual)))
            throw new IOException("The existing managed file content no longer matches its ownership record.");
    }

    private static string ResolveReferenceKey(ManagedFilePlacementRequest request)
    {
        var value = string.IsNullOrWhiteSpace(request.ReferenceKey)
            ? request.SourceJobId?.ToString("N") ?? Guid.NewGuid().ToString("N")
            : request.ReferenceKey.Trim();
        if (value.Length > 1000)
            throw new ArgumentException("Managed-file reference keys cannot exceed 1000 characters.", nameof(request));
        return value;
    }

    private static ManagedFileReference CreateReference(
        ManagedFilePlacementRequest request,
        Guid managedFileId,
        string referenceKey) => new(
        Guid.NewGuid(), managedFileId, request.Root.TenantId!.Value, request.Root.OwnerUserId,
        request.ScopeKey, referenceKey, DateTimeOffset.UtcNow);

    private static void ValidateCompatibleOwnership(ManagedFilePlacementRequest request, ManagedFileRecord record)
    {
        if (record.TenantId != request.Root.TenantId || record.OwnerUserId != request.Root.OwnerUserId ||
            !StringComparer.Ordinal.Equals(record.LibraryScopeId, request.Root.LibraryScopeId) ||
            !StringComparer.Ordinal.Equals(record.ScopeKey, request.ScopeKey))
            throw new UnauthorizedAccessException("The existing managed file is outside the requested ownership scope.");
    }
}
