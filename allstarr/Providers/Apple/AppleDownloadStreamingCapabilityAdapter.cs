using allstarr.Core.Providers.AppleMusicKit;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Models.Settings;
using allstarr.Services.AppleMusic;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Providers.AppleDownload;

public sealed class AppleDownloadStreamingCapabilityAdapter : IProviderDownloadStreamingCapability
{
    private readonly HttpClient http;
    private readonly AppleMusicClient client;
    private readonly AppleDownloadSettings settings;
    private readonly IAppleDownloadEndpointDiscovery discovery;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AppleDownloadStreamingCapabilityAdapter(
        IHttpClientFactory clients,
        IOptions<AppleDownloadSettings> settings,
        IAppleDownloadEndpointDiscovery discovery, AppleMusicClient client)
        : this(clients.CreateClient(AppleDownloadCapabilityAdapter.HttpClientName), settings.Value, discovery, client) { }

    public AppleDownloadStreamingCapabilityAdapter(
        HttpClient http,
        AppleDownloadSettings settings,
        IAppleDownloadEndpointDiscovery discovery, AppleMusicClient client)
    {
        this.http = http;
        this.client = client;
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
                return ProviderOutcome<ProviderStreamLease>.Failure(snapshot.State == AppleDownloadEndpointState.NeedsAuthentication
                    ? ProviderError.AppleDownloadSignInRequired() : new(ErrorFor(snapshot.State)));
            if (!OutboundRequestGuard.TryCreateConfiguredServiceUri(settings.BaseUrl, out var baseUri, out _))
                return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
            var resolved = await client.ResolveCatalogTrackAsync(context, request.TrackId.Value);
            if (!resolved.IsSuccess) return ProviderOutcome<ProviderStreamLease>.Failure(resolved.Error!);
            var quality = AppleDownloadCapabilityAdapter.Quality(request.RequestedQuality, settings.Quality);
            var audioFile = HasAudioFile(snapshot);
            var route = audioFile ? "audio" : "stream";
            var source = new Uri(baseUri!,
                $"api/{route}/{Uri.EscapeDataString(resolved.RequireValue())}?quality={Uri.EscapeDataString(quality)}");
            return ProviderOutcome<ProviderStreamLease>.Success(new(
                LeaseId(request.TrackId.Value, quality),
                source,
                DateTimeOffset.UtcNow.AddMinutes(2),
                supportsByteRanges: audioFile,
                supportsSeeking: audioFile,
                Media,
                ProviderStreamRetryBehavior.RetrySameLeaseOnce,
                (outbound, token) => OpenAsync(context, source, audioFile, outbound, token)));
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
        (HasAudioFile(snapshot) ||
         snapshot.Capability("stream-audio-song").State == AppleDownloadCapabilityState.Available);

    private static bool HasAudioFile(AppleDownloadEndpointSnapshot snapshot) =>
        snapshot.Capability("audio-file").State == AppleDownloadCapabilityState.Available;

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
        var accountError = AppleMusicClient.Validate(context, personal: true);
        if (accountError != null) return accountError;
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
        ProviderExecutionContext context, Uri expectedSource, bool audioFile,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri != expectedSource) throw new UnauthorizedAccessException("The Apple stream destination changed.");
        var response = await client.SendSidecarAsync(context, http, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                if (audioFile && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
                    response.Content.Headers.ContentRange == null &&
                    response.Content.Headers.TryGetValues("Content-Range", out var values))
                {
                    var ranges = values.ToArray();
                    if (ranges.Length == 1 && ranges[0].StartsWith("*/", StringComparison.Ordinal) &&
                        long.TryParse(ranges[0].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                    {
                        response.Content.Headers.ContentRange = new(length);
                    }
                }
                if (audioFile && response.StatusCode == HttpStatusCode.BadGateway &&
                    await MissingAudioArtifactAsync(response, cancellationToken))
                {
                    throw new HttpRequestException("The Apple audio artifact is unavailable.", null, HttpStatusCode.NotFound);
                }
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (response.RequestMessage?.RequestUri != request.RequestUri ||
            !(mediaType is "audio/flac" or "audio/x-flac" || audioFile && mediaType == "audio/mp4"))
        {
            response.Dispose();
            throw new InvalidDataException("The Apple stream transport is incompatible.");
        }
        return response;
    }

    private static async Task<bool> MissingAudioArtifactAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int maximumPayloadBytes = 64 * 1024;
        if (response.Content.Headers.ContentLength > maximumPayloadBytes) return false;
        try
        {
            await response.Content.LoadIntoBufferAsync(maximumPayloadBytes, cancellationToken);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken),
                new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("detail", out var detail) &&
                detail.ValueKind == JsonValueKind.String && detail.GetString() == "audio_artifact_missing";
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException)
        {
            return false;
        }
    }

    private static ProviderMediaFormat Media { get; } = new("audio/flac", "flac", "flac");

    private static string LeaseId(string trackId, string quality)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{trackId}\n{quality}"));
        return $"apple-stream-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }
}
