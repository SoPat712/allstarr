import { expect, test } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";
import { schema, responses, routeRelease, mockApi } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

for (const theme of ["light", "dark"] as const) {
  for (const state of ["streaming", "cached", "unknown", "library", "unavailable"] as const) {
    test(`Home playback source labels remain honest and contained — ${theme} ${state}`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
      await page.setViewportSize({ width: 390, height: 844 });
      await mockApi(page);
      const fixture = structuredClone(responses["/api/admin/ui/now-playing"]) as { items: Record<string, unknown>[] };
      fixture.items[0].sourceConfirmed = state !== "unknown";
      if (state === "library") fixture.items[0].providerId = "jellyfin";
      if (state === "unavailable") fixture.items[0].providerId = "unknown";
      fixture.items[0].cached = state === "cached";
      fixture.items[0].seekRung = state === "cached" ? "progressive-cache" : "sequential";
      await page.route("**/api/admin/ui/now-playing", (route) => route.fulfill({ json: fixture }));
      await page.goto("#/");
      const playing = page.getByRole("region", { name: "Now playing" });
      if (state === "unavailable") await expect(playing.locator(".playback-meta")).toHaveText("Tester on Feishin, Desktop");
      else await expect(playing.locator(".playback-meta")).toContainText(state === "unknown" ? "catalog: Lumen Audio (playback unconfirmed)" : state === "library" ? "from your library" : "from Lumen Audio");
      await expect(playing.locator(".playback-meta")).not.toContainText("Unknown source");
      await expect(playing.locator(".listener-profile, .playback-route")).toHaveCount(0);
      await expectContainedMobileGeometry(page);
      await page.screenshot({ path: `test-results/playback-source-${theme}-${state}.png`, fullPage: true });
      await page.setViewportSize({ width: 1280, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(1280);
      expect(await playing.evaluate((panel) => panel.scrollWidth <= panel.clientWidth)).toBe(true);
    });
  }
}

test("Home shows no account count for sources that need no account", async ({ page }) => {
  await mockApi(page);
  const definitions = [
    { id: "artistgrid", name: "ArtistGrid" }, { id: "genius", name: "Genius" },
    { id: "deezer", name: "Deezer" }, { id: "spotify", name: "Spotify" },
  ];
  const needsAccount: Record<string, boolean | undefined> = { artistgrid: false, genius: false, deezer: true };
  await page.route("**/api/admin/ui/home", (route) => route.fulfill({ json: {
    ...(responses["/api/admin/ui/home"] as Record<string, unknown>), schema: { ...schema, providers: definitions },
    providerHealth: { providers: definitions.map((provider) => ({
      providerId: provider.id, needsAccount: needsAccount[provider.id], connectedAccounts: [],
      enabledAccountCount: 0, capabilityTotal: 0, healthyCapabilityCount: 0, failedCapabilityCount: 0,
    })) },
  } }));
  await page.goto("#/");
  const setup = page.getByRole("region", { name: "Your setup", exact: true });
  const sources = setup.locator(".setup-tile").filter({ has: page.getByRole("heading", { name: "Sources", exact: true }) });
  await expect(sources).toContainText("Not checked");
  await expect(sources.locator(".tile-number")).toHaveText("0");
  await expect(sources.locator(".tile-label")).toHaveText("of 0 working");
  await expect(setup).not.toContainText(/connected accounts/);
});

test("Home stays inside runtime and request budgets", async ({ page }) => {
  const requests: string[] = [];
  page.on("request", (request) => requests.push(new URL(request.url()).pathname));
  await page.addInitScript(() => {
    const metrics = { cls: 0, lcp: 0, navigation: 0 };
    Object.assign(window, { __allstarrMetrics: metrics });
    new PerformanceObserver((list) => {
      metrics.lcp = Math.max(metrics.lcp, ...list.getEntries().map((entry) => entry.startTime));
    }).observe({ type: "largest-contentful-paint", buffered: true });
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) {
        if (!(entry as PerformanceEntry & { hadRecentInput?: boolean }).hadRecentInput) {
          metrics.cls += (entry as PerformanceEntry & { value: number }).value;
        }
      }
    }).observe({ type: "layout-shift", buffered: true });
  });
  await mockApi(page);
  await page.goto("#/");
  await expect(page.getByLabel("Loading Home")).toBeHidden();
  await expect(page.getByRole("region", { name: "Your setup", exact: true }).getByRole("heading", { name: "Library", exact: true })).toBeVisible();
  await expect(page.getByText("Legacy .env import")).toHaveCount(0);
  await expect(page.locator(".setup-tile").filter({ hasText: "Sources" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "playlist check", exact: true })).toBeVisible();
  await expect(page.locator(".setup-tile")).toHaveCount(6);
  await expect(page.getByRole("region", { name: "Recent activity", exact: true }).locator("svg").first()).toBeVisible();
  const nowPlaying = page.getByRole("region", { name: "Now playing" });
  await expect(nowPlaying).toContainText("Rocket");
  await expect(nowPlaying.locator(".playback-meta")).toContainText("Tester on Feishin, Desktop");
  await expect(nowPlaying.locator(".scrobble-state")).toContainText("Listening");

  const apiRequests = requests.filter((path) => path.startsWith("/api/admin/"));
  expect(apiRequests.length).toBeLessThanOrEqual(19);

  await page.evaluate(() => {
    const metrics = window.__allstarrMetrics;
    const start = { value: 0 };
    document.querySelector('a[href="#/library/playlists"]')?.addEventListener("click", () => {
      start.value = performance.now();
    }, { once: true });
    new MutationObserver((_, observer) => {
      if (document.querySelector("h1")?.textContent === "Library") {
        metrics.navigation = performance.now() - start.value;
        observer.disconnect();
      }
    }).observe(document.body, { childList: true, subtree: true, characterData: true });
  });
  await page.getByRole("link", { name: "Library" }).first().click();
  await expect.poll(() => page.evaluate(() =>
    window.__allstarrMetrics.navigation,
  )).toBeGreaterThan(0);

  const metrics = await page.evaluate(() => window.__allstarrMetrics);
  expect(metrics.lcp).toBeGreaterThan(0);
  expect(metrics.lcp).toBeLessThanOrEqual(2_500);
  expect(metrics.cls).toBeLessThanOrEqual(0.1);
  expect(metrics.navigation).toBeLessThanOrEqual(100);
});

test("Home follows fresh progress, pauses and backward seeks", async ({ page }) => {
  await page.clock.install({ time: new Date("2026-01-01T00:00:00Z") });
  await page.clock.pauseAt(new Date("2026-01-01T00:00:01Z"));
  await page.emulateMedia({ reducedMotion: "no-preference" });
  await mockApi(page);
  let state = { position: 30.25, paused: false, anchor: "2026-01-01T00:00:00Z", captured: "2026-01-01T00:00:00Z" };
  await page.route("**/api/admin/ui/now-playing", route => {
    const fixture = structuredClone(responses["/api/admin/ui/now-playing"]) as { items: Record<string, unknown>[] };
    Object.assign(fixture.items[0], { positionSeconds: state.position, isPaused: state.paused, positionUpdatedAt: state.anchor });
    return route.fulfill({ json: { ...fixture, capturedAt: state.captured } });
  });
  await page.goto("#/");
  const playing = page.getByRole("region", { name: "Now playing" });
  await expect(playing.locator(".playback-time")).toHaveText("0:30 / 2:00");
  await page.clock.runFor(2_000);
  await expect(playing.locator(".playback-time")).toHaveText("0:32 / 2:00");
  state = { position: 32.25, paused: true, anchor: "2026-01-01T00:00:02Z", captured: "2026-01-01T00:00:05Z" };
  await page.clock.runFor(3_000);
  await expect(playing.locator(".scrobble-state")).toHaveText("Paused");
  await page.clock.runFor(4_000);
  await expect(playing.locator(".playback-time")).toHaveText("0:32 / 2:00");
  state = { position: 10.25, paused: false, anchor: "2026-01-01T00:00:10Z", captured: "2026-01-01T00:00:10Z" };
  await page.clock.runFor(1_000);
  await expect(playing.locator(".playback-time")).toHaveText("0:10 / 2:00");
  await page.clock.runFor(2_000);
  await expect(playing.locator(".playback-time")).toHaveText("0:12 / 2:00");
});

test("Home keeps unknown and stale progress honest across clock skew", async ({ page }) => {
  await page.clock.install({ time: new Date("2026-01-01T00:00:00Z") });
  await page.clock.pauseAt(new Date("2026-01-01T00:00:01Z"));
  await mockApi(page);
  const fixture = structuredClone(responses["/api/admin/ui/now-playing"]) as { items: Record<string, unknown>[] };
  const base = fixture.items[0];
  fixture.items = [
    { ...base, deviceId: "unknown", positionSeconds: null, durationSeconds: null, progress: null, isPaused: null, positionUpdatedAt: null },
    { ...base, deviceId: "stale", title: "Stale report", positionSeconds: 30.25, isPaused: false, positionUpdatedAt: "2026-01-01T00:00:00Z" },
  ];
  await page.route("**/api/admin/ui/now-playing", route => route.fulfill({ json: {
    ...fixture, capturedAt: "2026-01-01T00:00:20Z",
  } }));
  await page.goto("#/");
  const cards = page.getByRole("region", { name: "Now playing" }).locator(".now-playing-card");
  await expect(cards.first().locator(".playback-time")).toHaveCount(0);
  await expect(cards.first().getByRole("progressbar")).not.toHaveAttribute("aria-valuenow");
  await expect(cards.last().locator(".playback-time")).toHaveText("~0:45 / 2:00");
  await expect(cards.last().locator(".scrobble-state")).toHaveText("Awaiting update");
  await page.clock.runFor(4_000);
  await expect(cards.last().locator(".playback-time")).toHaveText("~0:45 / 2:00");
});

test("Home does not report silence when playback status is unavailable", async ({ page }) => {
  await mockApi(page, { fail: ["/api/admin/ui/now-playing"] });
  await page.goto("#/");
  const playing = page.getByRole("region", { name: "Now playing" });
  await expect(playing.getByRole("heading", { name: "Playback status unavailable" })).toBeVisible();
  await expect(playing.getByRole("heading", { name: "Nothing is playing" })).toHaveCount(0);
  await expect(playing.getByRole("button", { name: "Try again", exact: true })).toBeEnabled();
});

test("Home keeps newer playback progress when an older poll finishes late", async ({ page }) => {
  await page.clock.install({ time: new Date("2026-01-01T00:00:00Z") });
  await page.clock.pauseAt(new Date("2026-01-01T00:00:01Z"));
  await page.addInitScript(() => {
    const consumed: number[] = [];
    Object.assign(window, { __consumedPlaybackPositions: consumed });
    const fetchResponse = window.fetch.bind(window);
    window.fetch = async (...args) => {
      const response = await fetchResponse(...args);
      if (new URL(response.url).pathname === "/api/admin/ui/now-playing") {
        const read = response.json.bind(response);
        response.json = async () => {
          const body = await read();
          consumed.push(body.items[0].positionSeconds);
          return body;
        };
      }
      return response;
    };
  });
  await mockApi(page);
  const older = routeRelease();
  let reads = 0;
  await page.route("**/api/admin/ui/now-playing", async route => {
    const request = ++reads;
    const fixture = structuredClone(responses["/api/admin/ui/now-playing"]) as { items: Record<string, unknown>[] };
    const position = request === 1 ? 30 : request === 2 ? 40 : 50;
    fixture.items[0].positionSeconds = position;
    fixture.items[0].progress = position / 120;
    if (request === 2) await older.promise;
    return route.fulfill({ json: fixture });
  });
  await page.goto("#/");
  const playing = page.getByRole("region", { name: "Now playing" });
  await expect(playing).toContainText("0:30 / 2:00");
  await page.clock.runFor(10_010);
  await expect(playing).toContainText("0:50 / 2:00");
  const stale = page.waitForResponse(async response => new URL(response.url()).pathname === "/api/admin/ui/now-playing" &&
    (await response.json()).items[0].positionSeconds === 40);
  older.release();
  await (await stale).finished();
  await expect.poll(() => page.evaluate(() =>
    (window as unknown as { __consumedPlaybackPositions: number[] }).__consumedPlaybackPositions.includes(40))).toBe(true);
  await expect(playing).toContainText("0:50 / 2:00");
  await expect(playing.getByRole("progressbar")).toHaveAttribute("aria-valuenow", "42");
});

test("Home accepts useful progress while a newer poll is still pending", async ({ page }) => {
  await page.clock.install({ time: new Date("2026-01-01T00:00:00Z") });
  await page.clock.pauseAt(new Date("2026-01-01T00:00:01Z"));
  await mockApi(page);
  const first = routeRelease();
  const second = routeRelease();
  let reads = 0;
  await page.route("**/api/admin/ui/now-playing", async route => {
    const request = ++reads;
    const fixture = structuredClone(responses["/api/admin/ui/now-playing"]) as { items: Record<string, unknown>[] };
    fixture.items[0].positionSeconds = request === 1 ? 30 : request === 2 ? 40 : 50;
    if (request === 2) await first.promise;
    if (request === 3) await second.promise;
    return route.fulfill({ json: fixture });
  });
  await page.goto("#/");
  const time = page.getByRole("region", { name: "Now playing" }).locator(".playback-time");
  await expect(time).toHaveText("0:30 / 2:00");
  await page.clock.runFor(11_000);
  await expect.poll(() => reads).toBe(3);
  first.release();
  await expect(time).toHaveText("0:40 / 2:00");
  second.release();
  await expect(time).toHaveText("0:50 / 2:00");
});

for (const width of [390, 1280]) {
  for (const theme of ["light", "dark"] as const) {
    test(`Home pause and stale progress remain contained at ${width}px in ${theme}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
      await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
      await mockApi(page);
      let paused = true;
      await page.route("**/api/admin/ui/now-playing", route => {
        const fixture = structuredClone(responses["/api/admin/ui/now-playing"]) as { items: Record<string, unknown>[] };
        Object.assign(fixture.items[0], { positionSeconds: 30.25, isPaused: paused, positionUpdatedAt: "2026-01-01T00:00:00Z" });
        return route.fulfill({ json: { ...fixture, capturedAt: "2026-01-01T00:00:20Z" } });
      });
      await page.goto("#/");
      const playing = page.getByRole("region", { name: "Now playing" });
      await expect(playing.locator(".scrobble-state")).toHaveText("Paused");
      await expect(playing.locator(".playback-bar > span")).toHaveCSS("transition-property", "none");
      if (width === 390) await expectContainedMobileGeometry(page);
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
      await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR ?? "test-results"}/home-paused-${width}-${theme}.png`, fullPage: true });
      paused = false;
      await page.reload();
      await expect(playing.locator(".scrobble-state")).toHaveText("Awaiting update");
      await expect(playing.locator(".playback-time")).toHaveText("~0:45 / 2:00");
      if (width === 390) await expectContainedMobileGeometry(page);
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
      await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR ?? "test-results"}/home-stale-${width}-${theme}.png`, fullPage: true });
      if (width === 390) {
        await page.setViewportSize({ width: 320, height: 844 });
        await expectContainedMobileGeometry(page);
      }
    });
  }
}
