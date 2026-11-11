using System.Text;

namespace allstarr.Core.ClientApiV2;

/// <summary>
/// Artwork urls for the native client API. The key names the item and its picture version, so a url never changes for the
/// same picture and a new picture gets a new url. Requests are served by the existing image route under the caller's sign-in.
/// </summary>
public static class V2ArtworkUrls
{
    public const string PathPrefix = "/allstarr/v2/artwork/";
    public const int DefaultWidth = 600;
    private const int MinimumWidth = 32;
    private const int MaximumWidth = 2048;
    private const int MaximumTagLength = 128;

    public static V2Artwork? For(string? itemId, string? tag)
    {
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(tag) || !IsSafeTag(tag) ||
            !(V2Ids.IsValidSource(itemId, V2IdKind.Track) || V2Ids.IsValidSource(itemId, V2IdKind.Album) ||
              V2Ids.IsValidSource(itemId, V2IdKind.Artist) || V2Ids.IsValidSource(itemId, V2IdKind.Playlist)))
            return null;
        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes(itemId + "\n" + tag)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new(PathPrefix + key);
    }

    /// <summary>Turns an artwork key and requested width into the image route path and query, or null for anything invalid.</summary>
    internal static (string Path, QueryString Query)? Resolve(string key, string? width)
    {
        if (key.Length is 0 or > 600 || key.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) return null;
        var padded = key.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        var bytes = new byte[padded.Length];
        if (!Convert.TryFromBase64String(padded, bytes, out var written)) return null;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes, 0, written); }
        catch (DecoderFallbackException) { return null; }
        var parts = text.Split('\n');
        if (parts.Length != 2 || For(parts[0], parts[1]) == null) return null;
        var pixels = int.TryParse(width, out var requested) ? Math.Clamp(requested, MinimumWidth, MaximumWidth) : DefaultWidth;
        var query = QueryString.Create(new KeyValuePair<string, string?>[]
        {
            new("tag", parts[1]),
            new("maxWidth", pixels.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("quality", "90")
        });
        return ($"/Items/{Uri.EscapeDataString(parts[0])}/Images/Primary", query);
    }

    private static bool IsSafeTag(string tag) =>
        tag.Length <= MaximumTagLength && tag.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
