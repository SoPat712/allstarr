using System.Text.Json;

namespace allstarr.Services.Common;

public static class ListenBrainzServiceEndpoint
{
    private static readonly Uri DefaultBaseUri = new("https://api.listenbrainz.org/1/");

    public static Uri FromSecret(JsonElement secret) =>
        Parse(secret.TryGetProperty("baseUrl", out var value) ? value.GetString() : null);

    public static Uri FromSecret(IReadOnlyDictionary<string, string>? secret) =>
        Parse(secret?.FirstOrDefault(item =>
            string.Equals(item.Key, "baseUrl", StringComparison.OrdinalIgnoreCase)).Value);

    public static Uri Route(Uri baseUri, string relativePath) =>
        new(baseUri, relativePath.TrimStart('/'));

    private static Uri Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultBaseUri;
        if (!OutboundRequestGuard.TryCreateConfiguredServiceUri(value, out var result, out _) ||
            result!.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The ListenBrainz-compatible service address must be an exact HTTPS base URL.");
        return result;
    }
}
