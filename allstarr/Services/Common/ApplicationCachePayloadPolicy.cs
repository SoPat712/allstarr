using System.Text.RegularExpressions;

namespace allstarr.Services.Common;

public static class ApplicationCachePayloadPolicy
{
    public static bool IsValidKey(string key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 512;

    internal static Func<string, bool> PatternMatcher(string pattern)
    {
        if (!IsValidKey(pattern)) return _ => false;
        var expression = new Regex("\\A" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "\\z",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking);
        return expression.IsMatch;
    }

    public static bool IsMemoryEligible(string key) =>
        ApplicationCachePolicyRegistry.Resolve(key).StorageTier ==
        ApplicationCacheStorageTier.Metadata;
}
