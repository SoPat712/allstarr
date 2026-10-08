import { expect, test, type Page, type Route } from "@playwright/test";

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}

const schema = {
  activeBackend: "Jellyfin",
  providers: [{ id: "library", name: "Jellyfin", categories: ["streaming"] }],
  settings: [],
  priorityGroups: [],
};

function activity(label: string) {
  return {
    id: `activity-${label}`,
    kind: "playback",
    source: "library",
    providerId: "library",
    label,
    state: "succeeded",
    detail: "Completed listen",
    occurredAt: "2026-10-08T15:00:00Z",
  };
}

function nowPlaying(userId: string, userName: string, title: string) {
  return {
    deviceId: "shared-device-id",
    userId,
    userName,
    avatarUrl: null,
    client: "Finamp",
    device: "Phone",
    itemId: `item-${userId}`,
    title,
    artist: "Test Artist",
    album: "Test Album",
    providerId: "library",
    artworkUrl: null,
    sourceConfirmed: true,
    cached: false,
    positionSeconds: 30,
    durationSeconds: 120,
    progress: 0.25,
    lastActivity: "2026-10-08T15:00:00Z",
    scrobbleEligible: false,
    scrobbleDeliveries: [],
    scrobbled: false,
  };
}

async function mockApp(page: Page, administrator: boolean) {
  const requests: string[] = [];
  await page.route("**/api/admin/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    requests.push(`${request.method()} ${url.pathname}${url.search}`);

    if (url.pathname === "/api/admin/auth/me") return json(route, {
      authenticated: true,
      backend: "Jellyfin",
      features: { intelligence: false },
      user: {
        id: administrator ? "administrator" : "listener",
        name: administrator ? "Administrator" : "Listener",
        isAdministrator: administrator,
      },
    });
    if (url.pathname === "/api/admin/auth/oidc/status") return json(route, { enabled: false });
    if (url.pathname === "/api/admin/onboarding/status") return json(route, {
      completed: true,
      setupOpen: false,
      shouldRedirectToSetup: false,
      schemaVersion: "onboarding-v1",
      completedSteps: ["backend-identity"],
      completionSource: "setup-guide",
      revision: 1,
      recoveryNotices: [],
      migration: { available: false, completed: false, firstRun: false },
    });
    if (url.pathname === "/api/admin/updates/stream") {
      return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    }
    if (url.pathname === "/api/admin/ui/home") return json(route, {
      schema,
      status: { version: "test", backendType: "Jellyfin", durableStorage: { readiness: "Ready" } },
      stats: {
        linkedPlaylists: 2,
        playableTracks: 14,
        unresolvedTracks: 1,
        activeJobs: 1,
        completedListens: 5,
        currentWeekListens: 12,
        previousWeekListens: 9,
        scrobbleDeliveries: 3,
        cacheTracks: 40,
        keptTracks: 4,
        topArtist: { name: "Personal Artist", listens: 7 },
      },
      providerHealth: { providers: [] },
      activity: { items: [activity(administrator ? "Household playback" : "My playback")], hasMore: false },
    });
    if (url.pathname === "/api/admin/ui/now-playing") return json(route, {
      items: administrator
        ? [
            nowPlaying("listener-a", "Listener A", "First active song"),
            nowPlaying("listener-b", "Listener B", "Second active song"),
          ]
        : [nowPlaying("listener", "Listener", "My active song")],
    });
    if (url.pathname === "/api/admin/ui/activity") return json(route, {
      items: [activity(administrator ? "Household playback" : "My playback")],
      hasMore: false,
    });
    if (url.pathname === "/api/admin/ui/schema") return json(route, schema);
    if (url.pathname === "/api/admin/provider-accounts") return json(route, {
      listenersCanConnectOwnAccounts: true,
      audienceUsers: [],
      accounts: [],
    });
    if (url.pathname === "/api/admin/ui/provider-summaries") return json(route, { providers: [] });
    if (url.pathname === "/api/admin/providers/status") return json(route, []);
    if (url.pathname === "/api/admin/provider-diagnostics/deep-stream/latest") return json(route, { measurements: [] });
    if (url.pathname === "/api/admin/config") return json(route, {});
    if (url.pathname === "/api/admin/downloads") return json(route, {
      storage: url.searchParams.get("storage"),
      files: [],
      totalSize: 0,
      totalSizeFormatted: "0 B",
      count: 0,
      managedCount: 0,
      diagnosticCount: 0,
    });
    return json(route, {});
  });
  return requests;
}

test("listener navigation exposes personal surfaces and blocks operator workspaces", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  const requests = await mockApp(page, false);

  await page.goto("/#/");
  await expect(page.getByRole("region", { name: "Now playing" })).toContainText("My active song");
  await expect(page.getByText(/My Playback/i).first()).toBeVisible();
  await expect(page.getByText("Personal Artist", { exact: true })).toBeVisible();
  await expect(page.getByText("Media server", { exact: true })).toHaveCount(0);
  await expect(page.getByText("Managed audio", { exact: true })).toHaveCount(0);

  const primary = page.locator("nav.mobile-navigation");
  await expect(primary.getByRole("link", { name: "Home" })).toBeVisible();
  await expect(primary.getByRole("link", { name: "Library" })).toBeVisible();
  await expect(primary.getByRole("link", { name: "Activity" })).toBeVisible();
  await expect(primary.getByRole("link", { name: "Settings" })).toHaveCount(0);

  await page.getByRole("button", { name: "More destinations" }).click();
  const more = page.getByRole("navigation", { name: "More destinations" });
  await expect(more.getByRole("link", { name: /Listening preferences/ })).toBeVisible();
  await expect(more.getByRole("link", { name: /Integrations/ })).toBeVisible();
  await expect(more.getByRole("link", { name: /Settings/ })).toHaveCount(0);
  await more.getByRole("link", { name: /Integrations/ }).click();
  await expect(page.getByRole("navigation", { name: "Integration sections" })).toContainText("Services");
  await expect(page.getByRole("navigation", { name: "Integration sections" })).toContainText("Accounts");
  await expect(page.getByRole("navigation", { name: "Integration sections" })).not.toContainText("Extensions");
  await expect(page.getByRole("navigation", { name: "Integration sections" })).not.toContainText("Routing");

  await page.goto("/#/library/playlists");
  const library = page.getByRole("navigation", { name: "Library sections" });
  await expect(library).toContainText("Playlists");
  await expect(library).toContainText("Mappings");
  await expect(library).not.toContainText("Cached");
  await expect(library).not.toContainText("Kept");

  requests.length = 0;
  await page.goto("/#/activity");
  await expect(page.getByRole("heading", { name: "Listening activity" })).toBeVisible();
  await expect(page.getByText(/My Playback/i).first()).toBeVisible();
  expect(requests.some((request) => request.includes("/api/admin/ui/schema"))).toBe(true);

  for (const route of ["settings/general", "library/cached", "integrations/extensions", "settings/extensions"]) {
    requests.length = 0;
    await page.goto(`/#/${route}`);
    await expect(page.getByRole("heading", { name: "This workspace is for administrators." })).toBeVisible();
    expect(requests.filter((request) => !request.includes("/api/admin/auth/") && !request.includes("/api/admin/updates/stream"))).toEqual([]);
  }
  await expect(page.getByRole("link", { name: "Go to Home" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Listening preferences" }).last()).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test("administrator navigation and household now-playing remain available", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await mockApp(page, true);

  await page.goto("/#/");
  const primary = page.locator("nav.desktop-navigation");
  await expect(primary.getByRole("link", { name: "Settings" })).toBeVisible();
  const playing = page.getByRole("region", { name: "Now playing" });
  await expect(playing.getByText("First active song", { exact: true })).toBeVisible();
  await expect(playing.getByText("Second active song", { exact: true })).toBeVisible();

  await page.goto("/#/library/cached");
  await expect(page.getByRole("navigation", { name: "Library sections" })).toContainText("Cached");
  await expect(page.getByRole("navigation", { name: "Library sections" })).toContainText("Kept");

  await page.goto("/#/integrations/services");
  const integrations = page.getByRole("navigation", { name: "Integration sections" });
  await expect(integrations).toContainText("Extensions");
  await expect(integrations).toContainText("Routing");
});
