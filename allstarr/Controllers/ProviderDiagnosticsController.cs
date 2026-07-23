using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/provider-diagnostics")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ProviderDiagnosticsController(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    ProviderCtsDiagnosticRunner diagnosticRunner) : ControllerBase
{
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
        var auditRows = await db.AuditEvents.AsNoTracking()
            .Where(item => item.Category == "provider-cts" &&
                           item.TenantId == session.TenantId)
            .OrderByDescending(item => item.CreatedAt)
            .Take(500)
            .ToArrayAsync(cancellationToken);
        var durableMeasurements = auditRows
            .Select(item => ParseMeasurement(item.DetailsJson, item.CreatedAt))
            .Where(item => item != null)
            .Cast<CtsMeasurement>()
            .GroupBy(item => item.ProviderAccountId)
            .Select(group => group.First())
            .ToArray();
        if (durableMeasurements.Length > 0)
            return Ok(new { measurements = durableMeasurements });
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

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var identity = await db.BackendIdentities.AsNoTracking()
            .Where(item => item.TenantId == session.TenantId.Value &&
                           item.UserId == session.AllstarrUserId.Value)
            .OrderByDescending(item => item.LastSeenAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (identity == null)
            return Conflict(new { error = "No verified backend identity is available for this administrator." });

        var actor = new ProviderActorContext(
            session.TenantId.Value,
            ProviderActorKind.Administrator,
            session.AllstarrUserId.Value,
            new ProviderBackendPrincipal(identity.BackendType, identity.BackendInstanceId, identity.PrincipalId));
        var correlationId = HttpContext.TraceIdentifier.Length <= 100
            ? HttpContext.TraceIdentifier
            : HttpContext.TraceIdentifier[..100];

        try
        {
            var result = await diagnosticRunner.MeasureAsync(
                actor,
                providerId,
                request.ProviderAccountId,
                request.Quality,
                correlationId,
                request.TrackId,
                request.TrackLabel,
                cancellationToken);
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = session.TenantId,
                ActorUserId = session.AllstarrUserId,
                Category = "provider-cts",
                Action = "cold-connect.measure",
                Outcome = result.Succeeded ? "succeeded" : "failed",
                CorrelationId = correlationId,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    result.ProviderId,
                    result.ProviderAccountId,
                    result.ProbeMode,
                    result.TrackLabel,
                    result.SelectionMode,
                    result.CorpusSize,
                    result.RequestedQuality,
                    result.ResolveMilliseconds,
                    result.HeadersMilliseconds,
                    result.FirstByteMilliseconds,
                    result.ClickToStreamMilliseconds,
                    result.SampleBytes,
                    result.ThroughputKbps,
                    result.ContentType,
                    result.CacheState,
                    result.Bars,
                    result.Quality,
                    result.Stage,
                    result.Error
                }),
                CreatedAt = result.MeasuredAt
            });
            await db.SaveChangesAsync(cancellationToken);
            if (result.RetryAfterSeconds.HasValue)
                Response.Headers.RetryAfter = result.RetryAfterSeconds.Value.ToString();
            return StatusCode(result.StatusCode, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499, new { error = "The diagnostic was canceled." });
        }
    }

    private static CtsMeasurement? ParseMeasurement(string json, DateTimeOffset measuredAt)
    {
        try
        {
            var value = JsonSerializer.Deserialize<CtsAuditPayload>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return value == null || value.ProviderAccountId == Guid.Empty
                ? null
                : new CtsMeasurement(
                    value.ProviderAccountId,
                    value.ProviderId ?? "provider",
                    value.Error == null ? "healthy" : "degraded",
                    value.ClickToStreamMilliseconds ?? value.FirstByteMilliseconds ?? 0,
                    value.Bars,
                    "cts",
                    measuredAt,
                    value.Error,
                    value.ProbeMode,
                    value.TrackLabel,
                    value.SelectionMode,
                    value.CorpusSize,
                    value.RequestedQuality,
                    value.ResolveMilliseconds,
                    value.HeadersMilliseconds,
                    value.FirstByteMilliseconds,
                    value.SampleBytes,
                    value.ThroughputKbps,
                    value.ContentType,
                    value.CacheState,
                    value.Quality);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record CtsMeasurement(
        Guid ProviderAccountId,
        string ProviderId,
        string Health,
        double LatencyMs,
        int Bars,
        string Metric,
        DateTimeOffset TestedAt,
        string? FailureCode,
        string? ProbeMode,
        string? TrackLabel,
        string? SelectionMode,
        int? CorpusSize,
        string? RequestedQuality,
        double? ResolveMilliseconds,
        double? HeadersMilliseconds,
        double? FirstByteMilliseconds,
        int SampleBytes,
        double? ThroughputKbps,
        string? ContentType,
        string? CacheState,
        string? Quality);

    private sealed class CtsAuditPayload
    {
        public string? ProviderId { get; set; }
        public Guid ProviderAccountId { get; set; }
        public string? ProbeMode { get; set; }
        public string? TrackLabel { get; set; }
        public string? SelectionMode { get; set; }
        public int? CorpusSize { get; set; }
        public string? RequestedQuality { get; set; }
        public double? ResolveMilliseconds { get; set; }
        public double? HeadersMilliseconds { get; set; }
        public double? FirstByteMilliseconds { get; set; }
        public double? ClickToStreamMilliseconds { get; set; }
        public int SampleBytes { get; set; }
        public double? ThroughputKbps { get; set; }
        public string? ContentType { get; set; }
        public string? CacheState { get; set; }
        public int Bars { get; set; }
        public string? Quality { get; set; }
        public string? Error { get; set; }
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
}

public sealed class DeepStreamDiagnosticRequest
{
    public string ProviderId { get; set; } = string.Empty;
    public Guid ProviderAccountId { get; set; }
    public string? TrackId { get; set; }
    public string? TrackLabel { get; set; }
    public ProviderAudioQuality Quality { get; set; } = ProviderAudioQuality.Any;
}
