import { expect, test, type Page, type Route } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const fields = [
  { key: "CACHE_AUDIO_MAXIMUM_GIB", label: "Maximum audio cache (GiB)", type: "number", valuePath: "cache.audioMaximumGiB", min: 1, max: 1024,
    helpText: "Applies to disposable managed audio and temporary read-ahead. Active readers and protected files are preserved. Default: 2 GiB." },
  { key: "PROVIDER_STREAMS_PER_LISTENER", label: "Provider streams per listener", type: "number", valuePath: "playback.providerStreamsPerListener", min: 1, max: 64,
    helpText: "Includes opening and active external streams and original downloads. Native backend streams are unaffected. Default: 6." },
  { key: "MEDIA_MAXIMUM_CONCURRENT_CONVERSIONS", label: "Concurrent media conversions", type: "number", valuePath: "media.maximumConcurrentConversions", min: 1, max: 16,
    helpText: "Maximum simultaneous core media conversions. New requests use the saved limit without restarting. Default: 2." },
];

async function respond(route: Route, value: unknown) {
  return route.fulfill({ contentType: "application/json", body: JSON.stringify(value) });
}

async function mockCapacity(page: Page, administrator = true) {
  const writes: Array<Record<string, string>> = [];
  const config = { cache: { audioMaximumGiB: 2, mediaMaximumMegabytes: 512 }, playback: { providerStreamsPerListener: 6 }, media: { maximumConcurrentConversions: 2 } };
  let configReads = 0;
  await page.route("**/api/admin/**", async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/admin/auth/me") return respond(route, { authenticated: true, backend: "Jellyfin",
      user: { id: "viewer", name: "Viewer", isAdministrator: administrator } });
    if (path === "/api/admin/auth/oidc/status") return respond(route, { enabled: false });
    if (path === "/api/admin/onboarding/status") return respond(route, { completed: true, setupOpen: false,
      shouldRedirectToSetup: false, schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"],
      completionSource: "setup-guide", revision: 1, recoveryNotices: [], migration: { available: false, completed: false, firstRun: false } });
    if (path === "/api/admin/ui/schema") return respond(route, { configSections: [{ id: "audio-limits", label: "Audio limits", fields }] });
    if (path === "/api/admin/config") {
      if (route.request().method() === "POST") {
        const values = route.request().postDataJSON().updates as Record<string, string>;
        writes.push(values);
        config.cache.audioMaximumGiB = Number(values.CACHE_AUDIO_MAXIMUM_GIB);
        config.playback.providerStreamsPerListener = Number(values.PROVIDER_STREAMS_PER_LISTENER);
        config.media.maximumConcurrentConversions = Number(values.MEDIA_MAXIMUM_CONCURRENT_CONVERSIONS);
        return respond(route, { message: "Saved", updatedKeys: Object.keys(values), requiresRestart: false });
      }
      configReads++;
      return respond(route, config);
    }
    if (path === "/api/admin/storage/files") return respond(route, { settings: { available: true, pending: false, needsReview: false, files: 0 }, csrfToken: "fixture" });
    if (path === "/api/admin/cache") return respond(route, {
      memory: { entryCount: 2, payloadBytes: 2048, maximumBytes: 16_777_216, hitRatio: .75, hits: 3, misses: 1, writes: 2, evictions: 1 },
      media: { entryCount: 1, payloadBytes: 4096, maximumBytes: 536_870_912, maximumEntryBytes: 16_777_216, hitRatio: .5, hits: 1, misses: 1, writes: 1, evictions: 1 },
      categories: [], activity: { coalescedRequests: 0, staleServes: 0, upstreamBytesAvoided: 0 },
      artworkLimits: { maximumEntryBytes: 16_777_216, maximumDecodedPixels: 16_000_000 },
      extensionStorage: { activeExtensions: 0, entryCount: 0, payloadBytes: 0, maximumBytes: 4_194_304 }, capturedAt: "2026-10-24T12:00:00Z",
    });
    if (path === "/api/admin/cache/maintenance/preview") return respond(route, {
      metadata: { scannedEntries: 0, scanLimitReached: false, expiredEntries: 0, unknownOwnerEntries: 0, disabledCategoryEntries: 0, noExpiryEntries: 0, staleAuthorizationScopeEntries: 0, supersededEntries: 0, overQuotaEntries: 0, reclaimableBytes: 0 },
      media: { scannedFiles: 0, scanLimitReached: false, temporaryFiles: 0, malformedMetadataFiles: 0, orphanedMetadataFiles: 0, orphanedPayloadFiles: 0, expiredEntries: 0, noExpiryEntries: 0, overQuotaEntries: 0, reclaimableBytes: 0, cleanupIntervalSeconds: 900, lastCleanupAt: null, lastCleanupDeletedEntries: 0 },
      unreferencedArtworkPayloads: 0, unreferencedArtworkBytes: 0, artworkReferenceScanLimitReached: false,
    });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return respond(route, {});
  });
  return { writes, configReads: () => configReads };
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Audio limits stay in Storage and save without restart at ${width}px ${theme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    const mock = await mockCapacity(page);
    await page.goto("/#/settings/general");
    await expect(page.getByRole("heading", { name: "General", exact: true })).toBeVisible();
    await expect(page.getByRole("form", { name: "Audio limits settings", exact: true })).toHaveCount(0);
    await page.goto("/#/settings/storage");
    const form = page.getByRole("form", { name: "Audio limits settings", exact: true });
    await expect(form).toBeVisible();
    const quota = form.locator('[name="CACHE_AUDIO_MAXIMUM_GIB"]');
    const streams = form.locator('[name="PROVIDER_STREAMS_PER_LISTENER"]');
    const conversions = form.locator('[name="MEDIA_MAXIMUM_CONCURRENT_CONVERSIONS"]');
    await expect(quota).toHaveValue("2");
    await expect(streams).toHaveValue("6");
    await expect(conversions).toHaveValue("2");
    await expect(quota).toHaveAccessibleName(/Maximum audio cache/);
    const save = form.getByRole("button", { name: "Save Audio limits", exact: true });
    await expect(save).toBeDisabled();
    await quota.fill("0");
    await save.click();
    expect(await quota.evaluate((input: HTMLInputElement) => input.validity.rangeUnderflow)).toBe(true);
    expect(mock.writes).toHaveLength(0);
    await quota.fill("4");
    await streams.fill("8");
    await conversions.fill("3");
    await save.focus();
    await page.keyboard.press("Enter");
    await expect.poll(() => mock.writes.length).toBe(1);
    expect(mock.writes[0]).toEqual({ CACHE_AUDIO_MAXIMUM_GIB: "4", PROVIDER_STREAMS_PER_LISTENER: "8", MEDIA_MAXIMUM_CONCURRENT_CONVERSIONS: "3" });
    await expect(page.getByText("Audio limits settings saved.", { exact: true })).toBeVisible();
    await expect(save).toBeDisabled();
    await expect(quota).toHaveValue("4");
    await expect(streams).toHaveValue("8");
    await expect(conversions).toHaveValue("3");
    if (width === 390) await expectContainedMobileGeometry(page);
    await form.scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath(`audio-limits-${width}-${theme}.png`) });
  });
}

test("Audio resource policy is unavailable to listeners", async ({ page }) => {
  const mock = await mockCapacity(page, false);
  await page.goto("/#/settings/storage");
  await expect(page.getByRole("heading", { name: "This workspace is for administrators.", exact: true })).toBeVisible();
  await expect(page.getByRole("form", { name: "Audio limits settings", exact: true })).toHaveCount(0);
  expect(mock.configReads()).toBe(0);
});
