using System.Security.Cryptography;
using System.Text;
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
        var expiryKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expiresAt)))[..16];
        await _sessionJobs!.EnqueueAsync(new DurableJobEnqueueRequest<ExtensionSessionVerificationRequest>(
            ExtensionSessionVerificationJobHandler.Type, $"{package.Id:N}:{package.Revision}:{account.Id:N}:{account.Revision}:{expiryKey}",
            new(package.Id, package.Revision, expiresAt), account.OwnerUserId, MaxAttempts: 3,
            ProviderAccountId: account.Id), cancellationToken);
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
        {
            if (!await CurrentAsync(cancellationToken)) return DurableJobCompletion.Cancelled();
            if (await prepared.Client.AutomaticVerificationExpiryAsync(cancellationToken) == request.ExpiresAt)
                return DurableJobCompletion.Retry("session_bootstrap_unavailable", "The sign-in service could not prepare verification.", TimeSpan.FromMinutes(5));
            var current = await prepared.Client.ViewAsync(cancellationToken);
            if (current.State == ExtensionSessionStates.SignedIn)
            {
                await RequestChecksIfSignedInAsync(request.PackageId, current, cancellationToken);
                return DurableJobCompletion.Success();
            }
            return DurableJobCompletion.Cancelled();
        }

        try
        {
            if (!ExtensionSessionVerificationClient.Supports(prepared.Challenge.VerificationUrl))
                return DurableJobCompletion.Failure("session_verification_unsupported", "Use Reconnect to verify this service manually.");
            var callback = await _sessionVerification.VerifyAsync(prepared.Challenge, cancellationToken);
            return await WithRuntimeGateAsync(async () =>
            {
                if (!await CurrentAsync(cancellationToken)) return DurableJobCompletion.Cancelled();
                var view = await prepared.Client.CompleteAutomaticVerificationAsync(prepared.Challenge, callback, CurrentAsync, cancellationToken);
                if (!view.Restored) return view.ReasonCode == "context_changed"
                    ? DurableJobCompletion.Cancelled()
                    : DurableJobCompletion.Retry("session_verification_incomplete", "Sign-in verification did not complete. Reconnect remains available.", TimeSpan.FromMinutes(5));
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
