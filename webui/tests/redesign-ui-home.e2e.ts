import { expect, test, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { appearance, mockForYou, openHome } from "./for-you-fixtures";
import { expectContainedMobileGeometry, widenText } from "./mobile-geometry";

const modes = [{ width: 1280, theme: "light" }, { width: 1280, theme: "dark" }, { width: 390, theme: "light" }, { width: 390, theme: "dark" }, { width: 320, theme: "light" }] as const;
test("For you actions fit with wider text at 320px", async ({ page }) => {
  await appearance(page, 320, "light");
  const state = await mockForYou(page);
  state.cards[0].inApps = false;
  await openHome(page);
  await widenText(page.locator(".page-actions [data-slot=button]"));
  const title = await page.locator(".section-heading h2").first().boundingBox();
  const actions = await page.locator(".page-actions").boundingBox();
  expect(actions!.y).toBeGreaterThanOrEqual(title!.y + title!.height);
  await expectContainedMobileGeometry(page);
  await page.getByRole("button", { name: "Open Blend 1", exact: true }).click();
  const panel = page.getByRole("dialog");
  await expect(panel.getByRole("button", { name: "Add to apps", exact: true })).toBeVisible();
  await widenText(panel.locator(".panel-footer [data-slot=button]"));
  await expectContainedMobileGeometry(page);
});
async function shot(page: Page, name: string, width: number, theme: string) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  if (width <= 390) await expectContainedMobileGeometry(page);
  const folder = new URL("../test-results/ui-home/", import.meta.url).pathname;
  await mkdir(folder, { recursive: true });
  await page.screenshot({ path: `${folder}${name}-${width}-${theme}.png`, fullPage: !(await page.getByRole("dialog").count()) });
}
async function homeFixture(page: Page, admin: boolean) {
  await mockForYou(page, { admin });
  await page.route("**/api/admin/ui/home", route => route.fulfill({ json: {
    schema: { providers: [{ id: "catalog", name: "Music service" }], activeBackend: "Jellyfin" },
    status: { backendType: "Jellyfin", durableStorage: { readiness: "Ready" } },
    stats: { linkedPlaylists: 24, playableTracks: 1224, unresolvedTracks: 1, activeJobs: 1,
      completedListens: 25, currentWeekListens: 36, previousWeekListens: 24, scrobbleDeliveries: 6, cacheTracks: 79, keptTracks: 3, topArtist: { name: "North", listens: 6 } },
    providerHealth: { providers: [{ providerId: "catalog", enabledAccountCount: 1, capabilityTotal: 4, healthyCapabilityCount: 3, failedCapabilityCount: 1 }] },
    activity: { items: [
      { id: "routine", kind: "job", label: "Background task", state: "succeeded", detail: "routine", occurredAt: "2026-11-03T20:00:00Z" },
      { id: "waiting", kind: "job", label: "Background task", state: "deferred", detail: "Waiting for the next scheduled retry", occurredAt: "2026-11-03T20:00:00Z" },
      { id: "settings", kind: "administration", label: "Runtime settings source use", state: "recorded", detail: "Runtime settings", occurredAt: "2026-11-03T20:00:00Z" },
      { id: "recorded", kind: "job", label: "Record a listen", state: "succeeded", detail: "routine", occurredAt: "2026-11-03T20:00:00Z" },
      { id: "saved", kind: "download", label: "Saved a copy of Blue morning", state: "succeeded", detail: "North", occurredAt: "2026-11-03T20:00:00Z" },
    ], hasMore: false },
  } }));
  await page.route("**/api/admin/ui/now-playing", route => route.fulfill({ json: { items: admin ? [{ deviceId: "phone", itemId: "song", userId: "listener", userName: "Listener", client: "Finamp", device: "Phone", title: "Blue morning", artist: "North", album: "Open skies", providerId: "catalog", sourceConfirmed: true, positionSeconds: 60, durationSeconds: 180, lastActivity: "2026-11-03T20:00:00Z", scrobbled: true }] : [] } }));
  await page.route("**/api/admin/downloads?*", route => route.fulfill({ json: { totalSize: new URL(route.request().url()).searchParams.get("storage") === "cache" ? 600 * 1024 ** 2 : 118 * 1024 ** 2, files: [], count: 79, managedCount: 79 } }));
  await page.route("**/api/admin/config", route => route.fulfill({ json: { cache: { audioMaximumGiB: 2 } } }));
  await page.route("**/api/admin/track-matches?*", route => route.fulfill({ json: { matches: [], stats: { matched: 4999, automatic: 4992, manualMatches: 6, ambiguous: 1, suggested: 1, attention: 1 } } }));
  await page.route("**/api/admin/cache", route => route.fulfill({ json: { media: { payloadBytes: 1670000000, maximumBytes: 2000000000 } } }));
  await page.route("**/api/admin/storage", route => route.fulfill({ json: { backups: [{ id: "backup", status: "verified", createdAt: "2026-11-02T22:00:00Z", bytes: 185100000 }], backupPolicy: { enabled: true }, backupStatus: { nextRunAt: "2026-11-04T22:00:00Z" } } }));
  await page.route("**/api/admin/stats?*", route => route.fulfill({ json: { playCount: 36, timelineUnit: "day", timeline: [{ key: "2026-10-29", plays: 5 }, { key: "2026-10-30", plays: 8 }, { key: "2026-10-31", plays: 3 }, { key: "2026-11-01", plays: 4 }, { key: "2026-11-02", plays: 6 }, { key: "2026-11-03", plays: 7 }, { key: "2026-11-04", plays: 3 }] } }));
}
for (const { section, label, stylesheet, ready } of [
  { section: "releases", label: "New releases", stylesheet: "IntelligenceReleases", ready: "Release type" },
  { section: "taste", label: "Your taste", stylesheet: "IntelligenceTaste", ready: "Reset taste" },
  { section: "recap", label: "Recap", stylesheet: "IntelligenceStats", ready: "Summary" },
]) test(`For you waits for page styles when a pending section is reopened: ${label}`, async ({ page }) => {
  await appearance(page, 390, "light"); await mockForYou(page);
  let release!: () => void, requested = false;
  const styles = new Promise<void>(resolve => { release = resolve; });
  await page.route(new RegExp(`/${stylesheet}[^/]*\\.css$`), async route => { requested = true; await styles; await route.continue(); });
  const content = section === "releases" ? page.getByRole("radiogroup", { name: ready, exact: true })
    : section === "taste" ? page.getByRole("button", { name: ready, exact: true })
    : page.getByRole("region", { name: ready, exact: true });
  try {
    await page.goto(`/#/for-you?section=${section}`, { waitUntil: "domcontentloaded" });
    await expect.poll(() => requested, { message: "The section must request its stylesheet before rendering" }).toBe(true);
    await expect(content).toHaveCount(0);
    await page.getByRole("tab", { name: "For you", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Made for you", exact: true })).toBeVisible();
    await page.getByRole("tab", { name: label, exact: true }).click();
    await expect(page.getByRole("status", { name: "Loading items", exact: true })).toBeVisible();
    await expect(content).toHaveCount(0);
    release();
    await expect(content).toBeVisible();
    await expectContainedMobileGeometry(page);
  } finally { release(); }
});
for (const { width, theme } of modes) {
  for (const admin of [true, false]) test(`Home overview ${admin ? "administrator" : "listener"} ${width} ${theme}`, async ({ page }) => {
    await appearance(page, width, theme); await homeFixture(page, admin); await page.goto("/#/");
    await expect(page.getByRole("region", { name: "Page status", exact: true })).toContainText(admin ? "1 listening" : "Nothing playing");
    await expect(page.getByRole("region", { name: "Your setup", exact: true })).toHaveCount(admin ? 1 : 0);
    if (admin) {
      await expect(page.locator(".setup-tile")).toHaveCount(6);
      const matches = page.locator(".setup-tile").filter({ has: page.getByRole("heading", { name: "Matches", exact: true }) });
      await expect(matches.locator(".tile-value")).toContainText("4,999 matched");
      await expect(matches.locator(".match-gauge > span")).toHaveCount(3);
      await expect(matches).toContainText("4,992 by Allstarr · 6 by you · 1 unsure");
      const storage = page.locator(".setup-tile").filter({ has: page.getByRole("heading", { name: "Storage", exact: true }) });
      await expect(storage.locator(".tile-value")).toHaveText("600 MiB cached of 2 GiB");
      await expect(page.locator(".source-dots > i")).toHaveCount(1);
      await expect(page.locator(".setup-tile").filter({ hasText: "Background work" }).locator(".tile-gauge")).toHaveCount(0);
      await expect(page.getByRole("region", { name: "Needs your attention" })).toContainText("Playback falls back to the others.");
      await expect(page.getByRole("heading", { name: "Runtime settings source use", exact: true })).toHaveCount(0);
      const setup = (await page.getByRole("region", { name: "Your setup", exact: true }).boundingBox())!;
      const week = (await page.getByRole("region", { name: "This week", exact: true }).boundingBox())!;
      const recent = (await page.getByRole("region", { name: "Recent activity", exact: true }).boundingBox())!;
      if (width === 1280) {
        expect(Math.abs(week.y - recent.y)).toBeLessThanOrEqual(1);
        expect(recent.x).toBeGreaterThan(week.x + week.width);
      } else {
        expect(week.y + week.height).toBeLessThanOrEqual(recent.y);
        expect(recent.y + recent.height).toBeLessThanOrEqual(setup.y);
      }
      const tiles = await page.locator(".week-numbers > div").evaluateAll(items => items.map(item => {
        const box = item.getBoundingClientRect(); return { x: box.x, y: box.y };
      }));
      expect(tiles[0].y).toBe(tiles[1].y);
      expect(tiles[2].y).toBe(tiles[3].y);
      expect(tiles[2].y).toBeGreaterThan(tiles[0].y);
      await expect(page.locator(".recent-panel .activity-status-icon")).toHaveCount(1);
    }
    await expect(page.getByRole("group", { name: "Your listening each day", exact: true })).toBeVisible();
    await expect(page.getByText("Background task", { exact: true })).toHaveCount(0);
    await expect(page.getByRole("heading", { name: /^record a listen$/i })).toHaveCount(0);
    await expect(page.getByRole("region", { name: "Pick up where you left off", exact: true })).toBeVisible();
    await shot(page, admin ? "home-administrator" : "home-listener", width, theme);
    if (width <= 390) {
      await expect(page.locator(".mobile-navigation").getByRole("link")).toHaveText(["Home", "Library", "For you", "Activity"]);
      await page.getByRole("button", { name: "More destinations" }).click();
      await expect(page.getByRole("navigation", { name: "More destinations" }).getByRole("link", { name: /Report a problem/ })).toBeVisible();
    }
  });
  test(`For you shelves and mix panel ${width} ${theme}`, async ({ page }) => {
    await appearance(page, width, theme); const state = await mockForYou(page); await openHome(page);
    await expect(page.getByRole("region", { name: "Page status", exact: true })).toContainText("mixes ready");
    const made = page.getByRole("region", { name: "Made for you", exact: true });
    await made.getByRole("button", { name: "Add all to apps", exact: true }).click();
    await expect.poll(() => state.cards.filter(card => card.section === "madeForYou" && !card.inApps).length).toBe(0);
    await shot(page, "for-you", width, theme);
    await page.getByRole("button", { name: "Start a mix", exact: true }).click();
    await expect(page.getByRole("dialog", { name: "Start a mix", exact: true })).toBeVisible();
    await shot(page, "start-mix", width, theme); await page.keyboard.press("Escape");
    await made.getByRole("button", { name: "Open Blend 1", exact: true }).click();
    await expect(page.getByRole("dialog").getByRole("list", { name: "Songs", exact: true })).toBeVisible();
    await shot(page, "mix-panel", width, theme); await page.getByRole("button", { name: "Close", exact: true }).click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
  });
  test(`New releases filters and panel ${width} ${theme}`, async ({ page }) => {
    await appearance(page, width, theme); await mockForYou(page); await page.goto("/#/for-you?section=releases");
    await expect(page.getByRole("region", { name: "Page status", exact: true })).toContainText("1 release");
    await page.getByRole("radio", { name: /Singles/ }).click(); await expect(page.locator(".release-card")).toHaveCount(0);
    await page.reload(); await expect(page.getByRole("radio", { name: /Singles/ })).toHaveAttribute("aria-checked", "true");
    await page.getByRole("radio", { name: /^All/ }).click(); await shot(page, "new-releases", width, theme);
    await page.getByRole("button", { name: "Open Open skies by North", exact: true }).click();
    await expect(page.getByRole("dialog", { name: "Open skies", exact: true })).toBeVisible();
    await shot(page, "release-panel", width, theme); await page.keyboard.press("Escape"); await expect(page.getByRole("dialog")).toHaveCount(0);
  });
  for (const section of ["taste", "recap"]) test(`For you ${section} shared states ${width} ${theme}`, async ({ page }) => {
    await appearance(page, width, theme); await mockForYou(page); await page.goto(`/#/for-you?section=${section}`);
    await expect(page.getByRole("region", { name: "Page status", exact: true })).toBeVisible();
    if (section === "taste") await expect(page.getByRole("button", { name: "Reset taste", exact: true })).toBeVisible();
    else await expect(page.getByRole("region", { name: "Summary", exact: true })).toBeVisible();
    await shot(page, section, width, theme);
  });
  test(`Sign in first frame ${width} ${theme}`, async ({ page }) => {
    await appearance(page, width, theme); await mockForYou(page);
    await page.route("**/api/admin/auth/me", route => route.fulfill({ json: { authenticated: false, backend: "Jellyfin" } }));
    await page.goto("/"); await expect(page.getByRole("heading", { name: "Sign in", exact: true })).toBeVisible();
    await expect(page.getByText("Use your Jellyfin account.", { exact: true })).toBeVisible();
    await expect(page.getByLabel("Username", { exact: true })).toBeVisible(); await shot(page, "sign-in", width, theme);
  });
}

async function personalPagesFixture(page: Page) {
  await mockForYou(page);
  const defaults = { explicitFilter: "All", showExternalLabel: true, showExplicitLabel: true,
    playbackStart: "Balanced", lyricsOrder: "backend,kept,spotify,lrclib", qualityLimit: null as string | null };
  const state = { values: { ...defaults }, revision: "preferences-1", usesHouseholdDefaults: false,
    writes: [] as Array<{ method: string; body: Record<string, unknown> }>, previews: [] as unknown[],
    reports: [] as Array<Record<string, unknown>> };
  await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: { providers: [
    { id: "deezer", name: "Deezer", categories: ["catalog"] },
    { id: "spotify", name: "Spotify", categories: ["lyrics"] },
    { id: "lrclib", name: "LRCLIB", categories: ["lyrics"] },
  ], configSections: [] } }));
  await page.route("**/api/admin/preferences", async route => {
    const method = route.request().method();
    if (method !== "GET") {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      expect(body.expectedRevision).toBe(state.revision);
      state.writes.push({ method, body });
      if (method === "DELETE") {
        state.values = { ...defaults };
        state.usesHouseholdDefaults = true;
      } else {
        expect(method).toBe("PUT");
        state.values = { ...state.values, explicitFilter: String(body.explicitFilter),
          showExternalLabel: Boolean(body.showExternalLabel), showExplicitLabel: Boolean(body.showExplicitLabel),
          playbackStart: String(body.playbackStart),
          ...(typeof body.lyricsOrder === "string" ? { lyricsOrder: body.lyricsOrder } : {}),
          ...(typeof body.qualityLimit === "string" ? { qualityLimit: body.qualityLimit || null } : {}) };
        state.usesHouseholdDefaults = false;
      }
      state.revision = `preferences-${state.writes.length + 1}`;
    }
    await route.fulfill({ json: { values: state.values, householdDefaults: defaults, householdQuality: "CdLossless",
      revision: state.revision, usesHouseholdDefaults: state.usesHouseholdDefaults, cleanOnlyLocked: false } });
  });
  await page.route("**/api/admin/ui/listener-context", route => route.fulfill({ json: { backend: "Jellyfin",
    username: "Listener", musicClientUrl: "http://music.test:5274", addressSource: "configured" } }));
  await page.route("**/api/admin/problem-reports/context", async route => {
    state.previews.push(route.request().postDataJSON());
    await route.fulfill({ json: { schemaVersion: 1, capturedAt: "2026-11-03T20:00:00Z",
      subjectUserId: null, subjectName: null,
      song: { itemId: "ext-deezer-song-fixture", externalSnapshotId: "33333333-3333-4333-8333-333333333333",
        canonicalRecordingId: null, libraryTrackId: null, title: "Blue morning", artist: "North", album: "Open skies",
        durationMilliseconds: 180000, catalogProviderId: "deezer" }, match: null, playback: null, checks: [] } });
  });
  await page.route("**/api/admin/problem-reports", async route => {
    expect(route.request().method()).toBe("POST");
    state.reports.push(route.request().postDataJSON() as Record<string, unknown>);
    await route.fulfill({ status: 201, json: { id: "22222222-2222-4222-8222-222222222222",
      status: "new", createdAt: "2026-11-03T20:00:00Z", replayed: false } });
  });
  return state;
}

for (const { width, theme } of modes) {
  test(`Listening preferences autosave and Undo ${width} ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    const state = await personalPagesFixture(page);
    await page.goto("/#/preferences");
    const status = page.getByRole("region", { name: "Listening preferences status", exact: true });
    await expect(status).toContainText("Changes save automatically");
    await expect(page.getByRole("button", { name: "Save preferences", exact: true })).toHaveCount(0);
    await expect(page.getByRole("link", { name: "Change the household limit", exact: true })).toHaveCount(0);
    const label = page.getByRole("switch", { name: "Show [A] for external tracks", exact: true });
    await label.click();
    await expect(label).toBeEnabled();
    await expect(label).not.toBeChecked();
    await expect.poll(() => state.writes.length).toBe(1);
    const saved = page.getByRole("status").filter({ hasText: "Listening preferences saved." }).last();
    await expect(saved.getByRole("button", { name: "Undo", exact: true })).toBeVisible();
    await page.evaluate(() => window.scrollTo(0, 0));
    await shot(page, "listening-preferences", width, theme);
    await saved.getByRole("button", { name: "Undo", exact: true }).click();
    await expect(label).toBeChecked();
    await expect(label).toBeEnabled();
    await expect.poll(() => state.writes.length).toBe(2);
    expect(state.writes.map(write => write.body.showExternalLabel)).toEqual([false, true]);
    expect(state.writes[1].body.expectedRevision).toBe("preferences-2");
  });

  test(`Connect apps selected guide ${width} ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    await personalPagesFixture(page);
    await page.goto("/#/connect");
    await expect(page.getByRole("region", { name: "Connection status", exact: true })).toContainText("Server address ready to copy");
    const app = page.getByRole("button", { name: "Finamp Android and iOS", exact: true });
    await app.click();
    await expect(app).toHaveAttribute("aria-pressed", "true");
    const guide = page.getByRole("region", { name: "Set up Finamp", exact: true });
    await expect(guide).toBeVisible();
    await expect(guide.getByRole("listitem")).toHaveCount(3);
    await expect(page.getByRole("region", { name: "Set up Finer Player", exact: true })).toHaveCount(0);
    await expect(guide.getByLabel("Music-client address", { exact: true })).toHaveValue("http://music.test:5274");
    await expect(guide.getByLabel("Username", { exact: true })).toHaveValue("Listener");
    await expect(guide.getByRole("button", { name: "Copy server address", exact: true })).toBeEnabled();
    await expect(guide.getByRole("button", { name: "Copy username", exact: true })).toBeEnabled();
    await page.evaluate(() => { (document.activeElement as HTMLElement)?.blur(); window.scrollTo(0, 0); });
    await shot(page, "connect-apps", width, theme);
  });

  test(`General report first frame ${width} ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    const state = await personalPagesFixture(page);
    await page.goto("/#/report");
    const status = page.getByRole("region", { name: "Report status", exact: true });
    await expect(status).toContainText("General report");
    const form = page.getByRole("form", { name: "Problem report", exact: true });
    await expect(form).toBeVisible();
    await expect(page.getByRole("alert")).toHaveCount(0);
    await expect(page.getByRole("article", { name: "Included report details", exact: true })).toHaveCount(0);
    expect(state.previews).toEqual([]);
    await form.getByLabel("What's wrong?", { exact: true }).fill("My music app stops responding when I open a playlist.");
    await expect(form.getByRole("button", { name: "Send report", exact: true })).toBeEnabled();
    await page.evaluate(() => { (document.activeElement as HTMLElement)?.blur(); window.scrollTo(0, 0); });
    await shot(page, "report-general", width, theme);
  });

  test(`Song report reasons ${width} ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    const state = await personalPagesFixture(page);
    await page.goto("/#/report?item=ext-deezer-song-fixture");
    const status = page.getByRole("region", { name: "Report status", exact: true });
    await expect(status).toContainText("Song report");
    const form = page.getByRole("form", { name: "Problem report", exact: true });
    const context = page.getByRole("article", { name: "Included report details", exact: true });
    await expect(context).toContainText("Blue morning");
    await expect(context).toContainText("Deezer");
    expect(state.previews).toEqual([{ itemId: "ext-deezer-song-fixture" }]);
    const reasons = page.getByRole("radiogroup", { name: "Problem", exact: true });
    await expect(reasons.getByRole("radio")).toHaveCount(6);
    await reasons.getByRole("radio", { name: "Wrong lyrics", exact: true }).check();
    expect(state.previews).toEqual([{ itemId: "ext-deezer-song-fixture" }]);
    const send = form.getByRole("button", { name: "Send report", exact: true });
    await expect(send).toBeDisabled();
    await form.getByLabel("What's wrong?", { exact: true }).fill("The words belong to a different song.");
    await expect(send).toBeEnabled();
    await page.evaluate(() => { (document.activeElement as HTMLElement)?.blur(); window.scrollTo(0, 0); });
    await shot(page, "report-song", width, theme);
    await send.click();
    await expect(page.getByRole("heading", { name: "Report saved", exact: true })).toBeVisible();
    expect(state.reports).toHaveLength(1);
    expect(state.reports[0]).toEqual({ requestId: expect.any(String), kind: "wrong-lyrics",
      note: "The words belong to a different song.", selector: { itemId: "ext-deezer-song-fixture" } });
    expect(state.previews).toEqual([{ itemId: "ext-deezer-song-fixture" }]);
  });
}
