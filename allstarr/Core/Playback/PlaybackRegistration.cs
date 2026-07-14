using allstarr.Core.Jobs;
using allstarr.Services.Scrobbling;

namespace allstarr.Core.Playback;

public static class PlaybackRegistration
{
    public static IServiceCollection AddDurablePlaybackSignals(this IServiceCollection services)
    {
        services.AddSingleton<IPlaybackSignalPipeline, PlaybackSignalPipeline>();
        services.AddSingleton<IPlaybackLyricsPrefetch, PlaybackLyricsPrefetch>();
        services.AddSingleton<IScopedPlaybackScrobbleDelivery, ScopedPlaybackScrobbleDelivery>();
        services.AddSingleton<IPlaybackDeliveryCheckpointStore, EfPlaybackDeliveryCheckpointStore>();
        services.AddHttpClient<IExactScopePlaybackScrobbleTarget, LastFmScopedPlaybackScrobbleTarget>(client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IExactScopePlaybackScrobbleTarget, ListenBrainzScopedPlaybackScrobbleTarget>(client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IDurableJobHandler, PlaybackSignalJobHandler>();
        return services;
    }
}
