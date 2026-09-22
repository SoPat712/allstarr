using allstarr.Core.Jobs;
using allstarr.Core.Intelligence;
using allstarr.Services.Common;
using allstarr.Services.Scrobbling;

namespace allstarr.Core.Playback;

public static class PlaybackRegistration
{
    public static IServiceCollection AddDurablePlaybackSignals(
        this IServiceCollection services,
        bool includeIntelligenceEnrichment)
    {
        services.AddSingleton<PlaybackDeliveryActivityStore>();
        services.AddSingleton<IPlaybackDeliveryActivitySource>(provider =>
            provider.GetRequiredService<PlaybackDeliveryActivityStore>());
        services.AddSingleton<IPlaybackSignalPipeline, PlaybackSignalPipeline>();
        services.AddSingleton<IPlaybackTrackResolver, PlaybackTrackResolver>();
        services.AddSingleton<IPlaybackLyricsPrefetch, PlaybackLyricsPrefetch>();
        services.AddSingleton<IScopedPlaybackScrobbleDelivery, ScopedPlaybackScrobbleDelivery>();
        services.AddSingleton<IPlaybackDeliveryCheckpointStore, EfPlaybackDeliveryCheckpointStore>();
        if (includeIntelligenceEnrichment)
        {
            services.AddSingleton<MusicBrainzListeningEnrichmentQueue>();
            services.AddSingleton<IDurableJobHandler, MusicBrainzListeningEnrichmentJobHandler>();
        }
        else
        {
            services.AddSingleton<IRecommendationSignalWriter, DisabledRecommendationSignalWriter>();
        }
        services.AddHttpClient<IExactScopePlaybackScrobbleTarget, LastFmScopedPlaybackScrobbleTarget>(client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IExactScopePlaybackScrobbleTarget, ListenBrainzScopedPlaybackScrobbleTarget>(client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IDurableJobHandler, PlaybackSignalJobHandler>();
        return services;
    }

    private sealed class DisabledRecommendationSignalWriter : IRecommendationSignalWriter
    {
        public Task<bool> WriteAsync(
            IntelligenceScope scope,
            string signalType,
            string trackReference,
            double value,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
