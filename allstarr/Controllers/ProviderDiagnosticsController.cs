using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/provider-diagnostics")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ProviderDiagnosticsController(
    IProviderRegistry providers,
    IProviderRouteAccountResolver accounts,
    IDbContextFactory<AllstarrDbContext> contextFactory,
    ProviderCtsTrackSelector trackSelector,
    DurableProviderHealthStore healthStore) : ControllerBase
{
    private const int SampleLimitBytes = 256 * 1024;
    private static readonly HttpClient SampleClient = CreateSampleClient();
    private static readonly BoundedOperationGate DeepStreamConcurrency = new(2);

    [HttpGet("deep-stream/latest")]
    public async Task<IActionResult> LatestDeepStream(CancellationToken cancellationToken)
    {
        if (!TryGetAdministrator(out var session, out var authError)) return authError!;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await (
            from sample in db.ProviderHealthSamples.AsNoTracking()
            join account in db.ProviderAccounts.AsNoTracking() on sample.ProviderAccountId equals account.Id
            where sample.Capability == "click-to-stream" &&
                  (account.TenantId == null || account.TenantId == session.TenantId) &&
                  (account.OwnerUserId == null || account.OwnerUserId == session.AllstarrUserId)
            orderby sample.ObservedAt descending
            select new
            {
                account.Id,
                account.ProviderId,
                sample.State,
                sample.LatencyMilliseconds,
                sample.FailureCode,
                sample.ObservedAt
            })
            .Take(500)
            .ToArrayAsync(cancellationToken);
        var measurements = rows.GroupBy(item => item.Id).Select(group =>
        {
            var latest = group.First();
            var succeeded = latest.State == allstarr.Core.Storage.ProviderHealthState.Healthy;
            var latency = latest.LatencyMilliseconds ?? 0;
            return new
            {
                providerAccountId = latest.Id,
                providerId = latest.ProviderId,
                health = latest.State.ToString().ToLowerInvariant(),
                latencyMs = latency,
                bars = ConnectivityQuality.Bars(latency, succeeded, ConnectivityMetric.ClickToStream),
                metric = "cts",
                testedAt = latest.ObservedAt,
                failureCode = latest.FailureCode
            };
        });
        return Ok(new { measurements });
    }

    [HttpPost("deep-stream")]
    public async Task<IActionResult> DeepStream(
        [FromBody] DeepStreamDiagnosticRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdministrator(out var session, out var authError)) return authError!;
        if (!session.TenantId.HasValue || !session.AllstarrUserId.HasValue)
            return Conflict(new { error = "The administrator session is not linked to an Allstarr user." });
        if (request.ProviderAccountId == Guid.Empty)
            return BadRequest(new { error = "A provider account is required." });

        string providerId;
        try { providerId = ProviderContractValidation.ProviderId(request.ProviderId, nameof(request.ProviderId)); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }

        if (!providers.TryGetCapability<IProviderStreamingCapability>(
                providerId, ProviderCapabilityKind.Streaming, out var streaming) || streaming == null)
            return BadRequest(new { error = "The selected provider does not expose streaming diagnostics." });

        using var probeLease = await DeepStreamConcurrency.TryEnterAsync(cancellationToken);
        if (probeLease == null)
        {
            Response.Headers.RetryAfter = "5";
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "Two click-to-stream diagnostics are already running. Retry after one completes.",
                retryAfterSeconds = 5
            });
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var identity = await db.BackendIdentities.AsNoTracking()
            .Where(item => item.TenantId == session.TenantId.Value &&
                           item.UserId == session.AllstarrUserId.Value)
            .OrderByDescending(item => item.LastSeenAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (identity == null)
            return Conflict(new { error = "No verified backend identity is available for this administrator." });

        var automaticTrack = string.IsNullOrWhiteSpace(request.TrackId)
            ? await trackSelector.SelectAsync(providerId, request.ProviderAccountId, cancellationToken)
            : null;
        if (string.IsNullOrWhiteSpace(request.TrackId) && automaticTrack == null)
            return Conflict(new
            {
                error = "No known provider tracks are available for automatic CTS rotation. Enter a provider track ID once or refresh playlist metadata first."
            });
        var trackId = string.IsNullOrWhiteSpace(request.TrackId) ? automaticTrack!.TrackId : request.TrackId.Trim();
        var trackLabel = string.IsNullOrWhiteSpace(request.TrackLabel)
            ? automaticTrack?.Label ?? "Selected diagnostic track"
            : request.TrackLabel.Trim();

        var actor = new ProviderActorContext(
            session.TenantId.Value,
            ProviderActorKind.Administrator,
            session.AllstarrUserId.Value,
            new ProviderBackendPrincipal(identity.BackendType, identity.BackendInstanceId, identity.PrincipalId));
        ProviderRouteAccountResolution? resolved;
        try
        {
            resolved = await accounts.ResolveAsync(new ProviderRouteAccountRequest(
                actor,
                providerId,
                ProviderCapabilityKind.Streaming,
                request.ProviderAccountId,
                null), cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = exception.Message });
        }

        if (resolved == null || resolved.Account.AccountId != request.ProviderAccountId)
            return NotFound(new { error = "The selected provider account is unavailable." });

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var correlationId = HttpContext.TraceIdentifier.Length <= 100
            ? HttpContext.TraceIdentifier
            : HttpContext.TraceIdentifier[..100];
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
            "admin-deep-stream-diagnostic",
            correlationId,
            DateTimeOffset.UtcNow.AddSeconds(30),
            deadline.Token);
        var track = new ProviderExternalResourceId(providerId, ProviderResourceKind.Track, trackId);
        var total = Stopwatch.StartNew();

        try
        {
            var leaseOutcome = await streaming.GetStreamLeaseAsync(
                execution,
                new ProviderStreamLeaseRequest(track, request.Quality, 0));
            var resolveMilliseconds = total.Elapsed.TotalMilliseconds;
            if (!leaseOutcome.IsSuccess)
                return UnprocessableEntity(new
                {
                    succeeded = false,
                    providerId,
                    providerAccountId = request.ProviderAccountId,
                    stage = "resolve",
                    error = leaseOutcome.Error!.Kind.ToString(),
                    resolveMilliseconds = Math.Round(resolveMilliseconds, 1),
                    bars = 0,
                    measuredAt = DateTimeOffset.UtcNow
                });

            var lease = leaseOutcome.RequireValue();
            if (lease.ExpiresAt <= DateTimeOffset.UtcNow)
                return UnprocessableEntity(new
                {
                    succeeded = false,
                    providerId,
                    stage = "lease-validation",
                    error = "expired-lease",
                    bars = 0,
                    measuredAt = DateTimeOffset.UtcNow
                });

            if (!OutboundRequestGuard.TryCreateSafeHttpUri(
                    lease.ProtectedSourceUri.AbsoluteUri, out var safeUri, out var unsafeReason))
                return UnprocessableEntity(new
                {
                    succeeded = false,
                    providerId,
                    stage = "lease-validation",
                    error = unsafeReason,
                    bars = 0,
                    measuredAt = DateTimeOffset.UtcNow
                });

            using var sampleRequest = new HttpRequestMessage(HttpMethod.Get, safeUri);
            sampleRequest.Headers.Range = new RangeHeaderValue(0, SampleLimitBytes - 1);
            sampleRequest.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true,
                MaxAge = TimeSpan.Zero
            };
            sampleRequest.Headers.Pragma.ParseAdd("no-cache");
            using var response = await SampleClient.SendAsync(
                sampleRequest, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var headersMilliseconds = total.Elapsed.TotalMilliseconds;
            if (IsRedirect(response.StatusCode) || !response.IsSuccessStatusCode)
                return UnprocessableEntity(new
                {
                    succeeded = false,
                    providerId,
                    stage = "first-byte",
                    error = IsRedirect(response.StatusCode) ? "redirect-rejected" : $"http-{(int)response.StatusCode}",
                    resolveMilliseconds = Math.Round(resolveMilliseconds, 1),
                    headersMilliseconds = Math.Round(headersMilliseconds, 1),
                    bars = 0,
                    measuredAt = DateTimeOffset.UtcNow
                });

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
                return UnprocessableEntity(new
                {
                    succeeded = false,
                    providerId,
                    stage = "first-byte",
                    error = "empty-media-response",
                    bars = 0,
                    measuredAt = DateTimeOffset.UtcNow
                });

            var transferSeconds = Math.Max((total.Elapsed - transferStart).TotalSeconds, 0.001);
            var throughputKbps = bytesRead * 8d / 1000d / transferSeconds;
            await healthStore.RecordAsync(
                providerId,
                request.ProviderAccountId.ToString("N"),
                "click-to-stream",
                allstarr.Core.Storage.ProviderHealthState.Healthy,
                (long)Math.Round(firstByteMilliseconds.Value),
                cancellationToken: cancellationToken);
            return Ok(new
            {
                succeeded = true,
                providerId,
                providerAccountId = request.ProviderAccountId,
                trackLabel,
                selectionMode = automaticTrack == null ? "manual" : "rotating-corpus",
                corpusSize = automaticTrack?.CorpusSize,
                requestedQuality = request.Quality.ToString().ToLowerInvariant(),
                resolveMilliseconds = Math.Round(resolveMilliseconds, 1),
                firstByteMilliseconds = Math.Round(firstByteMilliseconds.Value, 1),
                clickToStreamMilliseconds = Math.Round(firstByteMilliseconds.Value, 1),
                sampleBytes = bytesRead,
                throughputKbps = Math.Round(throughputKbps, 1),
                contentType = response.Content.Headers.ContentType?.MediaType,
                cacheState = response.Headers.TryGetValues("X-Cache", out var cacheValues)
                    ? string.Join(", ", cacheValues).Length <= 100 ? string.Join(", ", cacheValues) : "reported"
                    : "unknown",
                bars = ConnectivityQuality.Bars(firstByteMilliseconds.Value, true, ConnectivityMetric.ClickToStream),
                quality = ConnectivityQuality.Label(ConnectivityQuality.Bars(firstByteMilliseconds.Value, true, ConnectivityMetric.ClickToStream)),
                measuredAt = DateTimeOffset.UtcNow,
                limit = new { timeoutSeconds = 30, sampleBytes = SampleLimitBytes }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499, new { error = "The diagnostic was canceled." });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new
            {
                error = "The provider did not produce a media sample within 30 seconds.",
                bars = 0
            });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "The provider media endpoint could not be reached.",
                bars = 0
            });
        }
    }

    private bool TryGetAdministrator(out AdminAuthSession session, out IActionResult? error)
    {
        session = null!;
        error = null;
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession current)
        {
            error = Unauthorized(new { error = "Authentication required" });
            return false;
        }
        if (!current.IsAdministrator)
        {
            error = StatusCode(StatusCodes.Status403Forbidden, new { error = "Administrator access required" });
            return false;
        }
        session = current;
        return true;
    }

    private static HttpClient CreateSampleClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Allstarr-Provider-Diagnostic/1.0");
        return client;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

}

public sealed class DeepStreamDiagnosticRequest
{
    public string ProviderId { get; set; } = string.Empty;
    public Guid ProviderAccountId { get; set; }
    public string? TrackId { get; set; }
    public string? TrackLabel { get; set; }
    public ProviderAudioQuality Quality { get; set; } = ProviderAudioQuality.Any;
}
