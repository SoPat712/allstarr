using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Models.Settings;
using allstarr.Services.AppleMusic;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Providers.AppleDownload;

public sealed class AppleDownloadLyricsCapabilityAdapter : IProviderLyricsCapability
{
    private const int MaximumLyricsBytes = 2_000_000;
    private readonly HttpClient http;
    private readonly AppleDownloadSettings settings;
    private readonly IAppleDownloadEndpointDiscovery discovery;

    public AppleDownloadLyricsCapabilityAdapter(
        IHttpClientFactory clients,
        IOptions<AppleDownloadSettings> settings,
        IAppleDownloadEndpointDiscovery discovery)
        : this(clients.CreateClient(AppleDownloadCapabilityAdapter.HttpClientName), settings.Value, discovery)
    {
    }

    public AppleDownloadLyricsCapabilityAdapter(
        HttpClient http,
        AppleDownloadSettings settings,
        IAppleDownloadEndpointDiscovery discovery)
    {
        this.http = http;
        this.settings = settings;
        this.discovery = discovery;
    }

    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Lyrics;

    public string ProviderId => AppleDownloadCapabilityAdapter.StableProviderId;

    public async Task<ProviderOutcome<ProviderLyricsResult>> FetchLyricsAsync(
        ProviderExecutionContext context,
        ProviderLyricsRequest request)
    {
        try
        {
            context.RequireResourceOwner(request.ProviderTrackId, ProviderResourceKind.Track);
            if (!context.ProviderId.Equals(AppleDownloadCapabilityAdapter.StableProviderId, StringComparison.Ordinal) ||
                !context.Policy.AllowsProvider(AppleDownloadCapabilityAdapter.StableProviderId))
                return Failure(ProviderErrorKind.Forbidden);
            if (context.CancellationToken.IsCancellationRequested)
                return Failure(ProviderErrorKind.Canceled);
            if (context.IsExpired(DateTimeOffset.UtcNow))
                return Failure(ProviderErrorKind.CapabilityUnavailable);

            var snapshot = await discovery.DiscoverAsync(context.CancellationToken);
            if (snapshot.State != AppleDownloadEndpointState.Available ||
                snapshot.Capability("synced-lyrics-artifact").State != AppleDownloadCapabilityState.Available)
                return Failure(ProviderErrorKind.CapabilityUnavailable);
            if (!OutboundRequestGuard.TryCreateConfiguredServiceUri(settings.BaseUrl, out var baseUri, out _))
                return Failure(ProviderErrorKind.AccountNeedsConfiguration);

            var endpoint = new Uri(baseUri!, $"api/lyrics/{Uri.EscapeDataString(request.ProviderTrackId.Value)}");
            using var response = await http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return ProviderOutcome<ProviderLyricsResult>.Success(new(
                    ProviderLyricsAvailabilityState.Unavailable, "GAMDL"));
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumLyricsBytes)
                return Failure(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? ProviderErrorKind.AccountNeedsConfiguration
                    : ProviderErrorKind.TransientFailure);
            await response.Content.LoadIntoBufferAsync(MaximumLyricsBytes, context.CancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(context.CancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 8 }, context.CancellationToken);
            var root = document.RootElement;
            var content = root.TryGetProperty("content", out var contentValue) && contentValue.ValueKind == JsonValueKind.String
                ? contentValue.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(content))
                return ProviderOutcome<ProviderLyricsResult>.Success(new(
                    ProviderLyricsAvailabilityState.Unavailable, "GAMDL"));
            var source = root.TryGetProperty("source", out var sourceValue) && sourceValue.ValueKind == JsonValueKind.String
                ? sourceValue.GetString()!
                : "GAMDL";
            var format = root.TryGetProperty("format", out var formatValue) &&
                         Enum.TryParse<ProviderLyricsFormat>(formatValue.GetString(), true, out var parsed)
                ? parsed
                : ProviderLyricsFormat.LineTimed;
            return ProviderOutcome<ProviderLyricsResult>.Success(new(
                ProviderLyricsAvailabilityState.Available, source, format, content));
        }
        catch (OperationCanceledException)
        {
            return Failure(ProviderErrorKind.Canceled);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            return Failure(ProviderErrorKind.TransientFailure);
        }
        catch
        {
            return Failure(ProviderErrorKind.PermanentFailure);
        }
    }

    private static ProviderOutcome<ProviderLyricsResult> Failure(ProviderErrorKind kind) =>
        ProviderOutcome<ProviderLyricsResult>.Failure(new ProviderError(kind));
}
