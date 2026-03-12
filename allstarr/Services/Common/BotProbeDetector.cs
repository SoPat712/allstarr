using System.Globalization;

namespace allstarr.Services.Common;

/// <summary>
/// Identifies high-confidence internet scanner paths that should never hit Jellyfin.
/// </summary>
public static class BotProbeDetector
{
    private static readonly string[] PrefixMatches =
    {
        ".env",
        ".git",
        ".hg",
        ".svn",
        "_ignition/",
        "debug/default",
        "vendor/",
        "public/vendor/"
    };

    private static readonly string[] FragmentMatches =
    {
        "/.env",
        "/.git/",
        "/vendor/",
        "phpunit",
        "laravel-filemanager",
        "eval-stdin.php"
    };

    private static readonly string[] SuffixMatches =
    {
        ".php"
    };

    public static bool IsHighConfidenceProbePath(string? rawPath)
    {
        var path = NormalizePath(rawPath);
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (path.Equals("wp", StringComparison.Ordinal) ||
            path.StartsWith("wp-", StringComparison.Ordinal) ||
            path.StartsWith("wp/", StringComparison.Ordinal) ||
            path.Equals("wordpress", StringComparison.Ordinal) ||
            path.StartsWith("wordpress/", StringComparison.Ordinal))
        {
            return true;
        }

        if (PrefixMatches.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return true;
        }

        if (FragmentMatches.Any(fragment => path.Contains(fragment, StringComparison.Ordinal)))
        {
            return true;
        }

        return SuffixMatches.Any(suffix => path.EndsWith(suffix, StringComparison.Ordinal));
    }

    public static bool IsHighConfidenceProbeUrl(string? rawUrlOrPath)
    {
        if (string.IsNullOrWhiteSpace(rawUrlOrPath))
        {
            return false;
        }

        if (Uri.TryCreate(rawUrlOrPath, UriKind.Absolute, out var uri))
        {
            return IsHighConfidenceProbePath(uri.AbsolutePath);
        }

        return IsHighConfidenceProbePath(rawUrlOrPath);
    }

    private static string NormalizePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        var path = rawPath.Trim();

        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath;
        }

        path = Uri.UnescapeDataString(path)
            .Replace('\\', '/')
            .TrimStart('/');

        while (path.Contains("//", StringComparison.Ordinal))
        {
            path = path.Replace("//", "/", StringComparison.Ordinal);
        }

        return path.ToLower(CultureInfo.InvariantCulture);
    }
}
