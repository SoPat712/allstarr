using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Services.Common;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
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
