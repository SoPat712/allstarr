using allstarr.Core.Capabilities;
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
            if (result.RetryAfterSeconds.HasValue)
                Response.Headers.RetryAfter = result.RetryAfterSeconds.Value.ToString();
            return StatusCode(result.StatusCode, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499, new { error = "The diagnostic was canceled." });
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
}

public sealed class DeepStreamDiagnosticRequest
{
    public string ProviderId { get; set; } = string.Empty;
    public Guid ProviderAccountId { get; set; }
    public string? TrackId { get; set; }
    public string? TrackLabel { get; set; }
    public ProviderAudioQuality Quality { get; set; } = ProviderAudioQuality.Any;
}
