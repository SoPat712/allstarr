using System.Text.Json;
using allstarr.Core.Jobs;

namespace allstarr.Core.Extensions;

internal sealed record ExtensionSessionVerificationRequest(Guid PackageId, long PackageRevision, string ExpiresAt);

public sealed class ExtensionSessionVerificationJobHandler(ExtensionRuntimeCoordinator runtime) : IDurableJobHandler
{
    public const string Type = "extension.session.verify";
    public string JobType => Type;

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken)
    {
        var request = context.Claim.Payload.Deserialize<ExtensionSessionVerificationRequest>();
        if (request == null || request.PackageId == Guid.Empty || request.PackageRevision < 1 ||
            !DateTimeOffset.TryParse(request.ExpiresAt, out _) || context.Claim.ProviderAccountId == null)
            return DurableJobCompletion.Failure("session_context_invalid", "The saved sign-in context is invalid.");
        try { return await runtime.VerifyExpiredSignedSessionAsync(context.Claim, request, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return DurableJobCompletion.Retry("session_verification_unavailable",
                "Automatic sign-in verification is unavailable. Reconnect remains available in Integrations.", TimeSpan.FromMinutes(5));
        }
    }
}
