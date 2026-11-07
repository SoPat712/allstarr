import { expect, test, type Page, type Route } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

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

async function mockDownloadsApp(page: Page, queued = true, keptActions = false): Promise<DownloadFixture> {
  const fixture: DownloadFixture = { keepHeaders: [], keepRequests: 0, releaseRequests: 0, releaseStorage: [] };
  let cacheFile = { ...baseFile };
  let moved = false;
  let keptFile = {
    ...baseFile,
    path: "The Fixture Band/Fixture Song.flac",
    storage: "legacy",
    publicationState: "Referenced",
    referenceCount: 2,
    explicitlyKept: keptActions,
    removable: keptActions,
    canDeleteKeptCopy: keptActions,
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
        providers: [{ id: "lumen-audio", name: "Lumen Audio", categories: ["streaming"] },
          { id: "apple-musickit", name: "Apple Music", categories: ["streaming"] }],
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
        files: storage === "cache" && moved ? [] : [file],
        totalSize: storage === "cache" && moved ? 0 : file.size,
        totalSizeFormatted: storage === "cache" && moved ? "0 B" : file.sizeFormatted,
        count: storage === "cache" && moved ? 0 : 1,
        managedCount: storage === "cache" && moved ? 0 : 1,
        diagnosticCount: 0,
      });
    }
    if (url.pathname === "/api/admin/downloads/promote" && request.method() === "POST") {
      fixture.keepRequests += 1;
      const idempotencyKey = request.headers()["idempotency-key"];
      fixture.keepHeaders.push(idempotencyKey ?? "");
      moved = true;
      keptFile = { ...keptFile, explicitlyKept: true, removable: true, referenceCount: 3 };
      return json(route, {
        success: true,
        queued,
        jobId: "77777777-7777-4777-8777-777777777777",
        message: queued ? "Moving to Kept…" : "Moved to Kept.",
      }, queued ? 202 : 200);
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
      await expect(page.getByLabel("Notifications").getByText("Moving to Kept…", { exact: true })).toBeVisible();
      await expect.poll(() => fixture.keepRequests).toBe(1);
      expect(fixture.keepHeaders[0]).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
      await expect(row).toHaveCount(0);

      await page.goto("/#/library/kept");
      const keptRow = page.locator(".library-download-row").filter({ hasText: "Fixture Song" });
      await expect(keptRow).toBeVisible();
      await keptRow.getByRole("button", { name: "Actions for Fixture Song", exact: true }).click();
      await expect(page.getByRole("menuitem", { name: "Stop keeping", exact: true })).toBeVisible();
      await page.keyboard.press("Escape");

      await page.getByRole("button", { name: "Actions for kept songs", exact: true }).click();
      await page.getByRole("menuitem", { name: "Remove all", exact: true }).click();
      const bulkDialog = page.getByRole("alertdialog", { name: /Remove all kept songs/i });
      await expect(bulkDialog).toBeVisible();
      await expect(bulkDialog).toContainText(/1 explicit Keep/i);
      await expect(bulkDialog).toContainText(/24-hour grace period/i);
      await expect(bulkDialog).toContainText(/other household interests remain protected/i);
      await bulkDialog.getByRole("button", { name: "Cancel", exact: true }).click();

      await keptRow.getByRole("button", { name: "Actions for Fixture Song", exact: true }).click();
      await page.getByRole("menuitem", { name: "Stop keeping", exact: true }).click();
      const dialog = page.getByRole("alertdialog", { name: /Stop keeping/i });
      await expect(dialog).toBeVisible();
      await expect(dialog).toContainText(/24-hour grace period|other interests/i);
      await dialog.getByRole("button", { name: "Stop keeping", exact: true }).click();
      await expect(page.getByText(/Keep released\./i)).toBeVisible();
      await expect.poll(() => fixture.releaseRequests).toBe(1);
      expect(fixture.releaseStorage).toEqual(["kept"]);
      await expect(keptRow).toBeVisible();
      await keptRow.getByRole("button", { name: "Actions for Fixture Song", exact: true }).click();
      await expect(page.getByRole("menuitem", { name: "Stop keeping", exact: true })).toHaveCount(0);
      await page.keyboard.press("Escape");
      await page.goto("/#/library/cached");
      const retainedCacheRow = page.locator(".download-row").filter({ hasText: "Fixture Song" });
      await expect(retainedCacheRow).toHaveCount(0);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    });
  }
}

for (const width of [390, 1280]) {
  for (const colorScheme of ["light", "dark"] as const) {
    test(`hires downloads quality badges at ${width}px in ${colorScheme} mode`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
      await mockDownloadsApp(page);
      await page.route("**/api/admin/downloads?**", (route) => json(route, {
        storage: "kept", totalSize: 4_096_000, totalSizeFormatted: "4 MB", count: 4, managedCount: 4, diagnosticCount: 0,
        files: ["upsampled", "padded-bit-depth", "lossy-transcode-suspected", "true-hi-res"].map((verdict, index) => ({
          ...baseFile, path: `${index}.flac`, storage: "kept", title: `Quality ${index + 1}`, quality: "24-bit / 96 kHz",
          qualityCheck: { claimedSampleRate: 96000, claimedBitDepth: 24, effectiveBandwidthHz: 20000, verdict, confidence: 0.8, likelySourceRate: 44100 },
        })),
      }));
      await page.goto("/#/library/kept");
      await expect(page.getByText("Likely upscaled", { exact: true })).toBeVisible();
      await expect(page.getByText("Padded", { exact: true })).toBeVisible();
      await expect(page.getByText("Lossy source?", { exact: true })).toBeVisible();
      await expect(page.locator(".quality-check-badge")).toHaveCount(3);
      for (const badge of await page.locator(".quality-check-badge").all()) {
        await expect(badge).toHaveAttribute("title", /.+\.$/);
      }
      const row = page.locator(".library-download-row").filter({ hasText: "Quality 4" });
      await expect(row.locator(".quality-check-badge")).toHaveCount(0);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      await page.screenshot({ path: `test-results/hires-downloads-${width}-${colorScheme}.png`, fullPage: true });
    });
  }
}

for (const width of [390, 1280]) {
  for (const colorScheme of ["light", "dark"] as const) {
    test(`referenced kept copy deletion without the secure UUID API requires clear confirmation at ${width}px in ${colorScheme} mode`, async ({ page }) => {
      await page.setViewportSize({ width, height: 844 });
      await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
      await page.addInitScript(() => Object.defineProperty(crypto, "randomUUID", { value: undefined }));
      await mockDownloadsApp(page);
      let previewRequests = 0;
      const deletionRequests: unknown[] = [];
      await page.route("**/api/admin/downloads?storage=kept", route => json(route, {
        storage: "kept", files: [{ ...baseFile, path: "FLO/Access All Areas/Unknown - Check.flac", storage: "kept",
          title: "Check", artist: "FLO", album: "Access All Areas", provider: "apple-musickit", externalId: "1780787517",
          publicationState: "Referenced", referenceCount: 1, removable: false, canDeleteKeptCopy: true }],
        totalSize: baseFile.size, totalSizeFormatted: baseFile.sizeFormatted, count: 1, managedCount: 1, diagnosticCount: 0,
      }));
      const preview = { managedFileId: "11111111-1111-4111-8111-111111111111", revision: 5,
        referenceFingerprint: "reference-proof", title: "Check", artist: "FLO", album: "Access All Areas",
        referenceCount: 1, favoriteReferences: 1, playlistReferences: 0, keepReferences: 0, otherReferences: 0 };
      await page.route("**/api/admin/downloads/kept/delete-preview", route => {
        previewRequests++;
        expect(route.request().postDataJSON()).toEqual({ path: "FLO/Access All Areas/Unknown - Check.flac" });
        return json(route, preview);
      });
      await page.route("**/api/admin/downloads/kept/delete", route => {
        deletionRequests.push(route.request().postDataJSON());
        return json(route, { jobId: "delete-job", queued: true }, 202);
      });
      await page.goto("/#/library/kept");
      const row = page.locator(".library-download-row").filter({ hasText: "Check" });
      await row.getByRole("button", { name: "Open song details for Check", exact: true }).click();
      const panel = page.locator("[data-side-panel][data-state=open]");
      await expect(panel).toContainText("Apple Music");
      await expect(panel).not.toContainText("Unknown source");
      await panel.getByRole("button", { name: "Close", exact: true }).click();
      const menu = row.getByRole("button", { name: "Actions for Check", exact: true });
      await menu.click();
      const remove = page.getByRole("menuitem", { name: "Delete kept copy…", exact: true });
      await expect(remove).toBeVisible();
      if (width === 390) await expectContainedMobileGeometry(page);
      await remove.focus();
      await page.keyboard.press("Enter");
      const dialog = page.getByRole("alertdialog", { name: "Delete the kept copy of Check?" });
      await expect(dialog).toBeVisible();
      await expect(dialog).toContainText("audio and lyrics for everyone");
      await expect(dialog).toContainText("1 favorite");
      await expect(dialog).toContainText("Hearts and playlist entries stay");
      await expect(dialog).toContainText("stream or download again");
      if (width === 390) await expectContainedMobileGeometry(page);
      await page.screenshot({ path: `test-results/kept-delete-confirm-${width}-${colorScheme}.png`, fullPage: true });
      await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
      expect(deletionRequests).toHaveLength(0);
      await menu.click();
      await remove.click();
      await dialog.getByRole("button", { name: "Delete kept copy", exact: true }).click();
      await expect.poll(() => deletionRequests.length).toBe(1);
      expect(deletionRequests[0]).toMatchObject({ managedFileId: preview.managedFileId, revision: 5,
        referenceFingerprint: "reference-proof", confirmation: "Delete kept copy" });
      expect((deletionRequests[0] as { requestId: string }).requestId)
        .toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
      expect(previewRequests).toBe(2);
      await expect(page.getByText("Kept copy deletion queued.", { exact: true })).toBeVisible();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      await page.screenshot({ path: `test-results/kept-delete-${width}-${colorScheme}.png`, fullPage: true });
    });
  }
}

test("kept deletion keeps a changed confirmation open and requires a fresh preview", async ({ page }) => {
  await mockDownloadsApp(page);
  await page.route("**/api/admin/downloads?storage=kept", route => json(route, {
    storage: "kept", files: [{ ...baseFile, canDeleteKeptCopy: true, removable: false }],
    totalSize: baseFile.size, totalSizeFormatted: baseFile.sizeFormatted, count: 1, managedCount: 1, diagnosticCount: 0,
  }));
  let previews = 0;
  const requests: { revision: number; referenceFingerprint: string }[] = [];
  await page.route("**/api/admin/downloads/kept/delete-preview", route => json(route, {
    managedFileId: "11111111-1111-4111-8111-111111111111", revision: ++previews,
    referenceFingerprint: `proof-${previews}`, title: baseFile.title, referenceCount: 1,
    favoriteReferences: 1, playlistReferences: 0, keepReferences: 0, otherReferences: 0,
  }));
  await page.route("**/api/admin/downloads/kept/delete", route => {
    requests.push(route.request().postDataJSON());
    return requests.length === 1
      ? json(route, { error: "The references changed. Close this dialog and check the kept copy again." }, 409)
      : json(route, { jobId: "delete-job", queued: true }, 202);
  });
  await page.goto("/#/library/kept");
  const menu = page.getByRole("button", { name: "Actions for Fixture Song", exact: true });
  await menu.click();
  const remove = page.getByRole("menuitem", { name: "Delete kept copy…", exact: true });
  await remove.click();
  const dialog = page.getByRole("alertdialog", { name: "Delete the kept copy of Fixture Song?" });
  await dialog.getByRole("button", { name: "Delete kept copy", exact: true }).click();
  await expect(dialog.getByRole("alert")).toContainText("The references changed");
  await expect(dialog.getByRole("button", { name: "Delete kept copy", exact: true })).toBeDisabled();
  expect(requests).toHaveLength(1);
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await menu.click();
  await remove.click();
  await dialog.getByRole("button", { name: "Delete kept copy", exact: true }).click();
  await expect.poll(() => requests.length).toBe(2);
  expect(requests[1]).toMatchObject({ revision: 2, referenceFingerprint: "proof-2" });
  await expect(dialog).not.toBeVisible();
});

test("Keep works without the secure UUID API", async ({ page }) => {
  await page.addInitScript(() => Object.defineProperty(crypto, "randomUUID", { value: undefined }));
  const fixture = await mockDownloadsApp(page);
  await page.goto("/#/library/cached");
  await page.locator(".download-row").filter({ hasText: "Fixture Song" }).getByRole("button", { name: "Keep", exact: true }).click();
  await expect(page.getByLabel("Notifications").getByText("Moving to Kept…", { exact: true })).toBeVisible();
  expect(fixture.keepHeaders).toHaveLength(1);
  expect(fixture.keepHeaders[0]).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
});

test("completed Keep reports the move and removes the Cache row", async ({ page }) => {
  await mockDownloadsApp(page, false);
  await page.goto("/#/library/cached");
  const row = page.locator(".download-row").filter({ hasText: "Fixture Song" });
  await row.getByRole("button", { name: "Keep", exact: true }).click();
  await expect(page.getByText("Moved to Kept.", { exact: true })).toBeVisible();
  await expect(row).toHaveCount(0);
});

test("kept search and copy actions stay reachable without library status", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await mockDownloadsApp(page);
  const copies = [
    { ...baseFile, path: "first.flac", storage: "kept", title: "First copy", explicitlyKept: true },
    { ...baseFile, path: "second.mp3", storage: "kept", title: "Second copy", codec: "MP3", provider: "apple-musickit", bitrateKbps: 128, size: 512_000, sizeFormatted: "500 KiB" },
  ];
  await page.route("**/api/admin/downloads?storage=kept", route => json(route, {
    storage: "kept", files: copies, count: 2, managedCount: 2, diagnosticCount: 1,
    totalSize: 1_536_000, totalSizeFormatted: "1500 KiB",
  }));
  await page.route("**/api/admin/library-promotions/kept", route => json(route, { error: "Library status is unavailable." }, 503));
  await page.goto("/#/library/kept");
  await expect(page.locator(".library-download-row")).toHaveCount(2);
  const actions = page.getByRole("button", { name: "Actions for kept songs", exact: true });
  await actions.click();
  await page.getByRole("menuitem", { name: "Search and sort", exact: true }).click();
  await page.getByRole("searchbox", { name: "Filter kept songs", exact: true }).fill("Second");
  await expect(page.locator(".library-download-row")).toHaveCount(1);
  await page.getByRole("searchbox", { name: "Filter kept songs", exact: true }).fill("");
  await page.getByRole("radio", { name: "Your Keeps 1", exact: true }).click();
  await expect(page.locator(".library-download-row")).toHaveCount(1);
  await expect(page.locator(".library-download-row")).toContainText("First copy");
  await page.reload();
  await expect(page.getByRole("radio", { name: "Your Keeps 1", exact: true })).toBeChecked();
  await expect(page.locator(".library-download-row")).toHaveCount(1);
  await page.getByRole("radio", { name: "Lower quality 1", exact: true }).click();
  const row = page.locator(".library-download-row");
  await expect(row).toContainText("Second copy");
  await page.getByRole("button", { name: "Sort songs", exact: true }).click();
  await page.getByRole("option", { name: "Size", exact: true }).click();
  await page.getByRole("button", { name: "Source", exact: true }).click();
  await page.getByRole("option", { name: "Apple Music", exact: true }).click();
  await row.getByRole("button", { name: "Open song details for Second copy", exact: true }).click();
  const panel = page.locator("[data-side-panel][data-state=open]");
  await expect(panel).toContainText("Apple Music");
  await expect(panel).toContainText("500 KiB");
  await panel.getByText("Copy details", { exact: true }).click();
  await expect(panel.getByText("Last access", { exact: true })).toBeVisible();
  await expect(panel.getByText("Filename", { exact: true })).toBeVisible();
  await expect(panel.getByText("fixture-track", { exact: true })).toBeVisible();
  await expect(panel.getByRole("link", { name: "Download", exact: true })).toHaveAttribute("href", /path=second.mp3.*storage=kept/);
  await expectContainedMobileGeometry(page);
  await panel.getByRole("button", { name: "Close", exact: true }).click();
  await actions.click();
  await page.getByRole("menuitem", { name: "Remove all", exact: true }).click();
  const dialog = page.getByRole("alertdialog", { name: "Remove all kept songs?", exact: true });
  await expect(dialog).toContainText("1 explicit Keep");
  await expect(dialog).toContainText("1 unreferenced kept copy");
  await expect(dialog).toContainText("1500 KiB");
  await expect(dialog).toContainText("1 diagnostic file stays untouched");
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
});

test("kept copies sharing a recording each retain their own actions", async ({ page }) => {
  await mockDownloadsApp(page);
  const recordingId = "11111111-1111-4111-8111-111111111111";
  await page.route("**/api/admin/downloads?storage=kept", route => json(route, {
    storage: "kept", files: [
      { ...baseFile, path: "first.flac", storage: "kept", explicitlyKept: true },
      { ...baseFile, path: "second.flac", storage: "kept", provider: "apple-musickit", bitDepth: 24, sampleRateHz: 96_000, canDeleteKeptCopy: true,
        qualityCheck: { verdict: "upsampled", likelySourceRate: 44_100 } },
      { ...baseFile, path: "third.mp3", storage: "kept", codec: "MP3", bitrateKbps: 128, bitDepth: null, removable: false, referenceCount: 2 },
    ], count: 3, managedCount: 3, diagnosticCount: 0, totalSize: 3_072_000, totalSizeFormatted: "3 MB",
  }));
  await page.route("**/api/admin/library-promotions/kept", route => json(route, {
    items: ["first.flac", "second.flac", "third.mp3"].map(path => ({ path, recordingId, inLibrary: false })),
  }));
  await page.goto("/#/library/kept");
  const rows = page.locator(".library-download-row");
  await expect(rows).toHaveCount(1);
  await expect(page.getByRole("region", { name: "Kept totals", exact: true })).toContainText("1 kept");
  await expect(page.getByRole("region", { name: "Kept totals", exact: true })).toContainText("3 copies");
  await expect(rows.first().locator(".quality-labels")).toContainText("FLAC 24/96");
  await expect(rows.first()).toContainText("Likely upscaled");
  await expect(rows.first().locator(".quality-check-badge").filter({ hasText: /^Below CD quality$/ })).toHaveText("Below CD quality");
  await rows.first().getByRole("button", { name: "Actions for Fixture Song", exact: true }).click();
  await expect(rows.first().getByRole("link", { name: "Download", exact: true })).toHaveAttribute("href", /path=second.flac/);
  await page.keyboard.press("Escape");
  const expander = rows.first().getByRole("button", { name: /3 copies of Fixture Song$/ });
  await expect(expander).toHaveAccessibleName("Show 3 copies of Fixture Song");
  await expander.click();
  await expect(expander).toHaveAttribute("aria-expanded", "true");
  const copies = page.getByRole("table", { name: "Copies of Fixture Song", exact: true });
  await expect(copies.locator(".library-download-row")).toHaveCount(3);
  const firstCopy = copies.locator(".library-download-row").filter({ hasText: "FLAC 16/44" });
  const secondCopy = copies.locator(".library-download-row").filter({ hasText: "FLAC 24/96" });
  const thirdCopy = copies.locator(".library-download-row").filter({ hasText: "MP3 128" });
  await expect(firstCopy).toContainText("Lumen Audio");
  await expect(secondCopy).toContainText("Apple Music");
  await expect(secondCopy).toContainText("Likely upscaled");
  await expect(thirdCopy.locator(".quality-check-badge")).toHaveText("Below CD quality");
  await firstCopy.getByRole("button", { name: "Actions for Fixture Song", exact: true }).click();
  await expect(page.getByRole("menuitem", { name: "Stop keeping", exact: true })).toBeVisible();
  await expect(firstCopy.getByRole("link", { name: "Download", exact: true })).toHaveAttribute("href", /path=first.flac/);
  await page.keyboard.press("Escape");
  await secondCopy.getByRole("button", { name: "Actions for Fixture Song", exact: true }).click();
  await expect(page.getByRole("menuitem", { name: "Delete kept copy…", exact: true })).toBeVisible();
  await expect(rows.first().getByRole("link", { name: "Download", exact: true })).toHaveAttribute("href", /path=second.flac/);
  await page.keyboard.press("Escape");
  await thirdCopy.getByRole("button", { name: "Actions for Fixture Song", exact: true }).click();
  await expect(page.getByRole("menuitem", { name: "Protected copy", exact: true })).toBeDisabled();
  await expect(page.getByRole("menuitem", { name: "Stop keeping", exact: true })).toHaveCount(0);
  await expect(thirdCopy.getByRole("link", { name: "Download", exact: true })).toHaveAttribute("href", /path=third.mp3/);
  await page.keyboard.press("Escape");
  await firstCopy.getByRole("button", { name: "Open song details for Fixture Song", exact: true }).click();
  const panel = page.locator("[data-side-panel][data-state=open]");
  await expect(panel).toContainText("first.flac");
  await expect(panel.getByRole("link", { name: "Download", exact: true })).toHaveAttribute("href", /path=first.flac/);
  await panel.getByRole("button", { name: "Close", exact: true }).click();
  await page.setViewportSize({ width: 390, height: 900 });
  await expectContainedMobileGeometry(page);
  await page.getByRole("button", { name: "Hide 3 copies of Fixture Song", exact: true }).click();
  await expect(rows).toHaveCount(1);
});


for (const storage of ["cached", "kept"]) test(`Downloads have one outlined action and a menu in ${storage}`, async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await mockDownloadsApp(page, true, storage === "kept");
  await page.goto(`/#/library/${storage}`);
  const list = storage === "cached" ? page.getByRole("table", { name: "Cached songs" }) : page.getByRole("region", { name: "Kept songs", exact: true });
  if (storage === "cached") await expect(list.locator("thead th")).toHaveText(["Song", "Source", "Quality", "Leaves cache", "Size", "Actions"]);
  else {
    await expect(list.getByRole("button", { name: "Add selected to library", exact: true })).toBeVisible();
    await expect(list.locator(".quality-labels")).toHaveText("FLAC 16/44.1");
    await expect(list.getByRole("columnheader", { name: "Kept by", exact: true })).toBeVisible();
    await list.getByRole("button", { name: "Open song details for Fixture Song", exact: true }).click();
  }
  const row = storage === "cached" ? list.locator(".download-row").first() : page.locator("[data-side-panel][data-state=open] .kept-copy-actions");
  const action = storage === "cached" ? row.getByRole("button", { name: "Keep", exact: true }) : row.getByRole("link", { name: "Download", exact: true });
  await expect(action).toBeVisible();
  expect(await action.evaluate(element => getComputedStyle(element).borderTopStyle)).toBe("solid");
  await expect(row.locator(storage === "cached" ? '.download-actions > [data-slot="button"]' : ':scope > [data-slot="button"]')).toHaveCount(1);
  await row.getByRole("button", { name: "Actions for Fixture Song", exact: true }).click();
  if (storage === "cached") {
    await expect(page.getByRole("menuitem", { name: "Download", exact: true })).toBeVisible();
    await page.getByRole("menuitem", { name: "Remove", exact: true }).click();
    await expect(page.getByRole("alertdialog", { name: "Remove this song?" })).toBeVisible();
  } else {
    await expect(page.getByRole("menuitem", { name: "Stop keeping", exact: true })).toBeVisible();
    await expect(page.getByRole("menuitem", { name: "Delete kept copy…", exact: true })).toBeVisible();
    await page.keyboard.press("Escape");
  }
  await page.setViewportSize({ width: 390, height: 900 });
  await expect(action).toBeVisible();
  await expectContainedMobileGeometry(page);
});
