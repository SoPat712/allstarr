import { expect, test, type Locator, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { panelFixture, settingsFixture, homeFixture } from "./shared-design-fixtures";

const capturePhase: string = "after";
const groups = [
  { name: "library", paths: ["library/playlists", "library/matches", "library/cached", "library/kept", "library/import"] },
  { name: "activity", paths: ["activity", "activity?view=plays", "activity?view=jobs", "activity?view=tasks", "activity?view=reports"] },
  { name: "sources", paths: ["sources/services", "sources/accounts", "connect", "report"] },
  { name: "settings", paths: ["settings", "settings/playback", "settings/library", "settings/discovery", "settings/history", "settings/people", "settings/alerts", "settings/storage", "settings/backups", "settings/health", "settings/setup", "settings/appearance", "preferences"] },
  { name: "for-you", paths: ["for-you", "for-you?section=releases", "for-you?section=taste", "for-you?section=recap"] },
  { name: "home", paths: [""] },
];

async function capture(page: Page, name: string, width: number, theme: string) {
  await page.evaluate(async () => { await document.fonts.ready; (document.activeElement as HTMLElement)?.blur(); window.scrollTo(0, 0); });
  const folder = `test-results/shared-design/${capturePhase}`;
  await mkdir(folder, { recursive: true });
  await page.screenshot({ path: `${folder}/${name}-${width}-${theme}.png`, fullPage: true });
}

async function sharedControls(page: Page, width: number) {
  const header = page.locator(".workspace-header");
  if (await page.locator(".activity-content").count()) {
    const scope = header.getByRole("group", { name: "Activity scope", exact: true });
    await expect(scope).toBeVisible();
    await expect(header.getByRole("button")).toHaveText(["Mine", "Everyone"]);
    await expect(scope.locator('[aria-pressed="true"]')).toHaveCount(1);
  } else await expect(header.getByRole("button")).toHaveCount(0);
  if (width === 1280) {
    expect(await page.evaluate(() => document.fonts.check('400 14px "Roboto"') && document.fonts.check('650 32px "Roboto Flex"'))).toBe(true);
    await expect(page.locator(".sidebar")).toHaveCSS("width", "200px");
    expect(await page.locator(".workspace-header h1").evaluate(element => ({ size: getComputedStyle(element).fontSize, weight: getComputedStyle(element).fontWeight }))).toEqual({ size: "32px", weight: "650" });
    await expect(page.locator('.desktop-navigation [aria-label="Main destinations"] a')).toHaveText(["Home", "Library", "For you", "Sources", "Activity", "Settings"]);
    await expect(page.locator('.desktop-navigation [aria-label="Help and connections"] a')).toHaveText(["Connect apps", "Report a problem"]);
    expect(await page.locator(".desktop-navigation a").evaluateAll(elements => elements.every(element => element.getBoundingClientRect().height <= 40))).toBe(true);
  }
  for (const input of await page.locator('.search-field input[type="search"]').all()) {
    if (!await input.isVisible()) continue;
    expect(await input.evaluate(element => { const style = getComputedStyle(element); return { pill: parseFloat(style.borderRadius) >= 22, border: style.borderTopWidth, background: style.backgroundColor }; })).toEqual({ pill: true, border: "1px", background: "rgba(0, 0, 0, 0)" });
  }
  for (const heading of await page.locator("table thead th").all()) {
    if (!await heading.isVisible()) continue;
    await expect(heading).toHaveCSS("text-transform", "uppercase");
    expect(await heading.evaluate(element => parseFloat(getComputedStyle(element).fontSize))).toBeCloseTo(10.88, 2);
  }
  for (const refresh of await page.locator('button[aria-label^="Refresh"]').all()) {
    if (!await refresh.isVisible()) continue;
    await expect(refresh).toHaveAttribute("data-variant", "text");
    await expect(refresh).toHaveAttribute("data-size", "icon");
    await expect(refresh).toHaveText("");
    await expect(refresh).toHaveAttribute("title", /Refresh/);
  }
  if (width === 390) {
    for (const control of await page.locator('.seg > :is(a, button, label), .tabs > :is(a, button)').all()) {
      if (!await control.isVisible()) continue;
      const bounds = await control.boundingBox();
      expect(bounds!.width).toBeGreaterThanOrEqual(44);
      expect(bounds!.height).toBeGreaterThanOrEqual(44);
    }
  }
  for (const count of await page.locator(".filter-segments .segment-count").all()) {
    await expect(count.locator("..")).toHaveAttribute("role", "radio");
    expect(await count.evaluate(element => parseFloat(getComputedStyle(element).borderRadius))).toBeGreaterThan(10);
  }
  for (const checkbox of await page.locator('.settings-surface [data-slot="checkbox"]').all()) {
    if (!await checkbox.isVisible()) continue;
    const bounds = await checkbox.boundingBox();
    expect(bounds!.height, "the checkbox stays square inside its larger touch target").toBeCloseTo(bounds!.width, 0);
    expect(await checkbox.evaluate(element => { const hit = getComputedStyle(element, "::after"); return parseFloat(hit.width) >= 44 && parseFloat(hit.height) >= 44; })).toBe(true);
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth), "viewport contains the shared surfaces").toBeLessThanOrEqual(1);
}

for (const width of [1280, 390]) for (const theme of ["dark", "light"] as const) for (const group of groups) {
  test(`Shared ${group.name} surfaces ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await page.route("**/*", route => ["127.0.0.1", "localhost"].includes(new URL(route.request().url()).hostname) ? route.continue() : route.abort());
    if (group.name === "settings") await settingsFixture(page);
    else if (["home", "for-you"].includes(group.name)) await homeFixture(page, true);
    else await panelFixture(page);
    for (const path of group.paths) {
      await page.goto(`/#/${path}`);
      await expect(page.locator(".workspace-header h1")).toBeVisible();
      await expect(page.locator(".skeleton-panel")).toHaveCount(0);
      await expect(page.locator('.workspace [aria-busy="true"]:not(.live-state)')).toHaveCount(0);
      await page.evaluate(async () => { await document.fonts.ready; });
      if (capturePhase === "after") {
        await sharedControls(page, width);
        if (["library/playlists", "library/matches", "library/cached", "library/kept"].includes(path)) {
          const list = page.locator(path.endsWith("playlists") ? ".library-rows" : path.endsWith("matches") ? ".mapping-rows" : path.endsWith("kept") ? ".promotion-list" : ".downloads-table-scroll");
          expect(await list.evaluate(element => ({ border: getComputedStyle(element).borderTopWidth, radius: getComputedStyle(element).borderRadius }))).toEqual({ border: "1px", radius: "14px" });
          const title = page.locator(path.endsWith("playlists") ? ".library-heading" : path.endsWith("matches") ? ".mapping-heading" : path.endsWith("kept") ? ".promotion-page-heading" : ".library-downloads-heading");
          await expect(title.locator("h2")).toHaveCSS("font-weight", "600");
          expect((await title.boundingBox())!.x).toBeCloseTo((await page.locator(".library-tabs").boundingBox())!.x, 0);
          if (path === "library/cached" && width === 1280) {
            expect(await page.locator(".library-download-row").first().locator("td").evaluateAll(cells => cells.every(cell => Math.abs(cell.getBoundingClientRect().bottom - cells[0].getBoundingClientRect().bottom) < 1))).toBe(true);
          }
          if (path === "library/kept" && width === 1280) {
            await expect(list.getByRole("columnheader")).toHaveText(["Song", "Source", "Quality", "Kept by", "Size", "Actions"]);
            await expect(list.locator(".kept-song-group > .library-download-row")).toHaveCount(2);
            const centers = await list.locator(".library-download-row").first().locator("td").evaluateAll(cells => cells.map(cell => {
              const box = cell.getBoundingClientRect(); return box.y + box.height / 2;
            }));
            expect(centers).toHaveLength(6);
            expect(Math.max(...centers) - Math.min(...centers)).toBeLessThanOrEqual(1);
          }
        }
        if (path === "settings/alerts") {
          const group = page.locator(".grouped-row").first();
          await expect(group).toHaveAttribute("data-tone", "warn");
          await expect(group.locator(".group-icon")).toBeVisible();
          await expect(group.locator(".group-reason")).toBeVisible();
          await expect(group.locator(".group-action").getByRole("link", { name: "Review sources" })).toBeVisible();
          await expect(group.locator(".group-members")).toBeHidden();
          await group.getByRole("button").focus();
          await page.keyboard.press("Enter");
          await expect(group.locator(".group-members")).toBeVisible();
          await page.keyboard.press("Enter");
        }
        for (const fold of await page.locator(".settings-fold > summary").all()) {
          if (!await fold.isVisible()) continue;
          const shown = await fold.evaluate(element => ({ marker: getComputedStyle(element).listStyleType, label: element.textContent?.trim() ?? "" }));
          expect(shown.marker).toBe("none");
          expect(shown.label).toMatch(/^(Advanced|Exact quality asked from each service|Preference window|See the details|Bring in your history|More filters|Copy details|Choose individual collections|See songs|Naming script|Couldn.t check)/);
        }
        if (path === "settings") {
          if (width === 1280) expect((await page.locator(".settings-nav").boundingBox())!.width).toBe(170);
        }
      }
      await capture(page, path.replace(/[/?=]/g, "-") || "home", width, theme);
      if (capturePhase === "after" && path === "library/playlists") {
        await page.getByRole("button", { name: "Import playlist", exact: true }).click();
        const dialog = page.getByRole("dialog", { name: "Import a playlist" });
        await expect(dialog).toBeVisible();
        await expect(dialog.locator(".dialog-actions").getByRole("button")).toHaveText(["Cancel", "Next"]);
        await expect(dialog.getByRole("button", { name: "Cancel", exact: true })).toHaveClass(/text-\[var\(--color-signal-text\)\]/);
        await capture(page, "import-playlist-dialog", width, theme);
        await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
        await expect(dialog).toBeHidden();
      }
      if (capturePhase === "after" && path === "library/cached") {
        await page.getByRole("button", { name: "Remove all…", exact: true }).click();
        const confirmation = page.getByRole("alertdialog");
        await expect(confirmation).toBeVisible();
        await expect(confirmation.getByRole("button", { name: "Cancel", exact: true })).toHaveClass(/text-\[var\(--color-signal-text\)\]/);
        await capture(page, "cached-remove-confirmation", width, theme);
        await confirmation.getByRole("button", { name: "Cancel", exact: true }).click();
        await expect(confirmation).toBeHidden();
      }
    }
    expect(await page.pageErrors()).toEqual([]);
  });
}

for (const width of [1280, 390]) for (const theme of ["dark", "light"] as const) {
  test(`Grouped repeats keep repair controls visible ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await panelFixture(page);
    const destinationId = "33333333-3333-4333-8333-333333333333";
    await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: { accounts: [{ id: destinationId, providerId: "listenbrainz", displayName: "Listening history", ownerDisplayName: "Listener", scope: "Personal", enabled: true, canManage: true, revision: 1, secret: { configured: true, revoked: false } }], audienceUsers: [] } }));
    await page.route("**/api/admin/scrobbling/deliveries?*", route => route.fulfill({ json: { items: Array.from({ length: 6 }, (_, index) => ({ id: `listen-${index}`, revision: 1, targetId: "listenbrainz", state: "permanentfailure", providerCode: "destination-unbound", safeMessage: "The destination needs confirming", requiresReauthentication: false, sendCount: 0, canResend: true, suggestedDestinationAccountId: destinationId, availableDestinationAccountIds: [destinationId], updatedAt: "2026-11-04T12:00:00Z", submission: { title: `Evening song ${index + 1}`, artist: "River", occurredAt: "2026-11-04T12:00:00Z" } })), hasMore: false } }));
    await page.goto("/#/sources/accounts");
    const group = page.locator(".delivery-groups .grouped-row");
    await expect(group).toHaveCount(1);
    await expect(group.locator(".group-count")).toHaveText("6");
    await expect(group.locator(".group-reason")).toHaveText("The destination needs confirming");
    await expect(group.locator(".group-members")).toBeHidden();
    await expect(group.getByRole("button", { name: "Confirm destination", exact: true })).toHaveAttribute("data-variant", "outline");
    await expect(group.getByRole("button", { name: "Retry all", exact: true })).toHaveAttribute("data-variant", "text");
    await group.locator(".group-toggle").focus();
    await page.keyboard.press("Enter");
    await expect(group.locator(".listen-row")).toHaveCount(6);
    await expect(group.locator(".group-members")).toBeVisible();
    await group.locator(".group-chevron").click();
    await expect(group.locator(".group-members")).toBeHidden();
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    await capture(page, "sources-grouped-repeats", width, theme);
    expect(await page.pageErrors()).toEqual([]);
  });
}

const themeVariants = [[1280, "light"], [1280, "dark"], [390, "light"], [390, "dark"]] as const;
const transparent = "rgba(0, 0, 0, 0)";

async function themePage(page: Page, width: number, theme: "light" | "dark") {
  await page.setViewportSize({ width, height: 900 });
  await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
  await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
}

async function themeShot(page: Page, name: string, width: number, theme: string, target?: Locator) {
  await page.evaluate(async () => { await document.fonts.ready; (document.activeElement as HTMLElement | null)?.blur(); });
  const path = test.info().outputPath(`${name}-${width}-${theme}.png`);
  if (target) { await target.scrollIntoViewIfNeeded(); await target.screenshot({ path }); }
  else await page.screenshot({ path });
}

const bounds = async (locator: Locator) => (await locator.boundingBox())!;
const css = (locator: Locator, property: string) => locator.evaluate((element, name) => getComputedStyle(element).getPropertyValue(name), property);
// A day's rows start right under its heading, measured in one frame.
const headingGap = (day: Locator) => day.evaluate(element => element.querySelector(".event-log-list")!.getBoundingClientRect().top - element.querySelector("h2")!.getBoundingClientRect().bottom);

// The shared fold: a CSS chevron and no native marker, wherever it sits.
async function expectFold(fold: Locator, placement: "card" | "standalone" | "flush") {
  await expect(fold).toHaveAttribute("data-placement", placement);
  const summary = fold.locator(":scope > summary");
  expect(await summary.evaluate(element => ({ marker: getComputedStyle(element).listStyleType, chevron: getComputedStyle(element, "::after").borderRightStyle }))).toEqual({ marker: "none", chevron: "solid" });
  if (placement === "standalone") expect(await fold.evaluate(element => { const style = getComputedStyle(element); return [style.borderLeftWidth, style.borderBottomWidth, style.borderRadius]; })).toEqual(["1px", "1px", "14px"]);
  if (placement === "flush") {
    expect(await css(fold, "background-color")).toBe(transparent);
    expect(await css(summary, "padding-left")).toBe("0px");
  }
}

// Opens a fold and waits out its height transition, which still runs with reduced motion.
async function openFold(fold: Locator) {
  if (await fold.getAttribute("open") === null) await fold.locator(":scope > summary").click();
  await expect(fold).toHaveAttribute("open", "");
  await fold.page().waitForTimeout(350);
}

// Filter rows scroll sideways on phones; the side with more filters fades out.
async function expectEdgeFade(row: Locator, width: number) {
  const overflows = await row.evaluate(element => element.scrollWidth > element.clientWidth + 1);
  const mask = await css(row, "mask-image");
  if (overflows) {
    expect(mask).toContain("linear-gradient");
    const ends = await row.evaluate(async element => {
      const read = async (left: number) => { element.scrollLeft = left; await new Promise(requestAnimationFrame); await new Promise(requestAnimationFrame); return getComputedStyle(element).maskImage; };
      const initial = element.scrollLeft;
      const start = await read(0), end = await read(element.scrollWidth), middle = await read((element.scrollWidth - element.clientWidth) / 2);
      await read(initial);
      return new Set([start, middle, end]).size;
    });
    expect(ends).toBe(3);
  } else expect(mask).toBe("none");
  if (width === 390) expect(overflows).toBe(true);
}

for (const [width, theme] of themeVariants) {
  test(`Theme fixes: Activity ${width} ${theme}`, async ({ page }) => {
    await themePage(page, width, theme);
    await panelFixture(page);
    const timestamp = "2026-11-04T12:00:00Z";
    await page.route("**/api/admin/ui/activity*", route => route.fulfill({ json: { items: [
      { id: "event", kind: "playlist", label: "Playlist updated", detail: "Evening mix is ready.", state: "succeeded", occurredAt: timestamp, actorDisplayName: "Listener", source: "Jellyfin", providerId: "spotify", jobId: "66666666-6666-4666-8666-666666666666" },
      { id: "problem", kind: "extension", label: "Source unavailable", detail: "Try again later.", state: "failed", severity: "error", occurredAt: "2026-11-03T12:00:00Z", source: "Music source", providerId: "qobuz" },
    ], hasMore: true, nextCursor: "older", nextCursorId: "older" } }));
    await page.route("**/api/admin/jobs?*", route => {
      const queue = new URL(route.request().url()).searchParams.get("view") === "queue";
      return route.fulfill({ json: { jobs: queue
        ? [{ id: "66666666-6666-4666-8666-666666666666", type: "playlist.materialize", displayName: "Update playlist", state: "Deferred", ownerName: "Listener", summary: "Progress saved.", availableAt: "2026-11-04T13:00:00Z", startedAt: timestamp, updatedAt: timestamp, createdAt: timestamp, canCancel: true, attemptCount: 1 }]
        : [{ id: "77777777-7777-4777-8777-777777777777", type: "library.index", displayName: "Refresh library index", state: "Succeeded", ownerName: "Listener", startedAt: timestamp, completedAt: timestamp, updatedAt: timestamp, createdAt: timestamp, attemptCount: 1 }],
      progress: [], hasMore: false } });
    });

    await page.goto("/#/activity");
    const history = page.locator(".event-log-panel");
    await expect(history.locator(".event-log-group").first()).toBeVisible();
    // A long history keeps "Load earlier events" usable once its pages have loaded.
    await expect(page.getByRole("button", { name: "Load earlier events", exact: true })).toBeEnabled();
    const filters = page.getByRole("radiogroup", { name: "History filters", exact: true });
    if (width === 1280) for (const filter of await filters.getByRole("radio").all()) expect((await bounds(filter)).height).toBeLessThanOrEqual(36);
    await expectEdgeFade(filters, width);
    const more = page.locator(".event-more-filters");
    await expectFold(more, "standalone");
    await openFold(more);
    const day = history.locator(".event-log-day").first();
    expect(await headingGap(day)).toBeCloseTo(0, 0);
    await themeShot(page, "01-history-fold", width, theme, history);
    await themeShot(page, "02-filter-height", width, theme, page.locator(".event-log-toolbar"));
    await themeShot(page, "10-seg-fade-history", width, theme, page.locator(".event-log-toolbar"));
    if (width === 390) {
      await filters.evaluate(element => { element.scrollLeft = (element.scrollWidth - element.clientWidth) / 2; });
      await themeShot(page, "10-seg-fade-history-scrolled", width, theme, page.locator(".event-log-toolbar"));
      await filters.evaluate(element => { element.scrollLeft = 0; });
    }

    await page.locator(".event-log-group .row-action").first().click();
    const panel = page.locator("[data-side-panel]");
    await expect(panel).toBeVisible();
    const body = panel.locator(".panel-body");
    expect((await bounds(panel.locator(".event-child").first())).x).toBeCloseTo((await bounds(body)).x + 16, 0);
    const technical = panel.locator(".event-technical");
    await expectFold(technical, "flush");
    await openFold(technical);
    await expect(technical.getByText("Job reference", { exact: true })).toBeVisible();
    await themeShot(page, "11-history-panel", width, theme);
    await panel.getByRole("button", { name: "Close", exact: true }).click();
    await expect(panel).toHaveCount(0);

    await page.getByRole("tab", { name: "Plays", exact: true }).click();
    const plays = page.locator(".plays-list");
    await expect(plays.locator(".play-row").first()).toBeVisible();
    const playDay = plays.locator(".event-log-day").first();
    expect(await css(playDay.locator(".event-log-list"), "border-top-left-radius")).toBe("0px");
    expect(await headingGap(playDay)).toBeCloseTo(0, 0);
    await themeShot(page, "03-plays", width, theme, page.locator(".activity-plays"));

    await page.getByRole("tab", { name: "Jobs", exact: true }).click();
    const jobs = page.locator(".job-days");
    await expect(jobs.locator(".event-log-day")).toHaveCount(2);
    expect(await jobs.evaluate(element => { const style = getComputedStyle(element); return [style.borderTopWidth, style.borderRadius]; })).toEqual(["1px", "16px"]);
    const jobFilters = page.locator(".activity-work > .settings-fold");
    await expectFold(jobFilters, "standalone");
    await openFold(jobFilters);
    const state = jobFilters.getByRole("button", { name: "Job state", exact: true });
    expect((await bounds(state)).x).toBeGreaterThanOrEqual((await bounds(jobFilters)).x + 16);
    await themeShot(page, "01-jobs-fold", width, theme, page.locator(".activity-work"));
    await page.locator(".job-row .row-action").first().click();
    const jobTechnical = panel.locator(".event-technical");
    await expectFold(jobTechnical, "flush");
    await openFold(jobTechnical);
    await themeShot(page, "05-technical-details", width, theme);
    await panel.getByRole("button", { name: "Close", exact: true }).click();

    await page.getByRole("tab", { name: "Tasks", exact: true }).click();
    await page.locator(".task-open").first().click();
    const repeat = panel.getByRole("button", { name: "Repeat", exact: true });
    await expect(repeat).toBeVisible();
    const controls = [repeat, panel.getByLabel("Time", { exact: true }), panel.getByLabel("Time zone", { exact: true })];
    const boxes = await Promise.all(controls.map(bounds));
    for (const box of boxes) { expect(box.x).toBeCloseTo(boxes[0].x, 0); expect(box.width).toBeCloseTo(boxes[0].width, 0); }
    await themeShot(page, "09-task-panel", width, theme);
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    expect(await page.pageErrors()).toEqual([]);
  });

  test(`Theme fixes: Activity attention ${width} ${theme}`, async ({ page }) => {
    await themePage(page, width, theme);
    await panelFixture(page);
    let failures = 0;
    await page.route("**/api/admin/updates/stream*", route => route.fulfill({ contentType: "text/event-stream",
      body: `retry: 250\nevent: update\ndata: {"eventId":"household-failure-${++failures}","resource":"job","resourceId":"other","requiresAdministrator":true}\n\n` }));
    await page.goto("/#/activity?view=tasks");
    const banner = page.getByRole("status").filter({ hasText: "Household work needs attention." });
    await expect(banner).toBeVisible();
    const action = banner.getByRole("button", { name: "Review household work", exact: true });
    expect((await bounds(action)).width).toBeLessThan(260);
    expect(await css(banner, "background-color")).toBe(await page.evaluate(() => { const probe = document.createElement("div"); probe.style.background = "var(--warning-container)"; document.body.append(probe); const value = getComputedStyle(probe).backgroundColor; probe.remove(); return value; }));
    await themeShot(page, "04-tasks-banner", width, theme, page.locator(".activity-content"));
  });

  test(`Theme fixes: Library ${width} ${theme}`, async ({ page }) => {
    await themePage(page, width, theme);
    await panelFixture(page);
    const panel = page.locator("[data-side-panel]");

    await page.goto("/#/library/playlists");
    const search = page.locator(".library-search input[type=search]");
    await expect(search).toBeVisible();
    for (const trigger of await page.locator(".library-search .select-trigger").all()) expect(await css(trigger, "background-color")).toBe(await css(search, "background-color"));
    await themeShot(page, "08-selects-playlists", width, theme, page.locator(".library-filters"));
    await page.getByRole("button", { name: "Open Evening mix playlist details" }).click();
    const songs = panel.locator(".playlist-more");
    await expectFold(songs, "flush");
    expect((await bounds(songs.locator("summary"))).x).toBeCloseTo((await bounds(panel.locator(".panel-body"))).x + 16, 0);
    const advanced = panel.locator(".sync-advanced");
    await expectFold(advanced, "flush");
    await openFold(advanced);
    await expect(advanced.getByText("Cron schedule", { exact: true })).toBeVisible();
    expect(await css(advanced.locator("summary"), "font-size")).toBe("14px");
    await advanced.scrollIntoViewIfNeeded();
    await themeShot(page, "05-playlist-advanced", width, theme);
    await openFold(songs);
    await songs.scrollIntoViewIfNeeded();
    await themeShot(page, "01-playlist-panel", width, theme);
    await panel.getByRole("button", { name: "Close", exact: true }).click();

    await page.goto("/#/library/matches");
    const select = page.locator(".matches-toolbar").getByRole("button", { name: /^(Select|Done selecting)$/ });
    await expect(select).toBeVisible();
    const sort = page.locator(".matches-toolbar .select-trigger").first();
    expect((await bounds(select)).height).toBeCloseTo((await bounds(sort)).height, 0);
    expect(await css(sort, "background-color")).toBe(transparent);
    await themeShot(page, "11-matches-toolbar", width, theme, page.locator(".matches-toolbar"));
    await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
    const identity = panel.locator(".identity-details");
    await expectFold(identity, "flush");
    await openFold(identity);
    await identity.scrollIntoViewIfNeeded();
    await themeShot(page, "05-matches-panel", width, theme);
    await panel.getByRole("button", { name: "Close", exact: true }).click();

    await page.goto("/#/library/cached");
    const cachedSearch = page.locator(".downloads-search-sort input[type=search]");
    await expect(cachedSearch).toBeVisible();
    for (const trigger of await page.locator(".downloads-search-sort .select-trigger").all()) expect(await css(trigger, "background-color")).toBe(await css(cachedSearch, "background-color"));
    await themeShot(page, "08-selects-cached", width, theme, page.locator(".library-downloads-heading").locator(".."));
    await page.getByRole("button", { name: "Open song details for Take On Me", exact: true }).first().click();
    const advancedSong = panel.locator(".settings-fold");
    await expectFold(advancedSong, "flush");
    await openFold(advancedSong);
    await themeShot(page, "01-cached-panel", width, theme);
    await panel.getByRole("button", { name: "Close", exact: true }).click();

    await page.goto("/#/library/kept");
    await page.getByRole("button", { name: "Open song details for Take On Me", exact: true }).first().click();
    const copy = panel.locator(".copy-facts");
    await expectFold(copy, "flush");
    await openFold(copy);
    await themeShot(page, "01-kept-panel", width, theme);
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    expect(await page.pageErrors()).toEqual([]);
  });

  test(`Theme fixes: Sources and help ${width} ${theme}`, async ({ page }) => {
    await themePage(page, width, theme);
    await panelFixture(page);
    await page.goto("/#/sources/services");
    const sourceSearch = page.locator(".source-toolbar input[type=search]");
    await expect(sourceSearch).toBeVisible();
    expect(await css(page.locator(".source-toolbar .select-trigger"), "background-color")).toBe(await css(sourceSearch, "background-color"));
    await themeShot(page, "08-selects-sources", width, theme, page.locator(".source-toolbar"));
    await page.goto("/#/sources/services/qobuz");
    const panel = page.locator("[data-side-panel]");
    const more = panel.locator(".source-more");
    await expectFold(more, "flush");
    await openFold(more);
    const content = (await bounds(panel.locator(".panel-body"))).x + 16;
    expect((await bounds(more.locator("summary"))).x).toBeCloseTo(content, 0);
    await expect(more.locator(".service-description")).toBeVisible();
    expect((await bounds(more.locator(".service-description"))).x).toBeCloseTo(content, 0);
    for (const card of await more.locator(".service-advanced > .settings-card").all()) {
      expect(await css(card, "border-left-width")).toBe("1px");
      expect((await bounds(card)).x).toBeCloseTo(content, 0);
    }
    await more.scrollIntoViewIfNeeded();
    await themeShot(page, "05-source-more", width, theme);
    await panel.getByRole("button", { name: "Close", exact: true }).click();

    await page.goto("/#/sources/accounts");
    const connect = page.getByRole("button", { name: "Connect account", exact: true });
    await expect(connect).toBeVisible();
    expect((await bounds(connect)).height).toBeLessThanOrEqual(44);
    await themeShot(page, "10-connect-account", width, theme, page.locator(".sources-heading"));

    await page.goto("/#/connect");
    const address = page.locator(".connection-value input").first();
    await expect(address).toBeVisible();
    const filled = await page.evaluate(() => { const probe = document.createElement("div"); probe.style.background = "var(--color-panel-raised)"; document.body.append(probe); const value = getComputedStyle(probe).backgroundColor; probe.remove(); return value; });
    for (const field of await page.locator(".connection-value input").all()) expect(await css(field, "background-color")).toBe(filled);
    await themeShot(page, "11-connect-fields", width, theme, address.locator("xpath=ancestor::*[contains(@class, 'settings-card') or self::section][1]"));

    await page.goto("/#/report");
    const form = page.getByRole("form", { name: "Problem report" });
    const footer = form.locator(".settings-fields > footer");
    await expect(footer).toBeVisible();
    const formBox = await bounds(form), footerBox = await bounds(footer);
    expect(footerBox.x + footerBox.width).toBeCloseTo(formBox.x + formBox.width, 0);
    await themeShot(page, "11-report-footer", width, theme, form);
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    expect(await page.pageErrors()).toEqual([]);
  });

  test(`Theme fixes: Settings ${width} ${theme}`, async ({ page }) => {
    await themePage(page, width, theme);
    await settingsFixture(page);
    await page.route("**/api/admin/alerts", route => route.fulfill({ json: { channels: [], deliveries: [], revision: 1, permittedDestinations: [],
      thresholds: { warningBytes: 5 * 1024 ** 3, warningPercent: 5, criticalBytes: 1024 ** 3, criticalPercent: 1, hysteresisBytes: 1024 ** 3, hysteresisPercent: 1, cooldownMinutes: 30 } } }));

    await page.goto("/#/settings/playback");
    const row = page.locator('[data-setting="MATCHING_LOCAL_PREFERENCE_PERCENT"]');
    const toggle = row.getByRole("switch", { name: "Prefer your own music when unsure", exact: true });
    await expect(toggle).toBeVisible();
    const title = row.getByText("Prefer your own music when unsure", { exact: true });
    if (width === 1280) expect(Math.abs((await bounds(toggle)).y + (await bounds(toggle)).height / 2 - ((await bounds(row)).y + (await bounds(row)).height / 2))).toBeLessThanOrEqual(2);
    else expect((await bounds(toggle)).y).toBeGreaterThanOrEqual((await bounds(title)).y - 12);
    const preference = page.locator(".settings-fold").filter({ has: page.locator("summary", { hasText: /^Preference window$/ }) });
    await expectFold(preference, "card");
    await openFold(preference);
    await expect(page.getByLabel("Preference window (points)", { exact: true })).toHaveValue("7");
    await themeShot(page, "05-preference-window", width, theme, row.locator("xpath=ancestor::*[contains(@class, 'settings-card')][1]"));

    await page.goto("/#/settings/discovery");
    const sound = page.locator('section[aria-label="Mixes from sound"]');
    await expect(sound).toBeVisible();
    expect(await css(sound.locator(":scope > header + .settings-row"), "border-top-width")).toBe("1px");
    const edit = page.getByRole("button", { name: "Edit connection", exact: true });
    expect((await bounds(edit)).height).toBeCloseTo((await bounds(page.getByRole("button", { name: "Refresh sound analysis", exact: true }))).height, 0);
    await themeShot(page, "11-discovery", width, theme, page.locator(".settings-content"));

    await page.goto("/#/settings/alerts");
    const disk = page.getByRole("region", { name: "Disk alert thresholds", exact: true });
    await expect(disk).toBeVisible();
    expect(await css(disk.locator(":scope > header + form"), "border-top-width")).toBe("1px");
    await themeShot(page, "11-alerts", width, theme, disk);

    await page.goto("/#/settings/storage");
    const legend = page.getByRole("list", { name: "Status breakdown", exact: true });
    await expect(legend).toBeVisible();
    const free = page.locator(".status-gauge > span").last();
    expect(await css(free, "background-color")).toBe(transparent);
    await themeShot(page, "06-storage-gauge", width, theme, page.locator(".page-status").first());
    const storageFold = page.locator(".settings-content > .settings-fold");
    await expectFold(storageFold, "standalone");
    await openFold(storageFold);
    const verify = page.getByRole("button", { name: "Verify saved files", exact: true });
    await expect(verify).toBeVisible();
    expect((await bounds(page.getByRole("link", { name: "Open Activity", exact: true }))).height).toBeCloseTo((await bounds(verify)).height, 0);
    await themeShot(page, "11-storage-verification", width, theme, page.getByRole("article", { name: "Saved-file verification", exact: true }));
    await themeShot(page, "01-settings-storage", width, theme);

    await page.goto("/#/settings/health");
    const diagnostics = page.locator(".settings-content > .settings-fold");
    await expectFold(diagnostics, "standalone");
    await expectFold(page.getByRole("form", { name: "Health monitoring", exact: true }).locator(".settings-fold"), "card");
    await diagnostics.scrollIntoViewIfNeeded();
    await themeShot(page, "01-settings-health", width, theme);
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    expect(await page.pageErrors()).toEqual([]);
  });

  test(`Theme fixes: For you ${width} ${theme}`, async ({ page }) => {
    await themePage(page, width, theme);
    await homeFixture(page, true);
    await page.route("**/api/admin/releases", route => route.fulfill({ json: {
      entries: [{ id: "release", title: "Open skies", artist: "North", releaseDate: "2026-10-29", type: "album", sources: [], isExplicit: false, edition: "" }], excluded: [],
      coverage: ["Cedar", "Willow"].map(name => ({ artist: { providerId: "catalog", artistId: name.toLowerCase(), displayName: name }, status: "unknown", checkedAt: null })),
    } }));

    await page.goto("/#/for-you?section=taste");
    const taste = page.locator(".taste-details");
    await expectFold(taste, "standalone");
    await openFold(taste);
    await themeShot(page, "01-taste-fold", width, theme, taste);

    await page.goto("/#/for-you?section=recap");
    const imports = page.locator(".recap-imports");
    await expectFold(imports, "standalone");
    const period = page.locator(".filter-segments").first();
    await expect(period).toBeVisible();
    await expectEdgeFade(period, width);
    await themeShot(page, "10-seg-fade-recap", width, theme, page.locator(".stats-toolbar"));
    await openFold(imports);
    await themeShot(page, "05-recap-imports", width, theme, imports);

    await page.goto("/#/for-you?section=releases");
    const unchecked = page.locator(".checking-note .settings-fold");
    await expectFold(unchecked, "flush");
    await openFold(unchecked);
    await expect(unchecked.getByText("Willow", { exact: true })).toBeVisible();
    await themeShot(page, "05-releases-check", width, theme, page.locator(".checking-note"));
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    expect(await page.pageErrors()).toEqual([]);
  });

  test(`Theme fixes: boot screen ${width} ${theme}`, async ({ page }) => {
    await themePage(page, width, theme);
    let release!: () => void;
    const held = new Promise<void>(resolve => { release = resolve; });
    await page.route("**/api/admin/auth/me", async route => { await held; await route.fulfill({ json: { authenticated: false } }); });
    await page.goto("/");
    const core = page.locator(".signal-boot-core");
    await expect(core).toBeVisible();
    const onSignal = await page.evaluate(() => { const probe = document.createElement("div"); probe.style.color = "var(--color-on-signal)"; document.body.append(probe); const value = getComputedStyle(probe).color; probe.remove(); return value; });
    expect(await css(core, "color")).toBe(onSignal);
    await themeShot(page, "07-boot", width, theme, page.locator(".signal-boot-console"));
    release();
  });
}
