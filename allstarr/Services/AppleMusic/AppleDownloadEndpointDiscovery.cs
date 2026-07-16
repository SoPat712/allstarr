using System.Net;
using System.Text.Json;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Services.AppleMusic;

public enum AppleDownloadEndpointState
{
    NeedsConfiguration,
    Unreachable,
    Incompatible,
    NeedsAuthentication,
    Degraded,
    Available
}

public enum AppleDownloadCapabilityState
{
    Unsupported,
    Degraded,
    Available
}

public sealed record AppleDownloadCapabilityStatus(
    string Id,
    AppleDownloadCapabilityState State,
    string? ReasonCode = null);

public sealed record AppleDownloadEndpointSnapshot(
    AppleDownloadEndpointState State,
    string? ReasonCode,
    string? ApiVersion,
    bool Authenticated,
    IReadOnlyList<AppleDownloadCapabilityStatus> Capabilities)
{
    public AppleDownloadCapabilityStatus Capability(string id) =>
        Capabilities.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ??
        new AppleDownloadCapabilityStatus(id, AppleDownloadCapabilityState.Unsupported, "not_advertised");
}

public interface IAppleDownloadEndpointDiscovery
{
    Task<AppleDownloadEndpointSnapshot> DiscoverAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Discovers an explicitly configured, operator-managed Apple download endpoint.
/// The named client must not follow redirects; no Apple credentials are sent by
/// Allstarr during discovery.
/// </summary>
public sealed class AppleDownloadEndpointDiscovery(
    IHttpClientFactory httpClientFactory,
    IOptions<AppleDownloadSettings> settings) : IAppleDownloadEndpointDiscovery
{
    private const int MaximumPayloadBytes = 64 * 1024;
    private static readonly IReadOnlyDictionary<string, string[]> RequiredRoutes =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [ProviderCapabilities.Metadata] = ["metadata-search-song", "metadata-song"],
            [ProviderCapabilities.Streaming] = ["stream-audio-song"],
            [ProviderCapabilities.Download] = ["download-audio-song"]
        };
    private static readonly string[] GranularFeatureIds =
    [
        "metadata-search-song", "metadata-song", "metadata-album", "metadata-artist",
        "stream-audio-song", "download-audio-song", "download-album", "download-playlist",
        "library-read", "stream-music-video", "synced-lyrics-artifact",
        "tagging-artwork", "codec-alac", "codec-aac"
    ];
    private static readonly HashSet<string> ImplementedFeatureIds = new(
    [
        "metadata-search-song", "metadata-song", "stream-audio-song", "download-audio-song"
    ], StringComparer.OrdinalIgnoreCase);

    public async Task<AppleDownloadEndpointSnapshot> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OutboundRequestGuard.TryCreateConfiguredServiceUri(
                settings.Value.BaseUrl,
                out var baseUri,
                out _))
        {
            return Snapshot(AppleDownloadEndpointState.NeedsConfiguration, "invalid_or_missing_endpoint");
        }
        var endpointBase = baseUri!;

        var client = httpClientFactory.CreateClient("AppleDownloadDiscovery");
        try
        {
            var manifest = await GetObjectAsync(client, new Uri(endpointBase, "api/capabilities"), cancellationToken);
            if (manifest.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Snapshot(AppleDownloadEndpointState.NeedsAuthentication, "discovery_unauthorized");
            }
            if (manifest.Payload is not { } manifestRoot)
            {
                var incompatible = manifest.Status == HttpStatusCode.NotFound ||
                                   manifest.Status is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest;
                return Snapshot(
                    incompatible
                        ? AppleDownloadEndpointState.Incompatible
                        : AppleDownloadEndpointState.Unreachable,
                    manifest.Status == HttpStatusCode.NotFound
                        ? "gateway_manifest_missing"
                        : incompatible
                            ? "redirect_rejected"
                        : "capability_manifest_unavailable");
            }

            var apiVersion = ReadString(manifestRoot, "sidecarApiVersion", "api_version", "apiVersion");
            if (string.IsNullOrWhiteSpace(apiVersion) || !apiVersion.StartsWith("1.", StringComparison.Ordinal))
            {
                return Snapshot(AppleDownloadEndpointState.Incompatible, "unsupported_api_version", apiVersion);
            }

            var advertised = ReadSupportedCapabilities(manifestRoot);
            var capabilityStatuses = RequiredRoutes.Select(pair =>
            {
                var missing = pair.Value.Where(id => !advertised.Contains(id)).ToArray();
                return new AppleDownloadCapabilityStatus(
                    pair.Key,
                    missing.Length == 0
                        ? AppleDownloadCapabilityState.Available
                        : AppleDownloadCapabilityState.Unsupported,
                    missing.Length == 0 ? null : "required_routes_not_advertised");
            }).Concat(GranularFeatureIds.Select(id =>
            {
                var isAdvertised = advertised.Contains(id);
                var isImplemented = ImplementedFeatureIds.Contains(id);
                return new AppleDownloadCapabilityStatus(
                    id,
                    isAdvertised && isImplemented
                        ? AppleDownloadCapabilityState.Available
                        : AppleDownloadCapabilityState.Unsupported,
                    !isAdvertised
                        ? "not_advertised"
                        : isImplemented
                            ? null
                            : "adapter_not_implemented");
            })).ToList();

            var health = await GetObjectAsync(client, new Uri(endpointBase, "api/health"), cancellationToken);
            if (health.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return WithRuntimeState(capabilityStatuses, AppleDownloadEndpointState.NeedsAuthentication,
                    "health_unauthorized", apiVersion, false);
            }
            if (health.Payload is not { } healthRoot)
            {
                return WithRuntimeState(capabilityStatuses, AppleDownloadEndpointState.Degraded,
                    "health_unavailable", apiVersion, false);
            }

            var healthy = ReadTrue(healthRoot, "staged") &&
                          ReadTrue(healthRoot, "daemon_running") &&
                          ReadTrue(healthRoot, "wrapper_healthy");
            var authenticated = ReadTrue(healthRoot, "logged_in", "authenticated");
            if (healthy)
            {
                var account = await GetObjectAsync(client, new Uri(endpointBase, "api/me"), cancellationToken);
                if (account.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    authenticated = false;
                }
                else if (account.Payload is { } accountRoot)
                {
                    authenticated = authenticated || IsAuthenticated(accountRoot);
                }
            }

            if (!authenticated)
            {
                return WithRuntimeState(capabilityStatuses, AppleDownloadEndpointState.NeedsAuthentication,
                    "endpoint_authentication_required", apiVersion, false);
            }

            if (!healthy)
            {
                return WithRuntimeState(capabilityStatuses, AppleDownloadEndpointState.Degraded,
                    "endpoint_health_degraded", apiVersion, true);
            }

            var anySupported = capabilityStatuses.Any(item => item.State == AppleDownloadCapabilityState.Available);
            return new AppleDownloadEndpointSnapshot(
                anySupported ? AppleDownloadEndpointState.Available : AppleDownloadEndpointState.Incompatible,
                anySupported ? null : "no_supported_features",
                apiVersion,
                true,
                capabilityStatuses);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Snapshot(AppleDownloadEndpointState.Unreachable, "endpoint_unreachable");
        }
    }

    private static AppleDownloadEndpointSnapshot WithRuntimeState(
        IReadOnlyList<AppleDownloadCapabilityStatus> capabilities,
        AppleDownloadEndpointState state,
        string reason,
        string? version,
        bool authenticated) => new(
            state,
            reason,
            version,
            authenticated,
            capabilities.Select(item => item.State == AppleDownloadCapabilityState.Unsupported
                ? item
                : item with { State = AppleDownloadCapabilityState.Degraded, ReasonCode = reason }).ToList());

    private static AppleDownloadEndpointSnapshot Snapshot(
        AppleDownloadEndpointState state,
        string reason,
        string? version = null) => new(state, reason, version, false,
        RequiredRoutes.Keys.Concat(GranularFeatureIds).Select(id => new AppleDownloadCapabilityStatus(
            id,
            state == AppleDownloadEndpointState.Incompatible
                ? AppleDownloadCapabilityState.Unsupported
                : AppleDownloadCapabilityState.Degraded,
            reason)).ToList());

    private static async Task<(HttpStatusCode Status, JsonElement? Payload)> GetObjectAsync(
        HttpClient client,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumPayloadBytes)
        {
            return (response.StatusCode, null);
        }

        await response.Content.LoadIntoBufferAsync(MaximumPayloadBytes, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
        return (response.StatusCode, document.RootElement.Clone());
    }

    private static HashSet<string> ReadSupportedCapabilities(JsonElement root)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in capabilities.EnumerateArray())
        {
            if (ReadString(item, "state") is { } state &&
                !state.Equals("supported", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (ReadString(item, "id") is { Length: > 0 } id) result.Add(id);
        }
        return result;
    }

    private static bool IsAuthenticated(JsonElement root)
    {
        if (ReadTrue(root, "logged_in", "authenticated")) return true;
        if (root.TryGetProperty("auth", out var auth) && auth.ValueKind == JsonValueKind.Object)
        {
            var state = ReadString(auth, "state");
            return state is not null && state is "logged_in" or "authenticated" or "ready";
        }
        return false;
    }

    private static bool ReadTrue(JsonElement root, params string[] names) => names.Any(name =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True);

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }
}
