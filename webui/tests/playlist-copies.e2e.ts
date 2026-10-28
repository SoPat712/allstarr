import { mkdir } from "node:fs/promises";
import { expect, test, type Page } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const listener = "11111111-1111-4111-8111-111111111111";
const id = "22222222-2222-4222-8222-222222222222";
const virtualPlaylistId = `allstarr-vpl-${id.replaceAll("-", "")}`;
const playlist = { id, virtualPlaylistId, name: "Evening mix", enabled: true, sourceProviderId: "spotify", sourcePlaylistId: "evening", targetProtocol: "jellyfin", targetBackendInstanceId: "home", targetPlaylistId: "33333333333343338333333333333333", mode: "virtual", importMode: "linked", projectionMode: "resolved", trackRetention: "onDemand", trackCount: 1, matchedCount: 1, unmatchedCount: 0, playableCount: 1, materializedCount: 0, routeCoverage: [], metrics: { total: 1, review: 0 } };
async function mockCopies(page: Page, copies = true, byTarget = false) {
  const reads: string[] = [];
  await page.route("**/*", async route => {
    const url = new URL(route.request().url()), path = url.pathname;
    if (!["127.0.0.1", "localhost"].includes(url.hostname)) return route.abort();
    if (!path.startsWith("/api/admin/")) return route.continue();
    const reply = (body: unknown) => route.fulfill({ json: body });
    if (path === "/api/admin/auth/me") return reply({ authenticated: true, backend: "Jellyfin", user: { id: "backend-user", allstarrUserId: listener, name: "Josh", isAdministrator: false } });
    if (path === "/api/admin/auth/oidc/status") return reply({ enabled: false });
    if (path === "/api/admin/ui/schema") return reply({ providers: [{ id: "spotify", name: "Spotify" }, { id: "jellyfin", name: "Jellyfin" }], configSections: [] });
    if (path === "/api/admin/playlist-links") return reply({ playlistLinks: [playlist, { ...playlist, id: "44444444-4444-4444-8444-444444444444", name: "Shared mix", virtualPlaylistId: "allstarr-vpl-44444444444444448444444444444444", targetPlaylistId: "different" }] });
    if (path === `/api/admin/listeners/${listener}/playlist-forks`) {
      reads.push(path);
      return reply(copies ? [{ id: "allstarr-vpl-55555555555545558555555555555555", name: "Evening mix", originalPlaylistId: byTarget ? "older-playlist" : virtualPlaylistId, originalTargetPlaylistId: "33333333-3333-4333-8333-333333333333", backendInstanceId: "home", createdAt: "2026-10-28T12:00:00Z" }] : []);
    }
    if (path === `/api/admin/playlist-links/${id}`) return reply({ ...playlist, tracks: [], unresolvedCount: 0, reviewCount: 0, unknownDurationCount: 0, retrievedAt: "2026-10-28T12:00:00Z" });
    if (path === "/api/admin/jobs") return reply({ jobs: [], progress: [] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return reply({});
  });
  return reads;
}
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Playlist copy badge and explanation at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const reads = await mockCopies(page);
    await page.goto("/#/library/playlists");
    const row = page.locator(".playlist-row").filter({ hasText: "Evening mix" });
    await expect(row.getByText("Your copy", { exact: true })).toBeVisible();
    await expect(page.locator(".playlist-row").filter({ hasText: "Shared mix" }).getByText("Your copy", { exact: true })).toHaveCount(0);
    const folder = "test-results/shots-uiactivity";
    await mkdir(folder, { recursive: true });
    await page.screenshot({ path: `${folder}/playlist-copy-list-${width}-${theme}.png`, fullPage: true });
    await page.getByRole("button", { name: "Open Evening mix playlist details" }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText("Your copy", { exact: true })).toBeVisible();
    await expect(dialog).toContainText("You edited this playlist, so you have your own copy. The original keeps syncing for everyone else.");
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: `${folder}/playlist-copy-details-${width}-${theme}.png`, fullPage: true });
    expect(reads.length).toBeGreaterThan(0);
  });
}
test("Playlist copy uses its original media-server playlist reference", async ({ page }) => {
  await mockCopies(page, true, true);
  await page.goto("/#/library/playlists");
  await expect(page.getByText("Your copy", { exact: true })).toBeVisible();
});
test("An imported playlist without personal edits has no copy badge", async ({ page }) => {
  await mockCopies(page, false);
  await page.goto("/#/library/playlists");
  await expect(page.getByRole("button", { name: "Open Evening mix playlist details" })).toBeVisible();
  await expect(page.getByText("Your copy", { exact: true })).toHaveCount(0);
});
