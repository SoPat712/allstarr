using System.Text;
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
    private static readonly SKTypeface Display = Font("ArchivoNarrow-Bold.ttf"), Typed = Font("CourierPrime-Regular.ttf"), TypedBold = Font("CourierPrime-Bold.ttf");
    private static readonly Lazy<SKRuntimeEffect> Water = new(() => SKRuntimeEffect.CreateShader(WaterShader, out var errors)
        ?? throw new InvalidOperationException(errors));
    private readonly SKCanvas canvas;
    private readonly Random layout, jitter;
    private readonly MixCoverInput mix;
    private MixCoverRenderer(SKCanvas canvas, MixCoverInput mix)
    {
        this.canvas = canvas;
        this.mix = mix;
        layout = new(Hash(mix.Id + mix.Tag));
        jitter = new(Hash(mix.Id));
    }

    public static byte[] Render(MixCoverInput input)
    {
        using var bitmap = new SKBitmap(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale(Size);
        new MixCoverRenderer(canvas, input).Draw();
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
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
    internal sealed class Random(uint seed)
    {
        private uint state = seed;
        internal double Next()
        {
            unchecked
            {
                var a = state += 0x6d2b79f5;
                var t = (a ^ (a >> 15)) * (1 | a);
                t = (t + (t ^ (t >> 7)) * (61 | t)) ^ t;
                return (t ^ (t >> 14)) / 4294967296d;
            }
        }
    }
    private static int Palette(string tag)
    {
        var lower = tag.ToLowerInvariant();
        foreach (var (word, palette) in GenreWords) if (lower.Contains(word, StringComparison.Ordinal)) return palette;
        return (int)(Hash(lower) % Names.Length);
    }
    internal static string PaletteName(string tag) => Names[Palette(tag)];
    private static double Between(Random r, double a, double b) => a + r.Next() * (b - a);
    private static SKColor Colour(string hex, double alpha = 1) => SKColor.Parse(hex).WithAlpha((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255));
    private static SKPaint Paint(string colour, double alpha = 1) => new() { Color = Colour(colour, alpha), IsAntialias = true };
    private static SKTypeface Font(string name)
    {
        using var stream = typeof(MixCoverRenderer).Assembly.GetManifestResourceStream("allstarr.Resources.MixCovers." + name)
            ?? throw new InvalidOperationException("Cover font missing.");
        using var data = SKData.Create(stream);
        return SKTypeface.FromData(data) ?? throw new InvalidOperationException("Cover font unreadable.");
    }
    private static SKFont TextFont(SKTypeface face, double size) => new(face, (float)(size * Size)) { Subpixel = true, Hinting = SKFontHinting.None };
    private static void Transform(SKCanvas g, double x, double y, double angle)
    {
        g.Translate((float)x, (float)y);
        g.RotateDegrees((float)(angle * 180 / Math.PI));
    }
    private static void Rect(SKCanvas g, SKPaint p, double x, double y, double w, double h) => g.DrawRect((float)x, (float)y, (float)w, (float)h, p);
    private static void Circle(SKCanvas g, SKPaint p, double x, double y, double radius) => g.DrawCircle((float)x, (float)y, (float)radius, p);

    private void Paper(string colour, bool dark = false)
    {
        using var paint = Paint(colour);
        Rect(canvas, paint, 0, 0, 1, 1);
        for (var i = 0; i < 26; i++)
        {
            var x = layout.Next(); var y = layout.Next(); var radius = Between(layout, .1, .35);
            var tone = layout.Next() < .5 ? dark ? "#000000" : "#6b5a3a" : "#ffffff";
            using var shader = SKShader.CreateRadialGradient(new((float)x, (float)y), (float)radius,
                [Colour(tone, .05), Colour(tone, 0)], [0, 1], SKShaderTileMode.Clamp);
            paint.Color = SKColors.White; paint.Shader = shader;
            Rect(canvas, paint, 0, 0, 1, 1);
        }
        paint.Shader = null;
        for (var i = 0; i < 900; i++)
        {
            paint.Color = Colour(dark ? "#ffffff" : "#3a2f20", Between(layout, .03, .12));
            Rect(canvas, paint, layout.Next(), layout.Next(), Between(layout, .001, .003), Between(layout, .001, .003));
        }
        paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = .0012f; paint.Color = Colour(dark ? "#ffffff" : "#5a4a30", .07);
        for (var i = 0; i < 50; i++)
        {
            var x = layout.Next(); var y = layout.Next(); var a = layout.Next() * 6.28; var length = Between(layout, .01, .04);
            using var builder = new SKPathBuilder(); builder.MoveTo((float)x, (float)y);
            builder.QuadTo((float)(x + Math.Cos(a + .6) * length / 2), (float)(y + Math.Sin(a + .6) * length / 2),
                (float)(x + Math.Cos(a) * length), (float)(y + Math.Sin(a) * length));
            using var path = builder.Detach(); canvas.DrawPath(path, paint);
        }
    }
    private void Ink(string colour, Action<SKCanvas, SKPaint> draw, bool over = false, double alpha = .93, int specks = 1400)
    {
        using var sheet = new SKBitmap(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul);
        sheet.Erase(SKColors.Transparent);
        using var g = new SKCanvas(sheet); g.Scale(Size);
        using var paint = Paint(colour); draw(g, paint);
        paint.Style = SKPaintStyle.Fill; paint.BlendMode = SKBlendMode.DstOut;
        for (var i = 0; i < specks; i++)
        {
            paint.Color = Colour("#000000", Between(jitter, .15, .7));
            Circle(g, paint, jitter.Next(), jitter.Next(), Between(jitter, .0008, .0035));
        }
        using var composite = Paint("#ffffff", alpha); composite.BlendMode = over ? SKBlendMode.SrcOver : SKBlendMode.Multiply;
        canvas.Save(); canvas.Translate((float)Between(jitter, -.006, .006), (float)Between(jitter, -.006, .006));
        canvas.DrawBitmap(sheet, SKRect.Create(1, 1), new SKSamplingOptions(SKFilterMode.Linear), composite); canvas.Restore();
    }
    private void Cut(SKCanvas g, SKPaint paint, (double X, double Y)[] points, double rough = .006, Random? random = null)
    {
        var r = random ?? jitter;
        using var builder = new SKPathBuilder();
        for (var i = 0; i < points.Length; i++)
        {
            var (x, y) = points[i]; var next = points[(i + 1) % points.Length];
            var jx = (float)(x + Between(r, -rough, rough)); var jy = (float)(y + Between(r, -rough, rough));
            if (i == 0) builder.MoveTo(jx, jy); else builder.LineTo(jx, jy);
            for (var k = 1; k < 4; k++) builder.LineTo((float)(x + (next.X - x) * k / 4 + Between(r, -rough, rough) * .4),
                (float)(y + (next.Y - y) * k / 4 + Between(r, -rough, rough) * .4));
        }
        builder.Close(); using var path = builder.Detach(); g.DrawPath(path, paint);
    }
    private void Blob(SKCanvas g, SKPaint paint, double cx, double cy, double radius, double wobble = .04)
    {
        using var builder = new SKPathBuilder(); var phase = jitter.Next() * 6.28;
        for (var i = 0; i <= 34; i++)
        {
            var a = i / 34d * 6.283; var rr = radius * (1 + Math.Sin(a * 3 + phase) * wobble * .5 + Between(jitter, -wobble, wobble) * .5);
            var x = (float)(cx + Math.Cos(a) * rr); var y = (float)(cy + Math.Sin(a) * rr);
            if (i == 0) builder.MoveTo(x, y); else builder.LineTo(x, y);
        }
        builder.Close(); using var path = builder.Detach(); g.DrawPath(path, paint);
    }
    private void Pen(SKCanvas g, SKPaint paint, (double X, double Y)[] points, double width)
    {
        var colour = paint.Color; paint.Style = SKPaintStyle.Stroke; paint.StrokeCap = SKStrokeCap.Round; paint.StrokeJoin = SKStrokeJoin.Round;
        for (var pass = 0; pass < 2; pass++)
            for (var i = 1; i < points.Length; i++)
            {
                paint.StrokeWidth = (float)(width * Between(jitter, .7, 1.15) * (pass == 1 ? .5 : 1));
                paint.Color = colour.WithAlpha((byte)(pass == 1 ? colour.Alpha / 2 : colour.Alpha));
                g.DrawLine((float)(points[i - 1].X + Between(jitter, -1, 1) * .002), (float)(points[i - 1].Y + Between(jitter, -1, 1) * .002),
                    (float)(points[i].X + Between(jitter, -1, 1) * .002), (float)(points[i].Y + Between(jitter, -1, 1) * .002), paint);
            }
        paint.Color = colour; paint.Style = SKPaintStyle.Fill;
    }
    private void PenCircle(SKCanvas g, SKPaint paint, double cx, double cy, double radius, double width)
    {
        var start = jitter.Next() * 6.28; var over = Between(jitter, .05, .3);
        var points = new (double, double)[49];
        for (var i = 0; i <= 48; i++)
        {
            var a = start + i / 48d * (6.283 + over); var rr = radius * (1 + Between(jitter, -.015, .015));
            points[i] = (cx + Math.Cos(a) * rr, cy + Math.Sin(a) * rr);
        }
        Pen(g, paint, points, width);
    }
    private static void Halftone(SKCanvas g, SKPaint paint, double cell, double angle, Func<double, double, double> amount)
    {
        g.Save(); Transform(g, .5, .5, angle);
        for (var y = -1d; y < 1; y += cell)
            for (var x = -1d; x < 1; x += cell)
            {
                var v = amount(.5 + x * Math.Cos(angle) - y * Math.Sin(angle), .5 + x * Math.Sin(angle) + y * Math.Cos(angle));
                if (v > .02) Circle(g, paint, x, y, cell * .55 * Math.Sqrt(v));
            }
        g.Restore();
    }
    private static double Fit(string text, SKTypeface face, double max, double size)
    {
        using var font = TextFont(face, size);
        while (font.MeasureText(text) / Size > max && size * Size > 3) { size -= .5 / Size; font.Size = (float)(size * Size); }
        return size;
    }
    private static void Text(SKCanvas g, SKPaint paint, string text, double x, double y, double size, SKTypeface face, SKTextAlign align = SKTextAlign.Left)
    {
        using var font = TextFont(face, size);
        g.Save(); g.Scale(1f / Size); g.DrawText(text, (float)(x * Size), (float)(y * Size), align, font, paint); g.Restore();
    }
    private void Pasted(SKCanvas g, SKPaint paint, string text, double x, double y, double size, SKTypeface? face = null, double wobble = 1, Random? random = null)
    {
        var r = random ?? jitter; face ??= Display;
        using var font = TextFont(face, size);
        foreach (var rune in text.EnumerateRunes())
        {
            var ch = rune.ToString(); var width = font.MeasureText(ch) / Size;
            g.Save(); Transform(g, x + width / 2, y + Between(r, -1, 1) * size * .03 * wobble, Between(r, -.035, .035) * wobble);
            Text(g, paint, ch, -width / 2, 0, size, face); g.Restore(); x += width;
        }
    }
    private void Label(double x, double y, double width, string[] lines, double? rotation = null, Random? random = null)
    {
        var r = random ?? jitter; var height = .052 * lines.Length + .045; var rot = rotation ?? Between(layout, -.05, .03);
        canvas.Save(); Transform(canvas, x, y, rot);
        using var paint = Paint("#000000", .18);
        Cut(canvas, paint, [(.006, .008), (width + .006, .008), (width + .006, height + .008), (.006, height + .008)], .003, r);
        paint.Color = Colour("#fbf7ee"); Cut(canvas, paint, [(0, 0), (width, 0), (width, height), (0, height)], .003, r);
        for (var i = 0; i < lines.Length; i++)
        {
            var face = i == 0 ? TypedBold : Typed; var size = Fit(lines[i], face, width - .05, .042);
            paint.Color = Colour(Black, Between(r, .82, .95));
            Pasted(canvas, paint, lines[i], .025, .022 + .052 * (i + 1) - .052 * .25, size, face, .35, r);
        }
        canvas.Restore();
    }
    private void Stamp(double x, double y, string text, string colour, double rotation, double strength = 1)
    {
        Ink(colour, (g, p) =>
        {
            g.Save(); Transform(g, x, y, rotation);
            using var font = TextFont(Display, .085); var width = font.MeasureText(text) / Size + .06;
            p.Style = SKPaintStyle.Stroke; p.StrokeWidth = .008f; Rect(g, p, -width / 2, -.06, width, .12); p.Style = SKPaintStyle.Fill;
            var metrics = font.Metrics;
            Text(g, p, text, 0, .004 - (metrics.Ascent + metrics.Descent) / (2 * Size), .085, Display, SKTextAlign.Center); g.Restore();
        }, alpha: .75 * strength, specks: 2600);
    }

    private void Draw()
    {
        var palette = Palette(mix.Tag); var p = Palettes[palette]; var dark = palette is 4 or 7;
        if (mix.Kind.StartsWith("daily-", StringComparison.Ordinal))
        {
            Liquid(palette);
            Label(.06, .72, .6, ["daily mix " + mix.Kind[6..], mix.Tag.ToLowerInvariant(), mix.Artists.ToLowerInvariant()], random: new(Hash(mix.Id + "label")));
            return;
        }
        switch (mix.Kind)
        {
            case "discover-weekly":
                Paper(NavyPaper, true);
                var points = Enumerable.Range(0, 14).Select(_ => (X: Between(layout, .1, .9), Y: Between(layout, .08, .62))).ToArray();
                Ink("#e9e2d0", (g, paint) =>
                {
                    Pen(g, paint, [points[0], points[3], points[5], points[8], points[2]], .004);
                    Pen(g, paint, [points[1], points[6], points[9]], .004);
                    for (var i = 0; i < points.Length; i++)
                    {
                        var (x, y) = points[i]; var s = i % 4 == 0 ? .018 : .009;
                        Pen(g, paint, [(x - s, y), (x + s, y)], .004); Pen(g, paint, [(x, y - s), (x, y + s)], .004);
                    }
                }, over: true, specks: 500);
                var (nx, ny) = points[11];
                Ink(Yellow, (g, paint) => { Blob(g, paint, nx, ny, .022, .1); PenCircle(g, paint, nx, ny, .07, .006); }, over: true);
                using (var paint = Paint("#e9e2d0"))
                {
                    var size = Fit("Discover Weekly", Display, .86, .15) * 1.1;
                    Pasted(canvas, paint, "Discover", .07, .8, size); Pasted(canvas, paint, "Weekly", .07, .93, size);
                }
                break;
            case "release-radar":
                Paper(Cream);
                Ink(Green, (g, paint) =>
                {
                    for (var i = 1; i <= 4; i++) PenCircle(g, paint, .58, .42, .09 * i, .005);
                    Pen(g, paint, [(.58, .42), (.94, .22)], .006);
                });
                Ink(Pink, (g, paint) =>
                {
                    for (var i = 0; i < (mix.Count == 0 ? 9 : mix.Count); i++)
                    {
                        var a = layout.Next() * 6.283; var d = Between(layout, .06, .36);
                        Blob(g, paint, .58 + Math.Cos(a) * d, .42 + Math.Sin(a) * d, .017, .15);
                    }
                });
                Ink(Black, (g, paint) =>
                {
                    var size = Fit("Release Radar", Display, .86, .16) * 1.15;
                    Pasted(g, paint, "Release", .07, .8, size); Pasted(g, paint, "Radar", .07, .93, size);
                });
                Stamp(.76, .84, "NEW", Red, -.25);
                break;
            case "on-repeat":
                Paper(PaperWhite);
                for (var i = 0; i < 9; i++) Stamp(Between(layout, .2, .8), Between(layout, .12, .88), "ON REPEAT",
                    i % 3 == 0 ? Red : Navy, Between(layout, -.5, .5), Between(layout, .6, 1));
                break;
            case "rewind":
                Paper(Cream);
                Ink(Orange, (g, paint) => Halftone(g, paint, .03, .4, (x, y) => Math.Clamp(1.15 - Math.Sqrt(Math.Pow(x - .5, 2) + Math.Pow(y - .52, 2)) / .3, 0, 1)), alpha: .85);
                Ink(Pink, (g, paint) =>
                {
                    for (var i = 0; i < 6; i++) Cut(g, paint, [(0, .56 + i * .05), (1, .55 + i * .05), (1, .575 + i * .05), (0, .58 + i * .05)], .004);
                }, alpha: .8);
                Label(.08, .08, .44, ["rewind", "side b"], -.04);
                break;
            case "chill":
                Paper(PaperWhite);
                Ink(Blue, (g, paint) =>
                {
                    for (var i = 0; i < 9; i++)
                    {
                        var pts = new List<(double, double)>();
                        for (var x = -.02; x <= 1.02; x += 1d / 24) pts.Add((x, .12 + i * .065 + Math.Sin(x * 6.283 * 1.3 + i * .7) * .025));
                        Pen(g, paint, pts.ToArray(), .006);
                    }
                });
                Ink(Blue, (g, paint) => Pasted(g, paint, "Chill", .07, .9, .2));
                break;
            case "focus":
                Paper(PaperWhite);
                Ink("#7fb3d8", (g, paint) => { for (var i = 1; i < 16; i++) Pen(g, paint, [(0, i / 16d), (1, i / 16d)], .0025); }, specks: 300);
                Ink(Red, (g, paint) => Pen(g, paint, [(.14, 0), (.14, 1)], .003), specks: 200);
                Ink(Black, (g, paint) => Pasted(g, paint, "Focus", .2, .43, .2));
                Ink(Red, (g, paint) => PenCircle(g, paint, .4, .37, .24, .007));
                break;
            case "workout":
                Paper(Cream);
                Ink(Red, (g, paint) =>
                {
                    for (var i = 0; i < 5; i++)
                    {
                        var y = .08 + i * .13;
                        Cut(g, paint, [(-.1, y + .12), (1.1, y - .08), (1.1, y - .02), (-.1, y + .18)], .008);
                    }
                });
                Ink(Black, (g, paint) =>
                {
                    g.Save(); g.Translate(.07f, .9f); g.Skew(-.2f, 0);
                    Pasted(g, paint, "Workout", 0, 0, Fit("Workout", Display, .8, .22)); g.Restore();
                });
                break;
            case "party":
                Paper(PaperWhite);
                foreach (var colour in new[] { Pink, Yellow, Blue }) Ink(colour, (g, paint) =>
                {
                    for (var i = 0; i < 26; i++)
                    {
                        var x = layout.Next(); var y = layout.Next() * .72;
                        if (layout.Next() < .65) Blob(g, paint, x, y, Between(layout, .015, .03), .08);
                        else
                        {
                            g.Save(); Transform(g, x, y, layout.Next() * 6.28);
                            Cut(g, paint, [(-.03, -.008), (.03, -.008), (.03, .008), (-.03, .008)], .002); g.Restore();
                        }
                    }
                }, alpha: .85);
                Ink(Black, (g, paint) => Pasted(g, paint, "Party", .07, .92, .22));
                break;
            case "sleep":
                Moon();
                break;
            case "custom":
                Paper(p[0], dark);
                Ink(p[2], (g, paint) => Halftone(g, paint, .034, .79, (x, y) => .25 + .6 * x * (1 - y)), dark);
                Ink(p[1], (g, paint) =>
                {
                    g.Save(); Transform(g, .08, .82, Between(layout, -.08, .05));
                    var initial = mix.Seed.EnumerateRunes().FirstOrDefault().ToString();
                    Text(g, paint, string.IsNullOrWhiteSpace(initial) || initial == "\0" ? "?" : initial.ToUpperInvariant(), 0, 0, .95, Display); g.Restore();
                }, dark, .9);
                Label(.4, .74, .54, ["mix from", mix.Seed.ToLowerInvariant()]);
                break;
            case "external":
                External();
                break;
            default: throw new ArgumentException("Unknown mix kind.", nameof(mix));
        }
    }

    private void Moon()
    {
        Paper(NavyPaper, true);
        using var moon = new SKBitmap(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul); moon.Erase(SKColors.Transparent);
        using (var g = new SKCanvas(moon))
        {
            g.Scale(Size); using var paint = Paint("#f4dc8a"); Blob(g, paint, .62, .36, .2, .03);
            paint.BlendMode = SKBlendMode.DstOut; Blob(g, paint, .71, .3, .18, .03);
        }
        canvas.Save(); canvas.Translate(.008f, .01f); canvas.DrawBitmap(moon, SKRect.Create(1, 1), new SKSamplingOptions(SKFilterMode.Linear)); canvas.Restore();
        Ink("#f4dc8a", (g, _) => g.DrawBitmap(moon, SKRect.Create(1, 1), new SKSamplingOptions(SKFilterMode.Linear)), over: true, alpha: 1, specks: 400);
        Ink("#e9e2d0", (g, paint) =>
        {
            for (var i = 0; i < 16; i++)
            {
                var x = Between(layout, .06, .94); var y = Between(layout, .05, .68); var s = Between(layout, .006, .014);
                Pen(g, paint, [(x - s, y), (x + s, y)], .003); Pen(g, paint, [(x, y - s), (x, y + s)], .003);
            }
        }, over: true, specks: 200);
        using var text = Paint("#e9e2d0"); Pasted(canvas, text, "Sleep", .07, .92, .2);
    }
    private void External()
    {
        if (mix.Source == "lastfm")
        {
            Paper("#d51007", true);
            Ink("#f6efe6", (g, paint) =>
            {
                g.Save(); Transform(g, .1, .08, Between(layout, -.08, .02)); g.Scale(.5f / 24);
                using var path = SKPath.ParseSvgPathData(LastFm); g.DrawPath(path, paint); g.Restore();
            }, over: true, alpha: .95, specks: 2600);
            var (lines, subtitle) = mix.SourcePlaylistId switch
            {
                "loved" => (new[] { "loved", "tracks" }, "your hearts on last.fm"),
                "similar" => (new[] { "similar", "artists" }, "like your top artists"),
                _ => (new[] { "top tracks" }, mix.SourcePlaylistId.Split(':').ElementAtOrDefault(1) switch
                {
                    "7day" => "this week",
                    "1month" => "this month",
                    "3month" => "three months",
                    "6month" => "six months",
                    "12month" => "this year",
                    _ => "all time"
                })
            };
            Label(.07, .66, .7, [.. lines, subtitle]);
        }
        else
        {
            Paper("#353070", true);
            Ink("#eb743b", (g, paint) => Cut(g, paint, [(.1, .1), (.62, .14), (.56, .56), (.12, .5)], .01), over: true);
            Ink("#9a93e6", (g, paint) => Blob(g, paint, .66, .42, .17, .05), over: true, alpha: .9);
            var lines = mix.SourcePlaylistId.Contains("explor", StringComparison.OrdinalIgnoreCase) ? new[] { "weekly", "exploration" }
                : mix.SourcePlaylistId.Contains("jams", StringComparison.OrdinalIgnoreCase) ? new[] { "weekly jams" } : new[] { mix.Name.ToLowerInvariant() };
            Label(.07, .66, .72, ["listenbrainz", .. lines]);
        }
    }
    private void Liquid(int palette)
    {
        using var uniforms = new SKRuntimeEffectUniforms(Water.Value) { ["seed"] = (float)((Hash(mix.Id) % 1000) / 37d) };
        for (var i = 0; i < 4; i++)
        {
            var colour = Colour(Liquids[palette][i]);
            uniforms["c" + i] = new[] { colour.Red / 255f, colour.Green / 255f, colour.Blue / 255f };
        }
        using var shader = Water.Value.ToShader(uniforms);
        using var paint = new SKPaint { Shader = shader };
        Rect(canvas, paint, 0, 0, 1, 1);
    }
    private const string WaterShader = """
        uniform float seed;
        uniform float3 c0, c1, c2, c3;
        float2 h2(float2 p) { p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3))); return -1.0 + 2.0 * fract(sin(p) * 43758.5453); }
        float noise(float2 p) {
            float2 i = floor(p), f = fract(p), u = f * f * (3.0 - 2.0 * f);
            return mix(mix(dot(h2(i), f), dot(h2(i + float2(1,0)), f - float2(1,0)), u.x), mix(dot(h2(i + float2(0,1)), f - float2(0,1)), dot(h2(i + float2(1,1)), f - float2(1,1)), u.x), u.y);
        }
        float fbm(float2 p) {
            float v = 0.0, a = 0.5;
            float2x2 m = float2x2(1.6, 1.2, -1.2, 1.6);
            for (int i = 0; i < 4; i++) { v += a * noise(p); p = m * p; a *= 0.42; }
            return v;
        }
        half4 main(float2 xy) {
            float2 frag = float2(xy.x * 600.0, 600.0 - xy.y * 600.0);
            float2 uv = frag / 600.0;
            float2 p = uv * 0.85 + seed;
            float t = 3.0;
            float2 q = float2(fbm(p + float2(0.0, 0.11 * t)), fbm(p + float2(5.2, 1.3) - 0.09 * t));
            float2 r = float2(fbm(p + 1.7 * q + float2(1.7, 9.2) + 0.13 * t), fbm(p + 1.7 * q + float2(8.3, 2.8) - 0.1 * t));
            float f = fbm(p + 1.6 * r);
            float3 col = c0;
            col = mix(col, c1, smoothstep(-0.05, 0.15, q.x));
            col = mix(col, c2, smoothstep(0.04, 0.22, r.y));
            col = mix(col, c3, smoothstep(0.16, 0.34, f) * 0.9);
            col = mix(col, c0, smoothstep(0.2, 0.5, -r.x) * 0.6);
            col += (fract(sin(dot(frag, float2(12.9898, 78.233))) * 43758.5453) - 0.5) * 0.03;
            return half4(col, 1.0);
        }
        """;
    private const string LastFm = "M10.584 17.21l-.88-2.392s-1.43 1.594-3.573 1.594c-1.897 0-3.244-1.649-3.244-4.288 0-3.382 1.704-4.591 3.381-4.591 2.42 0 3.189 1.567 3.849 3.574l.88 2.749c.88 2.666 2.529 4.81 7.285 4.81 3.409 0 5.718-1.044 5.718-3.793 0-2.227-1.265-3.381-3.63-3.931l-1.758-.385c-1.21-.275-1.567-.77-1.567-1.595 0-.934.742-1.484 1.952-1.484 1.32 0 2.034.495 2.144 1.677l2.749-.33c-.22-2.474-1.924-3.492-4.729-3.492-2.474 0-4.893.935-4.893 3.932 0 1.87.907 3.051 3.189 3.601l1.87.44c1.402.33 1.869.907 1.869 1.704 0 1.017-.99 1.43-2.86 1.43-2.776 0-3.93-1.457-4.59-3.464l-.907-2.75c-1.155-3.573-2.997-4.893-6.653-4.893C2.144 5.333 0 7.89 0 12.233c0 4.18 2.144 6.434 5.993 6.434 3.106 0 4.591-1.457 4.591-1.457z";
}
