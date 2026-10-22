using allstarr.Core.Capabilities;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Core.Providers.ArtistGrid;

public static class ArtistGridProviderRegistration
{
    public static IServiceCollection AddArtistGridProvider(this IServiceCollection services)
    {
        services.AddHttpClient(ArtistGridConstants.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(ArtistGridHttpTransport.CreateHandler);
        services.TryAddSingleton<ArtistGridCatalog>(provider => new(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(ArtistGridConstants.HttpClientName)));
        services.TryAddSingleton<ArtistGridMetadataCapabilityAdapter>();
        services.TryAddSingleton<ArtistGridStreamingCapabilityAdapter>(provider => new(
            provider.GetRequiredService<ArtistGridCatalog>(),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(ArtistGridConstants.HttpClientName)));
        services.AddSingleton<ProviderRegistration>(provider => CreateRegistration(
            provider.GetRequiredService<ArtistGridMetadataCapabilityAdapter>(),
            provider.GetRequiredService<ArtistGridStreamingCapabilityAdapter>()));
        return services;
    }

    public static ProviderRegistration CreateRegistration(
        ArtistGridMetadataCapabilityAdapter metadata,
        ArtistGridStreamingCapabilityAdapter streaming) => new(
        new ProviderDescriptor(
            ArtistGridConstants.ProviderId,
            "ArtistGrid",
            "Manual ArtistGrid metadata search and source streaming.",
            ProviderOrigin.BuiltIn,
            sdkVersion: "1",
            compatibilityVersion: "artistgrid-v1",
            capabilities:
            [
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Metadata,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None,
                    compatibilityVersion: "1",
                    hooks: ["searchTracks", "getTrack", "getAlbum", "searchArtists", "getArtist"],
                    supportsPublicRead: true),
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Streaming,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None,
                    compatibilityVersion: "1",
                    hooks: ["getStreamLease", "probeStream"]),
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Health,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None,
                    compatibilityVersion: "1",
                    hooks: ["probeMetadata", "probeStreaming"])
            ],
            permissions: new ProviderPermissionDescriptor(
                networkOrigins:
                [
                    new Uri("https://artists.artistgrid.cx/"),
                    new Uri("https://trackerapi.artistgrid.cx/"),
                    new Uri("https://pillows.su/"),
                    new Uri("https://api.pillows.su/"),
                    new Uri("https://pixeldrain.com/"),
                    new Uri("https://drive.google.com/"),
                    new Uri("https://music.froste.lol/"),
                    new Uri("https://fuck-unvaulted.artistgrid.cx/")
                ],
                cache: true), healthProbe: true)
        {
            DefaultUseMode = ProviderUseMode.ManualOnly
        },
        [metadata, streaming, new ArtistGridHealthCapabilityAdapter(metadata, streaming)]);
}
