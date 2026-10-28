import { expect, test, type Page, type Route } from "@playwright/test";
import { resolve } from "node:path";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const userId = "11111111-1111-4111-8111-111111111111";
const accountId = "22222222-2222-4222-8222-222222222222";
const jobId = "33333333-3333-4333-8333-333333333333";
const description = "Adds your Spotify likes as hearts. Nothing is downloaded. Running it again only adds new likes.";
const summary = "Added 120 hearts · 30 already hearted · 4 need a check · 2 not found";
const account = { id: accountId, providerId: "spotify", displayName: "My Spotify", scope: "Personal",
  ownerUserId: userId, ownerDisplayName: "Alex", enabled: true, canManage: true, revision: 1,
  secret: { configured: true, revoked: false }, createdAt: "2026-10-24T12:00:00Z", updatedAt: "2026-10-24T12:00:00Z" };
const progress = { jobId, state: "Running", processed: 26, total: 156 as number | null, imported: 20,
  alreadyHearted: 4, needsReview: 1, notFound: 1, errorCode: null as string | null };

async function reply(route: Route, data: unknown, status = 200) {
  await route.fulfill({ status, contentType: "application/json", body: JSON.stringify(data) });
}

async function mockLikes(page: Page, administrator = false, accounts = [account]) {
  const state = { status: { ...progress }, reads: 0, readError: false, startError: false,
    posts: [] as Record<string, unknown>[], forbidden: [] as string[] };
  await page.route("**/*", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (url.hostname !== "127.0.0.1" && url.hostname !== "localhost") {
      state.forbidden.push(url.origin); return route.abort();
    }
    if (!path.startsWith("/api/admin/")) return route.continue();
    if (path === "/api/admin/auth/me") return reply(route, { authenticated: true, backend: "Jellyfin",
      features: { intelligence: false }, user: { id: "backend-alex", allstarrUserId: userId,
        name: "Alex", isAdministrator: administrator } });
    if (path === "/api/admin/auth/oidc/status") return reply(route, { enabled: false });
    if (path === "/api/admin/onboarding/status") return reply(route, { completed: true, setupOpen: false,
      shouldRedirectToSetup: false, schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"],
      completionSource: "setup-guide", revision: 1, recoveryNotices: [], migration: { available: false, completed: false, firstRun: false } });
    if (path === "/api/admin/ui/schema") return reply(route, { activeBackend: "Jellyfin", settings: [], priorityGroups: [],
      providers: [{ id: "spotify", name: "Spotify", categories: ["playlist"], requiresAccount: true,
        description: "Connect Spotify to use your playlists.", useMode: "automatic", settings: [] }] });
    if (path === "/api/admin/provider-accounts") return reply(route, { accounts, audienceUsers: [], listenersCanConnectOwnAccounts: true });
    if (path === "/api/admin/providers/status") return reply(route, []);
    if (path === "/api/admin/ui/provider-summaries") return reply(route, { providers: [] });
    if (path === "/api/admin/provider-diagnostics/deep-stream/latest") return reply(route, { measurements: [] });
    if (path === "/api/admin/providers/use") return reply(route, { providers: [] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    if (path === "/api/admin/spotify-likes/import") {
      state.posts.push(request.postDataJSON());
      if (state.startError) return reply(route, { error: "Unavailable" }, 503);
      return reply(route, { jobId }, 202);
    }
    if (path === `/api/admin/spotify-likes/${jobId}`) {
      state.reads++;
      if (state.readError) return reply(route, {}, 503);
      return reply(route, state.status);
    }
    return reply(route, {});
  });
  return state;
}

const card = (page: Page) => page.getByRole("region", { name: "Spotify likes", exact: true });
async function start(page: Page) {
  await card(page).getByRole("button", { name: "Import Spotify likes", exact: true }).click();
  const dialog = page.getByRole("alertdialog");
  await expect(dialog).toContainText(description);
  await dialog.getByRole("button", { name: "Import Spotify likes", exact: true }).click();
}
async function focusRefresh(page: Page) {
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
}
async function screenshot(page: Page, name: string, width: number, theme: string) {
  await page.screenshot({ path: resolve(`test-results/shots-uiactivity/spotify-likes-${name}-${width}-${theme}.png`), fullPage: true });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Spotify likes confirmation, progress and summary at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    const state = await mockLikes(page);
    await page.goto("/#/integrations/services/spotify?from=accounts");
    const importButton = card(page).getByRole("button", { name: "Import Spotify likes", exact: true });
    await importButton.focus(); await page.keyboard.press("Enter");
    const dialog = page.getByRole("alertdialog");
    await expect(dialog).toContainText(description);
    await screenshot(page, "confirm", width, theme);
    await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(importButton).toBeFocused();
    expect(state.posts).toHaveLength(0);
    await start(page);
    await expect(card(page).getByRole("status")).toContainText("26 of 156 likes checked");
    await expect(card(page).getByRole("progressbar")).toHaveAttribute("aria-valuenow", "26");
    await expect(importButton).toBeDisabled();
    expect(state.posts).toHaveLength(1);
    expect(state.posts[0]).toEqual({ providerAccountId: accountId, requestId: expect.stringMatching(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i) });
    await card(page).scrollIntoViewIfNeeded();
    if (width === 390) await expectContainedMobileGeometry(page);
    await screenshot(page, "progress", width, theme);
    const reads = state.reads;
    await page.reload();
    await expect(card(page).getByRole("status")).toContainText("26 of 156 likes checked");
    expect(state.reads).toBeGreaterThan(reads);
    expect(state.posts).toHaveLength(1);
    state.status = { ...progress, state: "Succeeded", processed: 156, imported: 120, alreadyHearted: 30, needsReview: 4, notFound: 2 };
    await focusRefresh(page);
    await expect(card(page).getByRole("status")).toContainText(summary);
    await expect(importButton).toBeEnabled();
    await expect(card(page).getByRole("progressbar")).toHaveCount(0);
    await expect(page.getByText("Refreshing…", { exact: true })).toHaveCount(0);
    if (width === 390) await expectContainedMobileGeometry(page);
    await card(page).scrollIntoViewIfNeeded();
    await screenshot(page, "summary", width, theme);
    expect(state.forbidden).toEqual([]);
  });
}

for (const administrator of [false, true]) test(`Spotify likes only appears on an enabled personal account owned by the signed-in ${administrator ? "administrator" : "listener"}`, async ({ page }) => {
  await mockLikes(page, administrator, [account,
    { ...account, id: "shared", scope: "Shared" },
    { ...account, id: "someone-else", ownerUserId: "another-listener" },
    { ...account, id: "disabled", enabled: false },
    { ...account, id: "deezer", providerId: "deezer" }]);
  await page.goto("/#/integrations/services/spotify");
  await expect(card(page)).toHaveCount(1);
  await expect(card(page).getByRole("button", { name: "Import Spotify likes", exact: true })).toBeVisible();
});

test("Spotify likes resumes unknown totals and preserves counts while waiting or stopped", async ({ page }) => {
  const state = await mockLikes(page);
  state.status = { ...progress, state: "Pending", total: null, processed: 0 };
  await page.goto("/#/integrations/services/spotify");
  await start(page);
  await expect(card(page).getByRole("status")).toContainText("Waiting to import");
  await expect(card(page).getByRole("progressbar")).not.toHaveAttribute("aria-valuenow");
  for (const [status, label] of [["RetryScheduled", "Waiting to retry"], ["Deferred", "Waiting to continue"], ["Failed", "Import needs attention"]]) {
    state.status = { ...progress, state: status, total: null, errorCode: "spotify_likes_unauthorized" };
    await focusRefresh(page);
    await expect(card(page).getByRole("status")).toContainText(label);
    await expect(card(page)).toContainText("Added 20 hearts · 4 already hearted · 1 need a check · 1 not found");
    await expect(card(page)).not.toContainText("spotify_likes_unauthorized");
  }
  expect(state.posts).toHaveLength(1);
});

test("Spotify likes shows saved counts after a cancelled import", async ({ page }) => {
  const state = await mockLikes(page);
  state.status = { ...progress, state: "Cancelled" };
  await page.goto("/#/integrations/services/spotify");
  await start(page);
  await expect(card(page).getByRole("status")).toContainText("Import stopped");
  await expect(card(page)).toContainText("Added 20 hearts · 4 already hearted · 1 need a check · 1 not found");
  await expect(card(page).getByRole("button", { name: "Import Spotify likes", exact: true })).toBeEnabled();
});

test("Spotify likes status failure keeps saved progress and retries with GET", async ({ page }) => {
  const state = await mockLikes(page);
  await page.goto("/#/integrations/services/spotify");
  await start(page);
  await expect(card(page).getByRole("status")).toContainText("26 of 156 likes checked");
  state.readError = true;
  await focusRefresh(page);
  await expect(card(page).getByRole("alert")).toContainText("Progress could not be refreshed");
  await expect(card(page).getByRole("status")).toContainText("26 of 156 likes checked");
  state.readError = false;
  state.status = { ...progress, state: "Succeeded" };
  await card(page).getByRole("button", { name: "Refresh import status", exact: true }).click();
  await expect(card(page).getByRole("alert")).toHaveCount(0);
  expect(state.posts).toHaveLength(1);
});

test("Spotify likes retries an uncertain start with the same request identity", async ({ page }) => {
  const state = await mockLikes(page); state.startError = true;
  await page.goto("/#/integrations/services/spotify");
  await start(page);
  const dialog = page.getByRole("alertdialog");
  await expect(dialog.getByRole("alert")).toContainText("The import could not be started");
  state.startError = false;
  const confirm = dialog.getByRole("button", { name: "Import Spotify likes", exact: true });
  await confirm.evaluate(button => { (button as HTMLButtonElement).click(); (button as HTMLButtonElement).click(); });
  await expect(card(page).getByRole("status")).toContainText("26 of 156 likes checked");
  expect(state.posts).toHaveLength(2);
  expect(state.posts[1]).toEqual(state.posts[0]);
});

test("Spotify likes imports when browser storage is unavailable", async ({ page }) => {
  await page.addInitScript(() => {
    const get = Storage.prototype.getItem, set = Storage.prototype.setItem;
    Storage.prototype.getItem = function (key) { if (key.startsWith("allstarr:spotify-likes:")) throw new Error("Storage unavailable"); return get.call(this, key); };
    Storage.prototype.setItem = function (key, value) { if (key.startsWith("allstarr:spotify-likes:")) throw new Error("Storage unavailable"); set.call(this, key, value); };
  });
  const state = await mockLikes(page);
  await page.goto("/#/integrations/services/spotify");
  await start(page);
  await expect(card(page).getByRole("status")).toContainText("26 of 156 likes checked");
  expect(state.posts).toHaveLength(1);
});
