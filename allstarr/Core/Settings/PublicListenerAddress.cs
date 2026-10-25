namespace allstarr.Core.Settings;

internal static class PublicListenerAddress
{
    internal const string Key = "Gateway:PublicListenerUrl";
    internal static string Normalize(string raw)
    {
        if (raw.Length > 2048 || raw.Any(char.IsControl) || raw.Contains('\\')) throw Invalid();
        var value = raw.Trim();
        if (value.Length == 0) return "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || string.IsNullOrWhiteSpace(uri.Host)) throw Invalid();
        var decoded = Uri.UnescapeDataString(value);
        if (decoded.Any(char.IsControl) || decoded.Contains('\\')) throw Invalid();
        return uri.AbsoluteUri.TrimEnd('/');
    }
    private static ArgumentException Invalid() => new("Use an HTTP or HTTPS music-client address without credentials, query, fragment or control characters.");
}
