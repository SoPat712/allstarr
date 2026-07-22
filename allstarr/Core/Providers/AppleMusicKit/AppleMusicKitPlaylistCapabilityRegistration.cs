using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;

namespace allstarr.Core.Providers.AppleMusicKit;

public static class AppleMusicKitPlaylistCapabilityRegistration
{
    public static IServiceCollection AddAppleMusicKitPlaylistCapability(this IServiceCollection services)
    {
        services.AddSingleton<IProviderAccountSecretAccessor, EncryptedProviderAccountSecretAccessor>();
        services.AddHttpClient(AppleMusicKitPlaylistCapabilityAdapter.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton(provider => new AppleMusicKitPlaylistCapabilityAdapter(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<IProviderAccountSecretAccessor>()));
        services.AddSingleton<ProviderRegistration>(provider =>
            AppleMusicKitPlaylistCapabilityAdapter.CreateRegistration(
                provider.GetRequiredService<AppleMusicKitPlaylistCapabilityAdapter>()));
        return services;
    }
}
