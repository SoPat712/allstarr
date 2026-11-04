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
        var candidates = plan.Candidates.Where(candidate => IsAutomaticProvider(candidate.Provider.Id, policy)).Take(32).ToArray();
        var facts = new List<PlaybackStartCandidate>();
        for (var index = 0; index < candidates.Length; index++)
            if (await ReadPlaybackStartCandidateAsync(protocol, candidates[index], track, quality, index) is { } fact)
                facts.Add(fact);
        var selected = PlaybackStartPolicy.Order(policy?.Preferences.PlaybackStart ?? "Balanced", facts).FirstOrDefault(-1);
        return selected < 0 ? null : candidates[selected].Provider.Id;
    }
}
