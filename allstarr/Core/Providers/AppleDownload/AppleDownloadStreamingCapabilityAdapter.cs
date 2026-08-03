using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Models.Settings;
using allstarr.Services.AppleMusic;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Providers.AppleDownload;

public sealed class AppleDownloadStreamingCapabilityAdapter : IProviderStreamingCapability
{
    private readonly HttpClient http;
    private readonly AppleDownloadSettings settings;
    private readonly IAppleDownloadEndpointDiscovery discovery;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AppleDownloadStreamingCapabilityAdapter(
        IHttpClientFactory clients,
        IOptions<AppleDownloadSettings> settings,
        IAppleDownloadEndpointDiscovery discovery)
        : this(clients.CreateClient(AppleDownloadCapabilityAdapter.HttpClientName), settings.Value, discovery) { }

    public AppleDownloadStreamingCapabilityAdapter(
        HttpClient http,
        AppleDownloadSettings settings,
        IAppleDownloadEndpointDiscovery discovery)
    {
        this.http = http;
        this.settings = settings;
        this.discovery = discovery;
    }

    public string ProviderId => AppleDownloadCapabilityAdapter.StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Streaming;

    public async Task<ProviderOutcome<ProviderStreamLease>> GetStreamLeaseAsync(
        ProviderExecutionContext context,
        ProviderStreamLeaseRequest request)
    {
        var error = Validate(context, request.TrackId);
        if (error != null) return ProviderOutcome<ProviderStreamLease>.Failure(error);
        try
        {
            var snapshot = await discovery.DiscoverAsync(context.CancellationToken);
            if (!Available(snapshot))
                return ProviderOutcome<ProviderStreamLease>.Failure(new(ErrorFor(snapshot.State)));
            if (!OutboundRequestGuard.TryCreateConfiguredServiceUri(settings.BaseUrl, out var baseUri, out _))
                return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
            var quality = AppleDownloadCapabilityAdapter.Quality(request.RequestedQuality, settings.Quality);
            var source = new Uri(baseUri!,
                $"api/stream/{Uri.EscapeDataString(request.TrackId.Value)}?quality={Uri.EscapeDataString(quality)}");
            return ProviderOutcome<ProviderStreamLease>.Success(new(
                LeaseId(request.TrackId.Value, quality),
                source,
                DateTimeOffset.UtcNow.AddMinutes(2),
                supportsByteRanges: false,
                supportsSeeking: false,
                Media,
                ProviderStreamRetryBehavior.RetrySameLeaseOnce,
                OpenAsync));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch (HttpRequestException)
        {
            return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.TransientFailure));
        }
        catch
        {
            return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.PermanentFailure));
        }
    }

    public async Task<ProviderOutcome<ProviderStreamProbeResult>> ProbeStreamAsync(
        ProviderExecutionContext context,
        ProviderStreamLeaseRequest request)
    {
        var error = Validate(context, request.TrackId);
        if (error != null) return ProviderOutcome<ProviderStreamProbeResult>.Failure(error);
        try
        {
            var snapshot = await discovery.DiscoverAsync(context.CancellationToken);
            var available = Available(snapshot);
            return ProviderOutcome<ProviderStreamProbeResult>.Success(new(
                available, DateTimeOffset.UtcNow, available ? Media : null));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderStreamProbeResult>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch
        {
            return ProviderOutcome<ProviderStreamProbeResult>.Failure(new(ProviderErrorKind.TransientFailure));
        }
    }

    private static bool Available(AppleDownloadEndpointSnapshot snapshot) =>
        snapshot.State == AppleDownloadEndpointState.Available &&
        snapshot.Capability(ProviderCapabilities.Streaming).State == AppleDownloadCapabilityState.Available &&
        snapshot.Capability("stream-audio-song").State == AppleDownloadCapabilityState.Available;

    private static ProviderErrorKind ErrorFor(AppleDownloadEndpointState state) => state switch
    {
        AppleDownloadEndpointState.NeedsConfiguration => ProviderErrorKind.AccountNeedsConfiguration,
        AppleDownloadEndpointState.NeedsAuthentication => ProviderErrorKind.Unauthorized,
        AppleDownloadEndpointState.Incompatible => ProviderErrorKind.NotSupported,
        _ => ProviderErrorKind.CapabilityUnavailable
    };

    private static ProviderError? Validate(
        ProviderExecutionContext context,
        ProviderExternalResourceId trackId)
    {
        ArgumentNullException.ThrowIfNull(context);
        try { context.RequireResourceOwner(trackId, ProviderResourceKind.Track); }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException)
        { return new(ProviderErrorKind.Forbidden); }
        if (!context.ProviderId.Equals(AppleDownloadCapabilityAdapter.StableProviderId, StringComparison.Ordinal) ||
            !context.Policy.AllowsProvider(AppleDownloadCapabilityAdapter.StableProviderId))
            return new(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested)
            return new(ProviderErrorKind.Canceled);
        return context.IsExpired(DateTimeOffset.UtcNow)
            ? new(ProviderErrorKind.CapabilityUnavailable)
            : null;
    }

    private async Task<HttpResponseMessage> OpenAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return response;
        if (response.RequestMessage?.RequestUri != request.RequestUri ||
            response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant()
                is not ("audio/flac" or "audio/x-flac"))
        {
            response.Dispose();
            throw new InvalidDataException("The Apple stream transport is incompatible.");
        }
        return response;
    }

    private static ProviderMediaFormat Media { get; } = new("audio/flac", "flac", "flac");

    private static string LeaseId(string trackId, string quality)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{trackId}\n{quality}"));
        return $"apple-stream-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }
}
