using System.Text;
using System.Text.Json;

namespace allstarr.Core.Providers.Lyrics;

internal sealed record MusixmatchCredential(string UserToken, string? AppVersion = null, string? AppBuild = null)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryParse(string? input, out MusixmatchCredential credential)
    {
        credential = null!;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 65_536) return false;
        var value = input.Trim();
        if (!value.Contains('[') && !value.Contains('\r') && !value.Contains('\n'))
        {
            if (!IsToken(value)) return false;
            credential = new(value);
            return true;
        }

        string? token = null, version = null, build = null;
        foreach (var rawLine in input.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('[')) continue;
            var end = line.IndexOf(']');
            if (end < 0) continue;
            var label = line[1..end];
            if (!label.Equals("UserToken", StringComparison.OrdinalIgnoreCase) &&
                !label.Equals("AppVersion", StringComparison.OrdinalIgnoreCase) &&
                !label.Equals("AppBuild", StringComparison.OrdinalIgnoreCase)) continue;
            var field = line[(end + 1)..].Trim();
            if (field.StartsWith(':') || field.StartsWith('=')) field = field[1..].Trim();
            if (label.Equals("UserToken", StringComparison.OrdinalIgnoreCase))
            {
                if (token != null || !IsToken(field)) return false;
                token = field;
            }
            else if (label.Equals("AppVersion", StringComparison.OrdinalIgnoreCase))
            {
                if (version != null || !IsHeaderField(field)) return false;
                version = field;
            }
            else
            {
                if (build != null || !IsHeaderField(field)) return false;
                build = field;
            }
        }

        if (token == null) return false;
        credential = new(token, version, build);
        return true;
    }

    public static bool TryRead(ReadOnlyMemory<byte> secret, out MusixmatchCredential credential)
    {
        credential = null!;
        if (secret.Length > 262_144) return false;
        try
        {
            using var document = JsonDocument.Parse(StrictUtf8.GetString(secret.Span), new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            string? token = null, version = null, build = null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("userToken", StringComparison.OrdinalIgnoreCase) &&
                    !property.Name.Equals("appVersion", StringComparison.OrdinalIgnoreCase) &&
                    !property.Name.Equals("appBuild", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(property.Name)) return false;
                if (property.Value.ValueKind == JsonValueKind.Null &&
                    !property.Name.Equals("userToken", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.String) return false;
                var value = property.Value.GetString();
                if (property.Name.Equals("userToken", StringComparison.OrdinalIgnoreCase)) token = value;
                else
                {
                    if (!IsHeaderField(value)) return false;
                    if (property.Name.Equals("appVersion", StringComparison.OrdinalIgnoreCase)) version = value;
                    else build = value;
                }
            }
            if (!TryParse(token, out var parsed)) return false;
            credential = parsed with { AppVersion = version ?? parsed.AppVersion, AppBuild = build ?? parsed.AppBuild };
            return true;
        }
        catch (JsonException) { return false; }
        catch (DecoderFallbackException) { return false; }
    }

    public byte[] ToSecretBytes()
    {
        var fields = new Dictionary<string, string> { ["userToken"] = UserToken };
        if (AppVersion != null) fields["appVersion"] = AppVersion;
        if (AppBuild != null) fields["appBuild"] = AppBuild;
        return JsonSerializer.SerializeToUtf8Bytes(fields);
    }

    private static bool IsToken(string value) => value.Length is > 0 and <= 4_000 &&
        value.Any(char.IsAsciiLetterOrDigit) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || "-_.~+/=&%".Contains(character));

    private static bool IsHeaderField(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
}
