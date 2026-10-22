using allstarr.Core.Extensions;
using allstarr.Core.Storage;

namespace allstarr.Services.Common;

internal static class ExtensionPackageIcon
{
    internal const int MaximumBytes = 2 * 1024 * 1024;

    internal static (string Path, string ContentType)? Resolve(ExtensionPackageRecord package)
    {
        try
        {
            var manifest = ExtensionSdkV1.ParseManifest(package.ManifestJson);
            var icon = manifest.IconPath;
            if (string.IsNullOrWhiteSpace(icon))
                icon = new[] { "icon.png", "icon.jpg", "icon.jpeg", "icon.webp" }
                    .FirstOrDefault(candidate => File.Exists(Path.Combine(package.PackagePath, candidate)));
            if (string.IsNullOrWhiteSpace(icon)) return null;
            var root = Path.GetFullPath(package.PackagePath);
            var path = Path.GetFullPath(Path.Combine(root, icon));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
                return null;
            var contentType = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => null
            };
            return contentType == null ? null : (path, contentType);
        }
        catch (ExtensionSdkValidationException)
        {
            return null;
        }
    }

    internal static async Task<MediaAssetSource?> ReadAsync(ExtensionPackageRecord package, CancellationToken cancellationToken)
    {
        if (Resolve(package) is not { } icon) return null;
        try
        {
            await using var source = new FileStream(icon.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length is <= 0 or > MaximumBytes) return null;
            using var output = new MemoryStream((int)source.Length);
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (output.Length + count > MaximumBytes) return null;
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            return output.Length == 0 ? null : new(output.ToArray(), icon.ContentType);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
