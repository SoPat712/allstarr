using allstarr.Core.Configuration;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/onboarding")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class OnboardingController(
    OnboardingStateService onboarding,
    LegacyEnvMigrationService legacyMigration) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken = default)
    {
        if (!TryGetAdminScope(out _, out var tenantId, out var userId, out var error))
        {
            return error!;
        }

        return Ok(await CreateResponseAsync(
            await onboarding.GetAsync(tenantId, userId, cancellationToken),
            tenantId,
            alreadyCompleted: false,
            cancellationToken));
    }

    [HttpPost("complete")]
    public async Task<IActionResult> Complete(CancellationToken cancellationToken = default)
    {
        if (!TryGetAdminScope(out var session, out var tenantId, out var userId, out var error))
        {
            return error!;
        }

        var current = await onboarding.GetAsync(tenantId, userId, cancellationToken);
        try
        {
            var completed = await onboarding.CompleteAsync(
                tenantId,
                userId,
                $"onboarding:{session.SessionId}",
                cancellationToken);
            return Ok(await CreateResponseAsync(
                completed,
                tenantId,
                current.Completed,
                cancellationToken));
        }
        catch (OnboardingStateException exception)
        {
            return Conflict(new { error = exception.Message, code = exception.Code });
        }
    }

    [HttpPost("reopen")]
    public async Task<IActionResult> Reopen(CancellationToken cancellationToken = default)
    {
        if (!TryGetAdminScope(out var session, out var tenantId, out var userId, out var error))
        {
            return error!;
        }

        var state = await onboarding.ReopenAsync(
            tenantId,
            userId,
            $"onboarding:{session.SessionId}",
            cancellationToken);
        return Ok(await CreateResponseAsync(
            state,
            tenantId,
            alreadyCompleted: false,
            cancellationToken));
    }

    private async Task<object> CreateResponseAsync(
        OnboardingStateSnapshot state,
        Guid tenantId,
        bool alreadyCompleted,
        CancellationToken cancellationToken)
    {
        var migration = await legacyMigration.GetStatusAsync(tenantId, cancellationToken);
        return new
        {
            completed = state.Completed,
            setupOpen = state.SetupOpen,
            shouldRedirectToSetup = state.ShouldRedirectToSetup,
            schemaVersion = state.SchemaVersion,
            completedSteps = state.CompletedSteps,
            completionSource = state.CompletionSource,
            completedAt = state.CompletedAt,
            reopenedAt = state.ReopenedAt,
            revision = state.Revision,
            recoveryNotices = state.RecoveryNotices,
            alreadyCompleted,
            migration = new
            {
                migration.Available,
                migration.Completed,
                migration.FirstRun,
                migration.LastAppliedAt
            }
        };
    }

    private bool TryGetAdminScope(
        out AdminAuthSession session,
        out Guid tenantId,
        out Guid userId,
        out IActionResult? error)
    {
        session = null!;
        tenantId = Guid.Empty;
        userId = Guid.Empty;
        error = null;
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession current)
        {
            error = Unauthorized(new { error = "Authentication required" });
            return false;
        }

        if (!current.IsAdministrator)
        {
            error = StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "Administrator access required" });
            return false;
        }

        if (current.TenantId is not { } linkedTenantId ||
            current.AllstarrUserId is not { } linkedUserId)
        {
            error = Conflict(new
            {
                error = "The administrator session is not linked to an Allstarr tenant and user.",
                code = "user_required"
            });
            return false;
        }

        session = current;
        tenantId = linkedTenantId;
        userId = linkedUserId;
        return true;
    }
}
