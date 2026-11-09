using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Extensions;

public sealed partial class ExtensionRuntimeCoordinator
{
    private async Task EnqueueSessionVerificationAsync(ExtensionPackageRecord package, ProviderAccountRecord account,
        ExtensionSignedSessionClient client, CancellationToken cancellationToken)
    {
        if (!client.SupportsAutomaticVerification) return;
        var expiresAt = await client.AutomaticVerificationExpiryAsync(cancellationToken);
        if (expiresAt == null) return;
        var prefix = $"{package.Id:N}:";
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        if (await db.Jobs.AnyAsync(item => item.Type == ExtensionSessionVerificationJobHandler.Type &&
                item.ProviderAccountId == account.Id && item.IdempotencyKey.StartsWith(prefix) &&
                (item.State == DurableJobState.Pending || item.State == DurableJobState.Running ||
                    item.State == DurableJobState.RetryScheduled), cancellationToken)) return;
        var history = await db.Jobs.AsNoTracking().Where(item => item.Type == ExtensionSessionVerificationJobHandler.Type &&
                item.ProviderAccountId == account.Id && item.IdempotencyKey.StartsWith(prefix) &&
                (item.State == DurableJobState.Failed || item.State == DurableJobState.Succeeded))
            .OrderByDescending(item => item.CompletedAt).ThenByDescending(item => item.CreatedAt)
            .Select(item => new { item.State, item.CompletedAt, item.UpdatedAt, item.PayloadJson }).ToListAsync(cancellationToken);
        var failures = history.Where(item => JsonSerializer.Deserialize<ExtensionSessionVerificationRequest>(item.PayloadJson)?.ExpiresAt == expiresAt)
            .TakeWhile(item => item.State == DurableJobState.Failed).ToArray();
        DateTimeOffset? availableAt = failures.Length == 0 ? null :
            (failures[0].CompletedAt ?? failures[0].UpdatedAt).AddHours(Math.Min(24, Math.Pow(2, Math.Min(failures.Length - 1, 5))));
        await _sessionJobs!.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<ExtensionSessionVerificationRequest>(
            ExtensionSessionVerificationJobHandler.Type, $"{package.Id:N}:{account.Id:N}:{Guid.NewGuid():N}",
            new(package.Id, package.Revision, expiresAt), account.OwnerUserId, MaxAttempts: 3,
            AvailableAt: availableAt, ProviderAccountId: account.Id), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task<DurableJobCompletion> VerifyExpiredSignedSessionAsync(DurableJobClaim claim,
        ExtensionSessionVerificationRequest request, CancellationToken cancellationToken)
    {
        if (_sessionVerification?.Enabled != true || _sessionJobs == null)
            return DurableJobCompletion.Failure("session_verification_disabled", "Automatic sign-in verification is disabled.");
        if (!_sandboxes.ContainsKey(request.PackageId))
        {
            var package = await GetPackageAsync(request.PackageId, cancellationToken);
            return package.State == ExtensionPackageState.Active && package.Revision == request.PackageRevision
                ? DurableJobCompletion.Defer("session_runtime_starting", "Waiting for the extension runtime to start.", TimeSpan.FromMinutes(1))
                : DurableJobCompletion.Cancelled();
        }
        var authorizer = new DurableJobContextAuthorizer(_factory);
        ExtensionSandbox? expectedSandbox = null;
        async Task<bool> CurrentAsync(CancellationToken token)
        {
            if (!await _sessionJobs.IsExecutionCurrentAsync(claim, token) ||
                !(await authorizer.ReauthorizeAsync(claim, token)).Authorized) return false;
            var package = await GetPackageAsync(request.PackageId, token);
            await using var db = await _factory.CreateDbContextAsync(token);
            var providerMatches = await db.ProviderAccounts.AsNoTracking().AnyAsync(item =>
                item.Id == claim.ProviderAccountId && item.ProviderId == package.ExtensionId, token);
            return package.State == ExtensionPackageState.Active && package.Revision == request.PackageRevision &&
                providerMatches && _sandboxes.TryGetValue(package.Id, out var sandbox) &&
                (expectedSandbox == null || ReferenceEquals(sandbox, expectedSandbox));
        }

        var prepared = await WithRuntimeGateAsync(async () =>
        {
            if (!await CurrentAsync(cancellationToken)) return (Client: (ExtensionSignedSessionClient?)null, Challenge: (ExtensionSessionChallenge?)null);
            var sandbox = RequireSandbox(request.PackageId);
            expectedSandbox = sandbox;
            using var scope = sandbox.OpenSignedSessionAccount(claim.ProviderAccountId!.Value);
            var client = sandbox.SignedSessionClient;
            return (Client: client, Challenge: await client.PrepareAutomaticVerificationAsync(request.ExpiresAt, cancellationToken, CurrentAsync));
        }, cancellationToken);
        if (prepared.Client == null) return DurableJobCompletion.Cancelled();
        if (prepared.Challenge == null)
            return await WithRuntimeGateAsync(async () =>
            {
                if (!await CurrentAsync(cancellationToken)) return DurableJobCompletion.Cancelled();
                if (await prepared.Client.AutomaticVerificationExpiryAsync(cancellationToken) == request.ExpiresAt)
                    return DurableJobCompletion.Retry("session_bootstrap_unavailable", "The sign-in service could not prepare verification.", TimeSpan.FromMinutes(5));
                var current = await prepared.Client.ViewAsync(cancellationToken);
                if (current.State == ExtensionSessionStates.SignedIn && current.ReasonCode == null)
                {
                    current = await prepared.Client.ConfirmAutomaticSessionAsync(CurrentAsync, cancellationToken);
                    if (!current.Restored) return current.ReasonCode == "context_changed"
                        ? DurableJobCompletion.Cancelled()
                        : DurableJobCompletion.Retry("session_verification_unusable", "The sign-in service did not accept the verified session.", TimeSpan.FromMinutes(5));
                    await RequestChecksIfSignedInAsync(request.PackageId, current, cancellationToken);
                    return DurableJobCompletion.Success();
                }
                return DurableJobCompletion.Cancelled();
            }, cancellationToken);

        try
        {
            if (!ExtensionSessionVerificationClient.Supports(prepared.Challenge.VerificationUrl))
                return DurableJobCompletion.Failure("session_verification_unsupported", "Use Reconnect to verify this service manually.");
            var callback = await _sessionVerification.VerifyAsync(prepared.Challenge, cancellationToken);
            return await WithRuntimeGateAsync(async () =>
            {
                if (!await CurrentAsync(cancellationToken)) return DurableJobCompletion.Cancelled();
                var view = await prepared.Client.CompleteAutomaticVerificationAsync(prepared.Challenge, callback, CurrentAsync, cancellationToken);
                if (!view.Restored || view.State != ExtensionSessionStates.SignedIn) return view.ReasonCode == "context_changed"
                    ? DurableJobCompletion.Cancelled()
                    : DurableJobCompletion.Retry("session_verification_incomplete", "Sign-in verification did not complete. Reconnect remains available.", TimeSpan.FromMinutes(5));
                view = await prepared.Client.ConfirmAutomaticSessionAsync(CurrentAsync, cancellationToken);
                if (!view.Restored) return view.ReasonCode == "context_changed"
                    ? DurableJobCompletion.Cancelled()
                    : DurableJobCompletion.Retry("session_verification_unusable", "The sign-in service did not accept the verified session.", TimeSpan.FromMinutes(5));
                await RequestChecksIfSignedInAsync(request.PackageId, view, cancellationToken);
                var (level, code, message) = RenewalLog(view);
                await _controlPlane.WriteLogAsync(request.PackageId, level, code, message, "extension-session", cancellationToken);
                return DurableJobCompletion.Success();
            }, cancellationToken);
        }
        finally
        {
            // Release only this attempt; a concurrent manual reconnect owns its own verification link.
            await prepared.Client.ReleaseAutomaticVerificationAsync(prepared.Challenge, CancellationToken.None);
        }
    }
}
