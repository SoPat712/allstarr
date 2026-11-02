using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace allstarr.Services.Common;

internal sealed record ProviderIconAsset(IFileInfo File, string ContentType);

internal static class ProviderArtworkResolver
{
    internal static Task<ResolvedMediaAsset?> ResolveCoverAsync(IServiceProvider services,
        IMediaAssetResolver assets, string provider, int dimension, CancellationToken cancellationToken) =>
        assets.ResolveAsync(new(null, null, provider, "provider-cover", provider, Width: dimension, Height: dimension),
            token => ProviderArtworkFallback.ReadAsync(services, provider, dimension, token),
            2 * 1024 * 1024, cancellationToken);

    internal static async Task<ProviderIconAsset?> ResolveIconAsync(IServiceProvider services,
        string providerId, CancellationToken cancellationToken) =>
        (await ResolveIconsAsync(services, [providerId], cancellationToken)).GetValueOrDefault(providerId);

    internal static async Task<IReadOnlyDictionary<string, ProviderIconAsset?>> ResolveIconsAsync(IServiceProvider services,
        IReadOnlyCollection<string> providerIds, CancellationToken cancellationToken)
    {
        var registry = services.GetRequiredService<IProviderRegistry>();
        var descriptors = new Dictionary<string, ProviderDescriptor>(StringComparer.Ordinal);
        foreach (var providerId in providerIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (registry.TryGet(providerId, out var descriptor) && descriptor != null) descriptors[providerId] = descriptor;
            }
            catch (ArgumentException) { }
        }
        var packages = descriptors.Values.Any(item => item.Origin == ProviderOrigin.Extension) &&
            services.GetService<ExtensionControlPlaneService>() is { } extensions
            ? await extensions.ListPackagesAsync(cancellationToken: cancellationToken) : [];
        var result = new Dictionary<string, ProviderIconAsset?>(StringComparer.Ordinal);
        foreach (var (id, descriptor) in descriptors)
        {
            if (descriptor.Origin != ProviderOrigin.Extension) result[id] = ResolveBuiltInIcon(services, descriptor);
            else
            {
                var package = packages.FirstOrDefault(item => item.ExtensionId == descriptor.Id && item.State == allstarr.Core.Storage.ExtensionPackageState.Active);
                result[id] = package != null && ExtensionPackageIcon.Resolve(package) is { } icon
                    ? new(new PhysicalFileInfo(new FileInfo(icon.Path)), icon.ContentType) : null;
            }
        }
        return result;
    }

    private static ProviderIconAsset? ResolveBuiltInIcon(IServiceProvider services, ProviderDescriptor descriptor)
    {
        var reference = descriptor.Branding?.LogoReference.Replace('\\', '/');
        if (reference != null && Uri.TryCreate(reference, UriKind.Absolute, out _)) return null;
        var path = reference != null && Path.HasExtension(reference)
            ? reference
            : "images/providers/" + BundledIcon(reference ?? descriptor.Id);
        if (!new FileExtensionContentTypeProvider().TryGetContentType(path, out var contentType) ||
            !contentType.StartsWith("image/", StringComparison.Ordinal))
            return null;
        var environment = services.GetRequiredService<IWebHostEnvironment>();
        var file = environment.WebRootFileProvider.GetFileInfo(path);
        if (!file.Exists) file = environment.ContentRootFileProvider.GetFileInfo("wwwroot/" + path);
        return file.Exists && !file.IsDirectory ? new(file, contentType) : null;
    }

    internal static async Task<ProviderIconAsset?> ResolveExtensionIconAsync(ExtensionControlPlaneService extensions,
        string providerId, CancellationToken cancellationToken)
    {
        var extension = (await extensions.ListPackagesAsync(providerId, cancellationToken))
            .FirstOrDefault(item => item.State == allstarr.Core.Storage.ExtensionPackageState.Active);
        if (extension == null || ExtensionPackageIcon.Resolve(extension) is not { } icon) return null;
        return new(new PhysicalFileInfo(new FileInfo(icon.Path)), icon.ContentType);
    }

    private static string BundledIcon(string provider)
    {
        var plain = string.Concat(provider.Replace("spotiflac-", "", StringComparison.Ordinal).Where(char.IsAsciiLetterOrDigit));
        return plain switch
        {
            "amazon" => "amazonmusic.svg",
            "apple" or "applemusickit" or "appledownload" or "applemusicdownload" => "applemusic.svg",
            "spotifyweb" => "spotify.svg",
            "tidalweb" => "tidal.svg",
            "qobuzweb" => "qobuz.svg",
            "qobuz" => "qobuz.webp",
            "spicylyrics" => "spicylyrics.ico",
            "ytmusic" or "ytmusicspotiflac" or "youtube" => "youtubemusic.svg",
            _ => plain + ".svg"
        };
    }
}
