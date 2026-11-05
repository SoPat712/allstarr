using SkiaSharp;

namespace allstarr.Core.Music;

public sealed class MixCoverRenderer
{
    private const int Size = 600;
    private const string Black = "#2a2826", White = "#f7f2e8", Cream = "#f1e8d6", PaperWhite = "#f6f2ea",
        NavyPaper = "#1d2a52", Pink = "#ff48b0", Yellow = "#ffd400", Blue = "#0078bf", Red = "#e8443a",
        Orange = "#ff6c2f", Green = "#00a95c", Navy = "#2e3b55";
    private static readonly string[] Names = ["R&B", "Hip-hop", "Pop", "Indie rock", "Electronic", "Jazz", "Folk", "Metal", "Latin", "Classical"];
    private static readonly string[][] Palettes =
    [
        [Cream, "#914e72", Pink, Orange, Black], [Cream, Yellow, Red, Black, Black],
        ["#f6e3e1", Pink, Blue, Yellow, Navy], ["#d6bf98", "#00838a", Red, Navy, Black],
        ["#1f1d1c", Pink, "#3fb6ff", Yellow, White], [Cream, Blue, Black, Orange, Black],
        ["#d6bf98", Green, "#b8602d", Black, Black], ["#1f1d1c", "#bdb8b0", Red, "#5c5955", White],
        [PaperWhite, Orange, "#00838a", Pink, Black], [PaperWhite, "#6f7a3a", "#b8602d", Black, Black]
    ];
    private static readonly string[][] Liquids =
    [
        ["#2a0b2e", "#c2366b", "#f29e4c", "#9b6bd6"], ["#15110c", "#e8a400", "#c8341c", "#6b4a2b"],
        ["#ff6fae", "#ffd166", "#5ee7ff", "#a98bff"], ["#16302f", "#d9653b", "#2f9c95", "#e8d8b0"],
        ["#070b26", "#3a5bff", "#c04cff", "#9dff3a"], ["#0c1a36", "#c99a3b", "#8a2f2f", "#3a6ea5"],
        ["#3a2a17", "#c9762f", "#7f9f5f", "#efe0c2"], ["#0c0c0e", "#8a1c1c", "#5d636e", "#c0c4cc"],
        ["#ff5a3c", "#ffd23f", "#00a6a6", "#7b2cbf"], ["#e9e2d2", "#2f4f3a", "#b08d57", "#8a8780"]
    ];
    private static readonly (string Word, int Palette)[] GenreWords =
    [
        ("r&b", 0), ("soul", 0), ("funk", 0), ("hip", 1), ("rap", 1), ("trap", 1), ("k-pop", 2), ("pop", 2),
        ("indie", 3), ("rock", 3), ("punk", 3), ("alt", 3), ("edm", 4), ("house", 4), ("techno", 4), ("electr", 4), ("dance", 4),
        ("jazz", 5), ("blues", 5), ("folk", 6), ("country", 6), ("acoustic", 6), ("metal", 7), ("hardcore", 7),
        ("latin", 8), ("reggaeton", 8), ("salsa", 8), ("classical", 9), ("piano", 9), ("ambient", 9)
    ];
    public static byte[] Render(MixCoverInput input)
    {
        var colours = Colours(input).Select(SKColor.Parse).ToArray();
        using var bitmap = new SKBitmap(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        using var shader = SKShader.CreateLinearGradient(new(0, 0), new(Size, Size), colours, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(0, 0, Size, Size, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static string[] Colours(MixCoverInput input)
    {
        var palette = Palette(input.Tag);
        if (input.Kind.StartsWith("daily-", StringComparison.Ordinal)) return Liquids[palette];
        return input.Kind switch
        {
            "discover-weekly" => [NavyPaper, "#e9e2d0", Yellow],
            "release-radar" => [Cream, Green, Pink, Red],
            "on-repeat" => [PaperWhite, Navy, Red],
            "rewind" => [Cream, Orange, Pink],
            "chill" => [PaperWhite, Blue],
            "focus" => [PaperWhite, "#7fb3d8", Red, Black],
            "workout" => [Cream, Red, Black],
            "party" => [PaperWhite, Pink, Yellow, Blue],
            "sleep" => [NavyPaper, "#f4dc8a", "#e9e2d0"],
            "custom" => Palettes[palette][..4],
            "external" => input.Source == "lastfm" ? ["#d51007", "#f6efe6"] : ["#353070", "#eb743b", "#9a93e6"],
            _ => throw new ArgumentException("Unknown mix kind.", nameof(input))
        };
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
    private static int Palette(string tag)
    {
        var lower = tag.ToLowerInvariant();
        foreach (var (word, palette) in GenreWords) if (lower.Contains(word, StringComparison.Ordinal)) return palette;
        return (int)(Hash(lower) % Names.Length);
    }
    internal static string PaletteName(string tag) => Names[Palette(tag)];
}
