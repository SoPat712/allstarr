using SkiaSharp;

namespace allstarr.Core.Enrichment;

internal static class PromotionCover
{
    public static bool Supported(byte[] bytes) => bytes.Length is > 8 and <= 5242880 &&
        (bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }) || bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));

    // Album art is never this small; a picture under it is a service's mark or a thumbnail that was saved as the cover.
    private const int SmallestCoverSide = 300;

    /// <summary>A supported picture large enough to be a cover.</summary>
    public static bool IsCover([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] byte[]? bytes)
    {
        if (bytes == null || !Supported(bytes)) return false;
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        return codec != null && Math.Min(codec.Info.Width, codec.Info.Height) >= SmallestCoverSide;
    }

    // A picture inside a song file is carried by every song of an album and read whole by players. It is kept sharp
    // enough for a phone or a television and no larger; a service's full-size cover can be several megabytes.
    private const int LargestEmbeddedSide = 1200;

    /// <summary>The cover as it is put inside a song file: at most 1,200 pixels a side.</summary>
    public static byte[] Embedded(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec == null || Math.Max(codec.Info.Width, codec.Info.Height) <= LargestEmbeddedSide) return bytes;
        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap == null) return bytes;
        var scale = LargestEmbeddedSide / (double)Math.Max(bitmap.Width, bitmap.Height);
        using var smaller = bitmap.Resize(new SKImageInfo(Math.Max(1, (int)Math.Round(bitmap.Width * scale)),
            Math.Max(1, (int)Math.Round(bitmap.Height * scale))), new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (smaller == null) return bytes;
        using var image = SKImage.FromBitmap(smaller);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return encoded?.ToArray() ?? bytes;
    }

    public static byte[]? Jpeg(byte[] bytes)
    {
        if (!Supported(bytes)) return null;
        if (bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 })) return bytes;
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec == null || codec.Info.Width is < 1 or > 4096 || codec.Info.Height is < 1 or > 4096 ||
            codec.Info.Width * (long)codec.Info.Height > 16777216) return null;
        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap == null) return null;
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return encoded?.ToArray();
    }
}
