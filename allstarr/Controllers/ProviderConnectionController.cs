using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ProviderConnectionController(ILogger<ProviderConnectionController> logger) : ControllerBase
{
    private readonly ILogger<ProviderConnectionController> _logger = logger;
    private AdminAuthSession? GetAdminSession() =>
        HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value)
            ? value as AdminAuthSession
            : null;

    private IActionResult? RequireAdministratorForSensitiveOperation(string operationName)
    {
        if (HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var sessionObj) &&
            sessionObj is AdminAuthSession session &&
            session.IsAdministrator)
        {
            return null;
        }

        _logger.LogWarning("Blocked sensitive admin operation '{Operation}' due to missing administrator session", operationName);
        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            error = "Administrator permissions required",
            message = "This operation is restricted to Jellyfin administrators."
        });
    }

    [HttpGet("providers/status")]
    public async Task<IActionResult> GetProvidersStatus(CancellationToken cancellationToken = default)
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("get providers status");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        var statusManager = HttpContext.RequestServices.GetRequiredService<ProviderStatusManager>();
        var contextFactory = HttpContext.RequestServices
            .GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.ProviderAccounts.AsNoTracking()
            .OrderBy(item => item.ProviderId)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

        accounts = accounts.Where(item => !ProviderAccountSettings.IsRemoved(item)).ToList();
        var results = new List<object>();
        foreach (var status in statusManager.GetAllAccountFreeStatuses().Concat(statusManager.GetAppleDownloadEndpointStatuses())
            .Where(item => !accounts.Any(account => account.ProviderId.Equals(item.Provider, StringComparison.OrdinalIgnoreCase))))
        {
            results.Add(new
            {
                provider = status.Provider,
                providerAccountId = (Guid?)null,
                providerAccountName = (string?)null,
                capability = status.Capability,
                accountScope = "public",
                supported = status.IsSupported,
                enabled = status.IsEnabled,
                configuration = status.Configuration == ProviderConfigurationState.NeedsConfiguration ? "needs_configuration" : "not_required",
                health = status.Health.ToString().ToLowerInvariant(),
                ready = status.IsReady,
                canAttempt = status.CanAttempt,
                testedAt = status.TestedAt,
                reasonCode = status.ReasonCode,
                verificationMeasurementId = status.VerificationMeasurementId,
                verificationMethod = status.VerificationMethod,
                canTest = statusManager.CanTestCapability(status.Provider, status.Capability)
            });
        }
        foreach (var account in accounts)
        {
            IReadOnlyDictionary<string, string> accountSecrets =
                new Dictionary<string, string>(StringComparer.Ordinal);
            if (account.SecretReferenceId.HasValue)
            {
                try
                {
                    accountSecrets = await ReadProviderAccountSecretsAsync(account, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        "Managed provider account status configuration could not be opened ({ExceptionType})",
                        ex.GetType().Name);
                }
            }

            foreach (var status in statusManager.GetAllManagedStatuses(
                         account.ProviderId,
                         account.Id,
                         accountSecrets, account.Revision))
            {
                results.Add(new
                {
                    provider = status.Provider,
                    providerAccountId = account.Id,
                    providerAccountName = account.DisplayName,
                    capability = status.Capability,
                    accountScope = account.Scope.ToString().ToLowerInvariant(),
                    supported = status.IsSupported,
                    enabled = account.Enabled && status.IsEnabled,
                    configuration = status.Configuration switch
                    {
                        ProviderConfigurationState.NotRequired => "not_required",
                        ProviderConfigurationState.Configured => "configured",
                        _ => "needs_configuration"
                    },
                    health = status.Health.ToString().ToLowerInvariant(),
                    ready = account.Enabled && status.IsReady,
                    canAttempt = account.Enabled && status.CanAttempt,
                    testedAt = status.TestedAt,
                    reasonCode = account.Enabled ? status.ReasonCode : "account_disabled",
                    verificationMeasurementId = status.VerificationMeasurementId,
                    verificationMethod = status.VerificationMethod,
                    canTest = statusManager.CanTestCapability(status.Provider, status.Capability)
                });
            }
        }

        return Ok(results);
    }

    [HttpPost("providers/test/{provider}")]
    [HttpPost("providers/test/{provider}/{capability}")]
    public async Task<IActionResult> TestProvider(
        string provider,
        string? capability = null,
        [FromQuery] Guid? accountId = null)
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("test provider connection");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        var statusManager = HttpContext.RequestServices.GetRequiredService<ProviderStatusManager>();
        var normalizedProvider = provider.Trim().ToLowerInvariant();
        capability = capability?.Trim().ToLowerInvariant();
        if (!accountId.HasValue || accountId == Guid.Empty)
        {
            if (string.IsNullOrWhiteSpace(capability))
            {
                return BadRequest(new
                {
                    success = false,
                    error = "Select a capability to test"
                });
            }

            if (!statusManager.CanTestCapability(normalizedProvider, capability))
            {
                return Ok(new
                {
                    success = false,
                    provider = normalizedProvider,
                    capability,
                    health = "unknown",
                    reasonCode = "probe_not_available"
                });
            }

            if (!statusManager.CanTestAccountFreeCapability(normalizedProvider, capability))
            {
                return BadRequest(new { success = false, error = "Select a provider account for this capability" });
            }

            if (capability == ProviderCapabilities.Streaming)
            {
                var media = await MeasureMediaAsync(null, normalizedProvider);
                return media == null
                    ? Conflict(new { error = "A verified administrator identity is required." })
                    : MediaTestResult(null, capability, media, []);
            }

            var accountFreeTimer = System.Diagnostics.Stopwatch.StartNew();
            var testedAccountFree = await statusManager.TestAccountFreeProviderCapabilityAsync(
                normalizedProvider,
                capability,
                HttpContext.RequestAborted);
            accountFreeTimer.Stop();
            var accountFreeLatencyMs = accountFreeTimer.ElapsedMilliseconds;
            return Ok(new
            {
                success = testedAccountFree.Health == allstarr.Services.Common.ProviderHealthState.Healthy,
                provider = testedAccountFree.Provider,
                capability = testedAccountFree.Capability,
                health = testedAccountFree.Health.ToString().ToLowerInvariant(),
                latencyMs = accountFreeLatencyMs,
                bars = ConnectivityQuality.Bars(accountFreeLatencyMs, testedAccountFree.Health == allstarr.Services.Common.ProviderHealthState.Healthy, ConnectivityMetric.ApiLatency),
                metric = "api-latency",
                testedAt = testedAccountFree.TestedAt,
                reasonCode = testedAccountFree.ReasonCode
            });
        }

        var contextFactory = HttpContext.RequestServices
            .GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(HttpContext.RequestAborted);
        var account = await context.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == accountId.Value,
            HttpContext.RequestAborted);
        if (account == null ||
            !account.ProviderId.Equals(normalizedProvider, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { success = false, error = "Managed provider account not found" });
        }

        IReadOnlyDictionary<string, string> accountSecrets;
        try
        {
            accountSecrets = await ReadProviderAccountSecretsAsync(account, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Managed provider account probe configuration could not be opened ({ExceptionType})",
                ex.GetType().Name);
            return BadRequest(new
            {
                success = false,
                error = "The managed provider account credential is missing or invalid"
            });
        }

        if (capability == ProviderCapabilities.Streaming)
        {
            if (!statusManager.GetManagedStatus(normalizedProvider, capability, account.Id, accountSecrets, account.Revision).IsSupported)
                return BadRequest(new { success = false, provider = normalizedProvider, capability, error = "Unsupported provider capability" });
            var media = await MeasureMediaAsync(account);
            return media == null
                ? Conflict(new { error = "A verified administrator identity is required." })
                : MediaTestResult(account, capability, media, []);
        }

        if (string.IsNullOrWhiteSpace(capability))
        {
            var connectionTimer = System.Diagnostics.Stopwatch.StartNew();
            var healthy = await statusManager.TestManagedProviderConnectionAsync(
                normalizedProvider,
                account.Id,
                accountSecrets,
                HttpContext.RequestAborted, account.Revision);
            connectionTimer.Stop();
            var connectionLatencyMs = connectionTimer.ElapsedMilliseconds;
            var failedCapabilities = statusManager.GetAllManagedStatuses(
                    normalizedProvider,
                    account.Id,
                    accountSecrets, account.Revision)
                .Where(item => item.Health == allstarr.Services.Common.ProviderHealthState.Degraded)
                .Select(item => new FailedCapability(item.Capability, item.ReasonCode))
                .ToArray();
            string? mediaReason = null;
            var mediaStatus = statusManager.GetManagedStatus(normalizedProvider, ProviderCapabilities.Streaming,
                account.Id, accountSecrets, account.Revision);
            if (healthy && mediaStatus.IsSupported && mediaStatus.IsEnabled &&
                mediaStatus.Configuration != ProviderConfigurationState.NeedsConfiguration)
            {
                var media = await MeasureMediaAsync(account);
                if (media != null && (media.Stage != "track-selection" || !ProviderRuntimeStatus.IsNeutralReason(media.ReasonCode)))
                    return MediaTestResult(account, null, media, failedCapabilities);
                mediaReason = media == null ? "media_sample_unavailable" : "media_sample_needs_known_track";
            }

            return Ok(new
            {
                success = true,
                provider = normalizedProvider,
                providerAccountId = account.Id,
                healthy,
                latencyMs = connectionLatencyMs,
                bars = ConnectivityQuality.Bars(connectionLatencyMs, healthy, ConnectivityMetric.ApiLatency),
                metric = "api-latency",
                reasonCode = failedCapabilities.FirstOrDefault()?.ReasonCode ?? mediaReason,
                failedCapabilities
            });
        }

        var current = statusManager.GetManagedStatus(
            normalizedProvider,
            capability,
            account.Id,
            accountSecrets, account.Revision);
        if (!current.IsSupported)
        {
            return BadRequest(new
            {
                success = false,
                provider = normalizedProvider,
                capability,
                error = "Unsupported provider capability"
            });
        }

        var capabilityTimer = System.Diagnostics.Stopwatch.StartNew();
        var tested = await statusManager.TestManagedProviderCapabilityAsync(
            normalizedProvider,
            capability,
            account.Id,
            accountSecrets,
            HttpContext.RequestAborted, account.Revision);
        capabilityTimer.Stop();
        var capabilityLatencyMs = capabilityTimer.ElapsedMilliseconds;
        return Ok(new
        {
            success = tested.Health == allstarr.Services.Common.ProviderHealthState.Healthy,
            provider = tested.Provider,
            providerAccountId = account.Id,
            capability = tested.Capability,
            health = tested.Health.ToString().ToLowerInvariant(),
            latencyMs = capabilityLatencyMs,
            bars = ConnectivityQuality.Bars(capabilityLatencyMs, tested.Health == allstarr.Services.Common.ProviderHealthState.Healthy, ConnectivityMetric.ApiLatency),
            metric = "api-latency",
            testedAt = tested.TestedAt,
            reasonCode = tested.ReasonCode
        });
    }

    private sealed record FailedCapability(string Capability, string? ReasonCode);

    private async Task<ProviderCtsDiagnosticResult?> MeasureMediaAsync(ProviderAccountRecord? account, string? providerId = null)
    {
        var session = GetAdminSession();
        if (session?.AllstarrUserId is not { } userId) return null;
        var token = HttpContext.RequestAborted;
        await using var db = await HttpContext.RequestServices
            .GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync(token);
        var identity = await db.Users.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == userId && item.Enabled && item.IsAdmin &&
            item.BackendType == session.BackendType.ToLowerInvariant() &&
            item.BackendInstanceId == session.BackendInstanceId && item.BackendPrincipalId == session.UserId, token);
        if (identity == null) return null;
        var actor = new ProviderActorContext(ProviderActorKind.Administrator, userId,
            new ProviderBackendPrincipal(identity.BackendType, identity.BackendInstanceId, identity.BackendPrincipalId));
        var policy = await HttpContext.RequestServices.GetRequiredService<IEffectiveProviderPolicyResolver>()
            .ResolveForUserAsync(userId, token);
        return await HttpContext.RequestServices.GetRequiredService<ProviderCtsDiagnosticRunner>().MeasureAsync(
            actor, account?.ProviderId ?? providerId!, account?.Id, AudioQualityPolicy.RequestedQuality(policy.AudioQuality),
            HttpContext.TraceIdentifier.Length <= 100 ? HttpContext.TraceIdentifier : HttpContext.TraceIdentifier[..100],
            cancellationToken: token);
    }

    private IActionResult MediaTestResult(ProviderAccountRecord? account, string? capability,
        ProviderCtsDiagnosticResult result, IReadOnlyList<FailedCapability> failedCapabilities)
    {
        if (result.RetryAfterSeconds.HasValue) Response.Headers.RetryAfter = result.RetryAfterSeconds.Value.ToString();
        return Ok(new
        {
            success = result.Succeeded,
            healthy = result.Succeeded,
            provider = result.ProviderId,
            providerAccountId = account?.Id,
            capability,
            health = ProviderRuntimeStatus.IsNeutralReason(result.ReasonCode) ? "unknown" : result.Succeeded ? "healthy" : "degraded",
            latencyMs = result.ClickToStreamMilliseconds,
            bars = result.Bars,
            metric = "cts",
            testedAt = result.MeasuredAt,
            reasonCode = result.ReasonCode,
            stage = result.Stage,
            seekRung = result.SeekRung,
            sampleBytes = result.SampleBytes,
            failedCapabilities
        });
    }


    private async Task<IReadOnlyDictionary<string, string>> ReadProviderAccountSecretsAsync(
        ProviderAccountRecord account,
        CancellationToken cancellationToken)
    {
        if (!account.SecretReferenceId.HasValue)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var secretStore = HttpContext.RequestServices.GetRequiredService<EncryptedSecretStore>();
        using var lease = await secretStore.OpenAsync(
            account.SecretReferenceId.Value,
            new SecretAccessContext(
                account.OwnerUserId,
                $"provider-account:{account.ProviderId}:{account.Id:N}",
                AllowShared: account.OwnerUserId == null),
            cancellationToken);
        using var document = JsonDocument.Parse(lease.Value);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Provider account credentials must be a JSON object.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var normalizedName = new string(property.Name
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(normalizedName) && !string.IsNullOrWhiteSpace(value))
            {
                values[normalizedName] = value;
            }
        }

        return values;
    }
}
