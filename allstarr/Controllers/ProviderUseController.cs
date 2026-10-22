using allstarr.Core.Capabilities;
using allstarr.Core.Settings;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/providers/use")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ProviderUseController(
    IDurableRuntimeSettings settings, IEffectiveProviderPolicyResolver policies, IProviderRegistry registry) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (Session() is not { AllstarrUserId: { } userId }) return Unauthorized();
        var personal = await settings.GetPreferencesAsync(userId, cancellationToken);
        var household = await settings.GetPreferencesAsync(null, cancellationToken);
        var policy = await policies.ResolveForUserAsync(userId, cancellationToken);
        var defaults = await policies.ResolveAsync(cancellationToken);
        return Ok(new
        {
            personalRevision = personal.Revision,
            householdRevision = household.Revision,
            providers = registry.Providers.Select(provider => provider.Id).Append("musicbrainz")
                .Distinct(StringComparer.Ordinal).Select(providerId => new
                {
                    providerId,
                    householdMode = ProviderUsePolicy.Name(defaults.GetProviderMode(providerId)),
                    personalMode = personal.ProviderModeOverrides?.GetValueOrDefault(providerId),
                    effectiveMode = ProviderUsePolicy.Name(policy.GetProviderMode(providerId)),
                    canChoosePersonal = defaults.GetProviderMode(providerId) != ProviderUseMode.Off
                })
        });
    }

    [HttpPut("{providerId}")]
    public async Task<IActionResult> Update(string providerId, [FromBody] UpdateProviderUseRequest request,
        CancellationToken cancellationToken)
    {
        if (Session() is not { AllstarrUserId: { } userId } session) return Unauthorized();
        if (request.Scope is not ("personal" or "household")) return BadRequest(new { error = "Choose a personal or household scope." });
        if (request.Scope == "household" && !session.IsAdministrator) return StatusCode(403);
        try
        {
            var sourceId = registry.TryGet(providerId, out var provider) ? provider!.Id :
                providerId.Equals("musicbrainz", StringComparison.OrdinalIgnoreCase) ? "musicbrainz" : null;
            if (sourceId == null) return NotFound();
            await settings.UpdateProviderModeAsync(request.Scope == "household" ? null : userId,
                sourceId, request.Mode, request.ExpectedRevision, userId, cancellationToken);
            return await Get(cancellationToken);
        }
        catch (RuntimeSettingConflictException) { return Conflict(new { error = "Source preferences changed. Refresh and try again." }); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
        catch (ArgumentException) { return BadRequest(new { error = "The source use mode is invalid." }); }
    }

    private AdminAuthSession? Session() => HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] as AdminAuthSession;
}

public sealed record UpdateProviderUseRequest(string? Mode, string Scope, string ExpectedRevision);
