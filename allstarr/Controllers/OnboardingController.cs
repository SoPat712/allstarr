using allstarr.Core.Configuration;
using allstarr.Core.Settings;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/onboarding")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class OnboardingController(
    IDurableRuntimeSettings settings,
    LegacyEnvMigrationService legacyMigration) : ControllerBase
{
    private const string CompletionKey = "WebUi:SetupCompleted";

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken = default)
    {
        if (!TryGetAdminTenant(out _, out var tenantId, out var error))
        {
            return error!;
        }

        var completion = await settings.GetAsync(tenantId, CompletionKey, cancellationToken);
        var migration = await legacyMigration.GetStatusAsync(tenantId, cancellationToken);
        return Ok(CreateResponse(completion, migration, alreadyCompleted: false));
    }

    [HttpPost("complete")]
    public async Task<IActionResult> Complete(CancellationToken cancellationToken = default)
    {
        if (!TryGetAdminTenant(out var session, out var tenantId, out var error))
        {
            return error!;
        }

        var current = await settings.GetAsync(tenantId, CompletionKey, cancellationToken);
        var alreadyCompleted = current.Value is true;
        if (!alreadyCompleted)
        {
            try
            {
                var result = await settings.ApplyBatchAsync(
                    tenantId,
                    [new RuntimeSettingWrite(
                        CompletionKey,
                        "true",
                        current.Origin == RuntimeSettingOrigin.Durable ? current.Revision : null)],
                    "webui-onboarding",
                    session.AllstarrUserId,
                    cancellationToken);
                current = result.Settings.Single();
            }
            catch (RuntimeSettingConflictException)
            {
                // A second tab may have completed setup at the same time. Treat that race as
                // an idempotent success only when the authoritative value is now complete.
                current = await settings.GetAsync(tenantId, CompletionKey, cancellationToken);
                if (current.Value is not true)
                {
                    return Conflict(new
                    {
                        error = "Onboarding state changed. Refresh and try again.",
                        code = "onboarding_state_conflict"
                    });
                }

                alreadyCompleted = true;
            }
        }

        var migration = await legacyMigration.GetStatusAsync(tenantId, cancellationToken);
        return Ok(CreateResponse(current, migration, alreadyCompleted));
    }

    private static object CreateResponse(
        EffectiveRuntimeSetting completion,
        LegacyEnvMigrationStatus migration,
        bool alreadyCompleted)
    {
        var completed = completion.Value is true;
        return new
        {
            completed,
            completedAt = completed && completion.Origin == RuntimeSettingOrigin.Durable
                ? completion.UpdatedAt
                : null,
            completion.Revision,
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

    private bool TryGetAdminTenant(
        out AdminAuthSession session,
        out Guid tenantId,
        out IActionResult? error)
    {
        session = null!;
        tenantId = Guid.Empty;
        error = null;
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession current)
        {
            error = Unauthorized(new { error = "Authentication required" });
            return false;
        }

        if (!current.IsAdministrator)
        {
            error = StatusCode(StatusCodes.Status403Forbidden, new { error = "Administrator access required" });
            return false;
        }

        if (current.TenantId is not { } linkedTenantId)
        {
            error = Conflict(new
            {
                error = "The administrator session is not linked to an Allstarr tenant.",
                code = "tenant_required"
            });
            return false;
        }

        session = current;
        tenantId = linkedTenantId;
        return true;
    }
}
