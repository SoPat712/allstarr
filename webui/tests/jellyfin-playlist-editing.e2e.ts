import { mkdir } from "node:fs/promises";
import { expect, test, type Page } from "@playwright/test";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

const listener = "11111111-1111-4111-8111-111111111111";
const playlist = {
  id: "22222222-2222-4222-8222-222222222222", name: "Evening mix", enabled: true,
  sourceProviderId: "spotify", sourcePlaylistId: "evening", targetProtocol: "jellyfin",
  targetBackendInstanceId: "home", targetPlaylistId: "original-playlist", mode: "hybrid",
  importMode: "linked", projectionMode: "resolved", trackRetention: "onDemand", revision: 1,
  trackCount: 3, matchedCount: 3, unmatchedCount: 0, playableCount: 3, materializedCount: 3,
  lastRunCode: "order,removal", lastRunAt: "2026-10-08T14:00:00Z", routeCoverage: [],
  metrics: { total: 3, matched: 3, unresolved: 0, review: 0, rejected: 0, playable: 3, materialized: 3 },
};

async function mockJellyfin(page: Page, options: { granted?: boolean; needsSignIn?: boolean; lastRunCode?: string | null; importMode?: string; rebuildError?: boolean; queuedSyncs?: number } = {}) {
  let granted = options.granted ?? false;
  let needsSignIn = options.needsSignIn ?? false;
  let link = { ...playlist, lastRunCode: options.lastRunCode === undefined ? playlist.lastRunCode : options.lastRunCode, importMode: options.importMode ?? "linked" };
  const writes: Array<{ method: string; path: string; body?: unknown }> = [];
  let listReads = 0, consentReads = 0;
  await page.route("**/*", async route => {
    const request = route.request();
    const url = new URL(request.url()), path = url.pathname;
    if (!["127.0.0.1", "localhost"].includes(url.hostname)) return route.abort();
    if (!path.startsWith("/api/admin/")) return route.continue();
    const reply = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    if (path === "/api/admin/auth/me") return reply({ authenticated: true, backend: "Jellyfin", user: { id: "backend-user", allstarrUserId: listener, name: "Listener", isAdministrator: false } });
    if (path === "/api/admin/auth/oidc/status") return reply({ enabled: false });
    if (path === "/api/admin/auth/playlist-consent") {
      if (request.method() === "GET") consentReads++;
      if (request.method() === "POST") {
        writes.push({ method: "POST", path, body: request.postDataJSON() });
        granted = true;
        needsSignIn = false;
      } else if (request.method() === "DELETE") {
        writes.push({ method: "DELETE", path });
        granted = false;
        needsSignIn = false;
      }
      return reply({ supported: true, granted, needsSignIn, updatedAt: granted ? "2026-10-08T14:00:00Z" : null, ...(request.method() === "POST" ? { queuedSyncs: options.queuedSyncs ?? 0 } : {}) });
    }
    if (path === "/api/admin/ui/schema") return reply({ listenersCanConnectOwnAccounts: true, providers: [{ id: "spotify", name: "Spotify" }], settings: [], priorityGroups: [], configSections: [] });
    if (path === "/api/admin/provider-accounts") return reply({ listenersCanConnectOwnAccounts: true, audienceUsers: [], accounts: [] });
    if (path === "/api/admin/mixes" || path.endsWith("/playlist-forks")) return reply([]);
    if (path === "/api/admin/playlist-sources") return reply({ accounts: [] });
    if (path === "/api/admin/playlist-links") { listReads++; return reply({ playlistLinks: [link] }); }
    if (path === `/api/admin/playlist-links/${playlist.id}/run`) {
      writes.push({ method: request.method(), path, body: request.postDataJSON() });
      link = { ...link, lastRunCode: null };
      return reply({ jobId: "sync-job", created: true });
    }
    if (path === `/api/admin/playlist-links/${playlist.id}/rebuild`) {
      writes.push({ method: request.method(), path });
      if (options.rebuildError) return reply({ error: "Jellyfin is unavailable. Try again." }, 502);
      link = { ...link, targetPlaylistId: "rebuilt-playlist", lastRunCode: null, revision: 2 };
      return reply({ id: link.id, targetPlaylistId: link.targetPlaylistId, revision: link.revision });
    }
    if (path === `/api/admin/playlist-links/${playlist.id}`) return reply({ ...link, tracks: [], unresolvedCount: 0, reviewCount: 0, unknownDurationCount: 0, durationMs: 540000, retrievedAt: "2026-10-08T14:00:00Z" });
    if (path === "/api/admin/jobs") return reply({ jobs: [], progress: [] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return reply({});
  });
  return { writes, listReads: () => listReads, consentReads: () => consentReads };
}

async function openPanel(page: Page) {
  const panel = page.getByRole("dialog", { name: "Evening mix", exact: true });
  if (!await panel.isVisible()) await page.getByRole("button", { name: "Open Evening mix playlist details" }).click();
  return panel;
}
async function openRebuild(page: Page) {
  await (await openPanel(page)).getByRole("button", { name: "Rebuild…", exact: true }).click();
}

async function screenshot(page: Page, name: string) {
  const folder = new URL("../../test-results/uibugs/", import.meta.url).pathname;
  await mkdir(folder, { recursive: true });
  await page.screenshot({ path: `${folder}/${name}.png`, fullPage: true });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Jellyfin playlist editing consent at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const fixture = await mockJellyfin(page, { queuedSyncs: 3 });
    await page.goto("/#/sources/services?section=playlist-editing");
    const card = page.getByRole("region", { name: "Playlist editing in Jellyfin" });
    await expect(card).toBeVisible();
    await expect(page.getByRole("dialog", { name: "Allow playlist editing?" })).toContainText("never your password");
    await expect(page.getByLabel("Jellyfin password")).toHaveAttribute("maxlength", "2000");
    await expect(page.getByRole("dialog", { name: "Allow playlist editing?" }).getByRole("button", { name: "Allow editing", exact: true })).toBeDisabled();
    await screenshot(page, `consent-${width}-${theme}`);
    await page.getByLabel("Jellyfin password").fill("listener-password");
    await page.getByRole("dialog", { name: "Allow playlist editing?" }).getByRole("button", { name: "Allow editing", exact: true }).click();
    await expect(page.getByRole("status").filter({ hasText: "Playlist editing allowed. Fixing the order of 3 playlists now." })).toBeVisible();
    await expect(card.getByText(/^Allowed since /)).toBeVisible();
    await expect(page.getByLabel("Jellyfin password")).toHaveCount(0);
    expect(fixture.writes).toEqual([{ method: "POST", path: "/api/admin/auth/playlist-consent", body: { password: "listener-password" } }]);
    await screenshot(page, `consent-allowed-${width}-${theme}`);
    await card.getByRole("button", { name: "Remove", exact: true }).click();
    expect(fixture.writes.filter(write => write.method === "DELETE")).toHaveLength(0);
    await page.getByRole("alertdialog").getByRole("button", { name: "Remove sign-in", exact: true }).click();
    await card.getByRole("button", { name: "Allow editing", exact: true }).click();
    await expect(page.getByLabel("Jellyfin password")).toHaveValue("");
    expect(fixture.writes[1]).toEqual({ method: "DELETE", path: "/api/admin/auth/playlist-consent" });
  });

  test(`Jellyfin playlist warning and rebuild at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const fixture = await mockJellyfin(page);
    await page.goto("/#/library/playlists");
    const row = page.locator(".playlist-row").filter({ hasText: playlist.name });
    await expect(row.getByText("Needs a look", { exact: true })).toBeVisible();
    const panel = await openPanel(page);
    await expect(panel).toContainText("Order in Jellyfin differs from Spotify");
    await expect(panel.getByRole("link", { name: "Allow editing" })).toHaveAttribute("href", "#/sources/services?section=playlist-editing");
    await screenshot(page, `playlist-order-${width}-${theme}`);
    await panel.getByRole("link", { name: "Allow editing" }).click();
    const card = page.getByRole("region", { name: "Playlist editing in Jellyfin" });
    await expect(card).toBeInViewport();
    await expect(page.getByLabel("Jellyfin password")).toBeFocused();
    await page.goto("/#/library/playlists");
    await expect(row.getByRole("button", { name: "Rebuild", exact: true })).toHaveCount(0);
    await openRebuild(page);
    const dialog = page.getByRole("alertdialog");
    await expect(dialog.getByRole("heading", { name: "Rebuild this playlist in Jellyfin?" })).toBeVisible();
    await expect(dialog).toContainText("Allstarr makes the playlist again in the right order and removes the old copy. Apps see it as a new playlist, so pins, favourites and shortcuts to the old one stop working.");
    expect(fixture.writes).toEqual([]);
    await screenshot(page, `playlist-rebuild-${width}-${theme}`);
    await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    expect(fixture.writes).toEqual([]);
    await openRebuild(page);
    await dialog.getByRole("button", { name: "Rebuild", exact: true }).click();
    await expect(dialog).toHaveCount(0);
    await expect(page.getByRole("status").filter({ hasText: "Playlist rebuilt in Jellyfin." })).toBeVisible();
    await expect(row.getByText("Order in Jellyfin differs from Spotify", { exact: true })).toHaveCount(0);
    expect(fixture.writes).toEqual([{ method: "POST", path: `/api/admin/playlist-links/${playlist.id}/rebuild` }]);
    expect(fixture.listReads()).toBeGreaterThan(1);
  });
}

test("Expired Jellyfin sign-in can be replaced and removed", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 });
  const fixture = await mockJellyfin(page, { granted: true, needsSignIn: true, lastRunCode: "credential_expired" });
  await page.goto("/#/library/playlists");
  await expect(await openPanel(page)).toContainText("Saved Jellyfin sign-in expired");
  await page.getByRole("link", { name: "Allow editing" }).click();
  const card = page.getByRole("region", { name: "Playlist editing in Jellyfin" });
  await expect(card).toContainText("Jellyfin no longer accepts the saved sign-in. Sign in again to keep playlists in order.");
  await screenshot(page, "consent-expired-390");
  await page.getByLabel("Jellyfin password").fill("replacement-password");
  await page.getByRole("dialog", { name: "Allow playlist editing?" }).getByRole("button", { name: "Allow editing", exact: true }).click();
  await expect(card.getByText(/^Allowed since /)).toBeVisible();
  await expect(card.getByText("Jellyfin no longer accepts", { exact: false })).toHaveCount(0);
  await card.getByRole("button", { name: "Remove", exact: true }).click();
  expect(fixture.writes.filter(write => write.method === "DELETE")).toHaveLength(0);
  await page.getByRole("alertdialog").getByRole("button", { name: "Remove sign-in", exact: true }).click();
  await card.getByRole("button", { name: "Allow editing", exact: true }).click();
  await expect(page.getByLabel("Jellyfin password")).toHaveValue("");
  expect(fixture.writes.map(write => write.method)).toEqual(["POST", "DELETE"]);
});

test("Saved Jellyfin playlist sign-in loads as allowed", async ({ page }) => {
  const fixture = await mockJellyfin(page, { granted: true });
  await page.goto("/#/sources/accounts");
  const card = page.getByRole("region", { name: "Playlist editing in Jellyfin" });
  await expect(card.getByText(/^Allowed since /)).toBeVisible();
  await expect(page.getByLabel("Jellyfin password")).toHaveCount(0);
  await card.getByRole("button", { name: "Remove", exact: true }).click();
  expect(fixture.writes.filter(write => write.method === "DELETE")).toHaveLength(0);
  await page.getByRole("alertdialog").getByRole("button", { name: "Remove sign-in", exact: true }).click();
  await card.getByRole("button", { name: "Allow editing", exact: true }).click();
  await expect(page.getByLabel("Jellyfin password")).toHaveValue("");
  expect(fixture.writes).toEqual([{ method: "DELETE", path: "/api/admin/auth/playlist-consent" }]);
});

test("Expired saved Jellyfin sign-in can be removed without signing in again", async ({ page }) => {
  const fixture = await mockJellyfin(page, { granted: true, needsSignIn: true });
  await page.goto("/#/sources/accounts");
  const card = page.getByRole("region", { name: "Playlist editing in Jellyfin" });
  await card.getByRole("button", { name: "Remove", exact: true }).click();
  expect(fixture.writes.filter(write => write.method === "DELETE")).toHaveLength(0);
  await page.getByRole("alertdialog").getByRole("button", { name: "Remove sign-in", exact: true }).click();
  await card.getByRole("button", { name: "Allow editing", exact: true }).click();
  await expect(page.getByLabel("Jellyfin password")).toHaveValue("");
  await expect(card.getByText("Jellyfin no longer accepts", { exact: false })).toHaveCount(0);
  expect(fixture.writes).toEqual([{ method: "DELETE", path: "/api/admin/auth/playlist-consent" }]);
});

test("Linked Jellyfin playlists can rebuild without an order warning", async ({ page }) => {
  await mockJellyfin(page, { lastRunCode: null });
  await page.goto("/#/library/playlists");
  await expect(page.getByRole("link", { name: "Allow editing" })).toHaveCount(0);
  await expect((await openPanel(page)).getByRole("button", { name: "Rebuild…", exact: true })).toBeVisible();
});

test("Jellyfin playlist details offer rebuilding with confirmation", async ({ page }) => {
  const fixture = await mockJellyfin(page, { lastRunCode: null });
  await page.goto("/#/library/playlists");
  await page.getByRole("button", { name: "Open Evening mix playlist details" }).click();
  await page.getByRole("button", { name: "Actions", exact: true }).click();
  await page.getByRole("menuitem", { name: "Rebuild in Jellyfin" }).click();
  const confirmation = page.getByRole("alertdialog");
  await expect(confirmation.getByRole("heading", { name: "Rebuild this playlist in Jellyfin?" })).toBeVisible();
  expect(fixture.writes).toEqual([]);
  await confirmation.getByRole("button", { name: "Rebuild", exact: true }).click();
  await expect(confirmation).toHaveCount(0);
  await expect(page.getByRole("dialog")).toBeVisible();
  await expect(page.getByRole("status").filter({ hasText: "Playlist rebuilt in Jellyfin." })).toBeVisible();
});

for (const code of ["order,removal", "credential_expired"]) {
  test(`One-time Jellyfin imports omit editing warning and rebuild for ${code}`, async ({ page }) => {
    await mockJellyfin(page, { importMode: "oneTime", lastRunCode: code });
    await page.goto("/#/library/playlists");
    await expect(page.getByRole("button", { name: "Open Evening mix playlist details" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Allow editing" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Rebuild", exact: true })).toHaveCount(0);
    await expect(page.getByText(/Order in Jellyfin differs|Saved Jellyfin sign-in expired/)).toHaveCount(0);
    await page.getByRole("button", { name: "Open Evening mix playlist details" }).click();
    await page.getByRole("button", { name: "Actions", exact: true }).click();
    await expect(page.getByRole("menuitem", { name: "Rebuild in Jellyfin" })).toHaveCount(0);
  });
}

test("Rebuild failure leaves its confirmation available for retry", async ({ page }) => {
  await mockJellyfin(page, { rebuildError: true });
  await page.goto("/#/library/playlists");
  await openRebuild(page);
  const dialog = page.getByRole("alertdialog");
  await dialog.getByRole("button", { name: "Rebuild", exact: true }).click();
  await expect(dialog.getByRole("alert")).toContainText("Jellyfin is unavailable. Try again.");
  await expect(dialog.getByRole("button", { name: "Rebuild", exact: true })).toBeEnabled();
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Granted playlist order repair at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const fixture = await mockJellyfin(page, { granted: true });
    await page.goto("/#/library/playlists");
    const row = page.locator(".playlist-row").filter({ hasText: playlist.name });
    await expect(row.getByText("Order fixing", { exact: true })).toBeVisible();
    const panel = await openPanel(page);
    await expect(panel).toContainText("Fixing order on the next sync");
    await expect(row.getByRole("link", { name: "Allow editing" })).toHaveCount(0);
    await expect(row.getByRole("button", { name: "Rebuild", exact: true })).toHaveCount(0);
    await screenshot(page, `playlist-fixing-${width}-${theme}`);
    await panel.getByRole("button", { name: "Sync now", exact: true }).click();
    await expect(row.getByText("Fixing order on the next sync", { exact: true })).toHaveCount(0);
    expect(fixture.writes).toEqual([{ method: "POST", path: `/api/admin/playlist-links/${playlist.id}/run`, body: {} }]);
    expect(fixture.listReads()).toBeGreaterThan(1);
    expect(fixture.consentReads()).toBe(1);
  });
}

test("An expired grant keeps the expired sign-in warning even with an order code", async ({ page }) => {
  await mockJellyfin(page, { granted: true, needsSignIn: true, lastRunCode: "order" });
  await page.goto("/#/library/playlists");
  await expect(await openPanel(page)).toContainText("Saved Jellyfin sign-in expired");
  await expect(page.getByRole("link", { name: "Allow editing" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Sync now", exact: true })).toBeVisible();
});

test("Playlist order status accepts spaced outcome codes", async ({ page }) => {
  await mockJellyfin(page, { granted: true, lastRunCode: " order , removal " });
  await page.goto("/#/library/playlists");
  await expect(page.locator(".playlist-row").getByText("Order fixing", { exact: true })).toBeVisible();
  await expect(await openPanel(page)).toContainText("Fixing order on the next sync");
});
