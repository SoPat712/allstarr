using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> availabilityLookups = new(StringComparer.Ordinal);

    private IReadOnlyList<string> StoredStreamingOrder(EffectiveProviderPolicySnapshot? policy)
    {
        var definition = ProviderOrderPolicyCatalog.Find(ProviderCapabilityKind.Streaming)!;
        var order = policy?.GetProviderOrder(ProviderCapabilityKind.Streaming) ??
            (configuration?[definition.SettingKey] ?? configuration?[definition.BootstrapKey] ?? definition.DefaultValue)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var available = ResolveProviderOrder(ProviderCapabilityKind.Streaming, policy);
        return order.Select(NormalizeProvider).Select(registry.ResolveProviderId)
            .Where(id => available.Contains(id, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private async Task<bool> TryResolvePlaybackAvailabilityAsync(ProviderRoutePlan<IProviderStreamingCapability> plan,
        IReadOnlyList<string> storedOrder, ProviderExternalResourceId source, string exactMissKey,
        EffectiveProviderPolicySnapshot? effectivePolicy, ProviderAudioQuality quality)
    {
        if (identities == null || !plan.Request.Policy.AllowFallback || !plan.Request.Policy.AllowManagedDownloads) return false;
        var provider = storedOrder.FirstOrDefault(id => plan.Request.ProviderPriority.Contains(id, StringComparer.Ordinal) &&
            plan.Decision.Candidates.Any(item => item.ProviderId == id && item.ReasonCode == "verified-identity-required") &&
            registry.TryGetCapability<IProviderStreamingCapability>(id, ProviderCapabilityKind.Streaming, out var implementation) &&
            implementation is IProviderDownloadStreamingCapability { SupportsRecordingAvailability: true });
        if (provider == null) return false;
        var request = plan.Request;
        var policy = request.Policy;
        var authorization = await router.PlanAsync<IProviderStreamingCapability>(new(
            request.Capability, request.Actor,
            new(policy.Quality, policy.ExplicitContent, policy.AllowFallback, policy.AllowSharedAccount,
                policy.AllowManagedDownloads, [provider]),
            request.OperationId, request.CorrelationId, request.Deadline, [provider], request.ProviderStates.Values,
            sourceTrackId: null, idempotencyKey: request.IdempotencyKey, cancellationToken: request.CancellationToken));
        var candidate = authorization.Candidates.SingleOrDefault();
        if (candidate?.Implementation is not IProviderDownloadStreamingCapability { SupportsRecordingAvailability: true }) return false;
        var evidence = await identities.GetRecordingAvailabilityAsync(candidate.Context, ProtocolItemId(source), request.CancellationToken);
        if (evidence == null || effectivePolicy?.Includes(evidence.ExplicitContentLyrics) == false ||
            !Guid.TryParse(evidence.SampleId, out var recordingId)) return false;
        var keyMaterial = JsonSerializer.Serialize(new
        {
            request.Actor,
            recordingId,
            provider,
            quality,
            candidate.Context.Account?.AccountId,
            candidate.Context.Account?.Revision,
            policy,
            States = request.ProviderStates.Values,
            Order = request.ProviderPriority,
            effectivePolicy?.PreferenceRevision,
            effectivePolicy?.Preferences,
            effectivePolicy?.AudioQuality
        });
        var key = "playback:availability:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial)));
        var pending = availabilityLookups.GetOrAdd(key, _ => new(() => ResolveAndLinkAsync(candidate, evidence, recordingId, key)));
        var task = pending.Value;
        _ = task.ContinueWith(_ => availabilityLookups.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(key, pending)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var resolved = await task.WaitAsync(request.CancellationToken);
        if (resolved && applicationCache?.IsEnabled == true) await applicationCache.DeleteAsync(exactMissKey);
        return resolved;

        async Task<bool> ResolveAndLinkAsync(ProviderRouteCandidate<IProviderStreamingCapability> authorized,
            ProviderRecordingAvailabilityRequest recording, Guid id, string missKey)
        {
            var context = authorized.Context;
            var target = new ProviderTrackIdentityTarget(provider, catalog: request.ProviderStates.GetValueOrDefault(provider)?.TrackCatalog);
            if (await identities.ResolveTargetAsync(context, id, target, context.CancellationToken) != null) return true;
            if (applicationCache?.IsEnabled == true && await applicationCache.ExistsAsync(missKey)) return false;
            var clock = timeProvider ?? TimeProvider.System;
            var deadline = clock.GetUtcNow().AddSeconds(10);
            if (context.Deadline < deadline) deadline = context.Deadline;
            var remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return false;
            using var timeout = new CancellationTokenSource(remaining, clock);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, timeout.Token);
            var execution = new ProviderExecutionContext(context.Actor, context.ProviderId, context.Account, context.Policy,
                context.OperationId, context.CorrelationId, deadline, bounded.Token, context.IdempotencyKey);
            try
            {
                var outcome = await ((IProviderDownloadStreamingCapability)authorized.Implementation)
                    .ResolveRecordingAsync(execution, recording, quality).WaitAsync(bounded.Token);
                bounded.Token.ThrowIfCancellationRequested();
                if (outcome.IsSuccess && outcome.RequireValue().TrackId is { } track &&
                    track.ProviderId == provider && track.ResourceKind == ProviderResourceKind.Track && track.Catalog == target.Catalog)
                {
                    // Recheck manual authority after the external operation before persisting its result.
                    if (await identities.GetRecordingAvailabilityAsync(execution, ProtocolItemId(source), bounded.Token) == null) return false;
                    var linked = await identities.LinkAsync(execution, new(id, track,
                        execution.Account == null ? ProviderIdentityScope.Catalog : ProviderIdentityScope.Account,
                        ProviderIdentityVerification.Verified, "availability", 1), bounded.Token);
                    if (linked.Status is TrackIdentityLinkStatus.Created or TrackIdentityLinkStatus.AlreadyLinked)
                    {
                        if (applicationCache?.IsEnabled == true) await applicationCache.DeleteAsync(missKey);
                        return true;
                    }
                }
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested) { }
            catch (TimeoutException) { }
            catch (HttpRequestException) { }
            context.CancellationToken.ThrowIfCancellationRequested();
            if (applicationCache?.IsEnabled == true) await applicationCache.SetStringAsync(missKey, "1", ExactRouteMissTtl);
            return false;
        }
    }
}
