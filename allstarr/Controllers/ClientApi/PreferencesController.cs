using allstarr.Core.Settings;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

public sealed record ClientPreferences(string HeartAction);

[Route("allstarr/v1/preferences")]
public sealed class PreferencesController(IDurableRuntimeSettings settings) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("getPreferences")]
    [ProducesResponseType<ClientPreferences>(200)]
    public async Task<ActionResult<ClientPreferences>> Get(CancellationToken token)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new ClientPreferences((await settings.GetPreferencesAsync(Execution.Principal!.UserId, token)).Values.HeartAction ?? "keep"));
    }

    [HttpPatch]
    [EndpointName("updatePreferences")]
    [ProducesResponseType<ClientPreferences>(200)]
    public async Task<ActionResult<ClientPreferences>> Update([FromBody] ClientPreferences request, CancellationToken token)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (request.HeartAction is not ("heart" or "keep" or "addToLibrary")) return BadRequest();
        try
        {
            var owner = Execution.Principal!.UserId;
            await settings.UpdateHeartActionAsync(owner, request.HeartAction, token);
            return Ok(request);
        }
        catch (RuntimeSettingConflictException) { return Conflict(); }
    }
}
