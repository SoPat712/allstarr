using System.Text;
using allstarr.Services.Local;

namespace allstarr.Core.ClientApiV2;

public enum V2IdKind { Track, Album, Artist, Playlist, Mix }

/// <summary>A decoded native id: a canonical recording for tracks that have one, otherwise the underlying library or service id.</summary>
public readonly record struct V2IdRef(V2IdKind Kind, Guid? Recording, string? Source);

/// <summary>
/// Typed, opaque ids for the native client API. Tracks with a canonical recording use it, so the id survives keeping,
/// adding to the library and rematching. Everything else wraps today's id. Any older spelling is accepted as input.
/// </summary>
public static class V2Ids
{
    private const int MaximumLength = 400;
    private const int MaximumSourceLength = 300;

    private static readonly (string Prefix, V2IdKind Kind)[] Prefixes =
    [
        ("trk_", V2IdKind.Track), ("alb_", V2IdKind.Album), ("art_", V2IdKind.Artist),
        ("pl_", V2IdKind.Playlist), ("mix_", V2IdKind.Mix)
    ];

    public static string Track(Guid recording) => "trk_" + recording.ToString("N");

    public static string TrackSource(string source) => "trk_s" + Encode(source);

    public static string Album(string source) => "alb_" + Encode(source);

    public static string Artist(string source) => "art_" + Encode(source);

    public static string Playlist(string source) => "pl_" + Encode(source);

    public static string Mix(Guid mixId) => "mix_" + mixId.ToString("N");

    /// <summary>Decodes a v2 id of the expected kind, or accepts an older library or service id; returns false for anything else.</summary>
    public static bool TryDecode(string? value, V2IdKind expected, out V2IdRef id)
    {
        id = default;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength) return false;
        foreach (var (prefix, kind) in Prefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (kind != expected) return false;
            var body = value[prefix.Length..];
            if (kind == V2IdKind.Track && body.Length == 32 && Guid.TryParseExact(body, "N", out var recording))
            {
                id = new(kind, recording, null);
                return true;
            }
            if (kind == V2IdKind.Mix)
            {
                if (!Guid.TryParseExact(body, "N", out var mix)) return false;
                id = new(kind, null, mix.ToString("D"));
                return true;
            }
            if (kind == V2IdKind.Track)
            {
                if (!body.StartsWith('s')) return false;
                body = body[1..];
            }
            if (!TryDecodeBody(body, out var source) || !IsValidSource(source, kind)) return false;
            id = new(kind, null, source);
            return true;
        }
        if (!IsValidSource(value, expected)) return false;
        id = new(expected, null, value);
        return true;
    }

    /// <summary>Accepts library ids (letters, digits, '-' and '_') and service ids of the matching kind.</summary>
    internal static bool IsValidSource(string source, V2IdKind kind)
    {
        if (source.Length is 0 or > MaximumSourceLength) return false;
        if (source.StartsWith("ext-", StringComparison.Ordinal))
        {
            if (source.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) ||
                character is '/' or '\\' or '?' or '#' or '%' or ',')) return false;
            var parsed = LocalLibraryService.ParseExternalResource(source);
            return parsed.isExternal && parsed.type == kind switch
            {
                V2IdKind.Track => "song",
                V2IdKind.Album => "album",
                V2IdKind.Artist => "artist",
                V2IdKind.Playlist => "playlist",
                _ => null
            };
        }
        return source.Length <= 128 && source.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static string Encode(string source) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(source)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecodeBody(string body, out string source)
    {
        source = "";
        if (body.Length == 0 || body.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) return false;
        var padded = body.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        var bytes = new byte[padded.Length];
        if (!Convert.TryFromBase64String(padded, bytes, out var written)) return false;
        try
        {
            source = new UTF8Encoding(false, true).GetString(bytes, 0, written);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        return true;
    }
}
