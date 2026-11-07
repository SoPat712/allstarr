using System.Text.RegularExpressions;

namespace allstarr.Core.ManagedFiles;

public sealed partial class RecordingKeptFiles
{
    private static string KeptPathTemplate(string template, ManagedTrackPathValues values)
    {
        if (Path.IsPathRooted(template)) throw new ArgumentException("The kept-file path template must be relative.", nameof(template));
        var fields = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["track"] = values.Track is > 0,
            ["year"] = values.Year is > 0,
            ["artist"] = Known(values.Artist),
            ["albumArtist"] = Known(values.AlbumArtist ?? values.Artist),
            ["album"] = Known(values.Album),
            ["genre"] = Known(values.Genre)
        };
        foreach (var field in fields.Where(field => !field.Value))
        {
            var token = @"\{" + field.Key + @"(?::[^{}]+)?\}";
            template = Regex.Replace(template, token + @"\s*[-–—]\s*|\s*[-–—]\s*" + token + "|" + token, "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return string.Join('/', template.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool Known(string? value) => !string.IsNullOrWhiteSpace(value) &&
        !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("Unknown artist", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("Unknown album", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("Unknown genre", StringComparison.OrdinalIgnoreCase);
}
