import { expect, test, type Page } from "@playwright/test";

declare global {
  interface Window {
    __allstarrMetrics: { cls: number; lcp: number; navigation: number };
    __emitAllstarrUpdate?: () => void;
  }
}

const viewports = [
  { width: 390, height: 844 },
  { width: 1280, height: 800 },
];

const schema = {
  activeBackend: "Jellyfin",
  providers: [
    { id: "jellyfin", name: "Jellyfin", categories: ["streaming"] },
    {
      id: "lumen-audio", name: "Lumen Audio", categories: ["metadata", "streaming"],
      accountSettings: [
        { key: "token", label: "Access token", type: "password", sensitive: true, required: true },
        { key: "region", label: "Region", type: "select", options: ["us", "ca"], defaultValueJson: '"us"' },
      ],
    },
    {
      id: "audiomuse-ai", name: "AudioMuse", categories: ["intelligence"],
      accountSettings: [
        { key: "baseUrl", label: "AudioMuse server URL", type: "url", required: true },
        { key: "token", label: "AudioMuse access token", type: "password", sensitive: true, required: true },
      ],
    },
    { id: "listenbrainz", name: "ListenBrainz", categories: ["scrobbling"] },
    {
      id: "apple-download", name: "Apple Music – GAMDL", categories: ["metadata", "streaming", "download"],
      connectionKind: "operator_managed",
      configSchema: [
        { key: "APPLE_DOWNLOAD_URL", label: "External provider URL", type: "url", valuePath: "appleDownload.baseUrl" },
      ],
    },
  ],
  configSections: [
    {
      id: "general", label: "General", fields: [
      { key: "AUDIO_QUALITY", label: "Audio quality", type: "audio-quality", valuePath: "audio.quality" },
      { key: "MATCHING_LOCAL_PREFERENCE_PERCENT", label: "Local track preference", type: "number", valuePath: "matching.localPreferencePercent", min: 0, max: 20, helpText: "Percentage points added to Jellyfin-local candidates. Default: 7%." },
      { key: "MATCHING_EXTENSION_PENALTY_PERCENT", label: "Extension match penalty", type: "number", valuePath: "matching.extensionPenaltyPercent", min: 0, max: 20, helpText: "Percentage points subtracted from extension candidates. Default: 3%." },
      { key: "STORAGE_MODE", label: "Storage mode", type: "select", valuePath: "library.storageMode", options: ["Permanent", "Cache"] },
      { key: "PublicUrl", label: "Public URL", type: "text", valuePath: "deployment.url", ownership: "deployment", readOnly: true },
      ],
    },
    {
      id: "cache", label: "Cache", fields: [
        { key: "CACHE_DURATION_HOURS", label: "Track cache hours", type: "number", valuePath: "library.cacheDurationHours", min: 1 },
        { key: "CACHE_SEARCH_RESULTS_MINUTES", label: "Search results minutes", type: "number", valuePath: "cache.searchResultsMinutes", min: 1, max: 1440 },
        { key: "CACHE_TRANSCODE_MINUTES", label: "Transcode cache minutes", type: "number", valuePath: "cache.transcodeCacheMinutes", min: 1 },
        { key: "CACHE_MEDIA_MAXIMUM_MEGABYTES", label: "Media cache total MiB", type: "number", valuePath: "cache.mediaMaximumMegabytes", ownership: "deployment", readOnly: true },
      ],
    },
  ],
  priorityGroups: [{
    id: "streaming", label: "Playback", envKey: "StreamingOrder", providers: ["lumen-audio", "future-audio"],
    pinnedProvider: { id: "jellyfin", name: "Jellyfin", icon: "server", reason: "Local media server" },
  }],
};

const responses: Record<string, unknown> = {
  "/api/admin/auth/me": {
    authenticated: true, backend: "Jellyfin",
    user: {
      id: "user", name: "Tester", isAdministrator: true,
      avatarUrl: "/api/admin/auth/me/avatar?user=user",
    },
  },
  "/api/admin/onboarding/status": {
    completed: true, setupOpen: false, shouldRedirectToSetup: false,
    schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"],
    completionSource: "setup-guide", completedAt: "2026-01-01", revision: 2,
    recoveryNotices: [],
    migration: { available: true, completed: false, firstRun: true },
  },
  "/api/admin/onboarding/complete": {
    completed: true, setupOpen: false, shouldRedirectToSetup: false,
    schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"],
    completionSource: "setup-guide", completedAt: "2026-01-01", revision: 2,
    recoveryNotices: [],
    migration: { available: true, completed: false, firstRun: true },
  },
  "/api/admin/onboarding/reopen": {
    completed: false, setupOpen: true, shouldRedirectToSetup: false,
    schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"],
    completionSource: "administrator", reopenedAt: "2026-01-01", revision: 3,
    recoveryNotices: [],
    migration: { available: true, completed: false, firstRun: true },
  },
  "/api/admin/ui/schema": schema,
  "/api/admin/status": { version: "test", backendType: "Jellyfin" },
  "/api/admin/playlists": { playlists: [], inventory: { managed: 0, unmanaged: 0 } },
  "/api/admin/jobs?limit=100": {
    jobs: [{
      id: "11111111-1111-1111-1111-111111111111", type: "playlist.materialize",
      state: "Running", attemptCount: 1, failureCount: 0, deferralCount: 0,
      cancellationRequestedAt: null, lastErrorCode: null, lastErrorMessage: null,
      updatedAt: "2026-01-01",
    }],
    progress: [{
      id: "progress", jobId: "11111111-1111-1111-1111-111111111111",
      action: "playlist.match", outcome: "running", createdAt: "2026-01-01",
      detailsJson: JSON.stringify({
        stage: "playlist.match", message: "Matching Test song", completed: 1, total: 2,
        provider: "lumen-audio", playlist: "Test playlist", track: "Test song",
        throughputPerSecond: 1.5,
      }),
    }],
  },
  "/api/admin/ui/activity?limit=8": {
    items: [{
      id: "activity", kind: "playlist", source: "lumen-audio",
      label: "playlist check", state: "succeeded", detail: "66 ms",
      occurredAt: "2026-01-01",
    }],
    hasMore: false,
  },
  "/api/admin/ui/provider-summaries": {
    providers: [{
      providerId: "lumen-audio", connectedAccountName: "Legacy .env import",
      enabledAccountCount: 1, capabilityTotal: 2, healthyCapabilityCount: 2,
      failedCapabilityCount: 0, lastCheckedAt: "2026-01-01",
    }],
  },
  "/api/admin/ui/home": {
    schema,
    status: { version: "test", backendType: "Jellyfin", durableStorage: { readiness: "Ready" } },
    stats: {
      linkedPlaylists: 1, playableTracks: 1, unresolvedTracks: 1, activeJobs: 1,
      completedListens: 12, currentWeekListens: 24, previousWeekListens: 20,
      scrobbleDeliveries: 2, cacheTracks: 3, keptTracks: 4,
      topArtist: { name: "Beyoncé", listens: 7 },
    },
    providerHealth: {
      providers: [{
        providerId: "lumen-audio", connectedAccountName: "Legacy .env import",
        enabledAccountCount: 1, capabilityTotal: 2, healthyCapabilityCount: 2,
        failedCapabilityCount: 0, lastCheckedAt: "2026-01-01",
      }],
    },
    activity: {
      items: [{
        id: "activity", kind: "playlist", source: "lumen-audio",
        label: "playlist check", state: "succeeded", detail: "66 ms",
        occurredAt: "2026-01-01",
      }],
      hasMore: false,
    },
  },
  "/api/admin/ui/now-playing": {
    items: [{
      deviceId: "device-1", userId: "user-1", userName: "Tester",
      avatarUrl: null, client: "Feishin", device: "Desktop",
      itemId: "ext-lumen-audio-song-1", title: "Rocket", artist: "Beyoncé",
      album: "Act II", providerId: "lumen-audio", artworkUrl: null,
      positionSeconds: 30, durationSeconds: 120, progress: 0.25,
      lastActivity: "2026-01-01", scrobbleThresholdSeconds: 60,
      scrobbleEligible: false, scrobbleDeliveries: [], scrobbled: false,
    }],
  },
  "/api/admin/playlist-links": {
    playlistLinks: [{
      id: "playlist-link", enabled: true, name: "Test playlist",
      sourceProviderId: "lumen-audio", sourcePlaylistId: "source-list",
      providerAccountId: "lumen", libraryScopeId: "music",
      targetProtocol: "jellyfin", targetBackendInstanceId: "main",
      targetCredentialReferenceId: "33333333-3333-3333-3333-333333333333",
      targetPlaylistId: "jellyfin-playlist", mode: "hybrid", projectionMode: "resolved",
      materializationMode: "reconcile", mirrorStaleEntries: false,
      preserveManualEntries: true, syncName: true, syncDescription: true, syncArtwork: true,
      ruleVersion: "playlist-rules-v1", policyVersion: "playlist-policy-v1",
      revision: 1, artworkUrl: "/missing-playlist-art",
      trackCount: 2,
      matchedCount: 0, unmatchedCount: 1, playableCount: 1, materializedCount: 0,
      routeCoverage: [{ providerId: "lumen-audio", count: 1 }],
      metrics: { total: 2, matched: 0, unresolved: 1, review: 1, rejected: 0, playable: 1, materialized: 0 },
    }],
  },
  "/api/admin/playlist-sources": {
    accounts: [
      { id: "qobuz", providerId: "qobuz", displayName: "Qobuz", accessLabel: "Personal account" },
      { id: "spotify", providerId: "spotify", displayName: "Spotify", ownerDisplayName: "Tester", accessLabel: "Personal account" },
      { id: "lumen", providerId: "lumen-audio", displayName: "Lumen", accessLabel: "Personal account" },
      { id: "subsonic", providerId: "subsonic", displayName: "Subsonic", accessLabel: "Library-shared account" },
      { id: "jellyfin", providerId: "jellyfin", displayName: "Jellyfin", accessLabel: "Library-shared account" },
    ],
    blockedAccounts: [],
    providers: [
      { id: "jellyfin", displayName: "Jellyfin" },
      { id: "subsonic", displayName: "Subsonic" },
      { id: "lumen-audio", displayName: "Lumen Audio" },
      { id: "qobuz", displayName: "Qobuz" },
      { id: "spotify", displayName: "Spotify" },
    ],
  },
  "/api/admin/media-targets": {
    targets: [{
      id: "22222222-2222-2222-2222-222222222222", protocol: "jellyfin", backendInstanceId: "main",
      libraryScopeId: "music", displayName: "Jellyfin Music",
      credentialReferenceId: "33333333-3333-3333-3333-333333333333",
    }],
  },
  "/api/admin/provider-accounts": {
    managementMode: "ApplicationManaged",
    audienceUsers: [
      { id: "user", displayName: "Tester" },
      { id: "listener", displayName: "Listener" },
    ],
    accounts: [{
      id: "account", providerId: "lumen-audio", displayName: "Lumen account",
      sourceDisplayName: "Lumen Audio", scope: "User", enabled: true, revision: 1,
      ownerUserId: "user", ownerDisplayName: "Tester", createdByUserId: "user",
      creatorDisplayName: "Tester",
      configuration: { region: "ca" }, configuredFields: ["token", "region"],
      secret: { configured: true, revoked: false }, createdAt: "2026-01-01", updatedAt: "2026-01-01",
    }],
  },
  "/api/admin/providers/status": [
    {
      provider: "lumen-audio", providerAccountId: "account", providerAccountName: "Lumen account",
      capability: "metadata", accountScope: "user", supported: true, enabled: true,
      configuration: "configured", health: "healthy", ready: true, canAttempt: true, canTest: true,
    },
    {
      provider: "lumen-audio", providerAccountId: "account", providerAccountName: "Lumen account",
      capability: "streaming", accountScope: "user", supported: true, enabled: true,
      configuration: "configured", health: "degraded", ready: false, canAttempt: true, canTest: true,
      reasonCode: "probe_failed",
    },
  ],
  "/api/admin/provider-diagnostics/deep-stream/latest": { measurements: [] },
  "/api/admin/apple-download/status": {
    state: "ready", ready: true, staged: true, daemon_running: true,
    wrapper_healthy: true, logged_in: true, login_state: "authenticated", api_version: "2",
  },
  "/api/admin/config": {
    deployment: { url: "https://music.example.test" },
    audio: { quality: "BestAvailable" },
    matching: { localPreferencePercent: 7, extensionPenaltyPercent: 3 },
    appleDownload: { baseUrl: "http://apple-download.test" },
    library: { storageMode: "Cache", cacheDurationHours: 24 },
    cache: { searchResultsMinutes: 1, mediaMaximumMegabytes: 512, transcodeCacheMinutes: 60 },
    providers: { streamingOrder: "lumen-audio" },
  },
  "/api/admin/storage": { storage: { provider: "PostgreSQL", readiness: "Ready" }, backups: [] },
  "/api/admin/cache": {
    database: { entryCount: 2, payloadBytes: 2048, hitRatio: .75, hits: 3, misses: 1, writes: 2, evictions: 1 },
    hot: { entryCount: 1, payloadBytes: 1024, maximumBytes: 16_777_216, hitRatio: .75, hits: 3, misses: 1, writes: 1, evictions: 0 },
    media: { entryCount: 1, payloadBytes: 4096, maximumBytes: 536_870_912, maximumEntryBytes: 16_777_216, hitRatio: .5, hits: 1, misses: 1, writes: 1, evictions: 1 },
    categories: [{
      category: "Artwork", owner: "media-assets", storageTier: "Media", enabled: true,
      entryCount: 1, payloadBytes: 4096, freshSeconds: 86_400, staleSeconds: 3600,
      maximumBytes: 536_870_912, maximumEntries: 10_000,
      warmingRule: "VisibleOrSelected", invalidationTrigger: "resource-or-artwork-revision",
    }],
    activity: { coalescedRequests: 4, staleServes: 2, upstreamBytesAvoided: 8192 },
    artworkLimits: { maximumEntryBytes: 16_777_216, maximumDecodedPixels: 16_000_000 },
    extensionStorage: { activeExtensions: 1, entryCount: 2, payloadBytes: 512, maximumBytes: 4_194_304 },
    capturedAt: "2026-01-01",
  },
  "/api/admin/cache/maintenance/preview": {
    metadata: { scannedEntries: 2, scanLimitReached: false, expiredEntries: 1, unknownOwnerEntries: 0, disabledCategoryEntries: 0, noExpiryEntries: 0, staleAuthorizationScopeEntries: 0, supersededEntries: 0, overQuotaEntries: 0, reclaimableBytes: 1024 },
    media: { scannedFiles: 2, scanLimitReached: false, temporaryFiles: 0, malformedMetadataFiles: 0, orphanedMetadataFiles: 0, orphanedPayloadFiles: 0, expiredEntries: 0, noExpiryEntries: 0, overQuotaEntries: 0, reclaimableBytes: 0, cleanupIntervalSeconds: 900, lastCleanupAt: "2026-01-01", lastCleanupDeletedEntries: 1 },
    unreferencedArtworkPayloads: 1, unreferencedArtworkBytes: 4096,
    artworkReferenceScanLimitReached: false,
  },
  "/api/admin/config/migration/status": {
    available: true, completed: false, sourcePresent: false, firstRun: true,
  },
  "/api/admin/preview-selective-state": {
    canImport: true,
    dependencies: ["Settings", "Accounts"],
    conflicts: [],
    report: {
      includedCategories: ["Accounts", "Playlists"],
      excludedCategories: [],
      totalRows: 3,
      rowsByEntry: { "provider-accounts": 1, "playlist-links": 2 },
    },
  },
  "/api/admin/import-selective-state": {
    success: true,
    message: "Selective state imported.",
    report: {
      includedCategories: ["Accounts", "Playlists"],
      excludedCategories: [],
      totalRows: 3,
      rowsByEntry: { "provider-accounts": 1, "playlist-links": 2 },
    },
  },
  "/api/admin/extensions/packages": [{
    id: "package", extensionId: "lumen-audio", displayName: "Lumen Audio", version: "1.0.0",
    lifecycle: "active", state: "active", active: true, installed: true,
    permissionReviewRequired: false, capabilities: ["metadata", "streaming"], revision: 1,
  }],
  "/api/admin/extensions/registries": [],
  "/api/admin/extensions/store": { items: [], errors: [] },
  "/api/admin/extensions/logs?limit=100": [],
  "/api/admin/intelligence": {
    state: "configured",
    scope: { protocol: "jellyfin", backendInstanceId: "main", libraryScopeId: "music" },
    policy: { enabled: true, retentionDays: 30, revision: 1, targetCredentialReferenceId: "33333333-3333-3333-3333-333333333333", targetCredentialConfigured: true },
    availableSignalTypes: [{ id: "play", label: "Play", enabled: true }],
    providers: [{
      id: "lumen-audio", label: "Lumen Audio", description: "Private similarity source.",
      enabled: true, available: true, state: "ready",
    }, {
      id: "audiomuse-ai", label: "AudioMuse", description: "Explore this library by sound.",
      enabled: true, available: true, state: "ready",
    }],
    listeningServices: [
      { id: "lastfm", label: "Last.fm", configured: true, latestState: "delivered", requiresReauthentication: false },
      { id: "listenbrainz", label: "ListenBrainz", configured: false, latestState: null, requiresReauthentication: false },
    ],
    songDetails: { pending: 2, resolved: 8, unresolved: 0, failed: 0 },
    actions: {
      canRun: true, canGenerate: true, latestRunId: "run-1", latestRunState: "running",
      latestJobId: "job-1", attemptCount: 1, failureCount: 0, maxAttempts: 5,
      canCancel: true, progress: {
        stage: "recommendation.provider", message: "Searching Lumen Audio.",
        completed: 1, total: 2, provider: "lumen-audio", track: "Future Song",
      },
    },
    candidates: [{
      id: "candidate-1", trackKey: "track-1", title: "Future Song", artist: "Artist",
      album: "Album", score: .91, source: "lumen-audio", providerId: "lumen-audio",
      sourceRevision: "fixture:1", revision: 1, explanations: [{
        code: "similar", weight: .9, explanation: "Similar to recent listening.",
      }], exclusions: [], feedback: null,
    }, {
      id: "candidate-2", trackKey: "track-2", title: "Second Song", artist: "Another Artist",
      album: "Second Album", score: .84, source: "audiomuse-ai", providerId: "audiomuse-ai",
      sourceRevision: "fixture:1", revision: 1, explanations: [{
        code: "similar", weight: .8, explanation: "A nearby sound in this library.",
      }], exclusions: [], feedback: null,
    }],
    generatedSets: [{
      id: "set-1", name: "Morning discovery", trackCount: 25, state: "succeeded", materialized: true,
    }],
    schedules: [{
      id: "schedule-1", cronExpression: "0 8 * * 1", timeZoneId: "America/New_York",
      overlapPolicy: "skip", misfirePolicy: "runOnce", enabled: true,
      nextRunAt: "2026-01-05T13:00:00Z", revision: 1, name: "Monday discoveries", limit: 25,
    }],
    visualization: [{ key: "plays", label: "Plays", value: .7 }],
  },
  "/api/admin/intelligence/listening-apps": {
    items: [{ id: "66666666-6666-6666-6666-666666666666", relayExternally: false, createdAt: "2026-01-01T00:00:00Z" }],
  },
  "/api/admin/intelligence/history/overview": {
    period: { from: "2025-12-01T00:00:00Z", to: "2026-01-01T00:00:00Z", timeZoneId: "UTC" },
    allTime: { completedListens: 42, distinctTracks: 20, distinctArtists: 8, listeningTimeMilliseconds: 7_200_000, firstListen: "2025-01-01T00:00:00Z" },
    selected: { completedListens: 12, distinctTracks: 9, distinctArtists: 5, listeningTimeMilliseconds: 3_660_000, firstListen: "2025-12-01T00:00:00Z" },
    breakdowns: {
      sources: [{ dimension: "source", value: "playback", listenCount: 8, durationMilliseconds: 2_400_000 }, { dimension: "source", value: "import", listenCount: 4, durationMilliseconds: 1_260_000 }],
      providers: [{ dimension: "provider", value: "jellyfin", listenCount: 7, durationMilliseconds: 2_100_000 }, { dimension: "provider", value: "deezer", listenCount: 5, durationMilliseconds: 1_560_000 }],
      clients: [{ dimension: "client", value: "Jellyfin Web", listenCount: 7, durationMilliseconds: 2_100_000 }, { dimension: "client", value: "Koito", listenCount: 5, durationMilliseconds: 1_560_000 }],
    },
    currentStreakDays: 3, longestStreakDays: 9, nowPlaying: null, recent: [],
  },
  "/api/admin/intelligence/history/activity": {
    period: { from: "2025-12-01T00:00:00Z", to: "2026-01-01T00:00:00Z", timeZoneId: "UTC" },
    currentStreakDays: 3, longestStreakDays: 9,
    buckets: Array.from({ length: 31 }, (_, index) => ({
      date: `2025-12-${String(index + 1).padStart(2, "0")}`,
      count: index + 1,
      durationMilliseconds: (index + 1) * 180_000,
    })),
  },
  "/api/admin/intelligence/history/top/artist": {
    kind: "artist", period: {}, items: [{ artist: "The Comets", listenCount: 7, listeningTimeMilliseconds: 1_260_000, lastListenedAt: "2025-12-31T18:00:00Z" }],
  },
  "/api/admin/intelligence/history/top/album": {
    kind: "album", period: {}, items: [{ album: "Night Drive", artist: "The Comets", listenCount: 5, listeningTimeMilliseconds: 900_000, lastListenedAt: "2025-12-31T18:00:00Z" }],
  },
  "/api/admin/intelligence/history/top/track": {
    kind: "track", period: {}, items: [{ title: "Moon Song", artist: "The Comets", album: "Night Drive", listenCount: 4, listeningTimeMilliseconds: 720_000, lastListenedAt: "2025-12-31T18:00:00Z" }],
  },
  "/api/admin/intelligence/history": {
    period: { from: "2025-12-01T00:00:00Z", to: "2026-01-01T00:00:00Z", timeZoneId: "UTC" },
    items: [{
      id: "44444444-4444-4444-4444-444444444444", title: "Moon Song", artist: "The Comets", album: "Night Drive",
      listenedAt: "2025-12-31T18:00:00Z", durationMilliseconds: 180_000, client: "Jellyfin Web",
      source: "playback", provider: "jellyfin", state: "completed", enrichmentState: "resolved",
      targetStatuses: [{ target: "lastfm", state: "delivered", requiresReauthentication: false, updatedAt: "2025-12-31T18:05:00Z" }], revision: 2,
    }],
    nextCursor: null,
  },
  "/api/admin/intelligence/history/44444444-4444-4444-4444-444444444444": {
    item: {
      id: "44444444-4444-4444-4444-444444444444", title: "Moon Song", artist: "The Comets", album: "Night Drive",
      listenedAt: "2025-12-31T18:00:00Z", durationMilliseconds: 180_000, client: "Jellyfin Web",
      source: "playback", provider: "jellyfin", state: "completed", enrichmentState: "resolved",
      targetStatuses: [{ target: "lastfm", state: "delivered", requiresReauthentication: false, updatedAt: "2025-12-31T18:05:00Z" }], revision: 2,
    },
    identity: { albumArtist: "The Comets", musicBrainzEnrichmentConfidence: .98 },
    provenance: { source: "playback", client: "Jellyfin Web", device: "browser", provider: "jellyfin", imported: false },
  },
  "/api/admin/intelligence/history/imports": { items: [] },
};

function routeRelease() {
  let release!: () => void;
  const promise = new Promise<void>((resolve) => { release = resolve; });
  return { promise, release };
}

async function mockApi(page: Page, options: { releasePath?: string; release?: Promise<void>; fail?: string[] } = {}) {
  await page.route("**/fonts/**", (route) => route.fulfill({ status: 204 }));
  await page.route("**/images/providers/**", (route) => route.fulfill({
    status: 200,
    contentType: "image/svg+xml",
    body: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1 1"/>',
  }));
  await page.route("**/api/admin/**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === options.releasePath) await options.release;
    if (options.fail?.includes(url.pathname)) {
      await route.fulfill({
        status: 503,
        contentType: "application/json",
        body: JSON.stringify({ error: "Fixture unavailable" }),
      });
      return;
    }
    let body = responses[`${url.pathname}${url.search}`] ?? responses[url.pathname];
    if (url.pathname === "/api/admin/ui/activity" && url.searchParams.get("limit") !== "8")
      body = { items: [], hasMore: false };
    if (url.pathname === "/api/admin/downloads")
      body = {
        storage: url.searchParams.get("storage"),
        files: [{
          path: "/managed/song.flac", storage: url.searchParams.get("storage"), artist: "Artist",
          album: "Album", title: "Test song", fileName: "song.flac", size: 1_024_000,
          sizeFormatted: "1000 KiB", lastModified: "2026-01-01", codec: "FLAC",
          bitrateKbps: 900, sampleRateHz: 44_100, bitDepth: 16, channels: 2,
          durationMilliseconds: 180_000, quality: "Lossless", provider: "lumen-audio",
          externalId: "track-1", artworkUrl: "/missing-download-art",
          lastAccessedAt: "2026-01-02", expiresAt: "2026-01-03",
          publicationState: "Indexed", referenceCount: 0, removable: true,
        }],
        totalSize: 1_024_000, totalSizeFormatted: "1000 KiB", count: 1,
        managedCount: 1, diagnosticCount: 0,
      };
    if (url.pathname === "/api/admin/track-matches")
      body = {
        matches: [{
          externalSnapshotId: "snapshot", providerId: "lumen-audio", libraryScopeId: "library",
          state: "suggested", decisionSource: "automatic", confidence: 0.82, threshold: 0.9,
          title: "Test song - Remix", searchQuery: "Test song", artist: "Artist", album: "Album", isrc: "US-AAA-26-00001",
          durationMilliseconds: 180_000,
          providerIdentities: [], reasons: ["title_match"], warnings: [], candidates: [{
            libraryTrackId: "local-track", backendItemId: "backend-track", title: "Test song",
            artist: "Artist", album: "Album", candidateIsrc: "US-AAA-26-00001",
            sourceIsrc: "US-AAA-26-00001", normalizedSourceTitle: "test song remix",
            normalizedCandidateTitle: "test song", artistOverlap: 1, albumEvidence: 1,
            durationDeltaMilliseconds: 250,
            providerTrackIds: { "lumen-audio": "provider-track", "apple-download": "apple-track" },
            confidence: 0.82, durationMilliseconds: 180_000, components: { title: 1 },
          }, {
            libraryTrackId: "metadata-only", isLocal: false, title: "MusicBrainz album",
            artist: "Metadata only", providerTrackIds: { musicbrainzalbum: "release-id" },
            confidence: 0.99, durationMilliseconds: 180_000,
          }, ...(url.searchParams.get("search") === "Test song" ? [{
            libraryTrackId: "jellyfin-track", backendItemId: "jellyfin-track", isLocal: true,
            title: "Test song", artist: "Artist", album: "Album",
            confidence: 0.82, durationMilliseconds: 180_000,
            components: { title: 1, localPreference: 0.07, preferenceScore: 0.89 },
          }] : [])],
        }],
        stats: { total: 1, matched: 0, accepted: 0, unresolved: 0, suggested: 1, review: 1, rejected: 0, attention: 1 },
        pagination: { page: 1, pageSize: 50, total: 1, totalPages: 1 },
      };
    if (url.pathname === "/api/admin/track-matches/snapshot/resolve" && route.request().method() === "POST")
      body = { success: true };
    if (url.pathname === "/api/admin/track-matches/targets/local")
      body = {
        tracks: [{
          id: "jellyfin-kiss-me-more", backendItemId: "jellyfin-kiss-me-more",
          title: "Kiss Me More", artist: "Doja Cat feat. SZA", album: "Planet Her",
          durationMilliseconds: 208_000, confidence: 0.91,
          components: { localPreference: 0.07, preferenceScore: 0.98 },
        }],
      };
    if (url.pathname === "/api/admin/track-matches/targets/provider")
      body = {
        tracks: [{
          id: "provider-kiss-me-more", externalId: "provider-kiss-me-more",
          externalProvider: "lumen-audio", title: "Kiss Me More",
          artist: "Doja Cat feat. SZA", album: "Planet Her",
          durationMilliseconds: 208_000, confidence: 0.94,
        }],
        providers: ["lumen-audio", "qobuz"],
      };
    if (url.pathname === "/api/admin/playlist-links/playlist-link" && route.request().method() === "GET") {
      const projectionMode = url.searchParams.get("projectionMode") ?? "resolved";
      body = {
        id: "playlist-link", snapshotId: "playlist-snapshot", snapshotVersion: 1,
        latestSourceSnapshotVersion: 1, hasNewerSourceGeneration: false,
        name: "Test playlist", sourceProviderId: "lumen-audio", projectionMode,
        targetProtocol: "jellyfin", targetPlaylistId: "jellyfin-playlist",
        artworkUrl: "/missing-playlist-art",
        retrievedAt: "2026-01-01", completedAt: "2026-01-01", trackCount: 2,
        localCount: 0, externalCount: 1, unresolvedCount: 1, durationMs: 180_000,
        matchedCount: 0, reviewCount: 1, rejectedCount: 0, playableCount: 1,
        routeCoverage: [{ providerId: "lumen-audio", count: 1 }],
        unknownDurationCount: 0,
        clientProjection: {
          protocolId: "allstarr-vpl-playlist-link", projectionMode, trackCount: 1,
          tracks: [{
            position: 1, sourcePosition: 0,
            itemId: projectionMode === "target" ? "target-track" : "client-track",
            title: projectionMode === "resolved" ? "Test song" : `${projectionMode} track`, artists: ["Artist"], album: "Album",
            durationMs: 180_000, routeKind: "local",
          }],
        },
        tracks: [{
          sourcePosition: 0, position: 1, externalSnapshotId: "snapshot", title: "Test song",
          artists: ["Artist"], album: "Album", isrc: "US-AAA-26-00001",
          artworkUrl: "/missing-track-art",
          durationMs: 180_000, routeKind: "external", routeProviderId: "lumen-audio",
          matchState: "suggested", targetEligible: false,
          outcomeCode: "skipped_external_only_for_backend", targetStatus: "wrongbackend",
          providerRoutes: [{ providerId: "lumen-audio", externalId: "provider-track", pinned: false }],
        }],
      };
    }
    if (url.pathname.includes("/api/admin/playlist-sources/") && url.pathname.endsWith("/playlists"))
      body = {
        items: [{
          id: url.searchParams.has("cursor") ? "playlist-2" : "playlist",
          providerId: url.pathname.split("/")[4],
          name: url.searchParams.has("cursor") ? "Second Mix" : "Source Mix",
          owner: "Tester", trackCount: 24, artworkUrl: "/missing-source-playlist-art",
        }],
        nextCursor: url.searchParams.has("cursor") ? null : "next",
      };
    if (url.pathname.includes("/api/admin/media-targets/") && url.pathname.endsWith("/playlists"))
      body = {
        items: [{
          id: "jellyfin-playlist", name: "Road trip", description: "Existing Jellyfin playlist",
          trackCount: 18, artworkUrl: "/missing-target-playlist-art", writable: true,
        }],
        nextCursor: null,
      };
    if (url.pathname === "/api/admin/playlist-links" && route.request().method() === "POST")
      body = { id: "new-playlist" };
    if (url.pathname === "/api/admin/playlist-links/playlist-link" && route.request().method() === "PUT")
      body = {
        ...(responses["/api/admin/playlist-links"] as { playlistLinks: Record<string, unknown>[] }).playlistLinks[0],
        ...route.request().postDataJSON(),
        revision: 2,
      };
    if (url.pathname.endsWith("/schedules") && url.pathname !== "/api/admin/intelligence/schedules" && route.request().method() === "POST")
      body = {
        id: "schedule", cronExpression: "0 3 * * *", timeZoneId: "America/New_York",
        overlapPolicy: "skip", misfirePolicy: "runOnce", enabled: true, revision: 1,
      };
    if (url.pathname.endsWith("/refresh") && route.request().method() === "POST")
      body = { snapshot: { snapshotId: "playlist-snapshot" }, preview: {} };
    if (url.pathname === "/api/admin/playlist-links/rematch/preview" && route.request().method() === "GET")
      body = {
        confirmationId: "a".repeat(64), playlistCount: 1, libraryCount: 1, totalRows: 2,
        localRows: 0, exactProviderRows: 1, genericExternalRows: 0, unresolvedRows: 1,
        confirmedManualRows: 0, staleRevisionRows: 1, conflictingRows: 0,
        rowsToRematch: 1, uniqueTracksToRematch: 1, canApply: true,
      };
    if (url.pathname === "/api/admin/playlist-links/rematch/apply" && route.request().method() === "POST")
      body = { jobId: "11111111-1111-1111-1111-111111111111", created: true };
    if (url.pathname.endsWith("/run") && route.request().method() === "POST")
      body = { jobId: "11111111-1111-1111-1111-111111111111", created: true };
    if (url.pathname.endsWith("/source-update/preview") && route.request().method() === "GET")
      body = {
        providerId: "spotify",
        providerName: "Spotify",
        sourcePlaylistName: "Road trip source",
        backendPlaylistName: "Road trip",
        backendProtocol: "jellyfin",
        sourceVersion: "a1b2c3d4e5f6",
        expectedRevision: 1,
        confirmationId: "a".repeat(64),
        currentCount: 4,
        includedCount: 4,
        skippedCount: 1,
        addedCount: 1,
        removedCount: 1,
        movedCount: 1,
        duplicateCount: 1,
        canApply: true,
        message: "Allstarr will update Road trip source in Spotify after you confirm.",
        changes: [
          { kind: "add", toPosition: 4, title: "New song", artist: "New artist" },
          { kind: "move", fromPosition: 3, toPosition: 1, title: "First song", artist: "Artist" },
          { kind: "remove", fromPosition: 2, title: "Old song", artist: "Old artist" },
        ],
        skipped: [{ position: 5, title: "Local only", artist: "Artist", reason: "This song has no confirmed match." }],
        unshownChangeCount: 0,
        unshownSkippedCount: 0,
      };
    if (url.pathname.endsWith("/source-update/apply") && route.request().method() === "POST")
      body = { jobId: "22222222-2222-2222-2222-222222222222", created: true };
    if (url.pathname.endsWith("/cancel") && route.request().method() === "POST")
      body = { jobId: "11111111-1111-1111-1111-111111111111", state: "CancellationRequested" };
    if (url.pathname.endsWith("/audience") && route.request().method() === "PUT") {
      const input = route.request().postDataJSON();
      const account = (responses["/api/admin/provider-accounts"] as { accounts: Record<string, unknown>[] }).accounts[0];
      body = { ...account, scope: input.scope, ownerUserId: input.ownerUserId, revision: 2 };
    }
    if (url.pathname === "/api/admin/config/migration/preview")
      body = {
        previewToken: "preview-token", revision: "revision", expiresAt: "2026-01-01",
        canApply: true, importedSettingCount: 1, providerAccountCount: 1, manualCount: 0,
        backendIdentityCount: 1, playlistLinkCount: 1, scheduleCount: 1,
        items: [{
          key: "CACHE_LYRICS_DAYS", sourceLine: 1, action: "import_if_absent",
          reason: "Import into tenant-scoped durable runtime settings.", sensitive: false,
          valuePreview: "21",
        }],
        conflicts: [], warnings: ["Imported accounts remain disabled until reviewed."],
      };
    if (url.pathname === "/api/admin/config/migration/apply")
      body = { success: true, alreadyApplied: false };
    if (url.pathname === "/api/admin/config/migration/reset")
      body = {};
    if (url.pathname.startsWith("/api/admin/cache/categories/") && route.request().method() === "DELETE")
      body = { category: decodeURIComponent(url.pathname.split("/").at(-1) ?? ""), deleted: 1 };
    if (url.pathname.match(/^\/api\/admin\/cache\/(metadata|media|all)$/) && route.request().method() === "DELETE")
      body = { deleted: 1 };
    if (url.pathname === "/api/admin/cache/maintenance" && route.request().method() === "POST")
      body = { deleted: 1 };
    if (url.pathname === "/api/admin/intelligence/runs")
      body = { runId: "run-2", jobId: "job-2" };
    if (url.pathname === "/api/admin/intelligence/generated-sets")
      body = { id: "set-2" };
    if (url.pathname.includes("/api/admin/intelligence/candidates/") && url.pathname.endsWith("/feedback"))
      body = { revision: 1 };
    if (url.pathname === "/api/admin/intelligence/policy")
      body = { revision: 2 };
    if (url.pathname === "/api/admin/intelligence/data")
      body = {};
    if (url.pathname === "/api/admin/intelligence/listening-apps" && route.request().method() === "POST")
      body = { id: "77777777-7777-7777-7777-777777777777", token: `als_${"7".repeat(32)}_${"8".repeat(64)}`, relayExternally: false, createdAt: "2026-01-02T00:00:00Z" };
    if (url.pathname.startsWith("/api/admin/intelligence/listening-apps/") && route.request().method() === "DELETE")
      body = {};
    if (url.pathname === "/api/admin/intelligence/audiomuse/analysis")
      body = { jobId: "sound-scan-1", state: "completed", completed: 200, total: 200 };
    if (url.pathname === "/api/admin/intelligence/audiomuse/similar")
      body = { tracks: [{ trackId: "track-3", title: "Nearby Song", artist: "Sound Artist", album: "Sound Album", score: .92, explanation: "AudioMuse found a song with a similar sound." }] };
    if (url.pathname === "/api/admin/intelligence/audiomuse/path")
      body = { tracks: [
        { trackId: "track-1", title: "Future Song", artist: "Future Artist", score: 1 },
        { trackId: "track-8", title: "Bridge Song", artist: "Sound Artist", score: .8 },
        { trackId: "track-2", title: "Second Song", artist: "Future Artist", score: 1 },
      ], totalDistance: .4 };
    if (url.pathname === "/api/admin/intelligence/audiomuse/blend")
      body = { tracks: [{ trackId: "track-9", title: "Blend Song", artist: "Sound Artist", score: .89 }] };
    if (url.pathname === "/api/admin/intelligence/audiomuse/fingerprint")
      body = { tracks: [{ trackId: "track-5", title: "Taste Song", artist: "Sound Artist", score: .9, explanation: "AudioMuse matched what you played most." }], periodDays: 90, completedListens: 42, seedCount: 8 };
    if (url.pathname === "/api/admin/intelligence/audiomuse/generated-sets")
      body = { id: "sound-playlist-1", state: "creating" };
    if (url.pathname === "/api/admin/intelligence/audiomuse/search") {
      const mode = route.request().postDataJSON().mode;
      body = { mode, tracks: [{ trackId: "track-4", title: mode === "lyrics" ? "Rain Words" : "Quiet Light", artist: "Sound Artist", score: .88, explanation: "AudioMuse matched this song to your description." }] };
    }
    if (url.pathname === "/api/admin/intelligence/audiomuse/clusters")
      body = url.searchParams.get("cursor")
        ? { clusters: [{ id: "bright", name: "Bright and quick", tracks: [{ trackId: "track-6", title: "Day Song", artist: "Sound Artist", score: .86 }] }], nextCursor: null }
        : { clusters: [{ id: "soft", name: "Soft and warm", tracks: [{ trackId: "track-4", title: "Quiet Light", artist: "Sound Artist", score: .88 }] }], nextCursor: "1" };
    if (url.pathname === "/api/admin/intelligence/audiomuse/map")
      body = url.searchParams.get("cursor")
        ? { items: [{ trackId: "track-7", title: "Far Song", artist: "Sound Artist", score: .8, x: -.4, y: .1 }], projection: "fixture", nextCursor: null, isPartial: false, snapshotVersion: "map-1" }
        : { items: [{ trackId: "track-4", title: "Quiet Light", artist: "Sound Artist", score: .88, x: .2, y: .6, clusterId: "soft" }], projection: "fixture", nextCursor: "next", isPartial: false, snapshotVersion: "map-1" };
    if (url.pathname === "/api/admin/intelligence/history/44444444-4444-4444-4444-444444444444" && route.request().method() === "PUT")
      body = { id: "44444444-4444-4444-4444-444444444444", revision: 3 };
    if (url.pathname === "/api/admin/intelligence/history/44444444-4444-4444-4444-444444444444" && route.request().method() === "DELETE")
      body = {};
    if (url.pathname === "/api/admin/intelligence/history/imports/preview") {
      const secondFile = route.request().postData()?.includes("ListenBrainz.jsonl") ?? false;
      body = {
        importId: secondFile ? "66666666-6666-6666-6666-666666666666" : "55555555-5555-5555-5555-555555555555", revision: "preview-revision",
        displayFileName: secondFile ? "ListenBrainz.jsonl" : "Streaming_History.json", sizeBytes: 1024, expiresAt: "2026-01-02T00:00:00Z",
        state: "previewed", outboundReplay: false,
        preview: {
          format: "spotify-extended-history", fileRows: 15, musicRows: 14, completed: 12, partial: 1,
          skipped: 1, episodes: 1, nonTrack: 0, malformed: 0, duplicateInFile: 1, duplicateExisting: 2,
          newRows: 9, resolvedNewRows: 7, unresolvedNewRows: 2, rowsWithoutProviderIdentity: 0,
          sourceUserCount: 1, estimatedMusicBrainzLookups: 2, earliest: "2025-01-01T00:00:00Z",
          latest: "2025-12-31T00:00:00Z", reasonCounts: {},
        },
      };
    }
    if (url.pathname === "/api/admin/intelligence/history/imports/55555555-5555-5555-5555-555555555555")
      body = { importId: "55555555-5555-5555-5555-555555555555", revision: "done-revision", state: "completed", importedRows: 9, duplicateRows: 3, resolvedRows: 7, unresolvedRows: 2, outboundReplay: false };
    if (url.pathname.match(/^\/api\/admin\/intelligence\/history\/imports\/[^/]+\/(apply|resume|cancel)$/))
      body = { importId: "55555555-5555-5555-5555-555555555555", revision: "done-revision", state: "completed", importedRows: 9, duplicateRows: 3, resolvedRows: 7, unresolvedRows: 2, outboundReplay: false };
    if (url.pathname === "/api/admin/intelligence/schedules" && route.request().method() === "POST")
      body = { id: "schedule-2", ...route.request().postDataJSON(), revision: 1, nextRunAt: "2026-01-02T13:00:00Z" };
    if (url.pathname.startsWith("/api/admin/intelligence/schedules/") && route.request().method() === "PUT")
      body = { id: url.pathname.split("/").at(-1), ...route.request().postDataJSON(), revision: 2, nextRunAt: "2026-01-02T13:00:00Z" };
    if (url.pathname.startsWith("/api/admin/intelligence/schedules/") && route.request().method() === "DELETE")
      body = {};
    await route.fulfill({
      status: body === undefined ? 404 : 200,
      contentType: "application/json",
      body: JSON.stringify(body ?? { error: `Missing fixture: ${url.pathname}` }),
    });
  });
}

const routes = [
  ["#/", "Home"],
  ["#/library", "Library"],
  ["#/library/playlists", "Library"],
  ["#/library/mappings", "Library"],
  ["#/library/cached", "Library"],
  ["#/library/kept", "Library"],
  ["#/activity", "Activity"],
  ["#/intelligence", "Intelligence"],
  ["#/integrations/services", "Integrations"],
  ["#/integrations/accounts", "Integrations"],
  ["#/integrations/extensions", "Integrations"],
  ["#/integrations/routing", "Integrations"],
  ["#/settings/general", "Settings"],
] as const;

const stateRoutes = [
  ["#/", "Home", "Loading Home", "/api/admin/ui/home", ["/api/admin/ui/home", "/api/admin/ui/now-playing"]],
  ["#/library/playlists", "Library", "Loading playlists", "/api/admin/playlist-links", ["/api/admin/playlist-links"]],
  ["#/library/mappings", "Library", "Loading match review", "/api/admin/track-matches", ["/api/admin/track-matches"]],
  ["#/library/cached", "Library", "Loading Cached tracks", "/api/admin/downloads", ["/api/admin/downloads"]],
  ["#/activity", "Activity", "Loading Event log", "/api/admin/ui/activity", ["/api/admin/ui/activity"]],
  ["#/integrations/services", "Integrations", "Loading Services", "/api/admin/provider-accounts", ["/api/admin/ui/schema", "/api/admin/provider-accounts"]],
  ["#/settings/general", "Settings", "Loading Settings", "/api/admin/ui/schema", ["/api/admin/ui/schema"]],
] as const;

for (const viewport of viewports) {
  test.describe(`${viewport.width}x${viewport.height}`, () => {
    test.use({ viewport });

    for (const [route, heading] of routes) {
      test(`${route} has no document overflow`, async ({ page }) => {
        const errors: string[] = [];
        page.on("pageerror", (error) => errors.push(error.message));
        const screenshotTheme = process.env.ALLSTARR_SCREENSHOT_THEME;
        if (screenshotTheme === "light" || screenshotTheme === "dark") {
          await page.addInitScript((theme) => localStorage.setItem("allstarr.theme", theme), screenshotTheme);
        }
        await mockApi(page);
        await page.goto(route);
        const pageHeading = page.getByRole("heading", { name: heading, level: 1 });
        await expect(pageHeading).toBeVisible();
        if (route === "#/integrations/extensions") {
          await expect(page.getByText("Extension manager", { exact: true })).toBeVisible();
        }
        await page.evaluate(() => new Promise<void>((resolve) =>
          requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
        ));
        expect(await page.evaluate(() => window.scrollY)).toBe(0);
        expect((await pageHeading.boundingBox())?.y).toBeGreaterThanOrEqual(0);
        await expect.poll(() => page.evaluate(() =>
          document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
        if (route === "#/activity") {
          if (viewport.width <= 650) {
            await expect(page.getByText("Filters", { exact: true })).toBeVisible();
            await expect(page.getByLabel("Search")).toBeHidden();
          } else {
            await expect(page.getByLabel("Search")).toBeVisible();
          }
        }
        expect(errors).toEqual([]);
        if (process.env.ALLSTARR_SCREENSHOT_DIR) {
          const name = route.replace(/^#\/?/, "").replaceAll(/[^a-z0-9]+/gi, "-") || "home";
          await page.screenshot({
            path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/${screenshotTheme ?? "system"}-${viewport.width}-${name}.png`,
            fullPage: true,
          });
        }
      });
    }

    test("Playlist projection dialogs remain usable", async ({ page }, testInfo) => {
      await mockApi(page);
      await page.goto("#/library/playlists");
      await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
      const details = page.getByRole("dialog", { name: "Test playlist" });
      await expect(details).not.toContainText(/native playlist|hybrid|materialized|write-back|projection mode|backend/i);
      await details.getByRole("tab", { name: "Jellyfin playlist", exact: true }).click();
      await expect(details.getByText("target track", { exact: true })).toBeVisible();
      await expect(details).toHaveCSS("overflow", "hidden");
      await expect(details.locator(".track-scroll")).toHaveCSS("overflow", "auto");
      await expect.poll(() => details.locator(".track-scroll").evaluate((scroll) =>
        scroll.clientHeight - scroll.querySelector("table")!.scrollHeight)).toBeLessThan(48);
      const headerContentClearsSwitcher = () => details.evaluate((dialog) => {
        const header = dialog.querySelector(".playlist-hero");
        const switcher = dialog.querySelector(".playlist-view-switcher");
        if (!header || !switcher) return false;
        const switcherTop = switcher.getBoundingClientRect().top;
        return Array.from(header.children).every((child) => child.getBoundingClientRect().bottom <= switcherTop + 1);
      });
      await expect.poll(headerContentClearsSwitcher).toBe(true);
      if (viewport.width === 1280) {
        await page.setViewportSize({ width: 1168, height: 497 });
        await expect.poll(headerContentClearsSwitcher).toBe(true);
        await page.setViewportSize(viewport);
      }
      const screenshotDirectory = process.env.ALLSTARR_SCREENSHOT_DIR;
      const detailScreenshot = await page.screenshot({
        path: screenshotDirectory ? `${screenshotDirectory}/playlist-${viewport.width}-details.png` : undefined,
      });
      await testInfo.attach("playlist-details", {
        body: detailScreenshot,
        contentType: "image/png",
      });

      await details.getByRole("button", { name: "Actions" }).click();
      await page.getByRole("menuitem", { name: "Edit settings" }).click();
      const settings = page.getByRole("dialog", { name: "Edit playlist settings" });
      await expect(settings).not.toContainText(/native playlist|hybrid|materialized|write-back|projection mode|backend/i);
      await expect(settings.locator(".playlist-settings-form")).toHaveCSS("overflow-y", "auto");
      await expect(settings.getByRole("button", { name: "Save settings" })).toBeInViewport();
      const settingsScreenshot = await page.screenshot({
        path: screenshotDirectory ? `${screenshotDirectory}/playlist-${viewport.width}-settings.png` : undefined,
      });
      await testInfo.attach("playlist-settings", {
        body: settingsScreenshot,
        contentType: "image/png",
      });
    });

    for (const [route, heading, loadingLabel, releasePath, failures] of stateRoutes) {
      test(`${route} exposes loading and error recovery`, async ({ page, context }) => {
        const delayed = routeRelease();
        await mockApi(page, { releasePath, release: delayed.promise });
        await page.goto(route);
        await expect(page.getByLabel(loadingLabel)).toBeVisible();
        delayed.release();
        await expect(page.getByRole("heading", { name: heading, level: 1 })).toBeVisible();

        const errorPage = await context.newPage();
        await mockApi(errorPage, { fail: [...failures] });
        await errorPage.goto(route);
        await expect(errorPage.getByRole("alert")).toBeVisible();
        await expect(errorPage.getByRole("button", { name: "Try again" })).toBeInViewport();
      });
    }

    test("Settings dialogs remain usable", async ({ page }) => {
      await mockApi(page);
      await page.goto("#/settings/general");
      await expect(page.getByText("Public URL", { exact: true }).locator("..").getByText("Deployment-owned")).toBeVisible();
      await page.getByLabel("Color theme").click();
      await expect(page.getByRole("option", { name: "Dark" })).toBeVisible();
      await page.getByRole("option", { name: "Dark" }).click();
      await expect(page.locator("html")).toHaveClass(/dark/);
      await page.keyboard.press("Escape");
      await page.goto("#/settings/accounts");
      await expect(page.getByRole("heading", { name: "Integrations", level: 1 })).toBeVisible();
      await expect(page.getByRole("tab", { name: "Accounts" })).toHaveAttribute("aria-selected", "true");
      await page.goto("#/sources?source=lumen-audio&section=configuration");
      await expect(page.getByRole("heading", { name: "Integrations", level: 1 })).toBeVisible({ timeout: 15_000 });
      await expect(page.getByRole("tab", { name: "Services" })).toHaveAttribute("aria-selected", "true");
      await page.getByRole("button", { name: "Connect another account" }).click();
      const sourceDialog = page.getByRole("dialog", { name: "Connect a Source" });
      await expect(sourceDialog.getByRole("button", { name: "Source", exact: true })).toContainText("Lumen Audio");
      await expect(sourceDialog.getByLabel("Access token")).toHaveValue("");
      await sourceDialog.getByRole("button", { name: "Cancel" }).click();
      await page.goto("#/settings/routing");
      await expect(page.getByRole("tab", { name: "Routing" })).toHaveAttribute("aria-selected", "true");
      await expect(page.getByText("Local · fixed")).toBeVisible();
      await expect(page.getByRole("button", { name: "Move Jellyfin up" })).toHaveCount(0);
      await expect(page.locator(".provider-art").first()).toHaveCSS("border-top-width", "0px");
      const routes = page.locator(".routing-group li[draggable=true]");
      await routes.nth(0).dragTo(routes.nth(1));
      await expect(routes.nth(0)).toContainText("Future Audio");
      await expect.poll(() => page.evaluate(() =>
        document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      await page.goto("#/settings/extensions");
      await expect(page.getByRole("tab", { name: "Extensions" })).toHaveAttribute("aria-selected", "true");
      await expect(page.locator(".extension-row .badge")).toContainText(["Metadata", "Streaming"]);
      await expect(page.locator(".segmented-tab-count").first()).toHaveCSS("border-style", "solid");
      await expect(page.getByRole("tab", { name: /Available/ })).toHaveCSS("border-right-width", "0px");
      await page.getByRole("tab", { name: /Available/ }).click();
      await expect(page.getByText("Available packages", { exact: true })).toBeVisible();
      await page.getByRole("tab", { name: /Registries/ }).click();
      await expect(page.getByText("Add registry", { exact: true })).toBeVisible();
      await page.getByRole("tab", { name: "Activity" }).click();
      await expect(page.getByText("Extension activity", { exact: true })).toBeVisible();
      await expect.poll(() => page.evaluate(() =>
        document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      await page.getByRole("button", { name: "Install extension" }).click();
      await expect(page.getByRole("dialog", { name: "Install extension" })).toBeVisible();
      await expect(page.getByRole("button", { name: "Verify package" })).toBeInViewport();
      await page.getByRole("button", { name: "Close installer" }).click();
      await page.goto("#/settings/maintenance");
      await expect(page.getByText("Selective state transfer")).toBeVisible();
      await expect(page.getByText("128 MiB max")).toBeVisible();
      await expect.poll(() => page.evaluate(() =>
        document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      await page.getByRole("button", { name: "Purge all cache" }).click();
      const dialog = page.getByRole("alertdialog", { name: "Purge the application cache?" });
      await expect(dialog).toBeVisible();
      await expect(dialog.getByRole("button", { name: "Purge cache" })).toBeInViewport();
    });

    test("Intelligence results remain usable", async ({ page, context }) => {
      const delayed = routeRelease();
      await page.emulateMedia({ reducedMotion: "reduce" });
      await mockApi(page, { releasePath: "/api/admin/intelligence", release: delayed.promise });
      const initialHistoryOverview = page.waitForRequest((request) =>
        new URL(request.url()).pathname === "/api/admin/intelligence/history/overview");
      await page.goto("#/intelligence");
      const loadingStatus = page.getByRole("status", { name: "Loading Intelligence" });
      await expect(loadingStatus).toBeVisible();
      expect(await loadingStatus.locator(".skeleton-panel").first().evaluate((element) =>
        getComputedStyle(element, "::after").animationName)).toBe("none");
      delayed.release();
      const initialHistoryUrl = new URL((await initialHistoryOverview).url());
      expect(initialHistoryUrl.searchParams.has("from")).toBe(false);
      expect(initialHistoryUrl.searchParams.has("to")).toBe(false);
      await expect(page.getByRole("tab", { name: "Overview" })).toHaveAttribute("aria-selected", "true");
      await expect(page.getByRole("tab", { name: "All time" })).toHaveAttribute("aria-selected", "true");
      await expect(page.locator(".scope-value")).toContainText("Music");
      await expect(page.locator(".scope-value")).toContainText("Jellyfin · Jellyfin Music");
      const themeButton = page.getByRole("button", { name: /Theme:/ });
      await themeButton.click();
      await expect(page.locator("html")).not.toHaveClass(/dark/);
      await themeButton.click();
      await expect(page.locator("html")).toHaveClass(/dark/);
      await themeButton.click();
      await expect(page.getByLabel("Server connection")).toHaveCount(0);
      await expect(page.getByRole("button", { name: "Open library" })).toHaveCount(0);
      if (viewport.width <= 650) {
        const sectionTabs = page.getByRole("navigation", { name: "Intelligence sections" }).locator(".segmented-tabs");
        await expect(sectionTabs).toHaveCSS("overflow-x", "auto");
        expect(Math.min(...await sectionTabs.getByRole("tab").evaluateAll((tabs) => tabs.map((tab) => tab.clientWidth)))).toBeGreaterThanOrEqual(72);
      }
      const recap = page.locator(".recap-card");
      await expect(recap).toContainText("12 times across 9 songs and 5 artists");
      await expect(recap).toContainText("Your most-played song was Moon Song by The Comets");
      await expect(recap).toContainText("Your first recorded listen was");
      await expect(page.getByRole("heading", { name: "Recently played", level: 3 })).toBeVisible();
      await expect(page.locator(".history-list").getByText("Moon Song", { exact: true })).toBeVisible();
      expect(await page.locator(".history-list").evaluate((element) => element.tagName)).toBe("UL");
      const activityCard = page.locator(".activity-card");
      await expect(activityCard.getByText(viewport.width <= 620
        ? "Showing 2025-12-02 through 2025-12-31"
        : "Showing 2025-12-01 through 2025-12-31", { exact: true })).toBeVisible();
      await expect(activityCard.locator(".activity-grid span:visible")).toHaveCount(viewport.width <= 620 ? 30 : 31);
      await activityCard.getByRole("tab", { name: "Monthly" }).click();
      await expect(activityCard.getByText("Dec 2025", { exact: true })).toBeVisible();
      await expect(activityCard.getByText("496 listens", { exact: true })).toBeVisible();
      await activityCard.getByRole("tab", { name: "Daily" }).click();
      await expect(page.locator(".breakdown-card").filter({ hasText: "Sources" })).toContainText("playback");
      await expect(page.locator(".breakdown-card").filter({ hasText: "Providers" })).toContainText("deezer");
      await expect(page.locator(".breakdown-card").filter({ hasText: "Listening apps" })).toContainText("Koito");
      const topTabs = page.getByRole("navigation", { name: "Top listening category" });
      await topTabs.getByRole("tab", { name: "Artists" }).focus();
      await page.keyboard.press("ArrowRight");
      await expect(topTabs.getByRole("tab", { name: "Albums" })).toHaveAttribute("aria-selected", "true");
      await page.keyboard.press("End");
      await expect(topTabs.getByRole("tab", { name: "Songs" })).toHaveAttribute("aria-selected", "true");
      if (viewport.width <= 650)
        expect(Math.min(...await topTabs.getByRole("tab").evaluateAll((tabs) => tabs.map((tab) => tab.clientHeight)))).toBeGreaterThanOrEqual(44);
      await page.getByRole("tab", { name: "Discover" }).click();
      await expect(page).toHaveURL(/#\/intelligence\?section=discover$/);
      await expect(page.getByText("Future Song", { exact: true })).toBeVisible();
      await expect(page.getByText("Morning discovery")).toBeVisible();
      await expect(page.getByText("Created in Jellyfin", { exact: true })).toBeVisible();
      await expect(page.getByText("Searching Lumen Audio.")).toBeVisible();
      await expect(page.getByRole("progressbar", { name: "Recommendation refresh progress" })).toHaveAttribute("aria-valuenow", "1");
      await page.getByRole("button", { name: "Cancel refresh" }).scrollIntoViewIfNeeded();
      await expect(page.getByRole("button", { name: "Cancel refresh" })).toBeInViewport();
      await page.getByRole("button", { name: "Refresh recommendations" }).scrollIntoViewIfNeeded();
      await expect(page.getByRole("button", { name: "Refresh recommendations" })).toBeInViewport();
      const soundDiscovery = page.locator(".sound-discovery");
      await expect(soundDiscovery).toContainText("Allstarr will not create or change a Jellyfin playlist");
      await expect(soundDiscovery).not.toContainText(/native playlist|hybrid|materialized|write-back/i);
      const firstScan = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/analysis"));
      await soundDiscovery.getByRole("button", { name: "Scan library sounds" }).click();
      expect((await firstScan).postDataJSON()).toMatchObject({ rebuild: false });
      await expect(soundDiscovery.getByRole("progressbar", { name: "Library sound scan progress" })).toHaveAttribute("aria-valuenow", "200");
      const rebuildScan = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/analysis"));
      await soundDiscovery.getByRole("button", { name: "Scan library again" }).click();
      expect((await rebuildScan).postDataJSON()).toMatchObject({ rebuild: true });
      const similarRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/similar"));
      await soundDiscovery.getByRole("button", { name: "Find songs" }).click();
      expect((await similarRequest).postDataJSON()).toMatchObject({
        protocol: "jellyfin", backendInstanceId: "main", libraryScopeId: "music", seedTrackIds: ["track-1"],
      });
      await expect(soundDiscovery.getByText("Nearby Song", { exact: true })).toBeVisible();
      await soundDiscovery.getByLabel("How to explore").click();
      await page.getByRole("option", { name: "Connect two songs" }).click();
      const pathRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/path"));
      await soundDiscovery.getByRole("button", { name: "Find songs" }).click();
      expect((await pathRequest).postDataJSON()).toMatchObject({ startTrackId: "track-1", endTrackId: "track-2" });
      await expect(soundDiscovery.getByText("Bridge Song", { exact: true })).toBeVisible();
      await soundDiscovery.getByLabel("How to explore").click();
      await page.getByRole("option", { name: "Include one sound and avoid another" }).click();
      const blendRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/blend"));
      await soundDiscovery.getByRole("button", { name: "Find songs" }).click();
      expect((await blendRequest).postDataJSON()).toMatchObject({ includeTrackIds: ["track-1"], avoidTrackIds: ["track-2"] });
      await expect(soundDiscovery.getByText("Blend Song", { exact: true })).toBeVisible();
      await soundDiscovery.getByLabel("How to explore").click();
      await page.getByRole("option", { name: "Describe what you want" }).click();
      await soundDiscovery.getByLabel("Describe a sound").fill("warm and quiet");
      const textRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/search"));
      await soundDiscovery.getByRole("button", { name: "Find songs" }).click();
      expect((await textRequest).postDataJSON()).toMatchObject({ query: "warm and quiet", mode: "text" });
      await expect(soundDiscovery.getByText("Quiet Light", { exact: true })).toBeVisible();
      await soundDiscovery.getByLabel("What to match").click();
      await page.getByRole("option", { name: "Song lyrics" }).click();
      await soundDiscovery.getByLabel("Words to find").fill("city lights in the rain");
      const lyricsRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/search"));
      await soundDiscovery.getByRole("button", { name: "Find songs" }).click();
      expect((await lyricsRequest).postDataJSON()).toMatchObject({ query: "city lights in the rain", mode: "lyrics" });
      await expect(soundDiscovery.getByText("Rain Words", { exact: true })).toBeVisible();
      await soundDiscovery.getByLabel("How to explore").click();
      await page.getByRole("option", { name: "Use what I played most" }).click();
      const fingerprintRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/fingerprint"));
      await soundDiscovery.getByRole("button", { name: "Find songs" }).click();
      expect((await fingerprintRequest).postDataJSON()).toMatchObject({
        protocol: "jellyfin", backendInstanceId: "main", libraryScopeId: "music", periodDays: 90,
      });
      await expect(soundDiscovery.getByText("Taste Song", { exact: true })).toBeVisible();
      await soundDiscovery.getByLabel("Playlist name").fill("Evening sounds");
      await soundDiscovery.getByRole("button", { name: "Create Jellyfin playlist" }).click();
      const createDialog = page.getByRole("alertdialog", { name: "Create Evening sounds in Jellyfin?" });
      await expect(createDialog).toContainText("Allstarr will create Evening sounds in Jellyfin with 1 song.");
      await createDialog.getByRole("button", { name: "Do not create playlist" }).click();
      await expect(createDialog).toBeHidden();
      await soundDiscovery.getByRole("button", { name: "Create Jellyfin playlist" }).click();
      const createRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/generated-sets"));
      await createDialog.getByRole("button", { name: "Create Jellyfin playlist" }).click();
      expect((await createRequest).postDataJSON()).toMatchObject({
        protocol: "jellyfin", backendInstanceId: "main", libraryScopeId: "music",
        name: "Evening sounds", trackIds: ["track-5"],
      });
      await expect(soundDiscovery.getByText("Allstarr is creating Evening sounds in Jellyfin.", { exact: true })).toBeVisible();
      await soundDiscovery.getByLabel("How to explore").click();
      await page.getByRole("option", { name: "Browse the whole library by sound" }).click();
      await soundDiscovery.getByRole("button", { name: "Group similar songs" }).click();
      await expect(soundDiscovery.getByText("Soft and warm", { exact: true })).toBeVisible();
      await soundDiscovery.getByRole("button", { name: "Show more groups" }).click();
      await expect(soundDiscovery.getByText("Bright and quick", { exact: true })).toBeVisible();
      await soundDiscovery.getByRole("button", { name: "List the sound map" }).click();
      await expect(soundDiscovery.getByText("Quiet Light", { exact: true })).toBeVisible();
      const nextMap = page.waitForRequest((request) => request.url().includes("/api/admin/intelligence/audiomuse/map") && new URL(request.url()).searchParams.get("cursor") === "next");
      await soundDiscovery.getByRole("button", { name: "Show more songs" }).click();
      await nextMap;
      await expect(soundDiscovery.getByText("Far Song", { exact: true })).toBeVisible();
      if (process.env.ALLSTARR_SCREENSHOT_DIR)
        await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/intelligence-${viewport.width}-recommendations.png`, fullPage: true });
      await expect.poll(() => page.evaluate(() =>
        document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      await page.getByRole("tab", { name: "History", exact: true }).click();
      await expect(page.getByRole("heading", { name: "Listening history", level: 3, exact: true })).toBeVisible();
      await expect(page.locator(".history-list").getByText("Moon Song", { exact: true })).toBeVisible();
      await expect(page.getByRole("heading", { name: "Import listening history" })).toHaveCount(0);
      await expect.poll(() => page.evaluate(() =>
        document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      if (process.env.ALLSTARR_SCREENSHOT_DIR)
        await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/intelligence-${viewport.width}-history.png`, fullPage: true });
      await page.getByRole("tab", { name: "Import", exact: true }).click();
      await expect(page.getByRole("heading", { name: "Import listening history" })).toBeVisible();
      await expect(page.getByText(/upload the JSON files from your Extended Streaming History download/)).toBeVisible();
      await expect(page.getByRole("tab", { name: "Automation", exact: true })).toBeInViewport();
      await expect(page.getByText("Imported listens older than 30 days are removed automatically.", { exact: true })).toBeVisible();
      await expect(page.locator(".history-list")).toHaveCount(0);
      const historyUpload = page.locator(".upload-zone");
      await historyUpload.scrollIntoViewIfNeeded();
      await expect(historyUpload).toBeInViewport();
      await expect(page.getByLabel("History export files")).toHaveAttribute("multiple", "");
      await expect.poll(() => page.evaluate(() =>
        document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      if (process.env.ALLSTARR_SCREENSHOT_DIR)
        await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/intelligence-${viewport.width}-imports.png`, fullPage: true });
      await page.getByRole("tab", { name: "Automation" }).click();
      await expect(page.getByText("Monday discoveries", { exact: true })).toBeVisible();
      await expect(page.getByText("Allstarr keeps private listening history and uses it for recommendations.", { exact: true })).toBeVisible();
      const automaticHistory = page.getByRole("checkbox", { name: /Save my listening automatically/ });
      await expect(automaticHistory).toBeChecked();
      await expect(automaticHistory.locator("..")).toHaveClass(/selected/);
      expect((await automaticHistory.locator("..").boundingBox())?.height ?? 999).toBeLessThan(140);
      await expect(automaticHistory).toHaveAttribute("data-slot", "checkbox");
      await expect(page.getByText("Keep listening history for", { exact: true })).toBeVisible();
      await expect(page.getByText("Recommendation actions", { exact: true })).toBeVisible();
      await expect(page.getByText("Recommendation sources", { exact: true })).toBeVisible();
      await expect(page.getByText("Connected services Allstarr may use to find candidates. This does not import history or change source accounts.", { exact: true })).toBeVisible();
      const audioMuseSetup = page.locator(".provider-setup").filter({ hasText: "AudioMuse connection" });
      await expect(audioMuseSetup).toContainText("Connect AudioMuse here");
      await audioMuseSetup.getByRole("button", { name: "Connect AudioMuse" }).click();
      const audioMuseDialog = page.getByRole("dialog", { name: "Connect a Source" });
      await expect(audioMuseDialog.getByLabel("AudioMuse server URL")).toBeVisible();
      await expect(audioMuseDialog.getByLabel("AudioMuse access token")).toBeVisible();
      await audioMuseDialog.getByRole("button", { name: "Close source connection dialog" }).click();
      await expect(page.locator(".settings-stack")).not.toContainText(/\bsignals?\b/i);
      await expect(page.getByText("Allstarr sent the latest completed listen to Last.fm.", { exact: true })).toBeVisible();
      await expect(page.getByText("Allstarr will not send listens to ListenBrainz.", { exact: true })).toBeVisible();
      await expect(page.getByText("Allstarr is checking 2 saved listens for more song details.", { exact: true })).toBeVisible();
      const intelligenceSource = page.locator(".provider-choices label").filter({ hasText: "Private similarity source." });
      await expect(intelligenceSource.locator('[data-slot="badge"]')).toHaveText("Ready");
      await expect(page.getByText("Where generated playlists are created", { exact: true })).toBeVisible();
      expect(await page.locator(".status-list").evaluate((element) => element.tagName)).toBe("UL");
      expect(await page.locator(".schedule-list").evaluate((element) => element.tagName)).toBe("UL");
      const listeningApps = page.locator(".listening-apps-card");
      await expect(listeningApps).toContainText("Allstarr will save listens from this key to music on Jellyfin.");
      await expect(listeningApps).toContainText("Allstarr will not send these listens to another service.");
      expect(await listeningApps.locator(".key-list").evaluate((element) => element.tagName)).toBe("UL");
      await expect(listeningApps).not.toContainText(/native playlist|hybrid|materialized|write-back|projection|backend/i);
      const keyRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/listening-apps") && request.method() === "POST");
      await listeningApps.getByRole("button", { name: "Create private key" }).click();
      expect((await keyRequest).postDataJSON()).toMatchObject({
        protocol: "jellyfin", backendInstanceId: "main", libraryScopeId: "music", sendToConnectedServices: false,
      });
      await expect(listeningApps.getByLabel("New listening app private key")).toHaveValue(/^als_/);
      await page.getByRole("button", { name: "Turn off and clear" }).click();
      const clearDialog = page.getByRole("alertdialog", { name: "Clear private listening data for this library?" });
      await expect(clearDialog).toContainText("it will not change Jellyfin playlists or connected Last.fm or ListenBrainz accounts.");
      await clearDialog.getByRole("button", { name: "Keep my data" }).click();
      if (process.env.ALLSTARR_SCREENSHOT_DIR)
        await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/intelligence-${viewport.width}-settings.png`, fullPage: true });

      const errorPage = await context.newPage();
      await mockApi(errorPage, { fail: ["/api/admin/intelligence"] });
      await errorPage.goto("#/intelligence");
      await expect(errorPage.getByRole("alert")).toContainText("Fixture unavailable");
      await expect(errorPage.getByRole("button", { name: "Try again" })).toBeInViewport();
    });

    test("Source dialogs remain usable", async ({ page }) => {
      await mockApi(page);
      await page.goto("#/sources");
      await page.getByRole("button", { name: "Connect Source" }).click();
      const connect = page.getByRole("dialog", { name: "Connect a Source" });
      await expect(connect).toBeVisible();
      await connect.getByRole("button", { name: "Source", exact: true }).click();
      await page.getByRole("option", { name: "ListenBrainz" }).click();
      await expect(connect.getByLabel("ListenBrainz or Koito token")).toBeVisible();
      await expect(connect.getByLabel("Where to send listens (optional)")).toBeVisible();
      await expect(connect.getByText("Leave blank to send listens to ListenBrainz. To use Koito, paste its HTTPS listening address.")).toBeVisible();
      await expect(connect.getByRole("button", { name: "Save and test" })).toBeInViewport();
      await page.getByRole("button", { name: "Close source connection dialog" }).click();
      await page.goto("#/integrations/services?source=audiomuse-ai&section=configuration");
      const intelligenceLink = page.getByRole("link", { name: "Configure in Intelligence" });
      await expect(intelligenceLink).toHaveAttribute("href", "#/intelligence?section=automation");
      await expect(page.getByRole("dialog")).toContainText("AudioMuse powers sound-based discovery");
      await page.goto("#/integrations/accounts");
      await page.getByRole("button", { name: /Lumen Audio Account details stored/ }).click();
      await page.getByRole("tab", { name: "Access" }).click();
      await page.getByRole("button", { name: "Edit access" }).click();
      const access = page.locator(".access-dialog");
      await expect(access).toBeVisible();
      await access.getByRole("radio", { name: "One user" }).check();
      await access.getByRole("button", { name: "Allstarr user" }).click();
      await page.getByRole("option", { name: "Listener" }).click();
      await access.getByRole("button", { name: "Save access" }).click();
      await expect(access).toBeHidden();

      await page.getByRole("button", { name: /Lumen Audio Account details stored/ }).click();
      await page.getByRole("tab", { name: "Access" }).click();
      await page.getByRole("button", { name: "Edit access" }).click();
      await page.getByRole("radio", { name: "One library" }).check();
      await expect(page.getByText("Only requests in the selected media library may use this account.")).toBeVisible();
      await page.getByLabel("Library ID").fill("music");
      await page.getByRole("button", { name: "Save access" }).click();
      const libraryShare = page.getByRole("alertdialog", {
        name: "Share this connection with a library?",
      });
      await expect(libraryShare).toBeVisible();
      await libraryShare.getByRole("button", { name: "Keep current access" }).click();
      await page.getByRole("radio", { name: "Everyone" }).check();
      await page.getByRole("button", { name: "Save access" }).click();
      await expect(page.getByRole("alertdialog", { name: "Share this connection with everyone?" })).toBeVisible();
    });

    test("Match and removal dialogs remain usable", async ({ page }) => {
      await mockApi(page);
      await page.goto("#/library/mappings");
      await page.getByRole("button", { name: "Interactive search" }).click();
      await expect(page.getByRole("dialog", { name: "Test song" })).toBeVisible();
      await expect(page.getByRole("button", { name: "Reject candidate" })).toBeInViewport();
      await page.getByRole("button", { name: "Reject candidate" }).click();
      const reject = page.getByRole("alertdialog", { name: "Reject this candidate?" });
      await expect(reject).toBeVisible();
      await expect(reject.getByRole("button", { name: "Reject candidate" })).toBeInViewport();
      await reject.getByRole("button", { name: "Cancel" }).click();
      await expect(reject).toBeHidden();
      await page.getByRole("button", { name: "Close match dialog" }).click();
      await expect(page.getByRole("dialog", { name: "Test song" })).toBeHidden();
      await expect.poll(() => page.evaluate(() => getComputedStyle(document.body).pointerEvents)).toBe("auto");

      await page.goto("#/library/cached");
      const removeButton = page.getByRole("button", { name: "Remove", exact: true });
      await removeButton.click();
      const removal = page.getByRole("alertdialog", { name: "Remove this track?" });
      await expect(removal).toBeVisible();
      await expect(removal.getByRole("button", { name: "Remove track" })).toBeInViewport();
    });

    test("keyboard dialogs honor reduced motion", async ({ page }) => {
      await page.emulateMedia({ reducedMotion: "reduce" });
      await mockApi(page);
      await page.goto("#/settings/extensions");
      await expect(page.getByRole("tab", { name: "Extensions" })).toHaveAttribute("aria-selected", "true");
      await page.getByRole("button", { name: "Install extension" }).click();
      const dialog = page.getByRole("dialog", { name: "Install extension" });
      await expect(dialog).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(dialog).toBeHidden();
    });
  });
}

test("Intelligence history imports, corrections, and schedules use the selected scope", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  const overviewRequest = page.waitForRequest((request) => request.url().includes("/api/admin/intelligence/history/overview"));
  await page.goto("#/intelligence?section=history");
  await expect(page.getByRole("tab", { name: "History", exact: true })).toHaveAttribute("aria-selected", "true");
  expect(new URL((await overviewRequest).url()).searchParams.get("backendInstanceId")).toBe("main");

  await page.locator(".history-list").getByRole("button").click();
  const detail = page.getByRole("dialog", { name: "Edit listen" });
  await expect(detail).toBeVisible();
  await detail.getByLabel("Song").fill("Moon Song (live)");
  const correction = page.waitForRequest((request) => request.method() === "PUT" &&
    request.url().includes("/api/admin/intelligence/history/44444444-4444-4444-4444-444444444444"));
  await detail.getByRole("button", { name: "Save changes" }).click();
  expect((await correction).postDataJSON()).toMatchObject({
    protocol: "jellyfin", backendInstanceId: "main", libraryScopeId: "music",
    title: "Moon Song (live)", expectedRevision: 2,
  });
  await detail.getByRole("button", { name: "Close listen details" }).click();

  await page.getByRole("tab", { name: "Import", exact: true }).click();
  await expect(page).toHaveURL(/#\/intelligence\?section=imports$/);
  const previews: string[] = [];
  page.on("request", (request) => {
    if (request.method() === "POST" && request.url().endsWith("/api/admin/intelligence/history/imports/preview")) previews.push(request.url());
  });
  await expect(page.getByLabel("History export files")).toHaveAttribute("multiple", "");
  await page.getByLabel("History export files").setInputFiles([
    { name: "Streaming_History.json", mimeType: "application/json", buffer: Buffer.from("[]") },
    { name: "ListenBrainz.jsonl", mimeType: "text/plain", buffer: Buffer.from("{}") },
  ]);
  await expect.poll(() => previews.length).toBe(2);
  await expect(page.getByText("9", { exact: true }).first()).toBeVisible();
  await expect(page.getByText("does not send them to Last.fm or ListenBrainz.", { exact: false }).first()).toBeVisible();
  await expect(page.getByRole("status").filter({ hasText: "Streaming_History.json" })).toContainText("previewed");
  await expect(page.getByRole("status").filter({ hasText: "ListenBrainz.jsonl" })).toContainText("previewed");
  const applies: Array<{ url: string; body: unknown }> = [];
  page.on("request", (request) => {
    if (request.method() === "POST" && request.url().match(/\/api\/admin\/intelligence\/history\/imports\/[^/]+\/apply$/))
      applies.push({ url: request.url(), body: request.postDataJSON() });
  });
  await expect(page.getByRole("checkbox", { name: "Include Streaming_History.json" })).toBeChecked();
  await expect(page.getByRole("checkbox", { name: "Include ListenBrainz.jsonl" })).toBeChecked();
  await page.getByRole("button", { name: "Add all 2 ready files" }).click();
  await expect.poll(() => applies.length).toBe(2);
  expect(applies.map((request) => request.url)).toEqual(expect.arrayContaining([
    expect.stringContaining("/55555555-5555-5555-5555-555555555555/apply"),
    expect.stringContaining("/66666666-6666-6666-6666-666666666666/apply"),
  ]));
  for (const request of applies) expect(request.body).toMatchObject({
    protocol: "jellyfin", backendInstanceId: "main", libraryScopeId: "music", revision: "preview-revision",
  });

  await page.getByRole("tab", { name: "Automation" }).click();
  await expect(page.getByRole("button", { name: "Generated playlist destination" })).toContainText("Jellyfin Music");
  await page.getByRole("button", { name: "New schedule" }).click();
  await page.getByLabel("Playlist name").fill("Friday discoveries");
  const cadence = page.getByRole("button", { name: "When to create it" });
  await cadence.click();
  await page.getByRole("option", { name: "Every Friday at 8:00 AM" }).click();
  await expect(cadence).toContainText("Every Friday at 8:00 AM");
  const schedule = page.waitForRequest((request) => request.method() === "POST" &&
    request.url().endsWith("/api/admin/intelligence/schedules"));
  await page.getByRole("button", { name: "Create schedule" }).click();
  expect((await schedule).postDataJSON()).toMatchObject({
    protocol: "jellyfin", backendInstanceId: "main", libraryScopeId: "music",
    name: "Friday discoveries", cronExpression: "0 8 * * 5", limit: 25,
  });
  await expect(page.getByText("Monday discoveries", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Remove" }).click();
  const removal = page.getByRole("alertdialog", { name: "Remove this automatic playlist schedule?" });
  const remove = page.waitForRequest((request) => request.method() === "DELETE" &&
    request.url().endsWith("/api/admin/intelligence/schedules/schedule-1"));
  await removal.getByRole("button", { name: "Remove schedule" }).click();
  await remove;
});

test("Intelligence keeps completed imports visible and explains retention", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.route("**/api/admin/intelligence/history/imports?*", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({ items: [{
      importId: "77777777-7777-7777-7777-777777777777",
      revision: "saved-revision",
      displayFileName: "Streaming_History_Audio_2025.json",
      sizeBytes: 4096,
      expiresAt: "2026-08-20T00:00:00Z",
      state: "completed",
      importedRows: 18_240,
      duplicateRows: 0,
      resolvedRows: 12_000,
      unresolvedRows: 6_240,
      outboundReplay: false,
      preview: {
        format: "spotify-extended-history", fileRows: 18_240, musicRows: 18_240,
        completed: 18_240, partial: 0, skipped: 0, episodes: 0, nonTrack: 0, malformed: 0,
        duplicateInFile: 0, duplicateExisting: 0, newRows: 18_240, resolvedNewRows: 12_000,
        unresolvedNewRows: 6_240, rowsWithoutProviderIdentity: 0, sourceUserCount: 1,
        estimatedMusicBrainzLookups: 6_240, earliest: "2025-01-01T00:00:00Z",
        latest: "2020-12-31T00:00:00Z", reasonCounts: {}, outsideRetentionRows: 0,
      },
    }] }),
  }));
  await page.route("**/api/admin/intelligence/history/imports/77777777-7777-7777-7777-777777777777", (route) => {
    if (route.request().method() !== "DELETE") return route.fallback();
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ removedImport: true, removedListens: 18_240 }),
    });
  });

  await page.goto("#/intelligence?section=imports");
  await expect(page.getByText("Streaming_History_Audio_2025.json", { exact: true })).toBeVisible();
  await expect(page.getByText("18,240 added", { exact: false })).toBeVisible();
  await expect(page.getByText(/no longer appear in Overview or History/)).toBeVisible();
  await page.getByRole("button", { name: "Undo import" }).click();
  const dialog = page.getByRole("alertdialog", { name: "Undo this history import?" });
  await expect(dialog).toContainText("18,240 were added");
  const removal = page.waitForRequest((request) => request.method() === "DELETE" &&
    request.url().endsWith("/api/admin/intelligence/history/imports/77777777-7777-7777-7777-777777777777"));
  await dialog.getByRole("button", { name: "Undo import" }).click();
  expect((await removal).postDataJSON()).toMatchObject({
    protocol: "jellyfin", backendInstanceId: "main", libraryScopeId: "music",
    revision: "saved-revision", confirmed: true,
  });
  await expect(page.getByText("Streaming_History_Audio_2025.json", { exact: true })).toHaveCount(0);
});

test("Intelligence empty states explain how to get results", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.route("**/api/admin/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    let body: unknown;
    if (path === "/api/admin/intelligence")
      body = { ...(responses[path] as Record<string, unknown>), candidates: [], visualization: [], generatedSets: [], providers: [] };
    else if (path === "/api/admin/intelligence/history/overview") {
      const empty = { completedListens: 0, distinctTracks: 0, distinctArtists: 0, listeningTimeMilliseconds: 0, firstListen: null };
      body = { ...(responses[path] as Record<string, unknown>), allTime: empty, selected: empty, currentStreakDays: 0, longestStreakDays: 0 };
    } else if (path === "/api/admin/intelligence/history/activity")
      body = { ...(responses[path] as Record<string, unknown>), buckets: [], currentStreakDays: 0, longestStreakDays: 0 };
    else if (path.startsWith("/api/admin/intelligence/history/top/"))
      body = { ...(responses[path] as Record<string, unknown>), items: [] };
    else if (path === "/api/admin/intelligence/history")
      body = { ...(responses[path] as Record<string, unknown>), items: [], nextCursor: null };
    else {
      await route.fallback();
      return;
    }
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
  });

  await page.goto("#/intelligence?section=unknown");
  await expect(page.getByRole("tab", { name: "Overview" })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByRole("heading", { name: "Listening overview" })).toBeVisible();
  await page.getByRole("tab", { name: "Discover" }).click();
  const sourceLink = page.getByRole("link", { name: "Connect or configure a source" });
  await expect(sourceLink).toBeVisible();
  expect((await sourceLink.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(44);
  await expect(page.getByText("Turn on automatic history in Automation, then play music or import a history file.")).toBeVisible();
  const settingsLink = page.getByRole("link", { name: "Automation", exact: true });
  expect((await settingsLink.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(44);

  await page.getByRole("tab", { name: "Overview" }).click();
  await expect(page.getByText("No completed listens were recorded for this period. Play music or import a history file to build this recap.")).toBeVisible();
  await expect(page.getByText("No activity in this period. Play music or import a history file to begin.")).toBeVisible();
  await expect(page.getByText("Nothing to rank yet. Play music or import a history file to begin.")).toBeVisible();

  await page.getByRole("tab", { name: "History", exact: true }).click();
  await expect(page.getByText("No completed listens yet")).toBeVisible();
  await expect(page.getByText("Turn on automatic history in Automation, then play music or import a history file.")).toBeVisible();
});

test("Intelligence partial and section error states stay announced", async ({ page }) => {
  await page.setViewportSize({ width: 768, height: 1024 });
  const historyRelease = routeRelease();
  let failHistory = false;
  await mockApi(page, {
    releasePath: "/api/admin/intelligence/history/activity",
    release: historyRelease.promise,
  });
  await page.route("**/api/admin/**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === "/api/admin/intelligence") {
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({
          ...(responses[url.pathname] as Record<string, unknown>),
          state: "degraded",
        }),
      });
      return;
    }
    if ((failHistory && url.pathname === "/api/admin/intelligence/history/activity") ||
      (url.pathname === "/api/admin/intelligence/history/imports/preview" && route.request().method() === "POST") ||
      (url.pathname === "/api/admin/intelligence/schedules" && route.request().method() === "POST")) {
      await route.fulfill({
        status: 503,
        contentType: "application/json",
        body: JSON.stringify({ error: "Fixture unavailable" }),
      });
      return;
    }
    await route.fallback();
  });

  await page.goto("#/intelligence?section=history");
  await expect(page.getByRole("status", { name: "Loading listening history" })).toBeVisible();
  historyRelease.release();
  await expect(page.locator(".history-list").getByText("Moon Song", { exact: true })).toBeVisible();
  await expect(page.locator(".degraded-banner")).toContainText("Existing results remain available.");

  await page.getByRole("tab", { name: "Discover" }).click();
  await expect(page.getByText("Future Song", { exact: true })).toBeVisible();
  failHistory = true;
  await page.getByRole("tab", { name: "History", exact: true }).click();
  await expect(page.locator(".history-workspace").getByRole("alert")).toContainText("Fixture unavailable");

  await page.getByRole("tab", { name: "Import", exact: true }).click();
  await page.getByLabel("History export files").setInputFiles({
    name: "Streaming_History.json", mimeType: "application/json", buffer: Buffer.from("[]"),
  });
  const queue = page.getByRole("list", { name: "Selected history exports" });
  await expect(queue).toHaveAttribute("aria-live", "polite");
  await expect(queue).toContainText("Fixture unavailable");

  await page.getByRole("tab", { name: "Automation" }).click();
  await page.getByRole("button", { name: "New schedule" }).click();
  await page.getByRole("button", { name: "Create schedule" }).click();
  await expect(page.locator(".intelligence-schedules").getByRole("alert")).toContainText("Fixture unavailable");

  const clear = page.getByRole("button", { name: "Turn off and clear" });
  await clear.click();
  const dialog = page.getByRole("alertdialog", { name: "Clear private listening data for this library?" });
  await expect(dialog).toBeVisible();
  await expect.poll(() => dialog.evaluate((element) => element.contains(document.activeElement))).toBe(true);
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  await expect(clear).toBeFocused();

  const scrollOwners = await page.locator(".intelligence-view").evaluate((element) => {
    const nested = [];
    for (let parent = element.parentElement; parent; parent = parent.parentElement) {
      const overflow = getComputedStyle(parent).overflowY;
      if (/(auto|scroll)/.test(overflow) && parent.scrollHeight > parent.clientHeight) nested.push(parent.className);
    }
    return {
      nested,
      document: document.scrollingElement!.scrollHeight > document.scrollingElement!.clientHeight,
    };
  });
  expect(scrollOwners).toEqual({ nested: [], document: true });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
});

test("Home stays inside runtime and request budgets", async ({ page }) => {
  const requests: string[] = [];
  page.on("request", (request) => requests.push(new URL(request.url()).pathname));
  await page.addInitScript(() => {
    const metrics = { cls: 0, lcp: 0, navigation: 0 };
    Object.assign(window, { __allstarrMetrics: metrics });
    new PerformanceObserver((list) => {
      metrics.lcp = Math.max(metrics.lcp, ...list.getEntries().map((entry) => entry.startTime));
    }).observe({ type: "largest-contentful-paint", buffered: true });
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) {
        if (!(entry as PerformanceEntry & { hadRecentInput?: boolean }).hadRecentInput) {
          metrics.cls += (entry as PerformanceEntry & { value: number }).value;
        }
      }
    }).observe({ type: "layout-shift", buffered: true });
  });
  await mockApi(page);
  await page.goto("#/");
  await expect(page.getByLabel("Loading Home")).toBeHidden();
  await expect(page.getByText("Linked playlists", { exact: true })).toBeVisible();
  await expect(page.getByText("Legacy .env import")).toHaveCount(0);
  await expect(page.locator(".provider-line strong", { hasText: "Lumen Audio" })).toBeVisible();
  await expect(page.getByText("Playlist Check", { exact: true })).toBeVisible();
  await expect(page.locator(".provider-line .provider-mark")).toBeVisible();
  await expect(page.locator(".activity-line .activity-artwork svg")).toBeVisible();
  const nowPlaying = page.getByRole("region", { name: "Now playing" });
  await expect(nowPlaying).toContainText("Rocket");
  await expect(nowPlaying.getByText("Tester", { exact: true })).toBeVisible();
  await expect(nowPlaying.getByText("Feishin · Desktop", { exact: true })).toBeVisible();
  await expect(nowPlaying.locator(".scrobble-state")).toContainText("Listening");

  const apiRequests = requests.filter((path) => path.startsWith("/api/admin/"));
  expect(apiRequests.length).toBeLessThanOrEqual(14);

  await page.evaluate(() => {
    const metrics = window.__allstarrMetrics;
    const start = { value: 0 };
    document.querySelector('a[href="#/library/playlists"]')?.addEventListener("click", () => {
      start.value = performance.now();
    }, { once: true });
    new MutationObserver((_, observer) => {
      if (document.querySelector("h1")?.textContent === "Library") {
        metrics.navigation = performance.now() - start.value;
        observer.disconnect();
      }
    }).observe(document.body, { childList: true, subtree: true, characterData: true });
  });
  await page.getByRole("link", { name: "Library" }).first().click();
  await expect.poll(() => page.evaluate(() =>
    window.__allstarrMetrics.navigation,
  )).toBeGreaterThan(0);

  const metrics = await page.evaluate(() => window.__allstarrMetrics);
  expect(metrics.lcp).toBeGreaterThan(0);
  expect(metrics.lcp).toBeLessThanOrEqual(2_500);
  expect(metrics.cls).toBeLessThanOrEqual(0.1);
  expect(metrics.navigation).toBeLessThanOrEqual(100);
});

test("Shared visual tokens meet typography, geometry, and contrast floors", async ({ page }) => {
  await mockApi(page);
  await page.goto("#/");
  const contract = await page.evaluate(() => {
    const root = getComputedStyle(document.documentElement);
    const eyebrowSample = document.createElement("p");
    eyebrowSample.className = "eyebrow";
    document.body.append(eyebrowSample);
    const eyebrow = getComputedStyle(eyebrowSample);
    const resolveColor = (value: string) => {
      const sample = document.createElement("span");
      sample.style.color = value;
      document.body.append(sample);
      const color = getComputedStyle(sample).color;
      sample.remove();
      return color;
    };
    const channels = (value: string) =>
      (value.match(/[\d.]+/g) ?? []).slice(0, 3).map(Number);
    const luminance = (value: string) => {
      const [red, green, blue] = channels(value).map((channel) => {
        const normalized = channel / 255;
        return normalized <= 0.04045
          ? normalized / 12.92
          : ((normalized + 0.055) / 1.055) ** 2.4;
      });
      return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
    };
    const contrast = (foreground: string, background: string) => {
      const values = [luminance(foreground), luminance(background)].sort((a, b) => b - a);
      return (values[0] + 0.05) / (values[1] + 0.05);
    };
    const canvas = resolveColor("var(--color-canvas)");
    const result = {
      spacing: ["--space-1", "--space-2", "--space-3", "--space-4"].map((token) =>
        Number.parseFloat(root.getPropertyValue(token)) * 16),
      controls: ["--control-sm", "--control-md", "--control-lg"].map((token) =>
        Number.parseFloat(root.getPropertyValue(token)) * 16),
      icons: ["--icon-sm", "--icon-md", "--icon-lg", "--icon-xl"].map((token) =>
        Number.parseFloat(root.getPropertyValue(token)) * 16),
      radii: ["--radius-sm", "--radius-md", "--radius-lg"].map((token) =>
        Number.parseFloat(root.getPropertyValue(token)) * 16),
      dataGeometry: ["--data-row-height", "--data-artwork-size"].map((token) =>
        Number.parseFloat(root.getPropertyValue(token)) * 16),
      fonts: [Number.parseFloat(root.getPropertyValue("--text-sm")) * 16, Number.parseFloat(eyebrow.fontSize)],
      contrast: {
        body: contrast(getComputedStyle(document.body).color, canvas),
        metadata: contrast(eyebrow.color, canvas),
        primary: contrast(resolveColor("var(--color-on-signal)"), resolveColor("var(--color-signal)")),
        focus: contrast(resolveColor("var(--focus-ring)"), canvas),
      },
    };
    eyebrowSample.remove();
    return result;
  });
  expect(contract.spacing).toEqual([4, 8, 12, 16]);
  expect(contract.controls).toEqual([36, 44, 48]);
  expect(contract.icons).toEqual([16, 18, 20, 24]);
  expect(contract.radii).toEqual([8, 12, 16]);
  expect(contract.dataGeometry).toEqual([70.4, 44]);
  expect(contract.fonts[0]).toBeGreaterThanOrEqual(14);
  expect(contract.fonts[1]).toBeGreaterThanOrEqual(12);
  expect(contract.contrast.body).toBeGreaterThanOrEqual(4.5);
  expect(contract.contrast.metadata).toBeGreaterThanOrEqual(4.5);
  expect(contract.contrast.primary).toBeGreaterThanOrEqual(4.5);
  expect(contract.contrast.focus).toBeGreaterThanOrEqual(3);
});

test("Slim sidebar centers navigation and profile controls", async ({ page }) => {
  await page.setViewportSize({ width: 835, height: 762 });
  await mockApi(page);
  await page.goto("#/");
  const sidebar = await page.locator(".sidebar").boundingBox();
  const home = await page.getByRole("link", { name: "Home", exact: true }).boundingBox();
  const sourcesIcon = await page.getByRole("link", { name: "Integrations" }).locator("svg").boundingBox();
  const profile = await page.getByRole("link", { name: "Settings for Tester" }).boundingBox();
  expect(sidebar && home && sourcesIcon && profile).toBeTruthy();
  const center = (box: NonNullable<typeof sidebar>) => box.x + box.width / 2;
  expect(Math.abs(center(home!) - center(sidebar!))).toBeLessThanOrEqual(1);
  expect(Math.abs(center(sourcesIcon!) - center(sidebar!))).toBeLessThanOrEqual(1);
  expect(Math.abs(center(profile!) - center(sidebar!))).toBeLessThanOrEqual(1);
});

test("Shared selects and settings tabs animate without remounting", async ({ page }) => {
  await mockApi(page);
  await page.goto("#/settings/general");
  const selectedNav = page.getByRole("navigation", { name: "Primary" })
    .getByRole("link", { name: "Settings", exact: true });
  const selectedNavBackground = await selectedNav.evaluate((element) =>
    getComputedStyle(element).backgroundColor);
  await selectedNav.hover();
  await expect(selectedNav).toHaveCSS("background-color", selectedNavBackground);
  await page.getByLabel("Color theme").click();
  await expect(page.locator(".select-content")).toHaveCSS("animation-name", "dropdown-in");
  await page.keyboard.press("Escape");
  await page.goto("#/integrations/services");
  const tabs = page.locator(".settings-tabs");
  await tabs.evaluate((element) => element.setAttribute("data-instance", "stable"));
  const routing = page.getByRole("tab", { name: "Routing" });
  await routing.hover();
  await expect(routing).toHaveCSS("border-top-left-radius", "8px");
  await routing.click();
  await expect(tabs).toHaveAttribute("data-instance", "stable");
  expect(await routing.evaluate((element) => getComputedStyle(element, "::after").height)).toBe("3px");
  expect(await routing.evaluate((element) => getComputedStyle(element, "::after").backgroundColor))
    .toBe(await page.evaluate(() => {
      const sample = document.createElement("span");
      sample.style.color = "var(--color-signal)";
      document.body.append(sample);
      const value = getComputedStyle(sample).color;
      sample.remove();
      return value;
    }));
});

test("Legacy Library links open their current shared views", async ({ page }) => {
  await mockApi(page);
  for (const route of ["#/library", "#/library/link", "#/library/injected", "#/library/external"]) {
    await page.goto(route);
    await expect(page.getByText("Linked playlists", { exact: true })).toBeVisible();
    await expect(page.getByRole("tab", { name: "Playlists" })).toHaveAttribute("aria-selected", "true");
  }
  for (const route of ["#/library/missing", "#/library/migration"]) {
    await page.goto(route);
    await expect(page.getByRole("tab", { name: "Mappings" })).toHaveAttribute("aria-selected", "true");
  }
});

test("Legacy integration links open their canonical tabs", async ({ page }) => {
  await mockApi(page);
  for (const [route, tab] of [
    ["#/sources", "Services"],
    ["#/settings/accounts", "Accounts"],
    ["#/settings/extensions", "Extensions"],
    ["#/settings/routing", "Routing"],
  ] as const) {
    await page.goto(route);
    await expect(page.getByRole("heading", { name: "Integrations", level: 1 })).toBeVisible();
    await expect(page.getByRole("tab", { name: tab })).toHaveAttribute("aria-selected", "true");
  }
});

test("extension updates explain access changes on mobile", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  const basePackage = {
    extensionId: "lumen-audio", displayName: "Lumen Audio", lifecycle: "disabled",
    active: false, installed: true, permissionReviewRequired: false, capabilities: ["metadata"],
    revision: 1,
  };
  await page.route("**/api/admin/extensions/packages", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([
      { ...basePackage, id: "old", version: "1.0.0", state: "disabled", stagedAt: "2026-01-01" },
      {
        ...basePackage, id: "new", version: "2.0.0", state: "reviewRequired",
        previousPackageId: "old", permissionReviewRequired: true,
        capabilities: ["metadata", "streaming"], stagedAt: "2026-02-01",
      },
    ]),
  }));
  const permission = (id: string, kind: string, value: string) => ({
    id, permissionKind: kind, permissionValue: value, required: true, decision: "pending",
  });
  await page.route("**/packages/old/permissions", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([permission("old-network", "network", "https://api.example.test/"), permission("old-cache", "cache", "metadata")]),
  }));
  await page.route("**/packages/new/permissions", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([permission("new-network", "network", "https://api.example.test/"), permission("new-secret", "secret", "accountToken")]),
  }));
  await page.route("**/packages/new/review", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      ...basePackage, id: "new", version: "2.0.0", state: "staged",
      previousPackageId: "old", revision: 2,
    }),
  }));
  let activationRequests = 0;
  let releaseActivation = () => {};
  const activationResponse = new Promise<void>((resolve) => {
    releaseActivation = resolve;
  });
  await page.route("**/packages/new/activate", async (route) => {
    activationRequests++;
    if (activationRequests === 1) {
      await activationResponse;
      return route.fulfill({
        status: 500,
        contentType: "application/json",
        body: JSON.stringify({ error: "Runtime failed to start." }),
      });
    }
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        ...basePackage, id: "new", version: "2.0.0", state: "active",
        active: true, previousPackageId: "old", revision: 3,
      }),
    });
  });

  await page.goto("#/settings/extensions");
  await page.getByRole("button", { name: "Review permissions" }).click();
  let review = page.getByRole("dialog", { name: "Review permissions" });
  await expect(review).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(review).toBeHidden();
  await page.getByRole("button", { name: "Review permissions" }).click();
  review = page.getByRole("dialog", { name: "Review permissions" });
  await expect(review.getByText("Update 1.0.0 → 2.0.0. Capability and permission changes are shown below.")).toBeVisible();
  await expect(review.getByText("New access", { exact: false })).toBeVisible();
  await expect(review.getByText("Removed access", { exact: false })).toBeVisible();
  await expect(review.getByRole("button", { name: "Save review" })).toBeInViewport();
  await review.getByRole("button", { name: "Allow all" }).click();
  await review.getByRole("checkbox").check();
  await review.getByRole("button", { name: "Save review" }).click();
  const activation = page.getByRole("alertdialog", { name: "Activate Lumen Audio?" });
  await expect(activation).toBeVisible();
  expect(activationRequests).toBe(0);
  await activation.getByRole("button", { name: "Activate extension" }).click();
  await expect(activation.getByRole("button", { name: "Activating…" })).toBeDisabled();
  await expect.poll(() => activationRequests).toBe(1);
  releaseActivation();
  await expect(activation.getByRole("alert")).toHaveText("Runtime failed to start.");
  await activation.getByRole("button", { name: "Activate extension" }).click();
  await expect(activation).toBeHidden();
  expect(activationRequests).toBe(2);
});

test("extension updates stay beside the shared management menu", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.route("**/api/admin/extensions/packages", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([{
      id: "old", extensionId: "lumen-audio", displayName: "Lumen Audio",
      version: "1.0.0", lifecycle: "active", state: "active", active: true,
      installed: true, permissionReviewRequired: false, hasPermissions: true, capabilities: ["metadata"],
      previousPackageId: "previous", registryId: "registry", stagedAt: "2026-01-01", revision: 1,
    }]),
  }));
  await page.route("**/api/admin/extensions/registries", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([{
      id: "registry", name: "Community", registryUrl: "https://example.test/registry.json",
      enabled: true, revision: 1,
    }]),
  }));
  await page.route("**/api/admin/extensions/store", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      items: [{
        id: "lumen-audio", displayName: "Lumen Audio", version: "2.0.0",
        downloadUrl: "https://example.test/lumen.zip", sha256: "a".repeat(64),
        registryId: null, types: ["metadata", "streaming"],
      }],
      errors: [],
    }),
  }));
  let reviewRequests = 0;
  await page.route("**/api/admin/extensions/packages/old/permissions/revoke", (route) => {
    reviewRequests += 1;
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        id: "old", extensionId: "lumen-audio", displayName: "Lumen Audio",
        version: "1.0.0", lifecycle: "reviewrequired", state: "reviewrequired",
        active: false, installed: false, permissionReviewRequired: true, hasPermissions: true,
        capabilities: ["metadata"], registryId: "registry", revision: 2,
      }),
    });
  });
  await page.route("**/api/admin/extensions/packages/old/permissions", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([{
      id: "permission", permissionKind: "network", permissionValue: "https://example.test/",
      required: true, decision: "pending",
    }]),
  }));

  await page.goto("#/settings/extensions");
  const actions = page.locator(".extension-actions");
  await expect(actions.getByRole("button", { name: "Update" })).toBeInViewport();
  await actions.getByRole("button", { name: "Manage extension" }).click();
  await expect(page.getByRole("menuitem", { name: "Disable" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Rollback" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Review access" })).toBeVisible();
  await page.getByRole("menuitem", { name: "Review access" }).click();
  const access = page.getByRole("alertdialog", { name: "Review access for Lumen Audio?" });
  await expect(access).toContainText("runtime will stop");
  await access.getByRole("button", { name: "Stop and review" }).click();
  await expect.poll(() => reviewRequests).toBe(1);
  const review = page.getByRole("dialog", { name: "Review permissions" });
  await expect(review).toBeVisible();
  await review.getByRole("button", { name: "Close review" }).click();
  await actions.getByRole("button", { name: "Manage extension" }).click();
  await expect(page.getByRole("menuitem", { name: "Uninstall" })).toBeVisible();
  await page.getByRole("menuitem", { name: "Uninstall" }).click();
  const uninstall = page.getByRole("alertdialog", { name: "Uninstall Lumen Audio?" });
  await expect(uninstall).toBeVisible();
  await uninstall.getByRole("button", { name: "Cancel" }).click();
  await page.getByRole("tab", { name: /Registries/ }).click();
  await expect(page.getByText("1 installed package version must be removed first.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Remove" })).toBeDisabled();
});

test("Add playlist separates source, client view, destination, and sync on mobile", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const sources = routeRelease();
  await mockApi(page, { releasePath: "/api/admin/playlist-sources", release: sources.promise });
  await page.goto("#/library/playlists");
  const add = page.getByRole("button", { name: "Add playlist" });
  await add.click();
  const dialog = page.getByRole("dialog", { name: "Link a playlist" });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByText("Loading playlist sources…")).toBeVisible();
  sources.release();
  await expect(dialog.locator(".playlist-source-groups legend")).toHaveCount(5);
  const stepTops = await dialog.locator(".playlist-add-steps button").evaluateAll((buttons) =>
    buttons.map((button) => Math.round(button.getBoundingClientRect().top)));
  expect(new Set(stepTops).size).toBe(1);
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  await expect(add).toBeFocused();
  await add.click();
  const sourceGroups = dialog.locator(".playlist-source-groups legend");
  await expect(sourceGroups).toHaveCount(5);
  expect(await sourceGroups.allTextContents()).toEqual([
    "Jellyfin", "Subsonic", "Spotify", "Lumen Audio", "Qobuz",
  ]);
  await dialog.getByRole("radio", { name: /Spotify/ }).check();
  await expect(dialog.locator(".playlist-art > span")).toBeVisible();
  await dialog.getByRole("button", { name: "Load more" }).click();
  await expect(dialog.getByRole("radio", { name: /Second Mix/ })).toBeVisible();
  await dialog.getByRole("radio", { name: /Source Mix/ }).check();
  await dialog.getByRole("button", { name: "Continue" }).click();
  if (process.env.ALLSTARR_SCREENSHOT_DIR)
    await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/playlist-390-listener-choice.png` });
  await dialog.getByRole("radio", { name: /Every song from Spotify Keep the songs/ }).check();
  await dialog.getByRole("button", { name: "Continue" }).click();
  await expect.poll(() => dialog.locator(".playlist-add-body").evaluate((body) => body.scrollTop)).toBe(0);
  await dialog.getByRole("radio", { name: /Show through Allstarr and add songs to.*Allstarr will show/ }).check();
  await expect(dialog.getByRole("radio", { name: /Road trip/ })).toBeVisible();
  await dialog.getByRole("radio", { name: /Road trip/ }).check();
  if (process.env.ALLSTARR_SCREENSHOT_DIR) {
    await dialog.locator(".playlist-add-body").evaluate((body) => body.scrollTo({ top: 0 }));
    await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/playlist-390-appearance-choice.png` });
  }
  await dialog.getByRole("button", { name: "Continue" }).click();
  await dialog.getByRole("button", { name: "Automatic updates" }).click();
  await page.getByRole("option", { name: "Daily at 3:00 AM" }).click();
  await expect(dialog.getByRole("button", { name: "Link playlist" })).toBeInViewport();
  await expect(dialog.locator(".playlist-add-body")).toHaveCSS("overflow-y", "auto");
  const create = page.waitForRequest((request) =>
    request.method() === "POST" && request.url().endsWith("/api/admin/playlist-links"));
  const scheduled = page.waitForRequest((request) =>
    request.method() === "POST" && request.url().endsWith("/schedules"));
  await dialog.getByRole("button", { name: "Link playlist" }).click();
  const input = (await create).postDataJSON();
  expect(input).toMatchObject({
    libraryScopeId: "music",
    targetPlaylistId: "jellyfin-playlist",
    sourcePlaylistId: "playlist",
    mode: "hybrid",
    projectionMode: "source",
  });
  expect((await scheduled).postDataJSON().cronExpression).toBe("0 3 * * *");
  await expect(dialog).toBeHidden();
});

test("Playlist views and revisioned settings stay keyboard-safe", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await mockApi(page);
  let listRevision = 1;
  await page.route("**/api/admin/playlist-links", (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    const response = responses["/api/admin/playlist-links"] as { playlistLinks: Record<string, unknown>[] };
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ playlistLinks: response.playlistLinks.map((item) => ({ ...item, revision: listRevision })) }),
    });
  });
  await page.route("**/api/admin/media-targets", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({ targets: [
      {
        id: "wrong-credential", protocol: "jellyfin", backendInstanceId: "main",
        libraryScopeId: "music", credentialReferenceId: "wrong", displayName: "Wrong credential",
      },
      ...(responses["/api/admin/media-targets"] as { targets: Record<string, unknown>[] }).targets,
    ] }),
  }));
  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
  const details = page.getByRole("dialog", { name: "Test playlist" });
  await expect(details).toBeVisible();
  const resolved = details.getByRole("tab", { name: "Jellyfin when available", exact: true });
  await resolved.focus();
  await page.keyboard.press("ArrowRight");
  await expect(details.getByRole("tab", { name: "Every song from Lumen Audio", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect(details.getByText("source track", { exact: true })).toBeVisible();
  await page.keyboard.press("ArrowRight");
  await expect(details.getByRole("tab", { name: "Jellyfin playlist", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect(details.getByText("target track", { exact: true })).toBeVisible();
  await expect(details).toHaveCSS("overflow", "hidden");
  await expect(details.locator(".track-scroll")).toHaveCSS("overflow", "auto");

  const exactTarget = page.waitForRequest((request) =>
    request.url().includes("/api/admin/media-targets/22222222-2222-2222-2222-222222222222/playlists"));
  await details.getByRole("button", { name: "Actions" }).click();
  await page.getByRole("menuitem", { name: "Edit settings" }).click();
  let settings = page.getByRole("dialog", { name: "Edit playlist settings" });
  await exactTarget;
  await settings.getByRole("radio", { name: /Every song from Lumen Audio Keep the songs/ }).check();
  await settings.getByRole("radio", { name: /Show only through Allstarr Allstarr will show/ }).check();
  const update = page.waitForRequest((request) =>
    request.method() === "PUT" && request.url().endsWith("/api/admin/playlist-links/playlist-link"));
  await settings.getByRole("button", { name: "Save settings" }).click();
  expect((await update).postDataJSON()).toMatchObject({
    expectedRevision: 1,
    mode: "virtual",
    projectionMode: "source",
    targetPlaylistId: null,
  });
  await expect(settings).toBeHidden();

  await page.route("**/api/admin/playlist-links/playlist-link", async (route) => {
    if (route.request().method() === "PUT") {
      await route.fulfill({
        status: 409,
        contentType: "application/json",
        body: JSON.stringify({ error: "The resource changed before this update" }),
      });
      return;
    }
    await route.fallback();
  });
  await details.getByRole("button", { name: "Actions" }).click();
  await page.getByRole("menuitem", { name: "Edit settings" }).click();
  settings = page.getByRole("dialog", { name: "Edit playlist settings" });
  await settings.getByRole("button", { name: "Save settings" }).click();
  await expect(settings.getByRole("alert")).toContainText("resource changed");
  listRevision = 2;
  await page.locator(".playlist-toolbar-actions").getByRole("button", { name: "Refresh playlists" })
    .evaluate((button) => (button as HTMLButtonElement).click());
  await expect(settings.getByRole("alert")).toContainText("changed while you were editing");
  await expect(settings.getByRole("button", { name: "Save settings" })).toBeDisabled();
  await page.keyboard.press("Escape");
  await expect(settings).toBeHidden();
  await expect(details).toBeVisible();

  await page.route("**/api/admin/playlist-links/playlist-link?projectionMode=source", (route) => route.fulfill({
    status: 503,
    contentType: "application/json",
    body: JSON.stringify({ error: "Projection fixture unavailable" }),
  }), { times: 1 });
  await details.getByRole("tab", { name: "Lumen Audio" }).click();
  const projectionError = details.getByRole("alert");
  await expect(projectionError).toContainText("Projection fixture unavailable");
  await projectionError.getByRole("button", { name: "Try again" }).click();
  await expect(details.getByText("source track", { exact: true })).toBeVisible();
});

for (const viewport of [
  { width: 390, height: 844 },
  { width: 820, height: 900 },
  { width: 1280, height: 800 },
]) {
test(`Provider playlist update names both playlists and requires the checked confirmation at ${viewport.width}px`, async ({ page }) => {
  await page.setViewportSize(viewport);
  await mockApi(page);
  const response = responses["/api/admin/playlist-links"] as { playlistLinks: Record<string, unknown>[] };
  await page.route("**/api/admin/playlist-links", (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        playlistLinks: response.playlistLinks.map((item) => ({
          ...item,
          sourceProviderId: "spotify",
          sourceUpdateAvailable: true,
        })),
      }),
    });
  });

  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
  const details = page.getByRole("dialog", { name: "Test playlist" });
  await details.getByRole("button", { name: "Actions" }).click();
  await page.getByRole("menuitem", { name: "Preview changes to Spotify" }).click();

  const preview = page.getByRole("dialog", { name: "Update Spotify?" });
  await expect(preview).toBeVisible();
  await expect(preview).toContainText("Allstarr will update “Road trip source” in Spotify to match “Road trip” in Jellyfin.");
  await expect(preview).toContainText("Allstarr will not change “Road trip” in Jellyfin.");
  await expect(preview).toContainText("If either changes before this runs, nothing will be updated.");
  await expect(preview.getByRole("definition")).toHaveCount(4);
  await expect(preview.getByText("Local only")).toBeVisible();
  await expect(preview).not.toContainText(/write-back|hybrid|native playlist/i);
  await expect(preview.locator(".playlist-source-update-body")).toHaveCSS("overflow", "auto");
  await expect(preview.getByRole("button", { name: "Update Spotify" })).toBeInViewport();
  if (process.env.ALLSTARR_SCREENSHOT_DIR)
    await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/playlist-${viewport.width}-update-spotify.png` });

  const request = page.waitForRequest((item) =>
    item.method() === "POST" && item.url().endsWith("/api/admin/playlist-links/playlist-link/source-update/apply"));
  await preview.getByRole("button", { name: "Update Spotify" }).click();
  expect((await request).postDataJSON()).toEqual({
    expectedRevision: 1,
    confirmationId: "a".repeat(64),
  });
  await expect(preview).toBeHidden();
  await expect(details.getByText("Spotify update queued.")).toBeVisible();
});
}

test("Tentative mappings sort by confidence and deep links open review", async ({ page }) => {
  await mockApi(page);
  const delayedProvider = routeRelease();
  let localSearches = 0;
  await page.route("**/api/admin/track-matches/targets/local?*", (route) => {
    localSearches += 1;
    const hasLocalResult = new URL(route.request().url()).searchParams.get("query") === "Kiss Me More";
    return route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        tracks: hasLocalResult ? [{
          id: "jellyfin-kiss-me-more", backendItemId: "jellyfin-kiss-me-more",
          title: "Kiss Me More", artist: "Doja Cat feat. SZA", album: "Planet Her",
          durationMilliseconds: 208_000, confidence: 0.91,
          components: { localPreference: 0.07, preferenceScore: 0.98 },
        }] : [],
      }),
    });
  });
  await page.route("**/api/admin/track-matches/targets/provider?*", async (route) => {
    await delayedProvider.promise;
    return route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        tracks: [{
          id: "provider-kiss-me-more", externalId: "provider-kiss-me-more",
          externalProvider: "lumen-audio", title: "Kiss Me More",
          artist: "Doja Cat feat. SZA", album: "Planet Her",
          durationMilliseconds: 208_000, confidence: 0.94,
          components: { localPreference: 0.07, preferenceScore: 0.98 },
        }],
        providers: ["lumen-audio", "qobuz"],
      }),
    });
  });
  await page.goto("#/library/mappings?search=Test%20song&review=snapshot");
  const dialog = page.getByRole("dialog", { name: "Test song" });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByLabel("Search local library and playable providers")).toHaveValue("Test song");
  await expect(dialog.getByText("ISRC US-AAA-26-00001")).toHaveCount(2);
  await expect(dialog.locator(".candidate-provider").filter({ hasText: "Apple Music – GAMDL" })).toBeVisible();
  await expect(dialog.getByText("MusicBrainz album")).toHaveCount(0);
  await expect(dialog.locator(".candidate-card .mapping-art > span").first()).toBeVisible();
  await expect(
    dialog.locator(".automatic-candidates .candidate-provider")
      .filter({ hasText: "Jellyfin · +7% local boost" }),
  ).toBeVisible();
  await expect(dialog.locator(".automatic-candidates .candidate-confidence").getByText("89%")).toBeVisible();
  await dialog.getByLabel("Search local library and playable providers").fill("Kiss Me More");
  await dialog.getByRole("button", { name: "Search", exact: true }).click();
  await expect(dialog.getByRole("button", { name: "Searching…" })).toBeVisible();
  await expect(dialog.locator(".provider-result-summary")).toHaveCount(0);
  delayedProvider.release();
  await expect.poll(() => localSearches).toBe(1);
  await expect(dialog.getByRole("button", { name: /Jellyfin 1/ })).toBeVisible();
  await expect(dialog.getByRole("button", { name: /Lumen Audio 1/ })).toBeVisible();
  await expect(dialog.getByRole("button", { name: /Qobuz/ })).toHaveCount(0);
  await expect(dialog.getByText("Planet Her")).toHaveCount(2);
  await expect(dialog.locator(".target-results > button .target-score")).toHaveCount(2);
  await expect(dialog.locator(".target-results").getByText("· +7% local boost")).toHaveCount(1);
  await expect(dialog.locator(".target-results")).toHaveCSS("overflow-y", "visible");
  await expect(dialog.locator(":scope > footer")).toHaveCSS("position", "sticky");
  await expect(
    dialog.locator(".target-results > button").filter({ hasText: "Jellyfin" })
      .locator(".target-score").getByText("98%"),
  ).toBeVisible();
  await expect(dialog.getByText("rank #1")).toBeVisible();
  await dialog.locator(".candidate-card").first().getByText("Full scoring evidence").click();
  await expect(dialog.locator(".candidate-card").first().getByText("Candidate ID")).toBeVisible();
  await expect(dialog.locator(".candidate-card").first().getByText("Artist overlap")).toBeVisible();
  await expect(dialog.locator(".candidate-card").first().getByText("Duration difference")).toBeVisible();
  await expect(dialog.locator(".candidate-card").first().getByText("Apple Music – GAMDL track ID")).toBeVisible();
  await dialog.locator(".candidate-card").last().getByText("Full scoring evidence").click();
  await expect(dialog.locator(".candidate-card").last().getByText("preference score")).toBeVisible();
  await dialog.getByLabel("Search local library and playable providers").fill("No local copy");
  await dialog.getByRole("button", { name: "Search", exact: true }).click();
  await expect.poll(() => localSearches).toBe(2);
  await expect(dialog.getByRole("button", { name: /Jellyfin 0/ })).toBeVisible();
  await expect(dialog.getByRole("button", { name: /Lumen Audio 1/ })).toBeVisible();
  await dialog.getByRole("button", { name: "Close match dialog" }).click();

  const request = page.waitForRequest((item) =>
    item.url().includes("/api/admin/track-matches") &&
    new URL(item.url()).searchParams.get("sort") === "confidence_desc");
  await page.getByRole("button", { name: "Confidence", exact: true }).click();
  await page.getByRole("option", { name: "Highest first" }).click();
  await request;
  const unresolved = page.waitForRequest((item) =>
    item.url().includes("/api/admin/track-matches") &&
    new URL(item.url()).searchParams.get("state") === "unresolved");
  await page.getByRole("tab", { name: "Unresolved 0" }).click();
  await unresolved;
  const attention = page.waitForRequest((item) =>
    item.url().includes("/api/admin/track-matches") &&
    new URL(item.url()).searchParams.get("state") === "attention");
  await page.getByRole("tab", { name: "Review 1" }).click();
  await attention;
  await expect(page.getByRole("button", { name: "Accept" })).toBeVisible();
  await page.getByRole("button", { name: "Accept" }).click();
  await expect(page.locator(".mapping-row")).toHaveCount(0);
});

test("Mappings keep mobile tabs, evidence, and actions readable", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.goto("#/library/mappings");

  for (const label of ["Library sections", "Mapping views"]) {
    const tabs = page.getByRole("navigation", { name: label }).getByRole("tab");
    expect(await tabs.evaluateAll((items) => items.every((item) => item.scrollWidth <= item.clientWidth))).toBe(true);
  }

  const row = page.locator(".mapping-row").first();
  await row.scrollIntoViewIfNeeded();
  await expect(row.getByRole("button", { name: "Accept" })).toBeInViewport();
  await expect(row.getByRole("button", { name: "Interactive search" })).toBeInViewport();
  await expect(row.locator(".mapping-evidence-summary")).toContainText("title match");
  expect(await row.locator(".mapping-evidence-summary").evaluate((item) => item.scrollWidth <= item.clientWidth)).toBe(true);

  const more = row.getByRole("button", { name: "More actions for Test song - Remix" });
  await expect(more).toBeInViewport();
  await more.click();
  await expect(page.getByRole("menuitem", { name: "Rematch" })).toBeVisible();
  await page.keyboard.press("Escape");
});

test("Shared search fields reserve icon space", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);

  for (const [route, label] of [
    ["#/library/playlists", "Filter playlists"],
    ["#/library/mappings", "Search"],
    ["#/library/cached", "Filter cached tracks"],
    ["#/activity", "Search"],
  ] as const) {
    await page.goto(route);
    if (route === "#/activity") await page.getByText("Filters", { exact: true }).click();
    const input = page.getByRole("searchbox", { name: label });
    await expect(input).toBeVisible();
    const spacing = await input.evaluate((element) => {
      const inputBounds = element.getBoundingClientRect();
      const iconBounds = element.previousElementSibling!.getBoundingClientRect();
      return {
        iconRight: iconBounds.right,
        textStart: inputBounds.left + Number.parseFloat(getComputedStyle(element).paddingLeft),
      };
    });
    expect(spacing.iconRight).toBeLessThanOrEqual(spacing.textStart);
  }

  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Add playlist" }).click();
  const dialog = page.getByRole("dialog", { name: "Link a playlist" });
  await dialog.getByRole("radio", { name: /Spotify/ }).check();
  await expect(dialog.getByRole("searchbox", { name: "Find a source playlist" })).toBeVisible();
});

test("Event log groups matching work and preserves actionable history", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.route("**/api/admin/ui/activity?*", (route) => {
    const url = new URL(route.request().url());
    const older = url.searchParams.has("before");
    const items = older
      ? [{
          id: "older", kind: "job", source: "system", label: "Playlist sync",
          state: "succeeded", detail: "Generation 1", occurredAt: "2026-01-01T00:00:00Z",
        }]
      : ["First", "First", "Second"].map((playlistName, index) => ({
          id: `match-${index}`, kind: "matching", source: "lumen-audio",
          providerId: "lumen-audio", label: "Track matched", state: "accepted",
          detail: `Song ${index}`, occurredAt: `2026-01-02T00:00:0${2 - index}Z`,
          correlationId: "job-1", action: "track-match.evaluate", playlistName,
          sourceTitle: `Song ${index}`, targetProviderId: "library",
          targetTitle: `Local Song ${index}`, confidenceLabel: "96%",
          sourceProviderTrackId: `provider-${index}`, backendItemId: `backend-${index}`,
          artworkUrl: `/artwork-${index}.jpg`,
          technicalDetails: { titleSimilarity: "0.98" },
        }));
    return route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        items,
        hasMore: !older,
        nextCursor: older ? null : "2026-01-02T00:00:00Z",
        nextCursorId: older ? null : "match-2",
      }),
    });
  });
  await page.goto("#/activity");

  await expect(page.getByText("Matched 3 tracks across 2 playlists")).toBeVisible();
  await expect(page.locator(".event-log-group summary .event-kind-icon img")).toHaveCount(0);
  await expect(page.locator(".event-log-group summary .event-kind-icon svg")).toBeVisible();
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  if (process.env.ALLSTARR_SCREENSHOT_DIR)
    await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/activity-populated-390.png`, fullPage: true });
  await expect(page.getByLabel("Search")).toBeHidden();
  await page.getByText("Filters", { exact: true }).click();
  await expect(page.getByLabel("Search")).toBeVisible();
  await page.getByLabel("Search").fill("missing event");
  await expect(page.getByText("No events match these filters")).toBeVisible();
  await page.getByRole("button", { name: "Reset filters" }).click();

  await page.getByRole("button", { name: "Load earlier events" }).click();
  await expect(page.getByText("4 events retained in this view")).toBeVisible();
  const group = page.locator(".event-log-group").first();
  await group.locator(":scope > summary").focus();
  await page.keyboard.press("Enter");
  await expect(group.locator(".event-child .event-art > span").first()).toBeVisible();
  const technical = page.getByText("Technical details").first();
  await expect(technical).toBeVisible();
  await expect(page.getByText("Title Similarity").first()).toBeHidden();
  await technical.click();
  await expect(page.getByText("Media server item ID").first()).toBeVisible();
  await expect(page.getByText("Title Similarity").first()).toBeVisible();
  await page.getByRole("button", { name: "Refresh" }).click();
  await expect(group).toHaveAttribute("open", "");
  await page.getByRole("link", { name: "Open related view" }).first().click();
  await expect(page).toHaveURL(/#\/library\/mappings\?search=Song%200$/);
});

test("Cached and Kept keep media facts and actions readable on mobile", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.goto("#/library/cached");
  await expect(page.getByRole("button", { name: "Provider" })).toContainText("All providers");
  const cached = page.locator(".download-row");
  await expect(cached.locator(".download-art .provider-mark")).toBeVisible();
  await expect(cached.getByText("FLAC · 900 kbps · 16-bit · 44.1 kHz · 2 ch")).toBeVisible();
  const fileFacts = cached.locator(".download-file");
  await expect(fileFacts).toHaveAttribute("aria-label", /Size 1000 KiB\. Updated /);
  await expect(fileFacts.getByText("1000 KiB", { exact: true })).toBeVisible();
  await expect(cached.getByRole("link", { name: "Download" })).toBeVisible();
  await expect(cached.getByRole("button", { name: "Keep" })).toBeVisible();
  await expect(cached.getByRole("button", { name: "Remove" })).toBeVisible();
  await expect(cached.getByText(/Track cache/)).toBeVisible();

  await page.goto("#/library/kept");
  const kept = page.locator(".download-row");
  await expect(kept.getByRole("button", { name: "Keep" })).toHaveCount(0);
  const downloadWidth = (await kept.getByRole("link", { name: "Download" }).boundingBox())?.width ?? 0;
  const removeWidth = (await kept.getByRole("button", { name: "Remove" }).boundingBox())?.width ?? 0;
  expect(Math.abs(downloadWidth - removeWidth)).toBeLessThanOrEqual(1);
  await kept.getByRole("button", { name: "Remove" }).click();
  await expect(page.getByRole("alertdialog", { name: "Remove this track?" })).toBeVisible();
});

test("Cached owns track storage and retention controls", async ({ page }) => {
  await mockApi(page);
  let saved: Record<string, string> | null = null;
  await page.route("**/api/admin/config", (route) => {
    if (route.request().method() === "POST") {
      saved = route.request().postDataJSON().updates;
      return route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({ message: "Runtime configuration updated.", updatedKeys: Object.keys(saved ?? {}) }),
      });
    }
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify(responses["/api/admin/config"]),
    });
  });

  await page.goto("#/library/cached");
  await expect(page.getByText("Track cache behavior")).toBeVisible();
  await expect(page.getByText("Cache mode", { exact: true })).toBeVisible();
  await page.getByText("Track cache behavior").click();
  await expect(page.locator('input[name="CACHE_DURATION_HOURS"]')).toHaveValue("24");
  await expect(page.locator('input[name="CACHE_TRANSCODE_MINUTES"]')).toHaveValue("60");
  await page.locator('input[name="CACHE_DURATION_HOURS"]').fill("48");
  await page.getByRole("button", { name: "Save track cache" }).click();
  await expect.poll(() => saved).toEqual({
    STORAGE_MODE: "Cache",
    CACHE_DURATION_HOURS: "48",
    CACHE_TRANSCODE_MINUTES: "60",
  });
  await expect(page.getByText("Track cache settings saved.")).toBeVisible();

  await page.goto("#/settings/general");
  await expect(page.getByRole("button", { name: "Storage mode" })).toHaveCount(0);
  await expect(page.getByText("Track cache hours")).toHaveCount(0);
  await expect(page.getByText("Transcode cache minutes")).toHaveCount(0);
});

test("Integrations keep primary actions visible and report scoped degradation", async ({ page, context }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  let appleState = {
    state: "needs_login", ready: false, staged: true, daemon_running: true,
    wrapper_healthy: true, logged_in: false, login_state: "logged_out", api_version: "2",
  };
  await page.route("**/api/admin/apple-download/status", (route) => route.fulfill({
    contentType: "application/json", body: JSON.stringify(appleState),
  }));
  await page.route("**/api/admin/apple-download/login", (route) => {
    appleState = { ...appleState, state: "needs_login", login_state: "awaiting_2fa" };
    return route.fulfill({ contentType: "application/json", body: JSON.stringify(appleState) });
  });
  await page.route("**/api/admin/apple-download/login/2fa", (route) => {
    appleState = { ...appleState, state: "ready", ready: true, logged_in: true, login_state: "authenticated" };
    return route.fulfill({ contentType: "application/json", body: JSON.stringify(appleState) });
  });
  let appleCtsRequest: unknown;
  await page.route("**/api/admin/provider-diagnostics/deep-stream", async (route) => {
    appleCtsRequest = route.request().postDataJSON();
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ succeeded: true, providerId: "apple-download", clickToStreamMilliseconds: 42 }),
    });
  });
  await page.route("**/api/admin/ui/schema", (route) => route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify({
      ...schema,
      providers: [
        ...schema.providers,
        { id: "disabled-source", name: "Disabled Source", status: "disabled", categories: ["metadata"] },
      ],
    }),
  }));
  await page.goto("#/sources");
  const lumenSource = page.locator(".sources-table tr").filter({ hasText: "Lumen Audio" });
  await expect(lumenSource.locator(".operational-mobile-detail dd").filter({ hasText: "Awaiting first CTS sample" })).toHaveText("Awaiting first CTS sample");
  const tableGutters = await page.locator(".sources-panel").evaluate((panel) => ({
    heading: Number.parseFloat(getComputedStyle(panel.querySelector(".sources-heading")!).paddingLeft),
    cell: Number.parseFloat(getComputedStyle(panel.querySelector(".sources-table td")!).paddingLeft),
  }));
  expect(tableGutters.cell).toBe(tableGutters.heading);
  await lumenSource.getByRole("button").first().click();
  const sourceDetails = page.getByRole("dialog", { name: "Lumen Audio", description: "Source capability and readiness" });
  await sourceDetails.getByRole("tab", { name: "Configuration" }).click();
  await expect(sourceDetails.getByText("Access token")).toBeVisible();
  await expect(sourceDetails.getByText("Stored", { exact: true })).toBeVisible();
  await expect(sourceDetails.getByText("Region")).toBeVisible();
  await expect(sourceDetails.getByText("ca", { exact: true })).toBeVisible();
  await expect(sourceDetails.getByRole("button", { name: "Connect another account" })).toBeVisible();
  await sourceDetails.getByRole("button", { name: "Edit configuration" }).click();
  const sourceEditor = page.getByRole("dialog", { name: "Configure Lumen Audio" });
  await expect(sourceEditor.getByRole("button", { name: "Region" })).toHaveText("ca");
  await expect(sourceEditor.getByLabel("Access token")).toHaveValue("");
  await page.keyboard.press("Escape");
  const disabledSource = page.locator(".sources-table tr").filter({ hasText: "Disabled Source" });
  const disabledStatus = disabledSource.locator(".operational-mobile-state");
  await expect(disabledStatus).toHaveText("Disabled");
  await expect(disabledStatus).toHaveCSS("border-top-width", "1px");
  await expect(disabledStatus).toHaveCSS("border-top-style", "solid");
  await page.getByRole("button", { name: /Apple Music – GAMDL/ }).click();
  await page.getByRole("tab", { name: "Configuration" }).click();
  await page.getByRole("button", { name: "Measure CTS" }).click();
  await expect(page.getByText("Apple Music – GAMDL click-to-stream measured.")).toBeVisible();
  expect(appleCtsRequest).toEqual({ providerId: "apple-download", quality: 0 });
  await page.getByRole("button", { name: "Manage Apple Music – GAMDL" }).click();
  const appleManager = page.getByRole("dialog", { name: "Apple Music – GAMDL" });
  await appleManager.getByLabel("Apple ID").fill("tester@example.test");
  await appleManager.getByLabel("Password").fill("password");
  await appleManager.getByRole("button", { name: "Start login" }).click();
  await appleManager.getByLabel("2FA code").fill("123456");
  await appleManager.getByRole("button", { name: "Submit 2FA" }).click();
  await expect(appleManager.getByText("Apple Music – GAMDL is ready")).toBeVisible();
  await expect(appleManager.locator('.source-metrics [data-slot="badge"]')).toHaveCount(3);
  await expect(appleManager.getByRole("link", { name: "Provider settings" }))
    .toHaveAttribute("href", "#/integrations/services?source=apple-download&section=configuration");
  await page.keyboard.press("Escape");
  await page.goto("#/integrations/accounts");
  await page.getByRole("button", { name: /Lumen Audio Account details stored/ }).click();
  const accountDetails = page.getByRole("dialog", { name: "Lumen Audio", description: "Lumen Audio account" });
  await accountDetails.getByRole("tab", { name: "Configuration" }).click();
  await expect(accountDetails.getByRole("button", { name: "Disable account" })).toBeVisible();
  await expect(accountDetails.getByRole("button", { name: "Test connection" })).toBeVisible();
  await expect(accountDetails.getByRole("button", { name: "Edit configuration" })).toBeVisible();
  const metadataCapability = accountDetails.locator(".source-detail-capabilities > span").filter({ hasText: "Metadata" });
  await expect(metadataCapability.locator('[data-slot="badge"]')).toHaveText("Ready");
  const successColor = await page.evaluate(() => {
    const sample = document.createElement("span");
    sample.style.color = "var(--color-success)";
    document.body.append(sample);
    const value = getComputedStyle(sample).color;
    sample.remove();
    return value;
  });
  await expect(metadataCapability.locator('[data-slot="badge"]')).toHaveCSS("color", successColor);
  await expect(metadataCapability.getByRole("button", { name: "Test" })).toHaveAttribute("data-slot", "button");
  await accountDetails.getByRole("button", { name: "Close Source details" }).click();

  const listener = await context.newPage();
  await mockApi(listener);
  await listener.route("**/api/admin/auth/me", (route) => route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify({
      authenticated: true, backend: "Jellyfin",
      user: { id: "listener", name: "Listener", isAdministrator: false },
    }),
  }));
  await listener.route("**/api/admin/provider-accounts", (route) => route.fulfill({
    status: 503,
    contentType: "application/json",
    body: JSON.stringify({ error: "Account scope unavailable" }),
  }));
  await listener.goto("#/integrations/accounts");
  await expect(listener.getByText("Source readiness may be stale.")).toBeVisible();
  await expect(listener.getByText("Accounts are administrator-managed")).toBeVisible();
});

test("Settings loads only the active section owners", async ({ page }) => {
  const requests: string[] = [];
  page.on("request", (request) => requests.push(new URL(request.url()).pathname));
  await mockApi(page);
  await page.goto("#/settings/general");
  await expect(page.getByRole("heading", { name: "General", level: 2 })).toBeVisible();
  expect(requests).toContain("/api/admin/config");
  expect(requests).not.toContain("/api/admin/provider-accounts");
  expect(requests).not.toContain("/api/admin/storage");
  expect(requests).not.toContain("/api/admin/cache");

  requests.length = 0;
  await page.getByRole("tab", { name: "Maintenance" }).click();
  await expect(page.getByRole("heading", { name: "Maintenance", level: 2 })).toBeVisible();
  await expect.poll(() => requests.includes("/api/admin/cache/maintenance/preview")).toBe(true);
  await expect.poll(() => requests.includes("/api/admin/config/migration/status")).toBe(true);
  expect(requests).toContain("/api/admin/storage");
  expect(requests).toContain("/api/admin/cache");
  expect(requests).not.toContain("/api/admin/config");
  expect(requests).not.toContain("/api/admin/provider-accounts");
});

test("Audio quality supports keyboard changes, provider outcomes, save, and reload", async ({ page }) => {
  await mockApi(page);
  let quality = "BestAvailable";
  let saved = "";
  await page.route("**/api/admin/config", (route) => {
    if (route.request().method() === "POST") {
      saved = route.request().postDataJSON().updates.AUDIO_QUALITY;
      quality = saved;
      return route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({ message: "Runtime configuration updated.", updatedKeys: ["AUDIO_QUALITY"] }),
      });
    }
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        ...(responses["/api/admin/config"] as Record<string, unknown>),
        audio: { quality },
      }),
    });
  });

  await page.goto("#/integrations/routing");
  const slider = page.getByRole("slider", { name: "Audio quality" });
  await expect(slider).toHaveAttribute("aria-valuetext", /Best available/);
  await slider.focus();
  await page.keyboard.press("Home");
  await expect(slider).toHaveAttribute("aria-valuetext", /Data saver/);
  await page.keyboard.press("End");
  await expect(slider).toHaveAttribute("aria-valuetext", /Best available/);
  await page.keyboard.press("Home");
  await page.keyboard.press("ArrowRight");
  await expect(slider).toHaveAttribute("aria-valuetext", /High lossy/);
  await expect(page.getByText(/Smaller files and streams with high sound quality/)).toBeVisible();

  await page.getByText("Music source quality details", { exact: true }).click();
  await expect(page.getByText("Apple Music: AAC 320 kbps")).toBeVisible();
  await expect(page.getByText("Deezer: MP3 320 kbps")).toBeVisible();
  await expect(page.getByLabel("Local track preference")).toHaveValue("7");
  await expect(page.getByLabel("Extension match penalty")).toHaveValue("3");
  await page.getByRole("button", { name: "Save playback and matching" }).click();
  await expect.poll(() => saved).toBe("High");
  await page.reload();
  await expect(page.getByRole("slider", { name: "Audio quality" }))
    .toHaveAttribute("aria-valuetext", /High lossy/);
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.locator(".audio-quality-field")).toBeVisible();
  expect(await page.locator(".audio-quality-field").evaluate((element) =>
    element.scrollWidth <= element.clientWidth)).toBe(true);
});

test("Settings preserves dirty drafts during live refresh and scrolls mobile tabs", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  await page.addInitScript(() => {
    class TestEventSource {
      static instance: TestEventSource | undefined;
      listeners = new Map<string, EventListener[]>();

      constructor() {
        TestEventSource.instance = this;
      }

      addEventListener(type: string, listener: EventListener) {
        this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
      }

      close() {}
    }

    Object.defineProperty(window, "EventSource", { value: TestEventSource });
    window.__emitAllstarrUpdate = () => {
      const event = {
        data: JSON.stringify({ resource: "config", resourceId: "runtime", revision: 2 }),
        lastEventId: "settings-2",
      };
      for (const listener of TestEventSource.instance?.listeners.get("update") ?? [])
        listener(event as unknown as Event);
    };
  });
  await mockApi(page);
  let serverValue = 1;
  await page.route("**/api/admin/config", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      ...(responses["/api/admin/config"] as Record<string, unknown>),
      cache: { searchResultsMinutes: serverValue, mediaMaximumMegabytes: 512 },
    }),
  }));
  await page.goto("#/settings/general");
  const cache = page.locator(".settings-disclosure").filter({ hasText: "Cache" });
  await cache.locator("summary").click();
  const input = cache.getByLabel("Search results minutes");
  await input.fill("27");
  await input.evaluate((element) => element.setAttribute("data-instance", "draft"));
  await input.focus();
  serverValue = 9;
  await page.evaluate(() => window.__emitAllstarrUpdate?.());

  await expect(page.getByText("Your unsaved edits are preserved.")).toBeVisible();
  await expect(cache).toHaveAttribute("open", "");
  await expect(input).toHaveValue("27");
  await expect(input).toHaveAttribute("data-instance", "draft");
  await expect(input).toBeFocused();
  await page.getByRole("button", { name: "Reload server values" }).click();
  await expect(input).toHaveValue("9");

  const tabs = page.locator(".settings-tabs");
  const tabSizes = await tabs.getByRole("tab").evaluateAll((items) => items.map((item) => {
    const box = item.getBoundingClientRect();
    return { height: box.height, fontSize: Number.parseFloat(getComputedStyle(item).fontSize) };
  }));
  expect(tabSizes.every(({ height, fontSize }) => height >= 44 && fontSize >= 14)).toBe(true);
  const generalTab = tabs.getByRole("tab", { name: "General" });
  const selectedBackground = await generalTab.evaluate((element) => getComputedStyle(element).backgroundColor);
  const selectedUnderline = await generalTab.evaluate((element) => getComputedStyle(element, "::after").backgroundColor);
  await generalTab.hover();
  await expect.poll(() => generalTab.evaluate((element) => getComputedStyle(element).backgroundColor))
    .not.toBe(selectedBackground);
  await tabs.getByRole("tab", { name: "Maintenance" }).click();
  await expect(tabs.getByRole("tab", { name: "Maintenance" })).toHaveAttribute("aria-selected", "true");
  const maintenanceTab = tabs.getByRole("tab", { name: "Maintenance" });
  await maintenanceTab.hover();
  await expect.poll(() => maintenanceTab.evaluate((element) => getComputedStyle(element, "::after").backgroundColor))
    .toBe(selectedUnderline);
  await expect.poll(() => tabs.evaluate((element) => {
    const active = element.querySelector<HTMLElement>('[aria-selected="true"]');
    if (!active) return false;
    const container = element.getBoundingClientRect();
    const item = active.getBoundingClientRect();
    return item.left >= container.left - 1 && item.right <= container.right + 1;
  })).toBe(true);
});

test("Maintenance previews, retries, and applies a legacy import on mobile", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.goto("#/settings/maintenance");
  const card = page.locator(".maintenance-card").filter({ hasText: "Legacy v2 import" });
  const legacyFile = {
    name: "allstarr.env",
    mimeType: "text/plain",
    buffer: Buffer.from("CACHE_LYRICS_DAYS=21"),
  };

  await card.getByLabel("Legacy environment file").setInputFiles(legacyFile);
  await card.getByRole("button", { name: "Preview import" }).click();
  await expect(card.getByText("Imported accounts remain disabled until reviewed.")).toBeVisible();
  await card.getByText("Review 1 parsed settings").click();
  await expect(card.getByText("CACHE_LYRICS_DAYS")).toBeVisible();
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  await card.getByRole("button", { name: "Discard and retry" }).click();
  await expect(card.getByRole("button", { name: "Preview import" })).toBeVisible();

  await card.getByLabel("Legacy environment file").setInputFiles(legacyFile);
  await card.getByRole("button", { name: "Preview import" }).click();
  await card.getByRole("checkbox").check();
  await card.getByRole("button", { name: "Import preview" }).click();
  await expect(card.getByText("Legacy settings imported.")).toBeVisible();
});

test("Maintenance validates before selective import and reports applied rows", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const delayed = routeRelease();
  await mockApi(page, { releasePath: "/api/admin/preview-selective-state", release: delayed.promise });
  await page.goto("#/settings/maintenance");
  const card = page.locator(".transfer-card");
  const archive = {
    name: "allstarr-state.zip",
    mimeType: "application/zip",
    buffer: Buffer.from("fixture"),
  };

  await expect(card.getByRole("button", { name: "Import validated archive" })).toHaveCount(0);
  await card.locator('input[type="file"]').setInputFiles(archive);
  await card.getByRole("button", { name: "Import behavior" }).click();
  await page.getByRole("option", { name: "Merge compatible rows" }).click();
  await card.getByRole("button", { name: "Validate archive" }).click();
  await expect(card.getByLabel("preview in progress")).toBeVisible();
  await card.getByRole("button", { name: "Cancel" }).click();
  delayed.release();
  await expect(card.getByText("State transfer cancelled.")).toBeVisible();
  await card.getByRole("button", { name: "Validate archive" }).click();
  await expect(card.getByRole("region", { name: "Selective transfer preview" })).toContainText("3 rows");
  await expect(card.getByText("Dependencies: Settings, Accounts")).toBeVisible();
  await card.getByRole("button", { name: "Import validated archive" }).click();
  const dialog = page.getByRole("alertdialog", { name: "Import validated state?" });
  await expect(dialog).toBeVisible();
  await dialog.getByRole("button", { name: "Import archive" }).click();
  const result = card.getByRole("region", { name: "Selective transfer result" });
  await expect(result).toContainText("Import complete");
  await expect(result).toContainText("provider-accounts");
  await expect(result).toContainText("playlist-links");
  await expect(dialog).toBeHidden();
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
});

test("Maintenance reports cache budgets and confirms category purge", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const requests: string[] = [];
  page.on("request", (request) => requests.push(new URL(request.url()).pathname));
  await mockApi(page);
  await page.goto("#/settings/general");
  const cacheSettings = page.locator("details").filter({ hasText: "Cache" });
  await cacheSettings.locator("summary").click();
  await expect(cacheSettings.getByText(/hot RAM remains fixed at 16 MiB/)).toBeVisible();
  await page.goto("#/settings/maintenance");
  const card = page.locator(".cache-diagnostics-card");

  await expect(card.getByText("Hot RAM hit / miss")).toBeVisible();
  await expect(card.getByText("Upstream avoided")).toBeVisible();
  await card.getByText("Limits and cleanup preview").click();
  await expect(card.getByText("Stale account scopes")).toBeVisible();
  await card.getByText("Category budgets").click();
  await expect(card.getByText(/media-assets/)).toBeVisible();
  await card.getByRole("button", { name: "Purge", exact: true }).click();
  const dialog = page.getByRole("alertdialog", { name: "Purge Artwork cache?" });
  await expect(dialog).toBeVisible();
  await dialog.getByRole("button", { name: "Purge cache" }).click();
  await expect.poll(() => requests).toContain("/api/admin/cache/categories/Artwork");
  await expect(dialog).toBeHidden();
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
});

test("Durable onboarding controls first setup and targeted recovery", async ({ page }) => {
  await mockApi(page);
  let completed = false;
  let recovery = false;
  let releaseStatus!: () => void;
  const initialStatus = new Promise<void>((resolve) => { releaseStatus = resolve; });
  let waitForInitialStatus = true;
  await page.route("**/api/admin/onboarding/status", async (route) => {
    if (waitForInitialStatus) {
      waitForInitialStatus = false;
      await initialStatus;
    }
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        completed, setupOpen: false, shouldRedirectToSetup: !completed,
        schemaVersion: "onboarding-v1",
        completedSteps: completed ? ["backend-identity"] : [],
        completionSource: completed ? "setup-guide" : "none",
        completedAt: completed ? "2026-01-01" : null,
        revision: completed ? 2 : 0,
        recoveryNotices: recovery ? ["backend_identity_missing"] : [],
        migration: { available: true, completed: false, firstRun: true },
      }),
    });
  });
  await page.route("**/api/admin/onboarding/complete", (route) => {
    completed = true;
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        completed: true, setupOpen: false, shouldRedirectToSetup: false,
        schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"],
        completionSource: "setup-guide", completedAt: "2026-01-01", revision: 2,
        recoveryNotices: [],
        migration: { available: true, completed: false, firstRun: true },
      }),
    });
  });

  const navigation = page.goto("#/");
  await expect(page.getByRole("heading", { name: "Bringing your music universe online" })).toBeVisible();
  await expect(page.getByRole("status")).toHaveText("Loading Allstarr. Preparing your music control center.");
  await expect(page.locator("[role=progressbar]")).toHaveCount(0);
  releaseStatus();
  await navigation;
  const setup = page.getByRole("dialog", { name: "Set up Allstarr" });
  await expect(setup).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(setup).toBeVisible();
  await setup.getByRole("button", { name: "Finish setup" }).click();
  await expect(setup).toBeHidden();

  await page.evaluate(() => localStorage.clear());
  await page.reload();
  await expect(setup).toBeHidden();

  recovery = true;
  await page.reload();
  await expect(page.getByText("Media server connection needs attention.")).toBeVisible();
  await expect(page.getByText(/review the media server connection/i)).toBeVisible();
  await expect(setup).toBeHidden();
});

test("Signal boot exposes a retryable bootstrap failure", async ({ page }) => {
  const failing = ["/api/admin/auth/me"];
  await mockApi(page, { fail: failing });
  await page.goto("#/");

  await expect(page.getByRole("heading", { name: "Allstarr could not start." })).toBeVisible();
  await expect(page.getByRole("alert")).toContainText("Fixture unavailable");
  failing.length = 0;
  await page.getByRole("button", { name: "Try again" }).click();
  await expect(page.getByRole("heading", { name: "Home", level: 1 })).toBeVisible();
});

test("Administrators can reopen durable setup from Maintenance", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.goto("#/settings/maintenance");
  await page.getByRole("button", { name: "Open setup guide" }).click();
  const setup = page.getByRole("dialog", { name: "Set up Allstarr" });
  await expect(setup).toBeVisible();
  await expect(setup.getByRole("button", { name: "Finish setup" })).toBeInViewport();
  await page.keyboard.press("Escape");
  await expect(setup).toBeHidden();
});

test("Playlist details use a responsive dialog and track rows open mapping review", async ({ page }) => {
  await page.setViewportSize({ width: 835, height: 762 });
  await mockApi(page);
  await page.goto("#/library/playlists");
  await expect.poll(async () => (await page.locator(".playlist-list").boundingBox())?.height ?? 0).toBeGreaterThan(600);
  await expect(page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Library" }).locator("svg"))
    .toBeVisible();
  await expect(page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Library" }).locator("use"))
    .toHaveCount(0);
  const refresh = page.waitForRequest((item) =>
    item.method() === "POST" && item.url().endsWith("/api/admin/playlist-links/playlist-link/refresh"));
  await page.getByRole("button", { name: "Refresh playlists" }).click();
  await refresh;
  await expect(page.getByText(/1 playlists refreshed in/)).toBeVisible();
  const coverage = page.locator(".playlist-row .coverage-bar > span[style]");
  await expect(coverage.nth(0)).toHaveAttribute("style", /width:\s*50%/);
  await expect(coverage.nth(1)).toHaveAttribute("style", /width:\s*50%; --route-color:\s*var\(--color-ink-muted\)/);
  const [playlistRow, playlistBar] = await Promise.all([
    page.locator(".playlist-row").boundingBox(),
    page.locator(".playlist-row .coverage-bar").boundingBox(),
  ]);
  expect(Math.abs((playlistRow!.y + playlistRow!.height) - (playlistBar!.y + playlistBar!.height)))
    .toBeLessThanOrEqual(1);
  expect(playlistBar!.height).toBeGreaterThanOrEqual(6);
  const rematchPreview = page.waitForRequest((item) =>
    item.method() === "GET" && item.url().endsWith("/api/admin/playlist-links/rematch/preview"));
  await page.getByRole("button", { name: "Review rematch" }).click();
  await rematchPreview;
  const rematchDialog = page.getByRole("dialog", { name: "Review full rematch" });
  await expect(rematchDialog.getByText("1 track needs review")).toBeVisible();
  await expect(rematchDialog.getByText("Generic external").locator("..")).toContainText("0");
  await rematchDialog.getByRole("checkbox", { name: /I reviewed these counts/ }).check();
  const rematchApply = page.waitForRequest((item) =>
    item.method() === "POST" && item.url().endsWith("/api/admin/playlist-links/rematch/apply"));
  await rematchDialog.getByRole("button", { name: "Rematch 1 track" }).click();
  await rematchApply;
  await expect(page.getByText("Controlled rematch queued.")).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.locator(".playlist-row .coverage-bar")).toContainText("Lumen Audio: 1, Unresolved: 1");
  await expect(page.locator(".playlist-row .playlist-art > span")).toBeVisible();
  const openPlaylist = page.getByRole("button", { name: "Open Test playlist playlist details", exact: true });
  await expect(openPlaylist).toBeVisible();
  await expect(page.getByText("1 to review", { exact: true })).toBeVisible();
  await expect(page.getByText("1 unresolved", { exact: true })).toBeVisible();
  await expect(page.getByText("Not updated yet", { exact: true })).toBeVisible();
  await expect(page.getByRole("group", { name: "0% confirmed, 1 of 2 playable" })).toBeVisible();
  const metricsFit = await page.locator(".playlist-summary").evaluate((summary) => {
    const parent = summary.getBoundingClientRect();
    const children = [...summary.children].map((child) => child.getBoundingClientRect());
    const inside = children.every((child) =>
      child.left >= parent.left - 1 && child.right <= parent.right + 1 &&
      child.top >= parent.top - 1 && child.bottom <= parent.bottom + 1);
    const separate = children.every((item, index) => children.slice(index + 1).every((other) =>
      item.right <= other.left || other.right <= item.left ||
      item.bottom <= other.top || other.bottom <= item.top));
    return inside && separate;
  });
  expect(metricsFit).toBe(true);
  await openPlaylist.click();
  const dialog = page.getByRole("dialog", { name: "Test playlist" });
  await expect(dialog).toBeVisible();
  await expect(dialog.locator(".hero-art > span")).toBeVisible();
  await expect(dialog.locator(".track-art > span")).toBeVisible();
  await expect(dialog.getByRole("table", { name: "Test playlist tracks" })).toBeVisible();
  await expect(dialog.locator(".coverage-bar")).toContainText("Lumen Audio: 1, Unresolved: 1");
  await expect(dialog.getByText("Needs review", { exact: true })).toBeVisible();
  await expect(dialog.getByText("No automatic updates", { exact: true })).toBeVisible();
  await dialog.getByRole("button", { name: "Show 1 track needing review" }).click();
  await expect(dialog.getByRole("button", { name: "Track route" })).toContainText("To review (1)");
  const mobileReview = dialog.getByRole("button", { name: "Open mapping details for Test song" });
  await expect.poll(async () => (await mobileReview.boundingBox())?.width ?? 0).toBeGreaterThanOrEqual(44);
  await mobileReview.click();
  const mobileMatch = page.getByRole("dialog", { name: "Test song" });
  await expect(mobileMatch).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(mobileMatch).toBeHidden();
  await expect(dialog).toBeVisible();
  await expect(mobileReview).toBeFocused();
  await dialog.getByRole("button", { name: "Close playlist details" }).click();
  await openPlaylist.click();
  await expect(dialog.getByRole("button", { name: "Track route" })).toContainText("All routes");
  await expect(dialog.getByRole("button", { name: "Actions" })).toBeInViewport();
  await dialog.getByRole("button", { name: "Actions" }).click();
  await expect(page.getByRole("menuitem", { name: "Update playlist now" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Rematch" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Refresh source" })).toBeVisible();
  await page.keyboard.press("Escape");
  await page.setViewportSize({ width: 1280, height: 800 });
  await expect.poll(async () => (await dialog.boundingBox())?.width ?? 0).toBeGreaterThan(900);
  await expect(dialog.getByText(/Snapshot v/)).toHaveCount(0);
  await expect(dialog.getByText("Last target sync")).toHaveCount(0);
  await expect(dialog.getByText("Current published snapshot")).toHaveCount(0);
  await expect(dialog.getByRole("button", { name: /Operation details:/ })).toBeVisible();
  await expect(dialog.getByRole("columnheader", { name: "Artist" })).toBeVisible();
  await expect(dialog.getByRole("columnheader", { name: "Album" })).toBeVisible();
  await dialog.getByRole("button", { name: /Choose track columns/ }).click();
  await page.locator(".track-column-picker").getByLabel("Album", { exact: true }).uncheck();
  await expect(dialog.getByRole("columnheader", { name: "Album" })).toHaveCount(0);
  await page.keyboard.press("Escape");
  const density = await dialog.evaluate((element) => {
    const bounds = element.getBoundingClientRect();
    const strip = element.querySelector(".playlist-meta-strip")!;
    const trackScroll = element.querySelector(".track-scroll")!.getBoundingClientRect();
    const table = element.querySelector(".track-data-table")!.getBoundingClientRect();
    const centers = [...strip.children].map((child) => {
      const childBounds = child.getBoundingClientRect();
      return childBounds.top + childBounds.height / 2;
    });
    return {
      dialogHeight: bounds.height,
      blankTrackTail: trackScroll.height - table.height,
      metadataCenterSpread: Math.max(...centers) - Math.min(...centers),
    };
  });
  expect(density.dialogHeight).toBeLessThan(700);
  expect(density.blankTrackTail).toBeLessThan(48);
  expect(density.metadataCenterSpread).toBeLessThanOrEqual(1);
  await dialog.getByRole("button", { name: "Technical details for Test song" }).click();
  await expect(page).toHaveURL(/#\/library\/playlists$/);
  const trackDetails = page.locator(".track-details-menu");
  await expect(trackDetails.getByRole("button", { name: "Review match" })).toBeVisible();
  await expect.poll(() => trackDetails.evaluate((panel) => {
    const bounds = panel.getBoundingClientRect();
    return panel.contains(document.elementFromPoint(bounds.left + bounds.width / 2, bounds.top + 8));
  })).toBe(true);
  await page.keyboard.press("Escape");
  await dialog.getByRole("button", { name: "Open mapping details for Test song" }).focus();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/#\/library\/playlists$/);
  await expect(page.getByRole("dialog", { name: "Test song" })).toBeVisible();
});

for (const viewport of [{ width: 390, height: 844 }, { width: 1280, height: 800 }]) {
  test(`Playlist sync preserves reading position at ${viewport.width}px`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await mockApi(page);
    const first = (responses["/api/admin/playlist-links"] as {
      playlistLinks: Record<string, unknown>[];
    }).playlistLinks[0];
    await page.route("**/api/admin/playlist-links", async (route) => {
      if (route.request().method() !== "GET") return route.fallback();
      return route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({
          playlistLinks: [
            { ...first, trackCount: 60, playableCount: 60, unmatchedCount: 0 },
            { ...first, id: "playlist-link-two", name: "Second playlist", trackCount: 60, playableCount: 60, unmatchedCount: 0 },
          ],
        }),
      });
    });

    const detail = (id: string, name: string, projectionMode: "resolved" | "source" | "target") => ({
      id,
      snapshotId: `${id}-snapshot`,
      snapshotVersion: 1,
      latestSourceSnapshotVersion: 1,
      hasNewerSourceGeneration: false,
      name,
      sourceProviderId: "lumen-audio",
      projectionMode,
      targetProtocol: "jellyfin",
      retrievedAt: "2026-01-01",
      completedAt: "2026-01-01",
      trackCount: 60,
      localCount: 60,
      externalCount: 0,
      unresolvedCount: 0,
      durationMs: 10_800_000,
      matchedCount: 60,
      reviewCount: 0,
      rejectedCount: 0,
      playableCount: 60,
      routeCoverage: [{ providerId: "jellyfin", count: 60 }],
      unknownDurationCount: 0,
      clientProjection: {
        protocolId: `allstarr-vpl-${id}`,
        projectionMode,
        trackCount: 60,
        tracks: Array.from({ length: 60 }, (_, index) => ({
          position: index + 1,
          sourcePosition: index,
          itemId: `${id}-${projectionMode}-${index}`,
          title: `Track ${index + 1}`,
          artists: ["Artist"],
          album: "Album",
          durationMs: 180_000,
          routeKind: "local",
        })),
      },
      tracks: Array.from({ length: 60 }, (_, index) => ({
        sourcePosition: index,
        position: index + 1,
        externalSnapshotId: `${id}-track-${index}`,
        title: `Track ${index + 1}`,
        artists: ["Artist"],
        album: "Album",
        durationMs: 180_000,
        routeKind: "local",
        routeProviderId: "jellyfin",
        matchState: "accepted",
        providerRoutes: [],
      })),
    });
    let detailRequests = 0;
    let releaseRefresh!: () => void;
    const refreshHeld = new Promise<void>((resolve) => { releaseRefresh = resolve; });
    await page.route("**/api/admin/playlist-links/playlist-link?*", async (route) => {
      detailRequests++;
      if (detailRequests === 2) await refreshHeld;
      const mode = new URL(route.request().url()).searchParams.get("projectionMode") as "resolved" | "source" | "target";
      return route.fulfill({ contentType: "application/json", body: JSON.stringify(detail("playlist-link", "Test playlist", mode)) });
    });
    await page.route("**/api/admin/playlist-links/playlist-link-two?*", (route) => {
      const mode = new URL(route.request().url()).searchParams.get("projectionMode") as "resolved" | "source" | "target";
      return route.fulfill({
        contentType: "application/json",
        body: JSON.stringify(detail("playlist-link-two", "Second playlist", mode)),
      });
    });

    await page.goto("#/library/playlists");
    await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
    const dialog = page.getByRole("dialog", { name: "Test playlist" });
    const scroll = dialog.locator(".track-scroll");
    await scroll.evaluate((element) => {
      element.setAttribute("data-instance", "reading-position");
      element.scrollTop = 600;
    });
    const filter = dialog.getByLabel("Filter tracks");
    await filter.fill("Track");
    await filter.focus();
    const before = await scroll.evaluate((element) => element.scrollTop);
    await dialog.getByRole("button", { name: "Actions" }).click();
    await page.getByRole("menuitem", { name: "Update playlist now" }).click();
    await filter.focus();
    await expect.poll(() => detailRequests).toBe(2);

    await expect(dialog.getByText("Loading playlist tracks…")).toHaveCount(0);
    await expect(scroll).toHaveAttribute("data-instance", "reading-position");
    await expect(filter).toHaveValue("Track");
    await expect(filter).toBeFocused();
    expect(Math.abs(await scroll.evaluate((element) => element.scrollTop) - before)).toBeLessThanOrEqual(1);
    releaseRefresh();
    await expect(dialog.getByText("Playlist update queued.")).toBeVisible();
    await expect(scroll).toHaveAttribute("data-instance", "reading-position");
    expect(Math.abs(await scroll.evaluate((element) => element.scrollTop) - before)).toBeLessThanOrEqual(1);

    await dialog.getByRole("button", { name: "Close playlist details" }).click();
    await page.getByRole("button", { name: "Open Second playlist playlist details" }).click();
    await expect(page.getByRole("dialog", { name: "Second playlist" }).locator(".track-scroll"))
      .toHaveJSProperty("scrollTop", 0);
  });
}

test("Playlist operations show durable progress and confirm cancellation", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  let cancelled = false;
  let detailRequests = 0;
  await page.route("**/api/admin/jobs?limit=100", (route) => {
    const fixture = responses["/api/admin/jobs?limit=100"] as { jobs: Record<string, unknown>[]; progress: unknown[] };
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        ...fixture,
        jobs: fixture.jobs.map((job) => ({
          ...job,
          state: cancelled ? "Succeeded" : "Deferred",
          deferralCount: 1,
          availableAt: "2099-01-01T12:00:00Z",
        })),
      }),
    });
  });
  await page.route("**/api/admin/jobs/*/cancel", (route) => {
    cancelled = true;
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        jobId: "11111111-1111-1111-1111-111111111111",
        state: "CancellationRequested",
      }),
    });
  });
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/admin/playlist-links/playlist-link")
      detailRequests++;
  });
  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: /Test playlist/ }).click();
  const dialog = page.getByRole("dialog", { name: "Test playlist" });
  const update = page.waitForRequest((item) =>
    item.method() === "POST" && item.url().endsWith("/api/admin/playlist-links/playlist-link/run") &&
    item.postDataJSON().snapshotId === "playlist-snapshot");
  await dialog.getByRole("button", { name: "Actions" }).click();
  await page.getByRole("menuitem", { name: "Update playlist now" }).click();
  await update;
  await expect(dialog.getByText("Playlist update queued.")).toBeVisible();
  await dialog.getByRole("button", { name: /Operation details:/ }).click();
  const operation = page.locator(".operation-popover");
  await expect(operation.getByText("Matching Test song", { exact: true }).first()).toBeVisible();
  await expect(operation.getByText("1/2")).toBeVisible();
  await expect(operation.getByText("Wait until")).toBeVisible();
  await expect(operation.getByText("Deferrals")).toBeVisible();
  await operation.getByRole("button", { name: "Cancel operation" }).click();
  const confirmation = page.getByRole("alertdialog", { name: "Cancel this operation?" });
  await expect(confirmation).toBeVisible();
  const request = page.waitForRequest((item) =>
    item.method() === "POST" && item.url().endsWith("/api/admin/jobs/11111111-1111-1111-1111-111111111111/cancel"));
  await confirmation.getByRole("button", { name: "Cancel operation" }).click();
  await request;
  await expect.poll(() => detailRequests).toBeGreaterThanOrEqual(2);
});

test("Profile artwork is stable in full, slim, and mobile navigation", async ({ page }) => {
  await mockApi(page);
  await page.route("**/api/admin/auth/me/avatar?user=user", (route) => route.fulfill({
    status: 200,
    contentType: "image/gif",
    body: Buffer.from("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==", "base64"),
  }));

  for (const width of [1280, 924, 390]) {
    await page.setViewportSize({ width, height: 844 });
    await page.goto("#/");
    const avatar = page.locator(".profile .avatar");
    await expect(avatar).toBeVisible();
    await expect(avatar.locator("img")).toBeVisible();
    await expect.poll(async () => (await avatar.boundingBox())?.width ?? 0).toBe(36);
    if (width === 924) {
      await page.getByRole("button", { name: "Collapse sidebar" }).click();
      await expect(page.locator(".app-shell")).toHaveClass(/slim/);
      await expect.poll(async () =>
        (await page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Library" }).boundingBox())?.width ?? 0
      ).toBe(48);
      await page.getByRole("button", { name: "Expand sidebar" }).click();
      await expect(page.locator(".app-shell")).not.toHaveClass(/slim/);
    }
    if (width === 390) {
      await expect(page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Settings" })).toBeHidden();
      await expect(avatar).toHaveAttribute("href", "#/settings");
      await expect.poll(async () => (await page.locator(".sidebar").boundingBox())?.height ?? 0).toBeLessThan(80);
    }
  }

  await page.route("**/api/admin/auth/me/avatar?user=user", (route) => route.fulfill({ status: 404 }));
  await page.reload();
  await expect(page.locator(".profile .avatar span")).toHaveText("T");
  await expect.poll(async () => (await page.locator(".profile .avatar").boundingBox())?.width ?? 0).toBe(36);
});

test("Segmented navigation and unified provider filters support keyboard use", async ({ page }) => {
  await mockApi(page);
  await page.goto("#/library/playlists");
  await page.getByRole("tab", { name: "Playlists" }).focus();
  await page.keyboard.press("ArrowRight");
  await expect(page).toHaveURL(/#\/library\/mappings$/);
  await expect(page.getByRole("tab", { name: "Mappings" })).toHaveAttribute("aria-selected", "true");

  await page.getByRole("button", { name: "Interactive search" }).click();
  const dialog = page.getByRole("dialog", { name: "Test song" });
  await dialog.getByLabel("Search local library and playable providers").fill("Kiss Me More");
  await dialog.getByRole("button", { name: "Search", exact: true }).click();
  await dialog.getByRole("button", { name: /Jellyfin 1/ }).focus();
  await page.keyboard.press("Space");
  await expect(dialog.getByRole("button", { name: /Jellyfin 1/ })).toHaveAttribute("aria-pressed", "true");

  await page.goto("#/settings/extensions");
  await page.getByRole("tab", { name: /Installed/ }).focus();
  await page.keyboard.press("ArrowRight");
  await expect(page.getByRole("tab", { name: /Available/ })).toHaveAttribute("aria-selected", "true");
});

test("Responsive boundaries preserve navigation and download identity", async ({ page }) => {
  await mockApi(page);
  await page.goto("#/");
  for (const width of [760, 761, 900, 901]) {
    await page.setViewportSize({ width, height: 844 });
    const navigation = page.getByRole("navigation", { name: "Primary" });
    await expect(navigation.getByRole("link")).toHaveCount(width <= 760 ? 5 : 6);
    await expect(page.locator(".sidebar")).toHaveCSS("position", width <= 760 ? "fixed" : "sticky");
    if (width === 900)
      await expect.poll(async () => (await page.locator(".sidebar").boundingBox())?.width ?? 0).toBe(80);
    if (width === 901)
      await expect.poll(async () => (await page.locator(".sidebar").boundingBox())?.width ?? 0).toBe(248);
    if (width === 760) {
      const boxes = await navigation.getByRole("link").evaluateAll((links) =>
        links.map((link) => link.getBoundingClientRect().height));
      expect(boxes.every((height) => height >= 44)).toBe(true);
    }
    await expect.poll(() => page.evaluate(() =>
      document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  }

  for (const width of [800, 801, 1050, 1051, 1100, 1101]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto("#/library/cached");
    const row = page.locator(".download-row");
    await expect(row.locator(".download-provider strong", { hasText: "Lumen Audio" })).toBeVisible();
    await expect(row.getByRole("link", { name: "Download" })).toBeVisible();
    await expect.poll(() => page.evaluate(() =>
      document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  }
});

test("Playlist columns and nested dialogs retain interaction ownership", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await mockApi(page);
  await page.goto("#/library/playlists");
  const opener = page.getByRole("button", { name: "Open Test playlist playlist details" });
  await opener.click();
  const playlist = page.getByRole("dialog", { name: "Test playlist" });
  await expect(page.locator("body")).toHaveCSS("overflow", "hidden");
  await playlist.getByRole("button", { name: /Choose track columns/ }).click();
  const columns = page.locator(".track-column-picker");
  await columns.getByLabel("Artist", { exact: true }).uncheck();
  await expect(playlist.getByRole("columnheader", { name: "Artist" })).toHaveCount(0);
  await page.keyboard.press("Escape");

  const review = playlist.getByRole("button", { name: "Open mapping details for Test song" });
  await review.click();
  const match = page.getByRole("dialog", { name: "Test song" });
  await expect(match).toBeVisible();
  const layers = await page.evaluate(() => ({
    parent: Number.parseInt(getComputedStyle(document.querySelector(".playlist-detail-dialog")!).zIndex),
    nestedOverlay: Number.parseInt(getComputedStyle(document.querySelector(".match-dialog-overlay")!).zIndex),
    nested: Number.parseInt(getComputedStyle(document.querySelector(".match-dialog")!).zIndex),
  }));
  expect(layers.parent).toBeLessThan(layers.nestedOverlay);
  expect(layers.nestedOverlay).toBeLessThan(layers.nested);
  await page.keyboard.press("Escape");
  await expect(match).toBeHidden();
  await expect(playlist).toBeVisible();
  await expect(review).toBeFocused();
  await playlist.getByRole("button", { name: "Close playlist details" }).click();
  await expect(opener).toBeFocused();
});

test("Sidebar uses an integrated expander and deterministic slim breakpoint", async ({ page }) => {
  await mockApi(page);
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("#/");
  const shell = page.locator(".app-shell");
  const expander = page.getByRole("button", { name: "Collapse sidebar" });
  await expect(expander).toBeVisible();
  const [expanderBox, brandBox] = await Promise.all([
    expander.boundingBox(),
    page.getByRole("link", { name: "Allstarr home" }).boundingBox(),
  ]);
  expect(expanderBox!.y + expanderBox!.height).toBeLessThanOrEqual(brandBox!.y);
  await expect(page.getByRole("navigation", { name: "Primary" }).getByRole("link")).toHaveCount(6);
  const libraryIcon = page.getByRole("navigation", { name: "Primary" })
    .getByRole("link", { name: "Library" }).locator("svg");
  const expandedIcon = await libraryIcon.boundingBox();
  const expandedMark = await expander.locator("svg").innerHTML();
  await expander.click();
  await expect(shell).toHaveClass(/slim/);
  await expect(page.getByRole("button", { name: "Expand sidebar" })).toBeVisible();
  await expect.poll(async () => (await page.locator(".sidebar").boundingBox())?.width ?? 0).toBe(80);
  const collapsedIcon = await libraryIcon.boundingBox();
  expect(Math.abs((expandedIcon!.x + expandedIcon!.width / 2) -
    (collapsedIcon!.x + collapsedIcon!.width / 2))).toBeLessThanOrEqual(0.5);
  const collapsedMark = await page.getByRole("button", { name: "Expand sidebar" })
    .locator("svg").innerHTML();
  expect(collapsedMark).not.toBe(expandedMark);
  const library = page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Library" });
  await expect.poll(async () => {
    const [link, icon] = await Promise.all([library.boundingBox(), library.locator("svg").boundingBox()]);
    return Math.abs((link!.y + link!.height / 2) - (icon!.y + icon!.height / 2));
  }).toBeLessThan(0.5);

  await page.setViewportSize({ width: 850, height: 800 });
  await expect(page.getByRole("button", { name: "Expand sidebar" })).toBeVisible();
  await expect.poll(async () => (await page.locator(".sidebar").boundingBox())?.width ?? 0).toBe(80);

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole("navigation", { name: "Primary" }).getByRole("link")).toHaveCount(5);
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
});
