using System.Text;
using SkiaSharp;

namespace allstarr.Services.Common;

internal static class BlurHashEncoder
{
    private const int ComponentsX = 4;
    private const int ComponentsY = 3;
    private const int MaximumSampleDimension = 32;
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";

    internal static string? TryEncode(byte[] bytes)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
                (long)codec.Info.Width * codec.Info.Height > MediaAssetResolver.MaximumDecodedPixels)
                return null;

            var scale = Math.Min(1, MaximumSampleDimension / (double)Math.Max(codec.Info.Width, codec.Info.Height));
            var dimensions = codec.GetScaledDimensions((float)scale);
            using var decoded = SKBitmap.Decode(codec, new SKImageInfo(
                dimensions.Width, dimensions.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            if (decoded is null) return null;

            var width = Math.Max(1, (int)Math.Round(codec.Info.Width * scale));
            var height = Math.Max(1, (int)Math.Round(codec.Info.Height * scale));
            using var sample = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            if (!decoded.ScalePixels(sample, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
                return null;

            var rgb = new byte[width * height * 3];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var color = sample.GetPixel(x, y);
                    var offset = (y * width + x) * 3;
                    rgb[offset] = color.Red;
                    rgb[offset + 1] = color.Green;
                    rgb[offset + 2] = color.Blue;
                }
            return EncodeRgb(rgb, width, height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string EncodeRgb(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height * 3 != pixels.Length)
            throw new ArgumentException("Pixel data must match positive image dimensions.", nameof(pixels));

        var factors = new (double R, double G, double B)[ComponentsX * ComponentsY];
        for (var componentY = 0; componentY < ComponentsY; componentY++)
            for (var componentX = 0; componentX < ComponentsX; componentX++)
            {
                double red = 0, green = 0, blue = 0;
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var basis = Math.Cos(Math.PI * componentX * x / width) *
                                    Math.Cos(Math.PI * componentY * y / height);
                        var offset = (y * width + x) * 3;
                        red += basis * ToLinear(pixels[offset]);
                        green += basis * ToLinear(pixels[offset + 1]);
                        blue += basis * ToLinear(pixels[offset + 2]);
                    }
                var normalization = componentX == 0 && componentY == 0 ? 1d : 2d;
                var scale = normalization / ((double)width * height);
                factors[componentY * ComponentsX + componentX] = (red * scale, green * scale, blue * scale);
            }

        var maximum = 0d;
        foreach (var factor in factors.AsSpan(1))
            maximum = Math.Max(maximum, Math.Max(Math.Abs(factor.R), Math.Max(Math.Abs(factor.G), Math.Abs(factor.B))));
        var quantizedMaximum = (int)Math.Clamp(Math.Floor(maximum * 166 - 0.5), 0, 82);
        var maximumValue = (quantizedMaximum + 1) / 166d;
        var hash = new StringBuilder(28);
        AppendBase83(hash, ComponentsX - 1 + (ComponentsY - 1) * 9, 1);
        AppendBase83(hash, quantizedMaximum, 1);
        var dc = factors[0];
        AppendBase83(hash, (ToSrgb(dc.R) << 16) + (ToSrgb(dc.G) << 8) + ToSrgb(dc.B), 4);
        foreach (var factor in factors.AsSpan(1))
            AppendBase83(hash, Quantize(factor.R, maximumValue) * 19 * 19 +
                               Quantize(factor.G, maximumValue) * 19 + Quantize(factor.B, maximumValue), 2);
        return hash.ToString();
    }

    private static double ToLinear(byte value)
    {
        var srgb = value / 255d;
        return srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
    }

    private static int ToSrgb(double value)
    {
        var linear = Math.Clamp(value, 0, 1);
        var srgb = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (int)(srgb * 255 + 0.5);
    }

    private static int Quantize(double value, double maximum) =>
        (int)Math.Clamp(Math.Floor(Math.CopySign(Math.Sqrt(Math.Abs(value / maximum)), value) * 9 + 9.5), 0, 18);

    private static void AppendBase83(StringBuilder output, int value, int length)
    {
        var divisor = (int)Math.Pow(83, length - 1);
        for (var index = 0; index < length; index++, divisor /= 83)
            output.Append(Digits[value / divisor % 83]);
    }
}
