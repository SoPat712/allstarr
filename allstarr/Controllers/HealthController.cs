using allstarr.Core.Health;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/health")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class HealthController(HealthSmokeService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Read(CancellationToken token) => Administrator() is null ? StatusCode(403) : Ok(await service.ReadAsync(token));

    [HttpPut]
    public async Task<IActionResult> Configure(HealthSmokeSaveRequest request, CancellationToken token)
    {
        if (Administrator() is not { AllstarrUserId: { } administrator }) return StatusCode(403);
        try
        {
            await service.ConfigureAsync(request, administrator, token);
            return Ok(await service.ReadAsync(token));
        }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (HealthSmokeConfigurationException exception) { return Conflict(new { code = exception.Code, error = exception.Message }); }
        catch (HttpRequestException) { return StatusCode(502, new { error = "The dedicated listener sign-in could not be verified." }); }
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken token)
    {
        if (Administrator() == null) return StatusCode(403);
        try { return Accepted(new { jobId = await service.EnqueueAsync(token) }); }
        catch (HealthSmokeConfigurationException exception) { return Conflict(new { code = exception.Code, error = exception.Message }); }
    }

    private AdminAuthSession? Administrator() =>
        HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is AdminAuthSession { IsAdministrator: true } session ? session : null;
}
