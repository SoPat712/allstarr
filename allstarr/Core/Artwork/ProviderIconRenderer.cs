using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;

namespace allstarr.Core.Artwork;

internal sealed class ProviderIconRenderer : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 16 * 1024 * 1024 });

    internal async Task<byte[]?> ReadAsync(ProviderIconAsset icon,
        int size, bool glyph, CancellationToken token)
    {
        var key = $"provider-icon:{icon.File.PhysicalPath ?? icon.File.Name}:{icon.Revision}:{icon.File.LastModified.Ticks}:{icon.File.Length}:{glyph}:{size}";
        if (_cache.TryGetValue<byte[]>(key, out var cached)) return cached;
        await using var source = icon.File.CreateReadStream();
        using var output = new MemoryStream();
        await source.CopyToAsync(output, token);
        var bytes = output.ToArray();
        if (icon.ContentType != "image/svg+xml") bytes = Render(bytes, size, glyph);
        if (bytes != null) _cache.Set(key, bytes, new MemoryCacheEntryOptions
        {
            Size = bytes.Length,
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
        });
        return bytes;
    }

    public void Dispose() => _cache.Dispose();

    internal static byte[]? Render(byte[] bytes, int size, bool glyph)
    {
        using var source = SKBitmap.Decode(bytes);
        if (source == null) return null;
        using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            var scale = Math.Min((float)size / source.Width, (float)size / source.Height);
            var width = source.Width * scale;
            var height = source.Height * scale;
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, new SKRect((size - width) / 2, (size - height) / 2,
                (size + width) / 2, (size + height) / 2), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        if (glyph)
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                    bitmap.SetPixel(x, y, new SKColor(0, 0, 0, bitmap.GetPixel(x, y).Alpha));
        using var rendered = SKImage.FromBitmap(bitmap);
        using var png = rendered.Encode(SKEncodedImageFormat.Png, 100);
        return png?.ToArray();
    }
}
