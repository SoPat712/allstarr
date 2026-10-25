using allstarr.Core.Settings;
using allstarr.Core.Routing;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Providers.Lyrics;
using allstarr.Services.Common;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway : IProtocolLyricsExecutionGateway
{
    async ValueTask<PreparedLyricsRead?> IProtocolLyricsExecutionGateway.PrepareAsync(LyricsReadSpec spec, CancellationToken token)
    {
        var candidate = await PlanAutomaticLyricsAsync(spec, token);
        if (candidate == null || candidate.TrackId == null && spec.SourceTrackId.ProviderId == "allstarr") return null;
        var source = await CaptureLyricsSourceAsync(candidate.Context.ProviderId, token);
        var binding = LyricsBinding(candidate, spec);
        var allowed = source.DependenciesReady && source.Speculation != LyricsSpeculationMode.ForegroundOnly &&
            (source.Speculation == LyricsSpeculationMode.Unrestricted || HasLyricsHealth(candidate, source));
        return new(spec, binding, source, allowed);
    }

    async Task<ProviderLyricsResult?> IProtocolLyricsExecutionGateway.ExecuteAsync(PreparedLyricsRead prepared,
        LyricsCompletionMode completion, CancellationToken token)
    {
        var candidate = await CurrentLyricsCandidateAsync(prepared, token);
        if (candidate == null) return null;
        var request = prepared.Spec.Request;
        request = new(request.CanonicalRecordingId, candidate.TrackId ?? request.ProviderTrackId, request.AvailabilityOnly,
            request.PreferredFormat, request.TrackTitle, request.ArtistNames, request.AlbumTitle, request.DurationSeconds, request.Isrc);
        var outcome = candidate.Implementation is IInternalLyricsCapability capability
            ? await capability.FetchAsync(candidate.Context, request, new(completion, prepared.Source))
            : await candidate.Implementation.FetchLyricsAsync(candidate.Context, request);
        token.ThrowIfCancellationRequested();
        if (!outcome.IsSuccess || outcome.RequireValue().Availability != ProviderLyricsAvailabilityState.Available) return null;
        if (await CurrentLyricsCandidateAsync(prepared, token) == null) return null;
        return outcome.RequireValue();
    }

    async ValueTask<bool> IProtocolLyricsExecutionGateway.IsCurrentAsync(PreparedLyricsRead prepared, CancellationToken token) =>
        await CurrentLyricsCandidateAsync(prepared, token) != null;

    private async Task<ProviderRouteCandidate<IProviderLyricsCapability>?> CurrentLyricsCandidateAsync(
        PreparedLyricsRead prepared, CancellationToken token)
    {
        var candidate = await PlanAutomaticLyricsAsync(prepared.Spec, token);
        if (candidate == null || LyricsBinding(candidate, prepared.Spec) != prepared.Binding) return null;
        LyricsSourceSnapshot source;
        try { source = await CaptureLyricsSourceAsync(candidate.Context.ProviderId, token); }
        catch (Exception exception) when (exception is not (OperationCanceledException or UnauthorizedAccessException)) { return null; }
        if (source.ConfigurationId != prepared.Source.ConfigurationId) return null;
        if (prepared.MaySpeculate && (!source.DependenciesReady ||
            source.Speculation == LyricsSpeculationMode.RecentHealthy && !HasLyricsHealth(candidate, source))) return null;
        return candidate;
    }

    private async ValueTask<LyricsSourceSnapshot> CaptureLyricsSourceAsync(string provider, CancellationToken token) =>
        registry.TryGetCapability<IProviderLyricsCapability>(provider, ProviderCapabilityKind.Lyrics, out var capability) &&
        capability is IInternalLyricsCapability source ? await source.CaptureSourceAsync(token) :
            LyricsAwait.Static(provider, LyricsSpeculationMode.ForegroundOnly);

    private bool HasLyricsHealth(ProviderRouteCandidate<IProviderLyricsCapability> candidate, LyricsSourceSnapshot source)
    {
        if (outcomes is not ProviderRuntimeHealth health) return false;
        var context = candidate.Context;
        var key = context.Account is { } account
            ? ProviderRuntimeStatusKey.CreateManaged(context.ProviderId, ProviderCapabilities.Lyrics, account.AccountId)
            : ProviderRuntimeStatusKey.CreateAccountFree(context.ProviderId, ProviderCapabilities.Lyrics);
        var observation = health.GetLyrics(key, context.Account?.Revision, source.ConfigurationId);
        return observation is { Health: ProviderHealthState.Healthy, ConsecutiveFailures: 0 } && !health.IsCircuitOpen(observation);
    }

    private async Task<ProviderRouteCandidate<IProviderLyricsCapability>?> PlanAutomaticLyricsAsync(LyricsReadSpec spec, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if ((timeProvider ?? TimeProvider.System).GetUtcNow() >= spec.Deadline) return null;
        var provider = registry.ResolveProviderId(NormalizeProvider(spec.ProviderId));
        EffectiveProviderPolicySnapshot? policy = null;
        if (effectivePolicies != null)
        {
            policy = !spec.HouseholdOnly && spec.Actor.EffectiveUserId is { } user
                ? await effectivePolicies.ResolveForUserAsync(user, token) : await effectivePolicies.ResolveAsync(token);
            if (!policy.ApplyAutomaticProviderAvailability(ProviderCapabilityKind.Lyrics, [provider]).Contains(provider, StringComparer.Ordinal)) return null;
        }
        if (!IsAutomaticProvider(provider, policy)) return null;
        var request = Request(spec.Actor, ProviderCapabilityKind.Lyrics,
            "protocol-lyrics-auto", [provider], spec.SourceTrackId, correlationId: spec.CorrelationId, deadline: spec.Deadline,
            cancellationToken: token, allowedAccountScopes: spec.AllowedAccountScopes, purpose: ProviderRoutePurpose.Automatic);
        if (spec.HouseholdOnly && (spec.AllowedAccountScopes.Count != 1 || spec.AllowedAccountScopes[0] != Core.Storage.ProviderAccountScope.Shared)) return null;
        var plan = spec.HouseholdOnly && router is IHouseholdLyricsRoutePlanner household
            ? await household.PlanHouseholdLyricsAsync(request) : await router.PlanAsync<IProviderLyricsCapability>(request);
        return plan.Candidates.SingleOrDefault();
    }

    private static LyricsRouteBinding LyricsBinding(ProviderRouteCandidate<IProviderLyricsCapability> candidate, LyricsReadSpec spec) =>
        new(candidate.Context.ProviderId, candidate.TrackId ?? spec.Request.ProviderTrackId,
            candidate.Context.Account?.AccountId, candidate.Context.Account?.Revision);
}
