import { expect, type Page } from "@playwright/test";
export { appearance, capture } from "./intelligence-fixtures";
import type { MixCard, MixSong, TasteProfile } from "../src/lib/api";

export const songs: MixSong[] = Array.from({ length: 8 }, (_, i) => ({ mixId: "daily", generation: 1, source: "library", reason: i ? "Calm sound" : "Played 6 times in 30 days", score: 1, song: { id: `song-${i}`, canonicalRecordingId: null, title: ["Blue morning", "Quiet water", "Afterglow"][i % 3] + (i > 2 ? ` ${i + 1}` : ""), artist: ["North", "Cedar", "Willow"][i % 3], album: "Open skies" } }));
const card = (id: string, name: string, section: MixCard["section"] = "madeForYou"): MixCard => ({ id, kind: id, name, section, sortOrder: 0, description: section === "moods" ? "Calm songs from your library" : "R&B · North, Cedar, Willow", sourceLabel: null, recipeVersion: null, updatedAt: "2026-10-29T04:00:00Z", readiness: { ready: true, message: "Ready" }, showInApps: true, inApps: true, appsAllowed: true, coverArtwork: songs.slice(0, 4).map(row => row.song.id) });
export async function mockForYou(page: Page, options: { admin?: boolean; brandNew?: boolean; publish?: boolean; partial?: boolean; custom?: boolean } = {}) {
  const state = { cards: [1, 2, 3, 4].map(n => card(`daily-${n}`, `Daily Mix ${n}`)).concat([card("discover-weekly", "Discover Weekly"), card("release-radar", "Release Radar"), card("on-repeat", "On Repeat"), card("rewind", "Rewind"), ...["Chill", "Focus", "Workout", "Party", "Sleep"].map(name => card(name.toLowerCase(), name, "moods"))]), songs: structuredClone(songs), publish: options.publish ?? true, calls: [] as { path: string; method: string; body: any }[], taste: { lately: { tags: [{ name: "R&B", strength: 80, value: 1 }, { name: "pop", strength: 70, value: 1 }], artists: [{ name: "North", strength: 80, value: 1 }] }, usually: { tags: [{ name: "indie rock", strength: 90, value: 1 }, { name: "EDM", strength: 70, value: 1 }, { name: "hip-hop", strength: 60, value: 1 }], artists: [] }, latelyWeight: .7, nudges: [], paused: false, useImportedHistory: true } as TasteProfile };
  const linked: { sourceProviderId: string; sourcePlaylistId: string }[] = [];
  if (options.custom) state.cards.push(card("custom", "Evening listening", "custom"));
  if (options.brandNew || options.partial) for (const item of state.cards.filter(item => options.brandNew || item.id === "daily-4")) { item.readiness = { ready: false, message: "Play about 80 more songs, or import your listening history." }; item.coverArtwork = []; item.inApps = false; }
  await page.route("**/*", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname, method = request.method();
    if (!["127.0.0.1", "localhost"].includes(url.hostname)) return route.abort();
    if (path.startsWith("/images/") || path.startsWith("/fonts/")) return route.abort();
    if (!path.startsWith("/api/admin/")) return route.continue();
    const body = request.postData() ? request.postDataJSON() : null;
    state.calls.push({ path, method, body });
    const reply = (json: unknown, status = 200) => route.fulfill({ json, status });
    if (path.includes("/artwork/")) return route.fulfill({ contentType: "image/svg+xml", body: '<svg xmlns="http://www.w3.org/2000/svg" width="184" height="184"><rect width="184" height="184" fill="currentColor"/><text x="70" y="105" fill="white" font-size="48">♪</text></svg>' });
    if (path === "/api/admin/auth/me") return reply({ authenticated: true, backend: "Jellyfin", features: { intelligence: false }, user: { id: "listener", allstarrUserId: "listener", name: "Josh Patra", isAdministrator: !!options.admin } });
    if (path === "/api/admin/auth/oidc/status") return reply({ enabled: false });
    if (path === "/api/admin/onboarding/status") return reply({ completed: true, shouldRedirectToSetup: false, setupOpen: false, completedSteps: ["backend-identity"], revision: 1, recoveryNotices: [] });
    if (path === "/api/admin/ui/schema") return reply({ providers: [], configSections: [] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    if (path === "/api/admin/playlist-sources") return reply({ accounts: options.brandNew ? [] : [{ id: "account", providerId: "lastfm", displayName: "Last.fm", scope: "personal" }], blockedAccounts: [], providers: [] });
    if (path === "/api/admin/mixes/publish-setting") { if (body) state.publish = body.enabled; return reply({ enabled: state.publish }); }
    if (path === "/api/admin/mixes/start") return reply({ previewId: "preview", songs, readiness: { ready: true, message: "Ready" } });
    if (path === "/api/admin/mixes/start/preview/save") { const saved = card("custom", body.name, "custom"); state.cards.push(saved); return reply({ ...saved, songs, generation: 1 }); }
    if (path === "/api/admin/mixes/external") { const saved = { ...card("external", "Loved on Last.fm", "yourServices"), sourceLabel: "Last.fm" }; state.cards.push(saved); linked.push({ sourceProviderId: body.source, sourcePlaylistId: body.source === "listenbrainz" ? `createdfor:${body.kind}` : body.kind === "top" ? "top:1month:50" : body.kind }); return reply(saved, 201); }
    if (path === "/api/admin/mixes") return reply(state.cards.map(item => ({ ...item, appsAllowed: state.publish, inApps: state.publish && item.showInApps && item.readiness.ready })));
    if (path.endsWith("/feedback")) {
      const song = state.songs.find(row => row.song.id === body.songId)!.song;
      state.songs = state.songs.filter(row => body.kind === "neverArtist" ? row.song.artist !== song.artist : row.song.id !== body.songId);
      const nudge = { kind: body.kind === "neverArtist" ? "artist" as const : body.kind === "moreLikeThis" ? "tag" as const : "song" as const, name: body.kind === "neverArtist" ? song.artist : body.kind === "moreLikeThis" ? "R&B" : song.canonicalRecordingId ?? song.id, mode: body.kind === "neverArtist" ? "never" as const : body.kind === "moreLikeThis" ? "more" as const : "less" as const, expiresAt: body.todayOnly ? "2099-01-01T00:00:00Z" : null };
      state.taste.nudges = [...state.taste.nudges.filter(item => item.kind !== nudge.kind || item.name !== nudge.name), nudge];
      return reply({ removed: body.songId });
    }
    if (path.endsWith("/in-apps")) { state.cards.find(item => path.includes(`/${item.id}/`))!.showInApps = body.enabled; return reply({ enabled: body.enabled }); }
    if (path.endsWith("/refresh")) return reply({ error: "Wait 15 minutes before refreshing this mix again." }, 429);
    if (path.startsWith("/api/admin/mixes/")) { if (method === "DELETE") { state.cards = state.cards.filter(item => item.id !== path.split("/").at(-1)); return reply({}); } return reply({ mix: { ...state.cards.find(item => item.id === path.split("/").at(-1)), appsAllowed: state.publish }, songs: state.songs }); }
    if (path.includes("/track-matches/targets/")) return reply({ tracks: [{ id: "local", backendItemId: "seed", title: "Golden hour", artist: "Mira", album: "Warm light", artworkUrl: null }], providers: [] });
    if (path.startsWith("/api/admin/taste")) {
      if (path.endsWith("/nudge")) { if (method === "DELETE") state.taste.nudges = state.taste.nudges.filter(item => item.kind !== url.searchParams.get("kind") || item.name !== url.searchParams.get("name")); else { state.taste.nudges = state.taste.nudges.filter(item => item.kind !== body.kind || item.name !== body.name); state.taste.nudges.push({ ...body, expiresAt: body.todayOnly ? "2099-01-01T00:00:00Z" : null }); } }
      if (path.endsWith("/reset")) { state.taste.lately.tags = []; state.taste.usually.tags = []; if (body.clearNudges) state.taste.nudges = []; }
      if (path.endsWith("/pause") || path.endsWith("/resume")) state.taste.paused = path.endsWith("/pause");
      if (path.endsWith("/imported-history")) state.taste.useImportedHistory = body.enabled;
      return reply(state.taste);
    }
    if (path === "/api/admin/audiomuse/status") return reply({ reason: options.brandNew ? "not_enabled" : null, accessibleLibrarySongs: 1400, analyzedSongs: options.brandNew ? 0 : 1240, coverageComplete: true });
    if (path === "/api/admin/audiomuse/connection") return reply({ baseUrl: body?.baseUrl ?? "http://sounds.invalid:8600", keyConfigured: true, revision: 1 });
    if (path === "/api/admin/releases/artists") return reply([{ providerId: "catalog", artistId: "north", displayName: "North" }]);
    if (path === "/api/admin/releases") return reply({ entries: options.brandNew ? [] : [{ id: "release", title: "Open skies", artist: "North", releaseDate: "2026-10-29", type: "album", sources: [], isExplicit: false, edition: "" }], excluded: [], coverage: [] });
    if (path === "/api/admin/playlist-links") return reply({ playlistLinks: linked });
    if (path === "/api/admin/releases/artists/from-hearts") return reply({ followed: 1 });
    if (path === "/api/admin/history/imports") return reply([]);
    if (path === "/api/admin/stats") return reply({ period: { from: null, to: "2026-10-29", timeZoneId: "America/New_York" }, playCount: 1200, activeDays: 28, importedCount: 300, observedSince: "2024-01-01", estimatedTimeFromSongLengthsMilliseconds: 18000000, coverage: { unknownSongIdentityCount: 0, unknownDurationCount: 0 }, topSongs: [{ title: "Blue morning", artist: "North", album: "Open skies", playCount: 25 }], topArtists: [{ artist: "North", playCount: 120 }], topAlbums: [] });
    return reply({});
  });
  return state;
}
export async function openHome(page: Page) { await page.goto("/#/for-you"); await expect(page.getByRole("heading", { name: "Made for you", exact: true })).toBeVisible(); }
