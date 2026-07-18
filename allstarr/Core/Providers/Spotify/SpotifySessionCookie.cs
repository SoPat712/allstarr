using System.Text.Json;

namespace allstarr.Core.Providers.Spotify;

internal static class SpotifySessionCookie
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();

        if (candidate.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    var name = new string(property.Name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                    if (name is "sessioncookie" or "spdc" or "cookie" && property.Value.ValueKind == JsonValueKind.String)
                        return Normalize(property.Value.GetString());
                }
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (candidate.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            candidate = candidate["Cookie:".Length..].Trim();

        foreach (var part in candidate.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator > 0 && part[..separator].Trim().Equals("sp_dc", StringComparison.OrdinalIgnoreCase))
                return EmptyToNull(part[(separator + 1)..].Trim());
        }

        return candidate.StartsWith("sp_dc=", StringComparison.OrdinalIgnoreCase)
            ? EmptyToNull(candidate["sp_dc=".Length..].Trim())
            : EmptyToNull(candidate);
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
