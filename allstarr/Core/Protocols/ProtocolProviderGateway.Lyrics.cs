using allstarr.Core.Settings;
using allstarr.Core.Routing;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Providers.Lyrics;
using allstarr.Services.Common;
using allstarr.Core.Matching;
using System.Collections.Concurrent;
using System.Text.Json;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway : IProtocolLyricsExecutionGateway
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lyricsMisses = new(StringComparer.Ordinal);

    async ValueTask<PreparedLyricsRead?> IProtocolLyricsExecutionGateway.PrepareAsync(LyricsReadSpec spec, CancellationToken token)
    {
        var candidate = await PlanAutomaticLyricsAsync(spec, token, cached: true);
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
        var cached = completion == LyricsCompletionMode.CacheOnly;
        var candidate = await CurrentLyricsCandidateAsync(prepared, token, cached);
        if (candidate == null) return null;
        var request = prepared.Spec.Request;
        request = new(request.CanonicalRecordingId, candidate.TrackId ?? request.ProviderTrackId, request.AvailabilityOnly,
            request.PreferredFormat, request.TrackTitle, request.ArtistNames, request.AlbumTitle, request.DurationSeconds, request.Isrc)
        { Refresh = request.Refresh };
        if (completion == LyricsCompletionMode.CacheOnly && candidate.Implementation is not IInternalLyricsCapability) return null;
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var key = CanonicalCatalogKeys.Hash(JsonSerializer.Serialize(new
        { prepared.Spec.Actor, prepared.Binding, prepared.Source.ConfigurationId, Request = request }));
        if (!cached && !request.Refresh && _lyricsMisses.TryGetValue(key, out var expires) && expires > now) return null;
        if (!cached && !request.Refresh && outcomes is ProviderRuntimeHealth health)
        {
            var statusKey = candidate.Context.Account is { } account
                ? ProviderRuntimeStatusKey.CreateManaged(candidate.Context.ProviderId, ProviderCapabilities.Lyrics, account.AccountId)
                : ProviderRuntimeStatusKey.CreateAccountFree(candidate.Context.ProviderId, ProviderCapabilities.Lyrics);
            var failure = health.GetLyrics(statusKey, candidate.Context.Account?.Revision, prepared.Source.ConfigurationId);
            if (failure is { ConsecutiveFailures: > 0 } && now - failure.ObservedAt < TimeSpan.FromMinutes(2)) return null;
        }
        var outcome = await (candidate.Implementation is IInternalLyricsCapability capability
            ? capability.FetchAsync(candidate.Context, request, new(completion, prepared.Source, prepared.Spec.Snapshot))
            : candidate.Implementation.FetchLyricsAsync(candidate.Context, request)).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        if (!outcome.IsSuccess) return null;
        if (await CurrentLyricsCandidateAsync(prepared, token, cached) == null) return null;
        var result = outcome.RequireValue();
        if (result.Availability == ProviderLyricsAvailabilityState.Available) _lyricsMisses.TryRemove(key, out _);
        if (!cached && result.Availability == ProviderLyricsAvailabilityState.Unavailable)
        {
            if (_lyricsMisses.Count >= 512) _lyricsMisses.Clear();
            _lyricsMisses[key] = now.AddHours(6);
        }
        if (result.Availability != ProviderLyricsAvailabilityState.Available) return null;
        if (completion == LyricsCompletionMode.OwnedAutomatic && prepared.Spec.Actor.EffectiveUserId is { } user)
            lyricsChanges?.RecordingChanged(user, request.CanonicalRecordingId);
        return result;
    }

    async ValueTask<bool> IProtocolLyricsExecutionGateway.IsCurrentAsync(PreparedLyricsRead prepared, CancellationToken token) =>
        await CurrentLyricsCandidateAsync(prepared, token, cached: true) != null;

    private async Task<ProviderRouteCandidate<IProviderLyricsCapability>?> CurrentLyricsCandidateAsync(
        PreparedLyricsRead prepared, CancellationToken token, bool cached = false)
    {
        var candidate = await PlanAutomaticLyricsAsync(prepared.Spec, token, cached);
        if (candidate == null || LyricsBinding(candidate, prepared.Spec) != prepared.Binding) return null;
        LyricsSourceSnapshot source;
        try { source = await CaptureLyricsSourceAsync(candidate.Context.ProviderId, token); }
        catch (Exception exception) when (exception is not (OperationCanceledException or UnauthorizedAccessException)) { return null; }
        if (source.ConfigurationId != prepared.Source.ConfigurationId) return null;
        if (!cached && !source.DependenciesReady) return null;
        if (!cached && prepared.MaySpeculate && (!source.DependenciesReady ||
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

    private async Task<ProviderRouteCandidate<IProviderLyricsCapability>?> PlanAutomaticLyricsAsync(LyricsReadSpec spec, CancellationToken token, bool cached = false)
    {
        token.ThrowIfCancellationRequested();
        if ((timeProvider ?? TimeProvider.System).GetUtcNow() >= spec.Deadline) return null;
        var provider = registry.ResolveProviderId(NormalizeProvider(spec.ProviderId));
        EffectiveProviderPolicySnapshot? policy = null;
        if (effectivePolicies != null)
        {
            policy = !spec.HouseholdOnly && spec.Actor.EffectiveUserId is { } user
                ? await effectivePolicies.ResolveForUserAsync(user, token) : await effectivePolicies.ResolveAsync(token);
            var available = spec.ManualSelection ? policy.ApplyProviderAvailability(ProviderCapabilityKind.Lyrics, [provider]) :
                policy.ApplyAutomaticProviderAvailability(ProviderCapabilityKind.Lyrics, [provider]);
            if (!available.Contains(provider, StringComparer.Ordinal)) return null;
        }
        if (!spec.ManualSelection && !IsAutomaticProvider(provider, policy)) return null;
        var request = Request(spec.Actor, ProviderCapabilityKind.Lyrics,
            "protocol-lyrics-auto", [provider], spec.SourceTrackId, correlationId: spec.CorrelationId, deadline: spec.Deadline,
            cancellationToken: token, allowedAccountScopes: spec.AllowedAccountScopes,
            purpose: spec.ManualSelection && !spec.HouseholdOnly ? ProviderRoutePurpose.ManualSelection : ProviderRoutePurpose.Automatic) with
        { CacheOnly = cached };
        if (spec.HouseholdOnly && (spec.AllowedAccountScopes.Count != 1 || spec.AllowedAccountScopes[0] != Core.Storage.ProviderAccountScope.Shared)) return null;
        var plan = spec.HouseholdOnly && router is IHouseholdLyricsRoutePlanner household
            ? await household.PlanHouseholdLyricsAsync(request) : await router.PlanAsync<IProviderLyricsCapability>(request);
        return plan.Candidates.SingleOrDefault();
    }

    private static LyricsRouteBinding LyricsBinding(ProviderRouteCandidate<IProviderLyricsCapability> candidate, LyricsReadSpec spec) =>
        new(candidate.Context.ProviderId, candidate.TrackId ?? spec.Request.ProviderTrackId,
            candidate.Context.Account?.AccountId, candidate.Context.Account?.Revision);
}
