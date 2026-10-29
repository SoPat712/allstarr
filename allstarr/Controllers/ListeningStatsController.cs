using allstarr.Core.Playback;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/stats")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ListeningStatsController(ListeningStatsService stats) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? period = null, [FromQuery] int? year = null, CancellationToken token = default)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session)
            return Unauthorized();
        try { return Ok(await stats.GetAsync(session, period, year, token)); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "A linked backend listener is required." }); }
        catch (ArgumentException) { return BadRequest(new { error = "Use period=7d, 30d, year or all, or year=1 through 9998." }); }
    }
}
