using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Core.Providers.Qobuz;

public static class QobuzDownloadCapabilityRegistration
{
    public static IServiceCollection AddQobuzProvider(this IServiceCollection services)
    {
        services.TryAddSingleton<IProviderAccountSecretAccessor, EncryptedProviderAccountSecretAccessor>();
        services.TryAddSingleton<QobuzBundleService>();
        services.AddHttpClient("QobuzApi", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            });
        services.TryAddSingleton<QobuzMediaClient>(provider => new(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("QobuzApi"),
            provider.GetRequiredService<QobuzBundleService>(),
            provider.GetRequiredService<ILogger<QobuzMediaClient>>()));
        services.TryAddSingleton<QobuzProvider>(provider => new(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("QobuzApi"),
            provider.GetRequiredService<QobuzBundleService>(),
            provider.GetRequiredService<IProviderAccountSecretAccessor>(),
            provider.GetRequiredService<ILogger<QobuzProvider>>()));
        services.AddHttpClient(QobuzDownloadCapabilityAdapter.HttpClientName, client =>
                client.Timeout = TimeSpan.FromMinutes(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                MaxConnectionsPerServer = 2,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            });
        services.AddSingleton<QobuzDownloadCapabilityAdapter>();
        services.AddSingleton<QobuzStreamingCapabilityAdapter>();
        services.AddSingleton<ProviderRegistration>(provider =>
            QobuzDownloadCapabilityAdapter.CreateRegistration(
                provider.GetRequiredService<QobuzDownloadCapabilityAdapter>(),
                provider.GetRequiredService<QobuzStreamingCapabilityAdapter>(),
                provider.GetRequiredService<QobuzProvider>(),
                new CatalogPlaylistCapability("qobuz", provider.GetRequiredService<QobuzProvider>())));
        return services;
    }
}
