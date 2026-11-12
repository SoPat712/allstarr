using allstarr.Core.Caching;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;

namespace allstarr.Core.Protocols;

public sealed record LibraryBackendBrand(string Id, string Name);

public sealed class LibraryBackendBrandResolver(
    IBackendLibraryPermissionSource source,
    IHttpClientFactory clients,
    IApplicationCache cache,
    IPlatformClock clock,
    IConfiguration configuration)
{
    public const string HttpClientName = "LibraryBackendBrand";
    private static readonly LibraryBackendBrand Jellyfin = new("jellyfin", "Jellyfin");
    private static readonly LibraryBackendBrand Subsonic = new("subsonic", "Subsonic server");
    private static readonly LibraryBackendBrand Navidrome = new("navidrome", "Navidrome");
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public async Task<LibraryBackendBrand> ResolveAsync(ProtocolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(configuration["Backend:Type"], "Subsonic", StringComparison.OrdinalIgnoreCase))
            return Jellyfin;
        if (context.Protocol != ProtocolKind.Subsonic || context.Principal == null) return Subsonic;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, cancellationToken);
        var remaining = context.Deadline - clock.UtcNow;
        if (remaining <= TimeSpan.Zero) return Subsonic;
        deadline.CancelAfter(remaining < ProbeTimeout ? remaining : ProbeTimeout);
        string? key = null;
        try
        {
            using var request = await source.CreateRequestAsync(context, deadline.Token);
            if (request?.RequestUri is not { IsAbsoluteUri: true } endpoint) return Subsonic;

            request.RequestUri = new Uri(endpoint, "ping.view");
            key = CacheKey(configuration["Subsonic:Url"] ?? endpoint.AbsoluteUri, context.BackendInstanceId);
            var cached = await cache.GetAsync<BrandEntry>(key);
            if (cached != null && cached.ExpiresAt > clock.UtcNow) return cached.Brand;

            using var client = clients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode && response.StatusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden)
                return await StoreAsync(key, Subsonic, false);

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(deadline.Token), cancellationToken: deadline.Token);
            if (!TryProperty(document.RootElement, "subsonic-response", out var envelope) || envelope.ValueKind != JsonValueKind.Object)
                return await StoreAsync(key, Subsonic, false);

            var brand = string.Equals(ReadString(envelope, "type"), "Navidrome", StringComparison.OrdinalIgnoreCase) ||
                        ContainsNavidrome(ReadString(envelope, "serverVersion"))
                ? Navidrome : Subsonic;
            var succeeded = response.IsSuccessStatusCode &&
                            string.Equals(ReadString(envelope, "status"), "ok", StringComparison.OrdinalIgnoreCase);
            return await StoreAsync(key, brand, succeeded);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return await StoreAsync(key, Subsonic, false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            return await StoreAsync(key, Subsonic, false);
        }
    }

    private async Task<LibraryBackendBrand> StoreAsync(string? key, LibraryBackendBrand brand, bool succeeded)
    {
        if (key == null) return brand;
        var lifetime = succeeded ? Lifetime : FailureLifetime;
        await cache.SetAsync(key, new BrandEntry(brand, clock.UtcNow.Add(lifetime)), lifetime);
        return brand;
    }

    private static string CacheKey(string configuredUrl, string backendInstance)
    {
        var url = configuredUrl;
        if (Uri.TryCreate(configuredUrl, UriKind.Absolute, out var endpoint))
        {
            var builder = new UriBuilder(endpoint) { UserName = "", Password = "", Query = "", Fragment = "" };
            url = builder.Uri.AbsoluteUri.TrimEnd('/');
        }
        var identity = JsonSerializer.Serialize(new { url, backendInstance });
        return "protocol:metadata:v2:backend-brand:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static bool ContainsNavidrome(string? version)
    {
        if (version == null) return false;
        const string name = "Navidrome";
        var start = 0;
        while ((start = version.IndexOf(name, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = start + name.Length;
            if ((start == 0 || !char.IsLetterOrDigit(version[start - 1])) &&
                (end == version.Length || !char.IsLetterOrDigit(version[end]))) return true;
            start = end;
        }
        return false;
    }

    private sealed record BrandEntry(LibraryBackendBrand Brand, DateTimeOffset ExpiresAt);
}
