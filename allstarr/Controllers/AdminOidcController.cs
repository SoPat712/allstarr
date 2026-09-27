using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/auth/oidc")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class AdminOidcController(
    AdminOidcOptions options,
    AdminOidcLinks links,
    AdminAuthSessionService sessions,
    IAntiforgery antiforgery,
    ILogger<AdminOidcController> logger) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!options.Enabled) return Ok(new { enabled = false });
        var pending = await HttpContext.AuthenticateAsync(AdminOidcOptions.PendingScheme);
        var session = await sessions.GetValidSessionAsync(Request, cancellationToken);
        return Ok(new
        {
            enabled = true,
            displayName = options.DisplayName,
            loginUrl = Request.PathBase + "/api/admin/auth/oidc/login",
            linkPending = pending.Succeeded,
            linked = session != null && await links.IsLinkedAsync(session, cancellationToken),
            csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken
        });
    }

    [HttpGet("login")]
    public async Task<IActionResult> Login()
    {
        if (!options.Enabled) return NotFound();
        if (!Request.IsHttps) return BadRequest(new { error = "SSO requires HTTPS. Check the proxy's trusted forwarded-protocol configuration." });
        await HttpContext.SignOutAsync(AdminOidcOptions.PendingScheme);
        return Challenge(new AuthenticationProperties
        {
            RedirectUri = options.PublicUrl + "/api/admin/auth/oidc/complete",
            IsPersistent = false
        }, AdminOidcOptions.Scheme);
    }

    [HttpGet("complete")]
    public async Task<IActionResult> Complete(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return NotFound();
        if (!Request.IsHttps) return BadRequest(new { error = "SSO requires HTTPS." });
        var pending = await HttpContext.AuthenticateAsync(AdminOidcOptions.PendingScheme);
        var key = pending.Principal == null ? null : AdminOidcLinks.IdentityKey(pending.Principal, options.ClientId);
        if (key == null) return Redirect(options.PublicUrl + "/?oidc=failed");
        try
        {
            var session = await links.SignInAsync(key, cancellationToken);
            if (session == null) return Redirect(options.PublicUrl + "/?oidc=link");
            AdminSessionCookies.Write(HttpContext, session);
            await HttpContext.SignOutAsync(AdminOidcOptions.PendingScheme);
            return Redirect(options.PublicUrl + "/");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("OIDC backend validation failed ({ExceptionType})", exception.GetType().Name);
            return Redirect(options.PublicUrl + "/?oidc=failed");
        }
    }

    [HttpDelete("link")]
    public async Task<IActionResult> Unlink(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return NotFound();
        var session = await sessions.GetValidSessionAsync(Request, cancellationToken);
        if (session == null) return Unauthorized();
        if (!await antiforgery.IsRequestValidAsync(HttpContext)) return BadRequest(new { error = "Reload the page before disconnecting SSO." });
        await links.UnlinkAsync(session, cancellationToken);
        await HttpContext.SignOutAsync(AdminOidcOptions.PendingScheme);
        if (session.OidcSecretReferenceId != null) AdminSessionCookies.Delete(HttpContext);
        return Ok(new { success = true });
    }

    [HttpDelete("pending")]
    public async Task<IActionResult> Cancel()
    {
        if (!options.Enabled) return NotFound();
        if (!await antiforgery.IsRequestValidAsync(HttpContext)) return BadRequest();
        await HttpContext.SignOutAsync(AdminOidcOptions.PendingScheme);
        return Ok(new { success = true });
    }
}
