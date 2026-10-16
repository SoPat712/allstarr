using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Core.Configuration;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Antiforgery;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/access")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class AdminAccessController(AdminOidcSettingsService oidc, BackendSetupService backend, IHostApplicationLifetime lifetime, IAntiforgery antiforgery) : ControllerBase
{
    private AdminAuthSession? Actor => HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value)
        ? value as AdminAuthSession : null;

    [HttpGet("backend")]
    public async Task<IActionResult> GetBackend(CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (Actor is not { IsAdministrator: true } actor) return StatusCode(403, new { error = "Administrator access required." });
        try { return Ok(new { settings = await backend.GetServiceAccessAsync(actor, token), csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken }); }
        catch (UnauthorizedAccessException error) { return StatusCode(403, new { error = error.Message }); }
    }

    [HttpPut("backend")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> SaveBackend([FromBody] BackendServiceAccessRequest request, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            if (Actor is not { IsAdministrator: true } actor) return StatusCode(403, new { error = "Administrator access required." });
            if (!await antiforgery.IsRequestValidAsync(HttpContext))
                return BadRequest(new { error = "Reload access settings before saving.", code = "access_csrf_required" });
            await backend.SaveServiceAccessAsync(actor, request, token);
            Response.OnCompleted(() => { lifetime.StopApplication(); return Task.CompletedTask; });
            return Ok(new { saved = true, requiresRestart = true, message = "Media-server scanning access saved. Allstarr will restart." });
        }
        catch (UnauthorizedAccessException error) { return StatusCode(403, new { error = error.Message }); }
        catch (RuntimeSettingConflictException error) { return Conflict(new { error = error.Message, code = "setting_conflict" }); }
        catch (Exception error) when (DbErrors.IsTransientConflict(error) || DbErrors.IsUniqueViolation(error))
        { return Conflict(new { error = "Access settings changed during this save. Reload and try again.", code = "setting_conflict" }); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message, code = "invalid_setting" }); }
        catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException)
        { return StatusCode(503, new { error = "Media-server verification is unavailable. Check its connection and retry.", code = "backend_unavailable" }); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return StatusCode(503, new { error = "Media-server verification timed out. Check its connection and retry.", code = "backend_timeout" }); }
        finally { request.Password = ""; }
    }

    [HttpGet("oidc")]
    public async Task<IActionResult> GetOidc(CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (Actor is not { IsAdministrator: true } actor) return StatusCode(403, new { error = "Administrator access required." });
        try { return Ok(new { settings = await oidc.GetAsync(actor, token), csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken }); }
        catch (UnauthorizedAccessException error) { return StatusCode(403, new { error = error.Message }); }
    }

    [HttpPut("oidc")]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> SaveOidc([FromBody] AdminOidcSettingsRequest request, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            if (Actor is not { IsAdministrator: true } actor) return StatusCode(403, new { error = "Administrator access required." });
            if (!await antiforgery.IsRequestValidAsync(HttpContext))
                return BadRequest(new { error = "Reload access settings before saving.", code = "access_csrf_required" });
            await oidc.SaveAsync(actor, request, token);
            Response.OnCompleted(() => { lifetime.StopApplication(); return Task.CompletedTask; });
            return Ok(new { saved = true, requiresRestart = true, message = "Single sign-on saved. Allstarr will restart; media-server login remains available." });
        }
        catch (UnauthorizedAccessException error) { return StatusCode(403, new { error = error.Message }); }
        catch (RuntimeSettingConflictException error) { return Conflict(new { error = error.Message, code = "setting_conflict" }); }
        catch (Exception error) when (DbErrors.IsTransientConflict(error) || DbErrors.IsUniqueViolation(error))
        { return Conflict(new { error = "Access settings changed during this save. Reload and try again.", code = "setting_conflict" }); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message, code = "invalid_setting" }); }
        finally { request.ClientSecret = null; }
    }
}
