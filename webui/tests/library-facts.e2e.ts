import { expect, test, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { panelFixture, settingsFixture } from "./shared-design-fixtures";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const mib = 1024 ** 2;
const file = { path: "song.flac", storage: "kept", title: "Evening song", artist: "North", album: "Open skies", fileName: "song.flac",
  size: mib, sizeFormatted: "1 MiB", lastModified: "2026-11-05T12:00:00Z", lastAccessedAt: "2026-11-05T12:00:00Z",
  codec: "FLAC", quality: "Lossless", bitDepth: 16, sampleRateHz: 44100, provider: "apple-musickit", externalId: "evening",
  publicationState: "Referenced", referenceCount: 2, explicitlyKept: true, removable: true };
const storage = { storage: { readiness: "Ready" }, databaseBytes: 3 * mib, restorePending: false,
  backups: [{ id: "backup", fileName: "backup.zip", createdAt: "2026-11-05T12:00:00Z", status: "verified", bytes: 2 * mib, origin: "manual", retentionBuckets: ["manual"] }],
  backupPolicy: { enabled: true, timeUtc: "03:00", directory: "", dailyRetentionCount: 7, weeklyRetentionCount: 4, manualRetentionCount: 5, revision: "1" },
  backupStatus: { state: "idle", directoryState: "ready", activeJobId: null, lastSuccessAt: "2026-11-05T12:00:00Z", nextRunAt: "2026-11-06T03:00:00Z", lastBytes: 2 * mib, lastFailure: null } };
const accountFields = { enabled: true, canManage: false, revision: 1, secret: { configured: true, revoked: false }, createdAt: "2026-11-05T12:00:00Z" };

async function capture(page: Page, name: string, width: number) {
  await page.evaluate(async () => { await document.fonts.ready; (document.activeElement as HTMLElement)?.blur(); });
  await mkdir("test-results/library-facts", { recursive: true });
  await page.screenshot({ path: `test-results/library-facts/${name}-${width}-dark.png`, fullPage: true });
}

for (const width of [1280, 390]) {
  test.describe(`Details at ${width}`, () => {
  test.use({ locale: "en-US", timezoneId: "UTC" });
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
    await page.addInitScript(() => { localStorage.setItem("allstarr.theme", "dark"); localStorage.setItem("allstarr.onboarding-guidance-dismissed", "true"); });
  });

  test(`Kept shows supplied keepers and preserves missing facts at ${width}`, async ({ page }) => {
    await panelFixture(page);
    await page.route("**/api/admin/downloads?*", route => route.fulfill({ json: { files: [
      { ...file, keptBy: [{ displayName: "You", isViewer: true, reasons: ["keep"] }, { displayName: "Someone else", isViewer: false, reasons: ["favorite"] }] },
      { ...file, path: "heart.flac", title: "Morning song", keptBy: [{ displayName: "joshp", isViewer: false, reasons: ["favorite"] }] },
      { ...file, path: "unknown.flac", title: "Quiet song", keptBy: [] },
    ], count: 3, managedCount: 3, totalSize: 3 * mib, totalSizeFormatted: "3 MiB", diagnosticCount: 0 } }));
    await page.goto("/#/library/kept");
    const table = page.getByRole("table", { name: "Kept songs", exact: true });
    if (width === 1280) await expect(table.getByRole("columnheader", { name: "Kept by", exact: true })).toBeVisible();
    const rows = table.locator(".library-download-row");
    await expect(rows).toHaveCount(3);
    await expect(rows.first().locator(width === 1280 ? ".lifecycle-cell" : ".phone-facts")).toContainText("You and 1 other");
    await expect(rows.nth(1).locator(width === 1280 ? ".lifecycle-cell" : ".phone-facts")).toContainText("♥ joshp");
    await expect(rows.last().locator(".lifecycle-cell")).toHaveText("");
    const actions = rows.first().locator(".download-actions");
    const menu = actions.getByRole("button", { name: "Actions for Evening song", exact: true });
    const bounds = (await actions.boundingBox())!, button = (await menu.boundingBox())!;
    expect(button.x + button.width).toBeLessThanOrEqual(bounds.x + bounds.width);
    await capture(page, "kept", width);
    if (width === 390) await expectContainedMobileGeometry(page);
    else {
      await table.getByRole("button", { name: "Kept by", exact: true }).click();
      await expect(rows.first()).toContainText("Morning song");
      await expect(rows.last()).toContainText("Quiet song");
    }
  });

  test(`Cached details show short local last play only when supplied at ${width}`, async ({ page }) => {
    await panelFixture(page);
    await page.route("**/api/admin/downloads?*", route => route.fulfill({ json: { files: [
      { ...file, storage: "cache", lastPlayedAt: "2026-11-04T22:15:00Z", expiresAt: "2026-11-08T12:00:00Z" },
      { ...file, path: "unknown.flac", title: "Quiet song", storage: "cache", lastPlayedAt: null },
    ], count: 2, managedCount: 2, totalSize: 2 * mib, totalSizeFormatted: "2 MiB", diagnosticCount: 0 } }));
    await page.goto("/#/library/cached");
    await page.getByRole("button", { name: "Open song details for Evening song", exact: true }).click();
    const panel = page.getByRole("dialog", { name: "Evening song", exact: true });
    await expect(panel.locator("dt").filter({ hasText: /^Last played$/ })).toBeVisible();
    await expect(panel.locator('time[datetime="2026-11-04T22:15:00Z"]')).toHaveText("Nov 4, 10:15 PM");
    await capture(page, "cached-panel", width);
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.keyboard.press("Escape");
    await page.getByRole("button", { name: "Open song details for Quiet song", exact: true }).click();
    await expect(page.getByRole("dialog", { name: "Quiet song", exact: true }).getByText("Last played", { exact: true })).toHaveCount(0);
  });

  test(`Storage draws the measured database segment at ${width}`, async ({ page }) => {
    await settingsFixture(page);
    await page.route("**/api/admin/storage", route => route.fulfill({ json: storage }));
    await page.route("**/api/admin/downloads?*", route => route.fulfill({ json: { files: [], count: 0, totalSize: mib } }));
    await page.goto("/#/settings/storage");
    await expect(page.getByText("3 MiB database", { exact: true })).toBeVisible();
    await expect(page.getByLabel("Status breakdown").getByText("Database (MiB)", { exact: true })).toBeVisible();
    await capture(page, "storage", width);
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.route("**/api/admin/storage", route => route.fulfill({ json: { ...storage, databaseBytes: null } }));
    await page.reload();
    await expect(page.getByText("1 MiB kept", { exact: true })).toBeVisible();
    await expect(page.getByLabel("Status breakdown").getByText("Database (MiB)", { exact: true })).toHaveCount(0);
  });

  test(`Backups show measured database and archive sizes at ${width}`, async ({ page }) => {
    await settingsFixture(page);
    await page.route("**/api/admin/storage", route => route.fulfill({ json: storage }));
    await page.goto("/#/settings/backups");
    await expect(page.getByText("3 MiB database", { exact: true })).toBeVisible();
    await expect(page.getByRole("table").locator(width === 390 ? ".phone-backup-size" : ".number")).toHaveText("2 MiB");
    await expect(page.getByRole("table").locator(width === 390 ? ".phone-backup-size" : ".number")).toBeVisible();
    await capture(page, "backups", width);
    if (width === 390) await expectContainedMobileGeometry(page);
  });

  test(`Accounts show stored labels and omit unnamed rows at ${width}`, async ({ page }) => {
    await panelFixture(page);
    await page.route("**/api/admin/provider-favorite-links**", route => route.fulfill({ json: { activeProtocol: "jellyfin", items: [], nextCursor: null } }));
    await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: { accounts: [
      { ...accountFields, id: "first", providerId: "spotify", displayName: "Evening account", customLabel: "Evening account", scope: "Personal", ownerDisplayName: "You" },
      { ...accountFields, id: "second", providerId: "spotify", displayName: "Family account", customLabel: "Family account", scope: "Shared" },
      { ...accountFields, id: "unnamed", providerId: "qobuz", displayName: "Qobuz", customLabel: null, scope: "Shared" },
    ], audienceUsers: [] } }));
    await page.goto("/#/sources/accounts");
    const table = page.getByRole("table", { name: "Accounts", exact: true });
    await expect(table.locator('[data-label="Account"]').first()).toHaveText("Evening account, Family account");
    await expect(table).not.toContainText("Not provided");
    await expect(table.locator("tbody tr").last()).not.toContainText("Evening account");
    await expect(page.getByRole("form", { name: "Link listener profiles", exact: true })).toBeVisible();
    await capture(page, "accounts", width);
    if (width === 390) await expectContainedMobileGeometry(page);
  });

  test(`General restart descriptions have readable separator spacing at ${width}`, async ({ page }) => {
    await settingsFixture(page);
    await page.goto("/#/settings/general");
    const notes = page.locator(".restart-note");
    await expect(notes.first()).toBeVisible();
    for (const note of await notes.all()) {
      const text = await note.evaluate(element => element.parentElement?.textContent ?? "");
      expect(text).not.toMatch(/\S·/);
      expect(text).not.toContain("·Restarts");
    }
    await capture(page, "general", width);
    await page.goto("/#/settings/access");
    await expect(page.locator(".access-card .restart-note").first()).toHaveText("Restarts Allstarr");
  });
  });
}
