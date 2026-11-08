import { expect, test, type Locator, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";

const userId = "11111111-1111-4111-8111-111111111111";
const accountId = "22222222-2222-4222-8222-222222222222";
const runId = "33333333-3333-4333-8333-333333333333";
const preferences = {
  lyricsOrder: "backend,kept", explicitFilter: "All", showExternalLabel: true,
  showExplicitLabel: true, playbackStart: "Balanced", qualityLimit: null,
};
const schema = {
  activeBackend: "Jellyfin",
  providers: [
    { id: "spotify", name: "Spotify", categories: ["playlist"], requiresAccount: true },
    { id: "lumen-audio", name: "Lumen Audio", categories: ["streaming"], useMode: "automatic" },
  ],
  configSections: [{ id: "general", label: "General", fields: [
    { key: "AUDIO_QUALITY", label: "Audio quality", type: "audio-quality", valuePath: "audio.quality" },
  ] }],
  priorityGroups: [{ id: "streaming", label: "Playback", envKey: "StreamingOrder", providers: ["lumen-audio", "spotify", "deezer", "soundcloud", "apple-download", "backend"] }],
};
const history = {
  importId: "44444444-4444-4444-8444-444444444444", revision: "a".repeat(64),
  displayFileName: "history.json", sizeBytes: 2048, expiresAt: null, state: 3,
  jobId: null, jobState: null, lastErrorCode: null, lastErrorMessage: null,
  importedRows: 24, duplicateRows: 0, resolvedRows: 24, unresolvedRows: 0,
  preview: {
    format: "spotify-extended-streaming-history", fileRows: 24, musicRows: 24, completed: 24,
    partial: 0, skipped: 0, episodes: 0, nonTrack: 0, malformed: 0, duplicateInFile: 0,
    duplicateExisting: 0, newRows: 24, resolvedNewRows: 24, unresolvedNewRows: 0,
    rowsWithoutProviderIdentity: 0, sourceUserCount: 1, estimatedMusicBrainzLookups: 0,
    earliest: "2026-10-01T12:00:00Z", latest: "2026-10-28T12:00:00Z", reasonCounts: {},
    outsideRetentionRows: 0, songs: 12,
  },
};

async function mockViews(page: Page) {
  await page.route("**/*", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (!["127.0.0.1", "localhost"].includes(url.hostname)) return route.abort();
    if (!path.startsWith("/api/admin/")) return route.continue();
    const reply = (json: unknown) => route.fulfill({ json });
    if (path === "/api/admin/auth/me") return reply({ authenticated: true, backend: "Jellyfin",
      features: { intelligence: false }, user: { id: "listener", allstarrUserId: userId, name: "Alex", isAdministrator: true } });
    if (path === "/api/admin/auth/oidc/status") return reply({ enabled: false });
    if (path === "/api/admin/onboarding/status") return reply({ completed: true, setupOpen: false,
      shouldRedirectToSetup: false, schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"],
      completionSource: "setup-guide", revision: 1, recoveryNotices: [],
      migration: { available: false, completed: false, firstRun: false } });
    if (path === "/api/admin/ui/schema") return reply(schema);
    if (path === "/api/admin/preferences") return reply({ values: preferences, householdDefaults: preferences,
      householdQuality: "BestAvailable", usesHouseholdDefaults: true, revision: "current", cleanOnlyLocked: false });
    if (path === "/api/admin/config") return reply({ audio: { quality: "BestAvailable" }, providers: { streamingOrder: "lumen-audio" } });
    if (path === "/api/admin/providers/scores") return reply({ qualityTarget: "BestAvailable", providers: [{
      providerId: "lumen-audio", score: 82, uptimePercent: 100, checks: 10, days: [],
      quality: { typical: "Lossless", best: null, lossless: true, samples: 5 },
      capabilities: [{ capability: "streaming", score: 82, uptimePercent: 100, checks: 10,
        failures: 0, typicalLatencyMilliseconds: 120, lastFailureCode: null }],
    }] });
    if (path === "/api/admin/provider-accounts") return reply({ audienceUsers: [], listenersCanConnectOwnAccounts: true,
      accounts: [{ id: accountId, providerId: "spotify", displayName: "My Spotify", scope: "Personal",
        ownerUserId: userId, ownerDisplayName: "Alex", enabled: true, canManage: true, revision: 1,
        secret: { configured: true, revoked: false }, createdAt: "2026-10-24T12:00:00Z", updatedAt: "2026-10-24T12:00:00Z" }] });
    if (path === "/api/admin/migrations" && request.method() === "POST") return reply({ id: runId });
    if (path === "/api/admin/migrations/preview" || path === `/api/admin/migrations/${runId}/discover`) return reply({ items: [
      { sourceId: "liked-songs", name: "Liked Songs", count: 3, kind: "liked-songs", support: "available" },
      { sourceId: "evening", name: "Evening mix", count: 4, kind: "playlist", support: "available" },
    ], nextCursor: null, unsupported: [] });
    if (path === `/api/admin/migrations/${runId}`) return reply({ id: runId,
      run: { id: runId, providerAccountId: accountId, state: "choosing", phase: "collecting", jobId: null },
      collections: [], total: { selected: 0, local: 0, streamed: 0, accepted: 0, review: 0, unmatched: 0, unsupported: 0, skipped: 0 }, provisional: false });
    if (path === "/api/admin/history/imports") return reply([history]);
    if (path === "/api/admin/stats") return reply({
      period: { from: null, to: "2026-10-28T12:00:00Z", timeZoneId: "UTC" }, playCount: 24,
      activeDays: 2, importedCount: 24, observedSince: null, estimatedTimeFromSongLengthsMilliseconds: 3600000,
      coverage: { unknownSongIdentityCount: 0, unknownDurationCount: 0 }, topSongs: [], topArtists: [], topAlbums: [],
    });
    if (path === "/api/admin/providers/status") return reply([]);
    if (["/api/admin/ui/provider-summaries", "/api/admin/providers/use"].includes(path)) return reply({ providers: [] });
    if (path === "/api/admin/provider-diagnostics/deep-stream/latest") return reply({ measurements: [] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return reply({});
  });
}

async function expectOutline(control: Locator) {
  await expect(control).toBeVisible();
  await expect.poll(() => control.evaluate(element => {
    const style = getComputedStyle(element);
    const canvas = document.createElement("canvas");
    canvas.width = canvas.height = 1;
    const context = canvas.getContext("2d")!;
    return ["Top", "Right", "Bottom", "Left"].every(side => {
      const width = style.getPropertyValue(`border-${side.toLowerCase()}-width`);
      const kind = style.getPropertyValue(`border-${side.toLowerCase()}-style`);
      context.clearRect(0, 0, 1, 1);
      context.fillStyle = style.getPropertyValue(`border-${side.toLowerCase()}-color`);
      context.fillRect(0, 0, 1, 1);
      return parseFloat(width) > 0 && !["none", "hidden"].includes(kind) && context.getImageData(0, 0, 1, 1).data[3] > 0;
    });
  }), { message: "Each outline edge has width, a visible style and nontransparent color" }).toBe(true);
}

async function expectSignalText(control: Locator) {
  await expect(control).toBeVisible();
  await expect.poll(() => control.evaluate(element => {
    const probe = document.createElement("span");
    probe.style.color = "var(--color-signal-text)";
    element.append(probe);
    const expected = getComputedStyle(probe).color;
    const actual = getComputedStyle(element).color;
    const token = getComputedStyle(element).getPropertyValue("--color-signal-text").trim();
    probe.remove();
    return Boolean(token) && actual === expected;
  }), { message: "Text control uses the resolved signal text color" }).toBe(true);
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Flat controls at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockViews(page);
    const folder = "test-results/flat-buttons";
    await mkdir(folder, { recursive: true });
    const capture = async (view: string) => {
      await page.evaluate(() => { (document.activeElement as HTMLElement)?.blur(); window.scrollTo(0, 0); });
      await page.screenshot({ path: `${folder}/${view}-${width}-${theme}.png`, fullPage: true });
    };

    await page.goto(`/#/library/import?account=${accountId}`);
    await page.getByText("Choose individual collections", { exact: true }).click();
    await expect(page.getByRole("checkbox", { name: "Select Liked Songs", exact: true })).toBeVisible();
    const selectAll = page.getByRole("button", { name: "Select all", exact: true });
    const clear = page.getByRole("button", { name: "Clear", exact: true });
    await expectSignalText(selectAll);
    await expectSignalText(clear);
    await selectAll.click();
    await expect(page.getByRole("checkbox", { name: "Select Liked Songs", exact: true })).toBeChecked();
    await clear.click();
    await expect(page.getByRole("checkbox", { name: "Select Liked Songs", exact: true })).not.toBeChecked();
    await capture("import");

    // Your choices and the household limit share the Playback page.
    await page.goto("/#/settings/playback");
    await expect(page.getByRole("heading", { name: "Playback", level: 2, exact: true })).toBeVisible();
    await capture("preferences");
    await expect(page.getByRole("slider", { name: "Audio quality", exact: true })).toBeVisible();
    const sort = page.getByRole("button", { name: "Sort by score", exact: true });
    await expectSignalText(sort);
    await expect(sort).toHaveAttribute("data-variant", "text");
    const showMore = page.getByRole("button", { name: "Show 1 more", exact: true });
    await expectSignalText(showMore);
    await showMore.click();
    const showFewer = page.getByRole("button", { name: "Show fewer", exact: true });
    await expectSignalText(showFewer);
    await expect(page.locator(".routing-group li")).toHaveCount(6);
    await showFewer.click();
    await expect(page.locator(".routing-group li")).toHaveCount(5);
    await page.locator(".routing-group li.has-score .routing-copy").click();
    const details = page.getByRole("dialog", { name: "Lumen Audio", exact: true });
    const close = details.getByRole("button", { name: "Close", exact: true });
    await expect(close).toBeVisible();
    await expect(close.locator("svg")).toHaveAttribute("aria-hidden", "true");
    await close.click();
    await expect(details).toBeHidden();
    await capture("quality");

    await page.goto("/#/for-you?section=recap");
    await page.locator(".recap-imports > summary").click();
    const imports = page.getByRole("region", { name: "Listening history from other services", exact: true });
    await expect(imports).toContainText("Done");
    const remove = imports.getByRole("button", { name: "Delete", exact: true });
    await expect(remove).toBeVisible();
    expect(await remove.evaluate(element => {
      const probe = document.createElement("span"); probe.style.color = "var(--danger)"; element.append(probe);
      const matches = getComputedStyle(element).color === getComputedStyle(probe).color; probe.remove(); return matches;
    })).toBe(true);
    await expect(remove.locator("svg")).toBeVisible();
    await expect(remove.locator("svg")).toHaveAttribute("aria-hidden", "true");
    await capture("recap");
    expect(await page.pageErrors()).toEqual([]);
  });
}
