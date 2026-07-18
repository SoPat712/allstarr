using System.Text.RegularExpressions;

namespace allstarr.Core.Operations;

public static partial class SafeOperationalText
{
    public static string? Sanitize(string? value, int maxLength = 1000)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        sanitized = UrlPattern().Replace(sanitized, match => SanitizeUrl(match.Value));
        sanitized = CredentialPattern().Replace(sanitized, "$1=<redacted>");
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength];
    }

    private static string SanitizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return "<redacted-url>";
        }

        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return $"{uri.Scheme}://{authority}{uri.AbsolutePath}";
        }

        var safeQuery = string.Join("&", query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var pieces = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0]);
            return SensitiveQueryKey().IsMatch(key)
                ? $"{pieces[0]}=<redacted>"
                : part;
        }));
        return $"{uri.Scheme}://{authority}{uri.AbsolutePath}?{safeQuery}";
    }

    [GeneratedRegex(@"https?://[^\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex("token|password|secret|cookie|authorization|api.?key|client.?id|private.?key|arl|signature|sig|expires", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveQueryKey();

    [GeneratedRegex(
        @"\b(token|password|secret|cookie|authorization|api[_-]?key|arl)\s*[=:]\s*[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();
}
