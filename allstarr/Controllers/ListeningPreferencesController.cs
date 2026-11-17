using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/preferences")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ListeningPreferencesController(IDurableRuntimeSettings settings,
    AdminProtocolExecutionContextFactory? contexts = null, ListeningLibraries? libraries = null) : ControllerBase
{
    [HttpGet("libraries")]
    public Task<IActionResult> GetLibraries(CancellationToken cancellationToken) =>
        LibrariesAsync((context, token) => libraries!.ReadAsync(context, token), cancellationToken);

    /// <summary>Sets the music libraries this listener's songs are picked from; null means every library they can open.</summary>
    [HttpPut("libraries")]
    public Task<IActionResult> UpdateLibraries([FromBody] UpdateMyLibrariesRequest request, CancellationToken cancellationToken) =>
        LibrariesAsync((context, token) => libraries!.UpdateAsync(context, request.Selected, token), cancellationToken);

    private async Task<IActionResult> LibrariesAsync(Func<ProtocolExecutionContext, CancellationToken, Task<ListeningLibraryChoice?>> action,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session) return Unauthorized();
        if (contexts == null || libraries == null) return StatusCode(503, new { error = "Music libraries can't be read right now." });
        try
        {
            var choice = await action(await contexts.CreateAsync(session, HttpContext.TraceIdentifier, cancellationToken), cancellationToken);
            return choice == null ? StatusCode(503, new { error = "Your music libraries can't be read right now. Try again shortly." })
                : Ok(new { selected = choice.Selected, available = choice.Available.Select(library => new { id = library.Id, name = library.Name }), revision = choice.Revision });
        }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "A linked backend user is required." }); }
        catch (RuntimeSettingConflictException) { return Conflict(new { error = "Your libraries changed. Reload them and try again." }); }
        catch (ArgumentException) { return BadRequest(new { error = "Choose one or more of your music libraries." }); }
        catch (HttpRequestException) { return StatusCode(503, new { error = "Your music libraries can't be read right now. Try again shortly." }); }
    }

    [HttpGet]
    public Task<IActionResult> Get(CancellationToken cancellationToken) => ExecuteAsync(
        userId => settings.GetPreferencesAsync(userId, cancellationToken));

    [HttpPut]
    public Task<IActionResult> Update([FromBody] UpdateListeningPreferencesRequest request, CancellationToken cancellationToken) => ExecuteAsync(
        userId => settings.UpdatePreferencesAsync(userId,
            new(request.ExplicitFilter, request.ShowExternalLabel, request.ShowExplicitLabel,
                PlaybackStart: request.PlaybackStart, QualityLimit: request.QualityLimit),
            request.ExpectedRevision, cancellationToken));

    [HttpDelete]
    public Task<IActionResult> Reset([FromBody] ResetListeningPreferencesRequest request, CancellationToken cancellationToken) => ExecuteAsync(
        userId => settings.UpdatePreferencesAsync(userId, null,
            request.ExpectedRevision, cancellationToken));

    [HttpGet("users/{userId:guid}/clean-only-lock")]
    public Task<IActionResult> GetCleanOnlyLock(Guid userId, CancellationToken cancellationToken) => ExecuteAsync(
        actorUserId => settings.GetManagedPreferencesAsync(userId, actorUserId, cancellationToken), administrator: true);

    [HttpPut("users/{userId:guid}/clean-only-lock")]
    public Task<IActionResult> UpdateCleanOnlyLock(Guid userId, [FromBody] UpdateCleanOnlyLockRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(
        actorUserId => settings.UpdateCleanOnlyLockAsync(userId, request.Locked,
            request.ExpectedRevision, actorUserId, cancellationToken), administrator: true);

    private async Task<IActionResult> ExecuteAsync(Func<Guid, Task<PersonalListeningPreferences>> action, bool administrator = false)
    {
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session)
            return Unauthorized();
        if (administrator && !session.IsAdministrator)
            return StatusCode(403, new { error = "Administrator permissions required." });
        if (session.AllstarrUserId is not { } userId)
            return StatusCode(403, new { error = "A linked backend user is required." });
        try { return Ok(await action(userId)); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "This user cannot change listening preferences." }); }
        catch (RuntimeSettingConflictException) { return Conflict(new { error = "Your preferences changed. Reload them and try again." }); }
        catch (ArgumentException) { return BadRequest(new { error = "The listening preferences are invalid." }); }
    }
}

public sealed record UpdateListeningPreferencesRequest(
    string ExplicitFilter, bool ShowExternalLabel, bool ShowExplicitLabel, string ExpectedRevision,
    string? PlaybackStart = null, string? QualityLimit = null);
public sealed record ResetListeningPreferencesRequest(string ExpectedRevision);
public sealed record UpdateCleanOnlyLockRequest(bool Locked, string ExpectedRevision);
public sealed record UpdateMyLibrariesRequest(IReadOnlyList<string>? Selected);
