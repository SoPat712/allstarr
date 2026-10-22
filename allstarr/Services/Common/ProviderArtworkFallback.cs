using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using allstarr.Core.Storage;
using SkiaSharp;

namespace allstarr.Services.Common;

internal static class ProviderArtworkFallback
{
    private const string Prefix = "/api/admin/providers/";

    internal static string CoverPath(string provider) => Prefix + Uri.EscapeDataString(provider) + "/cover";

    internal static string Tag(string? provider, string? cover = null) => "provider-cover-v2-" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SourceProvider(provider, cover))))[..16];

    internal static bool IsFallback(string? provider, string? cover, string kind) =>
        !string.IsNullOrWhiteSpace(provider) && kind is "song" or "track" or "album" or "artist" &&
        (string.IsNullOrWhiteSpace(cover) || CoverProvider(cover) != null);

    private static string SourceProvider(string? provider, string? cover) => CoverProvider(cover) ?? provider ?? "external";

    private static string? CoverProvider(string? cover)
    {
        if (cover?.StartsWith(Prefix, StringComparison.Ordinal) == true && cover.EndsWith("/cover", StringComparison.Ordinal))
            return Uri.UnescapeDataString(cover[Prefix.Length..^6]);
        if (cover?.StartsWith("/images/providers/", StringComparison.Ordinal) == true && cover.EndsWith("-cover.png", StringComparison.Ordinal))
            return Path.GetFileName(cover)[..^10];
        return null;
    }

    internal static async Task<MediaAssetSource?> ReadAsync(IServiceProvider services, string provider, int? size,
        CancellationToken token, string? cover = null)
    {
        provider = SourceProvider(provider, cover);
        var dimension = size is > 0 and <= 96 ? 96 : 600;
        var environment = services.GetRequiredService<IWebHostEnvironment>();
        var registry = services.GetService<IProviderRegistry>();
        ProviderDescriptor? descriptor = null;
        registry?.TryGet(provider, out descriptor);
        if ((descriptor?.Origin == ProviderOrigin.Extension || provider.StartsWith("spotiflac-", StringComparison.Ordinal)) &&
            services.GetService<ExtensionControlPlaneService>() is { } extensions)
        {
            var package = (await extensions.ListPackagesAsync(provider, token)).FirstOrDefault(item => item.State == ExtensionPackageState.Active);
            if (package != null && await ExtensionPackageIcon.ReadAsync(package, token) is { } icon && Render(icon, dimension) is { } rendered)
                return rendered with { IsSizeSpecific = dimension < 600 };
        }
        var name = BundledIcon(provider, descriptor?.Branding?.LogoReference);
        var file = File(environment, $"images/providers/{name}-cover{(dimension == 96 ? "-96" : "")}.png");
        if (file.Exists)
        {
            await using var source = file.CreateReadStream();
            using var output = new MemoryStream();
            await source.CopyToAsync(output, token);
            return new(output.ToArray(), "image/png", IsSizeSpecific: dimension < 600);
        }
        var label = descriptor?.DisplayName ?? provider.Replace("spotiflac-", "", StringComparison.Ordinal);
        var font = File(environment, "fonts/inter-cover.ttf");
        await using var fontSource = font.CreateReadStream();
        using var fontBytes = new MemoryStream();
        await fontSource.CopyToAsync(fontBytes, token);
        return Monogram(label, dimension, fontBytes.ToArray()) with { IsSizeSpecific = dimension < 600 };
    }

    private static Microsoft.Extensions.FileProviders.IFileInfo File(IWebHostEnvironment environment, string path)
    {
        var file = environment.WebRootFileProvider.GetFileInfo(path);
        return file.Exists ? file : environment.ContentRootFileProvider.GetFileInfo("wwwroot/" + path);
    }

    private static string BundledIcon(string provider, string? logo)
    {
        if (logo?.StartsWith("/images/providers/", StringComparison.Ordinal) == true && logo.EndsWith(".svg", StringComparison.Ordinal))
            return Path.GetFileNameWithoutExtension(logo);
        var plain = string.Concat(provider.Replace("spotiflac-", "", StringComparison.Ordinal).Where(char.IsAsciiLetterOrDigit));
        return plain switch
        {
            "amazon" => "amazonmusic",
            "apple" or "applemusickit" or "appledownload" or "applemusicdownload" => "applemusic",
            "spotifyweb" => "spotify",
            "tidalweb" => "tidal",
            "qobuzweb" => "qobuz",
            "ytmusic" or "ytmusicspotiflac" or "youtube" => "youtubemusic",
            _ => plain
        };
    }

    internal static MediaAssetSource? Render(MediaAssetSource source, int dimension)
    {
        using var data = SKData.CreateCopy(source.Bytes);
        using var codec = SKCodec.Create(data);
        if (codec?.Info is not { Width: > 0, Height: > 0 } info || (long)info.Width * info.Height > MediaAssetResolver.MaximumDecodedPixels)
            return null;
        using var original = SKBitmap.Decode(codec);
        if (original == null) return null;
        using var bitmap = new SKBitmap(dimension, dimension);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Background(original));
        var scale = Math.Min(dimension / (float)original.Width, dimension / (float)original.Height);
        var width = original.Width * scale;
        var height = original.Height * scale;
        using var image = SKImage.FromBitmap(original);
        canvas.DrawImage(image, new SKRect((dimension - width) / 2, (dimension - height) / 2,
            (dimension + width) / 2, (dimension + height) / 2), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return Png(bitmap);
    }

    private static SKColor Background(SKBitmap icon)
    {
        var corner = icon.GetPixel(0, 0);
        if (corner.Alpha == 255) return corner;
        long light = 0, count = 0;
        for (var y = 0; y < icon.Height; y += Math.Max(1, icon.Height / 32))
            for (var x = 0; x < icon.Width; x += Math.Max(1, icon.Width / 32))
            {
                var pixel = icon.GetPixel(x, y);
                if (pixel.Alpha < 128) continue;
                light += pixel.Red + pixel.Green + pixel.Blue;
                count++;
            }
        return count > 0 && light / count < 384 ? SKColors.White : SKColors.Black;
    }

    internal static MediaAssetSource Monogram(string label, int dimension, byte[] fontBytes)
    {
        using var bitmap = new SKBitmap(dimension, dimension);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColor.Parse("#dbe6ff"));
        using var data = SKData.CreateCopy(fontBytes);
        using var typeface = SKTypeface.FromData(data);
        using var font = new SKFont(typeface, dimension * 0.52f);
        using var paint = new SKPaint { Color = SKColor.Parse("#001b3f"), IsAntialias = true };
        var initial = label.Trim().FirstOrDefault();
        var letter = (initial != default ? initial.ToString() : "?").ToUpperInvariant();
        font.MeasureText(letter, out var bounds);
        canvas.DrawText(letter, (dimension - bounds.Width) / 2 - bounds.Left,
            (dimension - bounds.Height) / 2 - bounds.Top, SKTextAlign.Left, font, paint);
        return Png(bitmap);
    }

    private static MediaAssetSource Png(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return new(encoded.ToArray(), "image/png");
    }
}
