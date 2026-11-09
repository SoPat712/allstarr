import { expect, test, type Page } from "@playwright/test";

const listener = "11111111-1111-4111-8111-111111111111";
const base = {
  enabled: true, inApps: true, canRemoveFromApps: true, revision: 4,
  sourceProviderId: "spotify", providerAccountId: "spotify-account",
  targetProtocol: "jellyfin", targetBackendInstanceId: "home",
  mode: "hybrid", importMode: "linked", projectionMode: "resolved", materializationMode: "reconcile",
  trackRetention: "onDemand", mirrorStaleEntries: true, preserveManualEntries: true,
  syncName: true, syncDescription: true, syncArtwork: true,
  trackCount: 30, matchedCount: 30, unmatchedCount: 0, playableCount: 30, materializedCount: 30,
  routeCoverage: [{ providerId: "spotify", count: 30 }],
  metrics: { total: 30, matched: 30, unresolved: 0, review: 0, rejected: 0, playable: 30, materialized: 30 },
};
const playlists = [
  { ...base, id: "22222222-2222-4222-8222-222222222201", name: "Road trip", sourcePlaylistId: "road", targetPlaylistId: "road-copy", playlistKind: "yours", countsTowardLibrary: true },
  { ...base, id: "22222222-2222-4222-8222-222222222202", name: "Discover Weekly", sourcePlaylistId: "discover", targetPlaylistId: "discover-copy", playlistKind: "feed", countsTowardLibrary: false },
  { ...base, id: "22222222-2222-4222-8222-222222222203", name: "Today's Top Hits", sourcePlaylistId: "hits", targetPlaylistId: "hits-copy", playlistKind: "feed", countsTowardLibrary: false },
  { ...base, id: "22222222-2222-4222-8222-222222222204", name: "Sam's summer", sourcePlaylistId: "sam", targetPlaylistId: "sam-copy", playlistKind: "followed", countsTowardLibrary: false },
];

async function fixture(page: Page) {
  const items = playlists.map(item => ({ ...item }));
  const writes: Array<{ path: string; body: Record<string, unknown> }> = [];
  await page.route("**/*", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (!["localhost", "127.0.0.1"].includes(url.hostname)) return route.abort();
    if (!path.startsWith("/api/")) return route.continue();
    const reply = (body: unknown) => route.fulfill({ json: body });
    if (path === "/api/admin/auth/me") return reply({ authenticated: true, backend: "Jellyfin", user: { id: "listener", allstarrUserId: listener, name: "Listener", isAdministrator: false } });
    if (path === "/api/admin/auth/oidc/status") return reply({ enabled: false });
    if (path === "/api/admin/auth/playlist-consent") return reply({ supported: true, granted: true, needsSignIn: false });
    if (path === "/api/admin/ui/schema") return reply({ providers: [{ id: "spotify", name: "Spotify" }], settings: [], priorityGroups: [], configSections: [] });
    if (path === "/api/admin/playlist-links") return reply({ playlistLinks: items });
    const library = items.find(item => path === `/api/admin/playlist-links/${item.id}/library`);
    if (library) {
      const body = request.postDataJSON();
      writes.push({ path, body });
      expect(request.method()).toBe("PATCH");
      expect(body.expectedRevision).toBe(library.revision);
      library.revision++;
      library.countsTowardLibrary = body.countsTowardLibrary;
      return reply({ id: library.id, revision: library.revision, playlistKind: library.playlistKind, countsTowardLibrary: library.countsTowardLibrary });
    }
    const link = items.find(item => path === `/api/admin/playlist-links/${item.id}`);
    if (link) return reply({ ...link, tracks: [], unresolvedCount: 0, reviewCount: 0, unknownDurationCount: 0, durationMs: 5400000 });
    if (path === "/api/admin/mixes" || path.endsWith("/playlist-forks")) return reply([]);
    if (path === "/api/admin/activity/tasks") return reply({ items: [] });
    if (path === "/api/admin/jobs") return reply({ jobs: [], progress: [] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return reply({});
  });
  return { writes };
}

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

for (const [width, scheme] of [[1280, "dark"], [390, "light"]] as const) {
  test(`Feeds and followed playlists are grouped and can join the library (${width} ${scheme})`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.addInitScript(theme => {
      localStorage.setItem("allstarr.theme", theme);
      localStorage.setItem("allstarr.library.playlists.filter", "all");
    }, scheme);
    const { writes } = await fixture(page);
    await page.goto("/#/library/playlists");
    const groups = page.locator(".playlist-group");
    await expect(groups).toHaveCount(3);
    await expect(groups.nth(0)).toHaveText("Your playlists");
    await expect(groups.nth(1)).toContainText("Feeds");
    await expect(groups.nth(1)).toContainText("Made for you by a service");
    await expect(groups.nth(2)).toContainText("Followed");
    expect(await page.locator(".playlist-row h3").allInnerTexts()).toEqual(["Road trip", "Discover Weekly", "Today's Top Hits", "Sam's summer"]);
    expect((await page.evaluate(() => document.documentElement.scrollWidth)) <= width).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`playlist-kinds-${width}-${scheme}.png`), fullPage: true });

    await page.getByRole("button", { name: "Open Discover Weekly playlist details" }).click();
    const toggle = page.getByRole("switch", { name: "Show songs in my library" });
    await expect(toggle).not.toBeChecked();
    await expect(page.getByText("Made for you by Spotify; it stays a playlist only")).toBeVisible();
    await toggle.click();
    await expect(toggle).toBeChecked();
    await expect(page.getByText("Made for you by Spotify; its songs appear in Albums, Songs and Artists")).toBeVisible();
    expect(writes).toEqual([{ path: "/api/admin/playlist-links/22222222-2222-4222-8222-222222222202/library", body: { expectedRevision: 4, countsTowardLibrary: true } }]);
    await page.screenshot({ path: testInfo.outputPath(`playlist-kinds-panel-${width}-${scheme}.png`), fullPage: true });
    await page.keyboard.press("Escape");
    await expect(page.locator(".playlist-row", { hasText: "Discover Weekly" })).toContainText("Songs in your library");
    await expect(page.locator(".playlist-row", { hasText: "Today's Top Hits" })).not.toContainText("in your library");
  });
}
