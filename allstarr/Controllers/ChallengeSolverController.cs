using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/challenge-solver")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ChallengeSolverController(CloudflareChallengeSolver solver,
    IDbContextFactory<AllstarrDbContext> database) : ControllerBase
{
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] ChallengeSolverOptions request)
    {
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession { IsAdministrator: true, AllstarrUserId: not null } actor)
            return StatusCode(403, new { error = "Administrator permissions required." });
        await using var db = await database.CreateDbContextAsync(HttpContext.RequestAborted);
        try { await AdminSettingsAccess.RequireActiveAsync(db, actor, HttpContext.RequestAborted); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "Administrator permissions required." }); }
        return Ok(await solver.TestAsync(request, HttpContext.RequestAborted));
    }
}
