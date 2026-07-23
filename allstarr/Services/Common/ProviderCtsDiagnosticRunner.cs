using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Routing;
using allstarr.Core.Storage;

namespace allstarr.Services.Common;

public sealed class ProviderCtsDiagnosticRunner(
    IProviderRegistry providers,
    IProviderRouteAccountResolver accounts,
    ProviderCtsTrackSelector trackSelector,
    IDurableProviderHealthObservationStore healthStore)
{
    private const int SampleLimitBytes = 256 * 1024;
    private static readonly BoundedOperationGate Concurrency = new(2);

    public async Task<ProviderCtsDiagnosticResult> MeasureAsync(
        ProviderActorContext actor,
        string providerId,
        Guid providerAccountId,
        ProviderAudioQuality quality,
        string correlationId,
        string? trackId = null,
        string? trackLabel = null,
        CancellationToken cancellationToken = default)
    {
        providerId = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        if (!providers.TryGetCapability<IProviderStreamingCapability>(
                providerId, ProviderCapabilityKind.Streaming, out var streaming) || streaming == null)
        {
            return ProviderCtsDiagnosticResult.Failure(
                StatusCodes.Status400BadRequest,
                providerId,
                providerAccountId,
                "capability",
                "The selected provider does not expose streaming diagnostics.");
        }

        using var probeLease = await Concurrency.TryEnterAsync(cancellationToken);
        if (probeLease == null)
        {
            return ProviderCtsDiagnosticResult.Failure(
                StatusCodes.Status429TooManyRequests,
                providerId,
                providerAccountId,
                "concurrency",
                "Two click-to-stream diagnostics are already running. Retry after one completes.",
                retryAfterSeconds: 5);
        }

        var automaticTrack = string.IsNullOrWhiteSpace(trackId)
            ? await trackSelector.SelectAsync(providerId, providerAccountId, cancellationToken)
            : null;
        if (string.IsNullOrWhiteSpace(trackId) && automaticTrack == null)
        {
            return ProviderCtsDiagnosticResult.Failure(
                StatusCodes.Status409Conflict,
                providerId,
                providerAccountId,
                "track-selection",
                "No known provider tracks are available for automatic CTS rotation. Refresh playlist metadata first.");
        }

        trackId = string.IsNullOrWhiteSpace(trackId) ? automaticTrack!.TrackId : trackId.Trim();
        trackLabel = string.IsNullOrWhiteSpace(trackLabel)
            ? automaticTrack?.Label ?? "Selected diagnostic track"
            : trackLabel.Trim();

        ProviderRouteAccountResolution? resolved;
        try
        {
            resolved = await accounts.ResolveAsync(new ProviderRouteAccountRequest(
                actor,
                providerId,
                ProviderCapabilityKind.Streaming,
                providerAccountId,
                null), cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ProviderCtsDiagnosticResult.Failure(
                StatusCodes.Status403Forbidden,
                providerId,
                providerAccountId,
                "account-resolution",
                exception.Message);
        }

        if (resolved == null || resolved.Account.AccountId != providerAccountId)
        {
            return ProviderCtsDiagnosticResult.Failure(
                StatusCodes.Status404NotFound,
                providerId,
                providerAccountId,
                "account-resolution",
                "The selected provider account is unavailable.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var policy = new ProviderExecutionPolicy(
            new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
            ProviderExplicitContentPolicy.Allow,
            allowFallback: false,
            allowSharedAccount: true,
            allowManagedDownloads: false,
            [providerId]);
        var execution = new ProviderExecutionContext(
            actor,
            providerId,
            resolved.Account,
            null,
            policy,
            "provider-click-to-stream-diagnostic",
            ProviderContractValidation.RequiredText(correlationId, nameof(correlationId), 100),
            DateTimeOffset.UtcNow.AddSeconds(30),
            deadline.Token);
        var track = new ProviderExternalResourceId(providerId, ProviderResourceKind.Track, trackId);
        var total = Stopwatch.StartNew();

        try
        {
            var leaseOutcome = await streaming.GetStreamLeaseAsync(
                execution,
                new ProviderStreamLeaseRequest(track, quality, 0));
            var resolveMilliseconds = total.Elapsed.TotalMilliseconds;
            if (!leaseOutcome.IsSuccess)
            {
                var error = leaseOutcome.Error!.Kind.ToString();
                await RecordFailureAsync(providerId, providerAccountId, error, resolveMilliseconds, cancellationToken);
                return ProviderCtsDiagnosticResult.Failure(
                    StatusCodes.Status422UnprocessableEntity,
                    providerId,
                    providerAccountId,
                    "resolve",
                    error,
                    resolveMilliseconds);
            }

            var lease = leaseOutcome.RequireValue();
            if (lease.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                await RecordFailureAsync(providerId, providerAccountId, "expired-lease", resolveMilliseconds, cancellationToken);
                return ProviderCtsDiagnosticResult.Failure(
                    StatusCodes.Status422UnprocessableEntity,
                    providerId,
                    providerAccountId,
                    "lease-validation",
                    "expired-lease",
                    resolveMilliseconds);
            }

            if (!OutboundRequestGuard.TryCreateSafeHttpUri(
                    lease.ProtectedSourceUri.AbsoluteUri, out var safeUri, out var unsafeReason))
            {
                await RecordFailureAsync(providerId, providerAccountId, unsafeReason, resolveMilliseconds, cancellationToken);
                return ProviderCtsDiagnosticResult.Failure(
                    StatusCodes.Status422UnprocessableEntity,
                    providerId,
                    providerAccountId,
                    "lease-validation",
                    unsafeReason,
                    resolveMilliseconds);
            }

            using var sampleClient = CreateSampleClient();
            using var sampleRequest = new HttpRequestMessage(HttpMethod.Get, safeUri);
            sampleRequest.Headers.Range = new RangeHeaderValue(0, SampleLimitBytes - 1);
            sampleRequest.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true,
                MaxAge = TimeSpan.Zero
            };
            sampleRequest.Headers.Pragma.ParseAdd("no-cache");
            using var response = await sampleClient.SendAsync(
                sampleRequest, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var headersMilliseconds = total.Elapsed.TotalMilliseconds;
            if (IsRedirect(response.StatusCode) || !response.IsSuccessStatusCode)
            {
                var error = IsRedirect(response.StatusCode)
                    ? "redirect-rejected"
                    : $"http-{(int)response.StatusCode}";
                await RecordFailureAsync(providerId, providerAccountId, error, headersMilliseconds, cancellationToken);
                return ProviderCtsDiagnosticResult.Failure(
                    StatusCodes.Status422UnprocessableEntity,
                    providerId,
                    providerAccountId,
                    "first-byte",
                    error,
                    resolveMilliseconds,
                    headersMilliseconds);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[32 * 1024];
            var bytesRead = 0;
            double? firstByteMilliseconds = null;
            var transferStart = total.Elapsed;
            while (bytesRead < SampleLimitBytes)
            {
                var count = await stream.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, SampleLimitBytes - bytesRead)), deadline.Token);
                if (count == 0) break;
                bytesRead += count;
                firstByteMilliseconds ??= total.Elapsed.TotalMilliseconds;
            }

            if (bytesRead == 0 || !firstByteMilliseconds.HasValue)
            {
                await RecordFailureAsync(providerId, providerAccountId, "empty-media-response", total.Elapsed.TotalMilliseconds, cancellationToken);
                return ProviderCtsDiagnosticResult.Failure(
                    StatusCodes.Status422UnprocessableEntity,
                    providerId,
                    providerAccountId,
                    "first-byte",
                    "empty-media-response",
                    resolveMilliseconds,
                    headersMilliseconds);
            }

            var transferSeconds = Math.Max((total.Elapsed - transferStart).TotalSeconds, 0.001);
            var throughputKbps = bytesRead * 8d / 1000d / transferSeconds;
            await healthStore.RecordAsync(
                providerId,
                providerAccountId.ToString("N"),
                "click-to-stream",
                allstarr.Core.Storage.ProviderHealthState.Healthy,
                (long)Math.Round(firstByteMilliseconds.Value),
                cancellationToken: cancellationToken);
            return ProviderCtsDiagnosticResult.Success(
                providerId,
                providerAccountId,
                trackLabel,
                automaticTrack == null ? "manual" : "rotating-corpus",
                automaticTrack?.CorpusSize,
                quality,
                resolveMilliseconds,
                headersMilliseconds,
                firstByteMilliseconds.Value,
                bytesRead,
                throughputKbps,
                response.Content.Headers.ContentType?.MediaType,
                CacheState(response),
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await RecordFailureAsync(providerId, providerAccountId, "timeout", total.Elapsed.TotalMilliseconds, cancellationToken);
            return ProviderCtsDiagnosticResult.Failure(
                StatusCodes.Status504GatewayTimeout,
                providerId,
                providerAccountId,
                "first-byte",
                "The provider did not produce a media sample within 30 seconds.");
        }
        catch (HttpRequestException)
        {
            await RecordFailureAsync(providerId, providerAccountId, "endpoint-unreachable", total.Elapsed.TotalMilliseconds, cancellationToken);
            return ProviderCtsDiagnosticResult.Failure(
                StatusCodes.Status502BadGateway,
                providerId,
                providerAccountId,
                "first-byte",
                "The provider media endpoint could not be reached.");
        }
    }

    private async Task RecordFailureAsync(
        string providerId,
        Guid providerAccountId,
        string failureCode,
        double latencyMilliseconds,
        CancellationToken cancellationToken)
    {
        await healthStore.RecordAsync(
            providerId,
            providerAccountId.ToString("N"),
            "click-to-stream",
            allstarr.Core.Storage.ProviderHealthState.Degraded,
            (long)Math.Round(latencyMilliseconds),
            failureCode,
            cancellationToken);
    }

    private static HttpClient CreateSampleClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero,
            MaxConnectionsPerServer = 1,
            UseCookies = false
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Allstarr-Provider-Diagnostic/1.0");
        return client;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static string CacheState(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-Cache", out var values)) return "unknown";
        var value = string.Join(", ", values);
        return value.Length <= 100 ? value : "reported";
    }
}

public sealed class ProviderCtsDiagnosticResult
{
    [JsonIgnore]
    public int StatusCode { get; init; }

    public bool Succeeded { get; init; }
    public string ProviderId { get; init; } = string.Empty;
    public Guid ProviderAccountId { get; init; }
    public string? Stage { get; init; }
    public string? Error { get; init; }
    public string? TrackLabel { get; init; }
    public string? SelectionMode { get; init; }
    public int? CorpusSize { get; init; }
    public string? RequestedQuality { get; init; }
    public double? ResolveMilliseconds { get; init; }
    public double? HeadersMilliseconds { get; init; }
    public double? FirstByteMilliseconds { get; init; }
    public double? ClickToStreamMilliseconds { get; init; }
    public int SampleBytes { get; init; }
    public double? ThroughputKbps { get; init; }
    public string? ContentType { get; init; }
    public string? CacheState { get; init; }
    public string ProbeMode { get; init; } = "cold-connect";
    public int Bars { get; init; }
    public string? Quality { get; init; }
    public DateTimeOffset MeasuredAt { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public ProviderCtsDiagnosticLimit Limit { get; init; } = new();

    public static ProviderCtsDiagnosticResult Failure(
        int statusCode,
        string providerId,
        Guid providerAccountId,
        string stage,
        string error,
        double? resolveMilliseconds = null,
        double? headersMilliseconds = null,
        int? retryAfterSeconds = null) => new()
        {
            StatusCode = statusCode,
            ProviderId = providerId,
            ProviderAccountId = providerAccountId,
            Stage = stage,
            Error = error,
            ResolveMilliseconds = Round(resolveMilliseconds),
            HeadersMilliseconds = Round(headersMilliseconds),
            MeasuredAt = DateTimeOffset.UtcNow,
            RetryAfterSeconds = retryAfterSeconds
        };

    public static ProviderCtsDiagnosticResult Success(
        string providerId,
        Guid providerAccountId,
        string trackLabel,
        string selectionMode,
        int? corpusSize,
        ProviderAudioQuality quality,
        double resolveMilliseconds,
        double headersMilliseconds,
        double firstByteMilliseconds,
        int sampleBytes,
        double throughputKbps,
        string? contentType,
        string cacheState,
        DateTimeOffset measuredAt)
    {
        var bars = ConnectivityQuality.Bars(firstByteMilliseconds, true, ConnectivityMetric.ClickToStream);
        return new ProviderCtsDiagnosticResult
        {
            StatusCode = StatusCodes.Status200OK,
            Succeeded = true,
            ProviderId = providerId,
            ProviderAccountId = providerAccountId,
            TrackLabel = trackLabel,
            SelectionMode = selectionMode,
            CorpusSize = corpusSize,
            RequestedQuality = quality.ToString().ToLowerInvariant(),
            ResolveMilliseconds = Round(resolveMilliseconds),
            HeadersMilliseconds = Round(headersMilliseconds),
            FirstByteMilliseconds = Round(firstByteMilliseconds),
            ClickToStreamMilliseconds = Round(firstByteMilliseconds),
            SampleBytes = sampleBytes,
            ThroughputKbps = Round(throughputKbps),
            ContentType = contentType,
            CacheState = cacheState,
            ProbeMode = "cold-connect",
            Bars = bars,
            Quality = ConnectivityQuality.Label(bars),
            MeasuredAt = measuredAt
        };
    }

    private static double? Round(double? value) => value.HasValue ? Math.Round(value.Value, 1) : null;
}

public sealed class ProviderCtsDiagnosticLimit
{
    public int TimeoutSeconds { get; init; } = 30;
    public int SampleBytes { get; init; } = 256 * 1024;
}
