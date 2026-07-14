using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace allstarr.Core.ManagedFiles;

public static partial class ManagedPathTemplate
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "artist", "album", "albumArtist", "genre", "year", "track"
    };

    public static string Render(string template, ManagedTrackPathValues values)
    {
        if (string.IsNullOrWhiteSpace(template) || Path.IsPathRooted(template))
            throw new ArgumentException("The managed-file path template must be relative.", nameof(template));

        var rendered = TokenRegex().Replace(template, match => RenderToken(match, values));
        if (TokenRegex().IsMatch(rendered))
            throw new ArgumentException("The managed-file path template contains an invalid token.", nameof(template));

        var segments = rendered.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            throw new ArgumentException("The managed-file path template contains an unsafe segment.", nameof(template));

        var safe = segments.Select(SanitizeSegment).ToArray();
        var extension = NormalizeExtension(values.Extension);
        safe[^1] = Path.ChangeExtension(safe[^1], extension);
        return Path.Combine(safe);
    }

    private static string RenderToken(Match match, ManagedTrackPathValues values)
    {
        var name = match.Groups[1].Value;
        if (!Supported.Contains(name))
            throw new ArgumentException($"Unsupported managed-file template token '{name}'.", "template");

        var format = match.Groups[2].Success ? match.Groups[2].Value : null;
        object? value = name.ToLowerInvariant() switch
        {
            "title" => values.Title,
            "artist" => values.Artist,
            "album" => values.Album,
            "albumartist" => values.AlbumArtist ?? values.Artist,
            "genre" => values.Genre,
            "year" => values.Year,
            "track" => values.Track,
            _ => null
        };

        var rendered = value switch
        {
            null => "Unknown",
            IFormattable formattable when format is not null => formattable.ToString(format, CultureInfo.InvariantCulture),
            _ when format is not null => throw new ArgumentException($"Token '{name}' does not accept a format.", "template"),
            _ => value.ToString()!
        };
        return SanitizeSegment(rendered);
    }

    private static string SanitizeSegment(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        invalid.UnionWith(['/', '\\', ':', '\0']);
        var safe = new string(normalized.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray())
            .Trim().TrimEnd('.');
        if (safe is "" or "." or "..")
            return "Unknown";
        return safe.Length <= 180 ? safe : safe[..180];
    }

    private static string NormalizeExtension(string extension)
    {
        var value = extension.StartsWith('.') ? extension : $".{extension}";
        if (value.Length is < 2 or > 16 || value.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException("The managed-file extension is invalid.", nameof(extension));
        return value.ToLowerInvariant();
    }

    [GeneratedRegex("\\{([A-Za-z]+)(?::([^{}]+))?\\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
