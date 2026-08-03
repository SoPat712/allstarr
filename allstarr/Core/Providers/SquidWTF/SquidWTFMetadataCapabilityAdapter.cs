using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services;
using allstarr.Services.SquidWTF;

namespace allstarr.Core.Providers.SquidWTF;

public sealed class SquidWTFMetadataCapabilityAdapter(IConcreteMetadataService legacy)
    : ConcreteMetadataCapabilityAdapter("squidwtf", legacy)
{
    public static ProviderRegistration CreateRegistration(
        SquidWTFMetadataCapabilityAdapter adapter) => new(
        new ProviderDescriptor(
            "squidwtf",
            "SquidWTF",
            "Discovered public metadata; stream, download, and playlists remain policy-blocked.",
            ProviderOrigin.BuiltIn,
            sdkVersion: "1",
            compatibilityVersion: "squidwtf-metadata-v1",
            capabilities:
            [
                new(
                    ProviderCapabilityKind.Metadata,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None,
                    compatibilityVersion: "1",
                    hooks:
                    [
                        "searchTracks", "getTrack", "lookupByIsrc", "searchAlbums", "getAlbum",
                        "searchArtists", "getArtist", "getArtistAlbums", "getArtistTracks"
                    ]),
                Unavailable(ProviderCapabilityKind.Streaming),
                Unavailable(ProviderCapabilityKind.Download),
                new(
                    ProviderCapabilityKind.Playlist,
                    ProviderCapabilitySupportState.Unavailable,
                    ProviderAccountRequirement.Required,
                    compatibilityVersion: "policy-blocked-v1",
                    allowedAccountScopes:
                    [
                        ProviderAccountScope.Global,
                        ProviderAccountScope.User,
                        ProviderAccountScope.Library
                    ]),
                new(
                    ProviderCapabilityKind.Health,
                    ProviderCapabilitySupportState.ConfiguredOnly,
                    ProviderAccountRequirement.None,
                    compatibilityVersion: "discovery-health-v1")
            ],
            new ProviderPermissionDescriptor()),
        [adapter]);

    private static ProviderCapabilityDescriptor Unavailable(ProviderCapabilityKind capability) => new(
        capability,
        ProviderCapabilitySupportState.Unavailable,
        ProviderAccountRequirement.None,
        compatibilityVersion: "policy-blocked-v1");
}

public static class SquidWTFMetadataCapabilityRegistration
{
    public static IServiceCollection AddSquidWTFMetadataCapability(this IServiceCollection services)
    {
        services.AddSingleton<SquidWTFMetadataCapabilityAdapter>(provider => new(
            provider.GetRequiredService<SquidWTFMetadataService>()));
        services.AddSingleton<ProviderRegistration>(provider =>
            SquidWTFMetadataCapabilityAdapter.CreateRegistration(
                provider.GetRequiredService<SquidWTFMetadataCapabilityAdapter>()));
        return services;
    }
}
