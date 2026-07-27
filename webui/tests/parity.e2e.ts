import { expect, test, type Page } from "@playwright/test";

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
    { id: "lumen-audio", name: "Lumen Audio", categories: ["metadata", "streaming"] },
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
    user: { id: "user", name: "Tester", isAdministrator: true },
  },
  "/api/admin/ui/schema": schema,
  "/api/admin/status": { version: "test", backendType: "Jellyfin" },
  "/api/admin/playlists": { playlists: [], inventory: { managed: 0, unmanaged: 0 } },
  "/api/admin/jobs?limit=100": { jobs: [] },
  "/api/admin/ui/activity?limit=8": { items: [], hasMore: false },
  "/api/admin/ui/provider-summaries": { providers: [] },
  "/api/admin/playlist-links": { playlistLinks: [] },
  "/api/admin/provider-accounts": { managementMode: "ApplicationManaged", accounts: [] },
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

async function mockApi(page: Page) {
  await page.route("**/fonts/**", (route) => route.fulfill({ status: 204 }));
  await page.route("**/api/admin/**", async (route) => {
    const url = new URL(route.request().url());
    let body = responses[`${url.pathname}${url.search}`] ?? responses[url.pathname];
    if (url.pathname === "/api/admin/ui/activity") body = { items: [], hasMore: false };
    if (url.pathname === "/api/admin/downloads")
      body = { storage: url.searchParams.get("storage"), files: [], totalSize: 0, totalSizeFormatted: "0 B", count: 0 };
    if (url.pathname === "/api/admin/track-matches")
      body = {
        matches: [], stats: { total: 0, matched: 0, accepted: 0, unresolved: 0, review: 0, rejected: 0, attention: 0 },
        pagination: { page: 1, pageSize: 50, total: 0, totalPages: 0 },
      };
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
  });
}
