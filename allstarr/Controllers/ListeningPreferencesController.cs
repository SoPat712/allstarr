using allstarr.Core.Settings;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/preferences")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ListeningPreferencesController(IDurableRuntimeSettings settings) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Get(CancellationToken cancellationToken) => ExecuteAsync(
        userId => settings.GetPreferencesAsync(userId, cancellationToken));

    [HttpPut]
    public Task<IActionResult> Update([FromBody] UpdateListeningPreferencesRequest request, CancellationToken cancellationToken) => ExecuteAsync(
        userId => settings.UpdatePreferencesAsync(userId,
            new(request.ExplicitFilter, request.ShowExternalLabel, request.ShowExplicitLabel),
            request.ExpectedRevision, cancellationToken));

    [HttpDelete]
    public Task<IActionResult> Reset([FromBody] ResetListeningPreferencesRequest request, CancellationToken cancellationToken) => ExecuteAsync(
        userId => settings.UpdatePreferencesAsync(userId, null,
            request.ExpectedRevision, cancellationToken));

    private async Task<IActionResult> ExecuteAsync(Func<Guid, Task<PersonalListeningPreferences>> action)
    {
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session)
            return Unauthorized();
        if (session.AllstarrUserId is not { } userId)
            return StatusCode(403, new { error = "A linked backend user is required." });
        try { return Ok(await action(userId)); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "This user cannot change listening preferences." }); }
        catch (RuntimeSettingConflictException) { return Conflict(new { error = "Your preferences changed. Reload them and try again." }); }
        catch (ArgumentException) { return BadRequest(new { error = "The listening preferences are invalid." }); }
    }
}

public sealed record UpdateListeningPreferencesRequest(
    string ExplicitFilter, bool ShowExternalLabel, bool ShowExplicitLabel, string ExpectedRevision);
public sealed record ResetListeningPreferencesRequest(string ExpectedRevision);
