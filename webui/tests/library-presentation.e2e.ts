import { expect, test, type Page, type Route } from "@playwright/test";
import { appearance, mockForYou } from "./for-you-fixtures";
import { mkdir } from "node:fs/promises";

async function playlistsFixture(page: Page) {

const id = "22222222-2222-4222-8222-222222222222";
const listener = "11111111-1111-4111-8111-111111111111";
const account = "33333333-3333-4333-8333-333333333333";
async function fixture(page: Page) {
  const writes: { path: string; body: Record<string, unknown> }[] = [];
  let playlist = { id, name: "Evening mix", enabled: true, revision: 1, sourceProviderId: "spotify", providerAccountId: account,
    sourcePlaylistId: "evening", targetProtocol: "jellyfin", targetBackendInstanceId: "home", targetPlaylistId: "evening-copy",
    mode: "hybrid", importMode: "linked", projectionMode: "resolved", materializationMode: "reconcile", trackRetention: "onDemand",
    mirrorStaleEntries: true, preserveManualEntries: true, syncName: true, syncDescription: true, syncArtwork: true,
    trackCount: 3, matchedCount: 3, unmatchedCount: 0, playableCount: 3, materializedCount: 3, lastRunCode: "order", routeCoverage: [{ providerId: "local", count: 2 }],
    metrics: { total: 3, matched: 3, unresolved: 0, review: 0, rejected: 0, playable: 3, materialized: 3 } };
  let schedule = { id: "schedule", revision: 1, cronExpression: "0 4 * * *", timeZoneId: "UTC", enabled: true, overlapPolicy: "skip", misfirePolicy: "runOnce", nextRunAt: "2026-11-05T04:00:00Z" };
  await page.route("**/*", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (!["localhost", "127.0.0.1"].includes(url.hostname)) return route.abort();
    if (!path.startsWith("/api/")) return route.continue();
    const reply = (body: unknown) => route.fulfill({ json: body });
    if (request.method() !== "GET") writes.push({ path, body: request.postData() ? request.postDataJSON() : {} });
    if (path === "/api/admin/auth/me") return reply({ authenticated: true, backend: "Jellyfin", user: { id: "listener", allstarrUserId: listener, name: "Listener", isAdministrator: false } });
    if (path === "/api/admin/auth/playlist-consent") return reply({ supported: true, granted: true, needsSignIn: false });
    if (path === "/api/admin/auth/oidc/status") return reply({ enabled: false });
    if (path === "/api/admin/ui/schema") return reply({ providers: [{ id: "spotify", name: "Spotify" }], settings: [], priorityGroups: [], configSections: [] });
    if (path === "/api/admin/playlist-links") return reply({ playlistLinks: [playlist] });
    if (path === "/api/admin/activity/tasks") return reply({ items: [{ enabled: true, runPlaylistLinkId: id, nextRunAt: schedule.nextRunAt }] });
    if (path === `/api/admin/playlist-links/${id}`) {
      if (request.method() === "PUT") { playlist = { ...playlist, ...request.postDataJSON(), revision: playlist.revision + 1 }; return reply(playlist); }
      return reply({ ...playlist, schedule, snapshotId: "snapshot", localCount: 2, tracks: [], unresolvedCount: 0, reviewCount: 0, unknownDurationCount: 0, durationMs: 540000 });
    }
    if (path === "/api/admin/playlist-links/schedules/schedule") { schedule = { ...schedule, ...request.postDataJSON(), revision: schedule.revision + 1 }; return reply(schedule); }
    if (path === "/api/admin/playlist-sources") return reply({ accounts: [{ id: account, providerId: "spotify", displayName: "My Spotify" }], blockedAccounts: [], providers: [{ id: "spotify", displayName: "Spotify" }] });
    if (path.includes(`/playlist-sources/${account}/playlists`)) return reply({ items: [{ id: "late", providerId: "spotify", name: "Late drive", trackCount: 12 }, { id: "evening", providerId: "spotify", name: "Evening mix", trackCount: 3 }], nextCursor: null });
    if (path === "/api/admin/media-targets") return reply({ targets: [{ id: "home", protocol: "jellyfin", backendInstanceId: "home", displayName: "Jellyfin" }] });
    if (path === "/api/admin/mixes" || path.endsWith("/playlist-forks")) return reply([]);
    if (path === "/api/admin/jobs") return reply({ jobs: [], progress: [] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return reply({});
  });
  return writes;
}

 return fixture(page);
}

async function matchesFixture(page: Page) {

const makeSong = (id: string, state: string, confidence: number | null) => ({
  externalSnapshotId: id, providerId: "spotify", state, decisionSource: "track_match_decision", confidence, threshold: .88,
  title: `Song ${id}`, artist: "Example artist", album: "Original album", durationMilliseconds: 180000, isrc: "SOURCE123",
  allowedAuthorityScopes: ["personal", "household"], canonicalRecordingId: `recording-${id}`,
  providerIdentities: [], reasons: [], warnings: [], manualAuthorities: [],
  target: state === "unresolved" ? null : { providerId: "qobuz", externalId: `current-${id}`, title: `Song ${id} (Live)`, artist: "Example artist", album: "Deluxe album", durationMilliseconds: 182000, isrc: "OTHER123" },
  candidates: state === "unresolved" ? [] : [
    { isCurrent: true, isLocal: false, title: `Song ${id} (Live)`, artist: "Example artist", providerTrackIds: { qobuz: `current-${id}` }, confidence: confidence ?? 0 },
    { isLocal: false, title: `Song ${id}`, artist: "Example artist", album: "Original album", durationMilliseconds: 180000,
      providerTrackIds: { "apple-download": `better-${id}` }, confidence: .967, components: { title: 1, artist: 1, duration: 1 }, reasons: ["title_exact", "duration_close"] },
  ],
});

async function fixture(page: Page, options: { needs?: boolean; administrator?: boolean } = {}) {
  const needs = options.needs !== false, administrator = options.administrator !== false;
  const songs = needs ? [makeSong("One", "ambiguous", .967), makeSong("Two", "suggested", .967), makeSong("Three", "unresolved", null)] : [];
  songs.push(makeSong("Four", "accepted", .99), makeSong("Five", "accepted", .98));
  const writes: Record<string, unknown>[] = [];
  await page.route("**/api/**", async route => {
    const url = new URL(route.request().url());
    let body: unknown = {};
    if (url.pathname.endsWith("/auth/me")) body = { authenticated: true, backend: "Local library", user: { id: "listener", name: "Listener", isAdministrator: administrator } };
    if (url.pathname.endsWith("/onboarding/status")) body = { completed: true, shouldRedirectToSetup: false, setupOpen: false, completedSteps: [], recoveryNotices: [], migration: {} };
    if (url.pathname.endsWith("/ui/schema")) body = { activeBackend: "Local library", providers: [
      { id: "spotify", name: "Spotify", categories: ["metadata"] }, { id: "qobuz", name: "Qobuz", categories: ["streaming"] }, { id: "apple-download", name: "Apple Music", categories: ["streaming"] },
    ] };
    if (url.pathname === "/api/admin/playlist-links") body = { playlistLinks: [{ id: "first", name: "First playlist" }, { id: "second", name: "Second playlist" }] };
    if (url.pathname.startsWith("/api/admin/playlist-links/")) body = { tracks: songs.map(item => ({ externalSnapshotId: item.externalSnapshotId })) };
    if (url.pathname.endsWith("/resolve")) { writes.push(route.request().postDataJSON()); body = { success: true }; }
    if (url.pathname.endsWith("/rematch")) body = { rematched: true, state: "suggested" };
    if (url.pathname === "/api/admin/track-matches") {
      const state = url.searchParams.get("state");
      const filtered = songs.filter(item => state === "attention" ? ["ambiguous", "suggested"].includes(item.state)
        : state === "unresolved" ? item.state === state : state === "automatic" ? item.state === "accepted" : state === "manual_matches" || state === "manual_rejections" ? false : true);
      const rows = url.searchParams.has("externalSnapshotId") ? songs.filter(item => item.externalSnapshotId === url.searchParams.get("externalSnapshotId")) : filtered;
      body = { matches: rows.map(item => ({ ...item, allowedAuthorityScopes: administrator ? ["personal", "household"] : ["personal"] })),
        stats: { total: songs.length, matched: 2, accepted: 2, automatic: 2, manualMatches: 0, manualRejections: 0, suggested: needs ? 1 : 0, ambiguous: needs ? 1 : 0, review: needs ? 2 : 0, unresolved: needs ? 1 : 0, rejected: 0, attention: needs ? 2 : 0 },
        pagination: { page: 1, pageSize: 200, total: rows.length, totalPages: 1 } };
    }
    if (url.pathname.endsWith("/current")) body = { target: songs.find(item => url.pathname.includes(`/${item.externalSnapshotId}/`))?.target ?? null };
    if (url.pathname.endsWith("/targets/provider")) body = { tracks: [], providers: [] };
    if (url.pathname.endsWith("/updates/stream")) return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    await route.fulfill({ json: body });
  });
  return writes;
}

async function openSong(page: Page, title = "Song One") {
  const row = page.locator(".mapping-row").filter({ has: page.getByText(title, { exact: true }) });
  const opener = row.locator(".open-match:visible, .row-action:visible");
  await opener.click();
  return { panel: page.getByRole("dialog", { name: title, exact: true }), opener };
}

 return fixture(page);
}

async function downloadsFixture(page: Page) {

async function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}

const file = {
  path: "cache/apple/Take On Me.flac", storage: "cache", title: "Take On Me", artist: "a-ha",
  album: "Hunting High and Low", fileName: "Take On Me.flac", size: 29_000_000, sizeFormatted: "29 MB",
  lastModified: "2026-11-03T12:00:00Z", lastAccessedAt: "2026-11-04T12:00:00Z",
  expiresAt: "2026-11-05T12:00:00Z", codec: "FLAC", quality: "Lossless", bitrateKbps: 1064,
  sampleRateHz: 44100, bitDepth: 16, channels: 2, durationMilliseconds: 229000,
  provider: "apple-musickit", externalId: "take-on-me", artworkUrl: null,
  publicationState: "Indexed", referenceCount: 0, explicitlyKept: false, removable: true,
};

async function mockLibrary(page: Page, options: { cacheOff?: boolean; error?: boolean; empty?: boolean; delay?: boolean } = {}) {
  const mutations: string[] = [];
  await page.clock.install({ time: new Date("2026-11-04T14:00:00Z") });
  await page.route("**/images/providers/**", route => route.fulfill({ contentType: "image/svg+xml", body: '<svg xmlns="http://www.w3.org/2000/svg"/>' }));
  await page.route("**/api/admin/**", async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.pathname.endsWith("/auth/me")) return json(route, { authenticated: true, backend: "Jellyfin", features: { intelligence: false }, user: { id: "admin", name: "Administrator", isAdministrator: true } });
    if (url.pathname.endsWith("/auth/oidc/status")) return json(route, { enabled: false });
    if (url.pathname.endsWith("/onboarding/status")) return json(route, { completed: true, setupOpen: false, shouldRedirectToSetup: false, schemaVersion: "fixture-v1", completedSteps: ["backend-identity"], completionSource: "fixture", revision: 1, recoveryNotices: [], migration: { available: false, completed: false, firstRun: false } });
    if (url.pathname.endsWith("/updates/stream")) return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    if (url.pathname.endsWith("/ui/schema")) return json(route, { activeBackend: "Jellyfin", providers: [{ id: "apple-musickit", name: "Apple Music", categories: ["streaming"] }], settings: [], priorityGroups: [] });
    if (url.pathname.endsWith("/config")) return json(route, { library: { storageMode: options.cacheOff ? "Off" : "Cache", cacheDurationHours: 72 } });
    if (url.pathname === "/api/admin/downloads" && request.method() === "GET") {
      if (options.delay) await new Promise(resolve => setTimeout(resolve, 600));
      if (options.error) return json(route, { error: "Fixture storage is unavailable." }, 503);
      const kept = url.searchParams.get("storage") === "kept";
      const files = options.empty ? [] : [
        { ...file, ...(kept ? { storage: "kept", explicitlyKept: true, canDeleteKeptCopy: true, referenceCount: 2, publicationState: "Referenced" } : {}) },
        { ...file, path: "cache/apple/Nicole-24.flac", title: "Nicole Kidman", artist: "ADÉLA", album: "PRIMA", externalId: "nicole", quality: "Hi-Res", sampleRateHz: 48000, bitDepth: 24, expiresAt: "2026-11-07T12:00:00Z", ...(kept ? { storage: "kept", removable: false, referenceCount: 1 } : {}) },
        { ...file, path: "cache/apple/Nicole-16.flac", title: "Nicole Kidman", artist: "ADÉLA", album: "PRIMA", externalId: "nicole", expiresAt: "2026-11-07T12:00:00Z", ...(kept ? { storage: "kept", removable: false, referenceCount: 1 } : {}) },
        { ...file, path: "cache/apple/Low.mp3", title: "Lower quality song", externalId: "low", codec: "MP3", quality: "MP3 128", bitrateKbps: 128, bitDepth: null, ...(kept ? { storage: "kept", removable: false, referenceCount: 1 } : {}) },
      ];
      return json(route, { storage: kept ? "kept" : "cache", files, count: files.length, managedCount: files.length, diagnosticCount: 0, totalSize: files.length * file.size, totalSizeFormatted: files.length ? "116 MB" : "0 B" });
    }
    if (request.method() !== "GET") mutations.push(request.method() + " " + url.pathname);
    return json(route, { success: true, deletedCount: 4 });
  });
  return mutations;
}

 return mockLibrary(page);
}

async function importsFixture(page: Page) {

const userId = "11111111-1111-4111-8111-111111111111";
const accountId = "22222222-2222-4222-8222-222222222222";
const runId = "33333333-3333-4333-8333-333333333333";
const jobId = "44444444-4444-4444-8444-444444444444";
const snapshotId = "55555555-5555-4555-8555-555555555555";
const appleAccountId = "77777777-7777-4777-8777-777777777777";
const appleRunId = "88888888-8888-4888-8888-888888888888";
const account = { id: accountId, providerId: "spotify", displayName: "My Spotify", scope: "Personal",
  ownerUserId: userId, ownerDisplayName: "Alex", enabled: true, canManage: true, revision: 1,
  secret: { configured: true, revoked: false }, createdAt: "2026-10-24T12:00:00Z", updatedAt: "2026-10-24T12:00:00Z" };
const appleAccount = { ...account, id: appleAccountId, providerId: "apple-musickit", displayName: "My Apple Music" };
const liked = { sourceId: "liked-songs", name: "Liked Songs", count: 3, kind: "liked-songs", support: "available" };
const savedAlbums = { sourceId: "saved-albums", name: "Saved albums", count: 2, kind: "saved-albums", support: "available" };
const followedArtists = { sourceId: "followed-artists", name: "Followed artists", count: 2, kind: "followed-artists", support: "available" };
const evening = { sourceId: "evening", name: "Evening mix", count: 4, kind: "playlist", support: "available" };
const weekend = { sourceId: "weekend", name: "Weekend", count: 2, kind: "playlist", support: "available" };
type Counts = { selected: number; local: number; streamed: number; accepted: number; review: number; unmatched: number; unsupported: number; skipped: number };
const counts = (overrides: Partial<Counts> = {}): Counts => ({ selected: 0, local: 0, streamed: 0, accepted: 0, review: 0, unmatched: 0, unsupported: 0, skipped: 0, ...overrides });
type Write = { path: string; body: Record<string, unknown> };
type RunState = "choosing" | "collecting" | "review" | "waiting" | "cancelled" | "applying" | "completed";

async function reply(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}

async function mockImport(page: Page, accounts = [account], withCollections = false) {
  const runAccounts = new Map([[runId, account], [appleRunId, appleAccount]]);
  const accountRuns = new Map(accounts.map(item => [item.id, item.id === accountId ? runId : item.id === appleAccountId ? appleRunId : "99999999-9999-4999-8999-999999999999"]));
  const fixture = { state: "choosing" as RunState, phase: "collecting", fixed: false, appliedFixed: false, createError: false, previewError: false,
    albumChosen: false, artistSkipped: false, collectionChoiceError: false, unsupported: [] as { kind: string }[],
    reads: [] as string[], writes: [] as Write[], forbidden: [] as string[] };
  await page.addInitScript(() => {
    class FixtureStream {
      static instances: FixtureStream[] = [];
      listeners = new Map<string, EventListener[]>();
      constructor() { FixtureStream.instances.push(this); }
      addEventListener(type: string, listener: EventListener) { this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]); }
      close() { FixtureStream.instances = FixtureStream.instances.filter(item => item !== this); }
    }
    Object.defineProperty(window, "EventSource", { value: FixtureStream });
    Object.assign(window, { __emitMigrationUpdate: (id: string) => {
      const event = { data: JSON.stringify({ resource: "migration", resourceId: id, revision: Date.now() }), lastEventId: `migration-${Date.now()}` };
      for (const stream of FixtureStream.instances) for (const listener of stream.listeners.get("update") ?? []) listener(event as unknown as Event);
    } });
  });
  const summary = (id: string) => {
    const sourceAccount = runAccounts.get(id) ?? account;
    const likedCounts = fixture.state === "collecting" ? counts({ selected: 1, local: 1, accepted: 1 }) : counts({ selected: 3, local: fixture.fixed ? 2 : 1, streamed: 1, accepted: fixture.fixed ? 3 : 2, review: fixture.fixed ? 0 : 1 });
    const eveningCounts = fixture.state === "collecting" ? counts({ selected: 1, unmatched: 1 }) : counts({ selected: 4, local: 1, streamed: 1, accepted: 2, unmatched: 1, unsupported: 1 });
    const result = { id, run: { id, providerId: sourceAccount.providerId, providerAccountId: sourceAccount.id, state: fixture.state, phase: fixture.phase, jobId },
      collections: [
        { id: "likes", sourceId: liked.sourceId, collection: { kind: liked.kind, name: liked.name, expected: 3, read: fixture.state === "collecting" ? 1 : 3, state: fixture.state === "completed" ? "applied" : fixture.state === "collecting" ? "collecting" : "review", keepInSync: false, error: fixture.state === "waiting" ? "Progress saved. Restore the same Spotify connection or destination access, then resume." : null, receiptRunId: runId }, counts: likedCounts, unread: fixture.state === "collecting" ? 2 : 0 },
        { id: "mix", sourceId: evening.sourceId, collection: { kind: evening.kind, name: evening.name, expected: 4, read: fixture.state === "collecting" ? 1 : 4, state: fixture.state === "completed" ? "applied" : fixture.state === "collecting" ? "matching" : "review", keepInSync: true, error: fixture.state === "waiting" ? "Progress saved. Restore the same Spotify connection or destination access, then resume." : null, receiptRunId: runId }, counts: eveningCounts, unread: fixture.state === "collecting" ? 3 : 0 },
      ], total: fixture.state === "collecting" ? counts({ selected: 2, local: 1, accepted: 1, unmatched: 1 }) : counts({ selected: 7, local: fixture.fixed ? 3 : 2, streamed: 2, accepted: fixture.fixed ? 5 : 4, review: fixture.fixed ? 0 : 1, unmatched: 1, unsupported: 1 }), provisional: fixture.state === "collecting" };
    if (withCollections) {
      for (const [choice, id, resolved] of [[savedAlbums, "albums", fixture.albumChosen], [followedArtists, "artists", fixture.artistSkipped]] as const) {
        const skipped = id === "artists" && resolved;
        const added = 1 + Number(resolved && !skipped);
        const resourceCounts = counts({ selected: 2, local: added, accepted: added, review: resolved ? 0 : 1, skipped: Number(skipped) });
        result.collections.push({ id, sourceId: choice.sourceId, collection: { kind: choice.kind, name: choice.name, expected: 2, read: 2, state: fixture.state === "completed" ? "applied" : "review", keepInSync: false, error: null, receiptRunId: runId }, counts: resourceCounts, unread: 0 });
        for (const key of Object.keys(result.total) as (keyof Counts)[]) result.total[key] += resourceCounts[key];
      }
    }
    return result;
  };
  const occurrence = (collectionId: string, position: number, title: string | null, state: number, status: number, kind: number | null, added = false) => ({
    collectionId, sourceEntryId: `${collectionId}-${position}`, position, externalSnapshotId: title === "Housewife" ? snapshotId : `${collectionId}-snapshot-${position}`,
    metadata: { title, artists: [title === "Housewife" ? "Tate McRae" : "Mira"], artworkUrl: null },
    preview: { state, status, resolvedRoute: kind === null ? null : { kind } },
    receipt: fixture.state === "completed" && !(title === "Housewife" && !fixture.appliedFixed) ? { outcome: added ? 2 : kind === null ? 4 : 1, outcomeCode: kind === null ? "unresolved" : "included" } : null,
  });
  await page.route("**/*", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (!["127.0.0.1", "localhost"].includes(url.hostname)) { fixture.forbidden.push(url.origin); return route.abort(); }
    if (!path.startsWith("/api/admin/")) return route.continue();
    if (request.method() === "GET") fixture.reads.push(`${path}${url.search}`);
    else fixture.writes.push({ path, body: request.postData() ? request.postDataJSON() : {} });
    if (path === "/api/admin/auth/me") return reply(route, { authenticated: true, backend: "Jellyfin", features: { intelligence: false }, user: { id: "backend-alex", allstarrUserId: userId, name: "Alex", isAdministrator: false } });
    if (path === "/api/admin/auth/oidc/status") return reply(route, { enabled: false });
    if (path === "/api/admin/onboarding/status") return reply(route, { completed: true, setupOpen: false, shouldRedirectToSetup: false, schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"], completionSource: "setup-guide", revision: 1, recoveryNotices: [], migration: { available: false, completed: false, firstRun: false } });
    if (path === "/api/admin/ui/schema") return reply(route, { activeBackend: "Jellyfin", settings: [], configSections: [], priorityGroups: [], providers: [{ id: "spotify", name: "Spotify", categories: ["playlist"], requiresAccount: true, description: "Connect Spotify to use your playlists.", useMode: "automatic", settings: [] }] });
    if (path === "/api/admin/provider-accounts") return reply(route, { accounts, audienceUsers: [], listenersCanConnectOwnAccounts: true });
    if (path === "/api/admin/providers/status") return reply(route, []);
    if (["/api/admin/ui/provider-summaries", "/api/admin/providers/use"].includes(path)) return reply(route, { providers: [] });
    if (path === "/api/admin/provider-diagnostics/deep-stream/latest") return reply(route, { measurements: [] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    if (path === "/api/admin/migrations" && request.method() === "POST") {
      if (fixture.createError) return reply(route, { error: "The connection is temporarily unavailable. Try again." }, 503);
      const sourceAccount = accounts.find(item => item.id === request.postDataJSON().ProviderAccountId)!;
      const id = accountRuns.get(sourceAccount.id)!;
      runAccounts.set(id, sourceAccount);
      return reply(route, { id }, 202);
    }
    if (path === "/api/admin/migrations/preview") {
      if (fixture.previewError) return reply(route, { error: "The connection is temporarily unavailable." }, 503);
      return reply(route, url.searchParams.has("cursor")
        ? { items: [liked, weekend], nextCursor: null, unsupported: fixture.unsupported }
        : { items: [evening, ...(withCollections ? [followedArtists, savedAlbums] : []), liked], nextCursor: "next-playlists", unsupported: fixture.unsupported });
    }
    const currentRun = path.match(/^\/api\/admin\/migrations\/([^/]+)/)?.[1] ?? runId;
    if (path === `/api/admin/migrations/${currentRun}/discover`) return reply(route, url.searchParams.has("cursor")
      ? { items: [liked, weekend], nextCursor: null, unsupported: fixture.unsupported }
      : { items: [evening, ...(withCollections ? [followedArtists, savedAlbums] : []), liked], nextCursor: "next-playlists", unsupported: fixture.unsupported });
    if (path.endsWith("/review/albums/albums-1/choose") || path.endsWith("/review/artists/artists-1/choose")) {
      if (fixture.collectionChoiceError) return reply(route, { error: "Could not save." }, 503);
      if (path.includes("/albums/")) fixture.albumChosen = true; else fixture.artistSkipped = true;
      return route.fulfill({ status: 204, body: "" });
    }
    if (path === `/api/admin/migrations/${currentRun}/choose`) { fixture.state = "collecting"; return route.fulfill({ status: 202, body: "" }); }
    if (path === `/api/admin/migrations/${currentRun}` || path === `/api/admin/migrations/${currentRun}/summary`) return reply(route, summary(currentRun));
    if (path === `/api/admin/migrations/${currentRun}/review`) {
      const id = url.searchParams.get("collectionId");
      if (["albums", "artists"].includes(id ?? "")) {
        const album = id === "albums";
        return reply(route, [0, 1].map(position => ({
          collectionId: id, sourceEntryId: `${id}-${position}`, position, externalSnapshotId: "",
          metadata: { title: album ? position ? "Evening" : "Daylight" : position ? "Mira" : "Noor", artists: album ? ["Mira"] : [], artworkUrl: null, year: album ? 2020 : null, trackCount: album ? 12 : null },
          preview: null, receipt: null,
          resource: { kind: album ? "saved-albums" : "followed-artists", state: position === 0 || album && fixture.albumChosen ? "accepted" : !album && fixture.artistSkipped ? "skipped" : "review", applied: fixture.state === "completed" && (position === 0 || album && fixture.albumChosen), chosen: null,
            candidates: position === 0 ? [] : [
              { providerId: "library", resourceId: album ? "evening-album" : "mira-artist", title: album ? "Evening" : "Mira", artist: album ? "Mira" : null, year: album ? 2020 : null, trackCount: album ? 12 : null, artworkUrl: null, albums: album ? [] : ["Evening", "Daylight"] },
              { providerId: "library", resourceId: album ? "evening-live" : "mira-other", title: album ? "Evening Live" : "Mira", artist: album ? "Mira" : null, year: album ? 2022 : null, trackCount: album ? 14 : null, artworkUrl: null, albums: album ? [] : ["Another sky"] },
            ] },
        })));
      }
      const rows = id === "likes" ? [
        occurrence("likes", 0, "Golden hour", 2, 0, 1, true), occurrence("likes", 1, "Night drive", 2, 10, 2, true),
        occurrence("likes", 2, "Housewife", fixture.fixed ? 2 : 1, fixture.fixed ? 0 : 3, fixture.fixed ? 1 : null, fixture.appliedFixed),
      ] : [occurrence("mix", 0, "Golden hour", 2, 1, 1, true), occurrence("mix", 1, "Night drive", 2, 10, 2), occurrence("mix", 2, "Lost recording", 0, 2, null), occurrence("mix", 3, null, 0, 2, null)];
      return reply(route, rows.slice(Number(url.searchParams.get("offset") ?? 0), Number(url.searchParams.get("offset") ?? 0) + Number(url.searchParams.get("limit") ?? 50)));
    }
    if (path === `/api/admin/migrations/${currentRun}/apply`) { fixture.appliedFixed = fixture.fixed; fixture.state = "applying"; return route.fulfill({ status: 202, body: "" }); }
    if (path === `/api/admin/migrations/${currentRun}/resume`) { fixture.state = "collecting"; return route.fulfill({ status: 202, body: "" }); }
    if (path === `/api/admin/migrations/${currentRun}/cancel`) { fixture.state = "cancelled"; return route.fulfill({ status: 202, body: "" }); }
    if (path === "/api/admin/ui/listener-context") return reply(route, { backend: "Jellyfin", username: "Alex", musicClientUrl: "https://music.example.com", addressSource: "configured" });
    if (path === "/api/admin/track-matches") return reply(route, { matches: [{ externalSnapshotId: snapshotId, providerId: "spotify", title: "Housewife", artist: "Tate McRae", album: "So Close to What", state: "review", durationMilliseconds: 180000, providerIdentities: [], reasons: [], warnings: [], allowedAuthorityScopes: ["personal"], manualAuthorities: [], candidates: [{ title: "Housewife — library version", artist: "Tate McRae", libraryTrackId: "library-song", isLocal: true, confidence: .98, durationMilliseconds: 180000 }] }] });
    if (path === `/api/admin/track-matches/${snapshotId}/resolve`) { fixture.fixed = true; return reply(route, { success: true }); }
    if (path.startsWith("/api/admin/track-matches/targets/")) return reply(route, { tracks: [], providers: [] });
    return reply(route, {});
  });
  return fixture;
}

async function focusRefresh(page: Page) { await page.evaluate(() => window.dispatchEvent(new Event("focus"))); }
async function liveRefresh(page: Page) {
  await page.evaluate(id => (window as unknown as { __emitMigrationUpdate: (id: string) => void }).__emitMigrationUpdate(id), jobId);
}

 return mockImport(page);
}


const phase = "after";
const pages = ["playlists", "matches", "cached", "kept", "mixes", "releases", "taste", "recap", "import"];
for (const [width, theme] of [[1280, "light"], [1280, "dark"], [390, "light"]] as const) {
  for (const name of pages) test(`${name} presentation at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    if (name === "playlists") await playlistsFixture(page);
    else if (name === "matches") await matchesFixture(page);
    else if (["cached", "kept"].includes(name)) await downloadsFixture(page);
    else if (name === "import") await importsFixture(page);
    else await mockForYou(page);
    const library = ["playlists", "matches", "cached", "kept", "import"].includes(name);
    await page.goto(library ? `/#/library/${name}` : name === "mixes" ? "/#/for-you" : `/#/for-you?section=${name}`);
    const ready = name === "playlists" ? page.getByRole("button", { name: "Open Evening mix playlist details", exact: true })
      : name === "matches" ? page.getByRole("button", { name: "Open match for Song One", exact: true })
      : ["cached", "kept"].includes(name) ? page.getByRole("button", { name: "Open song details for Take On Me", exact: true })
      : name === "mixes" ? page.getByRole("region", { name: "Made for you", exact: true })
      : name === "releases" ? page.locator(".following-row")
      : name === "taste" ? page.getByRole("heading", { name: "Steer your mixes", exact: true })
      : name === "recap" ? page.getByRole("region", { name: "Summary", exact: true })
      : page.getByRole("region", { name: "Import status", exact: true });
    await expect(ready).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    if (name === "playlists") {
      await expect(page.getByRole("radio", { name: /Paused/ })).toHaveCount(0);
      await expect(page.getByRole("region", { name: "Playlist status", exact: true })).toContainText("Next sync");
      if (width === 1280) {
        const filters = await page.getByRole("radiogroup", { name: "Playlist filters", includeHidden: true }).boundingBox();
        const search = await page.getByRole("searchbox", { name: "Filter playlists", exact: true }).boundingBox();
        expect(Math.abs(filters!.y + filters!.height / 2 - search!.y - search!.height / 2)).toBeLessThan(2);
      }
    }
    if (name === "matches") {
      await expect(page.getByRole("img", { name: /^Matched by Allstarr:/ })).toHaveAttribute("aria-label", /No match: 1/);
      await expect(page.locator(".match-title").getByRole("button", { name: "Rematch all", exact: true })).toBeVisible();
      await expect(page.locator(".mapping-pagination")).toHaveCount(0);
    }
    if (["cached", "kept"].includes(name)) {
      await expect(page.locator(".downloads-table-scroll > table > tbody > .download-row")).toHaveCount(3);
      if (name === "cached") await expect(page.getByText("Playback cache on", { exact: true })).toHaveCount(0);
      if (name === "kept") await expect(page.getByRole("button", { name: /^Remove all/ })).toHaveCount(0);
      if (width === 1280) {
        expect(await page.locator(".library-download-actions").first().evaluate(element => getComputedStyle(element).display)).toBe("table-cell");
        expect(await page.locator(".song-title > strong").first().evaluate(element => getComputedStyle(element).whiteSpace)).toBe("nowrap");
      }
    }
    if (name === "mixes") {
      await expect(page.getByRole("button", { name: "Refresh page", exact: true })).toHaveCount(0);
      if (width === 1280) expect(await page.getByRole("region", { name: "Made for you", exact: true }).locator(".mix-shelf").evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
      const add = page.locator(".add-chip").first();
      if (await add.count()) expect(await add.evaluate(element => Math.abs(element.getBoundingClientRect().width - element.closest(".mix-card")!.getBoundingClientRect().width))).toBeLessThan(2);
    }
    if (name === "releases") {
      await expect(page.getByRole("link", { name: "Open Just Dropped", exact: true })).toHaveCount(0);
      await expect(page.getByRole("button", { name: "Refresh releases", exact: true })).toHaveCount(0);
      const menu = page.locator(".release-card-menu").first(), cover = page.locator(".release-art").first();
      if (await menu.count()) expect((await menu.boundingBox())!.y).toBeGreaterThanOrEqual((await cover.boundingBox())!.y + (await cover.boundingBox())!.height);
    }
    if (name === "taste") {
      await expect(page.getByRole("switch", { name: "Private listening (pause learning)", exact: true })).toBeVisible();
      await expect(page.getByRole("switch", { name: "Use imported history", exact: true })).toBeVisible();
      await expect(page.locator(".taste-details > summary")).toHaveText("See the details▾");
    }
    if (name === "recap") {
      await expect(page.getByRole("radiogroup", { name: "Stats period", exact: true }).getByRole("radio")).toHaveCount(5);
      await expect(page.getByRole("radio", { name: "Past years", exact: true })).toBeVisible();
      await expect(page.locator(".recap-imports")).not.toHaveAttribute("open");
      if (width === 1280) expect(await page.locator(".stats-lists").evaluate(element => getComputedStyle(element).gridTemplateColumns.split(" ").length)).toBe(2);
    }
    if (name === "import") {
      await expect(page.locator(".import-steps > li")).toHaveCount(4);
      await expect(page.getByRole("button", { name: "Start import", exact: true })).toBeVisible();
    }
    await mkdir(`test-results/library-review/${phase}`, { recursive: true });
    const capture = (suffix = "") => page.screenshot({ path: `test-results/library-review/${phase}/${name}${suffix}-${width}-${theme}.png`, fullPage: true });
    await capture();
    if (["playlists", "matches", "cached", "kept"].includes(name)) { await ready.click(); await expect(page.getByRole("dialog")).toBeVisible();
      if (["cached", "kept"].includes(name)) {
        await expect(page.getByRole("dialog").getByRole("region", { name: "In your playlists", exact: true })).toBeVisible();
        await expect(page.getByRole("dialog").getByText("Filename", { exact: true })).not.toBeVisible();
      }
      await capture("-panel"); }
    if (name === "playlists") {
      await page.keyboard.press("Escape"); await page.getByRole("button", { name: "Import playlist", exact: true }).click(); await expect(page.getByRole("dialog", { name: "Import a playlist", exact: true })).toBeVisible(); await expect(page.getByRole("dialog").getByRole("checkbox", { name: "Late drive", exact: true })).toBeVisible(); await capture("-choose");
    }
    if (name === "mixes") {
      await page.getByRole("button", { name: "Start a mix", exact: true }).click(); await expect(page.getByRole("dialog", { name: "Start a mix", exact: true })).toBeVisible(); await capture("-start");
      await page.getByRole("searchbox", { name: "Start a mix from a song or artist", exact: true }).fill("gold");
      await page.getByRole("button", { name: "Golden hour by Mira", exact: true }).click();
      await expect(page.getByRole("switch", { name: "Add to my apps", exact: true })).toBeVisible();
      const nameField = page.getByLabel("Mix name", { exact: true }), appsSwitch = page.getByRole("switch", { name: "Add to my apps", exact: true });
      await expect(nameField).toBeVisible();
      if (width === 390) {
        await nameField.focus(); await page.keyboard.press("Tab");
        await expect(appsSwitch).toBeFocused(); await expect(appsSwitch).toBeInViewport({ ratio: 1 });
      } else await nameField.scrollIntoViewIfNeeded();
      await capture("-preview");
    }
  });
}
