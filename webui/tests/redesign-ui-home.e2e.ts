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
async function homeFixture(page: Page, admin: boolean, playing = true, todayCount = true, activityItems?: unknown[]) {
  await mockForYou(page, { admin });
  const failing = ["Monochrome", "Paxsenix", "Amazon Music", "Qobuz", "SoundCloud"].map(name => ({ id: name.toLowerCase().replaceAll(" ", "-"), name }));
  const providers = [{ id: "catalog", name: "Music service" }, ...Array.from({ length: 22 }, (_, index) => ({ id: `working-${index}`, name: `Working source ${index + 1}` })), ...failing];
  await page.route("**/api/admin/ui/home", route => route.fulfill({ json: {
    schema: { providers, activeBackend: "Jellyfin" },
    status: { backendType: "Jellyfin", durableStorage: { readiness: "Ready" } },
    stats: { linkedPlaylists: 24, playableTracks: 1224, unresolvedTracks: 0, activeJobs: 2, waitingJobs: 1, completedJobsToday: 50, failedJobsToday: 0,
      completedListens: 25, currentWeekListens: 36, previousWeekListens: 0, scrobbleDeliveries: 6, ...(todayCount ? { scrobblesToday: 6 } : {}), cacheTracks: 79, keptTracks: 3, topArtist: { name: "North", listens: 6 } },
    providerHealth: { providers: providers.map(provider => ({ providerId: provider.id, enabledAccountCount: 1, capabilityTotal: 1, healthyCapabilityCount: failing.includes(provider) ? 0 : 1, failedCapabilityCount: failing.includes(provider) ? 1 : 0, lastFailureCode: provider.id === "amazon-music" ? "stream_slow" : null })) },
    activity: { items: activityItems ?? [
      { id: "routine", kind: "job", label: "Background task", state: "succeeded", detail: "routine", occurredAt: "2026-11-03T20:00:00Z" },
      { id: "waiting", kind: "job", label: "Background task", state: "deferred", detail: "Waiting for the next scheduled retry", occurredAt: "2026-11-03T20:00:00Z" },
      { id: "settings", kind: "administration", label: "Runtime settings source use", state: "recorded", detail: "Runtime settings", occurredAt: "2026-11-03T20:00:00Z" },
      { id: "recorded", kind: "job", label: "Record a listen", state: "succeeded", detail: "routine", occurredAt: "2026-11-03T20:00:00Z" },
      { id: "sent", kind: "scrobble", label: "Sent Blue morning to Last.fm and ListenBrainz", state: "delivered", detail: "Your listen", occurredAt: "2026-11-03T20:01:00Z" },
      { id: "saved", kind: "download", label: "Saved a copy of Blue morning", state: "succeeded", detail: "North", occurredAt: "2026-11-03T20:00:00Z" },
      { id: "mixes", kind: "playlist", label: "Mixes refreshed", state: "succeeded", detail: "13 mixes", occurredAt: "2026-11-03T19:59:00Z" },
      { id: "health", kind: "job", label: "Health check", displayName: "Health check passing", state: "succeeded", detail: "4 passed, 3 not checked", occurredAt: "2026-11-03T19:58:00Z" },
    ], hasMore: false },
  } }));
  await page.route("**/api/admin/ui/now-playing", route => route.fulfill({ json: { items: admin && playing ? [{ deviceId: "phone", itemId: "song", userId: "listener", userName: "Listener", client: "Finamp", device: "Phone", title: "Blue morning", artist: "North", album: "Open skies", providerId: "catalog", sourceConfirmed: true, positionSeconds: 60, durationSeconds: 180, lastActivity: "2026-11-03T20:00:00Z", scrobbled: true }] : [] } }));
  await page.route("**/api/admin/downloads?*", route => route.fulfill({ json: { totalSize: new URL(route.request().url()).searchParams.get("storage") === "cache" ? 600 * 1024 ** 2 : 118 * 1024 ** 2, files: [], count: 79, managedCount: 79 } }));
  await page.route("**/api/admin/library-index/counts", route => route.fulfill({ json: { trackCount: 10218 } }));
  await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: { accounts: [], audienceUsers: [{ id: "one" }, { id: "two" }, { id: "three" }] } }));
  await page.route("**/api/admin/activity/tasks?scope=everyone", route => route.fulfill({ json: { items: [{ id: "sync", name: "Update playlist", runPlaylistLinkId: "playlist", enabled: true, nextRunAt: "2026-11-07T04:00:00Z" }] } }));
  await page.route("**/api/admin/config", route => route.fulfill({ json: { cache: { audioMaximumGiB: 2 } } }));
  await page.route("**/api/admin/track-matches?*", route => route.fulfill({ json: { matches: [], stats: { matched: 4999, automatic: 4992, manualMatches: 6, ambiguous: 0, suggested: 1, attention: 1 } } }));
  await page.route("**/api/admin/cache", route => route.fulfill({ json: { media: { payloadBytes: 1670000000, maximumBytes: 2000000000 } } }));
  await page.route("**/api/admin/storage", route => route.fulfill({ json: { backups: [{ id: "backup", status: "verified", createdAt: "2026-11-02T22:00:00Z", bytes: 185100000 }], backupPolicy: { enabled: true, dailyRetentionCount: 7, weeklyRetentionCount: 4 }, backupStatus: { nextRunAt: "2026-11-04T22:00:00Z" } } }));
  await page.route("**/api/admin/stats?*", route => route.fulfill({ json: { playCount: 36, timelineUnit: "day", timeline: [{ key: "2026-10-29", plays: 5 }, { key: "2026-10-30", plays: 8 }, { key: "2026-10-31", plays: 3 }, { key: "2026-11-01", plays: 4 }, { key: "2026-11-02", plays: 6 }, { key: "2026-11-03", plays: 7 }, { key: "2026-11-04", plays: 3 }] } }));
}
test("Home fills setup tiles while mixes take ten seconds", async ({ page }) => {
  await appearance(page, 1280, "dark");
  await homeFixture(page, true);
  let mixesPending = true, release!: () => void;
  const pending = new Promise<void>(resolve => { release = resolve; });
  await page.route("**/api/admin/mixes?*", async route => {
    await new Promise(resolve => setTimeout(resolve, 10_000));
    await pending;
    mixesPending = false;
    await route.fallback();
  });
  await page.goto("/#/");
  const tile = (title: string) => page.locator(".setup-tile").filter({ has: page.getByRole("heading", { name: title, exact: true }) });
  try {
    await expect(tile("Matches")).toContainText("4,999 matched");
    await expect(tile("Storage")).toContainText("600 MB cached of 2 GB");
    await expect(tile("Backups")).toContainText("Verified");
    expect(mixesPending).toBe(true);
    await expect(page.getByRole("status", { name: "Loading mixes", exact: true })).toBeVisible();
    await expect(page.getByText("Your mixes start here", { exact: true })).toHaveCount(0);
    release();
    await expect(page.getByRole("region", { name: "Pick up where you left off", exact: true })).toBeVisible({ timeout: 15_000 });
  } finally { release(); }
});

for (const fails of [false, true]) test(`Home uses loading placeholders until each setup request settles: ${fails ? "failure" : "success"}`, async ({ page }) => {
  await appearance(page, 1280, "dark");
  await homeFixture(page, true);
  let release!: () => void;
  const pending = new Promise<void>(resolve => { release = resolve; });
  for (const endpoint of ["track-matches?*", "downloads?*", "storage", "mixes?*"]) {
    await page.route(`**/api/admin/${endpoint}`, async route => {
      await pending;
      if (fails) await route.fulfill({ status: 503, json: { error: "Unavailable" } });
      else await route.fallback();
    });
  }
  try {
    await page.goto("/#/");
    for (const title of ["Matches", "Storage", "Backups"]) {
      const tile = page.locator(".setup-tile").filter({ has: page.getByRole("heading", { name: title, exact: true }) });
      await expect(tile.getByRole("status", { name: `Loading ${title}`, exact: true })).toBeVisible();
      await expect(tile).not.toContainText("Not checked");
      await expect(tile).not.toContainText("Not available");
    }
    await expect(page.getByText("Your mixes start here", { exact: true })).toHaveCount(0);
    release();
    await expect(page.getByRole("status", { name: "Loading Matches", exact: true })).toHaveCount(0);
    if (fails) {
      await expect(page.locator(".setup-tile").filter({ hasText: "Matches" })).toContainText("Not checked");
      await expect(page.locator(".setup-tile").filter({ hasText: "Backups" })).toContainText("Not available");
      await expect(page.getByText("Your mixes start here", { exact: true })).toBeVisible();
    } else await expect(page.locator(".setup-tile").filter({ hasText: "Matches" })).toContainText("4,999 matched");
  } finally { release(); }
});

test("Home recent activity keeps one rollout summary and rematch failures", async ({ page }) => {
  await appearance(page, 1280, "dark");
  const row = { kind: "matching", state: "accepted", detail: "Result", occurredAt: "2026-11-03T20:00:00Z" };
  await homeFixture(page, true, true, true, [
    { ...row, id: "matched", label: "Track matched", action: "automatic.review", isRoutine: true },
    { ...row, id: "waiting", label: "Refresh matches", state: "retry_scheduled", action: "track-match.rematch", isRoutine: true },
    { ...row, id: "progress", label: "Checking matches", action: "checking-matches", state: "running", isRoutine: false,
      detail: "20 of 100 · Now plays from your library: 12 songs" },
    { ...row, id: "failed", label: "Refresh matches", state: "failed", severity: "error", isRoutine: false },
  ]);
  await page.goto("/#/");
  const recent = page.getByRole("region", { name: "Recent activity", exact: true });
  await expect(recent.getByText("Checking matches", { exact: true })).toHaveCount(1);
  await expect(recent).toContainText("20 of 100 · Now plays from your library: 12 songs");
  await expect(recent).not.toContainText("Track matched");
  await expect(recent.getByText("Refresh matches", { exact: true })).toHaveCount(1);
});

test("Home recent activity uses server flags and keeps background failures", async ({ page }) => {
  await appearance(page, 1280, "dark");
  const row = { kind: "administration", state: "succeeded", detail: "Result", occurredAt: "2026-11-03T20:00:00Z" };
  await homeFixture(page, true, true, true, [
    { ...row, id: "routine", label: "Matching old listens", isRoutine: true },
    { ...row, id: "failed", label: "Background task", state: "failed", severity: "error", isRoutine: false },
    { ...row, id: "health", label: "Health check", action: "health.smoke", detail: "4 passed", isRoutine: false },
    { ...row, id: "health-old", label: "Health check", action: "health.smoke", detail: "4 passed", isRoutine: false, occurredAt: "2026-11-03T19:00:00Z" },
  ]);
  await page.goto("/#/");
  const recent = page.getByRole("region", { name: "Recent activity", exact: true });
  await expect(recent).toContainText("Background task");
  await expect(recent).not.toContainText("Matching old listens");
  await expect(recent).not.toContainText("Nothing new lately");
  await expect(recent.getByText("Health check", { exact: true })).toHaveCount(1);
});

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
    await expect(page.getByRole("region", { name: "Page status", exact: true })).toContainText(admin ? "Playing normally" : "Nothing playing");
    await expect(page.getByRole("region", { name: "Your setup", exact: true })).toHaveCount(admin ? 1 : 0);
    if (admin) {
      await expect(page.locator(".setup-tile")).toHaveCount(6);
      const matches = page.locator(".setup-tile").filter({ has: page.getByRole("heading", { name: "Matches", exact: true }) });
      await expect(matches.locator(".tile-value")).toContainText("4,999 matched");
      await expect(matches.locator(".match-gauge .status-gauge > span")).toHaveCount(3);
      await expect(matches).toContainText("4,992 by Allstarr · 6 by you · 1 unsure");
      const storage = page.locator(".setup-tile").filter({ has: page.getByRole("heading", { name: "Storage", exact: true }) });
      await expect(storage.locator(".tile-value")).toHaveText("600 MB cached of 2 GB");
      await expect(page.locator(".source-dots > i")).toHaveCount(28);
      const status = page.getByRole("region", { name: "Page status", exact: true });
      await expect(status).toContainText("5 sources need a look");
      await expect(status).toContainText("1 job waiting");
      if (width < 650) await expect(status.getByText("1 job waiting", { exact: true })).toBeHidden();
      else await expect(status.getByText("1 job waiting", { exact: true })).toBeVisible();
      const now = page.getByRole("region", { name: "Now playing", exact: true });
      await expect(now).toContainText("Listener on Finamp, Phone · from Music service");
      await expect(now.locator(".playback-time")).toHaveText("1:00 / 3:00");
      await expect(now.getByRole("link", { name: "Report a problem", exact: true })).toHaveAttribute("data-variant", "text");
      await expect(now.locator(".listener-profile, .playback-route")).toHaveCount(0);
      await expect(now.locator(".state-pill")).toHaveText("1 listening");
      if (width <= 390) {
        const card = (await now.locator("article").boundingBox())!, chip = (await now.locator(".state-pill").boundingBox())!;
        expect(Math.abs(card.x + card.width - chip.x - chip.width - 14)).toBeLessThanOrEqual(1);
        expect(Math.abs(chip.y - card.y - 14)).toBeLessThanOrEqual(1);
      }
      await expect(page.locator(".home-setup > header")).toContainText("Jellyfin · 3 listeners");
      await expect(page.locator(".setup-tile").filter({ hasText: "Library" })).toContainText("10,218 songs indexed in Jellyfin");
      await expect(page.locator(".setup-tile").filter({ hasText: "Background work" })).toContainText("50 jobs done today");
      await expect(page.locator(".setup-tile").filter({ hasText: "Background work" })).toContainText("Next: 1 playlist sync");
      await expect(page.locator(".setup-tile").filter({ hasText: "Backups" })).toContainText("7 daily, 4 weekly kept");
      const heights = await page.locator(".setup-tile").evaluateAll(items => items.map(item => item.getBoundingClientRect().height));
      expect(Math.max(...heights) - Math.min(...heights)).toBeLessThanOrEqual(1);
      await expect(page.locator(".setup-tile").filter({ hasText: "Background work" }).locator(".tile-gauge")).toHaveCount(1);
      await expect(page.getByRole("region", { name: "Needs your attention" })).toContainText("Playback falls back to the others.");
      await expect(page.getByRole("heading", { name: "Runtime settings source use", exact: true })).toHaveCount(0);
      const setup = (await page.getByRole("region", { name: "Your setup", exact: true }).boundingBox())!;
      const week = (await page.getByRole("region", { name: "This week", exact: true }).boundingBox())!;
      const recent = (await page.getByRole("region", { name: "Recent activity", exact: true }).boundingBox())!;
      if (width === 1280) {
        expect((await now.locator("article").boundingBox())!.height).toBeLessThan(145);
        const banner = page.getByRole("region", { name: "Needs your attention" });
        expect((await banner.boundingBox())!.height).toBeLessThan(80);
        await expect(banner.locator(".attention-consequence")).toHaveText("Playback falls back to the others.");
        expect(await banner.locator(".attention-consequence").evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
        expect(await banner.locator("div").evaluate(element => element.clientHeight <= parseFloat(getComputedStyle(element).lineHeight) + 1)).toBe(true);
        await expect(page.locator(".week-chart .grid-line")).toHaveCount(0);
        const plot = (await page.locator(".week-chart .plot").boundingBox())!;
        const tallest = await page.locator(".week-chart .bar").evaluateAll(bars => Math.max(...bars.map(bar => bar.getBoundingClientRect().height)));
        expect(Math.abs(tallest - plot.height)).toBeLessThanOrEqual(1);
        await expect(page.locator(".week-numbers")).toContainText("songs finished · +36 on last week");
        expect(Math.abs(week.y - recent.y)).toBeLessThanOrEqual(1);
        expect(recent.x).toBeGreaterThan(week.x + week.width);
      } else {
        expect(week.y + week.height).toBeLessThanOrEqual(recent.y);
        expect(recent.y + recent.height).toBeLessThanOrEqual(setup.y);
      }
      const tiles = await page.locator(".week-numbers > div").evaluateAll(items => items.map(item => {
        const box = item.getBoundingClientRect(); return { x: box.x, y: box.y, height: box.height };
      }).filter(box => box.height > 0));
      expect(tiles[0].y).toBe(tiles[1].y);
      expect(tiles[2].y).toBe(tiles[3].y);
      expect(tiles[2].y).toBeGreaterThan(tiles[0].y);
      await expect(page.locator(".recent-panel .activity-status-icon")).toHaveCount(4);
      await expect(page.locator(".recent-panel .item-row:visible")).toHaveCount(width === 1280 ? 4 : 2);
    }
    if (width === 1280) await expect(page.getByRole("group", { name: "Songs finished each day", exact: true })).toBeVisible();
    else {
      await expect(page.locator(".week-chart")).toBeHidden();
      await expect(page.locator(".phone-trend")).toContainText("+36");
    }
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

for (const { width, theme } of modes.filter(mode => mode.width !== 320)) {
  test(`Home idle collapses playback ${width} ${theme}`, async ({ page }) => {
    await appearance(page, width, theme); await homeFixture(page, true, false); await page.goto("/#/");
    await expect(page.getByRole("region", { name: "Page status", exact: true })).toContainText("Nothing playing");
    await expect(page.locator(".home-now article")).toHaveCount(0);
    await expect(page.locator(".home-now")).toHaveCSS("display", "none");
    await expect(page.locator(".home-setup > header")).toContainText("3 listeners");
    await shot(page, "home-idle", width, theme);
  });
}
test("Home omits unavailable setup details without inventing counts", async ({ page }) => {
  await appearance(page, 1280, "dark"); await homeFixture(page, true, false, false);
  await page.route("**/api/admin/library-index/counts", route => route.fulfill({ status: 503 }));
  await page.route("**/api/admin/provider-accounts", route => route.fulfill({ status: 503 }));
  await page.route("**/api/admin/activity/tasks?scope=everyone", route => route.fulfill({ status: 503 }));
  await page.route("**/api/admin/config", route => route.fulfill({ status: 503 }));
  await page.route("**/api/admin/track-matches?*", route => route.fulfill({ status: 503 }));
  await page.route("**/api/admin/storage", route => route.fulfill({ json: { backups: [{ id: "backup", status: "invalid", createdAt: "2026-11-02T22:00:00Z", bytes: 185100000 }] } }));
  await page.goto("/#/");
  await expect(page.locator(".setup-tile")).toHaveCount(6);
  await expect(page.locator(".setup-tile").filter({ hasText: "Library" }).locator("p")).toHaveCount(0);
  await expect(page.locator(".home-setup > header")).toHaveText("Your setupJellyfin");
  await expect(page.locator(".setup-tile").filter({ hasText: "Background work" })).not.toContainText("Next:");
  const storage = page.locator(".setup-tile").filter({ hasText: "Storage" });
  await expect(storage.locator(".tile-value")).toHaveText("600 MB cached");
  await expect(storage).not.toContainText("of 2 GB");
  await expect(page.locator(".scrobble-kpi")).toHaveCount(0);
  const matches = page.locator(".setup-tile").filter({ has: page.getByRole("heading", { name: "Matches", exact: true }) });
  await expect(matches.locator(".state-pill")).toHaveText("Not checked");
  await expect(matches.locator(".tile-number, p")).toHaveCount(0);
  await expect(page.locator(".setup-tile").filter({ hasText: "Backups" }).locator(".state-pill")).toHaveAttribute("data-tone", "warn");
});

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
