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
        var compatible = await ownership.FindCompatibleAsync(request.Root.Id, fingerprint, request.ScopeKey, cancellationToken);
        if (compatible is not null && File.Exists(compatible.CanonicalPath))
            return new(await ownership.AddReferenceAsync(compatible.Id, cancellationToken), true);

        var requestedRecord = await ownership.FindByPathAsync(requestedTarget, cancellationToken);
        if (requestedRecord is not null && requestedRecord.ContentSha256 == fingerprint && File.Exists(requestedTarget))
            return new(await ownership.AddReferenceAsync(requestedRecord.Id, cancellationToken), true);

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
            var record = new ManagedFileRecord(Guid.NewGuid(), request.Root.Id, target, fingerprint, length, method,
                request.Root.TenantId, request.Root.OwnerUserId, request.Root.LibraryScopeId, request.SourceJobId,
                request.ScopeKey, 1, true, DateTimeOffset.UtcNow) { TargetRootPath = root };
            try
            {
                return new(await ownership.AddAsync(record, cancellationToken), false);
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
        if (request.SourceIsAllstarrManaged && request.SourceIsImmutable && files.TryCreateHardLink(staging, source))
            return ManagedFilePlacementMethod.HardLink;
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
}
