import { expect, test, type Page } from "@playwright/test";
import { panelFixture } from "./shared-design-fixtures";

const playlistId = "22222222-2222-4222-8222-222222222222";
const widthKey = "allstarr.panel-width.library/playlists";

async function fixture(page: Page, width = 1280, serviceMix = false) {
  await page.setViewportSize({ width, height: 900 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  await page.addInitScript(() => localStorage.setItem("allstarr.theme", "dark"));
  await panelFixture(page);
  const playlist = { id: playlistId, name: "Evening mix", enabled: true, revision: 1,
    sourceProviderId: serviceMix ? "listenbrainz" : "spotify", sourcePlaylistId: serviceMix ? "createdfor:weekly" : "evening",
    targetProtocol: "jellyfin", targetPlaylistId: "evening-copy", importMode: "linked", mode: "hybrid",
    trackCount: 2, matchedCount: 2, playableCount: 2, materializedCount: 2, routeCoverage: [{ providerId: "local", count: 1 }],
    metrics: { total: 2, matched: 2, unresolved: 0, review: 0, rejected: 0, playable: 2, materialized: 2 } };
  await page.route("**/api/admin/playlist-links", route => route.fulfill({ json: { playlistLinks: [playlist] } }));
  await page.route(`**/api/admin/playlist-links/${playlistId}?*`, route => route.fulfill({ json: {
    ...playlist, localCount: 1, reviewCount: 0, unresolvedCount: 0, durationMs: 360000, unknownDurationCount: 0,
    tracks: ["Local song", "External song"].map((title, index) => ({ title, artists: ["North"], album: "Open skies", position: index + 1,
      sourcePosition: index, externalSnapshotId: "", durationMs: 180000, routeKind: index ? "external" : "local",
      targetStatus: index ? "streamed" : "included", matchState: "accepted", providerRoutes: [
        { providerId: "apple-musickit", externalId: `apple-${index}`, pinned: false },
        { providerId: "qobuz", externalId: `qobuz-${index}`, pinned: false },
      ] })),
  } }));
  await page.goto("/#/library/playlists");
}

async function open(page: Page) {
  const panel = page.locator("[data-side-panel]");
  if (!await panel.isVisible()) await page.getByRole("button", { name: "Open Evening mix playlist details", exact: true }).click();
  await expect(panel).toBeVisible();
  await expect(panel.getByText("All 2 matches are settled", { exact: true })).toBeVisible();
  const fold = panel.locator(".playlist-more");
  if (await fold.getAttribute("open") !== null) await fold.locator("summary").click();
  await expect(fold).not.toHaveAttribute("open");
  return panel;
}

async function capture(page: Page, name: string) {
  await page.evaluate(async () => { await document.fonts.ready; (document.activeElement as HTMLElement)?.blur(); });
  await page.screenshot({ path: test.info().outputPath(`${name}-dark.png`) });
}

test("Panel drag and keyboard sizing persist and reset independently of content", async ({ page }) => {
  await fixture(page);
  const panel = await open(page), handle = panel.getByRole("slider", { name: "Resize panel", exact: true });
  await expect(panel).toHaveCSS("width", "420px");
  await handle.focus();
  await page.keyboard.press("ArrowLeft");
  await expect(panel).toHaveCSS("width", "452px");
  await page.keyboard.press("ArrowRight");
  await expect(panel).toHaveCSS("width", "420px");
  await page.keyboard.press("End");
  await expect(panel).toHaveCSS("width", "668px");
  expect(await page.locator(".library-rows").evaluate(element => element.getBoundingClientRect().width)).toBeGreaterThanOrEqual(360);
  await page.keyboard.press("Home");
  const box = (await handle.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2 - 80, box.y + box.height / 2, { steps: 4 });
  await page.mouse.up();
  await expect(panel).toHaveCSS("width", "500px");
  expect(await page.evaluate(key => localStorage.getItem(key), widthKey)).toBe("500");
  await page.keyboard.press("Escape");
  await expect(panel).toBeHidden();
  await page.reload();
  await open(page);
  await expect(panel).toHaveCSS("width", "500px");
  await page.keyboard.press("Escape");
  await page.goto("/#/sources/services");
  await page.getByRole("link", { name: "Open Qobuz", exact: true }).click();
  await expect(page.locator("[data-side-panel]")).toHaveCSS("width", "420px");
  await page.keyboard.press("Escape");
  await page.goto("/#/library/playlists");
  await open(page);
  await expect(panel).toHaveCSS("width", "500px");
  await panel.getByRole("slider", { name: "Resize panel", exact: true }).dblclick();
  await expect(panel).toHaveCSS("width", "420px");
  expect(await page.evaluate(key => localStorage.getItem(key), widthKey)).toBeNull();
  await capture(page, "default-1280");
});

for (const width of [1280, 1024]) test(`Panel expansion restores its width and keeps song disclosure stable at ${width}`, async ({ page }) => {
  await fixture(page, width);
  const panel = await open(page);
  const initial = (await panel.boundingBox())!;
  await capture(page, `default-${width}`);
  await panel.getByRole("button", { name: "Expand", exact: true }).click();
  await expect(panel).toHaveAttribute("data-expanded", "true");
  await expect(panel.getByRole("button", { name: "Restore", exact: true })).toBeFocused();
  const expanded = (await panel.boundingBox())!;
  const sidebar = (await page.locator(".sidebar").boundingBox())!;
  expect(expanded.x).toBe(sidebar.width);
  expect(expanded.width).toBe(width - sidebar.width);
  await expect(page.locator(".library-rows")).toBeHidden();
  await expect(page.locator(".workspace")).toHaveAttribute("inert", "");
  const fold = panel.locator(".playlist-more > summary");
  const listWidth = await page.locator(".library-rows").evaluate(element => element.getBoundingClientRect().width);
  const factsTop = await panel.locator(".playlist-kpis").evaluate(element => (element as HTMLElement).offsetTop);
  await fold.click();
  await expect(panel.getByRole("table", { name: "Evening mix tracks", exact: true })).toBeVisible();
  expect((await panel.boundingBox())!.width).toBe(expanded.width);
  expect(await page.locator(".library-rows").evaluate(element => element.getBoundingClientRect().width)).toBe(listWidth);
  expect(await panel.locator(".playlist-kpis").evaluate(element => (element as HTMLElement).offsetTop)).toBe(factsTop);
  if (width === 1280) expect(await panel.locator(".track-scroll").evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
  await panel.getByRole("table", { name: "Evening mix tracks", exact: true }).scrollIntoViewIfNeeded();
  await capture(page, `expanded-${width}`);
  await expect(panel).toHaveAttribute("data-expanded", "true");
  await panel.getByRole("button", { name: "Restore", exact: true }).click();
  await expect(panel).toHaveAttribute("data-expanded", "false");
  expect((await panel.boundingBox())!.width).toBe(initial.width);
  const before = await page.locator(".library-rows").evaluate(element => element.getBoundingClientRect().width);
  await fold.click(); await fold.click();
  expect((await panel.boundingBox())!.width).toBe(initial.width);
  expect(await page.locator(".library-rows").evaluate(element => element.getBoundingClientRect().width)).toBe(before);
  await panel.getByRole("button", { name: "Expand", exact: true }).click();
  await panel.getByRole("button", { name: "Restore", exact: true }).focus();
  await page.keyboard.press("Escape");
  await expect(panel).toBeHidden();
  await expect(page.locator(".library-rows")).toBeVisible();
  await expect(page.getByRole("button", { name: "Open Evening mix playlist details", exact: true })).toBeFocused();
  expect(await page.locator(".workspace").evaluate(element => (element as HTMLElement).inert)).toBe(false);
});

test("Panel sizing tolerates unavailable browser storage", async ({ page }) => {
  await page.addInitScript(() => {
    const get = Storage.prototype.getItem, set = Storage.prototype.setItem;
    Storage.prototype.getItem = function(key) { if (key.startsWith("allstarr.panel-width.")) throw new Error("Storage unavailable"); return get.call(this, key); };
    Storage.prototype.setItem = function(key, value) { if (key.startsWith("allstarr.panel-width.")) throw new Error("Storage unavailable"); return set.call(this, key, value); };
  });
  await fixture(page);
  const panel = await open(page);
  await panel.getByRole("slider", { name: "Resize panel", exact: true }).focus();
  await page.keyboard.press("ArrowLeft");
  await expect(panel).toHaveCSS("width", "452px");
  expect(await page.pageErrors()).toEqual([]);
});

test("Phone sheets keep their existing width without desktop panel controls", async ({ page }) => {
  await fixture(page, 390);
  const panel = await open(page);
  await expect(panel).toHaveCSS("width", "390px");
  await expect(panel.getByRole("button", { name: "Expand", exact: true })).toHaveCount(0);
  await expect(panel.getByRole("slider", { name: "Resize panel", exact: true })).toHaveCount(0);
});

test("Playlist routes use provider artwork and shared control surfaces", async ({ page }) => {
  await fixture(page, 1280, true);
  const panel = await open(page);
  const remove = panel.getByRole("button", { name: "Remove from my apps", exact: true });
  await expect(remove).toBeEnabled();
  await expect(remove).toHaveAttribute("data-variant", "destructive-text");
  await expect(remove).toHaveCSS("opacity", "1");
  await remove.click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Cancel", exact: true }).click();
  await panel.getByRole("button", { name: "Expand", exact: true }).click();
  await panel.locator(".playlist-more > summary").click();
  await expect(panel.locator(".playlist-more")).toHaveAttribute("open", "");
  const controls = panel.locator('.track-toolbar .select-trigger');
  const styles = await controls.evaluateAll(elements => elements.map(element => { const s = getComputedStyle(element); return [s.minHeight, s.borderRadius, s.borderColor, s.backgroundColor, s.fontSize]; }));
  expect(styles).toHaveLength(3);
  expect(styles[1]).toEqual(styles[0]); expect(styles[2]).toEqual(styles[0]);
  const rows = await controls.evaluateAll(elements => elements.map(element => element.getBoundingClientRect().y));
  expect(Math.max(...rows) - Math.min(...rows)).toBeLessThanOrEqual(1);
  expect(await controls.evaluateAll(elements => elements.every(element => {
    const value = element.querySelector("[data-select-value]");
    return !value || value.scrollWidth <= value.clientWidth;
  })), "filter labels remain fully visible").toBe(true);
  const controlBoxes = await controls.evaluateAll(elements => elements.map(element => element.getBoundingClientRect().toJSON()));
  expect(Math.max(...controlBoxes.map(box => box.height)) - Math.min(...controlBoxes.map(box => box.height))).toBeLessThanOrEqual(1);
  expect(controlBoxes[2].width).toBeLessThanOrEqual(140);
  expect((await panel.getByRole("searchbox", { name: "Filter tracks", exact: true }).boundingBox())!.width).toBeGreaterThanOrEqual(200);
  const route = panel.getByRole("button", { name: "Routes for Local song", exact: true });
  await expect(route).toContainText("Jellyfin");
  await expect(route).toContainText("In your library");
  await expect(route).toBeVisible();
  await route.scrollIntoViewIfNeeded();
  await capture(page, "routes-before-open-1280");
  expect(await route.evaluate(element => {
    const box = element.getBoundingClientRect(), hit = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2);
    return element === hit || element.contains(hit);
  }), "the visible route control receives pointer events").toBe(true);
  await route.click();
  const popover = page.locator(".track-routes-panel");
  await expect(popover).toHaveClass(/bits-popover/);
  await expect(popover.getByText("Jellyfin", { exact: true })).toBeVisible();
  await expect(popover.getByText("Apple Music", { exact: true })).toBeVisible();
  for (const image of await popover.locator(".provider-art img").all()) await expect(image).toBeVisible();
  await capture(page, "routes-1280");
});
