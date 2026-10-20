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
            providers = registry.Providers.Select(provider => new
            {
                providerId = provider.Id,
                mode = ProviderUsePolicy.Name(policy.GetProviderMode(provider.Id)),
                householdMode = ProviderUsePolicy.Name(defaults.GetProviderMode(provider.Id)),
                supportsAutomaticMatching = provider.MatchingMode == ProviderMatchingMode.Automatic,
                householdDisabled = policy.DisabledProviders.Contains(provider.Id)
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
            if (!registry.TryGet(providerId, out var provider)) return NotFound();
            if (request.Mode == "automatic" && provider!.MatchingMode == ProviderMatchingMode.ManualOnly)
                return BadRequest(new { error = "This source supports manual selections only." });
            await settings.UpdateProviderModeAsync(request.Scope == "household" ? null : userId,
                provider!.Id, request.Mode, request.ExpectedRevision, userId, cancellationToken);
            return await Get(cancellationToken);
        }
        catch (RuntimeSettingConflictException) { return Conflict(new { error = "Source preferences changed. Refresh and try again." }); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
        catch (ArgumentException) { return BadRequest(new { error = "The source use mode is invalid." }); }
    }

    private AdminAuthSession? Session() => HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] as AdminAuthSession;
}

public sealed record UpdateProviderUseRequest(string? Mode, string Scope, string ExpectedRevision);
