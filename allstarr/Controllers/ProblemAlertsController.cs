using allstarr.Core.Health;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace allstarr.Controllers;

public sealed record ProblemAlertNetworkRequest(string[] PermittedDestinations, long ExpectedRevision);
public sealed record ProblemAlertThresholdRequest(ProblemAlertThresholds Thresholds, long ExpectedRevision);

[ApiController]
[Route("api/admin/alerts")]
[ServiceFilter(typeof(AdminPortFilter), Order = -4000)]
[RequestSizeLimit(8192)]
[TypeFilter(typeof(ProblemAlertInputFilter), Order = -3000)]
public sealed class ProblemAlertsController(ProblemAlertService alerts) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Read(CancellationToken token) => ExecuteAsync(async session => Ok(await alerts.ReadAsync(session, token)));

    [HttpGet("incidents")]
    public Task<IActionResult> Incidents(CancellationToken token) => ExecuteAsync(async session => Ok(await alerts.ReadIncidentsAsync(session, token)));

    [HttpPost("channels")]
    public Task<IActionResult> Create(NotificationChannelRequest request, CancellationToken token) =>
        ExecuteAsync(async session => StatusCode(201, await alerts.SaveChannelAsync(session, null, request, token)));

    [HttpPut("channels/{id:guid}")]
    public Task<IActionResult> Update(Guid id, NotificationChannelRequest request, CancellationToken token) =>
        ExecuteAsync(async session => Ok(await alerts.SaveChannelAsync(session, id, request, token)));

    [HttpPut("thresholds")]
    public Task<IActionResult> Thresholds(ProblemAlertThresholdRequest request, CancellationToken token) => ExecuteAsync(async session =>
    {
        await alerts.SaveThresholdsAsync(session, request.Thresholds, request.ExpectedRevision, token);
        return Ok(await alerts.ReadAsync(session, token));
    });

    [HttpPut("destinations")]
    public Task<IActionResult> Destinations(ProblemAlertNetworkRequest request, CancellationToken token) => ExecuteAsync(async session =>
    {
        await alerts.SaveNetworkAsync(session, request.PermittedDestinations, request.ExpectedRevision, token);
        return Ok(await alerts.ReadAsync(session, token));
    });

    [HttpPost("channels/{id:guid}/test")]
    public Task<IActionResult> Test(Guid id, CancellationToken token) => ExecuteAsync(async session =>
        Accepted(new
        {
            deliveryId = await alerts.TestAsync(session, id, token),
            preview = new { message = "This is a test notification from Allstarr.", action = "Review notification delivery in alert settings." }
        }));

    private async Task<IActionResult> ExecuteAsync(Func<AdminAuthSession, Task<IActionResult>> action)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session)
            return Unauthorized(new { error = "Sign in again to continue." });
        try { return await action(session); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "The alert settings are unavailable." }); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (InvalidOperationException) { return Conflict(new { error = "Alert settings changed or the action is temporarily unavailable. Reload before retrying." }); }
    }
}

public sealed class ProblemAlertInputFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
            context.Result = new BadRequestObjectResult(new { error = "The alert settings are invalid." });
    }
    public void OnActionExecuted(ActionExecutedContext context) { }
}
