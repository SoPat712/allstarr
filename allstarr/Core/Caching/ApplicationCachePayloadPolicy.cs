using System.Text.RegularExpressions;

namespace allstarr.Core.Caching;

public static class ApplicationCachePayloadPolicy
{
    private static readonly string[] RestartSafePrefixes =
    [
        "protocol:metadata:v1:",
        "protocol:metadata:v2:",
        "search:v3:",
        "search:v4:"
    ];

    public static bool IsValidKey(string key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 512;

    internal static Func<string, bool> PatternMatcher(string pattern)
    {
        if (!IsValidKey(pattern)) return _ => false;
        var expression = new Regex("\\A" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "\\z",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking);
        return expression.IsMatch;
    }

    internal static bool IsRestartSafe(string key) =>
        key.StartsWith("protocol:metadata:v1:", StringComparison.Ordinal) ||
        key.StartsWith("protocol:metadata:v2:", StringComparison.Ordinal) ||
        key.StartsWith("search:v3:", StringComparison.Ordinal) ||
        key.StartsWith("search:v4:", StringComparison.Ordinal);

    public static bool IsMemoryEligible(string key) =>
        ApplicationCachePolicyRegistry.Resolve(key).StorageTier ==
        ApplicationCacheStorageTier.Metadata;

    public static bool CanPatternTouchDisk(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;

        var wildcardIndex = pattern.IndexOfAny(['*', '?']);
        var prefix = wildcardIndex >= 0 ? pattern[..wildcardIndex] : pattern;
        if (string.IsNullOrWhiteSpace(prefix)) return true;

        if (!ApplicationCachePolicyRegistry.TryClassify(prefix, out var category)) return true;

        var policy = ApplicationCachePolicyRegistry.Resolve(category);
        if (policy.StorageTier != ApplicationCacheStorageTier.Metadata) return true;

        foreach (var safePrefix in RestartSafePrefixes)
        {
            if (prefix.StartsWith(safePrefix, StringComparison.Ordinal) ||
                safePrefix.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
