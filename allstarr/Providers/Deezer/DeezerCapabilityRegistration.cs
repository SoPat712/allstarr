using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Core.Providers.Deezer;

public static class DeezerCapabilityRegistration
{
    public static IServiceCollection AddDeezerProvider(this IServiceCollection services)
    {
        services.TryAddSingleton<IProviderAccountSecretAccessor, EncryptedProviderAccountSecretAccessor>();
        services.TryAddSingleton<DeezerHttpClient>(provider => new(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("DeezerApi"),
            provider.GetRequiredService<IOptions<DeezerSettings>>().Value.MinRequestIntervalMs));
        services.TryAddSingleton<DeezerMediaClient>();
        services.TryAddSingleton<DeezerProvider>();
        services.AddHttpClient("DeezerApi", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);
        services.AddHttpClient(DeezerDownloadCapabilityAdapter.HttpClientName, client =>
                client.Timeout = TimeSpan.FromMinutes(30))
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);
        services.AddSingleton<DeezerDownloadCapabilityAdapter>();
        services.AddSingleton<DeezerStreamingCapabilityAdapter>();
        services.AddSingleton<ProviderRegistration>(provider =>
            DeezerProvider.CreateRegistration(
                provider.GetRequiredService<DeezerProvider>(),
                new CatalogPlaylistCapability(DeezerProvider.StableProviderId, provider.GetRequiredService<DeezerProvider>()),
                provider.GetRequiredService<DeezerDownloadCapabilityAdapter>(),
                provider.GetRequiredService<DeezerStreamingCapabilityAdapter>()));
        return services;
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        MaxConnectionsPerServer = 2,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };
}
