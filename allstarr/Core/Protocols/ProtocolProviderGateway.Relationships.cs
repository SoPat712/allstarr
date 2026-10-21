using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    private async Task<List<T>> ReadKnownRelationshipsAsync<T>(ProtocolExecutionContext protocol, string provider,
        string artistId, string relationship)
    {
        if (applicationCache?.IsEnabled != true ||
            (await ResolveEffectivePolicyAsync(protocol))?.IsEnabled(provider) == false) return [];
        try
        {
            var payload = await applicationCache.GetStringAsync(RelationshipCacheKey(protocol, provider, artistId, relationship));
            if (payload == null) return [];
            var stored = JsonSerializer.Deserialize<KnownRelationships<T>>(payload);
            if (stored == null) return [];
            if (stored.AccountId.HasValue)
            {
                var routed = await PlanExactAsync<IProviderMetadataCapability>(protocol, provider,
                    ProviderCapabilityKind.Metadata, "protocol-metadata-read-relationships");
                if (routed.Candidate?.Context.Account is not { } account || account.AccountId != stored.AccountId ||
                    account.Revision != stored.AccountRevision) return [];
            }
            protocol.CancellationToken.ThrowIfCancellationRequested();
            return stored.Items;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger?.LogDebug("Could not read known artist relationships ({ErrorType})", error.GetType().Name);
            return [];
        }
    }

    private async Task CacheKnownRelationshipsAsync<T>(ProtocolExecutionContext protocol, ProviderExecutionContext source,
        string artistId, string relationship, List<T> items)
    {
        if (applicationCache?.IsEnabled != true) return;
        try
        {
            await applicationCache.SetStringAsync(RelationshipCacheKey(protocol, source.ProviderId, artistId, relationship),
                JsonSerializer.Serialize(new KnownRelationships<T>(items, source.Account?.AccountId, source.Account?.Revision)),
                TimeSpan.FromMinutes(5));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger?.LogDebug("Could not cache artist relationships ({ErrorType})", error.GetType().Name);
        }
    }

    private static string RelationshipCacheKey(ProtocolExecutionContext context, string provider, string artistId, string relationship) =>
        CacheKeyBuilder.BuildProtocolMetadataKey(provider,
            JsonSerializer.Serialize(new { context.Actor, context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId }),
            $"artist:{relationship}", artistId);

    private sealed record KnownRelationships<T>(List<T> Items, Guid? AccountId, long? AccountRevision);
}
