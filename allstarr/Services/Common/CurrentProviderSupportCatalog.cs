using allstarr.Models.Admin;

namespace allstarr.Services.Common;

public static class CurrentProviderSupportCatalog
{
    public const string Supported = "supported";
    public const string Partial = "partial";
    public const string Unavailable = "unavailable";
    public const string PolicyBlocked = "policy_blocked";

    private static readonly string[] CapabilityIds =
    [
        "metadata",
        "streaming",
        "download",
        "playlist",
        "lyrics",
        "health",
        "scrobbling",
        "enrichment",
        "recommendation"
    ];

    public static IReadOnlyList<AdminUiProviderSupport> All { get; } =
    [
        Provider(
            "apple-download",
            "apple-download",
            "Apple Music - Gamdl",
            "global",
            "URL of an optional, operator-managed GAMDL-compatible service.",
            Capability("metadata", Partial, "The bundled gateway supports catalog song, album, and artist search and detail. Playlist and personal-library features remain separate capabilities.", "AppleMusicMetadataServiceTests; AppleDownloadEndpointDiscoveryTests; apple-gateway tests"),
            Capability("streaming", Partial, "The compatible external manifest must advertise single-track audio streaming; no provider range lease or video lane is implemented.", "AppleDownloadEndpointDiscoveryTests; provider contract gap"),
            Capability("download", Partial, "The compatible external manifest must advertise the distinct managed track-artifact route. Album, playlist, library, standalone artwork, and video jobs remain unsupported.", "AppleDownloadCapabilityAdapterTests; AppleDownloadEndpointDiscoveryTests"),
            Capability("lyrics", Supported, "The compatible external manifest must advertise synced lyrics artifacts for single tracks.", "AppleDownloadCapabilityAdapterTests; AppleDownloadEndpointDiscoveryTests"),
            Capability("health", Partial, "Runtime discovery verifies the gateway API version, authentication, health, and each advertised feature without treating a raw wrapper as a gateway.", "AppleDownloadEndpointDiscoveryTests; AppleMusicControllerTests")),
        Provider(
            "apple-musickit",
            "apple-musickit",
            "Apple Music",
            "user",
            "Developer token plus a per-user Music User Token stored in the selected encrypted account secret.",
            Capability("playlist", Supported, "Account-bound MusicKit library playlist paging, snapshots, artwork, matching, virtual reads, and backend materialization.", "AppleMusicKitPlaylistCapabilityAdapterTests; PlaylistOrchestrationIntegrationTests"),
            Capability("metadata", Supported, "Account-bound personal-library song, album, and artist search and lookups with deterministic paging. Catalog and ISRC lookup remain outside this capability.", "AppleMusicKitMetadataCapabilityAdapterTests")),
        Provider(
            "deezer",
            "deezer",
            "Deezer",
            "mixed",
            "Public metadata; an ARL is required on each managed account used for download or stream work.",
            Capability("metadata", Supported, "Catalog song, album, artist, playlist, and ISRC operations.", "DeezerMetadataServiceTests"),
            Capability("streaming", Partial, "Download-backed playback; no typed range lease.", "DeezerDownloadServiceTests"),
            Capability("download", Supported, "Account-bound encrypted transfer and decryption use a typed host-owned workspace with size, checksum, progress, cancellation, cleanup, and retry contracts.", "DirectProviderDownloadCapabilityAdapterTests; ProviderDownloadArtifactResolverTests"),
            Capability("playlist", Supported, "Read/discovery only; no provider-neutral write contract.", "DeezerMetadataServiceTests"),
            Capability("health", Partial, "Account-scoped metadata, playlist, stream, and download probes with durable capability samples.", "ProviderStatusManagerTests; ConfigControllerAuthorizationTests")),
        Provider(
            "qobuz",
            "qobuz",
            "Qobuz",
            "mixed",
            "A user token and user ID belong to each managed account used for download work.",
            Capability("metadata", Supported, "Catalog song, album, artist, playlist, and paged artist-track reads.", "QobuzMetadataServiceTests"),
            Capability("streaming", Partial, "Download-backed playback; no typed range lease.", "QobuzDownloadServiceTests"),
            Capability("download", Supported, "Account-bound signed downloads use a typed host-owned workspace with media facts, size, checksum, progress, cancellation, cleanup, and retry contracts.", "DirectProviderDownloadCapabilityAdapterTests; ProviderDownloadArtifactResolverTests"),
            Capability("playlist", Partial, "Read/discovery only.", "QobuzMetadataServiceTests"),
            Capability("health", Partial, "Account-scoped metadata, playlist, stream, and download probes with durable capability samples.", "ProviderStatusManagerTests; ConfigControllerAuthorizationTests")),
        Provider(
            "squidwtf",
            "squidwtf",
            "SquidWTF",
            "none",
            "Discovered public metadata endpoint; uptime feed is optional.",
            Capability("metadata", Partial, "Tidal-shaped catalog metadata through discovered endpoints.", "SquidWTFMetadataServiceTests"),
            Capability("streaming", PolicyBlocked, "Quarantined until a working endpoint and contract fixture exist.", "ProviderStatusManagerTests"),
            Capability("download", PolicyBlocked, "Quarantined until a working endpoint and contract fixture exist.", "ProviderStatusManagerTests"),
            Capability("playlist", PolicyBlocked, "Not routed as a current playlist source.", "ProviderStatusManagerTests"),
            Capability("health", Partial, "Metadata endpoint discovery only.", "provider health gap")),
        Provider(
            "spotify",
            "spotify",
            "Spotify",
            "user",
            "A selected managed account cookie is resolved from its encrypted provider-account secret.",
            Capability("metadata", Unavailable, "No generic IConcreteMetadataService is registered.", "none (unsupported)"),
            Capability("playlist", Supported, "Account-bound source paging, snapshots, artwork, provider-neutral matching, virtual reads, and manual/scheduled Jellyfin or Navidrome materialization.", "SpotifyPlaylistCapabilityAdapterTests; PlaylistOrchestrationIntegrationTests; VirtualPlaylistProtocolAdapterTests"),
            Capability("lyrics", Partial, "Optional Spotify lyrics sidecar path.", "lyrics contract gap"),
            Capability("health", Partial, "Account-scoped playlist cookie probe; the optional lyrics lane has no direct probe yet.", "ProviderStatusManagerTests; ConfigControllerAuthorizationTests")),
        Provider(
            "musicbrainz",
            "musicbrainz",
            "MusicBrainz",
            "none",
            "Meaningful User-Agent/contact and responsible rate limiting.",
            Capability("metadata", Unavailable, "Not registered as a general search/playback metadata provider.", "none (unsupported)"),
            Capability("enrichment", Supported, "Managed-file identity, credits, release facts, genres, and deterministic tag planning.", "MetadataEnrichmentTests; TrackIdentityServiceTests"),
            Capability("recommendation", Supported, "Verified MusicBrainz relationships improve habit-seeded local similarity; MusicBrainz is not presented as a personalized remote service.", "RecommendationSourceAdapterTests; IntelligenceCoreTests")),
        Provider(
            "lastfm",
            "lastfm",
            "Last.fm",
            "user",
            "Shared API credentials plus an exact user-scoped encrypted session-key reference.",
            Capability("scrobbling", Supported, "Durable Jellyfin and Subsonic playback delivery with idempotent checkpoints and exact user/account scope.", "PlaybackSignalPipelineTests; ScrobblingAdminControllerTests"),
            Capability("recommendation", Supported, "Current listening habits seed bounded similar-track requests with retained explanations.", "RecommendationSourceAdapterTests; IntelligenceCoreTests"),
            Capability("health", Partial, "Connection testing and readiness are exposed; provider failure degrades only this target/source.", "ScrobblingAdminControllerTests; RecommendationSourceAdapterTests")),
        Provider(
            "listenbrainz",
            "listenbrainz",
            "ListenBrainz",
            "user",
            "Exact user-scoped encrypted token reference.",
            Capability("scrobbling", Supported, "Durable Jellyfin and Subsonic playback delivery with idempotent checkpoints and exact user/account scope.", "PlaybackSignalPipelineTests; ScrobblingAdminControllerTests"),
            Capability("recommendation", Supported, "Collaborative-filtering recording recommendations join the same explained, scoped candidate pipeline.", "RecommendationSourceAdapterTests; IntelligenceCoreTests"),
            Capability("health", Partial, "Token validation and source readiness are exposed; provider failure remains isolated.", "ScrobblingAdminControllerTests; RecommendationSourceAdapterTests")),
        Provider(
            "lyricsplus",
            "lyricsplus",
            "LyricsPlus",
            "none",
            "Optional sidecar URL.",
            Capability("lyrics", Partial, "Built-in lyrics orchestrator source; sidecar contract is not fully characterized.", "lyrics contract gap")),
        Provider(
            "lrclib",
            "lrclib",
            "LRCLib",
            "none",
            "Public API.",
            Capability("lyrics", Partial, "Built-in lyrics orchestrator source.", "LrclibServiceTests")),
        Provider(
            "extensions",
            "extensions",
            "Trusted JavaScript extensions",
            "mixed",
            "Verified SDK v1 package, explicit permission review, declared account scopes, and staged activation.",
            Capability("metadata", Supported, "Typed search and direct-get hooks run through the permissioned SDK adapter and the shared Jellyfin/Subsonic protocol provider gateway.", "ExtensionCapabilityAdapterTests; ExtensionSdkV1Tests; ProtocolProviderGatewayContractTests"),
            Capability("streaming", Supported, "Typed stream leases route through the shared Jellyfin/Subsonic provider gateway with network and secret permissions; signed source URLs stay server-side and ranges are forwarded only when advertised.", "ExtensionCapabilityAdapterTests; ProviderRouterTests; ProtocolProviderGatewayContractTests"),
            Capability("download", Supported, "Typed download hooks stream approved HTTPS responses through the host-owned artifact broker into the exact durable job workspace; host-derived IDs, checksums, size limits, cancellation, and lineage are enforced.", "ExtensionCapabilityAdapterTests; ProviderDownloadArtifactResolverTests"),
            Capability("playlist", Supported, "Typed playlist discovery, item paging, and permissioned artwork resolution are available; provider mutation remains host-only.", "ExtensionCapabilityAdapterTests; PlaylistOrchestrationIntegrationTests"),
            Capability("lyrics", Partial, "Typed lyrics lookup is available through the permissioned capability adapter; legacy protocol lyrics orchestration still has built-in-only paths.", "ExtensionCapabilityAdapterTests; ExtensionSdkV1Tests; protocol exposure gap"),
            Capability("health", Supported, "Account-aware health hooks feed the same provider health path.", "ExtensionCapabilityAdapterTests; ProviderStatusManagerTests"))
    ];

    private static AdminUiProviderSupport Provider(
        string id,
        string? runtimeId,
        string name,
        string accountScope,
        string configuration,
        params AdminUiCapabilitySupport[] overrides)
    {
        var byId = overrides.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        return new AdminUiProviderSupport
        {
            Id = id,
            RuntimeId = runtimeId,
            Name = name,
            AccountScope = accountScope,
            Configuration = configuration,
            Capabilities = CapabilityIds
                .Select(capability => byId.GetValueOrDefault(capability) ?? Capability(
                    capability,
                    Unavailable,
                    "No current Allstarr adapter.",
                    "none (unsupported)"))
                .ToList()
        };
    }

    private static AdminUiCapabilitySupport Capability(
        string id,
        string state,
        string protocolLimit,
        string testCoverage) => new()
        {
            Id = id,
            State = state,
            ProtocolLimit = protocolLimit,
            TestCoverage = testCoverage
        };
}
