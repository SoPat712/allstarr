import { expect, type Locator, type Page } from "@playwright/test";
import { readFile } from "node:fs/promises";
import { expectContainedMobileGeometry } from "./mobile-geometry";

async function openOtherPlaylistSources(dialog: Locator) {
  const sources = dialog.locator(".other-sources");
  await expect(sources.locator(":scope > summary")).toBeVisible();
  if (await sources.getAttribute("open") === null)
    await sources.locator(":scope > summary").click();
}

async function nextPlaylistOptions(dialog: Locator) {
  const next = dialog.getByRole("button", { name: "Next", exact: true });
  if (await next.count()) await next.click();
}

declare global {
  interface Window {
    __allstarrMetrics: { cls: number; lcp: number; navigation: number };
    __emitAllstarrUpdate?: () => void;
  }
}

const viewports = [
  { width: 320, height: 844 },
  { width: 390, height: 844 },
  { width: 430, height: 932 },
  { width: 667, height: 375 },
  { width: 820, height: 760 },
  { width: 1280, height: 800 },
];

async function clickWhenStable(control: Locator) {
  await expect(control).toBeVisible();
  await control.evaluate(element => element.scrollIntoView({ block: "center", inline: "nearest", behavior: "instant" }));
  let previousBounds = "";
  await expect.poll(async () => {
    const current = await control.evaluate(element => {
      const box = element.getBoundingClientRect();
      return {
        bounds: JSON.stringify([box.x, box.y, box.width, box.height]),
        // Scroll-linked effects follow the scroll position and never finish on their own, so only time-based motion counts.
        animating: document.getAnimations().some(animation => animation.playState === "running" &&
          animation.timeline === document.timeline && animation.effect?.getTiming().iterations !== Infinity),
      };
    });
    const settled = !current.animating && current.bounds === previousBounds;
    previousBounds = current.bounds;
    return settled;
  }, { message: "Control finishes moving before the click" }).toBe(true);
  await control.click();
}

async function openSourceSettings(page: Page, name: string) {
  const panel = page.getByRole("dialog", { name, exact: true });
  await expect(panel).toBeVisible();
  const details = panel.locator(".source-more");
  if (await details.getAttribute("open") === null) await details.locator(":scope > summary").click();
}

async function waitForSettledOptions(options: Locator, expectedText: string) {
  await expect(options).toBeVisible();
  await expect(options.getByRole("option").last()).toBeVisible();
  let previousOptions = "";
  await expect.poll(async () => {
    const current = await options.getByRole("option").evaluateAll((items) => JSON.stringify(items.map((item) => {
      const box = item.getBoundingClientRect();
      return [item.textContent, box.x, box.y, box.width, box.height];
    })));
    const settled = current === previousOptions && current.includes(expectedText);
    previousOptions = current;
    return settled;
  }, { message: "Options finish loading and positioning before selection" }).toBe(true);
}

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
      id: "spotify", name: "Spotify", categories: ["playlists"],
      accountSettings: [
        { key: "sessionCookie", label: "Spotify session cookie", type: "password", sensitive: true, required: true },
      ],
    },
    {
      id: "audiomuse-ai", name: "AudioMuse-AI", categories: ["intelligence"],
      accountSettings: [
        { key: "baseUrl", label: "AudioMuse-AI server URL", type: "url", required: true },
        { key: "apiToken", label: "AudioMuse-AI access token", type: "password", sensitive: true, required: false },
        { key: "server", label: "AudioMuse-AI music server", type: "text", required: false },
      ],
    },
    { id: "listenbrainz", name: "ListenBrainz", categories: ["scrobbling"] },
    {
      id: "apple-musickit", name: "Apple Music", categories: ["metadata", "playlists", "streaming", "download", "lyrics"],
      accountSettings: [
        { key: "musicUserToken", label: "Media user token", type: "password", sensitive: true, required: true },
        { key: "storefront", label: "Storefront", type: "text", required: true, defaultValueJson: '"us"' },
      ],
      configSchema: [
        { key: "APPLE_DOWNLOAD_URL", label: "External provider URL", type: "url", valuePath: "appleDownload.baseUrl" },
      ],
    },
  ],
  configSections: [
    {
      id: "general", label: "General", fields: [
      { key: "AUDIO_QUALITY", label: "Audio quality", type: "audio-quality", valuePath: "audio.quality" },
      { key: "MATCHING_LOCAL_PREFERENCE_PERCENT", label: "Tentative local window", type: "number", valuePath: "matching.localPreferencePercent", min: 0, max: 20, helpText: "Only used when no candidate qualifies for automatic acceptance. Accepted matches always use local first, then Streaming priority." },
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
  "/api/admin/mixes": [
    ["daily-1", "Blend 1"], ["daily-2", "Blend 2"], ["daily-3", "Blend 3"], ["daily-4", "Blend 4"],
    ["discover-weekly", "Uncharted"], ["release-radar", "Just Dropped"], ["on-repeat", "Heavy Rotation"], ["rewind", "Long Lost"],
    ["chill", "Chill"], ["focus", "Focus"], ["workout", "Workout"], ["party", "Party"], ["sleep", "Sleep"],
  ].map(([kind, name], index) => ({
    id: `mix-${kind}`, kind, name, description: "Songs picked for you", section: index < 8 ? "madeForYou" : "moods",
    sortOrder: index, sourceLabel: null, recipeVersion: null, updatedAt: null,
    readiness: { ready: true, message: "Ready" }, inApps: false, appsAllowed: true,
    coverArtwork: ["ext-spotify-song-track-a"],
  })),
  "/api/admin/mixes/publish-setting": { enabled: false },
  "/api/admin/audiomuse/status": { reason: "not_enabled", accessibleLibrarySongs: 0, analyzedSongs: 0, coverageComplete: false },
  "/api/admin/audiomuse/connection": { baseUrl: "", keyConfigured: false, revision: 1 },
  "/api/admin/releases": { entries: [], excluded: [], coverage: [] },
  "/api/admin/access/backend": { settings: { supported: false, connectionSaved: false, configured: false, needsReview: false, revision: "r1" }, csrfToken: "fixture" },
  "/api/admin/access/oidc": { settings: { enabled: false, authority: "", clientId: "", publicUrl: "", displayName: "Single sign-on", hasClientSecret: false, needsReview: false, revision: "r1" }, csrfToken: "fixture" },
  "/api/admin/storage/files": { settings: { available: true, pending: false, needsReview: false, files: 0 }, csrfToken: "fixture" },
  "/api/admin/scrobbling/deliveries": { items: [] },
  "/api/admin/provider-favorite-links": { activeProtocol: "jellyfin", items: [], nextCursor: null },
  "/api/admin/provider-favorite-links/members": { activeProtocol: "jellyfin", items: [], nextCursor: null },
  "/api/admin/auth/playlist-consent": { supported: true, granted: false, updatedAt: null, needsSignIn: false },
  "/api/admin/auth/me": {
    authenticated: true, backend: "Jellyfin",
    features: { intelligence: true },
    user: {
      id: "user", allstarrUserId: "user", name: "Tester", isAdministrator: true,
      avatarUrl: "/api/admin/auth/me/avatar?user=user",
    },
  },
  "/api/admin/onboarding/status": {
    completed: true, setupOpen: false, shouldRedirectToSetup: false,
    schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"],
    completionSource: "setup-guide", completedAt: "2026-01-01", revision: 2,
    recoveryNotices: [],
    steps: [{ id: "legacy-environment", title: "Review previous settings", explanation: "Preview your previous settings.", required: false, state: "pending", href: "#/settings/setup" }],
    migration: { available: true, completed: false, firstRun: true },
  },
  "/api/admin/onboarding/complete": {
    completed: true, setupOpen: false, shouldRedirectToSetup: false,
    schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"],
    completionSource: "setup-guide", completedAt: "2026-01-01", revision: 2,
    recoveryNotices: [],
    steps: [{ id: "legacy-environment", title: "Review previous settings", explanation: "Preview your previous settings.", required: false, state: "pending", href: "#/settings/setup" }],
    migration: { available: true, completed: false, firstRun: true },
  },
  "/api/admin/onboarding/reopen": {
    completed: false, setupOpen: true, shouldRedirectToSetup: false,
    schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"],
    completionSource: "administrator", reopenedAt: "2026-01-01", revision: 3,
    recoveryNotices: [],
    steps: [{ id: "legacy-environment", title: "Review previous settings", explanation: "Preview your previous settings.", required: false, state: "pending", href: "#/settings/setup" }],
    migration: { available: true, completed: false, firstRun: true },
  },
  "/api/admin/ui/schema": schema,
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
      catalogProviderId: "deezer", sourceConfirmed: true, cached: false,
      positionSeconds: 30, durationSeconds: 120, progress: 0.25,
      isPaused: false,
      lastActivity: "2026-01-01", scrobbleThresholdSeconds: 60,
      scrobbleEligible: false, scrobbleDeliveries: [], scrobbled: false,
    }],
  },
  "/api/admin/playlist-links": {
    playlistLinks: [{
      id: "playlist-link", enabled: true, name: "Test playlist",
      sourceProviderId: "lumen-audio", sourcePlaylistId: "source-list",
      providerAccountId: "lumen",
      targetProtocol: "jellyfin", targetBackendInstanceId: "main",
      targetCredentialReferenceId: "33333333-3333-3333-3333-333333333333",
      targetPlaylistId: "jellyfin-playlist", mode: "hybrid", projectionMode: "resolved",
      materializationMode: "reconcile", importMode: "linked", trackRetention: "onDemand",
      mirrorStaleEntries: false,
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
      displayName: "Jellyfin Music",
      credentialReferenceId: "33333333-3333-3333-3333-333333333333",
    }],
  },
  "/api/admin/provider-accounts": {
    listenersCanConnectOwnAccounts: true,
    audienceUsers: [
      { id: "user", displayName: "Tester" },
      { id: "listener", displayName: "Listener" },
    ],
    accounts: [{
      id: "account", providerId: "lumen-audio", displayName: "Lumen account",
      sourceDisplayName: "Lumen Audio", scope: "Personal", enabled: true, revision: 1, canManage: true, canChangeAudience: true,
      ownerUserId: "user", ownerDisplayName: "Tester", createdByUserId: "user",
      creatorDisplayName: "Tester",
      configuration: { region: "ca" }, configuredFields: ["token", "region"],
      secret: { configured: true, revoked: false }, createdAt: "2026-01-01", updatedAt: "2026-01-01",
    }],
  },
  "/api/admin/providers/status": [
    {
      provider: "lumen-audio", providerAccountId: "account", providerAccountName: "Lumen account",
      capability: "metadata", accountScope: "personal", supported: true, enabled: true,
      configuration: "configured", health: "healthy", ready: true, canAttempt: true, canTest: true,
    },
    {
      provider: "lumen-audio", providerAccountId: "account", providerAccountName: "Lumen account",
      capability: "streaming", accountScope: "personal", supported: true, enabled: true,
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
    matching: { localPreferencePercent: 7 },
    appleDownload: { baseUrl: "http://apple-download.test" },
    library: { storageMode: "Cache", cacheDurationHours: 24 },
    cache: { searchResultsMinutes: 1, mediaMaximumMegabytes: 512, transcodeCacheMinutes: 60 },
    providers: { streamingOrder: "lumen-audio" },
  },
  "/api/admin/storage": { storage: { provider: "Sqlite", readiness: "Ready" }, backups: [] },
  "/api/admin/cache": {
    memory: { entryCount: 2, payloadBytes: 2048, maximumBytes: 16_777_216, hitRatio: .75, hits: 3, misses: 1, writes: 2, evictions: 1 },
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
    scope: { protocol: "jellyfin", backendInstanceId: "main" },
    policy: { enabled: true, retentionDays: 30, revision: 1, targetCredentialReferenceId: "33333333-3333-3333-3333-333333333333", targetCredentialConfigured: true },
    availableSignalTypes: [{ id: "play", label: "Play", enabled: true }],
    providers: [{
      id: "lumen-audio", label: "Lumen Audio", description: "Private similarity source.",
      enabled: true, available: true, state: "ready",
    }, {
      id: "audiomuse-ai", label: "AudioMuse-AI", description: "Explore this library by sound.",
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
      listenedAt: "2025-12-31T18:00:00Z", durationMilliseconds: 180_000, client: "ios",
      source: "import", provider: "spotify", state: "completed", enrichmentState: "notrequested",
      artworkUrl: "/api/admin/downloads/artwork/ext-spotify-song-track-a", targetStatuses: [], revision: 2,
    }],
    nextCursor: null,
  },
  "/api/admin/intelligence/history/44444444-4444-4444-4444-444444444444": {
    item: {
      id: "44444444-4444-4444-4444-444444444444", title: "Moon Song", artist: "The Comets", album: "Night Drive",
      listenedAt: "2025-12-31T18:00:00Z", durationMilliseconds: 180_000, client: "ios",
      source: "import", provider: "spotify", state: "completed", enrichmentState: "notrequested",
      artworkUrl: "/api/admin/downloads/artwork/ext-spotify-song-track-a", targetStatuses: [], revision: 2,
    },
    identity: { albumArtist: "The Comets", musicBrainzEnrichmentConfidence: null },
    provenance: { source: "import", client: "ios", device: null, provider: "spotify", imported: true },
  },
  "/api/admin/intelligence/history/imports": { items: [] },
  "/api/admin/history/imports": [],
};

function routeRelease() {
  let release!: () => void;
  const promise = new Promise<void>((resolve) => { release = resolve; });
  return { promise, release };
}

async function mockApi(page: Page, options: {
  releasePath?: string;
  release?: Promise<void>;
  fail?: string[];
  audioMuseUnavailable?: boolean;
  audioMuseDisabledAccount?: boolean;
  intelligenceEnabled?: boolean;
  trackArtworkUrl?: string | null;
} = {}) {
  await page.route("**/fonts/**", (route) => route.fulfill({ status: 204 }));
  await page.route("**/images/providers/**", async route => {
    const brand = new URL(route.request().url()).pathname.match(/^\/images\/providers\/brands\/([a-z0-9-]+)\/(color|glyph)\.(svg|png)$/);
    if (brand) {
      const body = await readFile(new URL(`../../allstarr/wwwroot/images/providers/brands/${brand[1]}/${brand[2]}.${brand[3]}`, import.meta.url))
        .catch(error => { if (error.code !== "ENOENT") throw error; return null; });
      return body ? route.fulfill({ contentType: brand[3] === "svg" ? "image/svg+xml" : "image/png", body })
        : route.fulfill({ status: 404 });
    }
    return route.fulfill({
      status: 200,
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1 1"/>',
    });
  });
  await page.route("**/api/admin/**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === "/api/admin/downloads/artwork/ext-spotify-song-track-a") {
      await route.fulfill({
        status: 200,
        contentType: "image/svg+xml",
        body: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1 1"><path fill="#4285f4" d="M0 0h1v1H0z"/></svg>',
      });
      return;
    }
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
    if (url.pathname.endsWith("/health-history")) body = {
      provider: url.pathname.split("/").at(-2), capturedAt: new Date().toISOString(),
      checks: [], counts: { passed: 0, failed: 0, neutral: 0 },
    };
    if (url.pathname === "/api/admin/providers/use") body = {
      personalRevision: "source-use-personal", householdRevision: "source-use-household",
      providers: (responses["/api/admin/ui/schema"] as { providers: { id: string }[] }).providers.map(provider => ({
        providerId: provider.id, householdMode: "automatic", personalMode: null,
        effectiveMode: "automatic", canChoosePersonal: true,
      })),
    };
    if (url.pathname === "/api/admin/jobs" && body === undefined) {
      const fixture = structuredClone(responses["/api/admin/jobs?limit=100"]) as { jobs: Array<{ state: string }>; progress: unknown[] };
      const history = url.searchParams.get("view") === "history";
      fixture.jobs = fixture.jobs.filter(job => history === ["Succeeded", "Failed", "Cancelled"].includes(job.state));
      body = { ...fixture, hasMore: false };
    }
    if (url.pathname === "/api/admin/activity/tasks" && body === undefined) body = { items: [] };
    if (url.pathname === "/api/admin/auth/me" && options.intelligenceEnabled === false) {
      body = {
        ...(structuredClone(body) as Record<string, unknown>),
        features: { intelligence: false },
      };
    }
    if (url.pathname === "/api/admin/provider-accounts" && route.request().method() === "GET" &&
        options.audioMuseDisabledAccount) {
      const accountResponse = structuredClone(body) as typeof responses["/api/admin/provider-accounts"];
      body = {
        ...(accountResponse as Record<string, unknown>),
        accounts: [
          ...((accountResponse as { accounts: Array<Record<string, unknown>> }).accounts ?? []),
          {
            id: "audio-account", providerId: "audiomuse-ai", displayName: "My AudioMuse-AI connection",
            sourceDisplayName: "AudioMuse-AI", scope: "Personal", enabled: false, revision: 2,
            ownerUserId: "user", ownerDisplayName: "Tester", createdByUserId: "user",
            creatorDisplayName: "Tester", configuration: {}, configuredFields: [],
            secret: { configured: false, revoked: false },
            createdAt: "2026-01-01", updatedAt: "2026-01-01",
          },
        ],
      };
    }
    if (url.pathname === "/api/admin/intelligence" && options.audioMuseUnavailable) {
      const intelligence = structuredClone(body) as typeof responses["/api/admin/intelligence"];
      body = {
        ...(intelligence as Record<string, unknown>),
        providers: ((intelligence as { providers: Array<Record<string, unknown>> }).providers ?? []).map((provider) =>
          provider.id === "audiomuse-ai" ? { ...provider, available: false, state: "unconfigured" } : provider),
      };
    }
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
          externalSnapshotId: "snapshot", providerId: "lumen-audio",
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
            reasons: ["title_fuzzy", "artist_exact"], warnings: ["semantic_version_mismatch_review"],
          }, {
            libraryTrackId: "metadata-only", isLocal: false, title: "MusicBrainz album",
            artist: "Metadata only", providerTrackIds: { musicbrainzalbum: "release-id" },
            confidence: 0.99, durationMilliseconds: 180_000,
          }, ...(url.searchParams.get("search") === "Test song" ? [{
            libraryTrackId: "jellyfin-track", backendItemId: "jellyfin-track", isLocal: true,
            title: "Test song", artist: "Artist", album: "Album",
            confidence: 0.82, durationMilliseconds: 180_000,
            components: { title: 1, priorityWindow: 0.07 },
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
          components: { priorityWindow: 0.07, routingPriority: 0, acceptanceQualified: 1 },
        }],
      };
    if (url.pathname === "/api/admin/track-matches/targets/provider")
      body = {
        tracks: [...(url.searchParams.get("includeLocal") === "true" ? [{
          id: "jellyfin-kiss-me-more", backendItemId: "jellyfin-kiss-me-more",
          title: "Kiss Me More", artist: "Doja Cat feat. SZA", album: "Planet Her",
          durationMilliseconds: 208_000, confidence: 0.91,
          components: { priorityWindow: 0.07, routingPriority: 0, acceptanceQualified: 1 },
        }] : []), {
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
          artworkUrl: options.trackArtworkUrl === undefined ? "/missing-track-art" : options.trackArtworkUrl,
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
    if (url.pathname === "/api/admin/track-matches/rematch-all/preview" && route.request().method() === "GET")
      body = {
        confirmationId: "b".repeat(64), algorithmVersion: "priority-windows-v16",
        totalTracks: 120, resolvedTracks: 80, reviewTracks: 15, unresolvedTracks: 25,
        protectedManualTracks: 5, automaticDecisionsToReplace: 100,
        tracksToRematch: 115, canApply: true,
      };
    if (url.pathname === "/api/admin/track-matches/rematch-all/apply" && route.request().method() === "POST")
      body = {
        jobId: "33333333-3333-3333-3333-333333333333", created: true,
        algorithmVersion: "priority-windows-v16", trackCount: 115,
      };
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
    if (url.pathname === "/api/admin/provider-accounts/audio-account/secret" &&
        route.request().method() === "PUT")
      body = { accountId: "audio-account", revision: 3 };
    if (url.pathname === "/api/admin/provider-accounts/audio-account" &&
        route.request().method() === "PATCH")
      body = { enabled: true, revision: 4 };
    if (url.pathname === "/api/admin/providers/test/audiomuse-ai" &&
        route.request().method() === "POST")
      body = { healthy: true };
    if (url.pathname === "/api/admin/config/migration/preview")
      body = {
        previewToken: "preview-token", revision: "revision", expiresAt: "2026-01-01",
        canApply: true, importedSettingCount: 1, providerAccountCount: 1, manualCount: 0,
        backendIdentityCount: 1, playlistLinkCount: 1, scheduleCount: 1,
        items: [{
          key: "CACHE_LYRICS_DAYS", sourceLine: 1, action: "import_if_absent",
          reason: "Import into household durable runtime settings.", sensitive: false,
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
  ["#/for-you", "For you"],
  ["#/integrations/services", "Sources"],
  ["#/integrations/accounts", "Sources"],
  ["#/integrations/extensions", "Sources"],
  ["#/integrations/routing", "Settings"],
  ["#/settings/general", "Settings"],
] as const;

const stateRoutes = [
  ["#/", "Home", "Loading Home", "/api/admin/ui/home", ["/api/admin/ui/home", "/api/admin/ui/now-playing"]],
  ["#/library/playlists", "Library", "Loading playlists", "/api/admin/playlist-links", ["/api/admin/playlist-links"]],
  ["#/library/mappings", "Library", "Loading match review", "/api/admin/track-matches", ["/api/admin/track-matches"]],
  ["#/library/cached", "Library", "Loading Cached tracks", "/api/admin/downloads", ["/api/admin/downloads"]],
  ["#/activity", "Activity", "Loading Event log", "/api/admin/ui/activity", ["/api/admin/ui/activity"]],
  ["#/integrations/services", "Sources", "Loading Services", "/api/admin/provider-accounts", ["/api/admin/ui/schema", "/api/admin/provider-accounts"]],
  ["#/settings/general", "Settings", "Loading items", "/api/admin/ui/schema", ["/api/admin/ui/schema"]],
] as const;

async function expectUnderlineTabs(tabs: Locator) {
  await expect(tabs).toHaveClass(/\btabs\b/);
  await expect(tabs).not.toHaveClass(/\bseg\b/);
  await expect(tabs).toHaveCSS("border-radius", "0px");
  await expect(tabs).toHaveCSS("border-top-width", "0px");
  await expect(tabs).toHaveCSS("border-bottom-width", "1px");
  const selected = tabs.getByRole("tab", { selected: true });
  await expect(selected).toHaveCSS("font-weight", "500");
  await expect(selected).toHaveCSS("background-color", "rgba(0, 0, 0, 0)");
  await expect(tabs.locator(".slide-indicator")).toBeVisible();
  await expect.poll(() => tabs.evaluate(element => {
    const probe = document.createElement("span");
    document.body.append(probe);
    const color = (token: string) => {
      probe.style.color = `var(${token})`;
      return getComputedStyle(probe).color;
    };
    const errors: string[] = [];
    const row = getComputedStyle(element);
    if (row.borderBottomColor !== color("--color-edge")) errors.push("row border");
    for (const tab of element.querySelectorAll('[role="tab"]')) {
      const style = getComputedStyle(tab);
      if (style.color !== color(tab.getAttribute("aria-selected") === "true" ? "--color-ink" : "--color-ink-muted")) errors.push("text color");
      if (style.borderRadius !== "0px" || style.backgroundColor !== "rgba(0, 0, 0, 0)") errors.push("tab surface");
    }
    const selected = element.querySelector('[aria-selected="true"]')!;
    const indicator = element.querySelector(".slide-indicator")!;
    const underline = getComputedStyle(indicator);
    if (underline.height !== "2px" || underline.backgroundColor !== color("--color-signal")) errors.push("underline style");
    const tabBounds = selected.getBoundingClientRect(), lineBounds = indicator.getBoundingClientRect();
    if (Math.abs(lineBounds.left - tabBounds.left) > 1 || Math.abs(lineBounds.width - tabBounds.width) > 1 || Math.abs(lineBounds.bottom - tabBounds.bottom) > 1) errors.push("underline position");
    probe.remove();
    return errors;
  })).toEqual([]);
}

async function mockSignedSessionExtension(page: Page, initial: Record<string, unknown> | "unavailable") {
  let revision = 1;
  let session: Record<string, unknown> = initial === "unavailable" ? { state: "signed_out" } : initial;
  let statusFailures = initial === "unavailable" ? 1 : 0;
  type SessionResponse = { status?: number; body: Record<string, unknown> };
  const responses: Record<string, SessionResponse[]> = { start: [], grant: [], clear: [] };
  const writes: Array<{ action: string; body: Record<string, unknown> }> = [];
  const pending = { state: "verification_pending", verificationUrl: "https://verify.example.test/challenge/opaque" };
  await page.route("**/api/admin/extensions/packages", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([{
      id: "signed", extensionId: "signed-audio", displayName: "Signed Audio", version: "1.0.0",
      lifecycle: "active", state: "active", active: true, installed: true, permissionReviewRequired: false,
      hasPermissions: true, usesSignedSession: true, capabilities: ["streaming"], stagedAt: "2026-01-01", revision,
    }]),
  }));
  await page.route("**/api/admin/extensions/packages/signed/session**", async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() === "GET") {
      if (statusFailures-- > 0)
        return route.fulfill({ status: 500, contentType: "application/json", body: JSON.stringify({ error: "unavailable" }) });
      return route.fulfill({ contentType: "application/json", body: JSON.stringify(session) });
    }
    const action = path.endsWith("/start") ? "start" : path.endsWith("/grant") ? "grant" : "clear";
    writes.push({ action, body: request.postDataJSON() as Record<string, unknown> });
    const next: SessionResponse = responses[action].shift() ?? {
      body: action === "start" ? pending : action === "grant"
        ? { state: "signed_in", expiresAt: "2099-06-15T12:00:00Z" } : { state: "signed_out" },
    };
    if (!next.status || next.status < 400) session = { ...next.body, reasonCode: undefined };
    return route.fulfill({ status: next.status ?? 200, contentType: "application/json", body: JSON.stringify(next.body) });
  });
  return {
    writes,
    pending,
    respond: (action: "start" | "grant" | "clear", body: Record<string, unknown>, status?: number) =>
      responses[action].push({ status, body }),
    updatePackage: () => { revision++; },
  };
}

async function expectRecordingLyricsGeometry(page: Page, picker: Locator, width: number) {
  if (width === 390) await expectContainedMobileGeometry(page);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  const audit = await picker.evaluate((element, phone) => {
    const failures: string[] = [];
    for (const child of element.querySelectorAll<HTMLElement>("*")) {
      const rect = child.getBoundingClientRect();
      if (!rect.width || !rect.height) continue;
      if (rect.left < -1 || rect.right > innerWidth + 1) failures.push(`overflow: ${child.tagName}`);
      if (child.matches("button, textarea, label")) {
        const target = getComputedStyle(child, "::after");
        const expanded = child.matches('button[data-slot="button"]') && target.content === '""';
        const targetWidth = expanded ? Math.max(rect.width, parseFloat(target.width) || 0, parseFloat(target.minWidth) || 0) : rect.width;
        const targetHeight = expanded ? Math.max(rect.height, parseFloat(target.height) || 0, parseFloat(target.minHeight) || 0) : rect.height;
        if (targetWidth < 44 || targetHeight < 44)
          failures.push(`target: ${child.tagName} ${targetWidth}×${targetHeight}`);
      }
    }
    return failures;
  }, width < 900);
  expect(audit, "lyrics controls stay contained and provide 44px targets in both layouts").toEqual([]);
}

const lyricsRecordingId = "11111111-1111-4111-8111-111111111111";

async function mockRecordingLyrics(page: Page, options: { administrator?: boolean; conflict?: boolean; failFirst?: boolean } = {}) {
  await mockApi(page);
  const administrator = options.administrator ?? true;
  if (!administrator) await page.route("**/api/admin/auth/me", route => route.fulfill({ json: {
    ...(responses["/api/admin/auth/me"] as object),
    user: { id: "listener", name: "Listener", isAdministrator: false },
  } }));
  await page.route("**/api/admin/track-matches?*", route => route.fulfill({ json: {
    matches: [{
      externalSnapshotId: "snapshot", canonicalRecordingId: lyricsRecordingId, providerId: "lumen-audio",
      title: "Fixture song", artist: "Artist", album: "Album", state: "suggested", confidence: 0.8, threshold: 0.9,
      candidates: [], providerIdentities: [], reasons: [], warnings: [],
      allowedAuthorityScopes: administrator ? ["personal", "household"] : ["personal"],
    }],
    stats: { total: 1, matched: 0, accepted: 0, unresolved: 0, suggested: 1, review: 1, rejected: 0, attention: 1 },
    pagination: { page: 1, pageSize: 50, total: 1, totalPages: 1 },
  } }));
  await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
    ...schema, providers: [...schema.providers, { id: "lrclib", name: "LRCLIB", categories: ["lyrics"] },
      { id: "lyricsplus", name: "LyricsPlus", categories: ["lyrics"] }],
  } }));
  let personal: { id: string; sourceId: string; revision: number } | null = { id: "personal-choice", sourceId: "spotify", revision: 1 };
  let household: { id: string; sourceId: string; revision: number } | null = { id: "household-choice", sourceId: "lrclib", revision: 2 };
  let reads = 0;
  const writes: { method: string; body: Record<string, unknown> }[] = [];
  const layers = () => ({ personal, household, effective: personal ?? household,
    revision: { personal: personal && { id: personal.id, revision: personal.revision },
      household: household && { id: household.id, revision: household.revision } } });
  await page.route(`**/api/admin/recordings/${lyricsRecordingId}/lyrics*`, route => {
    const method = route.request().method();
    if (method === "GET") {
      reads++;
      if (options.failFirst && reads === 1) return route.fulfill({ status: 503, json: { error: "Lyrics sources are temporarily unavailable. Try again shortly." } });
      const scope = new URL(route.request().url()).searchParams.get("scope") ?? "personal";
      return route.fulfill({ json: {
        recordingId: lyricsRecordingId, scope, song: { title: "Fixture song", artist: "Artist", album: "Album", durationSeconds: 180 },
        choices: layers(), sources: [
          { sourceId: "spotify", available: false, preview: null, previewRevision: null, format: null, truncated: false, score: null, rank: null, reasons: null },
          { sourceId: "lrclib", available: true, preview: scope === "household" ? "Shared plain words" : "Personal plain words <script>inert</script>",
            previewRevision: `sha256:${scope}-plain-${reads}`, format: "plain", truncated: false, score: 0.8, rank: 2, reasons: ["plain_text"] },
          { sourceId: "lyricsplus", available: true, preview: "[00:01.234]Timed words", previewRevision: `sha256:${scope}-timed-${reads}`, format: "synced", truncated: true,
            score: 0.95, rank: 1, reasons: ["synced_lines", "duration_fits"] },
        ],
      } });
    }
    const body = route.request().postDataJSON();
    writes.push({ method, body });
    if (options.conflict && writes.length === 1) {
      personal = { id: "personal-choice", sourceId: "lyricsplus", revision: 8 };
      return route.fulfill({ status: 409, json: { error: "The lyrics or saved choice changed. Reload the sources and try again." } });
    }
    if (body.scope === "household") household = method === "DELETE" ? null : { id: "household-choice", sourceId: body.sourceId, revision: (household?.revision ?? 0) + 1 };
    else personal = method === "DELETE" ? null : { id: "personal-choice", sourceId: body.sourceId, revision: (personal?.revision ?? 0) + 1 };
    return route.fulfill({ json: layers() });
  });
  return { writes, reads: () => reads };
}

const tryCatalog = {
  provider: "lumen-audio",
  capabilities: [
    { capability: "metadata", accountRequirement: "required", operations: [
      { id: "search", inputs: ["query", "kind"], kinds: ["tracks", "albums", "artists"] },
      { id: "track", inputs: ["id"] }, { id: "isrc", inputs: ["isrc"] },
    ] },
    { capability: "lyrics", accountRequirement: "none", operations: [{ id: "lyrics", inputs: ["title", "artist", "album", "durationSeconds", "isrc", "trackId"] }] },
    { capability: "streaming", accountRequirement: "required", operations: [{ id: "stream", inputs: ["trackId", "quality"] }] },
    { capability: "download", accountRequirement: "required", operations: [{ id: "download", inputs: ["trackId", "quality"] }] },
  ],
};

const tryTracks = [
  { id: "lumen-71230", title: "Get Lucky (feat. Pharrell Williams and Nile Rodgers)", artists: ["Daft Punk", "Pharrell Williams", "Nile Rodgers"], album: "Random Access Memories", durationSeconds: 369, isrc: "USQX91300108", explicit: false, artworkUrl: null, year: 2013 },
  { id: "lumen-71231", title: "Get Lucky (Radio Edit)", artists: ["Daft Punk"], album: "Get Lucky", durationSeconds: 248, isrc: "USQX91300105", explicit: true, artworkUrl: null, year: 2013 },
];

async function mockTry(page: Page, requests: Record<string, unknown>[] = []) {
  await page.route("**/api/admin/providers/lumen-audio/try**", async (route) => {
    if (route.request().method() === "GET") return route.fulfill({ json: tryCatalog });
    const body = route.request().postDataJSON() as Record<string, unknown>;
    requests.push(body);
    const base = { provider: "lumen-audio", capability: body.capability, operation: body.operation, accountId: body.accountId ?? null, succeeded: true, elapsedMs: 840, reasonCode: null, message: null };
    if (body.operation === "search") return route.fulfill({ json: { ...base, tracks: body.query === "nothing" ? [] : tryTracks } });
    if (body.operation === "lyrics") return route.fulfill({ json: { ...base, elapsedMs: 1320, lyrics: { format: "word", source: "Lumen Audio", language: "en", lineCount: 3, lines: [
      { startMs: 31405, text: "Like the legend of the phoenix" }, { startMs: 33728, text: "All ends with beginnings" }, { startMs: 37200, text: "What keeps the planet spinning" },
    ] } } });
    if (body.operation === "stream") return route.fulfill({ json: { ...base, elapsedMs: 1450, media: { kind: "stream", url: "/api/admin/providers/try/media/stream-ticket", expiresAt: "2026-10-19T20:00:00Z", fileName: null, sizeBytes: null, contentType: "audio/flac", codec: "flac", sampleRateHz: 44100, bitDepth: 16, bitrateKbps: null, durationSeconds: 369, quality: "lossless", seekMode: "byte-range", startMs: 1180 } } });
    if (body.operation === "download") return route.fulfill({ json: { ...base, elapsedMs: 9400, media: { kind: "download", url: "/api/admin/providers/try/media/download-ticket", expiresAt: "2026-10-19T20:00:00Z", fileName: "Daft Punk - Get Lucky.flac", sizeBytes: 46_137_344, contentType: "audio/flac", codec: "flac", sampleRateHz: 96000, bitDepth: 24, bitrateKbps: null, durationSeconds: 369, quality: "high-resolution", seekMode: null, startMs: null } } });
    return route.fulfill({ status: 404, json: { error: "Unknown operation" } });
  });
  await page.route("**/api/admin/providers/try/media/**", (route) => route.fulfill({ status: 204 }));
}

async function openLyricsTry(page: Page, requests: Record<string, unknown>[]) {
  await mockApi(page);
  await mockTry(page, requests);
  await page.goto("#/integrations/accounts");
  await page.getByRole("link", { name: "Open Lumen Audio account" }).click();
  await page.locator(".service-account.focused .service-check").filter({ hasText: "Metadata" })
    .getByRole("button", { name: "Open Metadata tests" }).click();
  const dialog = page.getByRole("dialog", { name: "Test Lumen Audio" });
  await dialog.getByRole("tab", { name: "Lyrics" }).click();
  return dialog;
}

const spicyDefinition = {
  id: "spicylyrics", name: "SpicyLyrics", categories: ["lyrics"], connectionKind: "built_in",
  logoUrl: "/images/providers/spicylyrics.ico", supportedAudiences: ["Personal", "Shared"],
  accountSettings: [{ key: "apiKey", label: "Server API key", type: "password", sensitive: true, required: true }],
  runtimeCapabilities: [{ id: "lyrics", supported: true, canTest: true, status: "ready" }],
};

const spicyCredits = {
  service: "spicylyrics", catalog: "spicy_lyrics",
  uploader: { username: "Uploader with a very long display name for narrow screens", url: "https://spicylyrics.org/uid/123" },
  maker: { username: "Maker <script>inert</script>", url: "https://spicylyrics.org/uid/456" },
};

const spicyTrack = "4iV5W9uYEdYUVa79Axb7Rh";

const spicyUnlinkedMaker = "Unlinked maker " + "x".repeat(185);

async function mockSpicySource(page: Page, configured = true) {
  await mockApi(page);
  const accountFixture = responses["/api/admin/provider-accounts"] as { accounts: Record<string, unknown>[]; audienceUsers: object[] };
  await page.route("**/images/providers/spicylyrics.ico", async route => route.fulfill({
    contentType: "image/x-icon", body: await readFile("../allstarr/wwwroot/images/providers/spicylyrics.ico"),
  }));
  await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: { ...schema, providers: [...schema.providers, spicyDefinition] } }));
  await page.route("**/api/admin/providers/use", route => route.fulfill({ json: {
    personalRevision: "source-use-personal", householdRevision: "source-use-household",
    providers: [...schema.providers, spicyDefinition].map(provider => ({
      providerId: provider.id, householdMode: "automatic", personalMode: null,
      effectiveMode: "automatic", canChoosePersonal: true,
    })),
  } }));
  await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: {
    ...accountFixture, accounts: configured ? [{ ...accountFixture.accounts[0], id: "spicy-account", providerId: "spicylyrics",
      displayName: "Spicy key", configuration: {}, configuredFields: ["apiKey"] }] : [],
  } }));
  await page.route("**/api/admin/providers/status", route => route.fulfill({ json: configured ? [{
    provider: "spicylyrics", providerAccountId: "spicy-account", providerAccountName: "Spicy key", capability: "lyrics",
    accountScope: "personal", supported: true, enabled: true, configuration: "configured", health: "healthy",
    ready: true, canAttempt: true, canTest: true,
  }] : [] }));
}

const sourceHistoryTime = "2030-04-05T12:00:00.000Z";

const sourceHistory = {
  provider: "lumen-audio", capturedAt: sourceHistoryTime,
  counts: { passed: 3, failed: 3, neutral: 3 },
  checks: Array.from({ length: 9 }, (_, index) => ({
    capability: index % 2 ? "streaming" : "metadata",
    accountLabel: index === 0 ? "Long account " + "界".repeat(190) : index % 2 ? "Household account" : "Public access",
    accountScope: index % 2 ? "shared" : "public",
    testedAt: "2030-04-05T11:59:00.000Z",
    outcome: ["passed", "failed", "neutral"][index % 3],
    reasonCode: index % 3 === 1 ? "timeout" : index % 3 === 2 ? "sample-unavailable" : null,
    latencyMs: index === 0 ? 0 : index === 1 ? null : 125,
  })),
};

const sourcePause = {
  provider: "lumen-audio", providerAccountId: "account", providerAccountName: "Household account",
  capability: "streaming", accountScope: "shared", supported: true, enabled: true,
  configuration: "configured", health: "degraded", ready: false, canAttempt: false, canTest: true,
  automaticPausedAt: "2030-04-05T12:00:00.000Z", automaticRetryAt: "2030-04-05T12:01:00.000Z", failureCount: 3, automaticPauseReasonCode: "RateLimited",
};

export {
  openOtherPlaylistSources,
  nextPlaylistOptions,
  viewports,
  clickWhenStable,
  openSourceSettings,
  waitForSettledOptions,
  schema,
  responses,
  routeRelease,
  mockApi,
  routes,
  stateRoutes,
  expectUnderlineTabs,
  mockSignedSessionExtension,
  expectRecordingLyricsGeometry,
  lyricsRecordingId,
  mockRecordingLyrics,
  tryCatalog,
  tryTracks,
  mockTry,
  openLyricsTry,
  spicyDefinition,
  spicyCredits,
  spicyTrack,
  spicyUnlinkedMaker,
  mockSpicySource,
  sourceHistory,
  sourcePause,
};
