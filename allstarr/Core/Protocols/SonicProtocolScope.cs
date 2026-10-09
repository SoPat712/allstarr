using System.Text.Json;
using allstarr.Core.Intelligence;

namespace allstarr.Core.Protocols;

internal static class SonicProtocolScope
{
    public static async Task<IntelligenceScope?> ResolveAsync(
        ProtocolExecutionContext context,
        IIntelligencePolicyService? policies,
        CancellationToken cancellationToken)
    {
        if (!context.CanRunUserScopedWork || policies == null)
            return null;

        var actor = context.RequireActor();
        if (actor.EffectiveUserId is not { } owner) return null;
        var scope = new IntelligenceScope(owner,
            context.Protocol.ToString().ToLowerInvariant(), context.BackendInstanceId);
        var policy = await policies.GetAsync(scope, cancellationToken);
        if (policy?.Enabled != true) return null;

        try
        {
            var enabled = JsonSerializer.Deserialize<string[]>(policy.EnabledProvidersJson) ?? [];
            return enabled.Contains("audiomuse-ai", StringComparer.Ordinal) ? scope : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
