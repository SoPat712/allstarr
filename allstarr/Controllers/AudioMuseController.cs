using allstarr.Core.AudioMuse;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/audiomuse")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class AudioMuseController(AudioMuseDiscovery discovery, AudioMuseSettings settings,
    AdminProtocolExecutionContextFactory contexts) : ControllerBase
{
    [HttpGet("connection")]
    public Task<IActionResult> Connection(CancellationToken token) => ExecuteAsync(async (_, _) =>
        Ok(await settings.GetAsync(token)), token, administrator: true);

    [HttpPut("connection")]
    public Task<IActionResult> Configure([FromBody] AudioMuseConnectionRequest request, CancellationToken token) => ExecuteAsync(async (_, session) =>
    {
        await settings.ApplyAsync(session, request.BaseUrl, request.ApiKey, request.Revision, token);
        return Ok(await settings.GetAsync(token));
    }, token, administrator: true);

    [HttpGet("status")]
    public Task<IActionResult> Status(CancellationToken token) => ExecuteAsync(async (context, _) =>
        Ok(await discovery.StatusAsync(context, token)), token);

    [HttpGet("seeds")]
    public Task<IActionResult> Seeds([FromQuery] string query, CancellationToken token) => ExecuteAsync(async (context, _) =>
        Ok(await discovery.SearchAsync(context, query, token)), token);

    [HttpPost("similar")]
    public Task<IActionResult> Similar([FromBody] AudioMuseSeedPreviewRequest request, CancellationToken token) => ExecuteAsync(async (context, _) =>
        Ok(await discovery.SimilarAsync(context, request.SeedId, request.N, token)), token);

    [HttpPost("mood")]
    public Task<IActionResult> Mood([FromBody] AudioMuseMoodRequest request, CancellationToken token) => ExecuteAsync(async (context, _) =>
        Ok(await discovery.MoodAsync(context, request.Mood, request.N, token)), token);

    [HttpPost("save")]
    public Task<IActionResult> Save([FromBody] AudioMuseSaveRequest request, CancellationToken token) => ExecuteAsync(async (context, _) =>
        Ok(await discovery.SaveAsync(context, request.PreviewId, request.Name, token)), token);

    private async Task<IActionResult> ExecuteAsync(Func<ProtocolExecutionContext, AdminAuthSession, Task<IActionResult>> action,
        CancellationToken token, bool administrator = false)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session) return Unauthorized();
        if (administrator && !session.IsAdministrator) return Forbid();
        try { return await action(await contexts.CreateAsync(session, HttpContext.TraceIdentifier, token), session); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException) { return BadRequest(new { error = "Invalid connection, song, recipe, length, playlist name or expired preview." }); }
        catch (RuntimeSettingConflictException) { return Conflict(new { error = "Connection changed. Reload before saving." }); }
    }
}

public sealed class AudioMuseConnectionRequest
{
    public string BaseUrl { get; init; } = "";
    public string? ApiKey { get; init; }
    public long? Revision { get; init; }
}
public sealed record AudioMuseSeedPreviewRequest(string SeedId, int N = 25);
public sealed record AudioMuseMoodRequest(string Mood, int N = 25);
public sealed record AudioMuseSaveRequest(Guid PreviewId, string Name);
