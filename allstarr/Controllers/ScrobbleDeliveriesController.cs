using allstarr.Core.Playback;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/scrobbling/deliveries")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ScrobbleDeliveriesController(IDbContextFactory<AllstarrDbContext> factory,
    PlaybackScrobbleRepair repair) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? before = null, CancellationToken token = default)
    {
        if (Session() is not { AllstarrUserId: { } user }) return Forbid();
        await using var db = await factory.CreateDbContextAsync(token);
        var query = db.PlaybackDeliveryCheckpoints.AsNoTracking().Where(x => x.OwnerUserId == user &&
            x.Kind == PlaybackScrobbleDeliveryKind.Completed);
        if (before is { } cursor) query = query.Where(x => x.Id.CompareTo(cursor) < 0);
        var rows = await query.OrderByDescending(x => x.Id).Take(100).ToArrayAsync(token);
        var items = rows.Select(x => new
        {
            x.Id,
            x.Revision,
            x.TargetId,
            state = x.State.ToString().ToLowerInvariant(),
            x.ProviderCode,
            x.SafeMessage,
            x.RequiresReauthentication,
            x.DestinationAccountId,
            x.DestinationAccountRevision,
            x.SendCount,
            x.RetryAfter,
            x.AttemptStartedAt,
            x.UpdatedAt,
            submission = ReadSubmission(x.SubmissionJson)
        }).ToArray();
        return Ok(new { items });
    }

    public sealed record RepairRequest(long Revision, string Action, long? AccountRevision = null);

    [HttpPost("{deliveryId:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid deliveryId, [FromBody] RepairRequest request, CancellationToken token)
    {
        if (Session() is not { AllstarrUserId: { } user }) return Forbid();
        try
        {
            return await repair.ResolveAsync(user, deliveryId, request.Revision, request.Action, request.AccountRevision, token)
                ? Ok(new { resolved = true }) : NotFound();
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    private static object? ReadSubmission(string? json)
    {
        if (json == null) return null;
        try
        {
            var submission = System.Text.Json.JsonSerializer.Deserialize<PlaybackScrobbleSubmission>(json);
            return submission == null ? null : new { submission.Track.Title, submission.Track.Artist, submission.OccurredAt };
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private AdminAuthSession? Session() => HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey,
        out var session) ? session as AdminAuthSession : null;
}
