using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Services.Common;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Core.Providers.ListenBrainz;

public static class ListenBrainzPlaylistCapabilityRegistration
{
    public static IServiceCollection AddListenBrainzPlaylistCapability(this IServiceCollection services)
    {
        services.TryAddSingleton<IProviderAccountSecretAccessor, EncryptedProviderAccountSecretAccessor>();
        services.AddHttpClient(ListenBrainzPlaylistCapabilityAdapter.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(ListenBrainzServiceEndpoint.CreateHttpHandler);
        services.AddSingleton(provider => new ListenBrainzPlaylistCapabilityAdapter(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(ListenBrainzPlaylistCapabilityAdapter.HttpClientName),
            provider.GetRequiredService<IProviderAccountSecretAccessor>()));
        services.AddSingleton<ProviderRegistration>(provider => ListenBrainzPlaylistCapabilityAdapter.CreateRegistration(
            provider.GetRequiredService<ListenBrainzPlaylistCapabilityAdapter>()));
        return services;
    }
}
