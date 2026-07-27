import { expect, test, type Page } from "@playwright/test";

declare global {
  interface Window {
    __allstarrMetrics: { cls: number; lcp: number; inp: number; navigation: number };
  }
}

const viewports = [
  { width: 390, height: 844 },
  { width: 768, height: 1024 },
  { width: 1280, height: 800 },
  { width: 1440, height: 900 },
];

const schema = {
  activeBackend: "Jellyfin",
  providers: [
    { id: "jellyfin", name: "Jellyfin", categories: ["streaming"] },
    {
      id: "lumen-audio", name: "Lumen Audio", categories: ["metadata", "streaming"],
      accountSettings: [{ key: "token", label: "Access token", type: "password", sensitive: true, required: true }],
    },
  ],
  configSections: [{
    id: "general", label: "General", fields: [
      { key: "Theme", label: "Theme", type: "select", valuePath: "general.theme", options: ["Dark"] },
      { key: "PublicUrl", label: "Public URL", type: "text", valuePath: "deployment.url", ownership: "deployment", readOnly: true },
    ],
  }],
  priorityGroups: [{
    id: "streaming", label: "Playback", envKey: "StreamingOrder", providers: ["lumen-audio"],
    pinnedProvider: { id: "jellyfin", name: "Jellyfin", icon: "server", reason: "Local media server" },
  }],
};

const responses: Record<string, unknown> = {
  "/api/admin/auth/me": {
    authenticated: true, backend: "Jellyfin",
    user: {
      id: "user", name: "Tester", isAdministrator: true,
      avatarUrl: "/api/admin/auth/me/avatar?user=user",
    },
  },
  "/api/admin/ui/schema": schema,
  "/api/admin/status": { version: "test", backendType: "Jellyfin" },
  "/api/admin/playlists": { playlists: [], inventory: { managed: 0, unmanaged: 0 } },
  "/api/admin/jobs?limit=100": {
    jobs: [{
      id: "11111111-1111-1111-1111-111111111111", type: "playlist.materialize",
      state: "Running", attemptCount: 1, failureCount: 0, deferralCount: 0,
      cancellationRequestedAt: null, lastErrorCode: null, lastErrorMessage: null,
      updatedAt: "2026-01-01",
    }],
    progress: [{
      id: "progress", jobId: "11111111-1111-1111-1111-111111111111",
      action: "playlist.match", outcome: "running", createdAt: "2026-01-01",
      detailsJson: JSON.stringify({
        stage: "playlist.match", message: "Matching Test song", completed: 1, total: 2,
        provider: "lumen-audio", playlist: "Test playlist", track: "Test song",
        throughputPerSecond: 1.5,
      }),
    }],
  },
  "/api/admin/ui/activity?limit=8": { items: [], hasMore: false },
  "/api/admin/ui/provider-summaries": { providers: [] },
  "/api/admin/playlist-links": {
    playlistLinks: [{
      id: "playlist-link", enabled: true, name: "Test playlist",
      sourceProviderId: "lumen-audio", targetProtocol: "jellyfin",
      materializationMode: "reconcile", revision: 1, trackCount: 1,
      matchedCount: 0, unmatchedCount: 0, playableCount: 1, materializedCount: 1,
      routeCoverage: [{ providerId: "lumen-audio", count: 1 }],
      metrics: { total: 1, matched: 0, unresolved: 0, review: 1, rejected: 0, playable: 1, materialized: 1 },
    }],
  },
  "/api/admin/playlist-sources": {
    accounts: [
      { id: "qobuz", providerId: "qobuz", displayName: "Qobuz", accessLabel: "Personal account" },
      { id: "spotify", providerId: "spotify", displayName: "Spotify", ownerDisplayName: "Tester", accessLabel: "Personal account" },
      { id: "lumen", providerId: "lumen-audio", displayName: "Lumen", accessLabel: "Personal account" },
      { id: "subsonic", providerId: "subsonic", displayName: "Subsonic", accessLabel: "Library-shared account" },
      { id: "jellyfin", providerId: "jellyfin", displayName: "Jellyfin", accessLabel: "Library-shared account" },
    ],
    blockedAccounts: [],
    providers: [
      { id: "jellyfin", displayName: "Jellyfin" },
      { id: "subsonic", displayName: "Subsonic" },
      { id: "lumen-audio", displayName: "Lumen Audio" },
      { id: "qobuz", displayName: "Qobuz" },
      { id: "spotify", displayName: "Spotify" },
    ],
  },
  "/api/admin/media-targets": {
    targets: [{
      id: "target", protocol: "jellyfin", backendInstanceId: "main",
      displayName: "Jellyfin Music",
    }],
  },
  "/api/admin/provider-accounts": {
    managementMode: "ApplicationManaged",
    audienceUsers: [
      { id: "user", displayName: "Tester" },
      { id: "listener", displayName: "Listener" },
    ],
    accounts: [{
      id: "account", providerId: "lumen-audio", displayName: "Lumen account",
      sourceDisplayName: "Lumen Audio", scope: "User", enabled: true, revision: 1,
      ownerUserId: "user", ownerDisplayName: "Tester", createdByUserId: "user",
      creatorDisplayName: "Tester",
      secret: { configured: true, revoked: false }, createdAt: "2026-01-01", updatedAt: "2026-01-01",
    }],
  },
  "/api/admin/providers/status": [],
  "/api/admin/provider-diagnostics/deep-stream/latest": { measurements: [] },
  "/api/admin/config": {
    general: { theme: "Dark" }, deployment: { url: "https://music.example.test" },
    providers: { streamingOrder: "lumen-audio" },
  },
  "/api/admin/storage": { storage: { provider: "PostgreSQL", readiness: "Ready" }, backups: [] },
  "/api/admin/cache": {
    database: { entryCount: 0, payloadBytes: 0, hitRatio: 0 },
    hot: { entryCount: 0, payloadBytes: 0, hitRatio: 0 },
    media: { entryCount: 0, payloadBytes: 0, hitRatio: 0 },
    activity: { coalescedRequests: 0, staleServes: 0, upstreamBytesAvoided: 0 },
    extensionStorage: { activeExtensions: 0, entryCount: 0, payloadBytes: 0, maximumBytes: 0 },
    capturedAt: "2026-01-01",
  },
  "/api/admin/cache/maintenance/preview": {
    metadata: { expiredEntries: 0, overQuotaEntries: 0, reclaimableBytes: 0 },
    media: { expiredEntries: 0, overQuotaEntries: 0, reclaimableBytes: 0 },
    unreferencedArtworkPayloads: 0, unreferencedArtworkBytes: 0,
  },
  "/api/admin/extensions/packages": [{
    id: "package", extensionId: "lumen-audio", displayName: "Lumen Audio", version: "1.0.0",
    lifecycle: "active", state: "active", active: true, installed: true,
    permissionReviewRequired: false, capabilities: ["metadata", "streaming"], revision: 1,
  }],
  "/api/admin/extensions/registries": [],
  "/api/admin/extensions/store": { items: [], errors: [] },
  "/api/admin/extensions/logs?limit=100": [],
};

async function mockApi(page: Page, options: { delay?: string; fail?: string[] } = {}) {
  await page.route("**/fonts/**", (route) => route.fulfill({ status: 204 }));
  await page.route("**/api/admin/**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === options.delay) await new Promise((resolve) => setTimeout(resolve, 500));
    if (options.fail?.includes(url.pathname)) {
      await route.fulfill({
        status: 503,
        contentType: "application/json",
        body: JSON.stringify({ error: "Fixture unavailable" }),
      });
      return;
    }
    let body = responses[`${url.pathname}${url.search}`] ?? responses[url.pathname];
    if (url.pathname === "/api/admin/ui/activity") body = { items: [], hasMore: false };
    if (url.pathname === "/api/admin/downloads")
      body = {
        storage: url.searchParams.get("storage"),
        files: [{
          path: "/managed/song.flac", storage: url.searchParams.get("storage"), artist: "Artist",
          album: "Album", title: "Test song", fileName: "song.flac", size: 1_024_000,
          sizeFormatted: "1000 KiB", lastModified: "2026-01-01", codec: "FLAC",
          bitrateKbps: 900, sampleRateHz: 44_100, bitDepth: 16, channels: 2,
          durationMilliseconds: 180_000, quality: "Lossless", provider: "lumen-audio",
          externalId: "track-1",
        }],
        totalSize: 1_024_000, totalSizeFormatted: "1000 KiB", count: 1,
      };
    if (url.pathname === "/api/admin/track-matches")
      body = {
        matches: [{
          externalSnapshotId: "snapshot", providerId: "lumen-audio", libraryScopeId: "library",
          state: "suggested", decisionSource: "automatic", confidence: 0.82, threshold: 0.9,
          title: "Test song", artist: "Artist", album: "Album", isrc: "US-AAA-26-00001",
          durationMilliseconds: 180_000,
          providerIdentities: [], reasons: ["title_match"], warnings: [], candidates: [{
            libraryTrackId: "local-track", backendItemId: "backend-track", title: "Test song",
            artist: "Artist", album: "Album", candidateIsrc: "US-AAA-26-00001",
            providerTrackIds: { "lumen-audio": "provider-track" },
            confidence: 0.82, durationMilliseconds: 180_000, components: { title: 1 },
          }],
        }],
        stats: { total: 1, matched: 0, accepted: 0, unresolved: 0, suggested: 1, review: 1, rejected: 0, attention: 1 },
        pagination: { page: 1, pageSize: 50, total: 1, totalPages: 1 },
      };
    if (url.pathname === "/api/admin/playlist-links/playlist-link")
      body = {
        id: "playlist-link", snapshotId: "playlist-snapshot", snapshotVersion: 1,
        name: "Test playlist", sourceProviderId: "lumen-audio", targetProtocol: "jellyfin",
        retrievedAt: "2026-01-01", completedAt: "2026-01-01", trackCount: 1,
        localCount: 0, externalCount: 1, unresolvedCount: 0, durationMs: 180_000,
        routeCoverage: [{ providerId: "lumen-audio", count: 1 }],
        unknownDurationCount: 0, tracks: [{
          position: 1, externalSnapshotId: "snapshot", title: "Test song",
          artists: ["Artist"], album: "Album", isrc: "US-AAA-26-00001",
          durationMs: 180_000, routeKind: "external", routeProviderId: "lumen-audio",
          matchState: "suggested", providerRoutes: [{ providerId: "lumen-audio", externalId: "provider-track", pinned: false }],
        }],
      };
    if (url.pathname.includes("/api/admin/playlist-sources/") && url.pathname.endsWith("/playlists"))
      body = {
        items: [{
          id: url.searchParams.has("cursor") ? "playlist-2" : "playlist",
          providerId: url.pathname.split("/")[4],
          name: url.searchParams.has("cursor") ? "Second Mix" : "Source Mix",
          owner: "Tester", trackCount: 24,
        }],
        nextCursor: url.searchParams.has("cursor") ? null : "next",
      };
    if (url.pathname === "/api/admin/playlist-links" && route.request().method() === "POST")
      body = { id: "new-playlist" };
    if (url.pathname.endsWith("/run") && route.request().method() === "POST")
      body = { jobId: "11111111-1111-1111-1111-111111111111", created: true };
    if (url.pathname.endsWith("/cancel") && route.request().method() === "POST")
      body = { jobId: "11111111-1111-1111-1111-111111111111", state: "CancellationRequested" };
    if (url.pathname.endsWith("/audience") && route.request().method() === "PUT") {
      const input = route.request().postDataJSON();
      const account = (responses["/api/admin/provider-accounts"] as { accounts: Record<string, unknown>[] }).accounts[0];
      body = { ...account, scope: input.scope, ownerUserId: input.ownerUserId, revision: 2 };
    }
    await route.fulfill({
      status: body === undefined ? 404 : 200,
      contentType: "application/json",
      body: JSON.stringify(body ?? { error: `Missing fixture: ${url.pathname}` }),
    });
  });
}

const routes = [
  ["#/", "Home"],
  ["#/library/playlists", "Library"],
  ["#/library/mappings", "Library"],
  ["#/library/cached", "Library"],
  ["#/library/kept", "Library"],
  ["#/activity", "Activity"],
  ["#/sources", "Sources"],
  ["#/settings/general", "Settings"],
] as const;

const stateRoutes = [
  ["#/", "Home", "Loading Home", "/api/admin/status", [
    "/api/admin/ui/schema", "/api/admin/status", "/api/admin/playlists", "/api/admin/playlist-links", "/api/admin/jobs",
    "/api/admin/ui/activity", "/api/admin/ui/provider-summaries",
  ]],
  ["#/library/playlists", "Library", "Loading playlists", "/api/admin/playlist-links", ["/api/admin/playlist-links"]],
  ["#/library/mappings", "Library", "Loading match review", "/api/admin/track-matches", ["/api/admin/track-matches"]],
  ["#/library/cached", "Library", "Loading Cached tracks", "/api/admin/downloads", ["/api/admin/downloads"]],
  ["#/activity", "Activity", "Loading Event log", "/api/admin/ui/activity", ["/api/admin/ui/activity"]],
  ["#/sources", "Sources", "Loading Sources", "/api/admin/provider-accounts", ["/api/admin/ui/schema", "/api/admin/provider-accounts"]],
  ["#/settings/general", "Settings", "Loading Settings", "/api/admin/ui/schema", ["/api/admin/ui/schema"]],
] as const;

for (const viewport of viewports) {
  test.describe(`${viewport.width}x${viewport.height}`, () => {
    test.use({ viewport });

    for (const [route, heading] of routes) {
      test(`${route} has no document overflow`, async ({ page }) => {
        const errors: string[] = [];
        page.on("pageerror", (error) => errors.push(error.message));
        await mockApi(page);
        await page.goto(route);
        await expect(page.getByRole("heading", { name: heading, level: 1 })).toBeVisible();
        await expect.poll(() => page.evaluate(() =>
          document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
        expect(errors).toEqual([]);
      });
    }

    for (const [route, heading, loadingLabel, delay, failures] of stateRoutes) {
      test(`${route} exposes loading and error recovery`, async ({ page, context }) => {
        await mockApi(page, { delay });
        await page.goto(route);
        await expect(page.getByLabel(loadingLabel)).toBeVisible();
        await expect(page.getByRole("heading", { name: heading, level: 1 })).toBeVisible();

        const errorPage = await context.newPage();
        await mockApi(errorPage, { fail: [...failures] });
        await errorPage.goto(route);
        await expect(errorPage.getByRole("alert")).toBeVisible();
        await expect(errorPage.getByRole("button", { name: "Try again" })).toBeInViewport();
      });
    }

    test("Settings dialogs remain usable", async ({ page }) => {
      await mockApi(page);
      await page.goto("#/settings/extensions");
      await page.getByRole("button", { name: "Install extension" }).click();
      await expect(page.getByRole("dialog", { name: "Install extension" })).toBeVisible();
      await expect(page.getByRole("button", { name: "Verify package" })).toBeInViewport();
      await page.getByRole("button", { name: "Close installer" }).click();
      await page.goto("#/settings/maintenance");
      await page.getByRole("button", { name: "Purge cache" }).click();
      const dialog = page.getByRole("alertdialog", { name: "Purge the application cache?" });
      await expect(dialog).toBeVisible();
      await expect(dialog.getByRole("button", { name: "Purge cache" })).toBeInViewport();
    });

    test("Source dialogs remain usable", async ({ page }) => {
      await mockApi(page);
      await page.goto("#/sources");
      await page.getByRole("button", { name: "Connect Source" }).click();
      await expect(page.getByRole("dialog", { name: "Connect a Source" })).toBeVisible();
      await expect(page.getByRole("button", { name: "Save and test" })).toBeInViewport();
      await page.getByRole("button", { name: "Close source connection dialog" }).click();
      await page.getByRole("button", { name: "Audience Only Tester" }).click();
      const access = page.getByRole("dialog", { name: "Lumen Audio" });
      await expect(access).toBeVisible();
      await access.getByRole("radio", { name: "One user" }).check();
      await access.getByRole("combobox").selectOption("listener");
      await access.getByRole("button", { name: "Save access" }).click();
      await expect(access).toBeHidden();

      await page.getByRole("button", { name: "Audience Only Tester" }).click();
      await page.getByRole("radio", { name: "One library" }).check();
      await page.getByLabel("Library ID").fill("music");
      await page.getByRole("button", { name: "Save access" }).click();
      const libraryShare = page.getByRole("alertdialog", {
        name: "Share this connection with a library?",
      });
      await expect(libraryShare).toBeVisible();
      await libraryShare.getByRole("button", { name: "Keep current access" }).click();
      await page.getByRole("radio", { name: "Everyone" }).check();
      await page.getByRole("button", { name: "Save access" }).click();
      await expect(page.getByRole("alertdialog", { name: "Share this connection with everyone?" })).toBeVisible();
    });

    test("Match and removal dialogs remain usable", async ({ page }) => {
      await mockApi(page);
      await page.goto("#/library/mappings");
      await page.getByRole("button", { name: "Review match" }).click();
      await expect(page.getByRole("dialog", { name: "Test song" })).toBeVisible();
      await expect(page.getByRole("button", { name: "Reject candidate" })).toBeInViewport();
      await page.getByRole("button", { name: "Reject candidate" }).click();
      const reject = page.getByRole("alertdialog", { name: "Reject this candidate?" });
      await expect(reject).toBeVisible();
      await expect(reject.getByRole("button", { name: "Reject candidate" })).toBeInViewport();

      await page.goto("#/library/cached");
      await page.getByRole("button", { name: "Remove", exact: true }).click();
      const removal = page.getByRole("alertdialog", { name: "Remove this track?" });
      await expect(removal).toBeVisible();
      await expect(removal.getByRole("button", { name: "Remove track" })).toBeInViewport();
    });

    test("keyboard dialogs honor reduced motion", async ({ page }) => {
      await page.emulateMedia({ reducedMotion: "reduce" });
      await mockApi(page);
      await page.goto("#/settings/extensions");
      await expect.poll(() => page.locator(".settings-tabs > .segmented-tab-indicator").evaluate((element) =>
        Number.parseFloat(getComputedStyle(element).transitionDuration))).toBeLessThanOrEqual(0.01);
      await page.getByRole("button", { name: "Install extension" }).click();
      const dialog = page.getByRole("dialog", { name: "Install extension" });
      await expect(dialog).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(dialog).toBeHidden();
    });
  });
}

test("Home stays inside runtime and request budgets", async ({ page }) => {
  const requests: string[] = [];
  page.on("request", (request) => requests.push(new URL(request.url()).pathname));
  await page.addInitScript(() => {
    const metrics = { cls: 0, lcp: 0, inp: 0, navigation: 0 };
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
    new PerformanceObserver((list) => {
      metrics.inp = Math.max(metrics.inp, ...list.getEntries().map((entry) => entry.duration));
    }).observe({ type: "event", buffered: true, durationThreshold: 16 } as PerformanceObserverInit);
  });
  await mockApi(page);
  await page.goto("#/");
  await expect(page.getByLabel("Loading Home")).toBeHidden();

  const apiRequests = requests.filter((path) => path.startsWith("/api/admin/"));
  const jsRequests = requests.filter((path) => path.endsWith(".js"));
  expect(apiRequests.length).toBeLessThanOrEqual(14); // Lit Home baseline.
  expect(jsRequests.length).toBeLessThanOrEqual(13); // Shell, Home, and one shared primitive.

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
  expect(metrics.inp).toBeGreaterThan(0);
  expect(metrics.lcp).toBeLessThanOrEqual(2_500);
  expect(metrics.inp).toBeLessThanOrEqual(200);
  expect(metrics.cls).toBeLessThanOrEqual(0.1);
  expect(metrics.navigation).toBeLessThanOrEqual(100);
});

test("extension updates explain access changes on mobile", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  const basePackage = {
    extensionId: "lumen-audio", displayName: "Lumen Audio", lifecycle: "disabled",
    active: false, installed: true, permissionReviewRequired: false, capabilities: ["metadata"],
    revision: 1,
  };
  await page.route("**/api/admin/extensions/packages", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([
      { ...basePackage, id: "old", version: "1.0.0", state: "disabled", stagedAt: "2026-01-01" },
      {
        ...basePackage, id: "new", version: "2.0.0", state: "reviewRequired",
        previousPackageId: "old", permissionReviewRequired: true,
        capabilities: ["metadata", "streaming"], stagedAt: "2026-02-01",
      },
    ]),
  }));
  const permission = (id: string, kind: string, value: string) => ({
    id, permissionKind: kind, permissionValue: value, required: true, decision: "pending",
  });
  await page.route("**/packages/old/permissions", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([permission("old-network", "network", "https://api.example.test/"), permission("old-cache", "cache", "metadata")]),
  }));
  await page.route("**/packages/new/permissions", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([permission("new-network", "network", "https://api.example.test/"), permission("new-secret", "secret", "accountToken")]),
  }));
  await page.route("**/packages/new/review", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      ...basePackage, id: "new", version: "2.0.0", state: "staged",
      previousPackageId: "old", revision: 2,
    }),
  }));
  let activationRequests = 0;
  let releaseActivation = () => {};
  const activationResponse = new Promise<void>((resolve) => {
    releaseActivation = resolve;
  });
  await page.route("**/packages/new/activate", async (route) => {
    activationRequests++;
    if (activationRequests === 1) {
      await activationResponse;
      return route.fulfill({
        status: 500,
        contentType: "application/json",
        body: JSON.stringify({ error: "Runtime failed to start." }),
      });
    }
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        ...basePackage, id: "new", version: "2.0.0", state: "active",
        active: true, previousPackageId: "old", revision: 3,
      }),
    });
  });

  await page.goto("#/settings/extensions");
  await page.getByRole("button", { name: "Review permissions" }).click();
  let review = page.getByRole("dialog", { name: "Review permissions" });
  await page.keyboard.press("Escape");
  await expect(review).toBeHidden();
  await page.getByRole("button", { name: "Review permissions" }).click();
  review = page.getByRole("dialog", { name: "Review permissions" });
  await expect(review.getByText("Update 1.0.0 → 2.0.0. Capability and permission changes are shown below.")).toBeVisible();
  await expect(review.getByText("New access", { exact: false })).toBeVisible();
  await expect(review.getByText("Removed access", { exact: false })).toBeVisible();
  await expect(review.getByRole("button", { name: "Save review" })).toBeInViewport();
  for (const button of await review.getByRole("button", { name: "Allow" }).all())
    await button.click();
  await review.getByRole("checkbox").check();
  await review.getByRole("button", { name: "Save review" }).click();
  const activation = page.getByRole("alertdialog", { name: "Activate Lumen Audio?" });
  await expect(activation).toBeVisible();
  expect(activationRequests).toBe(0);
  await activation.getByRole("button", { name: "Activate extension" }).click();
  await expect(activation.getByRole("button", { name: "Activating…" })).toBeDisabled();
  await expect.poll(() => activationRequests).toBe(1);
  releaseActivation();
  await expect(activation.getByRole("alert")).toHaveText("Runtime failed to start.");
  await activation.getByRole("button", { name: "Activate extension" }).click();
  await expect(activation).toBeHidden();
  expect(activationRequests).toBe(2);
});

test("extension updates stay beside the shared management menu", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.route("**/api/admin/extensions/packages", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([{
      id: "old", extensionId: "lumen-audio", displayName: "Lumen Audio",
      version: "1.0.0", lifecycle: "active", state: "active", active: true,
      installed: true, permissionReviewRequired: false, capabilities: ["metadata"],
      previousPackageId: "previous", stagedAt: "2026-01-01", revision: 1,
    }]),
  }));
  await page.route("**/api/admin/extensions/store", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      items: [{
        id: "lumen-audio", displayName: "Lumen Audio", version: "2.0.0",
        downloadUrl: "https://example.test/lumen.zip", sha256: "a".repeat(64),
        registryId: null, types: ["metadata", "streaming"],
      }],
      errors: [],
    }),
  }));

  await page.goto("#/settings/extensions");
  const actions = page.locator(".extension-actions");
  await expect(actions.getByRole("button", { name: "Update" })).toBeInViewport();
  await actions.getByRole("button", { name: "Manage extension" }).click();
  await expect(page.getByRole("menuitem", { name: "Disable" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Rollback" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Review access" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Uninstall" })).toBeVisible();
});

test("Add playlist prioritizes local and configured Sources on mobile", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Add playlist" }).click();
  const dialog = page.getByRole("dialog", { name: "Add playlist" });
  await expect(dialog).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  await page.getByRole("button", { name: "Add playlist" }).click();
  const sourceGroups = dialog.locator(".playlist-source-groups legend");
  await expect(sourceGroups).toHaveCount(5);
  expect(await sourceGroups.allTextContents()).toEqual([
    "Jellyfin", "Subsonic", "Spotify", "Lumen Audio", "Qobuz",
  ]);
  await dialog.getByRole("radio", { name: /Spotify/ }).check();
  await dialog.getByRole("button", { name: "Load more" }).click();
  await expect(dialog.getByRole("radio", { name: /Second Mix/ })).toBeVisible();
  await dialog.getByRole("radio", { name: /Source Mix/ }).check();
  await expect(dialog.getByRole("button", { name: "Add playlist" })).toBeInViewport();
  await dialog.getByRole("button", { name: "Add playlist" }).click();
  await expect(dialog).toBeHidden();
});

test("Suggested mappings sort by confidence and deep links open review", async ({ page }) => {
  await mockApi(page);
  await page.goto("#/library/mappings?search=Test%20song&review=snapshot");
  const dialog = page.getByRole("dialog", { name: "Test song" });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByText("ISRC US-AAA-26-00001")).toHaveCount(2);
  await expect(dialog.getByText("Lumen Audio · provider-track")).toBeVisible();
  await dialog.getByRole("button", { name: "Close match dialog" }).click();

  const request = page.waitForRequest((item) =>
    item.url().includes("/api/admin/track-matches") &&
    new URL(item.url()).searchParams.get("sort") === "confidence_desc");
  await page.getByLabel("Confidence").selectOption("confidence_desc");
  await request;
  const unresolved = page.waitForRequest((item) =>
    item.url().includes("/api/admin/track-matches") &&
    new URL(item.url()).searchParams.get("state") === "unresolved");
  await page.getByLabel("Status").selectOption("unresolved");
  await unresolved;
  await page.getByRole("button", { name: /Suggested.*High likelihood/ }).click();
  await expect(page.getByRole("button", { name: "Accept" })).toBeVisible();
});

test("Event log groups matching work and preserves actionable history", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.route("**/api/admin/ui/activity?*", (route) => {
    const url = new URL(route.request().url());
    const older = url.searchParams.has("before");
    const items = older
      ? [{
          id: "older", kind: "job", source: "system", label: "Playlist sync",
          state: "succeeded", detail: "Generation 1", occurredAt: "2026-01-01T00:00:00Z",
        }]
      : ["First", "First", "Second"].map((playlistName, index) => ({
          id: `match-${index}`, kind: "matching", source: "lumen-audio",
          providerId: "lumen-audio", label: "Track matched", state: "accepted",
          detail: `Song ${index}`, occurredAt: `2026-01-02T00:00:0${2 - index}Z`,
          correlationId: "job-1", action: "track-match.evaluate", playlistName,
          sourceTitle: `Song ${index}`, targetProviderId: "library",
          targetTitle: `Local Song ${index}`, confidenceLabel: "96%",
          sourceProviderTrackId: `provider-${index}`, backendItemId: `backend-${index}`,
        }));
    return route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        items,
        hasMore: !older,
        nextCursor: older ? null : "2026-01-02T00:00:00Z",
        nextCursorId: older ? null : "match-2",
      }),
    });
  });
  await page.goto("#/activity");

  await expect(page.getByText("Matched 3 tracks across 2 playlists")).toBeVisible();
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  await page.getByLabel("Search").fill("missing event");
  await expect(page.getByText("No events match these filters")).toBeVisible();
  await page.getByRole("button", { name: "Reset filters" }).click();

  await page.getByRole("button", { name: "Load earlier events" }).click();
  await expect(page.getByText("4 events retained in this view")).toBeVisible();
  await page.locator(".event-log-group summary").first().click();
  await expect(page.getByText("Technical details").first()).toBeVisible();
  await page.getByRole("link", { name: "Open related view" }).first().click();
  await expect(page).toHaveURL(/#\/library\/mappings\?search=Song%200$/);
});

test("Cached and Kept keep media facts and actions readable on mobile", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.goto("#/library/cached");
  const cached = page.locator(".download-row");
  await expect(cached.getByText("FLAC · 900 kbps · 16-bit · 44.1 kHz · 2 ch")).toBeVisible();
  await expect(cached.getByRole("cell", { name: "Size 1000 KiB" })).toBeVisible();
  await expect(cached.getByRole("cell", { name: /^Updated / })).toBeVisible();
  await expect(cached.getByRole("link", { name: "Download" })).toBeInViewport();
  await expect(cached.getByRole("button", { name: "Keep" })).toBeInViewport();
  await expect(cached.getByRole("button", { name: "Remove" })).toBeInViewport();

  await page.goto("#/library/kept");
  const kept = page.locator(".download-row");
  await expect(kept.getByRole("button", { name: "Keep" })).toHaveCount(0);
  const downloadWidth = (await kept.getByRole("link", { name: "Download" }).boundingBox())?.width ?? 0;
  const removeWidth = (await kept.getByRole("button", { name: "Remove" }).boundingBox())?.width ?? 0;
  expect(Math.abs(downloadWidth - removeWidth)).toBeLessThanOrEqual(1);
  await kept.getByRole("button", { name: "Remove" }).click();
  await expect(page.getByRole("alertdialog", { name: "Remove this track?" })).toBeVisible();
});

test("Sources keep primary actions visible and report scoped degradation", async ({ page, context }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.goto("#/sources");
  await expect(page.getByRole("button", { name: "Audience Only Tester" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Configure" })).toBeVisible();
  await page.getByRole("button", { name: "Actions for Lumen account" }).click();
  const menu = page.getByRole("menu");
  await expect(menu.getByRole("menuitem", { name: "Configure" })).toHaveCount(0);
  await expect(menu.getByRole("menuitem", { name: "Manage access" })).toHaveCount(0);
  await expect(menu.locator(".bits-menu-item")).toHaveCount(2);
  await expect(menu.getByRole("menuitem", { name: "Disable" })).toBeVisible();
  await expect(menu.getByRole("menuitem", { name: "Remove" })).toBeVisible();

  const listener = await context.newPage();
  await mockApi(listener);
  await listener.route("**/api/admin/auth/me", (route) => route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify({
      authenticated: true, backend: "Jellyfin",
      user: { id: "listener", name: "Listener", isAdministrator: false },
    }),
  }));
  await listener.route("**/api/admin/provider-accounts", (route) => route.fulfill({
    status: 503,
    contentType: "application/json",
    body: JSON.stringify({ error: "Account scope unavailable" }),
  }));
  await listener.goto("#/sources");
  await expect(listener.getByText("Source readiness may be stale.")).toBeVisible();
  await expect(listener.getByText("Connections are administrator-managed")).toBeVisible();
});

test("Settings loads only the active section owners", async ({ page }) => {
  const requests: string[] = [];
  page.on("request", (request) => requests.push(new URL(request.url()).pathname));
  await mockApi(page);
  await page.goto("#/settings/general");
  await expect(page.getByRole("heading", { name: "General", level: 2 })).toBeVisible();
  expect(requests).toContain("/api/admin/config");
  expect(requests).not.toContain("/api/admin/provider-accounts");
  expect(requests).not.toContain("/api/admin/storage");
  expect(requests).not.toContain("/api/admin/cache");

  requests.length = 0;
  await page.getByRole("tab", { name: "Maintenance" }).click();
  await expect(page.getByRole("heading", { name: "Maintenance", level: 2 })).toBeVisible();
  await expect.poll(() => requests.includes("/api/admin/cache/maintenance/preview")).toBe(true);
  expect(requests).toContain("/api/admin/storage");
  expect(requests).toContain("/api/admin/cache");
  expect(requests).not.toContain("/api/admin/config");
  expect(requests).not.toContain("/api/admin/provider-accounts");
});

test("Playlist details use a responsive dialog and track rows open mapping review", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.goto("#/library/playlists");
  await expect(page.locator('.playlist-row [title="Lumen Audio: 1"]')).toBeVisible();
  await page.getByRole("button", { name: /Test playlist/ }).click();
  const dialog = page.getByRole("dialog", { name: "Test playlist" });
  await expect(dialog).toBeVisible();
  await expect(dialog.locator('[title="Lumen Audio: 1"]')).toBeVisible();
  await expect(dialog.getByRole("button", { name: "Sync" })).toBeInViewport();
  await expect(dialog.getByRole("button", { name: "Rematch" })).toBeInViewport();
  await expect(dialog.getByRole("button", { name: "Refresh" })).toBeInViewport();
  await page.setViewportSize({ width: 1280, height: 800 });
  await expect.poll(async () => (await dialog.boundingBox())?.width ?? 0).toBeGreaterThan(900);
  await dialog.getByRole("button", { name: "Open mapping details for Test song" }).click();
  await expect(page).toHaveURL(/#\/library\/mappings\?search=Test%20song&review=snapshot$/);
  await expect(page.getByRole("dialog", { name: "Test song" })).toBeVisible();
});

test("Playlist operations show durable progress and confirm cancellation", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  let cancelled = false;
  let detailRequests = 0;
  await page.route("**/api/admin/jobs?limit=100", (route) => {
    const fixture = responses["/api/admin/jobs?limit=100"] as { jobs: Record<string, unknown>[]; progress: unknown[] };
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        ...fixture,
        jobs: fixture.jobs.map((job) => ({ ...job, state: cancelled ? "Succeeded" : "Running" })),
      }),
    });
  });
  await page.route("**/api/admin/jobs/*/cancel", (route) => {
    cancelled = true;
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        jobId: "11111111-1111-1111-1111-111111111111",
        state: "CancellationRequested",
      }),
    });
  });
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/admin/playlist-links/playlist-link")
      detailRequests++;
  });
  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: /Test playlist/ }).click();
  const dialog = page.getByRole("dialog", { name: "Test playlist" });
  const rematch = page.waitForRequest((item) =>
    item.method() === "POST" && item.url().endsWith("/api/admin/playlist-links/playlist-link/run") &&
    Object.keys(item.postDataJSON()).length === 0);
  await dialog.getByRole("button", { name: "Rematch" }).click();
  await rematch;
  await expect(dialog.getByText("Rematch queued.")).toBeVisible();
  await expect(dialog.locator("summary").getByText("Matching Test song")).toBeVisible();
  await expect(dialog.getByText("1/2")).toBeVisible();
  await dialog.getByRole("button", { name: "Cancel operation" }).click();
  const confirmation = page.getByRole("alertdialog", { name: "Cancel this operation?" });
  await expect(confirmation).toBeVisible();
  const request = page.waitForRequest((item) =>
    item.method() === "POST" && item.url().endsWith("/api/admin/jobs/11111111-1111-1111-1111-111111111111/cancel"));
  await confirmation.getByRole("button", { name: "Cancel operation" }).click();
  await request;
  await expect.poll(() => detailRequests).toBeGreaterThanOrEqual(2);
});

test("Profile artwork is stable in full, slim, and mobile navigation", async ({ page }) => {
  await mockApi(page);
  await page.route("**/api/admin/auth/me/avatar?user=user", (route) => route.fulfill({
    status: 200,
    contentType: "image/gif",
    body: Buffer.from("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==", "base64"),
  }));

  for (const width of [1280, 850, 390]) {
    await page.setViewportSize({ width, height: 844 });
    await page.goto("#/");
    const avatar = page.locator(".profile .avatar");
    await expect(avatar).toBeVisible();
    await expect(avatar.locator("img")).toBeVisible();
    await expect.poll(async () => (await avatar.boundingBox())?.width ?? 0).toBe(40);
  }

  await page.route("**/api/admin/auth/me/avatar?user=user", (route) => route.fulfill({ status: 404 }));
  await page.reload();
  await expect(page.locator(".profile .avatar span")).toHaveText("T");
  await expect.poll(async () => (await page.locator(".profile .avatar").boundingBox())?.width ?? 0).toBe(40);
});

test("Segmented navigation and match tabs support arrow keys", async ({ page }) => {
  await mockApi(page);
  await page.goto("#/library/playlists");
  await page.getByRole("tab", { name: "Playlists" }).focus();
  await page.keyboard.press("ArrowRight");
  await expect(page).toHaveURL(/#\/library\/mappings$/);
  await expect(page.getByRole("tab", { name: "Mappings" })).toHaveAttribute("aria-selected", "true");

  await page.getByRole("button", { name: "Review match" }).click();
  const dialog = page.getByRole("dialog", { name: "Test song" });
  await dialog.getByRole("tab", { name: "Local library" }).focus();
  await page.keyboard.press("ArrowRight");
  await expect(dialog.getByRole("tab", { name: "Playable providers" })).toHaveAttribute("data-state", "active");

  await page.goto("#/settings/extensions");
  await page.getByRole("tab", { name: /Installed/ }).focus();
  await page.keyboard.press("ArrowRight");
  await expect(page.getByRole("tab", { name: /Available/ })).toHaveAttribute("aria-selected", "true");
});

test("Sidebar uses an edge expander and deterministic slim breakpoint", async ({ page }) => {
  await mockApi(page);
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("#/");
  const shell = page.locator(".app-shell");
  const expander = page.getByRole("button", { name: "Collapse sidebar" });
  await expect(expander).toBeVisible();
  await expect(page.getByRole("navigation", { name: "Primary" }).getByRole("link")).toHaveCount(5);
  await expander.click();
  await expect(shell).toHaveClass(/slim/);
  await expect(page.getByRole("button", { name: "Expand sidebar" })).toBeVisible();
  await expect.poll(async () => (await page.locator(".sidebar").boundingBox())?.width ?? 0).toBe(80);

  await page.setViewportSize({ width: 850, height: 800 });
  await expect(page.getByRole("button", { name: "Expand sidebar" })).toBeHidden();
  await expect.poll(async () => (await page.locator(".sidebar").boundingBox())?.width ?? 0).toBe(80);
});
