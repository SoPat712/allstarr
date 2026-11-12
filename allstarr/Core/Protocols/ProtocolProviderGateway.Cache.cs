using allstarr.Core.Playback;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Music;
using allstarr.Core.Routing;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    private void ScheduleCacheUpgrade(ProtocolExecutionContext protocol,
        IReadOnlyList<ProviderRouteCandidate<IProviderStreamingCapability>> candidates,
        IReadOnlyList<PlaybackStartCandidate> facts, ProviderExternalResourceId requestedTrack, ProviderAudioQuality quality)
    {
        if (managedTrackCache == null || protocol.IsOriginalDownload) return;
        ProviderMediaFormat? BestMedia(PlaybackStartCandidate fact) => PlaybackStartPolicy.CompareQuality(fact.AvailableMedia, fact.Media) > 0
            ? fact.AvailableMedia : fact.Media;
        var cached = facts.FirstOrDefault(fact => fact.IsCached && facts.Any(other =>
            PlaybackStartPolicy.CompareQuality(BestMedia(other), fact.Media) > 0));
        if (cached == null) return;
        var target = candidates[cached.Index];
        var track = target.TrackId ?? requestedTrack;
        var scope = managedTrackCache.ScopeFor(protocol, target.Context.Account?.AccountId, quality, target.Context.Account?.Revision);
        var better = facts.Where(fact => PlaybackStartPolicy.CompareQuality(BestMedia(fact), cached.Media) > 0)
            .OrderByDescending(BestMedia, Comparer<ProviderMediaFormat?>.Create(PlaybackStartPolicy.CompareQuality))
            .Take(4).Select(fact => candidates[fact.Index]).ToArray();
        _ = managedTrackCache.ScheduleUpgrade(protocol, track, scope, target.Context.Account?.Revision, async token =>
        {
            var background = ManagedTrackCacheService.UpgradeContext(protocol, token);
            foreach (var candidate in better)
            {
                var source = candidate.TrackId ?? requestedTrack;
                var execution = new ProviderExecutionContext(candidate.Context.Actor, candidate.Context.ProviderId,
                    candidate.Context.Account, candidate.Context.Policy, candidate.Context.OperationId, candidate.Context.CorrelationId,
                    background.Deadline, token, candidate.Context.IdempotencyKey);
                if (!await AllowsCleanPlaybackAsync(background, source, execution)) continue;
                var available = await OpenAvailableCacheAsync(background, execution, source, quality, null, false, false, token);
                if (available != null)
                {
                    if (PlaybackStartPolicy.CompareQuality(available.FileFacts.Media ?? available.Lease.Media, cached.Media) > 0) return available;
                    available.Response.Dispose();
                }
                if (outcomes?.IsPlaybackCoolingDown(execution, source, quality) == true) continue;
                var outcome = await OpenCandidateAsync(
                    () => candidate.Implementation.GetStreamLeaseAsync(execution, new(source, quality)), async lease =>
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
                        return lease.ProtectedResponseFactory != null ? await lease.ProtectedResponseFactory(request, token)
                            : await httpClientFactory.CreateClient(StreamingClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    }, false, null, token, await ExpectedPlaybackDurationAsync(execution, source, requestedTrack));
                if (!outcome.IsSuccess) continue;
                var opened = outcome.RequireValue();
                if (PlaybackStartPolicy.CompareQuality(opened.Lease.Media, cached.Media) <= 0)
                {
                    opened.Response.Dispose();
                    continue;
                }
                return new(opened.Response, opened.Lease, candidate.Provider.Id, source.Value, execution.Account?.AccountId,
                    EffectiveQuality: quality, ServingAccountRevision: execution.Account?.Revision)
                { FileFacts = new() { Measured = PrefetchedStream.Facts(opened.Response.Content), Media = opened.Lease.Media } };
            }
            return null;
        }, (stream, token) => GetSongAsync(ManagedTrackCacheService.UpgradeContext(protocol, token), stream.ServingProviderId, stream.ServingExternalId!));
    }

    private static IEnumerable<ProviderAudioQuality> CachedQualities(ProtocolExecutionContext protocol, ProviderAudioQuality quality, bool range)
    {
        yield return quality;
        if (protocol.IsOriginalDownload) yield break;
        var delivery = protocol.AudioDelivery;
        if (!range && (delivery == null || delivery.Profiles == null && delivery.Formats == null &&
            delivery.TranscodingProfiles == null && delivery.MaxBitrate == null && delivery.MaxSampleRate == null &&
            delivery.SampleRate == null && delivery.MaxChannels == null && delivery.Channels == null && delivery.MaxBitDepth == null &&
            delivery.EnableDirectPlay && delivery.EnableDirectStream)) yield break;
        foreach (var other in new[] { ProviderAudioQuality.HighResolution, ProviderAudioQuality.Lossless,
                     ProviderAudioQuality.Lossy, ProviderAudioQuality.DataSaver, ProviderAudioQuality.Any })
            if (other != quality && (range || other == ProviderAudioQuality.Any || other >= quality)) yield return other;
    }

    private async Task<ProtocolProviderStream?> OpenAvailableCacheAsync(ProtocolExecutionContext protocol,
        ProviderExecutionContext execution, ProviderExternalResourceId track, ProviderAudioQuality quality,
        long? offset, bool completeOnly, bool range, CancellationToken token)
    {
        if (managedTrackCache == null || !await AllowsCleanPlaybackAsync(protocol, track, execution)) return null;
        foreach (var variant in CachedQualities(protocol, quality, range))
        {
            var scope = managedTrackCache.ScopeFor(protocol, execution.Account?.AccountId, variant, execution.Account?.Revision);
            var opened = await managedTrackCache.TryOpenAsync(track, scope, token, execution.Account?.Revision,
                protocol.IsPreparation, offset, protocol.IsOriginalDownload, protocol.RetainOriginalMedia, completeOnly);
            if (opened != null)
            {
                if (CanDeliverCached(protocol, opened)) return opened;
                opened.Response.Dispose();
            }
        }
        if (!protocol.IsOriginalDownload && !protocol.IsPreparation)
        {
            var original = await managedTrackCache.TryOpenAsync(track, new(execution.Account?.AccountId, ProviderAudioQuality.Any),
                token, execution.Account?.Revision, requestedOffset: offset, originalDownload: true,
                retainOriginalMedia: true, completeOnly: true);
            if (original != null)
            {
                if (CanDeliverCached(protocol, original)) return original;
                original.Response.Dispose();
            }
        }
        return null;
    }

    private async Task<ProviderMediaFormat?> AvailableCachedMediaAsync(ProtocolExecutionContext protocol,
        ProviderExecutionContext execution, ProviderExternalResourceId track, ProviderAudioQuality quality, CancellationToken token)
    {
        if (managedTrackCache == null) return null;
        if (protocol.IsOriginalDownload)
            return managedTrackCache.CanRetainOriginal(protocol)
                ? await managedTrackCache.ReadCachedMediaAsync(track,
                    managedTrackCache.ScopeFor(protocol, execution.Account?.AccountId, quality, execution.Account?.Revision),
                    token, execution.Account?.Revision)
                : null;
        foreach (var variant in CachedQualities(protocol, quality, false))
        {
            var media = await managedTrackCache.ReadCachedMediaAsync(track,
                new(execution.Account?.AccountId, variant), token, execution.Account?.Revision);
            if (media != null) return media;
        }
        return await managedTrackCache.ReadCachedMediaAsync(track,
            new(execution.Account?.AccountId, ProviderAudioQuality.Any, DownloadedSongRepresentation.Original, execution.Account?.Revision),
            token, execution.Account?.Revision);
    }
}
