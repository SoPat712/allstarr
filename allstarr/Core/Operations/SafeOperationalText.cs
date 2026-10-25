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
        var hasRedactedQuery = value.EndsWith("?<redacted>", StringComparison.Ordinal);
        var parseValue = hasRedactedQuery
            ? value[..^"<redacted>".Length]
            : value;
        if (!Uri.TryCreate(parseValue, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return "<redacted-url>";
        }

        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0 && !hasRedactedQuery)
        {
            return $"{uri.Scheme}://{authority}{uri.AbsolutePath}";
        }

        return $"{uri.Scheme}://{authority}{uri.AbsolutePath}?<redacted>";
    }

    [GeneratedRegex("https?://(?:[^\\s,;\\\"'<>?#]|<redacted>)+(?:[?#](?:[^\\s\\\"'<>]|<redacted>)*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(
        @"\b(token|password|secret|cookie|authorization|api[_-]?key|arl|code|state|nonce|code_verifier|client_secret)\s*[=:]\s*[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();
}
