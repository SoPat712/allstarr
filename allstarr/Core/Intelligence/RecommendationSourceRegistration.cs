using allstarr.Services.Recommendations;

namespace allstarr.Core.Intelligence;

public static class RecommendationSourceRegistration
{
    public static IServiceCollection AddBuiltInRecommendationSources(this IServiceCollection services)
    {
        services.AddSingleton<IScopedRecommendationAccountAccessor, ScopedRecommendationAccountAccessor>();
        services.AddSingleton<IJellyfinInstantMixClient, JellyfinInstantMixClient>();
        services.AddSingleton<ILocalRecommendationCatalog, LocalRecommendationCatalog>();
        services.AddHttpClient<ILastFmRecommendationClient, LastFmRecommendationClient>(client =>
            client.Timeout = TimeSpan.FromSeconds(10)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IListenBrainzRecommendationClient, ListenBrainzRecommendationClient>(client =>
            client.Timeout = TimeSpan.FromSeconds(10)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IAudioMuseRecommendationClient, AudioMuseRecommendationClient>();
        services.AddSingleton<IRecommendationProvider, JellyfinInstantMixRecommendationProvider>();
        services.AddSingleton<IRecommendationProvider, LocalRuleRecommendationProvider>();
        services.AddSingleton<IRecommendationProvider, MusicBrainzLocalRecommendationProvider>();
        services.AddSingleton<IRecommendationProvider, LastFmRecommendationProvider>();
        services.AddSingleton<IRecommendationProvider>(provider => new ListenBrainzRecommendationProvider(
            provider.GetRequiredService<IListenBrainzRecommendationClient>()));
        services.AddSingleton<IRecommendationProvider>(provider => new ListenBrainzRecommendationProvider(
            provider.GetRequiredService<IListenBrainzRecommendationClient>(),
            ListenBrainzDiscoveryKind.WeeklyExploration, "listenbrainz-weekly-exploration"));
        services.AddSingleton<IRecommendationProvider>(provider => new ListenBrainzRecommendationProvider(
            provider.GetRequiredService<IListenBrainzRecommendationClient>(),
            ListenBrainzDiscoveryKind.WeeklyJams, "listenbrainz-weekly-jams"));
        services.AddSingleton<IRecommendationProvider>(provider => new ListenBrainzRecommendationProvider(
            provider.GetRequiredService<IListenBrainzRecommendationClient>(),
            ListenBrainzDiscoveryKind.TopRecordings, "listenbrainz-top-recordings"));
        services.AddSingleton<IRecommendationProvider, AudioMuseRecommendationProvider>();
        return services;
    }
}
