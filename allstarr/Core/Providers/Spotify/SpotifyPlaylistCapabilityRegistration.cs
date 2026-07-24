using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Spotify;

public static class SpotifyPlaylistCapabilityRegistration
{
    public static IServiceCollection AddSpotifyPlaylistCapability(this IServiceCollection services)
    {
        services.AddSingleton<IProviderAccountSecretAccessor, EncryptedProviderAccountSecretAccessor>();
        services.AddHttpClient(SpotifyPlaylistCapabilityAdapter.HttpClientName);
        services.AddSingleton(provider => new SpotifyPlaylistCapabilityAdapter(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<IProviderAccountSecretAccessor>(),
            provider.GetRequiredService<IApplicationCache>(),
            provider.GetRequiredService<ILogger<SpotifyPathfinderPlaylistClient>>()));
        services.AddSingleton<ProviderRegistration>(provider =>
            SpotifyPlaylistCapabilityAdapter.CreateRegistration(
                provider.GetRequiredService<SpotifyPlaylistCapabilityAdapter>()));
        return services;
    }
}
