using System.IO.Compression;
using System.Security.Cryptography;

namespace allstarr.Core.Extensions;

public sealed record FirstPartyExtensionArchive(
    string Path,
    string Sha256,
    string ContentSha256,
    ExtensionSdkManifest Manifest);

public static class FirstPartyExtensionPackages
{
    private static readonly DateTimeOffset StableTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static FirstPartyExtensionArchive Build(string packageRoot, string archivePath)
    {
        var root = Path.GetFullPath(packageRoot);
        var manifestPath = Path.Combine(root, "manifest.json");
        if (!Directory.Exists(root) || !File.Exists(manifestPath))
            throw new ExtensionSdkValidationException("First-party package source is incomplete.");
        var manifest = ExtensionSdkV1.ParseManifest(File.ReadAllText(manifestPath));
        var contentSha256 = ExtensionSdkV1.ComputePackageContentSha256(root);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archivePath))!);
        using (var output = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Relative, CompressionLevel.NoCompression);
                entry.LastWriteTime = StableTimestamp;
                entry.ExternalAttributes = Convert.ToInt32("100644", 8) << 16;
                using var source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var target = entry.Open();
                source.CopyTo(target);
            }
        }
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new(archivePath, sha256, contentSha256, manifest);
    }
}
