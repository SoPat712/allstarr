using System.Text.Json;
using allstarr.Core.Configuration;
using allstarr.Core.Storage;
using allstarr.Filters;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/auth/setup")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class BackendSetupController(BackendSetupService setup, DurableStorageState storage,
    IAntiforgery antiforgery, IHostApplicationLifetime lifetime) : ControllerBase
{
    [HttpGet]
    public IActionResult Status()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            configured = setup.IsConfigured,
            storageReady = storage.GetSnapshot().Readiness == DurableStorageReadiness.Ready,
            step = SetupGuide.Steps[0],
            csrfToken = setup.IsConfigured ? null : antiforgery.GetAndStoreTokens(HttpContext).RequestToken
        });
    }

    [HttpPost]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> Save([FromBody] BackendSetupRequest request, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await antiforgery.IsRequestValidAsync(HttpContext))
            return BadRequest(new { error = "Reload setup before saving the media server.", code = "setup_csrf_required" });
        try
        {
            await setup.SaveAsync(request, token);
            Response.OnCompleted(() => { lifetime.StopApplication(); return Task.CompletedTask; });
            return Ok(new { saved = true, requiresRestart = true, message = "The media server is saved. Allstarr will restart; sign in when it returns." });
        }
        catch (BackendSetupException error)
        {
            var status = error.Code switch
            {
                "backend_already_configured" => 409,
                "administrator_required" => 403,
                "storage_unavailable" => 503,
                _ => 400
            };
            return StatusCode(status, new { error = error.Message, code = error.Code });
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException or InvalidOperationException)
        { return StatusCode(503, new { error = "Media-server verification or storage is unavailable. Check the server and data folder, then retry.", code = "setup_unavailable" }); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return StatusCode(503, new { error = "Media-server verification timed out. Check its address and retry.", code = "setup_timeout" }); }
        finally { request.Password = ""; }
    }
}
