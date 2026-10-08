using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services;

namespace allstarr.Core.Providers.Deezer;

/// <summary>
/// Exposes the existing Deezer HTTP implementation through the typed capability core.
/// </summary>
public sealed class DeezerMetadataCapabilityAdapter(IConcreteMetadataService legacy)
    : ConcreteMetadataCapabilityAdapter(StableProviderId, legacy)
{
    public const string StableProviderId = "deezer";

    public static ProviderRegistration CreateRegistration(
        DeezerMetadataCapabilityAdapter adapter,
        IProviderPlaylistCapability playlists,
        IProviderDownloadCapability download,
        IProviderStreamingCapability streaming) => new(
        new ProviderDescriptor(
            StableProviderId,
            "Deezer",
            "Public Deezer metadata through the existing Allstarr provider implementation.",
            ProviderOrigin.BuiltIn,
            sdkVersion: "1",
            compatibilityVersion: "legacy-metadata-v1",
            capabilities:
            [
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Metadata,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None,
                    compatibilityVersion: "1",
                    hooks:
                    [
                        "searchTracks",
                        "getTrack",
                        "lookupByIsrc",
                        "searchAlbums",
                        "getAlbum",
                        "searchArtists",
                        "getArtist",
                        "getArtistAlbums",
                        "getArtistTracks"
                    ]),
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Streaming,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required,
                    compatibilityVersion: "1",
                    hooks: ["getStreamLease", "probeStream"],
                    allowedAccountScopes:
                    [
                        ProviderAccountScope.Global,
                        ProviderAccountScope.User,
                        ProviderAccountScope.Library
                    ]),
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Download,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required,
                    compatibilityVersion: "1",
                    hooks: ["checkAvailability", "download"],
                    allowedAccountScopes:
                    [
                        ProviderAccountScope.Global,
                        ProviderAccountScope.User,
                        ProviderAccountScope.Library
                    ]),
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Playlist,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required,
                    compatibilityVersion: "1",
                    hooks: ["getUserPlaylists", "searchPlaylists", "getPlaylistTracks"],
                    allowedAccountScopes:
                    [
                        ProviderAccountScope.Global,
                        ProviderAccountScope.User,
                        ProviderAccountScope.Library
                    ])
            ],
            permissions: new ProviderPermissionDescriptor(
                networkOrigins:
                [
                    new Uri("https://api.deezer.com/"),
                    new Uri("https://media.deezer.com/"),
                    new Uri("https://www.deezer.com/")
                ],
                cache: true)),
        [adapter, playlists, download, streaming]);
}

public sealed class DeezerPlaylistCapabilityAdapter(
    IConcreteMetadataService legacy,
    DeezerMetadataCapabilityAdapter metadata)
    : ConcretePlaylistCapabilityAdapter(DeezerMetadataCapabilityAdapter.StableProviderId, legacy, metadata);
