using allstarr.Core.Enrichment;
using allstarr.Core.Protocols;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/library-promotions/profiles")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class PromotionProfilesController(PromotionProfiles profiles, PromotionNamingPreviewService preview,
    AdminProtocolExecutionContextFactory contexts) : ControllerBase
{
    [HttpGet("me")]
    public Task<IActionResult> Mine(CancellationToken token) => ExecuteAsync(token, context => profiles.GetMineAsync(context, token));

    [HttpPut("me")]
    public Task<IActionResult> SaveMine([FromBody] PromotionProfile request, CancellationToken token) =>
        ExecuteAsync(token, context => profiles.SaveMineAsync(context, request, token));

    [HttpDelete("me")]
    public Task<IActionResult> ResetMine(CancellationToken token) => ExecuteAsync(token, context => profiles.SaveMineAsync(context, null, token));

    [HttpGet("household")]
    public Task<IActionResult> Household(CancellationToken token) => ExecuteAsync(token, context => profiles.GetHouseholdAsync(context, token), administrator: true);

    [HttpPut("household")]
    public Task<IActionResult> SaveHousehold([FromBody] HouseholdPromotionProfile request, CancellationToken token) =>
        ExecuteAsync(token, context => profiles.SaveHouseholdAsync(context, request, token), administrator: true);

    [HttpPost("preview")]
    public Task<IActionResult> Preview([FromBody] PromotionNamingPreviewRequest request, CancellationToken token) =>
        ExecuteAsync(token, context => preview.PreviewAsync(context, request, token));

    private async Task<IActionResult> ExecuteAsync<T>(CancellationToken token, Func<ProtocolExecutionContext, Task<T>> action, bool administrator = false)
    {
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session) return Unauthorized();
        if (session.AllstarrUserId == null || administrator && !session.IsAdministrator)
            return StatusCode(403, new { error = "A linked user with the required permissions is required." });
        try
        {
            var context = await contexts.CreateAsync(session, HttpContext.TraceIdentifier, token);
            return Ok(await action(context));
        }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "These promotion settings or examples are unavailable to this user." }); }
        catch (PromotionProfileValidationException error) { return BadRequest(new { errors = error.Errors }); }
        catch (ArgumentException) { return BadRequest(new { error = "The promotion profile or preview request is invalid." }); }
        catch (DbUpdateException) { return Conflict(new { error = "The promotion profile changed. Reload it and try again." }); }
    }
}
