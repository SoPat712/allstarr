import { mkdir } from "node:fs/promises";
import { resolve } from "node:path";
import { expect, test, type Locator, type Page } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const listenerId = "11111111-1111-4111-8111-111111111111";
const lastfmAccountId = "22222222-2222-4222-8222-222222222222";
const playlistMbid = "33333333-3333-4333-8333-333333333333";
const targetId = "44444444-4444-4444-8444-444444444444";
const periods = [
  { value: "7day", label: "Last 7 days" },
  { value: "1month", label: "Last month" },
  { value: "3month", label: "Last 3 months" },
  { value: "12month", label: "Last year" },
  { value: "overall", label: "All time" },
];

async function mockImport(page: Page, options: { lastfmAccount?: boolean; publicRead?: boolean } = {}) {
  const state = {
    created: [] as Record<string, unknown>[],
    schedules: [] as Record<string, unknown>[],
    resolved: [] as Record<string, unknown>[],
    browsed: [] as string[],
    externalRequests: [] as string[],
  };
  await page.route("**/*", async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (!["127.0.0.1", "localhost"].includes(url.hostname)) {
      state.externalRequests.push(url.origin);
      return route.abort();
    }
    const path = url.pathname;
    if (path.startsWith("/images/") || path.startsWith("/fonts/")) return route.abort();
    if (!path.startsWith("/api/admin/")) return route.continue();
    const reply = (body: unknown) => route.fulfill({ json: body });
    if (path === "/api/admin/auth/me") return reply({ authenticated: true, backend: "Jellyfin",
      features: { intelligence: true }, user: { id: "backend-listener", allstarrUserId: listenerId,
        name: "Alex", isAdministrator: false } });
    if (path === "/api/admin/auth/oidc/status") return reply({ enabled: false });
    if (path === "/api/admin/onboarding/status") return reply({ completed: true, setupOpen: false,
      shouldRedirectToSetup: false, schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"],
      completionSource: "setup-guide", revision: 1, recoveryNotices: [],
      migration: { available: false, completed: false, firstRun: false } });
    if (path === "/api/admin/ui/schema") return reply({ activeBackend: "Jellyfin", settings: [],
      priorityGroups: [], providers: [
        { id: "listenbrainz", name: "ListenBrainz", categories: ["playlist"], settings: [] },
        { id: "lastfm", name: "Last.fm", categories: ["playlist"], settings: [] },
        { id: "jellyfin", name: "Jellyfin", categories: ["streaming"], settings: [] },
      ] });
    if (path === "/api/admin/mixes") return reply([]);
    if (path === "/api/admin/playlist-sources") return reply({
      accounts: options.lastfmAccount === false ? [] : [{ id: lastfmAccountId, providerId: "lastfm",
        displayName: "My Last.fm", ownerDisplayName: "Alex", accessLabel: "Personal account", scope: "personal" }],
      blockedAccounts: [], providers: [
        { id: "listenbrainz", displayName: "ListenBrainz", supportsPublicRead: options.publicRead !== false },
        { id: "lastfm", displayName: "Last.fm", supportsPublicRead: false },
      ],
    });
    if (path.startsWith("/api/admin/playlist-sources/") && path.endsWith("/playlists")) {
      state.browsed.push(path);
      return reply({ items: [], nextCursor: null, isPartial: false });
    }
    if (path === "/api/admin/media-targets") return reply({ targets: [{ id: targetId,
      protocol: "jellyfin", backendInstanceId: "home", displayName: "Home music", credentialReferenceId: "home-listener" }] });
    if (path === `/api/admin/media-targets/${targetId}/playlists`) return reply({ items: [{
      id: "existing-playlist", name: "Evening music", trackCount: 12, writable: true,
    }] });
    if (path === "/api/admin/playlist-links/resolve-url") {
      state.resolved.push(request.postDataJSON());
      return reply({ source: { id: "listenbrainz", displayName: "ListenBrainz" }, kind: "playlist",
        id: playlistMbid, providerAccountId: null, title: "Public discoveries", owner: "Alex",
        trackCount: 25, artworkUrl: null });
    }
    if (path === "/api/admin/playlist-links") {
      if (request.method() === "POST") {
        state.created.push(request.postDataJSON());
        return reply({ id: "imported-playlist" });
      }
      return reply({ playlistLinks: [] });
    }
    if (path === "/api/admin/playlist-links/imported-playlist/schedules") {
      state.schedules.push(request.postDataJSON());
      return reply({ id: "saved-schedule" });
    }
    if (path === `/api/admin/listeners/${listenerId}/playlist-forks`) return reply([]);
    if (path === "/api/admin/provider-accounts") return reply({ accounts: [], audienceUsers: [], listenersCanConnectOwnAccounts: true });
    if (path === "/api/admin/providers/status") return reply([]);
    if (path === "/api/admin/ui/provider-summaries" || path === "/api/admin/providers/use") return reply({ providers: [] });
    if (path === "/api/admin/provider-diagnostics/deep-stream/latest") return reply({ measurements: [] });
    if (path === "/api/admin/jobs") return reply({ jobs: [], progress: [] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return reply({});
  });
  return state;
}

async function openImport(page: Page) {
  await page.goto("/#/library/playlists");
  await page.getByRole("button", { name: "Import playlist", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Import a playlist" });
  await expect(dialog.getByRole("radio", { name: /Public ListenBrainz playlist/ })).toBeVisible();
  return dialog;
}

async function finishOnce(dialog: Locator) {
  for (let step = 0; step < 3; step++) await dialog.getByRole("button", { name: "Continue", exact: true }).click();
  await dialog.getByRole("button", { name: "Import playlist", exact: true }).click();
  await expect(dialog).toBeHidden();
}

async function capture(page: Page, name: string, width: number, theme: string) {
  if (width === 390) await expectContainedMobileGeometry(page);
  const folder = resolve(process.env.ALLSTARR_SCREENSHOT_DIR ?? "test-results/shots-uiintel");
  await mkdir(folder, { recursive: true });
  await page.screenshot({ path: resolve(folder, `${name}-${width}-${theme}.png`) });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Playlist source choices at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const state = await mockImport(page);
    const dialog = await openImport(page);
    await dialog.getByRole("radio", { name: /Last\.fm/ }).check();
    await expect(dialog.getByRole("searchbox", { name: "Find a source playlist" })).toHaveCount(0);
    await dialog.getByRole("radio", { name: /Top tracks/ }).check();
    await expect(dialog.getByRole("button", { name: "Top tracks period" })).toContainText("All time");
    await dialog.getByRole("button", { name: "Top tracks period" }).scrollIntoViewIfNeeded();
    await capture(page, "lastfm-import-source", width, theme);
    await dialog.getByRole("textbox", { name: "Paste a link" }).fill(playlistMbid);
    await dialog.getByRole("textbox", { name: "Paste a link" }).press("Enter");
    await expect(dialog.getByText("Public discoveries", { exact: true })).toBeVisible();
    await capture(page, "listenbrainz-import-preview", width, theme);
    expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect(state.browsed).toEqual([]);
    expect(state.externalRequests).toEqual([]);
  });
}

for (const source of [
  { label: "Loved tracks", id: "loved" },
  { label: "Similar tracks", id: "similar" },
  ...periods.map(period => ({ label: "Top tracks", id: `top:${period.value}:50`, period })),
]) {
  test(`Personal Last.fm imports ${source.id}`, async ({ page }) => {
    const state = await mockImport(page);
    const dialog = await openImport(page);
    await dialog.getByRole("radio", { name: /Last\.fm/ }).check();
    await dialog.getByRole("radio", { name: new RegExp(`^${source.label}`) }).check();
    if ("period" in source) {
      await dialog.getByRole("button", { name: "Top tracks period" }).click();
      await page.getByRole("option", { name: source.period.label, exact: true }).click();
    }
    await finishOnce(dialog);
    expect(state.created).toHaveLength(1);
    expect(state.created[0]).toMatchObject({ providerAccountId: lastfmAccountId, sourceProviderId: "lastfm",
      sourcePlaylistId: source.id, importMode: "oneTime", trackRetention: "onDemand", targetPlaylistId: null,
      targetProtocol: "jellyfin", targetBackendInstanceId: "home", targetCredentialReferenceId: "home-listener" });
    expect(state.created[0]).not.toHaveProperty("followNewest");
    expect(state.schedules).toEqual([]);
    expect(state.browsed).toEqual([]);
    expect(state.externalRequests).toEqual([]);
  });
}

for (const reference of [playlistMbid, `https://listenbrainz.org/playlist/${playlistMbid}`]) {
  test(`ListenBrainz resolves a ${reference === playlistMbid ? "bare playlist MBID" : "playlist URL"}`, async ({ page }) => {
    const state = await mockImport(page, { lastfmAccount: false });
    const dialog = await openImport(page);
    const input = dialog.getByRole("textbox", { name: "Paste a link" });
    await input.fill(reference);
    await input.press("Enter");
    await expect(dialog.getByText("Public discoveries", { exact: true })).toBeVisible();
    await finishOnce(dialog);
    expect(state.resolved).toEqual([{ url: reference }]);
    expect(state.created).toHaveLength(1);
    expect(state.created[0]).toMatchObject({ providerAccountId: null, sourceProviderId: "listenbrainz",
      sourcePlaylistId: playlistMbid, importMode: "oneTime" });
    expect(state.externalRequests).toEqual([]);
  });
}

test("Source availability keeps link import and offers a personal Last.fm connection", async ({ page }) => {
  const state = await mockImport(page, { lastfmAccount: false, publicRead: false });
  await page.goto("/#/library/playlists");
  await page.getByRole("button", { name: "Import playlist", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Import a playlist" });
  await expect(dialog.getByRole("link", { name: "Connect Last.fm", exact: true })).toHaveAttribute("href", "#/integrations/accounts?source=lastfm&connect=1");
  await expect(dialog.getByRole("radio", { name: /Public ListenBrainz playlist/ })).toHaveCount(0);
  await expect(dialog.getByRole("textbox", { name: "Paste a link" })).toBeVisible();
  await expect(dialog.getByRole("radio", { name: /Loved tracks/ })).toHaveCount(0);
  expect(state.externalRequests).toEqual([]);
});

test("A Last.fm source can stay linked to an existing playlist with its chosen schedule", async ({ page }) => {
  const state = await mockImport(page);
  const dialog = await openImport(page);
  await dialog.getByRole("radio", { name: /Last\.fm/ }).check();
  await dialog.getByRole("radio", { name: /Loved tracks/ }).check();
  await dialog.getByRole("button", { name: "Continue", exact: true }).click();
  await dialog.getByRole("radio", { name: /Keep in sync/ }).check();
  await dialog.getByRole("button", { name: "Continue", exact: true }).click();
  await dialog.getByRole("radio", { name: /Existing playlist/ }).check();
  await dialog.getByRole("radio", { name: /Evening music/ }).check();
  await dialog.getByRole("button", { name: "Continue", exact: true }).click();
  await dialog.getByRole("button", { name: "Update schedule", exact: true }).click();
  await page.getByRole("option", { name: "Every hour", exact: true }).click();
  await dialog.getByRole("button", { name: "Import and link", exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(state.created).toHaveLength(1);
  expect(state.created[0]).toMatchObject({ importMode: "linked", targetPlaylistId: "existing-playlist",
    sourceProviderId: "lastfm", sourcePlaylistId: "loved", providerAccountId: lastfmAccountId });
  expect(state.schedules).toHaveLength(1);
  expect(state.schedules[0]).toMatchObject({ cronExpression: "0 * * * *", overlapPolicy: "skip", misfirePolicy: "runOnce" });
  expect(state.browsed).toEqual([]);
  expect(state.externalRequests).toEqual([]);
});
