import { mkdir } from "node:fs/promises";
import { expect, test, type Page } from "@playwright/test";
import { expectPageSurface, expectSingleRowFooter, expectSubtleSelection, expectToolbarLayout } from "./library-layout";

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

for (const [width, theme] of [[1280, "light"], [1280, "dark"], [390, "light"]] as const) {
  test(`Library redesign playlists and panel ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const writes = await fixture(page);
    await page.goto("/#/library/playlists");
    await expect(page.getByRole("region", { name: "Playlist status" })).toContainText("1 playlist");
    await expectPageSurface(page.locator(".library-list"));
    await expect(page.getByRole("radio", { name: /Paused/ })).toHaveCount(0);
    await expect(page.getByRole("region", { name: "Playlist status" })).toContainText("Next sync");
    if (width === 1280) await expectToolbarLayout(page.locator(".library-search"), page.getByRole("radiogroup", { includeHidden: true, name: "Playlist filters" }));
    await page.getByRole("radio", { name: /Healthy/ }).click();
    await expect(page.getByText("No playlists match these filters")).toBeVisible();
    await page.getByRole("radio", { name: /Needs a look/ }).click();
    await page.reload();
    await expect(page.getByRole("radio", { name: /Needs a look/ })).toBeChecked();
    await mkdir("test-results/ui-library", { recursive: true });
    await page.screenshot({ path: `test-results/ui-library/playlists-${width}-${theme}.png`, fullPage: true });
    await page.getByRole("button", { name: "Open Evening mix playlist details" }).click();
    const panel = page.getByRole("dialog", { name: "Evening mix", exact: true });
    await expect(panel.getByRole("heading", { name: "Evening mix", exact: true })).toBeVisible();
    await expect(panel.getByRole("button", { name: "Sync now", exact: true })).toBeVisible();
    const selected = page.locator(".library-rows .item-row.selected");
    await expectSubtleSelection(selected, selected.locator("h3"));
    await expectSingleRowFooter(panel);
    await expect(panel.locator(".panel-body").getByRole("button", { name: "Actions", exact: true })).toHaveCount(0);
    await panel.locator(".panel-header").getByRole("button", { name: "Actions", exact: true }).click();
    await expect(page.getByRole("menuitem", { name: "Refresh source", exact: true })).toBeVisible();
    await expect(page.getByRole("menuitem", { name: "Edit settings", exact: true })).toBeVisible();
    await page.keyboard.press("Escape");
    if (width === 1280) await expectToolbarLayout(page.locator(".library-search"), page.getByRole("radiogroup", { includeHidden: true, name: "Playlist filters" }));
    await page.screenshot({ path: `test-results/ui-library/playlist-panel-${width}-${theme}.png`, fullPage: true });
    await panel.getByRole("switch", { name: "Keep in sync", exact: true }).click();
    await expect.poll(() => writes.filter(item => item.path.endsWith("/schedules/schedule")).length).toBe(1);
    expect(writes.at(-1)?.body.enabled).toBe(false);
    await page.getByRole("button", { name: "Undo", exact: true }).click();
    await expect(panel.getByRole("switch", { name: "Keep in sync", exact: true })).toBeChecked();
    await panel.getByRole("button", { name: "Close", exact: true }).click();
    await expect(panel).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.getByRole("button", { name: "Import playlist", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Import a playlist", exact: true });
    await expect(dialog.getByRole("checkbox", { name: /Evening mix/ })).toBeDisabled();
    await expect(dialog).toContainText("Already in your apps");
    await dialog.getByRole("checkbox", { name: /Late drive/ }).check();
    await page.screenshot({ path: `test-results/ui-library/import-playlist-choose-${width}-${theme}.png`, fullPage: true });
    await dialog.getByRole("button", { name: "Next", exact: true }).click();
    await expect(dialog.getByRole("heading", { name: "What comes over" })).toBeVisible();
    await expect(dialog).toContainText("12 songs");
    await page.screenshot({ path: `test-results/ui-library/import-playlist-options-${width}-${theme}.png`, fullPage: true });
    await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    expect(writes.filter(item => item.path === "/api/admin/playlist-links")).toHaveLength(0);
    expect(await page.pageErrors()).toEqual([]);
  });
}

test("Library playlist autosave restores failed values and keeps other settings", async ({ page }) => {
  const writes = await fixture(page);
  await page.goto("/#/library/playlists");
  await page.getByRole("button", { name: "Open Evening mix playlist details" }).click();
  const panel = page.getByRole("dialog", { name: "Evening mix", exact: true });
  const toggle = panel.getByRole("switch", { name: "Keep in sync", exact: true });
  await expect(toggle).toBeChecked();
  await page.route("**/playlist-links/schedules/schedule", route => route.fulfill({ status: 503, json: { error: "Sync settings could not be saved." } }));
  await toggle.click();
  await expect(panel.getByRole("alert")).toContainText("Sync settings could not be saved.");
  await expect(toggle).toBeChecked();
  await panel.getByRole("switch", { name: "Keep downloaded copies", exact: true }).click();
  await expect.poll(() => writes.filter(item => item.path === `/api/admin/playlist-links/${id}`).length).toBe(1);
  expect(writes.at(-1)?.body).toMatchObject({ expectedRevision: 1, trackRetention: "keepAll", mirrorStaleEntries: true, preserveManualEntries: true });
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect(panel.getByRole("switch", { name: "Keep downloaded copies", exact: true })).not.toBeChecked();
  expect(writes.at(-1)?.body).toMatchObject({ expectedRevision: 2, trackRetention: "onDemand" });
});

test("Library playlist panel navigation skips mixes that open elsewhere", async ({ page }) => {
  await fixture(page);
  await page.route(url => url.pathname === "/api/admin/mixes", route => route.fulfill({ json: [{ id: "mix", name: "A mix", kind: "custom", inApps: true }] }));
  await page.route("**/api/admin/mixes/mix", route => route.fulfill({ json: { songs: [] } }));
  await page.goto("/#/library/playlists");
  await expect(page.getByRole("region", { name: "Playlist status" })).toContainText("2 playlists");
  await expect(page.getByRole("link", { name: "Open A mix mix", exact: true })).toHaveAttribute("href", "#/for-you?mix=mix");
  await page.getByRole("button", { name: "Open Evening mix playlist details" }).click();
  const panel = page.getByRole("dialog", { name: "Evening mix", exact: true });
  await expect(panel.getByRole("button", { name: "Previous song", exact: true })).toBeDisabled();
  await expect(panel.getByRole("button", { name: "Next song", exact: true })).toBeDisabled();
});


test("Playlist choices group accounts, clear on tab changes and retry only remaining imports", async ({ page }) => {
  const writes = await fixture(page);
  const secondAccount = "44444444-4444-4444-8444-444444444444";
  await page.route("**/api/admin/playlist-sources", route => route.fulfill({ json: {
    accounts: [{ id: account, providerId: "spotify", displayName: "My Spotify" }, { id: secondAccount, providerId: "apple", displayName: "My Apple Music" }],
    blockedAccounts: [], providers: [{ id: "spotify", displayName: "Spotify" }, { id: "apple", displayName: "Apple Music" }],
  } }));
  await page.route(`**/playlist-sources/${secondAccount}/playlists**`, route => route.fulfill({ json: { items: [{ id: "replay", providerId: "apple", name: "Replay", trackCount: 8 }] } }));
  await page.goto("/#/library/playlists");
  await page.getByRole("button", { name: "Import playlist", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Import a playlist", exact: true });
  await expect(dialog.getByRole("region", { name: "Spotify · My Spotify", exact: true })).toBeVisible();
  await expect(dialog.getByRole("region", { name: "Apple Music · My Apple Music", exact: true })).toBeVisible();
  await dialog.getByRole("checkbox", { name: "Late drive", exact: true }).check();
  await dialog.getByRole("checkbox", { name: "Replay", exact: true }).check();
  await expect(dialog.getByRole("status")).toContainText("2 playlists selected · 20 songs");
  expect(writes.filter(item => item.path === "/api/admin/playlist-links")).toHaveLength(0);
  await dialog.getByRole("tab", { name: "Paste a link", exact: true }).click();
  await expect(dialog.getByRole("button", { name: "Next", exact: true })).toBeDisabled();
  await dialog.getByRole("tab", { name: "From my accounts", exact: true }).click();
  await expect(dialog.getByRole("checkbox", { name: "Late drive", exact: true })).not.toBeChecked();
  await dialog.getByRole("checkbox", { name: "Late drive", exact: true }).check();
  await dialog.getByRole("checkbox", { name: "Replay", exact: true }).check();
  await dialog.getByRole("button", { name: "Next", exact: true }).click();
  let failReplay = true;
  await page.route(url => url.pathname === "/api/admin/playlist-links" && url.search === "", route => {
    if (route.request().method() !== "POST") return route.fallback();
    const body = route.request().postDataJSON();
    writes.push({ path: new URL(route.request().url()).pathname, body });
    if (body.sourcePlaylistId === "replay" && failReplay) {
      failReplay = false;
      return route.fulfill({ status: 503, json: { error: "Replay is temporarily unavailable." } });
    }
    return route.fulfill({ json: { id: "new" } });
  });
  await dialog.getByRole("button", { name: "Import 20 songs", exact: true }).click();
  await expect(dialog.getByRole("alert")).toContainText("Replay is temporarily unavailable.");
  await expect(dialog.locator(".import-preview")).toContainText("8 songs from My Apple Music");
  await dialog.getByRole("button", { name: "Import 8 songs", exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(writes.filter(item => item.path === "/api/admin/playlist-links").map(item => item.body.sourcePlaylistId)).toEqual(["late", "replay", "replay"]);
});
