using allstarr.Core.Operations;
using allstarr.Core.Settings;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Filters;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/problem-reports")]
[ServiceFilter(typeof(AdminPortFilter), Order = -4000)]
[RequestSizeLimit(16384)]
[TypeFilter(typeof(ProblemReportInputFilter), Order = -3000)]
public sealed class ProblemReportsController(ProblemReportService reports, IDurableRuntimeSettings settings) : ControllerBase
{
    [HttpGet("/api/admin/ui/listener-context")]
    public Task<IActionResult> ListenerContext(CancellationToken token) => ExecuteAsync(async session =>
    {
        await reports.RequireSessionAsync(session, token);
        var setting = await settings.GetAsync("Gateway:PublicListenerUrl", token);
        var url = string.IsNullOrEmpty(setting.NormalizedValue) ? null : setting.NormalizedValue;
        return Ok(new ListenerContext(session.BackendType.Equals("Subsonic", StringComparison.OrdinalIgnoreCase) ? "Subsonic" : "Jellyfin",
            session.UserName, url, url == null ? "unconfigured" : "configured"));
    });

    [HttpPost("context")]
    public Task<IActionResult> Preview([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ProblemReportSelector? selector, CancellationToken token) =>
        ExecuteAsync(async session => Ok(await reports.ContextAsync(session, selector, token)));

    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreateProblemReportRequest request, CancellationToken token) => ExecuteAsync(async session =>
    {
        var receipt = await reports.CreateAsync(session, request, token);
        return StatusCode(receipt.Replayed ? 200 : 201, receipt);
    });

    [HttpGet]
    public Task<IActionResult> List([FromQuery] string? status = null, [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50, CancellationToken token = default) =>
        ExecuteAsync(async session => Ok(await reports.ListAsync(session, status, cursor, limit, token)));

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Detail(Guid id, CancellationToken token) => ExecuteAsync(async session =>
        Ok(await reports.DetailAsync(session, id, token)));

    [HttpPatch("{id:guid}/status")]
    public Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateProblemReportStatusRequest request, CancellationToken token) =>
        ExecuteAsync(async session => Ok(await reports.UpdateStatusAsync(session, id, request, token)));

    private async Task<IActionResult> ExecuteAsync(Func<AdminAuthSession, Task<IActionResult>> action)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) || value is not AdminAuthSession session)
            return Unauthorized(new { code = "authentication_required", error = "Sign in again to continue." });
        try { return await action(session); }
        catch (ProblemReportException error) { return StatusCode(error.StatusCode, new { code = error.Code, error = error.Message }); }
    }
}

public sealed class ProblemReportInputFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
            context.Result = new BadRequestObjectResult(new { code = "report_invalid", error = "The problem report is invalid." });
    }
    public void OnActionExecuted(ActionExecutedContext context) { }
}
