using allstarr.Core.Capabilities;
using allstarr.Core.Music;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    public async Task<string?> GetStreamingProviderAsync(ProtocolExecutionContext protocol, string providerId, string externalId)
    {
        if (protocol.Actor == null) return null;
        protocol = protocol with { IsReadOnly = true };
        var policy = await ResolveEffectivePolicyAsync(protocol);
        providerId = NormalizeProvider(providerId);
        if (providerId != "allstarr" && policy?.IsEnabled(providerId) == false) return null;
        var track = new ProviderExternalResourceId(providerId, ProviderResourceKind.Track, externalId);
        if (!await AllowsCleanPlaybackAsync(protocol, track, storedOnly: true)) return null;
        var quality = policy == null ? ProviderAudioQuality.Any : AudioQualityPolicy.RequestedQuality(policy.AudioQuality);
        var order = ResolveProviderOrder(ProviderCapabilityKind.Streaming, policy);
        if (providerId != "allstarr") order = order.Append(providerId).Distinct(StringComparer.Ordinal).ToArray();
        var plan = await router.PlanAsync<IProviderStreamingCapability>(Request(protocol, protocol.RequireActor(),
            ProviderCapabilityKind.Streaming, "protocol-stream-open", order, track, quality,
            allowFallback: true, allowManagedDownloads: true));
        var candidates = plan.Candidates.Where(candidate => IsAutomaticProvider(candidate.Provider.Id, policy))
            .DistinctBy(candidate => (candidate.Provider.Id, candidate.Context.Account?.AccountId, (candidate.TrackId ?? track).Value)).Take(32).ToArray();
        // A list asks this for every row at once, so each row reads its sources a couple at a time.
        var (_, tried) = await OrderStartAsync(protocol, candidates, track, quality, policy?.Preferences.PlaybackStart ?? "Balanced", sideBySide: 2);
        return tried.Length == 0 ? null : candidates[tried[0]].Provider.Id;
    }
}
