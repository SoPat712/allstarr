using System.Net;
using System.Net.Http.Headers;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.ArtistGrid;

public sealed class ArtistGridStreamingCapabilityAdapter : IProviderStreamingCapability
{
    private const int MaximumRedirectHops = 4;
    private readonly ArtistGridCatalog _catalog;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    public ArtistGridStreamingCapabilityAdapter(
        ArtistGridCatalog catalog,
        HttpClient http,
        TimeProvider? clock = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _clock = clock ?? TimeProvider.System;
    }

    public string ProviderId => ArtistGridConstants.ProviderId;

    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Streaming;

    public async Task<ProviderOutcome<ProviderStreamLease>> GetStreamLeaseAsync(
        ProviderExecutionContext context,
        ProviderStreamLeaseRequest request)
    {
        var error = ValidateContext(context, request.TrackId);
        if (error != null) return Failure<ProviderStreamLease>(error);
        if (!ArtistGridTrackId.TryParse(request.TrackId.Value, out var tracker, out var gid, out var sourceHash))
            return Failure<ProviderStreamLease>(ProviderErrorKind.NotFound);

        var resolved = await _catalog.ResolveTrackAsync(tracker, gid, sourceHash, context.CancellationToken)
            .ConfigureAwait(false);
        if (!resolved.IsSuccess) return ProviderOutcome<ProviderStreamLease>.Failure(resolved.Error!);
        var value = resolved.RequireValue();
        var media = MediaFor(value.Source, value.Track.Quality);
        var source = value.Source;
        return ProviderOutcome<ProviderStreamLease>.Success(new(
            $"artistgrid-stream-{source.Hash}",
            source.Endpoint,
            _clock.GetUtcNow().AddMinutes(5),
            supportsByteRanges: true,
            supportsSeeking: true,
            media,
            ProviderStreamRetryBehavior.RetrySameLeaseOnce,
            (outbound, cancellationToken) => OpenAsync(source, outbound, cancellationToken)));
    }

    public async Task<ProviderOutcome<ProviderStreamProbeResult>> ProbeStreamAsync(
        ProviderExecutionContext context,
        ProviderStreamLeaseRequest request)
    {
        var error = ValidateContext(context, request.TrackId);
        if (error != null) return Failure<ProviderStreamProbeResult>(error);
        if (!ArtistGridTrackId.TryParse(request.TrackId.Value, out var tracker, out var gid, out var sourceHash))
            return Failure<ProviderStreamProbeResult>(ProviderErrorKind.NotFound);

        var resolved = await _catalog.ResolveTrackAsync(tracker, gid, sourceHash, context.CancellationToken)
            .ConfigureAwait(false);
        if (!resolved.IsSuccess) return ProviderOutcome<ProviderStreamProbeResult>.Failure(resolved.Error!);
        var value = resolved.RequireValue();
        try
        {
            using var requestMessage = new HttpRequestMessage(HttpMethod.Head, value.Source.Endpoint);
            using var response = await OpenAsync(value.Source, requestMessage, context.CancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var statusError = ErrorFor(response.StatusCode);
                return statusError == null
                    ? ProviderOutcome<ProviderStreamProbeResult>.Success(new(false, _clock.GetUtcNow()))
                    : ProviderOutcome<ProviderStreamProbeResult>.Failure(statusError);
            }
            return ProviderOutcome<ProviderStreamProbeResult>.Success(new(
                true,
                _clock.GetUtcNow(),
                MediaFor(value.Source, value.Track.Quality)));
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return Failure<ProviderStreamProbeResult>(ProviderErrorKind.Canceled);
        }
        catch (HttpRequestException exception)
        {
            return ProviderOutcome<ProviderStreamProbeResult>.Failure(
                ErrorFor(exception.StatusCode) ?? new ProviderError(ProviderErrorKind.TransientFailure));
        }
        catch
        {
            return Failure<ProviderStreamProbeResult>(ProviderErrorKind.TransientFailure);
        }
    }

    private async Task<HttpResponseMessage> OpenAsync(
        ArtistGridSource source,
        HttpRequestMessage original,
        CancellationToken cancellationToken)
    {
        if (original.RequestUri == null ||
            !UriEquals(original.RequestUri, source.Endpoint) ||
            original.Method != HttpMethod.Get && original.Method != HttpMethod.Head)
            throw new HttpRequestException("The provider stream request is invalid.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var method = original.Method;
        var range = original.Headers.Range;
        var current = source.Endpoint;
        for (var redirect = 0; ; redirect++)
        {
            if (!OutboundRequestGuard.TryCreateSafeHttpUri(current.AbsoluteUri, out var safeUri, out _) ||
                safeUri!.Scheme != Uri.UriSchemeHttps || safeUri.Port != 443 ||
                !source.AllowedRedirectHosts.Contains(safeUri.Host))
                throw new HttpRequestException("The provider stream target is unavailable.");

            using var request = new HttpRequestMessage(method, safeUri);
            if (method == HttpMethod.Get && range != null) request.Headers.Range = range;
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                if (redirect >= MaximumRedirectHops || response.Headers.Location == null)
                {
                    response.Dispose();
                    throw new HttpRequestException("The provider stream redirect is unavailable.");
                }

                var redirected = new Uri(current, response.Headers.Location);
                response.Dispose();
                if (!OutboundRequestGuard.TryCreateSafeHttpUri(redirected.AbsoluteUri, out var safeRedirect,
                        out _) || safeRedirect!.Scheme != Uri.UriSchemeHttps || safeRedirect.Port != 443 ||
                    !source.AllowedRedirectHosts.Contains(safeRedirect.Host))
                    throw new HttpRequestException("The provider stream redirect is unavailable.");
                current = safeRedirect;
                continue;
            }

            if (method == HttpMethod.Head && response.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                response.Dispose();
                method = HttpMethod.Get;
                range = null;
                continue;
            }

            if (response.IsSuccessStatusCode && source.Kind is "froste" or "direct")
            {
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType == null ||
                    !mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) &&
                    !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                    response.Content.Headers.ContentLength > ArtistGridAudioContent.MaximumBytes ||
                    response.Content.Headers.ContentRange?.Length > ArtistGridAudioContent.MaximumBytes)
                {
                    response.Dispose();
                    throw new HttpRequestException("The provider did not return a supported audio file.");
                }
                if (method == HttpMethod.Get) response.Content = new ArtistGridAudioContent(response.Content);
            }
            return response;
        }
    }

    private static bool UriEquals(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port &&
        left.AbsolutePath.Equals(right.AbsolutePath, StringComparison.Ordinal) &&
        left.Query.Equals(right.Query, StringComparison.Ordinal);

    private static ProviderMediaFormat MediaFor(ArtistGridSource source, string? quality)
    {
        if (source.Kind == "direct")
        {
            var direct = Path.GetExtension(source.Endpoint.AbsolutePath).ToLowerInvariant() switch
            {
                ".flac" => new ProviderMediaFormat("audio/flac", "flac", "flac"),
                ".ogg" => new ProviderMediaFormat("audio/ogg", "ogg", "unknown"),
                ".wav" => new ProviderMediaFormat("audio/wav", "wav", "unknown"),
                ".aac" => new ProviderMediaFormat("audio/aac", "aac", "aac"),
                ".m4a" or ".mp4" => new ProviderMediaFormat("audio/mp4", "mp4", "aac"),
                ".mp3" => new ProviderMediaFormat("audio/mpeg", "mp3", "mp3"),
                _ => null
            };
            if (direct != null) return direct;
        }
        var normalized = quality?.ToLowerInvariant() ?? string.Empty;
        if (normalized.Contains("flac", StringComparison.Ordinal) ||
            normalized.Contains("lossless", StringComparison.Ordinal))
            return new ProviderMediaFormat("audio/flac", "flac", "flac");
        if (normalized.Contains("m4a", StringComparison.Ordinal) ||
            normalized.Contains("aac", StringComparison.Ordinal) ||
            normalized.Contains("mp4", StringComparison.Ordinal))
            return new ProviderMediaFormat("audio/mp4", "mp4", "aac");
        return new ProviderMediaFormat("audio/mpeg", "mp3", "mp3");
    }

    private static ProviderError? ValidateContext(
        ProviderExecutionContext context,
        ProviderExternalResourceId trackId)
    {
        ArgumentNullException.ThrowIfNull(context);
        try { context.RequireResourceOwner(trackId, ProviderResourceKind.Track); }
        catch (ArgumentException) { return new ProviderError(ProviderErrorKind.Forbidden); }
        if (!context.ProviderId.Equals(ArtistGridConstants.ProviderId, StringComparison.Ordinal) ||
            !context.Policy.AllowsProvider(ArtistGridConstants.ProviderId) || context.Account != null)
            return new ProviderError(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested)
            return new ProviderError(ProviderErrorKind.Canceled);
        if (context.IsExpired(DateTimeOffset.UtcNow))
            return new ProviderError(ProviderErrorKind.CapabilityUnavailable);
        return null;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static ProviderError? ErrorFor(HttpStatusCode? status) => status switch
    {
        HttpStatusCode.NotFound or HttpStatusCode.Gone => new ProviderError(ProviderErrorKind.NotFound),
        HttpStatusCode.Unauthorized => new ProviderError(ProviderErrorKind.Unauthorized),
        HttpStatusCode.Forbidden => new ProviderError(ProviderErrorKind.Forbidden),
        HttpStatusCode.TooManyRequests => new ProviderError(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(30)),
        HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError => new ProviderError(ProviderErrorKind.TransientFailure),
        _ => null
    };

    private static ProviderOutcome<T> Failure<T>(ProviderErrorKind kind) =>
        ProviderOutcome<T>.Failure(new ProviderError(kind));

    private static ProviderOutcome<T> Failure<T>(ProviderError error) =>
        ProviderOutcome<T>.Failure(error);
}
