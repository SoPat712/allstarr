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
            "apple-musickit", "apple-musickit", "Apple Music", "user",
            "Personal Media User Token and storefront; public catalog access requires no user token.",
            Capability("metadata", Supported, "Public catalog search, ISRC, track/album/artist detail and paging; private library lookups use the selected account. Web-player authorization is fetched automatically.", "AppleMusicKitMetadataCapabilityAdapterTests; AppleMusicClientTests; AppleWebTokenProviderTests"),
            Capability("playlist", Supported, "Public playlists and selected personal-library playlists, with ordered paging and bounded artwork. Source writeback is unavailable.", "AppleMusicKitPlaylistCapabilityAdapterTests; AppleMusicClientTests"),
            Capability("streaming", Supported, "Optional gateway API 2 streams with the exact selected account. Cold progressive responses do not advertise byte ranges.", "AppleDownloadCapabilityAdapterTests; ProtocolProviderStreamingGatewayTests"),
            Capability("download", Partial, "Optional account-bound managed track artifacts. Wrapper-backed audio requires the selected account to match the wrapper account.", "AppleDownloadCapabilityAdapterTests; apple-gateway tests"),
            Capability("lyrics", Supported, "Optional account-bound synced lyrics artifacts for single tracks.", "AppleDownloadCapabilityAdapterTests; apple-gateway tests"),
            Capability("health", Partial, "Public web-token failures appear on the account card. Optional gateway discovery checks API version, authentication and advertised features.", "AppleMusicClientTests; AppleDownloadEndpointDiscoveryTests")),
        Provider(
            "deezer",
            "deezer",
            "Deezer",
            "mixed",
            "Public metadata; an ARL is required on each managed account used for download or stream work.",
            Capability("metadata", Supported, "Catalog song, album, artist, playlist, and ISRC operations.", "DeezerProviderTests"),
            Capability("streaming", Supported, "Selected-account streams use an exact-provider typed lease and incremental 2 KiB decryption without buffering the full track or advertising ranges.", "DirectProviderDownloadCapabilityAdapterTests; ProtocolProviderStreamingGatewayTests"),
            Capability("download", Supported, "Account-bound encrypted transfer and decryption use a typed host-owned workspace with size, checksum, progress, cancellation, cleanup, and retry contracts.", "DirectProviderDownloadCapabilityAdapterTests; ProviderDownloadArtifactResolverTests"),
            Capability("playlist", Supported, "Read/discovery only; no provider-neutral write contract.", "DeezerProviderTests"),
            Capability("health", Partial, "Account-scoped metadata, playlist, stream, and download probes with durable capability samples.", "ProviderStatusManagerTests; ConfigControllerAuthorizationTests")),
        Provider(
            "qobuz",
            "qobuz",
            "Qobuz",
            "mixed",
            "A user token and user ID belong to each managed account used for stream or download work.",
            Capability("metadata", Supported, "Catalog song, album, artist, playlist, and paged artist-track reads.", "QobuzProviderTests"),
            Capability("streaming", Supported, "Selected-account signed streams use an exact-provider typed lease and preserve real upstream byte ranges and media facts.", "DirectProviderDownloadCapabilityAdapterTests; ProtocolProviderStreamingGatewayTests"),
            Capability("download", Supported, "Account-bound signed downloads use a typed host-owned workspace with media facts, size, checksum, progress, cancellation, cleanup, and retry contracts.", "DirectProviderDownloadCapabilityAdapterTests; ProviderDownloadArtifactResolverTests"),
            Capability("playlist", Partial, "Read/discovery only.", "QobuzProviderTests"),
            Capability("health", Partial, "Account-scoped metadata, playlist, stream, and download probes with durable capability samples.", "ProviderStatusManagerTests; ConfigControllerAuthorizationTests")),
        Provider(
            "spotify",
            "spotify",
            "Spotify",
            "user",
            "A selected managed account cookie is resolved from its encrypted provider-account secret.",
            Capability("metadata", Unavailable, "No metadata capability is registered.", "none (unsupported)"),
            Capability("playlist", Supported, "Account-bound source paging, snapshots, artwork, provider-neutral matching, virtual reads, and manual/scheduled Jellyfin or Navidrome materialization.", "SpotifyPlaylistCapabilityAdapterTests; PlaylistOrchestrationIntegrationTests; VirtualPlaylistProtocolAdapterTests"),
            Capability("lyrics", Supported, "Typed optional Spotify lyrics sidecar preserves plain/timed content, source, and stable content revision through shared Jellyfin/Subsonic routing.", "BuiltInLyricsCapabilityAdapterTests; ProtocolLyricsResolverTests"),
            Capability("health", Partial, "Playlist and optional lyrics probes remain independently sampled, so one lane does not mask the other.", "ProviderStatusManagerTests; ConfigControllerAuthorizationTests")),
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
            "Exact user-scoped encrypted token with an optional HTTPS Koito listening address.",
            Capability("scrobbling", Supported, "Durable Jellyfin and Subsonic playback delivery with idempotent checkpoints and exact user/account scope.", "PlaybackSignalPipelineTests; ScrobblingAdminControllerTests"),
            Capability("recommendation", Supported, "Collaborative-filtering recording recommendations join the same explained, scoped candidate pipeline.", "RecommendationSourceAdapterTests; IntelligenceCoreTests"),
            Capability("health", Partial, "Token validation and source readiness are exposed; provider failure remains isolated.", "ScrobblingAdminControllerTests; RecommendationSourceAdapterTests")),
        Provider(
            "lrclib",
            "lrclib",
            "LRCLib",
            "none",
            "Public API.",
            Capability("lyrics", Supported, "Typed metadata lookup preserves plain/timed content, source, and stable content revision through shared Jellyfin/Subsonic routing.", "BuiltInLyricsCapabilityAdapterTests; ProtocolLyricsResolverTests; LrclibServiceTests")),
        Provider(
            "extensions",
            "extensions",
            "Trusted JavaScript extensions",
            "mixed",
            "Verified SDK v1 package, explicit permission review, declared account scopes, and staged activation.",
            Capability("metadata", Supported, "Typed search and direct-get hooks run through the permissioned SDK adapter and the shared Jellyfin/Subsonic protocol provider gateway.", "ExtensionCapabilityAdapterTests; ExtensionSdkV1Tests; ProtocolRouteFixtureTests"),
            Capability("streaming", Supported, "Typed stream leases route through the shared Jellyfin/Subsonic provider gateway with network and secret permissions; signed source URLs stay server-side and ranges are forwarded only when advertised.", "ExtensionCapabilityAdapterTests; ProviderRouterTests; ProtocolRouteFixtureTests; ProtocolStreamingResponseAdapterTests"),
            Capability("download", Supported, "Typed download hooks stream approved HTTPS responses through the host-owned artifact broker into the exact durable job workspace; host-derived IDs, checksums, size limits, cancellation, and lineage are enforced.", "ExtensionCapabilityAdapterTests; ProviderDownloadArtifactResolverTests"),
            Capability("playlist", Supported, "Typed playlist discovery, item paging, and permissioned artwork resolution are available; provider mutation remains host-only.", "ExtensionCapabilityAdapterTests; PlaylistOrchestrationIntegrationTests"),
            Capability("lyrics", Supported, "Permissioned typed lyrics lookup reaches both Jellyfin and Subsonic through the shared scoped protocol resolver.", "ExtensionCapabilityAdapterTests; ExtensionSdkV1Tests; ProtocolLyricsResolverTests"),
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
