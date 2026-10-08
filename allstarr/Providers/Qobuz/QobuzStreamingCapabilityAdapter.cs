using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Models.Settings;
using allstarr.Services.Qobuz;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Providers.Qobuz;

public sealed class QobuzStreamingCapabilityAdapter : IProviderStreamingCapability
{
    private readonly HttpClient http;
    private readonly IProviderAccountSecretAccessor secrets;
    private readonly QobuzDownloadService downloads;
    private readonly string? configuredQuality;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public QobuzStreamingCapabilityAdapter(
        IHttpClientFactory clients,
        IProviderAccountSecretAccessor secrets,
        QobuzDownloadService downloads,
        IOptions<QobuzSettings> settings)
        : this(clients.CreateClient(QobuzDownloadCapabilityAdapter.HttpClientName),
            secrets, downloads, settings.Value.Quality)
    { }

    public QobuzStreamingCapabilityAdapter(
        HttpClient http,
        IProviderAccountSecretAccessor secrets,
        QobuzDownloadService downloads,
        string? configuredQuality)
    {
        this.http = http;
        this.secrets = secrets;
        this.downloads = downloads;
        this.configuredQuality = configuredQuality;
    }

    public string ProviderId => QobuzDownloadCapabilityAdapter.StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Streaming;

    public async Task<ProviderOutcome<ProviderStreamLease>> GetStreamLeaseAsync(
        ProviderExecutionContext context,
        ProviderStreamLeaseRequest request)
    {
        var error = Validate(context, request.TrackId);
        if (error != null) return ProviderOutcome<ProviderStreamLease>.Failure(error);
        try
        {
            var resolved = await ResolveAsync(context, request);
            if (resolved == null)
                return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
            return ProviderOutcome<ProviderStreamLease>.Success(new(
                $"qobuz-stream-{Guid.CreateVersion7():N}",
                resolved.Value.Source,
                DateTimeOffset.UtcNow.AddMinutes(1),
                supportsByteRanges: true,
                supportsSeeking: true,
                resolved.Value.Media,
                ProviderStreamRetryBehavior.RefreshLease,
                (outbound, token) => OpenAsync(
                    outbound, resolved.Value.Media, token)));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch (KeyNotFoundException)
        {
            return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
        }
        catch (InvalidDataException)
        {
            return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.IncompatibleMedia));
        }
        catch (HttpRequestException exception)
        {
            return ProviderOutcome<ProviderStreamLease>.Failure(
                QobuzDownloadCapabilityAdapter.HttpError(exception));
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
            var resolved = await ResolveAsync(context, request);
            return resolved == null
                ? ProviderOutcome<ProviderStreamProbeResult>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration))
                : ProviderOutcome<ProviderStreamProbeResult>.Success(new(
                    true, DateTimeOffset.UtcNow, resolved.Value.Media));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderStreamProbeResult>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch (KeyNotFoundException)
        {
            return ProviderOutcome<ProviderStreamProbeResult>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
        }
        catch (InvalidDataException)
        {
            return ProviderOutcome<ProviderStreamProbeResult>.Failure(new(ProviderErrorKind.IncompatibleMedia));
        }
        catch (HttpRequestException exception)
        {
            return ProviderOutcome<ProviderStreamProbeResult>.Failure(
                QobuzDownloadCapabilityAdapter.HttpError(exception));
        }
        catch
        {
            return ProviderOutcome<ProviderStreamProbeResult>.Failure(new(ProviderErrorKind.TransientFailure));
        }
    }

    private async Task<(Uri Source, ProviderMediaFormat Media)?> ResolveAsync(
        ProviderExecutionContext context,
        ProviderStreamLeaseRequest request)
    {
        var credential = await secrets.UseAsync(context.Account!, bytes =>
            Task.FromResult(ParseCredential(bytes)), context.CancellationToken);
        if (credential == null) return null;
        var prepared = await downloads.ResolveDownloadAsync(
            request.TrackId.Value,
            credential.UserAuthToken,
            QobuzDownloadCapabilityAdapter.Quality(request.RequestedQuality, configuredQuality),
            context.CancellationToken);
        if (prepared.IsSample ||
            !QobuzDownloadCapabilityAdapter.TryMedia(prepared, out var media, out _) ||
            !QobuzDownloadCapabilityAdapter.TryProviderUri(prepared.Url, out var source))
            throw new InvalidDataException("The Qobuz stream response is incompatible.");
        return (source, media!);
    }

    private async Task<HttpResponseMessage> OpenAsync(
        HttpRequestMessage request,
        ProviderMediaFormat media,
        CancellationToken cancellationToken)
    {
        var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return response;
        if (response.RequestMessage?.RequestUri != request.RequestUri ||
            !QobuzDownloadCapabilityAdapter.ValidTransportType(
                response.Content.Headers.ContentType?.MediaType, media.MimeType))
        {
            response.Dispose();
            throw new InvalidDataException("The Qobuz stream transport is incompatible.");
        }
        return response;
    }

    private static Credential? ParseCredential(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var credential = JsonSerializer.Deserialize<Credential>(bytes.Span);
            return string.IsNullOrWhiteSpace(credential?.UserAuthToken) ||
                   string.IsNullOrWhiteSpace(credential.UserId)
                ? null
                : credential;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ProviderError? Validate(
        ProviderExecutionContext context,
        ProviderExternalResourceId trackId)
    {
        ArgumentNullException.ThrowIfNull(context);
        try { context.RequireResourceOwner(trackId, ProviderResourceKind.Track); }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException)
        { return new(ProviderErrorKind.Forbidden); }
        if (!context.ProviderId.Equals(QobuzDownloadCapabilityAdapter.StableProviderId, StringComparison.Ordinal) ||
            !context.Policy.AllowsProvider(QobuzDownloadCapabilityAdapter.StableProviderId))
            return new(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested)
            return new(ProviderErrorKind.Canceled);
        if (context.IsExpired(DateTimeOffset.UtcNow))
            return new(ProviderErrorKind.CapabilityUnavailable);
        return context.Account == null
            ? new(ProviderErrorKind.AccountNeedsConfiguration)
            : null;
    }

    private sealed record Credential(
        [property: JsonPropertyName("userAuthToken")] string? UserAuthToken,
        [property: JsonPropertyName("userId")] string? UserId);
}
