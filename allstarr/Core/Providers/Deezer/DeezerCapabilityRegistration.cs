using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Services.Deezer;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Core.Providers.Deezer;

public static class DeezerCapabilityRegistration
{
    public static IServiceCollection AddDeezerMetadataCapability(this IServiceCollection services)
    {
        services.TryAddSingleton<IProviderAccountSecretAccessor, EncryptedProviderAccountSecretAccessor>();
        services.TryAddSingleton<DeezerDownloadService>();
        services.AddHttpClient(DeezerDownloadCapabilityAdapter.HttpClientName, client =>
                client.Timeout = TimeSpan.FromMinutes(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                MaxConnectionsPerServer = 2,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            });
        services.AddSingleton<DeezerMetadataCapabilityAdapter>(provider =>
            new DeezerMetadataCapabilityAdapter(
                provider.GetRequiredService<DeezerMetadataService>()));
        services.AddSingleton<DeezerDownloadCapabilityAdapter>();
        services.AddSingleton<DeezerStreamingCapabilityAdapter>();
        services.AddSingleton<ProviderRegistration>(provider =>
            DeezerMetadataCapabilityAdapter.CreateRegistration(
                provider.GetRequiredService<DeezerMetadataCapabilityAdapter>(),
                provider.GetRequiredService<DeezerDownloadCapabilityAdapter>(),
                provider.GetRequiredService<DeezerStreamingCapabilityAdapter>()));
        return services;
    }
}
