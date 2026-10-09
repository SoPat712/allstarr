using allstarr.Core.Capabilities;
using allstarr.Core.Storage;

namespace allstarr.Core.Providers.Deezer;

public sealed partial class DeezerProvider
{
    public static ProviderRegistration CreateRegistration(
        IProviderMetadataCapability provider,
        IProviderPlaylistCapability playlists,
        IProviderDownloadCapability download,
        IProviderStreamingCapability streaming) => new(
        new ProviderDescriptor(
            StableProviderId,
            "Deezer",
            "Deezer catalog, playlists, streaming, and managed downloads.",
            ProviderOrigin.BuiltIn,
            sdkVersion: "1",
            compatibilityVersion: "1",
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
                        ProviderAccountScope.Shared,
                        ProviderAccountScope.Personal
                    ]),
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Download,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required,
                    compatibilityVersion: "1",
                    hooks: ["checkAvailability", "download"],
                    allowedAccountScopes:
                    [
                        ProviderAccountScope.Shared,
                        ProviderAccountScope.Personal
                    ]),
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Playlist,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required,
                    compatibilityVersion: "1",
                    hooks: ["getUserPlaylists", "searchPlaylists", "getPlaylistTracks"],
                    allowedAccountScopes:
                    [
                        ProviderAccountScope.Shared,
                        ProviderAccountScope.Personal
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
        [provider, playlists, download, streaming]);
}
