using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Spotify;

public static class SpotifyPlaylistCapabilityRegistration
{
    public static IServiceCollection AddSpotifyPlaylistCapability(this IServiceCollection services)
    {
        services.AddSingleton<IProviderAccountSecretAccessor, EncryptedProviderAccountSecretAccessor>();
        services.AddHttpClient(SpotifyPlaylistCapabilityAdapter.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
        services.AddSingleton(provider => new SpotifyPlaylistCapabilityAdapter(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<IProviderAccountSecretAccessor>(),
            provider.GetRequiredService<IApplicationCache>(),
            provider.GetRequiredService<ILogger<SpotifyPathfinderPlaylistClient>>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<allstarr.Models.Settings.SpotifyApiSettings>>()));
        services.AddSingleton<SpotifyLyricsCapabilityAdapter>(provider =>
            new SpotifyLyricsCapabilityAdapter(provider.GetRequiredService<SpotifyPlaylistCapabilityAdapter>()));
        services.AddSingleton<IProviderLyricsCapability>(provider =>
            provider.GetRequiredService<SpotifyLyricsCapabilityAdapter>());
        services.AddSingleton<ProviderRegistration>(provider =>
            SpotifyPlaylistCapabilityAdapter.CreateRegistration(
                provider.GetRequiredService<SpotifyPlaylistCapabilityAdapter>(),
                provider.GetRequiredService<SpotifyLyricsCapabilityAdapter>()));
        return services;
    }
}
