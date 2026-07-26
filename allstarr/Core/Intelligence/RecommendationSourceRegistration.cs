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
        services.AddHttpClient<IAudioMuseRecommendationClient, AudioMuseRecommendationClient>(client =>
            client.Timeout = TimeSpan.FromSeconds(10)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IRecommendationProvider, JellyfinInstantMixRecommendationProvider>();
        services.AddSingleton<IRecommendationProvider, LocalRuleRecommendationProvider>();
        services.AddSingleton<IRecommendationProvider, MusicBrainzLocalRecommendationProvider>();
        services.AddSingleton<IRecommendationProvider, LastFmRecommendationProvider>();
        services.AddSingleton<IRecommendationProvider, ListenBrainzRecommendationProvider>();
        services.AddSingleton<IRecommendationProvider, AudioMuseRecommendationProvider>();
        return services;
    }
}
