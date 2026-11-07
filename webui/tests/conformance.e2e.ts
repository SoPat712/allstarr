import { expect, test, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { panelFixture, settingsFixture, homeFixture } from "./shared-design-fixtures";

async function capture(page: Page, name: string) {
  await page.evaluate(async () => { await document.fonts.ready; (document.activeElement as HTMLElement)?.blur(); });
  await mkdir("test-results/conformance", { recursive: true });
  await page.screenshot({ path: `test-results/conformance/${name}-1280-dark.png`, fullPage: true });
}

test.beforeEach(async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  await page.addInitScript(() => localStorage.setItem("allstarr.theme", "dark"));
});

test("Home keeps the fallback explanation visible with many failing sources", async ({ page }) => {
  await homeFixture(page, true);
  await page.route("**/api/admin/ui/home", route => route.fulfill({ json: {
    schema: { providers: Array.from({ length: 8 }, (_, i) => ({ id: `source-${i}`, name: `Music source number ${i + 1}` })) },
    status: { backendType: "Jellyfin", durableStorage: { readiness: "Ready" } },
    stats: { linkedPlaylists: 0, playableTracks: 0, unresolvedTracks: 0, activeJobs: 0, completedListens: 0, currentWeekListens: 0, previousWeekListens: 0 },
    providerHealth: { providers: Array.from({ length: 8 }, (_, i) => ({ providerId: `source-${i}`, enabledAccountCount: 1, capabilityTotal: 1, healthyCapabilityCount: 0, failedCapabilityCount: 1 })) },
    activity: { items: [3, 2, 1].map(i => ({ id: `check-${i}`, kind: "job", label: "Health check", action: "health.smoke", state: "succeeded", detail: "4 passed, 3 not checked", occurredAt: `2026-11-06T12:0${i}:00Z` })) },
  } }));
  await page.goto("/#/");
  const consequence = page.locator(".attention-consequence");
  await expect(consequence).toHaveText("Playback falls back to the others.");
  expect(await consequence.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
  await expect(page.locator(".recent-panel .item-row")).toHaveCount(1);
  await capture(page, "home");
});

test("Cached copies flag only the lower-quality copy and omit unknown listening facts", async ({ page }) => {
  await panelFixture(page);
  const file = { storage: "cache", title: "Evening song", artist: "North", album: "Open skies", size: 1000000, sizeFormatted: "1 MB", lastModified: "2026-11-06T12:00:00Z", lastAccessedAt: "2026-11-06T12:00:00Z", expiresAt: "2026-11-08T12:00:00Z", publicationState: "Indexed", explicitlyKept: false, removable: true, durationMilliseconds: 180000 };
  await page.route("**/api/admin/downloads?*", route => route.fulfill({ json: {
    storage: "cache", count: 2, managedCount: 2, diagnosticCount: 0, totalSize: 2000000, totalSizeFormatted: "2 MB",
    files: [{ ...file, path: "cache/lossless.flac", fileName: "lossless.flac", provider: "apple-musickit", externalId: "one", codec: "FLAC", quality: "Lossless", sampleRateHz: 44100, bitDepth: 16 },
      { ...file, path: "cache/compressed.mp3", fileName: "compressed.mp3", provider: "qobuz", externalId: "two", codec: "MP3", quality: "Lossy", bitrateKbps: 128 }],
  } }));
  await page.goto("/#/library/cached");
  const row = page.locator(".library-download-row");
  await expect(row).toHaveCount(1);
  await expect(row.locator(".lower-quality")).toHaveText("MP3 128 kbps");
  await expect(row.locator(".quality-labels")).toContainText("FLAC 16/44.1");
  await capture(page, "cached");
  expect(await row.locator(".quality-labels").evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
  await page.setViewportSize({ width: 1100, height: 900 });
  expect(await row.locator(".quality-labels").evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
  expect(await row.locator(".quality-labels > span:not([aria-hidden])").evaluateAll(labels => labels.map(label => {
    const range = document.createRange(); range.selectNodeContents(label);
    return [...range.getClientRects()].filter(rect => rect.width > 0).length;
  })), "common quality labels stay on one line while the group can wrap").toEqual([1, 1]);
  await page.screenshot({ path: "test-results/conformance/cached-1100-dark.png", fullPage: true });
  await page.setViewportSize({ width: 1280, height: 900 });
  await row.getByRole("button", { name: "Open song details for Evening song", exact: true }).click();
  const panel = page.getByRole("dialog", { name: "Evening song", exact: true });
  expect(await row.locator(".quality-labels").evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
  await expect(panel.locator("dt").filter({ hasText: /^Last played$/ })).toHaveCount(0);
  const fold = panel.locator(".settings-fold");
  await fold.locator("summary").click();
  await expect(fold.getByText("Filename", { exact: true })).toBeVisible();
  await capture(page, "cached-panel");
});

test("Accounts omit unavailable labels while retaining source and owner", async ({ page }) => {
  await panelFixture(page);
  let deliveryReads = 0;
  await page.route("**/api/admin/scrobbling/deliveries*", route => { deliveryReads++; return route.fulfill({ json: { items: [] } }); });
  await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: {
    accounts: [1, 2].map(i => ({ id: `account-${i}`, providerId: "spotify", displayName: "Spotify", ownerDisplayName: "Listener", scope: "Personal", enabled: true, canManage: true, revision: 1, secret: { configured: true, revoked: false } })), audienceUsers: [],
  } }));
  await page.goto("/#/sources/accounts");
  const table = page.getByRole("table", { name: "Accounts", exact: true });
  await expect(table.getByText("Spotify · 2 accounts", { exact: true })).toBeVisible();
  await expect(table.getByRole("columnheader", { name: "Account", exact: true })).toHaveCount(0);
  await expect(table.locator('[data-label="Account"]')).toHaveCount(0);
  await expect(table.locator('[data-label="Owner"]')).toHaveText("Listener");
  await expect(page.getByRole("region", { name: "Scrobble delivery", exact: true })).toBeHidden();
  const before = deliveryReads;
  await page.locator(".sources-heading-actions").getByRole("button", { name: "Refresh", exact: true }).click();
  await expect.poll(() => deliveryReads).toBeGreaterThan(before);
  await expect(page.locator(".delivery-groups")).toHaveAttribute("aria-busy", "false");
  await capture(page, "accounts");
});

test("General timing folds inside its card without nesting forms", async ({ page }) => {
  await settingsFixture(page);
  await page.goto("/#/settings");
  const links = page.getByRole("form", { name: "Song links and extensions settings", exact: true });
  const fold = links.locator(":scope > .settings-fold");
  await expect(fold.locator("summary")).toHaveText("Advanced: wait between song.link lookups");
  await fold.locator("summary").click();
  await expect(links.locator("form")).toHaveCount(0);
  await expect(fold.getByRole("group", { name: "Song link timing settings", exact: true })).toBeVisible();
  await capture(page, "settings");
});

test("Empty library additions fill the available list column", async ({ page }) => {
  await panelFixture(page);
  await page.route("**/api/admin/library-promotions?*", route => route.fulfill({ json: { items: [], counts: { active: 0, needsLook: 0, added: 0, addedToday: 0, all: 0 }, hasMore: false } }));
  await page.goto("/#/library/to-library");
  const empty = page.locator(".promotion-page .empty-state");
  await expect(empty).toBeVisible();
  const column = (await page.locator(".promotion-page").boundingBox())!;
  const box = (await empty.boundingBox())!;
  expect(box.width).toBeCloseTo(column.width, 0);
  await capture(page, "to-library");
});
