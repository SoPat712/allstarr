using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Extensions;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace allstarr.Services.Common;

internal sealed record ProviderIconAsset(IFileInfo File, string ContentType, string Revision = "");

internal sealed record ProviderIconVariant(ProviderIconAsset? Svg, ProviderIconAsset? Png, ProviderIconAsset? Preferred = null)
{
    internal ProviderIconAsset? Select(string? format) => format switch
    {
        "svg" => Svg,
        "png" => Png,
        _ => Preferred ?? Svg ?? Png
    };
}

internal sealed record ProviderArtwork(ProviderIconVariant Color, ProviderIconVariant Glyph)
{
    internal ItemDetailsProvider Provider(string id, string name)
    {
        var url = $"/allstarr/v1/providers/{Uri.EscapeDataString(id)}/icon";
        return new(id, name, Color.Select(null) == null ? null : url,
            Glyph.Select(null) == null ? null : url + "?variant=glyph");
    }
}

internal static class ProviderArtworkResolver
{
    private static readonly object RequestCacheKey = new();
    private static readonly IReadOnlyDictionary<string, string> Brands = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["apple-musickit"] = "applemusic",
        ["apple-download"] = "applemusic",
        ["spotiflac-apple-music"] = "applemusic",
        ["spotify"] = "spotify",
        ["spotiflac-spotify-web"] = "spotify",
        ["deezer"] = "deezer",
        ["spotiflac-deezer"] = "deezer",
        ["spotiflac-ytmusic-spotiflac"] = "youtubemusic",
        ["youtube"] = "youtubemusic",
        ["ytmusic"] = "youtubemusic",
        ["spotiflac-tidal-web"] = "tidal",
        ["tidal"] = "tidal",
        ["spotiflac-soundcloud"] = "soundcloud",
        ["spotiflac-amazon"] = "amazonmusic",
        ["amazon"] = "amazonmusic",
        ["spotiflac-qobuz-web"] = "qobuz",
        ["qobuz"] = "qobuz",
        ["spotiflac-jiosaavn"] = "jiosaavn",
        ["spotiflac-melon-music"] = "melon",
        ["spotiflac-genie-music"] = "genie",
        ["spotiflac-bugs-music"] = "bugs",
        ["multi-scrobbler"] = "multiscrobbler",
        ["jellyfin"] = "jellyfin",
        ["navidrome"] = "navidrome",
        ["subsonic"] = "subsonic",
        ["opensubsonic"] = "subsonic",
        ["local"] = "jellyfin",
        ["native"] = "jellyfin",
        ["lastfm"] = "lastfm",
        ["listenbrainz"] = "listenbrainz",
        ["musicbrainz"] = "musicbrainz",
        ["musicbrainz-enrichment"] = "musicbrainz",
        ["genius"] = "genius",
        ["musixmatch"] = "musixmatch",
        ["lrclib"] = "lrclib",
        ["lrcred"] = "lrcred",
        ["lyricsplus"] = "lyricsplus",
        ["unison"] = "unison",
        ["paxsenix"] = "paxsenix",
        ["spicylyrics"] = "spicylyrics",
        ["monochrome"] = "monochrome",
        ["artistgrid"] = "artistgrid",
        ["koito"] = "koito",
        ["allstarr"] = "allstarr",
    };

    internal static string Brand(string id) => Brands.GetValueOrDefault(id) ??
        (id.StartsWith("spotiflac-", StringComparison.Ordinal) ? id[10..] : id);

    internal static Task<ResolvedMediaAsset?> ResolveCoverAsync(IServiceProvider services,
        IMediaAssetResolver assets, string provider, int dimension, CancellationToken cancellationToken) =>
        assets.ResolveAsync(new(null, null, provider, "provider-cover", provider, Width: dimension, Height: dimension),
            token => ProviderArtworkFallback.ReadAsync(services, provider, dimension, token),
            2 * 1024 * 1024, cancellationToken);

    internal static async Task<ProviderIconAsset?> ResolveIconAsync(IServiceProvider services,
        string providerId, CancellationToken cancellationToken, string variant = "color", string? format = null)
    {
        var artwork = (await ResolveIconsAsync(services, [providerId], cancellationToken)).GetValueOrDefault(providerId);
        return (variant == "glyph" ? artwork?.Glyph : artwork?.Color)?.Select(format);
    }

    internal static async Task<IReadOnlyDictionary<string, ProviderArtwork?>> ResolveIconsAsync(IServiceProvider services,
        IReadOnlyCollection<string> providerIds, CancellationToken cancellationToken)
    {
        var http = services.GetService<IHttpContextAccessor>()?.HttpContext;
        var result = http?.Items.TryGetValue(RequestCacheKey, out var cached) == true
            ? (Dictionary<string, ProviderArtwork?>)cached! : new(StringComparer.Ordinal);
        if (http != null) http.Items[RequestCacheKey] = result;
        var registry = services.GetRequiredService<IProviderRegistry>();
        var environment = services.GetRequiredService<IWebHostEnvironment>();
        var descriptors = new Dictionary<string, ProviderDescriptor>(StringComparer.Ordinal);
        foreach (var providerId in providerIds.Distinct(StringComparer.Ordinal).Where(id => !result.ContainsKey(id)))
        {
            result[providerId] = null;
            try
            {
                if (registry.TryGet(providerId, out var descriptor) && descriptor != null) descriptors[providerId] = descriptor;
                else if (Brands.ContainsKey(providerId)) result[providerId] = ResolveArtwork(environment, providerId, null, false, null);
            }
            catch (ArgumentException) { }
        }
        var packages = descriptors.Values.Any(item => item.Origin == ProviderOrigin.Extension) &&
            services.GetService<ExtensionControlPlaneService>() is { } extensions
            ? await extensions.ListPackagesAsync(cancellationToken: cancellationToken) : [];
        foreach (var (id, descriptor) in descriptors)
        {
            var package = descriptor.Origin == ProviderOrigin.Extension
                ? packages.FirstOrDefault(item => item.ExtensionId == descriptor.Id && item.State == ExtensionPackageState.Active) : null;
            result[id] = ResolveArtwork(environment, descriptor, package);
        }
        return result;
    }

    internal static ProviderArtwork ResolveArtwork(IWebHostEnvironment environment, ProviderDescriptor descriptor,
        ExtensionPackageRecord? package) => ResolveArtwork(environment, descriptor.Id,
            descriptor.Branding?.LogoReference, descriptor.Origin == ProviderOrigin.Extension, package);

    private static ProviderArtwork ResolveArtwork(IWebHostEnvironment environment, string providerId,
        string? logoReference, bool extension, ExtensionPackageRecord? package)
    {
        ProviderIconAsset? FileAsset(string path)
        {
            if (!new FileExtensionContentTypeProvider().TryGetContentType(path, out var type) ||
                !type.StartsWith("image/", StringComparison.Ordinal)) return null;
            var file = environment.WebRootFileProvider.GetFileInfo(path);
            if (!file.Exists) file = environment.ContentRootFileProvider.GetFileInfo("wwwroot/" + path);
            return file.Exists && !file.IsDirectory ? new(file, type, path) : null;
        }
        ProviderIconAsset? PackageAsset(bool glyph)
        {
            if (package == null || ExtensionPackageIcon.Resolve(package, glyph) is not { } icon) return null;
            return new(new PhysicalFileInfo(new FileInfo(icon.Path)), icon.ContentType,
                $"{package.Id}:{package.Revision}:{package.ContentSha256}");
        }
        var brand = Brand(providerId);
        ProviderIconVariant Variant(string variant, ProviderIconAsset? packageIcon, ProviderIconAsset? legacyIcon = null)
        {
            var prefix = $"images/providers/brands/{brand}/{variant}";
            var svg = FileAsset(prefix + ".svg");
            var png = FileAsset(prefix + ".png");
            return new(svg ?? (packageIcon?.ContentType == "image/svg+xml" ? packageIcon : null) ??
                (legacyIcon?.ContentType == "image/svg+xml" ? legacyIcon : null),
                png ?? (packageIcon?.ContentType != "image/svg+xml" ? packageIcon : null) ??
                (legacyIcon?.ContentType != "image/svg+xml" ? legacyIcon : null),
                svg ?? png ?? packageIcon ?? legacyIcon);
        }
        var reference = logoReference?.Replace('\\', '/');
        var legacy = reference != null && Uri.TryCreate(reference, UriKind.Absolute, out _) ? null :
            FileAsset(reference != null && Path.HasExtension(reference) && !extension
                ? reference : "images/providers/" + LegacyIcon(reference != null && !extension ? reference : providerId));
        return new(Variant("color", PackageAsset(false), legacy), Variant("glyph", PackageAsset(true)));
    }

    internal static async Task<ProviderIconAsset?> ResolveExtensionIconAsync(ExtensionControlPlaneService extensions,
        string providerId, CancellationToken cancellationToken)
    {
        var extension = (await extensions.ListPackagesAsync(providerId, cancellationToken))
            .FirstOrDefault(item => item.State == allstarr.Core.Storage.ExtensionPackageState.Active);
        if (extension == null || ExtensionPackageIcon.Resolve(extension) is not { } icon) return null;
        return new(new PhysicalFileInfo(new FileInfo(icon.Path)), icon.ContentType);
    }

    private static string LegacyIcon(string provider)
    {
        var brand = Brand(provider);
        return brand switch
        {
            "qobuz" when provider == "qobuz" => "qobuz.webp",
            "spicylyrics" => "spicylyrics.ico",
            "multiscrobbler" => "multi-scrobbler.svg",
            _ => string.Concat(brand.Where(char.IsAsciiLetterOrDigit)) + ".svg"
        };
    }
}
