using allstarr.Core.Favorites;
using allstarr.Core.Protocols;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/spotify-likes")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class SpotifyLikesController(SpotifyLikesImport imports, AdminProtocolExecutionContextFactory contexts) : ControllerBase
{
    [HttpPost("import")]
    public Task<IActionResult> Import([FromBody] ImportSpotifyLikesRequest request, CancellationToken token) => ExecuteAsync(async execution =>
    {
        var receipt = await imports.StartAsync(execution, request.ProviderAccountId, request.RequestId, token);
        return AcceptedAtAction(nameof(Get), new { jobId = receipt.JobId }, receipt);
    }, token);

    [HttpGet("{jobId:guid}")]
    public Task<IActionResult> Get(Guid jobId, CancellationToken token) => ExecuteAsync(async execution =>
        await imports.GetAsync(execution, jobId, token) is { } status ? Ok(status) : NotFound(), token);

    private async Task<IActionResult> ExecuteAsync(Func<ProtocolExecutionContext, Task<IActionResult>> action, CancellationToken token)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session)
            return Unauthorized(new { error = "Authentication required" });
        try { return await action(await contexts.CreateAsync(session, HttpContext.TraceIdentifier, token)); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException) { return BadRequest(new { error = "A personal Spotify account and request ID are required." }); }
        catch (InvalidOperationException) { return Conflict(new { error = "The request ID has already been used for a different import." }); }
    }
}

public sealed record ImportSpotifyLikesRequest(Guid ProviderAccountId, Guid RequestId);
