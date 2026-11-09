using System.Globalization;
using SkiaSharp;

namespace allstarr.Core.Music;

public sealed class MixCoverRenderer
{
    private const int Size = 600;

    public static byte[] Render(MixCoverInput input)
    {
        var (stops, angle) = Gradient(input);
        using var bitmap = new SKBitmap(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        // The gradient runs through the centre at its angle and reaches every corner, as a CSS linear-gradient does.
        var radians = angle * Math.PI / 180;
        var (dx, dy) = ((float)Math.Cos(radians), (float)Math.Sin(radians));
        var reach = Size / 2f * (Math.Abs(dx) + Math.Abs(dy));
        const float centre = Size / 2f;
        using var shader = SKShader.CreateLinearGradient(new(centre - dx * reach, centre - dy * reach),
            new(centre + dx * reach, centre + dy * reach), stops.Select(SKColor.Parse).ToArray(), SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(0, 0, Size, Size, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    // Four pictures make a 2×2 grid and one fills the square, each cropped to its centre. Null when a picture can't be read.
    public static byte[]? RenderCollage(IReadOnlyList<byte[]> pictures)
    {
        if (pictures.Count is not (1 or 4)) return null;
        var images = new List<SKImage>(pictures.Count);
        try
        {
            foreach (var picture in pictures)
            {
                var image = SKImage.FromEncodedData(picture);
                if (image == null || image.Width < 1 || image.Height < 1) return null;
                images.Add(image);
            }
            using var bitmap = new SKBitmap(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            var tile = images.Count == 4 ? Size / 2 : Size;
            using var paint = new SKPaint { IsAntialias = true };
            for (var index = 0; index < images.Count; index++)
            {
                var image = images[index];
                var side = Math.Min(image.Width, image.Height);
                var source = SKRect.Create((image.Width - side) / 2f, (image.Height - side) / 2f, side, side);
                var target = SKRect.Create(index % 2 * tile, index / 2 * tile, tile, tile);
                canvas.DrawImage(image, source, target, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            }
            using var result = SKImage.FromBitmap(bitmap);
            using var jpeg = result.Encode(SKEncodedImageFormat.Jpeg, 88);
            return jpeg.ToArray();
        }
        finally
        {
            foreach (var image in images) image.Dispose();
        }
    }

    // Colours and angle the web UI draws too (webui/src/lib/covers/cover-for.ts), so both agree.
    internal static (string[] Stops, int Angle) Gradient(MixCoverInput input) =>
        input.Kind == "external" ? (External(input), 45) : Bold(input.Id);

    // Pale, bright and deep: saturation and brightness of the first stop, then of the second.
    private static readonly (int Saturation, int Value, int NextSaturation, int NextValue)[] Tones =
        [(42, 98, 72, 86), (86, 96, 82, 62), (78, 62, 70, 34)];

    // Two stops seeded from the mix: a hue anywhere on the wheel, a second hue 40–75° away, a tone (pale, bright or
    // deep) and an angle. Lightness tells covers apart at list size as much as hue does. Dark yellows turn olive and
    // bright greens glare, so those are moved to a nearby tone.
    internal static (string[] Stops, int Angle) Bold(string id)
    {
        var hash = Hash(id);
        var hue = (int)((hash % 12 * 30 + (hash >> 4) % 17 + 352) % 360);
        var swing = 40 + (int)((hash >> 9) % 36);
        if ((hash >> 15 & 1) == 0) swing = -swing;
        var tone = (int)((hash >> 20) % 3);
        if (tone == 2 && hue is >= 35 and <= 80) tone = 1;
        var (saturation, value, nextSaturation, nextValue) = Tones[tone];
        if (tone == 1 && hue is >= 80 and <= 160) (saturation, value, nextSaturation) = (saturation - 22, value - 12, nextSaturation - 18);
        return ([Hsv(hue + 360, saturation, value), Hsv(hue + swing + 360, nextSaturation, nextValue)], (int)((hash >> 16) % 360));
    }

    internal static string Hsv(int hue, int saturation, int value)
    {
        hue %= 360;
        var chroma = value / 100.0 * (saturation / 100.0);
        var x = chroma * (1 - Math.Abs(hue / 60.0 % 2 - 1));
        var match = value / 100.0 - chroma;
        var (red, green, blue) = hue switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0.0, chroma, x),
            < 240 => (0.0, x, chroma),
            < 300 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };
        static int Channel(double part) => (int)Math.Round(part * 255, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture,
            $"#{Channel(red + match):x2}{Channel(green + match):x2}{Channel(blue + match):x2}");
    }

    // A service mix keeps its service's colour and takes two accents from which mix it is, so mixes from one service differ.
    private static readonly string[] LastFmAccents = ["#f6efe6", "#ff9a5c", "#2a0b2e", "#f5c518", "#7a0a2a", "#ffd6c9", "#3a5bff"];
    private static readonly string[] ListenBrainzAccents = ["#eb743b", "#9a93e6", "#f6e3c8", "#1d2a52", "#5ee7ff", "#ff6fae", "#e8d8b0"];

    private static string[] External(MixCoverInput input)
    {
        var lastFm = input.Source == "lastfm";
        var accents = lastFm ? LastFmAccents : ListenBrainzAccents;
        var hash = Hash(input.SourcePlaylistId.Length > 0 ? input.SourcePlaylistId : input.Id);
        var first = (int)(hash % (uint)accents.Length);
        var second = (first + 1 + (int)(hash / (uint)accents.Length % (uint)(accents.Length - 1))) % accents.Length;
        return [lastFm ? "#d51007" : "#353070", accents[first], accents[second]];
    }

    internal static uint Hash(string text)
    {
        var value = 2166136261u;
        for (var i = 0; i < text.Length; i++)
        {
            var codePoint = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                ? (uint)char.ConvertToUtf32(text[i], text[++i]) : text[i];
            value = unchecked((value ^ codePoint) * 16777619u);
        }
        return value;
    }
}
