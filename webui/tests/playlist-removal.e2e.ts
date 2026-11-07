import { expect, test, type Page } from "@playwright/test";

const listener = "11111111-1111-4111-8111-111111111111";
const id = "22222222-2222-4222-8222-222222222222";
const playlist = {
  id, name: "Evening mix", enabled: true, inApps: true, canRemoveFromApps: true, revision: 4,
  sourceProviderId: "spotify", sourcePlaylistId: "evening", providerAccountId: "spotify-account",
  targetProtocol: "jellyfin", targetBackendInstanceId: "home", targetPlaylistId: "evening-copy",
  mode: "hybrid", importMode: "linked", projectionMode: "resolved", materializationMode: "reconcile",
  trackRetention: "onDemand", mirrorStaleEntries: true, preserveManualEntries: true,
  syncName: true, syncDescription: true, syncArtwork: true,
  trackCount: 3, matchedCount: 3, unmatchedCount: 0, playableCount: 3, materializedCount: 3,
  routeCoverage: [{ providerId: "local", count: 3 }],
  metrics: { total: 3, matched: 3, unresolved: 0, review: 0, rejected: 0, playable: 3, materialized: 3 },
};
type FixturePlaylist = typeof playlist;
type Write = { path: string; method: string; body: Record<string, unknown> };
type UserAddedSong = { backendItemId: string; name: string; artist: string | null };

async function fixture(
  page: Page,
  items: FixturePlaylist[] = [{ ...playlist }],
  options: { userAddedSongs?: UserAddedSong[]; skippedItemCount?: number } = {},
) {
  const writes: Write[] = [];
  let listReads = 0, previewReads = 0;
  await page.route("**/*", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (!["localhost", "127.0.0.1"].includes(url.hostname)) return route.abort();
    if (!path.startsWith("/api/")) return route.continue();
    const reply = (body: unknown) => route.fulfill({ json: body });
    if (request.method() !== "GET") writes.push({ path, method: request.method(), body: request.postData() ? request.postDataJSON() : {} });
    if (path === "/api/admin/auth/me") return reply({ authenticated: true, backend: "Jellyfin", user: { id: "listener", allstarrUserId: listener, name: "Listener", isAdministrator: false } });
    if (path === "/api/admin/auth/oidc/status") return reply({ enabled: false });
    if (path === "/api/admin/auth/playlist-consent") return reply({ supported: true, granted: true, needsSignIn: false });
    if (path === "/api/admin/ui/schema") return reply({ providers: [{ id: "spotify", name: "Spotify" }, { id: "lastfm", name: "Last.fm" }], settings: [], priorityGroups: [], configSections: [] });
    if (path === "/api/admin/playlist-links") { listReads++; return reply({ playlistLinks: items }); }
    if (items.some(item => path === `/api/admin/playlist-links/${item.id}/in-apps/removal-preview`)) {
      expect(request.method()).toBe("GET");
      previewReads++;
      return reply({ userAddedSongs: options.userAddedSongs ?? [] });
    }
    const link = items.find(item => path === `/api/admin/playlist-links/${item.id}` || path === `/api/admin/playlist-links/${item.id}/in-apps`);
    if (link && path.endsWith("/in-apps")) {
      expect(request.postDataJSON()).toEqual({ expectedRevision: link.revision });
      link.inApps = request.method() === "POST";
      link.revision++;
      return reply(link.inApps ? { ...link, skippedItemCount: options.skippedItemCount ?? 0 } : { inApps: false, revision: link.revision });
    }
    if (link) return reply({ ...link, tracks: [], unresolvedCount: 0, reviewCount: 0, unknownDurationCount: 0, durationMs: 540000 });
    if (path.startsWith("/api/admin/mixes/") && path.endsWith("/in-apps")) return reply({ inApps: false });
    if (path === "/api/admin/mixes" || path.endsWith("/playlist-forks")) return reply([]);
    if (path === "/api/admin/activity/tasks") return reply({ items: [] });
    if (path === "/api/admin/jobs") return reply({ jobs: [], progress: [] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return reply({});
  });
  return { writes, items, listReads: () => listReads, previewReads: () => previewReads };
}

test.beforeEach(async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  await page.addInitScript(() => {
    localStorage.setItem("allstarr.theme", "dark");
    localStorage.setItem("allstarr.library.playlists.filter", "all");
  });
});
test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

test("Linked playlist removal confirms from its row and panel, then supports Undo", async ({ page }, testInfo) => {
  const { writes, items, listReads } = await fixture(page, [{ ...playlist }, { ...playlist, id: "paused", name: "Paused playlist", enabled: false }]);
  await page.goto("/#/library/playlists");
  const rows = page.locator(".playlist-row");
  await expect(rows).toHaveCount(2);
  await page.getByRole("button", { name: "More options for Evening mix", exact: true }).click();
  const rowRemoval = page.getByRole("menuitem", { name: "Remove from my apps", exact: true });
  await expect(rowRemoval).toBeVisible();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("linked-playlist-menu-1280-dark.png"), fullPage: true });
  await rowRemoval.click();
  const confirm = page.getByRole("alertdialog");
  await expect(confirm).toContainText("Remove “Evening mix” from your apps?");
  await expect(confirm).toContainText("Jellyfin and your music apps stop showing it. Spotify and your saved matches stay; you can add it back.");
  await expect(confirm.getByText(/songs you added in Jellyfin/)).toHaveCount(0);
  await confirm.getByRole("button", { name: "Cancel", exact: true }).click();
  expect(writes).toEqual([]);
  await expect(rows).toHaveCount(2);

  await page.getByRole("button", { name: "Open Evening mix playlist details", exact: true }).click();
  const panel = page.getByRole("dialog", { name: "Evening mix", exact: true });
  const footerRemoval = panel.locator(".playlist-panel-footer").getByRole("button", { name: "Remove from my apps", exact: true });
  await expect(footerRemoval).toBeVisible();
  await expect(footerRemoval).toBeEnabled();
  await expect(footerRemoval).toHaveCSS("opacity", "1");
  await expect(footerRemoval).toHaveAttribute("data-variant", "destructive-text");
  await page.screenshot({ path: testInfo.outputPath("linked-playlist-footer-1280-dark.png"), fullPage: true });
  await footerRemoval.click();
  await expect(confirm.getByRole("button", { name: "Remove from my apps", exact: true })).toBeEnabled();
  await page.screenshot({ path: testInfo.outputPath("linked-playlist-confirm-1280-dark.png"), fullPage: true });
  await confirm.getByRole("button", { name: "Remove from my apps", exact: true }).click();
  await expect(confirm).toHaveCount(0);
  await expect(panel).toHaveCount(0);
  await expect(rows).toHaveCount(1);
  await expect(rows).toContainText("Paused playlist");
  await expect(page.getByText("Evening mix removed from your apps", { exact: true })).toBeVisible();
  expect(writes).toEqual([{ path: `/api/admin/playlist-links/${id}/in-apps`, method: "DELETE", body: { expectedRevision: 4 } }]);
  expect(items[0].inApps).toBe(false);
  const readsBeforeUndo = listReads();
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect(rows).toHaveCount(2);
  await expect.poll(listReads).toBeGreaterThan(readsBeforeUndo);
  expect(writes.at(-1)).toEqual({ path: `/api/admin/playlist-links/${id}/in-apps`, method: "POST", body: { expectedRevision: 5 } });
  expect(items[0].inApps).toBe(true);
  await expect(page.getByText("Evening mix added back to your apps", { exact: true })).toBeVisible();
});

for (const width of [1280, 390]) {
  test(`Linked removal previews Jellyfin songs at ${width}px in dark mode`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    const { writes, previewReads } = await fixture(page, [{ ...playlist }], { userAddedSongs: [
      { backendItemId: "first-local", name: "First local song", artist: "Local artist" },
      { backendItemId: "second-local", name: "Second local song", artist: null },
    ] });
    await page.goto("/#/library/playlists");
    await page.getByRole("button", { name: "More options for Evening mix", exact: true }).click();
    await page.getByRole("menuitem", { name: "Remove from my apps", exact: true }).click();
    const confirm = page.getByRole("alertdialog");
    await expect(confirm).toContainText("Remove “Evening mix” from your apps?");
    await expect(confirm).toContainText("Jellyfin and your music apps stop showing it. Spotify and your saved matches stay; you can add it back.");
    await expect(confirm).toContainText("It also has 2 songs you added in Jellyfin; Undo brings them back.");
    const songs = confirm.getByRole("list", { name: "Songs you added in Jellyfin" });
    await expect(songs).toBeHidden();
    await confirm.locator("summary").filter({ hasText: "See songs" }).click();
    await expect(songs).toBeVisible();
    await expect(songs.getByRole("listitem")).toHaveCount(2);
    await expect(songs).toContainText("First local song");
    await expect(songs).toContainText("Local artist");
    await expect(songs).toContainText("Second local song");
    expect(previewReads()).toBe(1);
    expect(writes).toEqual([]);
    const bounds = await confirm.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.x).toBeGreaterThanOrEqual(0);
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width);
    const extraSongs = await confirm.locator(".removal-songs").boundingBox();
    expect(extraSongs!.x).toBeGreaterThanOrEqual(bounds!.x + 16);
    expect(extraSongs!.x + extraSongs!.width).toBeLessThanOrEqual(bounds!.x + bounds!.width - 16);
    await page.screenshot({ path: testInfo.outputPath(`linked-playlist-songs-${width}-dark.png`), fullPage: true });
    await confirm.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(confirm).toHaveCount(0);
    expect(writes).toEqual([]);
  });
}

test("A failed removal preview blocks removal and offers retry", async ({ page }) => {
  const { writes } = await fixture(page);
  let failing = true;
  await page.route(`**/playlist-links/${id}/in-apps/removal-preview`, route => failing
    ? route.fulfill({ status: 503, json: { error: "Songs added in Jellyfin could not load. Try again." } })
    : route.fallback());
  await page.goto("/#/library/playlists");
  await page.getByRole("button", { name: "More options for Evening mix", exact: true }).click();
  await page.getByRole("menuitem", { name: "Remove from my apps", exact: true }).click();
  const confirm = page.getByRole("alertdialog");
  await expect(confirm.getByRole("alert")).toHaveText("Songs added in Jellyfin could not load. Try again.");
  const remove = confirm.getByRole("button", { name: "Remove from my apps", exact: true });
  await expect(remove).toBeDisabled();
  expect(writes).toEqual([]);
  failing = false;
  await confirm.getByRole("button", { name: "Try again", exact: true }).click();
  await expect(confirm.getByRole("alert")).toHaveCount(0);
  await expect(remove).toBeEnabled();
  await remove.click();
  await expect(page.locator(".playlist-row")).toHaveCount(0);
  expect(writes).toHaveLength(1);
});

test("A loading removal preview keeps removal disabled", async ({ page }) => {
  const { writes } = await fixture(page);
  let releasePreview!: () => void;
  const previewReady = new Promise<void>(resolve => { releasePreview = resolve; });
  await page.route(`**/playlist-links/${id}/in-apps/removal-preview`, async route => {
    await previewReady;
    await route.fulfill({ json: { userAddedSongs: [] } });
  });
  await page.goto("/#/library/playlists");
  await page.getByRole("button", { name: "More options for Evening mix", exact: true }).click();
  await page.getByRole("menuitem", { name: "Remove from my apps", exact: true }).click();
  const confirm = page.getByRole("alertdialog");
  await expect(confirm.getByRole("status")).toHaveText("Checking songs added in Jellyfin…");
  await expect(confirm.getByRole("button", { name: "Remove from my apps", exact: true })).toBeDisabled();
  expect(writes).toEqual([]);
  releasePreview();
  await expect(confirm.getByRole("status")).toHaveCount(0);
  await expect(confirm.getByRole("button", { name: "Remove from my apps", exact: true })).toBeEnabled();
  await confirm.getByRole("button", { name: "Cancel", exact: true }).click();
  expect(writes).toEqual([]);
});

test("Undo reports songs that could not be restored", async ({ page }) => {
  const { writes } = await fixture(page, [{ ...playlist }], { skippedItemCount: 2 });
  await page.goto("/#/library/playlists");
  await page.getByRole("button", { name: "More options for Evening mix", exact: true }).click();
  await page.getByRole("menuitem", { name: "Remove from my apps", exact: true }).click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Remove from my apps", exact: true }).click();
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect(page.locator(".playlist-row")).toHaveCount(1);
  await expect(page.getByText("Evening mix added back to your apps. 2 songs could not be restored.", { exact: true })).toBeVisible();
  expect(writes.at(-1)?.method).toBe("POST");
});

test("A linked playlist can be removed directly from the row menu", async ({ page }) => {
  const { writes } = await fixture(page);
  await page.goto("/#/library/playlists");
  await page.getByRole("button", { name: "More options for Evening mix", exact: true }).click();
  await page.getByRole("menuitem", { name: "Remove from my apps", exact: true }).click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Remove from my apps", exact: true }).click();
  await expect(page.locator(".playlist-row")).toHaveCount(0);
  expect(writes[0]).toMatchObject({ path: `/api/admin/playlist-links/${id}/in-apps`, method: "DELETE" });
});

test("A removal error keeps the playlist and displays the server message", async ({ page }) => {
  const { items, writes } = await fixture(page);
  let failing = true;
  await page.route(`**/playlist-links/${id}/in-apps`, route => failing
    ? route.fulfill({ status: 409, json: { error: "This playlist changed. Refresh and try again." } })
    : route.fallback());
  await page.goto("/#/library/playlists");
  await page.getByRole("button", { name: "More options for Evening mix", exact: true }).click();
  await page.getByRole("menuitem", { name: "Remove from my apps", exact: true }).click();
  const confirm = page.getByRole("alertdialog");
  await confirm.getByRole("button", { name: "Remove from my apps", exact: true }).click();
  await expect(confirm.getByRole("alert")).toHaveText("This playlist changed. Refresh and try again.");
  await expect(page.locator(".playlist-row")).toHaveCount(1);
  expect(items[0].inApps).toBe(true);
  expect(writes).toEqual([]);
  failing = false;
  await confirm.getByRole("button", { name: "Remove from my apps", exact: true }).click();
  await expect(confirm).toHaveCount(0);
  await expect(page.locator(".playlist-row")).toHaveCount(0);
});

test("Removal follows the server permission while paused playlists stay listed", async ({ page }) => {
  await fixture(page, [
    { ...playlist, canRemoveFromApps: false },
    { ...playlist, id: "paused", name: "Paused playlist", enabled: false },
    { ...playlist, id: "hidden", name: "Removed playlist", inApps: false },
    { ...playlist, id: "one-time", name: "Imported once", importMode: "oneTime", canRemoveFromApps: false },
  ]);
  await page.goto("/#/library/playlists");
  await expect(page.locator(".playlist-row")).toHaveCount(3);
  await expect(page.getByRole("button", { name: "More options for Evening mix", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "More options for Imported once", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Open Evening mix playlist details", exact: true }).click();
  await expect(page.getByRole("dialog").getByRole("button", { name: "Remove from my apps", exact: true })).toHaveCount(0);
  await page.getByRole("dialog").getByRole("button", { name: "Close", exact: true }).click();
  await page.getByRole("radio", { name: /Paused/ }).click();
  await expect(page.locator(".playlist-row")).toHaveCount(1);
  await expect(page.locator(".playlist-row")).toContainText("Paused playlist");
});

test("Service mixes retain their existing removal endpoint", async ({ page }) => {
  const { writes, previewReads } = await fixture(page, [{ ...playlist, name: "Loved mix", sourceProviderId: "lastfm", sourcePlaylistId: "loved", canRemoveFromApps: false }]);
  await page.goto("/#/library/playlists");
  await page.getByRole("button", { name: "Open Loved mix playlist details", exact: true }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Remove from my apps", exact: true }).click();
  const confirm = page.getByRole("alertdialog");
  await expect(confirm).toContainText("Remove Loved mix from your apps?");
  await expect(confirm).toContainText("Loved mix leaves your apps for you. Its source is unchanged. Saved matches stay, so adding it back is quick.");
  await confirm.getByRole("button", { name: "Remove from my apps", exact: true }).click();
  await expect(page.locator(".playlist-row")).toHaveCount(0);
  expect(previewReads()).toBe(0);
  expect(writes).toEqual([{ path: `/api/admin/mixes/${id}/in-apps`, method: "DELETE", body: {} }]);
});

test("Adding a removed source keeps its settings and reports missing songs", async ({ page }) => {
  const { writes, items } = await fixture(page, [{ ...playlist, inApps: false, enabled: false }]);
  await page.route("**/api/admin/playlist-sources", route => route.fulfill({ json: {
    accounts: [{ id: "spotify-account", providerId: "spotify", displayName: "My Spotify" }],
    blockedAccounts: [], providers: [{ id: "spotify", displayName: "Spotify" }],
  } }));
  await page.route("**/playlist-sources/spotify-account/playlists**", route => route.fulfill({ json: {
    items: [{ id: "evening", providerId: "spotify", name: "Evening mix", trackCount: 3 }], nextCursor: null,
  } }));
  await page.route("**/api/admin/media-targets", route => route.fulfill({ json: {
    targets: [{ id: "home", protocol: "jellyfin", backendInstanceId: "home", displayName: "Jellyfin" }],
  } }));
  await page.route("**/api/admin/playlist-links", route => {
    if (route.request().method() !== "POST") return route.fallback();
    expect(route.request().postDataJSON()).toMatchObject({ importMode: "linked" });
    writes.push({ path: "/api/admin/playlist-links", method: "POST", body: route.request().postDataJSON() });
    items[0].inApps = true;
    items[0].enabled = true;
    return route.fulfill({ json: { id, scheduleId: null, initialJobId: null, restoredToApps: true, skippedItemCount: 2 } });
  });
  await page.goto("/#/library/playlists");
  await expect(page.locator(".playlist-row")).toHaveCount(0);
  await page.getByRole("button", { name: "Import playlist", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Import a playlist", exact: true });
  await dialog.getByRole("checkbox", { name: "Evening mix", exact: true }).check();
  await dialog.getByRole("button", { name: "Next", exact: true }).click();
  await dialog.getByRole("radio", { name: /^Keep in sync/ }).check();
  await dialog.getByRole("button", { name: "Import 3 songs", exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.locator(".playlist-row")).toHaveCount(1);
  expect(writes).toHaveLength(1);
  expect(writes[0]).toMatchObject({ path: "/api/admin/playlist-links", body: { sourcePlaylistId: "evening" } });
  await expect(page.getByText(/automatic schedule could not/)).toHaveCount(0);
  await expect(page.getByText(/2 songs could not be restored\./)).toBeVisible();
  await expect(page.getByText(/Playlist added back to your apps\./)).toBeVisible();
});
