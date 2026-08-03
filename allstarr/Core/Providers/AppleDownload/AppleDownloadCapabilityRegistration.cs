using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.AppleDownload;

public static class AppleDownloadCapabilityRegistration
{
    public static IServiceCollection AddAppleDownloadCapability(this IServiceCollection services)
    {
        services.AddHttpClient(AppleDownloadCapabilityAdapter.HttpClientName, client =>
                client.Timeout = TimeSpan.FromMinutes(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                MaxConnectionsPerServer = 2,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            });
        services.AddSingleton<AppleDownloadCapabilityAdapter>();
        services.AddSingleton<AppleDownloadLyricsCapabilityAdapter>();
        services.AddSingleton<AppleDownloadStreamingCapabilityAdapter>();
        services.AddSingleton<ProviderRegistration>(provider =>
            AppleDownloadCapabilityAdapter.CreateRegistration(
                provider.GetRequiredService<AppleDownloadCapabilityAdapter>(),
                provider.GetRequiredService<AppleDownloadLyricsCapabilityAdapter>(),
                provider.GetRequiredService<AppleDownloadStreamingCapabilityAdapter>()));
        return services;
    }
}
