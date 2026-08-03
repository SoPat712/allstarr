using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Services.Qobuz;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Core.Providers.Qobuz;

public static class QobuzDownloadCapabilityRegistration
{
    public static IServiceCollection AddQobuzDownloadCapability(this IServiceCollection services)
    {
        services.TryAddSingleton<IProviderAccountSecretAccessor, EncryptedProviderAccountSecretAccessor>();
        services.TryAddSingleton<QobuzDownloadService>();
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
        services.AddSingleton<ProviderRegistration>(provider =>
            QobuzDownloadCapabilityAdapter.CreateRegistration(
                provider.GetRequiredService<QobuzDownloadCapabilityAdapter>()));
        return services;
    }
}
