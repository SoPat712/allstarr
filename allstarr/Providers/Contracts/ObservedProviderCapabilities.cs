using System.Diagnostics;
using allstarr.Core.Health;

namespace allstarr.Core.Capabilities;

public static class ObservedProviderCapabilities
{
    public static TCapability Wrap<TCapability>(TCapability implementation, IProviderOutcomeObserver observer)
        where TCapability : class, IProviderCapability
    {
        ArgumentNullException.ThrowIfNull(implementation);
        ArgumentNullException.ThrowIfNull(observer);
        IProviderCapability? wrapped = implementation switch
        {
            IProviderMetadataCapability metadata when CanReturn<Metadata>() => new Metadata(metadata, observer),
            IProviderPlaylistCapability playlist when CanReturn<Playlist>() => new Playlist(playlist, observer),
            IProviderDownloadCapability download when CanReturn<Download>() => new Download(download, observer),
            IProviderLyricsCapability lyrics when CanReturn<Lyrics>() => new Lyrics(lyrics, observer),
            IProviderStreamingCapability streaming when CanReturn<Streaming>() => new Streaming(streaming, observer),
            IProviderIntelligenceCapability intelligence when CanReturn<Intelligence>() => new Intelligence(intelligence, observer),
            IProviderHealthProbeCapability health when CanReturn<Health>() => new Health(health, observer),
            _ => null
        };
        return wrapped is TCapability result ? result : implementation;

        static bool CanReturn<TWrapper>() => typeof(TCapability).IsAssignableFrom(typeof(TWrapper));
    }

    private abstract class ObservedCapability<TCapability>(TCapability implementation, IProviderOutcomeObserver observer)
        : IProviderCapability where TCapability : class, IProviderCapability
    {
        protected TCapability Implementation { get; } = implementation;
        public string ProviderId => Implementation.ProviderId;
        public ProviderCapabilityKind Capability => Implementation.Capability;

        protected async Task<ProviderOutcome<T>> InvokeAsync<T>(
            ProviderExecutionContext context, Func<Task<ProviderOutcome<T>>> operation, bool observeSuccess = true,
            ProviderCapabilityKind? observedCapability = null,
            Func<ProviderOutcome<T>, ProviderError?>? observationError = null)
        {
            var started = Stopwatch.GetTimestamp();
            ProviderOutcome<T> outcome;
            try
            {
                outcome = await operation();
            }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
                {
                    Observe(context, new ProviderError(exception is UnauthorizedAccessException
                        ? ProviderErrorKind.Unauthorized
                        : ProviderErrorKind.TransientFailure), started, observedCapability);
                }
                throw;
            }

            if (observeSuccess || !outcome.IsSuccess)
                Observe(context, observationError == null ? outcome.Error : observationError(outcome), started, observedCapability);
            return outcome;
        }

        private void Observe(ProviderExecutionContext context, ProviderError? error, long started,
            ProviderCapabilityKind? observedCapability)
        {
            try
            {
                observer.Observe(context, observedCapability ?? Capability, error, Stopwatch.GetElapsedTime(started));
            }
            catch (Exception)
            {
                // Status observation must never replace a provider result or exception.
            }
        }
    }

    private sealed class Metadata(IProviderMetadataCapability implementation, IProviderOutcomeObserver observer)
        : ObservedCapability<IProviderMetadataCapability>(implementation, observer), IProviderMetadataCapability
    {
        public Task<ProviderOutcome<ProviderArtworkReference>> GetPlaylistArtworkAsync(
            ProviderExecutionContext context, ProviderExternalResourceId playlistId) =>
            InvokeAsync(context, () => Implementation.GetPlaylistArtworkAsync(context, playlistId));

        public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> SearchTracksAsync(
            ProviderExecutionContext context, ProviderMetadataSearchRequest request) =>
            InvokeAsync(context, () => Implementation.SearchTracksAsync(context, request));

        public Task<ProviderOutcome<ProviderTrackMetadata>> GetTrackAsync(
            ProviderExecutionContext context, ProviderTrackLookupRequest request) =>
            InvokeAsync(context, () => Implementation.GetTrackAsync(context, request));

        public Task<ProviderOutcome<ProviderTrackMetadata>> LookupByIsrcAsync(
            ProviderExecutionContext context, ProviderIsrcLookupRequest request) =>
            InvokeAsync(context, () => Implementation.LookupByIsrcAsync(context, request));

        public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> SearchAlbumsAsync(
            ProviderExecutionContext context, ProviderMetadataSearchRequest request) =>
            InvokeAsync(context, () => Implementation.SearchAlbumsAsync(context, request));

        public Task<ProviderOutcome<ProviderAlbumMetadata>> GetAlbumAsync(
            ProviderExecutionContext context, ProviderAlbumLookupRequest request) =>
            InvokeAsync(context, () => Implementation.GetAlbumAsync(context, request));

        public Task<ProviderOutcome<ProviderPage<ProviderArtistMetadata>>> SearchArtistsAsync(
            ProviderExecutionContext context, ProviderMetadataSearchRequest request) =>
            InvokeAsync(context, () => Implementation.SearchArtistsAsync(context, request));

        public Task<ProviderOutcome<ProviderArtistMetadata>> GetArtistAsync(
            ProviderExecutionContext context, ProviderArtistLookupRequest request) =>
            InvokeAsync(context, () => Implementation.GetArtistAsync(context, request));

        public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> GetArtistAlbumsAsync(
            ProviderExecutionContext context, ProviderArtistItemsRequest request) =>
            InvokeAsync(context, () => Implementation.GetArtistAlbumsAsync(context, request));

        public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> GetArtistTracksAsync(
            ProviderExecutionContext context, ProviderArtistItemsRequest request) =>
            InvokeAsync(context, () => Implementation.GetArtistTracksAsync(context, request));
    }

    private sealed class Playlist(IProviderPlaylistCapability implementation, IProviderOutcomeObserver observer)
        : ObservedCapability<IProviderPlaylistCapability>(implementation, observer), IProviderPlaylistCapability
    {
        public ProviderPlaylistMutationSupport MutationSupport => Implementation.MutationSupport;

        public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetUserPlaylistsAsync(
            ProviderExecutionContext context, ProviderUserPlaylistsRequest request) =>
            InvokeAsync(context, () => Implementation.GetUserPlaylistsAsync(context, request));

        public Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetPlaylistTracksAsync(
            ProviderExecutionContext context, ProviderPlaylistTracksRequest request) =>
            InvokeAsync(context, () => Implementation.GetPlaylistTracksAsync(context, request));

        public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> SearchPlaylistsAsync(
            ProviderExecutionContext context, ProviderPlaylistSearchRequest request) =>
            InvokeAsync(context, () => Implementation.SearchPlaylistsAsync(context, request));

        public Task<ProviderOutcome<ProviderPlaylistArtwork>> ResolveArtworkAsync(
            ProviderExecutionContext context, ProviderPlaylistArtworkRequest request) =>
            InvokeAsync(context, () => Implementation.ResolveArtworkAsync(context, request));

        public Task<ProviderOutcome<ProviderPlaylistMutationReceipt>> MutatePlaylistAsync(
            ProviderExecutionContext context, ProviderPlaylistMutationRequest request) =>
            InvokeAsync(context, () => Implementation.MutatePlaylistAsync(context, request));
    }

    private sealed class Download(IProviderDownloadCapability implementation, IProviderOutcomeObserver observer)
        : ObservedCapability<IProviderDownloadCapability>(implementation, observer), IProviderDownloadCapability
    {
        public Task<ProviderOutcome<ProviderDownloadAvailability>> CheckAvailabilityAsync(
            ProviderExecutionContext context, ProviderDownloadAvailabilityRequest request) =>
            InvokeAsync(context, () => Implementation.CheckAvailabilityAsync(context, request));

        public Task<ProviderOutcome<ProviderDownloadedArtifact>> DownloadAsync(
            ProviderExecutionContext context, ProviderDownloadRequest request,
            IProgress<ProviderDownloadProgress>? progress = null) =>
            InvokeAsync(context, () => Implementation.DownloadAsync(context, request, progress));
    }

    private sealed class Lyrics(IProviderLyricsCapability implementation, IProviderOutcomeObserver observer)
        : ObservedCapability<IProviderLyricsCapability>(implementation, observer), IProviderLyricsCapability
    {
        public Task<ProviderOutcome<ProviderLyricsResult>> FetchLyricsAsync(
            ProviderExecutionContext context, ProviderLyricsRequest request) =>
            InvokeAsync(context, () => Implementation.FetchLyricsAsync(context, request));
    }

    private sealed class Streaming(IProviderStreamingCapability implementation, IProviderOutcomeObserver observer)
        : ObservedCapability<IProviderStreamingCapability>(implementation, observer), IProviderStreamingCapability
    {
        public Task<ProviderOutcome<ProviderStreamLease>> GetStreamLeaseAsync(
            ProviderExecutionContext context, ProviderStreamLeaseRequest request) =>
            InvokeAsync(context, () => Implementation.GetStreamLeaseAsync(context, request), observeSuccess: false);

        public Task<ProviderOutcome<ProviderStreamProbeResult>> ProbeStreamAsync(
            ProviderExecutionContext context, ProviderStreamLeaseRequest request) =>
            InvokeAsync(context, () => Implementation.ProbeStreamAsync(context, request),
                observationError: outcome => !outcome.IsSuccess ? outcome.Error :
                    outcome.RequireValue().Available ? null : new ProviderError(ProviderErrorKind.CapabilityUnavailable));
    }

    private sealed class Intelligence(IProviderIntelligenceCapability implementation, IProviderOutcomeObserver observer)
        : ObservedCapability<IProviderIntelligenceCapability>(implementation, observer), IProviderIntelligenceCapability
    {
        public Task<ProviderOutcome<ProviderAnalysisProgress>> StartAnalysisAsync(
            ProviderExecutionContext context, bool rebuild = false) =>
            InvokeAsync(context, () => Implementation.StartAnalysisAsync(context, rebuild));

        public Task<ProviderOutcome<ProviderAnalysisProgress>> GetAnalysisProgressAsync(
            ProviderExecutionContext context, string jobId) =>
            InvokeAsync(context, () => Implementation.GetAnalysisProgressAsync(context, jobId));

        public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceCluster>>> GetClustersAsync(
            ProviderExecutionContext context, int limit = 50) =>
            InvokeAsync(context, () => Implementation.GetClustersAsync(context, limit));

        public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>> RecommendAsync(
            ProviderExecutionContext context, IReadOnlyList<string> seedTrackIds, int limit) =>
            InvokeAsync(context, () => Implementation.RecommendAsync(context, seedTrackIds, limit));

        public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>> SearchAsync(
            ProviderExecutionContext context, string query, bool includeLyrics, int limit) =>
            InvokeAsync(context, () => Implementation.SearchAsync(context, query, includeLyrics, limit));

        public Task<ProviderOutcome<ProviderIntelligencePath>> FindPathAsync(
            ProviderExecutionContext context, string startTrackId, string endTrackId, int limit) =>
            InvokeAsync(context, () => Implementation.FindPathAsync(context, startTrackId, endTrackId, limit));

        public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>> BlendAsync(
            ProviderExecutionContext context, IReadOnlyList<string> positiveSeedTrackIds,
            IReadOnlyList<string> negativeSeedTrackIds, int limit) =>
            InvokeAsync(context, () => Implementation.BlendAsync(context, positiveSeedTrackIds, negativeSeedTrackIds, limit));

        public Task<ProviderOutcome<ProviderIntelligenceMapPage>> GetMapAsync(
            ProviderExecutionContext context, ProviderPageRequest page) =>
            InvokeAsync(context, () => Implementation.GetMapAsync(context, page));

        public Task<ProviderOutcome<bool>> DisconnectAsync(ProviderExecutionContext context) =>
            InvokeAsync(context, () => Implementation.DisconnectAsync(context));
    }

    private sealed class Health(IProviderHealthProbeCapability implementation, IProviderOutcomeObserver observer)
        : ObservedCapability<IProviderHealthProbeCapability>(implementation, observer), IProviderHealthProbeCapability
    {
        public Task<ProviderOutcome<ProviderHealthProbeResult>> ProbeAsync(
            ProviderExecutionContext context, ProviderHealthProbeRequest request) =>
            InvokeAsync(context, () => Implementation.ProbeAsync(context, request),
                observedCapability: request.TargetCapability,
                observationError: outcome => !outcome.IsSuccess ? outcome.Error : outcome.RequireValue().Status switch
                {
                    ProviderProbeStatus.Healthy => null,
                    ProviderProbeStatus.Unauthorized => new ProviderError(ProviderErrorKind.Unauthorized),
                    _ => new ProviderError(ProviderErrorKind.TransientFailure)
                });
    }
}
