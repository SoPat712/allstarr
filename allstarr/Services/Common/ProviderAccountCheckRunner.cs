using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using allstarr.Core.Protocols;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Common;

public interface IProviderCheckSessions
{
    Task<bool> IsSignedInAsync(string provider, Guid? accountId, CancellationToken token);
}

public sealed class ProviderCheckSessions(IDbContextFactory<AllstarrDbContext> factory,
    ExtensionRuntimeCoordinator runtime) : IProviderCheckSessions
{
    public async Task<bool> IsSignedInAsync(string provider, Guid? accountId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var package = await db.ExtensionPackages.AsNoTracking().SingleOrDefaultAsync(row =>
            row.ExtensionId == provider && row.State == ExtensionPackageState.Active, token);
        return package != null && (await runtime.SignedSessionStatusAsync(package.Id, token, accountId)).State == ExtensionSessionStates.SignedIn;
    }
}

public sealed class ProviderAccountCheckRunner(
    IDbContextFactory<AllstarrDbContext> factory,
    ProviderStatusManager status,
    ProviderCheckHistory history,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<ProviderAccountCheckRunner> logger,
    EncryptedSecretStore? secrets = null,
    IProviderRegistry? registry = null,
    IProviderCheckSessions? sessions = null,
    ProviderCtsDiagnosticRunner? media = null) : IProviderCheckRunner
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeSpan pause = TimeSpan.FromMilliseconds(Math.Clamp(configuration.GetValue("ProviderChecks:PauseMilliseconds", 500), 0, 10000));
    private readonly TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("ProviderChecks:TimeoutSeconds", 45), 5, 300));

    public async Task RestoreAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var rows = await db.ProviderAccounts.AsNoTracking().Where(row => row.Enabled).ToListAsync(token);
        var accounts = rows.Where(row => !ProviderAccountSettings.IsRemoved(row)).ToDictionary(row => row.Id);
        foreach (var result in await history.ReadLatestAsync(token))
        {
            if (result.Key.ProviderAccountId is { } id &&
                (!accounts.TryGetValue(id, out var account) || account.Revision != result.Revision || account.ProviderId != result.Key.Provider)) continue;
            status.RestoreCheckResult(result.Key, result.Revision, result.Status);
        }
    }

    public async Task<bool> SweepAsync(CancellationToken token, string? providerId = null)
    {
        // A requested single-source check waits for a running sweep; a scheduled sweep never overlaps one.
        if (providerId != null) await gate.WaitAsync(token);
        else if (!await gate.WaitAsync(0, token)) return true;
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var accounts = await db.ProviderAccounts.AsNoTracking()
                .Where(row => row.Enabled && (providerId == null || row.ProviderId == providerId))
                .OrderBy(row => row.ProviderId).ThenBy(row => row.Id).ToArrayAsync(token);
            var targets = accounts.Where(account => !ProviderAccountSettings.IsRemoved(account))
                .SelectMany(account => status.GetAllManagedStatuses(account.ProviderId, account.Id,
                    new Dictionary<string, string>(), account.Revision)
                .Select(item => (Status: item, Account: (ProviderAccountRecord?)account)))
                .Concat(status.GetAllAccountFreeStatuses()
                    .Where(item => providerId == null || string.Equals(item.Provider, providerId, StringComparison.OrdinalIgnoreCase))
                    .Select(item => (Status: item, Account: (ProviderAccountRecord?)null)))
                .Where(item => item.Status.IsEnabled && status.CanTestCapability(item.Status.Provider, item.Status.Capability)).ToArray();
            var success = true;
            var correlation = Guid.NewGuid().ToString("N");
            for (var index = 0; index < targets.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                if (index > 0) await Task.Delay(pause, time, token);
                var target = targets[index];
                var account = target.Account;
                if (account != null)
                {
                    var current = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(row => row.Id == account.Id &&
                        row.Enabled && row.Revision == account.Revision, token);
                    if (current == null || ProviderAccountSettings.IsRemoved(current)) continue;
                }
                var key = account == null ? ProviderRuntimeStatusKey.CreateAccountFree(target.Status.Provider, target.Status.Capability) :
                    ProviderRuntimeStatusKey.CreateManaged(target.Status.Provider, target.Status.Capability, account.Id);
                ProviderCheckResult result;
                var downloadBacked = key.Capability == ProviderCapabilities.Streaming && media != null &&
                    registry?.TryGetCapability<IProviderStreamingCapability>(key.Provider, ProviderCapabilityKind.Streaming, out var streaming) == true &&
                    streaming is IProviderDownloadStreamingCapability;
                var checkTimeout = timeout;
                if (downloadBacked)
                {
                    var options = new ProtocolExecutionOptions();
                    configuration.GetSection(ProtocolExecutionOptions.SectionName).Bind(options);
                    checkTimeout = options.GetDownloadDiagnosticTimeout();
                }
                using var deadline = new CancellationTokenSource(checkTimeout, time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                try
                {
                    var check = CheckAsync(key, account, target.Status, correlation, linked.Token);
                    result = downloadBacked ? await check.WaitAsync(linked.Token) : await check;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    result = new(key, account?.Revision, target.Status with
                    {
                        Health = ProviderHealthState.Degraded,
                        TestedAt = time.GetUtcNow(),
                        ReasonCode = exception is OperationCanceledException ? "timeout" : "unreachable"
                    });
                }
                await history.RecordAsync(result, correlation, token, target.Status.Health);
                status.RestoreCheckResult(key, result.Revision, result.Status);
                if (result.Status.Health != ProviderHealthState.Healthy)
                {
                    success = false;
                    logger.LogWarning("Provider check failed for {Provider}/{Capability}, account {ProviderAccountId}, health {Health}",
                        key.Provider, key.Capability, key.ProviderAccountId, result.Status.Health);
                }
            }
            return success;
        }
        finally { gate.Release(); }
    }

    private async Task<ProviderCheckResult> CheckAsync(ProviderRuntimeStatusKey key, ProviderAccountRecord? account,
        ProviderRuntimeStatus baseline, string correlation, CancellationToken token)
    {
        if (registry?.TryGet(key.Provider, out var descriptor) == true && descriptor!.UsesSignedSession &&
            (sessions == null || !await sessions.IsSignedInAsync(key.Provider, account?.Id, token)))
            return new(key, account?.Revision, baseline with
            {
                Health = ProviderHealthState.Degraded,
                TestedAt = time.GetUtcNow(),
                ReasonCode = "sign_in_needed"
            });
        var values = await ReadSecretsAsync(account, token);
        if (account != null)
            baseline = status.GetManagedStatus(key.Provider, key.Capability, account.Id, values, account.Revision);
        if (key.Capability == ProviderCapabilities.Streaming && media != null)
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var administrator = await db.Users.AsNoTracking().Where(row => row.Enabled && row.IsAdmin)
                .OrderBy(row => row.Id).FirstOrDefaultAsync(token);
            if (administrator == null)
                return new(key, account?.Revision, baseline with
                {
                    Health = ProviderHealthState.Degraded,
                    TestedAt = time.GetUtcNow(),
                    ReasonCode = "administrator_required"
                });
            var actor = new ProviderActorContext(ProviderActorKind.Administrator, administrator.Id,
                new ProviderBackendPrincipal(administrator.BackendType, administrator.BackendInstanceId, administrator.BackendPrincipalId));
            var measured = await media.MeasureAsync(actor, key.Provider, account?.Id, ProviderAudioQuality.HighResolution,
                correlation, cancellationToken: token, householdCheck: true);
            return new(key, account?.Revision, baseline with
            {
                Health = measured.Succeeded ? ProviderHealthState.Healthy : ProviderHealthState.Degraded,
                TestedAt = time.GetUtcNow(),
                LatencyMilliseconds = (long?)measured.ClickToStreamMilliseconds,
                ReasonCode = measured.ReasonCode
            }, measured.SeekRung);
        }
        var tested = account == null
            ? await status.TestAccountFreeProviderCapabilityAsync(key.Provider, key.Capability, token)
            : await status.TestManagedProviderCapabilityAsync(key.Provider, key.Capability, account.Id, values, token, account.Revision);
        return new(key, account?.Revision, tested with { TestedAt = tested.TestedAt ?? time.GetUtcNow() });
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadSecretsAsync(ProviderAccountRecord? account, CancellationToken token)
    {
        if (account?.SecretReferenceId is not { } secretId) return new Dictionary<string, string>();
        if (secrets == null) throw new InvalidOperationException("Provider credentials are unavailable.");
        using var lease = await secrets.OpenAsync(secretId,
            new SecretAccessContext(account.OwnerUserId, $"provider-account:{account.ProviderId}:{account.Id:N}", AllowShared: account.OwnerUserId == null), token);
        using var document = JsonDocument.Parse(lease.Value);
        return document.RootElement.EnumerateObject().Where(item => item.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            .ToDictionary(item => new string(item.Name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()),
                item => item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString()! : item.Value.GetRawText());
    }
}
