import { expect, test, type Page, type Route } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";
import { expectPageSurface, expectSingleRowFooter, expectSubtleSelection, expectToolbarLayout } from "./library-layout";

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

for (const viewport of [{ width: 1280, theme: "light" }, { width: 1280, theme: "dark" }, { width: 390, theme: "light" }] as const) {
  for (const storage of ["cached", "kept"] as const) {
    test(`${storage} table and song panel at ${viewport.width}px ${viewport.theme}`, async ({ page }) => {
      await page.setViewportSize({ width: viewport.width, height: 900 });
      await page.emulateMedia({ colorScheme: viewport.theme, reducedMotion: "reduce" });
      await mockLibrary(page);
      await page.goto(`/#/library/${storage}`);
      const table = page.locator(".downloads-table-scroll > .library-downloads-table");
      await expect(page.getByRole("table", { name: `${storage === "cached" ? "Cached" : "Kept"} songs`, exact: true })).toBeVisible();
      await expectPageSurface(page.locator(".library-downloads"));
      if (viewport.width === 1280) {
        await expectToolbarLayout(page.locator(".downloads-search-sort"), page.getByRole("radiogroup", { includeHidden: true, name: `${storage === "cached" ? "Cached" : "Kept"} song filters` }));
        await expect(table.getByRole("columnheader", { name: storage === "cached" ? "Leaves cache" : "Kept by", exact: true })).toBeVisible();
        await expect(table.getByRole("columnheader", { name: "Size", exact: true })).toBeVisible();
      }
      await expect(table.locator("tbody > .download-row")).toHaveCount(3);
      await page.getByRole("button", { name: "Show 2 copies of Nicole Kidman" }).click();
      await expect(page.getByRole("table", { name: "Copies of Nicole Kidman", exact: true })).toBeVisible();
      if (viewport.width === 390) await expectContainedMobileGeometry(page);
      await page.getByRole("button", { name: "Hide 2 copies of Nicole Kidman" }).click();
      await expect(table.locator("tbody > .download-row .source-name").first()).toBeVisible();
      if (viewport.width === 390) await expectContainedMobileGeometry(page);
      await page.screenshot({ path: `test-results/ui-library/${storage}-${viewport.width}-${viewport.theme}.png`, fullPage: true });
      const opener = page.getByRole("button", { name: "Open song details for Take On Me", exact: true });
      await opener.focus();
      await page.keyboard.press("Enter");
      const panel = page.getByRole("dialog", { name: "Take On Me", exact: true });
      await expect(panel).toBeVisible();
      await expect(panel).toContainText("1064 kbps");
      await expect(panel).toContainText("Take On Me.flac");
      await expect(panel).toContainText("Last access");
      const selected = table.locator("tbody > .download-row.selected");
      await expectSubtleSelection(selected, selected.locator("strong"));
      await expectSingleRowFooter(panel);
      if (viewport.width === 1280) {
        await expectToolbarLayout(page.locator(".downloads-search-sort"), page.getByRole("radiogroup", { includeHidden: true, name: `${storage === "cached" ? "Cached" : "Kept"} song filters` }));
        await expect(table.locator(".lifecycle-heading")).toBeHidden();
        await expect(table.locator(".size-heading")).toBeHidden();
        for (const heading of ["song", "source", "quality", "actions"]) await expect(table.locator(`.${heading}-heading`)).toBeVisible();
        expect(await table.evaluate(element => element.getBoundingClientRect().width <= element.parentElement!.clientWidth + 1)).toBe(true);
        const visibleCells = selected.locator(".source-cell, .quality-cell, .download-actions");
        expect(await visibleCells.evaluateAll(elements => elements.every(element => {
          const cell = element.getBoundingClientRect(), container = element.closest(".downloads-table-scroll")!.getBoundingClientRect();
          return cell.x >= container.x - 1 && cell.right <= container.right + 1 && element.scrollWidth <= element.clientWidth;
        }))).toBe(true);
      }
      if (viewport.width === 390) await expectContainedMobileGeometry(page);
      await expect(panel.getByRole("link", { name: "Download", exact: true })).toHaveAttribute("href", /downloads\/file/);
      await page.screenshot({ path: `test-results/ui-library/${storage}-panel-${viewport.width}-${viewport.theme}.png`, fullPage: true });
      await panel.getByRole("button", { name: "Next song", exact: true }).click();
      await expect(page.getByRole("dialog", { name: "Nicole Kidman", exact: true })).toBeVisible();
      await page.keyboard.press("ArrowUp");
      await expect(panel).toBeVisible();
      await page.keyboard.press("Tab");
      expect(await page.evaluate(() => Boolean(document.activeElement?.closest('[data-side-panel]')))).toBe(true);
      await page.keyboard.press("Escape");
      await expect(panel).not.toBeVisible();
      await expect(opener).toBeFocused();
      if (viewport.width === 1280) {
        await expect(table.getByRole("columnheader", { name: "Size", exact: true })).toBeVisible();
        await expectToolbarLayout(page.locator(".downloads-search-sort"), page.getByRole("radiogroup", { includeHidden: true, name: `${storage === "cached" ? "Cached" : "Kept"} song filters` }));
      }
    });
  }
}

test("cached filters remember their selection and bulk removal confirms exact copies and size", async ({ page }) => {
  const mutations = await mockLibrary(page);
  await page.goto("/#/library/cached");
  await page.getByRole("radio", { name: "Lower quality 1", exact: true }).click();
  await expect(page.locator(".download-row")).toHaveCount(1);
  await page.reload();
  await expect(page.getByRole("radio", { name: "Lower quality 1", exact: true })).toBeChecked();
  await page.getByRole("button", { name: "Remove all", exact: true }).click();
  const dialog = page.getByRole("alertdialog", { name: "Remove all cached songs?" });
  await expect(dialog).toContainText("4 cached copies");
  await expect(dialog).toContainText("116 MB");
  await expect(dialog).toContainText("Kept songs and original library files stay");
  expect(mutations).toHaveLength(0);
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  expect(mutations).toHaveLength(0);
});

test("cache off, loading, empty and error states stay useful", async ({ page }) => {
  await mockLibrary(page, { cacheOff: true, empty: true, delay: true });
  await page.goto("/#/library/cached");
  await expect(page.getByRole("region", { name: "Loading Cached tracks", exact: true })).toBeVisible();
  await expect(page.getByRole("status", { name: "Loading items" })).toBeVisible();
  await expect(page.getByText("No cached songs", { exact: true })).toBeVisible();
  await expect(page.getByText("Playback cache off", { exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: "Cache settings", exact: true })).toBeVisible();
  await page.route("**/api/admin/downloads?**", route => json(route, { error: "Fixture storage is unavailable." }, 503));
  await page.reload();
  await expect(page.getByRole("alert")).toContainText("Fixture storage is unavailable.");
  await expect(page.getByRole("button", { name: "Try again", exact: true })).toBeVisible();
  await page.unroute("**/api/admin/downloads?**");
  await page.getByRole("button", { name: "Try again", exact: true }).click();
  await expect(page.getByText("No cached songs", { exact: true })).toBeVisible();
  await expect(page.getByRole("alert")).toHaveCount(0);
});
