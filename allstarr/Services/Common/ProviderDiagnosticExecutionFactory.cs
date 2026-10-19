using allstarr.Core.Capabilities;
using allstarr.Core.Routing;
using allstarr.Core.Storage;

namespace allstarr.Services.Common;

public sealed class ProviderDiagnosticExecutionFactory(
    IProviderRegistry providers,
    IProviderRouteAccountResolver accounts,
    IProviderCheckSessions? sessions = null)
{
    public async Task<ProviderDiagnosticExecution> CreateAsync(ProviderActorContext actor, string providerId,
        ProviderCapabilityKind capability, Guid? accountId, string correlationId, DateTimeOffset deadline,
        CancellationToken token)
    {
        var descriptor = providers.GetRequired(providerId);
        var selected = descriptor.Capabilities.Single(item => item.Capability == capability);
        if (accountId == Guid.Empty) accountId = null;
        if (selected.AccountRequirement == ProviderAccountRequirement.Required && !accountId.HasValue)
            return new(null, descriptor.UsesSignedSession ? 409 : 400,
                descriptor.UsesSignedSession ? "sign_in_needed" : "account_required");
        if (selected.AccountRequirement == ProviderAccountRequirement.None && accountId.HasValue)
            return new(null, 400, "account_not_used");
        ProviderRouteAccountResolution? resolved = null;
        try
        {
            if (selected.AccountRequirement != ProviderAccountRequirement.None)
                resolved = await accounts.ResolveAsync(new ProviderRouteAccountRequest(actor, providerId, capability,
                    accountId, null, AllowManagedDiagnosticAccount: actor.Kind == ProviderActorKind.Administrator && accountId.HasValue), token).WaitAsync(token);
        }
        catch (UnauthorizedAccessException) { return new(null, 403, "account_forbidden"); }
        if (descriptor.UsesSignedSession && (resolved == null || sessions == null ||
            !await sessions.IsSignedInAsync(providerId, resolved.Account.AccountId, token).WaitAsync(token)))
            return new(null, 409, "sign_in_needed");
        if (selected.AccountRequirement == ProviderAccountRequirement.Required && resolved == null ||
            accountId.HasValue && resolved?.Account.AccountId != accountId)
            return new(null, 404, "account_unavailable");
        var executionActor = actor;
        if (actor.Kind == ProviderActorKind.Administrator && resolved?.Account.Scope == ProviderAccountScope.Personal &&
            resolved.Account.OwnerUserId is { } owner && owner != actor.EffectiveUserId)
            executionActor = new(actor.Kind, actor.UserId, actor.BackendPrincipal, actor.DurableJobId, owner);
        var policy = new ProviderExecutionPolicy(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
            ProviderExplicitContentPolicy.Allow, allowFallback: false, allowSharedAccount: true,
            allowManagedDownloads: true, [providerId]);
        return new(new ProviderExecutionContext(executionActor, providerId, resolved?.Account, policy,
            "provider-diagnostic", ProviderContractValidation.RequiredText(correlationId, nameof(correlationId), 100), deadline, token), 200, null);
    }
}

public sealed record ProviderDiagnosticExecution(ProviderExecutionContext? Context, int StatusCode, string? ReasonCode);
