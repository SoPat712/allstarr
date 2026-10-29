using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Core.Providers.LastFm;

public static class LastFmPlaylistCapabilityRegistration
{
    public static IServiceCollection AddLastFmPlaylistCapability(this IServiceCollection services)
    {
        services.TryAddSingleton<IProviderAccountSecretAccessor, EncryptedProviderAccountSecretAccessor>();
        services.AddSingleton(provider => new LastFmPlaylistCapabilityAdapter(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("LastFm"),
            provider.GetRequiredService<IProviderAccountSecretAccessor>(), provider.GetRequiredService<IApplicationCache>()));
        services.AddSingleton(provider => CreateRegistration(provider.GetRequiredService<LastFmPlaylistCapabilityAdapter>()));
        return services;
    }

    public static ProviderRegistration CreateRegistration(LastFmPlaylistCapabilityAdapter adapter) => new(
        new ProviderDescriptor("lastfm", "Last.fm", "Loved tracks, top tracks and suggestions from your connected Last.fm account.",
            ProviderOrigin.BuiltIn, "1", "lastfm-playlists-v1",
            [new(ProviderCapabilityKind.Playlist, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Required,
                "1", ["getUserPlaylists", "getPlaylistTracks"], [ProviderAccountScope.Personal], supportsPlaylistDiscovery: true)],
            new ProviderPermissionDescriptor([new Uri("https://ws.audioscrobbler.com/")], true)),
        [adapter]);
}
