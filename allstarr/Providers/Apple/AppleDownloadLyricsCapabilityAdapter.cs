using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Providers.AppleMusicKit;
using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Models.Settings;
using allstarr.Services.AppleMusic;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Providers.AppleDownload;

public sealed class AppleDownloadLyricsCapabilityAdapter : IProviderLyricsCapability, IInternalLyricsCapability
{
    private const int MaximumLyricsBytes = 2_000_000;
    private readonly HttpClient http;
    private readonly AppleMusicClient client;
    private readonly AppleDownloadSettings settings;
    private readonly IAppleDownloadEndpointDiscovery discovery;
    private readonly IApplicationCache? cache;

    public AppleDownloadLyricsCapabilityAdapter(
        IHttpClientFactory clients,
        IOptions<AppleDownloadSettings> settings,
        IAppleDownloadEndpointDiscovery discovery, AppleMusicClient client, IApplicationCache? cache = null)
        : this(clients.CreateClient(AppleDownloadCapabilityAdapter.HttpClientName), settings.Value, discovery, client, cache)
    {
    }

    public AppleDownloadLyricsCapabilityAdapter(
        HttpClient http,
        AppleDownloadSettings settings,
        IAppleDownloadEndpointDiscovery discovery, AppleMusicClient client, IApplicationCache? cache = null)
    {
        this.http = http;
        this.client = client;
        this.settings = settings;
        this.discovery = discovery;
        this.cache = cache;
    }

    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Lyrics;

    public string ProviderId => AppleDownloadCapabilityAdapter.StableProviderId;

    private sealed record SourceSnapshot(string BaseUrl) : LyricsSourceSnapshot(
        LyricsConfigurationId.From("apple-musickit:lyrics:1:" + BaseUrl), LyricsSpeculationMode.RecentHealthy, true);
    ValueTask<LyricsSourceSnapshot> IInternalLyricsCapability.CaptureSourceAsync(CancellationToken token) =>
        ValueTask.FromResult<LyricsSourceSnapshot>(new SourceSnapshot(settings.BaseUrl));
    async Task<ProviderOutcome<ProviderLyricsResult>> IInternalLyricsCapability.FetchAsync(
        ProviderExecutionContext context, ProviderLyricsRequest request, LyricsExecution execution)
    {
        var source = (SourceSnapshot)execution.Source;
        return await FetchCoreAsync(context, request, execution.Completion, source.BaseUrl, execution.Snapshot);
    }
    public Task<ProviderOutcome<ProviderLyricsResult>> FetchLyricsAsync(ProviderExecutionContext context, ProviderLyricsRequest request) =>
        FetchCoreAsync(context, request, LyricsCompletionMode.RequestBound, settings.BaseUrl, null);
    private async Task<ProviderOutcome<ProviderLyricsResult>> FetchCoreAsync(ProviderExecutionContext context,
        ProviderLyricsRequest request, LyricsCompletionMode completion, string configuredUrl,
        LyricsExecutionSnapshot? executionSnapshot)
    {
        try
        {
            var accountError = AppleMusicClient.Validate(context, personal: true);
            if (accountError != null) return ProviderOutcome<ProviderLyricsResult>.Failure(accountError);
            context.RequireResourceOwner(request.ProviderTrackId, ProviderResourceKind.Track);
            if (!context.ProviderId.Equals(AppleDownloadCapabilityAdapter.StableProviderId, StringComparison.Ordinal) ||
                !context.Policy.AllowsProvider(AppleDownloadCapabilityAdapter.StableProviderId))
                return Failure(ProviderErrorKind.Forbidden);
            if (context.CancellationToken.IsCancellationRequested)
                return Failure(ProviderErrorKind.Canceled);
            if (context.IsExpired(DateTimeOffset.UtcNow))
                return Failure(ProviderErrorKind.CapabilityUnavailable);

            if (!OutboundRequestGuard.TryCreateConfiguredServiceUri(configuredUrl, out var baseUri, out _))
                return Failure(ProviderErrorKind.AccountNeedsConfiguration);
            var cacheKey = "lyrics:v4:apple-musickit:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(new { context.Actor, context.Account, request, configuredUrl })));
            try
            {
                var stored = cache == null ? null : await LyricsAwait.CompleteAsync(cache.GetAsync<ProviderLyricsResult>(cacheKey), completion, context.CancellationToken);
                if (stored is { Availability: ProviderLyricsAvailabilityState.Available, Content: not null })
                    return ProviderOutcome<ProviderLyricsResult>.Success(stored);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { }
            if (completion == LyricsCompletionMode.CacheOnly)
                return ProviderOutcome<ProviderLyricsResult>.Success(new(
                    ProviderLyricsAvailabilityState.Unavailable, ProviderId));

            var snapshot = executionSnapshot == null
                ? await discovery.DiscoverAsync(context.CancellationToken)
                : await executionSnapshot.GetOrCreateAsync(
                    "apple-download-capabilities",
                    discovery.DiscoverAsync,
                    context.CancellationToken);
            if (snapshot.State != AppleDownloadEndpointState.Available ||
                snapshot.Capability("synced-lyrics-artifact").State != AppleDownloadCapabilityState.Available)
                return Failure(ProviderErrorKind.CapabilityUnavailable);

            var resolved = await client.ResolveCatalogTrackAsync(context, request.ProviderTrackId.Value);
            if (!resolved.IsSuccess) return ProviderOutcome<ProviderLyricsResult>.Failure(resolved.Error!);
            var endpoint = new Uri(baseUri!, $"api/lyrics/{Uri.EscapeDataString(resolved.RequireValue())}");
            using var outbound = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await client.SendSidecarAsync(context, http, outbound, context.CancellationToken);
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
            ProviderLyricsResult result;
            if (content.TrimStart().StartsWith('<'))
            {
                var parsedTtml = TtmlLyricsParser.Parse(content, source);
                if (!parsedTtml.IsSuccess) return parsedTtml;
                result = parsedTtml.RequireValue();
                if (request.PreferredFormat == ProviderLyricsFormat.PlainText && result.TimedLines != null)
                    result = new(result.Availability, source, ProviderLyricsFormat.PlainText,
                        string.Join('\n', result.TimedLines.Select(line => line.Text)), result.Revision);
            }
            else result = new(ProviderLyricsAvailabilityState.Available, source, format, content);
            try
            {
                if (cache != null)
                    await LyricsAwait.CompleteAsync(cache.SetAsync(cacheKey, result, CacheExtensions.LyricsTTL), completion, context.CancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { }
            return ProviderOutcome<ProviderLyricsResult>.Success(result);
        }
        catch (OperationCanceledException)
        {
            return Failure(ProviderErrorKind.Canceled);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or System.Xml.XmlException or ArgumentException)
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
