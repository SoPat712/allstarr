import { expect, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { expectContainedMobileGeometry } from "./mobile-geometry";

export const brainzId = "11111111-1111-4111-8111-111111111111";
export const lastfmId = "22222222-2222-4222-8222-222222222222";
export const playlistId = "33333333-3333-4333-8333-333333333333";
export const sourcePlaylist = { id: playlistId, providerId: "listenbrainz", name: "Weekly Jams", owner: "Listener", description: "Creator: ListenBrainz\nCreated: 2026-10-26T12:00:00Z\nAlgorithm: weekly-jams\nMade from your listening.", trackCount: null, sourceRevision: "snapshot", artworkUrl: null };
export type Fixtures = { disconnected?: boolean; notReady?: boolean; keyRequired?: boolean; stats?: unknown; inbox?: unknown; artists?: unknown };

export async function mockIntelligence(page: Page, options: Fixtures = {}) {
  const calls: { method: string; path: string; query: string; body: Record<string, unknown> | null }[] = [];
  const saved: Record<string, unknown>[] = [];
  await page.route("**/*", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (!["127.0.0.1", "localhost"].includes(url.hostname)) return route.abort();
    if (path.startsWith("/images/") || path.startsWith("/fonts/")) return route.abort();
    if (!path.startsWith("/api/admin/")) return route.continue();
    const body = request.postData() ? request.postDataJSON() : null;
    calls.push({ method: request.method(), path, query: url.search, body });
    const reply = (data: unknown, status = 200) => route.fulfill({ status, json: data });
    if (path === "/api/admin/auth/me") return reply({ authenticated: true, backend: "Jellyfin", features: { intelligence: false }, user: { id: "listener", name: "Listener", isAdministrator: false } });
    if (path === "/api/admin/auth/oidc/status") return reply({ enabled: false });
    if (path === "/api/admin/ui/schema") return reply({ providers: [{ id: "listenbrainz", name: "ListenBrainz" }, { id: "lastfm", name: "Last.fm" }], configSections: [] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    if (path === "/api/admin/playlist-sources") return reply({ accounts: options.disconnected ? [] : [
      { id: brainzId, providerId: "listenbrainz", displayName: "ListenBrainz", scope: "personal", accessLabel: "Personal account" },
      { id: lastfmId, providerId: "lastfm", displayName: "Last.fm", scope: "personal", accessLabel: "Personal account" },
    ], blockedAccounts: [], providers: [{ id: "listenbrainz", displayName: "ListenBrainz", supportsPublicRead: true }, { id: "lastfm", displayName: "Last.fm", supportsPublicRead: false }] });
    if (path === `/api/admin/playlist-sources/${brainzId}/playlists`) return reply({ items: options.notReady ? [] : [sourcePlaylist], nextCursor: null, isPartial: false });
    if (path === `/api/admin/playlist-sources/${lastfmId}/playlists`) return options.keyRequired
      ? reply({ error: "Needs Last.fm API key.", reasonCode: "lastfm-api-key-required", providerId: "lastfm", accountId: lastfmId }, 409)
      : reply({ items: [], nextCursor: null, isPartial: false });
    if (path === "/api/admin/media-targets") return reply({ targets: [{ id: "target", protocol: "jellyfin", backendInstanceId: "home", displayName: "Jellyfin" }] });
    if (path === "/api/admin/playlist-links" && request.method() === "POST") {
      saved.push({ ...body, sourcePlaylistId: body.followNewest ? "createdfor:weekly-jams" : body.sourcePlaylistId, id: "saved-playlist", name: "Weekly Jams", enabled: true, trackCount: 0, metrics: { total: 0, review: 0 }, routeCoverage: [] });
      return reply({ id: "saved-playlist" }, 201);
    }
    if (path === "/api/admin/playlist-links") return reply({ playlistLinks: saved });
    if (path === "/api/admin/stats") return reply(options.stats ?? {});
    if (path === "/api/admin/history/imports") return reply([]);
    if (path === "/api/admin/releases/artists") return reply(options.artists ?? []);
    if (path === "/api/admin/releases") return reply(options.inbox ?? { entries: [], excluded: [], coverage: [] });
    return reply({});
  });
  return calls;
}

export async function capture(page: Page, name: string, width: number, theme: "light" | "dark") {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  if (width === 390) await expectContainedMobileGeometry(page);
  await page.evaluate(() => { window.scrollTo({ top: 0, behavior: "instant" }); (document.activeElement as HTMLElement)?.blur(); });
  const folder = process.env.ALLSTARR_SCREENSHOT_DIR ?? "test-results/shots-uiintel";
  await mkdir(folder, { recursive: true });
  const dialog = await page.getByRole("dialog").count() + await page.getByRole("alertdialog").count();
  await page.screenshot({ path: `${folder}/${name}-${width}-${theme}.png`, fullPage: !dialog });
}

export async function appearance(page: Page, width: number, theme: "light" | "dark") {
  await page.setViewportSize({ width, height: 900 });
  await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
  await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
}
