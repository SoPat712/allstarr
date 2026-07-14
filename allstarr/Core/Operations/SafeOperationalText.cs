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
        sanitized = UrlPattern().Replace(sanitized, "<redacted-url>");
        sanitized = CredentialPattern().Replace(sanitized, "$1=<redacted>");
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength];
    }

    [GeneratedRegex(@"https?://[^\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(
        @"\b(token|password|secret|cookie|authorization|api[_-]?key|arl)\s*[=:]\s*[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();
}
