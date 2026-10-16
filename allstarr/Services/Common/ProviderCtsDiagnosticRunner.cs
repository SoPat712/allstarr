using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Core.Extensions;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Common;

public sealed class ProviderCtsDiagnosticRunner(
    IProviderRegistry providers,
    IProviderRouteAccountResolver accounts,
    ProviderCtsTrackSelector trackSelector,
    IProviderOutcomeObserver health,
    IDbContextFactory<AllstarrDbContext>? contextFactory = null)
{
    private const int SampleLimitBytes = 64 * 1024;
    private static readonly BoundedOperationGate Concurrency = new(2);

    public async Task<ProviderCtsDiagnosticResult> MeasureAsync(
        ProviderActorContext actor,
        string providerId,
        Guid? providerAccountId,
        ProviderAudioQuality quality,
        string correlationId,
        string? trackId = null,
        CancellationToken cancellationToken = default,
        bool householdCheck = false)
    {
        var result = await MeasureCoreAsync(
            actor, providerId, providerAccountId, quality, correlationId, trackId, cancellationToken);
        if (contextFactory != null)
        {
            await using var db = await contextFactory.CreateDbContextAsync(CancellationToken.None);
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = householdCheck ? null : actor.EffectiveUserId,
                Category = "provider-cts",
                Action = householdCheck ? "scheduled.measure" : "cold-connect.measure",
                Outcome = householdCheck ? "recorded" : result.Succeeded ? "succeeded" : "failed",
                CorrelationId = correlationId,
                DetailsJson = JsonSerializer.Serialize(result),
                CreatedAt = result.MeasuredAt
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return result;
    }

    private async Task<ProviderCtsDiagnosticResult> MeasureCoreAsync(
        ProviderActorContext actor,
        string providerId,
        Guid? providerAccountId,
        ProviderAudioQuality quality,
        string correlationId,
        string? trackId = null,
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
        var capabilityDescriptor = providers.GetRequired(providerId).Capabilities.Single(item =>
            item.Capability == ProviderCapabilityKind.Streaming);
        if (providerAccountId == Guid.Empty)
            providerAccountId = null;
        if (capabilityDescriptor.AccountRequirement != ProviderAccountRequirement.None &&
            !providerAccountId.HasValue)
        {
            return ProviderCtsDiagnosticResult.Failure(
                StatusCodes.Status400BadRequest,
                providerId,
                null,
                "account-resolution",
                "The selected provider streaming capability requires an account.");
        }
        if (capabilityDescriptor.AccountRequirement == ProviderAccountRequirement.None &&
            providerAccountId.HasValue)
        {
            return ProviderCtsDiagnosticResult.Failure(
                StatusCodes.Status400BadRequest,
                providerId,
                providerAccountId,
                "account-resolution",
                "The selected provider streaming capability does not use an account.");
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

        var total = Stopwatch.StartNew();
        ProviderRouteAccountResolution? resolved = null;
        try
        {
            if (capabilityDescriptor.AccountRequirement != ProviderAccountRequirement.None)
            {
                resolved = await accounts.ResolveAsync(new ProviderRouteAccountRequest(
                    actor,
                    providerId,
                    ProviderCapabilityKind.Streaming,
                    providerAccountId,
                    null,
                    AllowManagedDiagnosticAccount: actor.Kind == ProviderActorKind.Administrator &&
                        providerAccountId.HasValue), cancellationToken);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return ProviderCtsDiagnosticResult.Failure(
                StatusCodes.Status403Forbidden,
                providerId,
                providerAccountId,
                "account-resolution",
                "The selected provider account is not authorized.");
        }

        if ((capabilityDescriptor.AccountRequirement == ProviderAccountRequirement.Required && resolved == null) ||
            (providerAccountId.HasValue && resolved?.Account.AccountId != providerAccountId))
        {
            return ProviderCtsDiagnosticResult.Failure(
                StatusCodes.Status404NotFound,
                providerId,
                providerAccountId,
                "account-resolution",
                "The selected provider account is unavailable.");
        }
        providerAccountId = resolved?.Account.AccountId;
        var routeMilliseconds = total.Elapsed.TotalMilliseconds;

        var executionActor = actor;
        if (actor.Kind == ProviderActorKind.Administrator &&
            resolved != null &&
            resolved.Account.Scope == ProviderAccountScope.Personal &&
            resolved.Account.OwnerUserId.HasValue &&
            resolved.Account.OwnerUserId.Value != actor.EffectiveUserId)
        {
            executionActor = new ProviderActorContext(
                actor.Kind,
                actor.UserId,
                actor.BackendPrincipal,
                actor.DurableJobId,
                resolved.Account.OwnerUserId.Value);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = streaming is ExtensionDownloadStreamingCapabilityAdapter
            ? TimeSpan.FromMinutes(5)
            : TimeSpan.FromSeconds(30);
        deadline.CancelAfter(timeout);
        var policy = new ProviderExecutionPolicy(
            new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
            ProviderExplicitContentPolicy.Allow,
            allowFallback: false,
            allowSharedAccount: true,
            allowManagedDownloads: true,
            [providerId]);
        var execution = new ProviderExecutionContext(
            executionActor,
            providerId,
            resolved?.Account,
            policy,
            "provider-click-to-stream-diagnostic",
            ProviderContractValidation.RequiredText(correlationId, nameof(correlationId), 100),
            DateTimeOffset.UtcNow.Add(timeout),
            deadline.Token);

        var selectionMode = "manual";
        int? corpusSize = null;
        if (string.IsNullOrWhiteSpace(trackId))
        {
            var automaticTrack = await trackSelector.SelectAsync(
                providerId, providerAccountId, cancellationToken);
            if (automaticTrack != null)
            {
                trackId = automaticTrack.TrackId;
                selectionMode = "rotating-corpus";
                corpusSize = automaticTrack.CorpusSize;
            }
            else
            {
                trackId = await SearchSampleTrackAsync(providerId, execution, cancellationToken);
                selectionMode = "search";
            }
            if (trackId == null)
            {
                return ProviderCtsDiagnosticResult.Failure(
                    StatusCodes.Status409Conflict,
                    providerId,
                    providerAccountId,
                    "track-selection",
                    $"{providers.GetRequired(providerId).DisplayName} has no song to test with. None of its songs appear in your playlists yet, and its search returned no results.");
            }
        }
        else
        {
            trackId = trackId.Trim();
        }

        var track = new ProviderExternalResourceId(providerId, ProviderResourceKind.Track, trackId);

        try
        {
            var preparationStartedAt = total.Elapsed;
            var leaseOutcome = await streaming.GetStreamLeaseAsync(
                execution,
                new ProviderStreamLeaseRequest(track, quality, 0));
            var preparationMilliseconds = (total.Elapsed - preparationStartedAt).TotalMilliseconds;
            var resolveMilliseconds = preparationMilliseconds;
            if (!leaseOutcome.IsSuccess)
            {
                var error = leaseOutcome.Error!.Kind.ToString();
                RecordFailure(execution, error, resolveMilliseconds);
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
                RecordFailure(execution, "expired-lease", resolveMilliseconds);
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
                RecordFailure(execution, unsafeReason, resolveMilliseconds);
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
            if (lease.SupportsByteRanges)
                sampleRequest.Headers.Range = new RangeHeaderValue(0, SampleLimitBytes - 1);
            sampleRequest.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true,
                MaxAge = TimeSpan.Zero
            };
            sampleRequest.Headers.Pragma.ParseAdd("no-cache");
            var upstreamHeadersStartedAt = total.Elapsed;
            using var response = lease.ProtectedResponseFactory != null
                ? await lease.ProtectedResponseFactory(sampleRequest, deadline.Token)
                : await sampleClient.SendAsync(
                    sampleRequest, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var headersMilliseconds = total.Elapsed.TotalMilliseconds;
            var upstreamHeadersMilliseconds = (total.Elapsed - upstreamHeadersStartedAt).TotalMilliseconds;
            if (IsRedirect(response.StatusCode) || !response.IsSuccessStatusCode)
            {
                var error = IsRedirect(response.StatusCode)
                    ? "redirect-rejected"
                    : $"http-{(int)response.StatusCode}";
                if (!IsRedirect(response.StatusCode) && response.Headers.TryGetValues("X-Allstarr-Failure-Code", out var codes) &&
                    codes.SequenceEqual(["account_mismatch"], StringComparer.Ordinal))
                    error = "account_mismatch";
                RecordFailure(execution, error, headersMilliseconds);
                return ProviderCtsDiagnosticResult.Failure(
                    StatusCodes.Status422UnprocessableEntity,
                    providerId,
                    providerAccountId,
                    "first-byte",
                    error,
                    resolveMilliseconds,
                    headersMilliseconds);
            }

            if (!StreamByteRanges.Matches(response, sampleRequest.Headers.Range))
            {
                RecordFailure(execution, "range-response-mismatch", headersMilliseconds);
                return ProviderCtsDiagnosticResult.Failure(
                    StatusCodes.Status422UnprocessableEntity, providerId, providerAccountId,
                    "response-range", "range-response-mismatch", resolveMilliseconds, headersMilliseconds);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[32 * 1024];
            var bytesRead = 0;
            double? firstByteMilliseconds = null;
            var transferStart = total.Elapsed;
            using var sampleHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (bytesRead < SampleLimitBytes)
            {
                var count = await stream.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, SampleLimitBytes - bytesRead)), deadline.Token);
                if (count == 0) break;
                bytesRead += count;
                sampleHash.AppendData(buffer, 0, count);
                firstByteMilliseconds ??= total.Elapsed.TotalMilliseconds;
            }

            if (bytesRead == 0 || !firstByteMilliseconds.HasValue)
            {
                RecordFailure(execution, "empty-media-response", total.Elapsed.TotalMilliseconds);
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
            var totalMilliseconds = total.Elapsed.TotalMilliseconds;
            health.Observe(execution, ProviderCapabilityKind.Streaming, null,
                TimeSpan.FromMilliseconds(firstByteMilliseconds.Value));
            return ProviderCtsDiagnosticResult.Success(
                providerId,
                providerAccountId,
                selectionMode,
                corpusSize,
                quality,
                routeMilliseconds,
                preparationMilliseconds,
                resolveMilliseconds,
                headersMilliseconds,
                upstreamHeadersMilliseconds,
                firstByteMilliseconds.Value,
                totalMilliseconds,
                bytesRead,
                throughputKbps,
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType,
                response.Content.Headers.ContentLength,
                response.StatusCode == HttpStatusCode.PartialContent ? response.Content.Headers.ContentRange?.ToString() : null,
                response.StatusCode == HttpStatusCode.PartialContent &&
                    response.Headers.AcceptRanges.Any(value => value.Equals("bytes", StringComparison.OrdinalIgnoreCase)),
                CacheState(response),
                Convert.ToHexString(sampleHash.GetHashAndReset()).ToLowerInvariant(),
                lease.SupportsByteRanges,
                lease.SupportsSeeking,
                lease.Media,
                lease.QualityDowngradeReason,
                DateTimeOffset.UtcNow,
                sampleRequest.Headers.Range?.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            RecordFailure(execution, "timeout", total.Elapsed.TotalMilliseconds);
            return ProviderCtsDiagnosticResult.Failure(
                StatusCodes.Status504GatewayTimeout,
                providerId,
                providerAccountId,
                "first-byte",
                "The provider did not produce a media sample within 30 seconds.");
        }
        catch (HttpRequestException)
        {
            RecordFailure(execution, "endpoint-unreachable", total.Elapsed.TotalMilliseconds);
            return ProviderCtsDiagnosticResult.Failure(
                StatusCodes.Status502BadGateway,
                providerId,
                providerAccountId,
                "first-byte",
                "The provider media endpoint could not be reached.");
        }
    }

    private void RecordFailure(ProviderExecutionContext context, string failureCode, double latencyMilliseconds)
    {
        var kind = Enum.TryParse<ProviderErrorKind>(failureCode, out var parsed) ? parsed : ProviderErrorKind.TransientFailure;
        health.Observe(context, ProviderCapabilityKind.Streaming,
            new ProviderError(kind, kind == ProviderErrorKind.RateLimited ? TimeSpan.FromMinutes(1) : null),
            TimeSpan.FromMilliseconds(latencyMilliseconds));
    }

    private static readonly string[] SampleSearchQueries = ["Daft Punk", "Taylor Swift", "The Beatles"];

    /// <summary>
    /// Falls back to the provider's own search when no known track is stored for it yet,
    /// so a freshly connected source can still be measured.
    /// </summary>
    private async Task<string?> SearchSampleTrackAsync(
        string providerId,
        ProviderExecutionContext execution,
        CancellationToken cancellationToken)
    {
        if (!providers.TryGetCapability<IProviderMetadataCapability>(
                providerId, ProviderCapabilityKind.Metadata, out var metadata) || metadata == null)
            return null;
        foreach (var query in SampleSearchQueries)
        {
            try
            {
                var outcome = await metadata.SearchTracksAsync(
                    execution,
                    new ProviderMetadataSearchRequest(query, new ProviderPageRequest(5)));
                var match = outcome.IsSuccess
                    ? outcome.RequireValue().Items.FirstOrDefault(item =>
                        item.Id.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (match != null) return match.Id.Value;
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                                              !cancellationToken.IsCancellationRequested)
            {
                // A failed search only means this fallback has nothing to offer.
            }
        }
        return null;
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
        if (value.Contains("hit", StringComparison.OrdinalIgnoreCase)) return "hit";
        if (value.Contains("miss", StringComparison.OrdinalIgnoreCase)) return "miss";
        if (value.Contains("bypass", StringComparison.OrdinalIgnoreCase)) return "bypass";
        return "reported";
    }
}

public sealed class ProviderCtsDiagnosticResult
{
    [JsonIgnore]
    public int StatusCode { get; init; }

    public bool Succeeded { get; init; }
    public string ProviderId { get; init; } = string.Empty;
    public Guid? ProviderAccountId { get; init; }
    public string? Stage { get; init; }
    public string? Error { get; init; }
    public string? SelectionMode { get; init; }
    public int? CorpusSize { get; init; }
    public string? RequestedQuality { get; init; }
    public double? RouteMilliseconds { get; init; }
    public double? PreparationMilliseconds { get; init; }
    public double? ResolveMilliseconds { get; init; }
    public double? HeadersMilliseconds { get; init; }
    public double? UpstreamHeadersMilliseconds { get; init; }
    public double? FirstByteMilliseconds { get; init; }
    public double? ClickToStreamMilliseconds { get; init; }
    public double? TotalMilliseconds { get; init; }
    public int SampleBytes { get; init; }
    public double? ThroughputKbps { get; init; }
    public int? UpstreamStatusCode { get; init; }
    public string? ContentType { get; init; }
    public long? ContentLength { get; init; }
    public string? ContentRange { get; init; }
    public string? RequestedRange { get; init; }
    public bool AcceptsByteRanges { get; init; }
    public string? CacheState { get; init; }
    public string? SampleSha256 { get; init; }
    public bool LeaseSupportsByteRanges { get; init; }
    public bool LeaseSupportsSeeking { get; init; }
    public string SeekRung => !Succeeded ? "unavailable" :
        LeaseSupportsByteRanges && UpstreamStatusCode == 206 &&
        ContentRangeHeaderValue.TryParse(ContentRange, out var range) && range.HasLength &&
        RangeHeaderValue.TryParse(RequestedRange, out var requested) &&
        StreamByteRanges.MatchesContentRange(range, ContentLength, requested)
            ? "byte-range" : RequestedRange == null && LeaseSupportsSeeking ? "provider-seek" : "sequential";
    public ProviderMediaFormat? Media { get; init; }
    public string? QualityDowngradeReason { get; init; }
    public string ProbeMode { get; init; } = "cold-connect";
    public int Bars { get; init; }
    public string? Quality { get; init; }
    public DateTimeOffset MeasuredAt { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public ProviderCtsDiagnosticLimit Limit { get; init; } = new();

    public static ProviderCtsDiagnosticResult Failure(
        int statusCode,
        string providerId,
        Guid? providerAccountId,
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
        Guid? providerAccountId,
        string selectionMode,
        int? corpusSize,
        ProviderAudioQuality quality,
        double routeMilliseconds,
        double preparationMilliseconds,
        double resolveMilliseconds,
        double headersMilliseconds,
        double upstreamHeadersMilliseconds,
        double firstByteMilliseconds,
        double totalMilliseconds,
        int sampleBytes,
        double throughputKbps,
        int upstreamStatusCode,
        string? contentType,
        long? contentLength,
        string? contentRange,
        bool acceptsByteRanges,
        string cacheState,
        string sampleSha256,
        bool leaseSupportsByteRanges,
        bool leaseSupportsSeeking,
        ProviderMediaFormat media,
        string? qualityDowngradeReason,
        DateTimeOffset measuredAt,
        string? requestedRange = null)
    {
        var bars = ConnectivityQuality.Bars(firstByteMilliseconds, true, ConnectivityMetric.ClickToStream);
        return new ProviderCtsDiagnosticResult
        {
            StatusCode = StatusCodes.Status200OK,
            Succeeded = true,
            ProviderId = providerId,
            ProviderAccountId = providerAccountId,
            SelectionMode = selectionMode,
            CorpusSize = corpusSize,
            RequestedQuality = quality.ToString().ToLowerInvariant(),
            RouteMilliseconds = Round(routeMilliseconds),
            PreparationMilliseconds = Round(preparationMilliseconds),
            ResolveMilliseconds = Round(resolveMilliseconds),
            HeadersMilliseconds = Round(headersMilliseconds),
            UpstreamHeadersMilliseconds = Round(upstreamHeadersMilliseconds),
            FirstByteMilliseconds = Round(firstByteMilliseconds),
            ClickToStreamMilliseconds = Round(firstByteMilliseconds),
            TotalMilliseconds = Round(totalMilliseconds),
            SampleBytes = sampleBytes,
            ThroughputKbps = Round(throughputKbps),
            UpstreamStatusCode = upstreamStatusCode,
            ContentType = contentType,
            ContentLength = contentLength,
            ContentRange = contentRange,
            RequestedRange = requestedRange,
            AcceptsByteRanges = acceptsByteRanges,
            CacheState = cacheState,
            SampleSha256 = sampleSha256,
            LeaseSupportsByteRanges = leaseSupportsByteRanges,
            LeaseSupportsSeeking = leaseSupportsSeeking,
            Media = media,
            QualityDowngradeReason = qualityDowngradeReason,
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
    public int SampleBytes { get; init; } = 64 * 1024;
}
