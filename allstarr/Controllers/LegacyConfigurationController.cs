using allstarr.Core.Configuration;
using allstarr.Filters;
using allstarr.Middleware;
using allstarr.Services.Admin;
using allstarr.Core.Settings;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class LegacyConfigurationController : ControllerBase
{
    [HttpGet("config/migration/status")]
    public async Task<IActionResult> GetEnvMigrationStatus(CancellationToken cancellationToken = default)
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("env migration status");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        var service = HttpContext.RequestServices.GetRequiredService<LegacyEnvMigrationService>();
        var status = await service.GetStatusAsync(cancellationToken);
        return Ok(status with { SourcePresent = HttpContext.RequestServices.GetService<MountedLegacyEnvironment>()?.IsAvailable == true });
    }

    [HttpPost("config/migration/preview")]
    [RequestSizeLimit(LegacyEnvParser.MaxBytes * 2L)]
    public async Task<IActionResult> PreviewEnvMigration(
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        var adminCheck = RequireAdministratorForSensitiveOperation("env migration preview");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        if (file == null || file.Length is <= 0 or > LegacyEnvParser.MaxBytes)
        {
            return BadRequest(new { error = $"Choose a .env file between 1 and {LegacyEnvParser.MaxBytes} bytes." });
        }

        var sourceName = Path.GetFileName(file.FileName);
        if (sourceName.Length is 0 or > 255 ||
            !sourceName.EndsWith(".env", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Choose a file with a bounded .env filename." });
        }

        try
        {
            await using var source = file.OpenReadStream();
            using var buffer = new MemoryStream((int)file.Length);
            await source.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            try
            {
                var service = HttpContext.RequestServices.GetRequiredService<LegacyEnvMigrationService>();
                return Ok(await service.PreviewAsync(
                    bytes,
                    CreateMigrationActor(),
                    cancellationToken));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (LegacyEnvParseException ex)
        {
            return BadRequest(new { error = ex.Message, code = "invalid_env" });
        }
        catch (LegacyEnvMigrationException ex)
        {
            return BadRequest(new { error = ex.Message, code = ex.Code });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message, code = "invalid_setting" });
        }
    }

    [HttpPost("config/migration/apply")]
    public async Task<IActionResult> ApplyEnvMigration(
        [FromBody] ApplyLegacyEnvMigrationRequest request,
        CancellationToken cancellationToken = default)
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("env migration apply");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<LegacyEnvMigrationService>();
            return Ok(await service.ApplyAsync(
                request.PreviewToken ?? string.Empty,
                request.Revision ?? string.Empty,
                request.Confirmed,
                CreateMigrationActor(),
                cancellationToken));
        }
        catch (LegacyEnvMigrationException ex) when (ex.Code == "preview_owner_mismatch")
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message, code = ex.Code });
        }
        catch (LegacyEnvMigrationException ex) when (ex.Code is "revision_mismatch" or "state_changed" or
                                                      "provider_account_conflict")
        {
            return Conflict(new { error = ex.Message, code = ex.Code });
        }
        catch (LegacyEnvMigrationException ex)
        {
            return BadRequest(new { error = ex.Message, code = ex.Code });
        }
        catch (RuntimeSettingConflictException ex)
        {
            return Conflict(new { error = ex.Message, code = "setting_conflict" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message, code = "invalid_setting" });
        }
    }

    [HttpPost("config/migration/reset")]
    public async Task<IActionResult> ResetEnvMigration(
        [FromBody] ResetLegacyEnvMigrationRequest request,
        CancellationToken cancellationToken = default)
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("env migration reset");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<LegacyEnvMigrationService>();
            await service.ResetPreviewAsync(
                request.PreviewToken ?? string.Empty,
                CreateMigrationActor(),
                cancellationToken);
            return NoContent();
        }
        catch (LegacyEnvMigrationException ex) when (ex.Code == "preview_owner_mismatch")
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = ex.Message, code = ex.Code });
        }
        catch (LegacyEnvMigrationException ex) when (ex.Code == "preview_applied")
        {
            return Conflict(new { error = ex.Message, code = ex.Code });
        }
        catch (LegacyEnvMigrationException ex)
        {
            return BadRequest(new { error = ex.Message, code = ex.Code });
        }
    }

    [HttpPost("config/migration/preview-mounted")]
    public async Task<IActionResult> PreviewMountedEnvironment(CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        var adminCheck = RequireAdministratorForSensitiveOperation("mounted environment preview");
        if (adminCheck != null) return adminCheck;
        try
        {
            var source = HttpContext.RequestServices.GetRequiredService<MountedLegacyEnvironment>();
            var bytes = await source.ReadAsync(cancellationToken);
            try
            {
                return Ok(await HttpContext.RequestServices.GetRequiredService<LegacyEnvMigrationService>()
                    .PreviewAsync(bytes, CreateMigrationActor(), cancellationToken));
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (LegacyEnvParseException error) { return BadRequest(new { error = error.Message, code = "invalid_env" }); }
        catch (LegacyEnvMigrationException error) { return BadRequest(new { error = error.Message, code = error.Code }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Conflict(new { error = "The mounted environment is unavailable. Upload a saved .env file instead.", code = "mounted_env_unavailable" }); }
    }

    private LegacyEnvMigrationActor CreateMigrationActor()
    {
        var session = GetAdminSession() ?? throw new LegacyEnvMigrationException(
            "admin_session_required",
            "An administrator session is required.");
        var correlationId = HttpContext.Items[CorrelationMiddleware.HttpContextItemKey]?.ToString()
                            ?? HttpContext.TraceIdentifier;
        return new(session.SessionId, session.AllstarrUserId, correlationId);
    }

    public sealed class ApplyLegacyEnvMigrationRequest
    {
        public string? PreviewToken { get; set; }
        public string? Revision { get; set; }
        public bool Confirmed { get; set; }
    }

    public sealed class ResetLegacyEnvMigrationRequest
    {
        public string? PreviewToken { get; set; }
    }

    private AdminAuthSession? GetAdminSession() =>
        HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value)
            ? value as AdminAuthSession : null;

    private IActionResult? RequireAdministratorForSensitiveOperation(string operation) =>
        GetAdminSession()?.IsAdministrator == true ? null : StatusCode(403,
            new { error = "Administrator permissions required", message = "Sign in with a media-server administrator." });
}
