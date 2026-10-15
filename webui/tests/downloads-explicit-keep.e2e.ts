import { expect, test, type Page, type Route } from "@playwright/test";

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({
    status,
    contentType: "application/json",
    body: JSON.stringify(body),
  });
}

type DownloadFixture = {
  keepHeaders: string[];
  keepRequests: number;
  releaseRequests: number;
  releaseStorage: string[];
};

const baseFile = {
  path: "cache/lumen-audio/track.flac",
  storage: "cache",
  artist: "The Fixture Band",
  album: "Fixture Album",
  title: "Fixture Song",
  fileName: "track.flac",
  size: 1_024_000,
  sizeFormatted: "1000 KiB",
  lastModified: "2026-10-01T12:00:00Z",
  codec: "FLAC",
  bitrateKbps: 900,
  sampleRateHz: 44_100,
  bitDepth: 16,
  channels: 2,
  durationMilliseconds: 180_000,
  quality: "Lossless",
  provider: "lumen-audio",
  externalId: "fixture-track",
  artworkUrl: null,
  lastAccessedAt: "2026-10-02T12:00:00Z",
  expiresAt: "2026-10-03T12:00:00Z",
  publicationState: "Indexed",
  referenceCount: 0,
  explicitlyKept: false,
  removable: true,
};

async function mockDownloadsApp(page: Page): Promise<DownloadFixture> {
  const fixture: DownloadFixture = { keepHeaders: [], keepRequests: 0, releaseRequests: 0, releaseStorage: [] };
  let cacheFile = { ...baseFile };
  let keptFile = {
    ...baseFile,
    path: "The Fixture Band/Fixture Song.flac",
    storage: "legacy",
    publicationState: "Referenced",
    referenceCount: 2,
    explicitlyKept: false,
    removable: false,
  };

  await page.route("**/images/providers/**", (route) => route.fulfill({
    status: 200,
    contentType: "image/svg+xml",
    body: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1 1"/>',
  }));
  await page.route("**/api/admin/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());

    if (url.pathname === "/api/admin/auth/me") {
      return json(route, {
        authenticated: true,
        backend: "Jellyfin",
        features: { intelligence: false },
        user: { id: "administrator", name: "Administrator", isAdministrator: true },
      });
    }
    if (url.pathname === "/api/admin/auth/oidc/status") return json(route, { enabled: false });
    if (url.pathname === "/api/admin/onboarding/status") {
      return json(route, {
        completed: true,
        setupOpen: false,
        shouldRedirectToSetup: false,
        schemaVersion: "fixture-v1",
        completedSteps: ["backend-identity"],
        completionSource: "fixture",
        revision: 1,
        recoveryNotices: [],
        migration: { available: false, completed: false, firstRun: false },
      });
    }
    if (url.pathname === "/api/admin/updates/stream") {
      return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    }
    if (url.pathname === "/api/admin/ui/schema") {
      return json(route, {
        activeBackend: "Jellyfin",
        providers: [{ id: "lumen-audio", name: "Lumen Audio", categories: ["streaming"] }],
        settings: [],
        priorityGroups: [],
      });
    }
    if (url.pathname === "/api/admin/config") {
      return json(route, { library: { storageMode: "Cache", cacheDurationHours: 24 }, cache: { transcodeCacheMinutes: 60 } });
    }
    if (url.pathname === "/api/admin/downloads" && request.method() === "GET") {
      const storage = url.searchParams.get("storage") ?? "cache";
      const file = storage === "kept" ? keptFile : cacheFile;
      return json(route, {
        storage,
        files: [file],
        totalSize: file.size,
        totalSizeFormatted: file.sizeFormatted,
        count: 1,
        managedCount: 1,
        diagnosticCount: 0,
      });
    }
    if (url.pathname === "/api/admin/downloads/promote" && request.method() === "POST") {
      fixture.keepRequests += 1;
      const idempotencyKey = request.headers()["idempotency-key"];
      fixture.keepHeaders.push(idempotencyKey ?? "");
      keptFile = { ...keptFile, explicitlyKept: true, removable: true, referenceCount: 3 };
      return json(route, {
        success: true,
        queued: true,
        jobId: "77777777-7777-4777-8777-777777777777",
        message: "Track queued for Kept. Its cached copy stays available.",
      }, 202);
    }
    if (url.pathname === "/api/admin/downloads" && request.method() === "DELETE") {
      fixture.releaseRequests += 1;
      const storage = url.searchParams.get("storage") ?? "kept";
      fixture.releaseStorage.push(storage);
      keptFile = { ...keptFile, explicitlyKept: false, removable: false, referenceCount: 2 };
      return json(route, {
        success: true,
        released: true,
        message: "Keep released. Other interests are preserved. If none remain, the file enters a 24-hour grace period.",
      }, 202);
    }
    return json(route, {});
  });

  return fixture;
}

for (const width of [390, 1280]) {
  for (const colorScheme of ["light", "dark"] as const) {
    test(`explicit Keep is queued and released safely at ${width}px in ${colorScheme} mode`, async ({ page }) => {
      await page.setViewportSize({ width, height: 844 });
      await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
      const fixture = await mockDownloadsApp(page);

      await page.goto("/#/library/cached");
      const row = page.locator(".download-row").filter({ hasText: "Fixture Song" });
      await expect(row).toBeVisible();
      const keep = row.getByRole("button", { name: "Keep", exact: true });
      await keep.focus();
      await page.keyboard.press("Enter");
      await expect(page.getByText("Track queued for Kept. Its cached copy stays available.", { exact: true })).toBeVisible();
      await expect.poll(() => fixture.keepRequests).toBe(1);
      expect(fixture.keepHeaders[0]).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
      await expect(row.getByRole("button", { name: "Keep", exact: true })).toBeVisible();
      await expect(page.getByText("Fixture Song", { exact: true })).toBeVisible();

      await page.goto("/#/library/kept");
      const keptRow = page.locator(".download-row").filter({ hasText: "Fixture Song" });
      await expect(keptRow).toBeVisible();
      await expect(keptRow.getByRole("button", { name: "Release Keep", exact: true })).toBeVisible();

      await page.getByRole("button", { name: "Remove all", exact: true }).click();
      const bulkDialog = page.getByRole("alertdialog", { name: /Remove all kept tracks/i });
      await expect(bulkDialog).toBeVisible();
      await expect(bulkDialog).toContainText(/1 explicit Keep/i);
      await expect(bulkDialog).toContainText(/24-hour grace period/i);
      await expect(bulkDialog).toContainText(/other household interests remain protected/i);
      await bulkDialog.getByRole("button", { name: "Cancel", exact: true }).click();

      await keptRow.getByRole("button", { name: "Release Keep", exact: true }).click();
      const dialog = page.getByRole("alertdialog", { name: /Release.*Keep/i });
      await expect(dialog).toBeVisible();
      await expect(dialog).toContainText(/24-hour grace period|other interests/i);
      await dialog.getByRole("button", { name: "Release Keep", exact: true }).click();
      await expect(page.getByText(/Keep released\./i)).toBeVisible();
      await expect.poll(() => fixture.releaseRequests).toBe(1);
      expect(fixture.releaseStorage).toEqual(["kept"]);
      await expect(keptRow).toBeVisible();
      await expect(keptRow.getByRole("button", { name: "Release Keep", exact: true })).toHaveCount(0);
      await page.goto("/#/library/cached");
      const retainedCacheRow = page.locator(".download-row").filter({ hasText: "Fixture Song" });
      await expect(retainedCacheRow).toBeVisible();
      await expect(retainedCacheRow.getByRole("button", { name: "Keep", exact: true })).toBeVisible();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    });
  }
}
