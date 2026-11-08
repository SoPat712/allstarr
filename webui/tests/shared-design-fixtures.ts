import type { Page } from "@playwright/test";
import { readFile } from "node:fs/promises";
import { mockForYou } from "./for-you-fixtures";

const timestamp = "2026-11-04T12:00:00Z";
const listenerId = "11111111-1111-4111-8111-111111111111";
const playlistId = "22222222-2222-4222-8222-222222222222";
const accountId = "33333333-3333-4333-8333-333333333333";
const scheduleId = "44444444-4444-4444-8444-444444444444";
const reportId = "55555555-5555-4555-8555-555555555555";
const jobId = "66666666-6666-4666-8666-666666666666";

export async function panelFixture(page: Page) {
  const blocked: string[] = [];
  const providers = [
    { id: "qobuz", name: "Qobuz", implementationOrigin: "extension", categories: ["streaming"], runtimeCapabilities: [{ id: "streaming", health: "degraded", ready: false, canTest: true, reasonCode: "service_unavailable", testedAt: timestamp }] },
    { id: "apple-musickit", name: "Apple Music", categories: ["streaming", "playlists"], accountSettings: [{ key: "token", label: "Media User Token", type: "text", sensitive: true, required: true }], runtimeCapabilities: [{ id: "streaming", health: "healthy", ready: true, canTest: true }] },
    { id: "spotify", name: "Spotify", categories: ["metadata", "playlists"] },
    { id: "listenbrainz", name: "ListenBrainz", categories: ["scrobbling"] },
  ];
  const accounts = [{ id: accountId, providerId: "apple-musickit", displayName: "Listening account", ownerDisplayName: "Listener", ownerUserId: listenerId, scope: "Personal", canManage: true, canChangeAudience: true, revision: 1, enabled: true, secret: { configured: true, revoked: false }, createdAt: timestamp, configuredFields: ["token"] }];
  const schedule = { id: scheduleId, revision: 1, cronExpression: "0 4 * * *", timeZoneId: "UTC", enabled: true, overlapPolicy: "skip", misfirePolicy: "runOnce", nextRunAt: "2026-11-05T04:00:00Z" };
  const playlist = { id: playlistId, name: "Evening mix", enabled: true, revision: 1, sourceProviderId: "spotify", providerAccountId: accountId,
    sourcePlaylistId: "evening", targetProtocol: "jellyfin", targetBackendInstanceId: "home", targetPlaylistId: "evening-copy",
    mode: "hybrid", importMode: "linked", projectionMode: "resolved", materializationMode: "reconcile", trackRetention: "onDemand",
    mirrorStaleEntries: true, preserveManualEntries: true, syncName: true, syncDescription: true, syncArtwork: true,
    trackCount: 3, matchedCount: 3, unmatchedCount: 0, playableCount: 3, materializedCount: 3, lastRunCode: "order", routeCoverage: [{ providerId: "local", count: 2 }],
    metrics: { total: 3, matched: 3, unresolved: 0, review: 0, rejected: 0, playable: 3, materialized: 3 } };
  const file = { path: "cache/apple/Take On Me.flac", storage: "cache", title: "Take On Me", artist: "a-ha", album: "Hunting High and Low", fileName: "Take On Me.flac",
    size: 29_000_000, sizeFormatted: "29 MB", lastModified: timestamp, lastAccessedAt: timestamp, expiresAt: "2026-11-05T12:00:00Z",
    codec: "FLAC", quality: "Lossless", bitrateKbps: 1064, sampleRateHz: 44100, bitDepth: 16, channels: 2, durationMilliseconds: 229000,
    provider: "apple-musickit", externalId: "take-on-me", artworkUrl: null, publicationState: "Indexed", referenceCount: 0, explicitlyKept: false, removable: true };
  const matches = ["One", "Two"].map((id, index) => ({ externalSnapshotId: id, providerId: "spotify", state: index ? "accepted" : "ambiguous", confidence: .967, threshold: .88,
    title: `Evening song ${id}`, artist: "Example artist", album: "Original album", durationMilliseconds: 180000, isrc: "SOURCE123", playlistNames: ["Evening mix"],
    allowedAuthorityScopes: ["personal", "household"], canonicalRecordingId: `recording-${id}`, providerIdentities: [], reasons: [], warnings: [], manualAuthorities: [],
    target: { providerId: "qobuz", externalId: `current-${id}`, title: `Evening song ${id} (Live)`, artist: "Example artist", album: "Deluxe album", durationMilliseconds: 182000, isrc: "OTHER123" },
    candidates: [{ isCurrent: true, isLocal: false, title: `Evening song ${id} (Live)`, artist: "Example artist", providerTrackIds: { qobuz: `current-${id}` }, confidence: .967 },
      { isLocal: false, title: `Evening song ${id}`, artist: "Example artist", album: "Original album", durationMilliseconds: 180000, providerTrackIds: { "apple-musickit": `other-${id}` }, confidence: .967, components: { title: 1, artist: 1, duration: 1 }, reasons: ["title_exact", "duration_close"] }] }));
  const job = { id: jobId, type: "playlist.materialize", displayName: "Update playlist", state: "Deferred", ownerName: "Listener", summary: "Progress saved. More songs remain.", availableAt: "2026-11-04T13:00:00Z", startedAt: timestamp, updatedAt: timestamp, createdAt: timestamp, canCancel: true, attemptCount: 1 };
  const task = { id: scheduleId, name: "Sync Evening mix for Listener, week of November 2", kind: "playlist", state: "Scheduled", enabled: true, ownerName: "Listener", cadence: "Daily at 04:00 · UTC", lastRunAt: timestamp, nextRunAt: schedule.nextRunAt, runPlaylistLinkId: playlistId };
  const report = { id: reportId, kind: "playback", status: "new", revision: 1, notePreview: "Playback stopped early.", note: "Playback stopped early.", reporterName: "Listener", songTitle: "Evening song", artist: "River", providerId: "jellyfin", clientName: "Music app", createdAt: timestamp, updatedAt: timestamp };
  const trace = { id: "recent", listenerId, startedAt: timestamp, outcome: "completed", request: { presented: { title: "Evening song", artist: "River" } }, resolution: { provider: "library", cached: true }, timing: { firstByteMs: 4600, elapsedMs: 60000, headersMs: 100, omittedPhases: 0, phases: [] } };
  const songs = Array.from({ length: 8 }, (_, index) => ({ mixId: "daily-1", generation: 1, source: "library", reason: index ? "Calm sound" : "Played 6 times in 30 days", score: 1, song: { id: `song-${index}`, canonicalRecordingId: null, title: ["Blue morning", "Quiet water", "Afterglow"][index % 3] + (index > 2 ? ` ${index + 1}` : ""), artist: ["North", "Cedar", "Willow"][index % 3], album: "Open skies" } }));
  const mixes = [1, 2, 3, 4, 5, 6, 7, 8].map(index => ({ id: `daily-${index}`, kind: `daily-${index}`, name: index === 2 ? "Hip-Hop Blend" : index === 3 ? "Acoustic Blend" : `Blend ${index}`, section: "madeForYou", sortOrder: index, description: "R&B · North, Cedar, Willow", sourceLabel: null, recipeVersion: null, updatedAt: timestamp, readiness: { ready: true, message: "Ready" }, inApps: true, appsAllowed: true, coverArtwork: songs.slice(0, 4).map(row => row.song.id) }));
  await page.route("**/*", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (!["127.0.0.1", "localhost"].includes(url.hostname)) { blocked.push(url.href); return route.abort(); }
    if (path.startsWith("/images/") || path === "/favicon.svg") {
      const asset = await readFile(new URL(`../../allstarr/wwwroot${path}`, import.meta.url)).catch(() => null);
      return asset ? route.fulfill({ body: asset, contentType: path.endsWith(".png") ? "image/png" : "image/svg+xml" }) : route.fulfill({ status: 404 });
    }
    if (path.startsWith("/fonts/")) {
      const asset = await readFile(new URL(`../../allstarr/wwwroot${path}`, import.meta.url)).catch(() => null);
      return asset ? route.fulfill({ body: asset, contentType: path.endsWith(".woff2") ? "font/woff2" : "font/ttf" }) : route.fulfill({ status: 404 });
    }
    if (!path.startsWith("/api/")) return route.continue();
    const reply = (json: unknown) => route.fulfill({ json });
    if (path.includes("/artwork/")) return route.fulfill({ contentType: "image/svg+xml", body: '<svg xmlns="http://www.w3.org/2000/svg" width="184" height="184"><rect width="184" height="184" fill="#476875"/></svg>' });
    if (path.endsWith("/auth/me")) return reply({ authenticated: true, backend: "Jellyfin", backendConfigured: true, features: { intelligence: false }, user: { id: "listener", allstarrUserId: listenerId, name: "Listener", isAdministrator: true } });
    if (path.endsWith("/auth/oidc/status")) return reply({ enabled: false });
    if (path.endsWith("/onboarding/status")) return reply({ completed: true, shouldRedirectToSetup: false, setupOpen: false, completedSteps: ["backend-identity"], revision: 1, recoveryNotices: [], migration: {} });
    if (path.endsWith("/ui/schema")) return reply({ activeBackend: "Jellyfin", providers, settings: [], priorityGroups: [], configSections: [] });
    if (path.endsWith("/updates/stream")) return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    if (path === "/api/admin/provider-accounts") return reply({ accounts, audienceUsers: [{ id: listenerId, displayName: "Listener" }] });
    if (path === "/api/admin/providers/status") return reply([{ provider: "qobuz", capability: "streaming", ready: false, health: "degraded", canTest: true, canAttempt: false, supported: true, enabled: true, reasonCode: "service_unavailable", testedAt: timestamp }]);
    if (path === "/api/admin/ui/provider-summaries" || path.endsWith("/scores")) return reply({ providers: [] });
    if (path.endsWith("/latest")) return reply({ measurements: [] });
    if (path.endsWith("/health-history")) return reply({ capturedAt: timestamp, counts: { passed: 1, failed: 1, neutral: 0 }, checks: [{ outcome: "failed", capability: "streaming", accountLabel: "Public", testedAt: timestamp }, { outcome: "passed", capability: "streaming", accountLabel: "Public", testedAt: "2026-11-03T12:00:00Z" }] });
    if (path.startsWith("/api/admin/providers/use")) return reply({ householdRevision: "1", personalRevision: "1", providers: providers.map(provider => ({ providerId: provider.id, householdMode: "automatic", personalMode: null, effectiveMode: "automatic", canChoosePersonal: true })) });
    if (path === "/api/admin/scrobbling/deliveries") return reply({ items: [] });
    if (path.endsWith("/auth/playlist-consent")) return reply({ supported: true, granted: true, needsSignIn: false, updatedAt: timestamp, queuedSyncs: 0 });
    if (path.endsWith("/extensions/packages")) return reply([{ id: "qobuz", extensionId: "qobuz", displayName: "Qobuz", version: "1.0.0", description: "Search and stream music from your connected source.", lifecycle: "active", state: "active", active: true, installed: true, permissionReviewRequired: false, hasPermissions: false, capabilities: ["streaming"], revision: 1 }, {
      id: "deezer", extensionId: "deezer", displayName: "Deezer", version: "1.0.0", description: "Search the catalog and stream full songs from your connected Deezer account.", lifecycle: "active", state: "active", active: true, installed: true, permissionReviewRequired: false, hasPermissions: false, capabilities: ["metadata", "streaming", "playlists"], revision: 1,
    }]);
    if (path.endsWith("/extensions/registries")) return reply([]);
    if (path.endsWith("/extensions/store")) return reply({ items: [{ id: "deezer", displayName: "Deezer", version: "2.0.0", types: ["metadata", "streaming", "playlists"] }], errors: [] });
    if (path.includes("/favorites")) return reply({ links: [] });
    if (path === "/api/admin/config") return reply({ library: { storageMode: "Cache", cacheDurationHours: 72 } });
    if (path === "/api/admin/downloads") {
      const kept = url.searchParams.get("storage") === "kept";
      const files = [file, { ...file, path: "cache/apple/Quiet water.flac", title: "Quiet water", artist: "Cedar", externalId: "quiet-water" }].map(item => kept ? { ...item, storage: "kept", explicitlyKept: true, canDeleteKeptCopy: true, referenceCount: 2, publicationState: "Referenced" } : item);
      return reply({ storage: kept ? "kept" : "cache", files, count: files.length, managedCount: files.length, diagnosticCount: 0, totalSize: 58_000_000, totalSizeFormatted: "58 MB" });
    }
    if (path === "/api/admin/playlist-links") return reply({ playlistLinks: [playlist] });
    if (path === `/api/admin/playlist-links/${playlistId}`) return reply({ ...playlist, schedule, snapshotId: "snapshot", localCount: 2, tracks: [], unresolvedCount: 0, reviewCount: 0, unknownDurationCount: 0, durationMs: 540000 });
    if (path === "/api/admin/playlist-sources") return reply({ accounts: [{ id: accountId, providerId: "spotify", displayName: "My Spotify", scope: "personal" }], blockedAccounts: [], providers: [{ id: "spotify", displayName: "Spotify" }] });
    if (path === "/api/admin/media-targets") return reply({ targets: [{ id: "home", protocol: "jellyfin", backendInstanceId: "home", displayName: "Jellyfin" }] });
    if (path.endsWith("/playlist-forks")) return reply([]);
    if (path === "/api/admin/track-matches/selection") {
      const state = url.searchParams.get("state");
      const rows = matches.filter(item => !state || state === "attention" ? item.state === "ambiguous" : item.state === state);
      return reply({ matches: rows });
    }
    if (path === "/api/admin/track-matches") {
      const state = url.searchParams.get("state");
      const rows = url.searchParams.has("externalSnapshotId")
        ? matches.filter(item => item.externalSnapshotId === url.searchParams.get("externalSnapshotId"))
        : !state || state === "needs" || state === "attention" ? matches.filter(item => item.state === "ambiguous") : matches.filter(item => item.state === state);
      return reply({ matches: rows, stats: { total: 2, matched: 1, accepted: 1, automatic: 1, manualMatches: 0, manualRejections: 0, suggested: 0, ambiguous: 1, review: 1, unresolved: 0, rejected: 0, attention: 1 }, pagination: { page: 1, pageSize: 50, total: rows.length, totalPages: 1 } });
    }
    if (path.includes("/track-matches/targets/")) return reply({ tracks: [], providers: [] });
    if (path === "/api/admin/jobs") return reply({ jobs: [job], progress: [], hasMore: false });
    if (path === `/api/admin/jobs/${jobId}`) return reply({ job, progress: [], attempts: [{ attemptNumber: 1, startedAt: timestamp, completedAt: timestamp, outcome: "deferred" }], hasMore: false });
    if (path === "/api/admin/activity/tasks") return reply({ items: [task], hasMore: false });
    if (path === "/api/admin/problem-reports") return reply({ items: [report], nextCursor: null });
    if (path === `/api/admin/problem-reports/${reportId}`) return reply({ ...report, context: { schemaVersion: 1, capturedAt: timestamp, subjectName: "Listener", song: { title: "Evening song", artist: "River", catalogProviderId: "jellyfin" }, match: null, playback: { clientName: "Music app" }, checks: [] } });
    if (path === "/api/admin/play-traces") return reply([trace]);
    if (path.startsWith("/api/admin/play-traces/")) return reply(trace);
    if (path === "/api/admin/ui/activity") return reply({ items: [], hasMore: false });
    if (path === "/api/admin/mixes") return reply(mixes);
    if (path === "/api/admin/mixes/publish-setting") return reply({ enabled: true });
    if (path.startsWith("/api/admin/mixes/")) return reply({ mix: mixes.find(item => item.id === path.split("/").at(-1)) ?? mixes[0], songs });
    if (path === "/api/admin/releases") return reply({ entries: [], excluded: [], coverage: [] });
    if (path === "/api/admin/history/imports") return reply([]);
    if (path === "/api/admin/audiomuse/status") return reply({ reason: null, accessibleLibrarySongs: 1400, analyzedSongs: 1240, coverageComplete: true });
    return reply({});
  });
  return blocked;
}

export async function settingsFixture(page: Page, libraryFailure = false) {
  const writes: Record<string, unknown>[] = [];
  let config = { clientApi: { enabled: false }, backendType: "Jellyfin", operatorSettings: { "Backend:MusicLibraryIds": "music", "Cors:AllowedOrigins": "https://app.invalid" }, audio: { quality: "CdLossless" }, odesli: { enabled: true, rateLimitMs: 1000 }, jellyfin: { url: "http://media.invalid:8096" }, matching: { localPreferencePercent: 7 } };
  await page.route("**/api/admin/**", async route => {
    const path = new URL(route.request().url()).pathname;
    const reply = (json: unknown) => route.fulfill({ json });
    if (path.endsWith("/auth/me")) return reply({ authenticated: true, backend: "Jellyfin", user: { id: "admin", name: "Alex", isAdministrator: true } });
    if (path.endsWith("/auth/oidc/status")) return reply({ enabled: false });
    if (path.endsWith("/updates/stream")) return route.fulfill({ contentType: "text/event-stream", body: ": ready\n\n" });
    if (path.endsWith("/onboarding/status")) return reply({ completed: true, setupOpen: false, shouldRedirectToSetup: false, revision: 1, recoveryNotices: [], migration: {}, steps: [{ id: "backend-identity", title: "Your media server", explanation: "Connect your media server.", required: true, state: "pending" }] });
    if (path.endsWith("/ui/schema")) return reply({ providers: [{ id: "first", name: "First source" }, { id: "second", name: "Second source" }], priorityGroups: [{ id: "metadata", label: "Search", envKey: "METADATA_ORDER", providers: ["first", "second"] }, { id: "streaming", label: "Streaming", envKey: "STREAMING_ORDER", providers: ["second", "first"] }], configSections: [
      { id: "general", label: "Defaults for everyone", fields: [
        { key: "CLIENT_API_ENABLED", label: "Client API for apps", type: "toggle", valuePath: "clientApi.enabled" },
        { key: "BACKEND_TYPE", label: "Music server", type: "text", valuePath: "backendType", ownership: "deployment" },
        { key: "AUDIO_QUALITY", label: "Audio quality", type: "audio-quality", valuePath: "audio.quality" },
        { key: "MATCHING_LOCAL_PREFERENCE_PERCENT", label: "Prefer your own music when unsure", type: "number", valuePath: "matching.localPreferencePercent", min: 0, max: 20 },
      ] }, { id: "operator", label: "Library", fields: [
        { key: "BACKEND_MUSIC_LIBRARY_IDS", label: "Libraries to use", type: "text", valuePath: "operatorSettings.Backend:MusicLibraryIds", requiresRestart: true },
        { key: "CORS_ALLOWED_ORIGINS", label: "Browser origins", type: "text", valuePath: "operatorSettings.Cors:AllowedOrigins", requiresRestart: true },
        { key: "ODESLI_ENABLED", label: "Find the same song on other services", type: "toggle", valuePath: "odesli.enabled" },
        { key: "ODESLI_RATE_LIMIT_MS", label: "Wait between song.link lookups (milliseconds)", type: "number", valuePath: "odesli.rateLimitMs" },
      ] },
    ] });
    if (path.endsWith("/preferences")) {
      const values = { lyricsOrder: "backend,kept", explicitFilter: "All", showExternalLabel: true, showExplicitLabel: true, playbackStart: "Balanced", qualityLimit: null };
      return reply({ values, householdDefaults: values, householdQuality: "BestAvailable", usesHouseholdDefaults: true, revision: "1", cleanOnlyLocked: false });
    }
    if (path.endsWith("/ui/music-libraries")) return libraryFailure
      ? route.fulfill({ status: 502, json: { error: "Music libraries could not be loaded. Check the media server connection." } })
      : reply([{ id: "music", name: "Music" }, { id: "books", name: "Concert recordings" }]);
    if (path.endsWith("/config")) {
      if (route.request().method() === "POST") {
        const body = route.request().postDataJSON(); writes.push(body);
        if (body.updates.CLIENT_API_ENABLED !== undefined) config = { ...config, clientApi: { enabled: body.updates.CLIENT_API_ENABLED === "true" } };
        if (body.updates.BACKEND_MUSIC_LIBRARY_IDS !== undefined) config.operatorSettings["Backend:MusicLibraryIds"] = body.updates.BACKEND_MUSIC_LIBRARY_IDS;
        if (body.updates.AUDIO_QUALITY) config.audio.quality = body.updates.AUDIO_QUALITY;
        if (body.updates.MATCHING_LOCAL_PREFERENCE_PERCENT !== undefined) config.matching.localPreferencePercent = Number(body.updates.MATCHING_LOCAL_PREFERENCE_PERCENT);
        return reply({ updatedKeys: Object.keys(body.updates), requiresRestart: false });
      }
      return reply(config);
    }
    if (path.endsWith("/access/oidc")) return reply({ settings: { enabled: false, authority: "", clientId: "", publicUrl: "", displayName: "Single sign-on", hasClientSecret: false, revision: "1" }, csrfToken: "test" });
    if (path.endsWith("/access/backend")) return reply({ settings: { supported: true, connectionSaved: true, configured: true, needsReview: false, revision: "1" }, csrfToken: "test" });
    if (path.endsWith("/storage/files")) return reply({ settings: { available: true, files: 0, pending: false }, csrfToken: "test" });
    if (path.endsWith("/providers/scores")) return reply({ providers: [{ providerId: "first", score: 100, uptimePercent: 100, checks: 1, days: [], capabilities: [{ capability: "metadata", score: 100, uptimePercent: 100, checks: 1, failures: 0 }] }] });
    if (path.endsWith("/provider-accounts")) return reply({ accounts: [], audienceUsers: [{ id: "sam", displayName: "Sam" }] });
    if (path.endsWith("/playlist-links")) return reply({ playlistLinks: [] });
    if (path.endsWith("/playlist-forks")) return reply([]);
    if (path.endsWith("/clean-only-lock")) return reply({ revision: "1", cleanOnlyLocked: false, values: {}, householdDefaults: {} });
    if (path.endsWith("/alerts/incidents")) return reply([1, 2].map(id => ({ id: String(id), kind: "source-failure", state: "open", message: "A source is repeatedly failing", action: "Review source health", resource: `Source ${id}` })));
    if (path.endsWith("/alerts")) return reply({ channels: [], deliveries: [], revision: 1, permittedDestinations: [], thresholds: null });
    if (path.endsWith("/health")) return reply({ protocol: "Jellyfin", configuration: { mode: "server-api-key", hasConfiguration: true, serviceCredentialReady: true, enabled: true, intervalMinutes: 60, searchQuery: "Sample", firstByteBudgetSeconds: 15, revision: 1 }, listeners: [], runs: [{ id: "run", protocol: "Jellyfin", mode: "server-api-key", startedAt: "2026-11-03T12:00:00Z", completedAt: "2026-11-03T12:00:01Z", passed: false, alertStatus: "none", steps: [{ name: "backend", status: "passed", latencyMilliseconds: 5, reason: null }, { name: "lyrics", status: "skipped", latencyMilliseconds: null, reason: "lyrics_unavailable" }] }] });
    if (path.endsWith("/storage")) return reply({ storage: { readiness: "Ready" }, backups: [{ id: "backup", fileName: "backup.zip", status: "verified", createdAt: "2026-11-03T03:00:00Z", bytes: 1048576, origin: "scheduled" }], restorePending: false, backupPolicy: { enabled: true, timeUtc: "03:00", directory: "", dailyRetentionCount: 14, weeklyRetentionCount: 8, manualRetentionCount: 10, revision: "1" }, backupStatus: { state: "idle", directoryState: "ready", lastSuccessAt: null } });
    if (path.endsWith("/downloads")) return reply({ totalSize: 1048576, count: 1, files: [] });
    if (path.endsWith("/audiomuse/status")) return reply({ reason: null, analyzedSongs: 10, coverageComplete: true });
    if (path.endsWith("/audiomuse/connection")) return reply({ baseUrl: "", keyConfigured: false, revision: 1 });
    return reply({ enabled: false, categories: [], media: {}, memory: {}, metadata: {}, activity: {}, artworkLimits: {}, extensionStorage: {} });
  });
  return writes;
}

export async function homeFixture(page: Page, admin: boolean) {
  await mockForYou(page, { admin });
  await page.route("**/api/admin/ui/home", route => route.fulfill({ json: {
    schema: { providers: [{ id: "catalog", name: "Music service" }], activeBackend: "Jellyfin" },
    status: { backendType: "Jellyfin", durableStorage: { readiness: "Ready" } },
    stats: { linkedPlaylists: 24, playableTracks: 1224, unresolvedTracks: 1, activeJobs: 1,
      completedListens: 25, currentWeekListens: 36, previousWeekListens: 24, scrobbleDeliveries: 6, cacheTracks: 79, keptTracks: 3, topArtist: { name: "North", listens: 6 } },
    providerHealth: { providers: [{ providerId: "catalog", enabledAccountCount: 1, capabilityTotal: 4, healthyCapabilityCount: 3, failedCapabilityCount: 1 }] },
    activity: { items: [
      { id: "routine", kind: "job", label: "Background task", state: "succeeded", detail: "routine", occurredAt: "2026-11-03T20:00:00Z" },
      { id: "recorded", kind: "job", label: "Record a listen", state: "succeeded", detail: "routine", occurredAt: "2026-11-03T20:00:00Z" },
      { id: "saved", kind: "download", label: "Saved a copy of Blue morning", state: "succeeded", detail: "North", occurredAt: "2026-11-03T20:00:00Z" },
    ], hasMore: false },
  } }));
  await page.route("**/api/admin/ui/now-playing", route => route.fulfill({ json: { items: admin ? [{ deviceId: "phone", itemId: "song", userId: "listener", userName: "Listener", client: "Finamp", device: "Phone", title: "Blue morning", artist: "North", album: "Open skies", providerId: "catalog", sourceConfirmed: true, positionSeconds: 60, durationSeconds: 180, lastActivity: "2026-11-03T20:00:00Z", scrobbled: true }] : [] } }));
  await page.route("**/api/admin/cache", route => route.fulfill({ json: { media: { payloadBytes: 1670000000, maximumBytes: 2000000000 } } }));
  await page.route("**/api/admin/storage", route => route.fulfill({ json: { backups: [{ id: "backup", status: "verified", createdAt: "2026-11-02T22:00:00Z", bytes: 185100000 }], backupPolicy: { enabled: true }, backupStatus: { nextRunAt: "2026-11-04T22:00:00Z" } } }));
  await page.route("**/api/admin/stats?*", route => route.fulfill({ json: { period: { from: null, to: "2026-11-04", timeZoneId: "America/New_York" }, playCount: 36, activeDays: 7, importedCount: 0, estimatedTimeFromSongLengthsMilliseconds: 3600000, coverage: { unknownSongIdentityCount: 0, unknownDurationCount: 0 }, topSongs: [], topArtists: [], topAlbums: [], timelineUnit: "day", timeline: [{ key: "2026-10-29", plays: 5 }, { key: "2026-10-30", plays: 8 }, { key: "2026-10-31", plays: 3 }, { key: "2026-11-01", plays: 4 }, { key: "2026-11-02", plays: 6 }, { key: "2026-11-03", plays: 7 }, { key: "2026-11-04", plays: 3 }] } }));
}
