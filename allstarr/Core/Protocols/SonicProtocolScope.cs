using System.Text.Json;
using allstarr.Core.Intelligence;

namespace allstarr.Core.Protocols;

internal static class SonicProtocolScope
{
    public static async Task<IntelligenceScope?> ResolveAsync(
        ProtocolExecutionContext context,
        string itemId,
        IProtocolLibraryScopeResolver? libraryScopes,
        IIntelligencePolicyService? policies,
        CancellationToken cancellationToken)
    {
        if (!context.CanRunUserScopedWork || libraryScopes == null || policies == null)
            return null;

        ProtocolExecutionContext resolved;
        try
        {
            resolved = await libraryScopes.ResolveAsync(context, itemId, cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                           UnauthorizedAccessException or
                                           InvalidOperationException)
        {
            return null;
        }

        var actor = resolved.RequireActor();
        if (actor.EffectiveUserId is not { } owner ||
            string.IsNullOrWhiteSpace(resolved.LibraryScopeId))
            return null;

        var scope = new IntelligenceScope(
            actor.TenantId,
            owner,
            resolved.Protocol.ToString().ToLowerInvariant(),
            resolved.BackendInstanceId,
            resolved.LibraryScopeId);
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
