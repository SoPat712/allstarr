import { mkdir } from "node:fs/promises";
import { expect, test, type Page } from "@playwright/test";
import type { MixCard } from "../src/lib/api";
import { capture } from "./for-you-fixtures";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const listener = "11111111-1111-4111-8111-111111111111";
const id = "22222222-2222-4222-8222-222222222222";
const virtualPlaylistId = `allstarr-vpl-${id.replaceAll("-", "")}`;
const playlist = { id, virtualPlaylistId, name: "Evening mix", enabled: true, sourceProviderId: "spotify", sourcePlaylistId: "evening", targetProtocol: "jellyfin", targetBackendInstanceId: "home", targetPlaylistId: "33333333333343338333333333333333", mode: "virtual", importMode: "linked", projectionMode: "resolved", trackRetention: "onDemand", trackCount: 1, matchedCount: 1, unmatchedCount: 0, playableCount: 1, materializedCount: 0, routeCoverage: [], metrics: { total: 1, review: 0 } };
const coverUrl = `/api/admin/mixes/${id}/cover?v=1`;
const mixPlaylist = { ...playlist, name: "After hours mix", sourceProviderId: "lastfm", sourcePlaylistId: "loved", artworkUrl: coverUrl };
const keptMix: MixCard = { id: "66666666-6666-4666-8666-666666666666", kind: "daily-1", name: "Blend 1", description: "Songs picked for you", section: "madeForYou", sortOrder: 0, sourceLabel: null, recipeVersion: "1", updatedAt: "2026-10-29T04:00:00Z", readiness: { ready: true, message: "Ready" }, inApps: true, appsAllowed: true, coverArtwork: [] };
const builtInMixes: MixCard[] = [keptMix, { ...keptMix, id: "77777777-7777-4777-8777-777777777777", kind: "focus", name: "Focus", section: "moods" }, { ...keptMix, id: "88888888-8888-4888-8888-888888888888", kind: "custom", name: "Evening listening", section: "custom" }, { ...keptMix, id: "99999999-9999-4999-8999-999999999999", kind: "discover-weekly", name: "Uncharted", inApps: false, }, { ...keptMix, id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", kind: "release-radar", name: "Just Dropped", inApps: false, appsAllowed: false }];
async function mockCopies(page: Page, copies = true, byTarget = false, items?: Array<typeof playlist & { artworkUrl?: string }>, cards: MixCard[] = []) {
  const reads: string[] = [];
  await page.route("**/*", async route => {
    const url = new URL(route.request().url()), path = url.pathname;
    if (!["127.0.0.1", "localhost"].includes(url.hostname)) return route.abort();
    if (!path.startsWith("/api/admin/")) return route.continue();
    const reply = (body: unknown) => route.fulfill({ json: body });
    if (path === "/api/admin/auth/me") return reply({ authenticated: true, backend: "Jellyfin", user: { id: "backend-user", allstarrUserId: listener, name: "Josh", isAdministrator: false } });
    if (path === "/api/admin/auth/oidc/status") return reply({ enabled: false });
    if (path === "/api/admin/ui/schema") return reply({ providers: [{ id: "spotify", name: "Spotify" }, { id: "lastfm", name: "Last.fm" }, { id: "jellyfin", name: "Jellyfin" }], configSections: [] });
    if (path.startsWith("/api/admin/mixes/") && path.endsWith("/cover")) return route.fulfill({ contentType: "image/svg+xml", body: '<svg xmlns="http://www.w3.org/2000/svg" width="400" height="400" viewBox="0 0 400 400"><rect width="400" height="400" fill="#163c45"/><circle cx="200" cy="200" r="150" fill="#dcae62"/><circle cx="200" cy="200" r="95" fill="#163c45"/><circle cx="200" cy="200" r="24" fill="#e5d8bf"/></svg>' });
    if (path === "/api/admin/mixes") return reply(cards);
    if (path.endsWith("/in-apps")) {
      reads.push(`${route.request().method()} ${path}`);
      const card = cards.find(card => path.includes(`/${card.id}/`))!; card.inApps = route.request().method() === "POST";
      return reply(card);
    }
    if (path.startsWith("/api/admin/mixes/")) {
      reads.push(path);
      return reply({ mix: cards.find(card => card.id === path.split("/").at(-1)), songs: [{ mixId: keptMix.id, generation: 1, source: "library", reason: "A familiar favourite", score: 1, song: { id: "song", canonicalRecordingId: null, title: "Blue morning", artist: "North", album: "Open skies" } }] });
    }
    if (path === "/api/admin/releases") return reply({ entries: [] });
    if (path === "/api/admin/playlist-sources") return reply({ accounts: [] });
    if (path === "/api/admin/playlist-links") return reply({ playlistLinks: items ?? [playlist, { ...playlist, id: "44444444-4444-4444-8444-444444444444", name: "Shared mix", virtualPlaylistId: "allstarr-vpl-44444444444444448444444444444444", targetPlaylistId: "different" }] });
    if (path === `/api/admin/listeners/${listener}/playlist-forks`) {
      reads.push(path);
      return reply(copies ? [{ id: "allstarr-vpl-55555555555545558555555555555555", name: "Evening mix", originalPlaylistId: byTarget ? "older-playlist" : virtualPlaylistId, originalTargetPlaylistId: "33333333-3333-4333-8333-333333333333", backendInstanceId: "home", createdAt: "2026-10-28T12:00:00Z" }] : []);
    }
    if (path === `/api/admin/playlist-links/${id}`) return reply({ ...(items?.find(item => item.id === id) ?? playlist), tracks: [], unresolvedCount: 0, reviewCount: 0, unknownDurationCount: 0, durationMs: 180000, retrievedAt: "2026-10-28T12:00:00Z" });
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

test("Every imported playlist stays in one filterable and sortable list", async ({ page }) => {
  const items = [mixPlaylist, ...Array.from({ length: 24 }, (_, index) => ({
    ...playlist,
    id: `collection-${index + 1}`,
    name: `Collection ${index + 1}`,
    enabled: index !== 23,
    trackCount: index + 2,
  }))];
  await mockCopies(page, false, false, items);
  await page.goto("/#/library/playlists");
  const rows = page.locator(".playlist-row");
  await expect(rows).toHaveCount(25);
  await expect(rows.first()).toContainText("After hours mix");
  await expect(rows.last()).toContainText("Collection 9");
  await expect(page.getByRole("navigation", { name: "Playlist pages" })).toHaveCount(0);
  await page.getByRole("searchbox", { name: "Filter playlists" }).fill("Collection 24");
  await expect(rows).toHaveCount(1);
  await expect(rows).toContainText("Collection 24");
  await page.getByRole("searchbox", { name: "Filter playlists" }).fill("");
  await page.getByRole("button", { name: "Playlist status", exact: true }).click();
  await page.getByRole("option", { name: "Paused", exact: true }).click();
  await expect(rows).toHaveCount(1);
  await expect(rows).toContainText("Collection 24");
  await page.getByRole("button", { name: "Playlist status", exact: true }).click();
  await page.getByRole("option", { name: "All states", exact: true }).click();
  await page.getByRole("button", { name: "Sort playlists", exact: true }).click();
  await page.getByRole("option", { name: "Tracks", exact: true }).click();
  await expect(rows).toHaveCount(25);
  await expect(rows.first()).toContainText("Collection 24");
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Mix cover appears in the playlist row and detail hero at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockCopies(page, false, false, [mixPlaylist]);
    await page.goto("/#/library/playlists");
    const row = page.locator(".playlist-row").filter({ hasText: mixPlaylist.name });
    await expect(row.locator(".route-pair > span:not(.provider-mark)").filter({ hasText: /^Last\.fm$/ })).toBeVisible();
    const rowCover = row.locator(".playlist-art img");
    await expect(rowCover).toHaveAttribute("src", coverUrl);
    await expect(rowCover).toHaveAttribute("loading", "lazy");
    await expect.poll(() => rowCover.evaluate(image => (image as HTMLImageElement).naturalWidth)).toBeGreaterThan(0);
    if (width === 390) await expectContainedMobileGeometry(page);
    const folder = new URL("../test-results/libcovers/", import.meta.url).pathname;
    await mkdir(folder, { recursive: true });
    await page.screenshot({ path: `${folder}/playlist-cover-list-${width}-${theme}.png`, fullPage: true });
    await row.getByRole("button", { name: `Open ${mixPlaylist.name} playlist details` }).click();
    await expect(page.getByRole("dialog").locator(".hero-route > span:not(.provider-mark)").filter({ hasText: /^Last\.fm$/ })).toBeVisible();
    const heroCover = page.getByRole("dialog").locator(".hero-art img");
    await expect(heroCover).toHaveAttribute("src", coverUrl);
    await expect(heroCover).toHaveAttribute("loading", "eager");
    await expect.poll(() => heroCover.evaluate(image => (image as HTMLImageElement).naturalWidth)).toBeGreaterThan(0);
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: `${folder}/playlist-cover-details-${width}-${theme}.png`, fullPage: true });
  });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Kept mixes share the playlist list at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const reads = await mockCopies(page, false, false, [mixPlaylist], [...builtInMixes.map(card => ({ ...card })), { ...keptMix, id, kind: "external", name: mixPlaylist.name, section: "yourServices" }]);
    await page.goto("/#/library/playlists");
    const rows = page.locator(".playlist-row");
    await expect(rows).toHaveCount(4);
    await expect(page.getByText("4 playlists", { exact: true })).toBeVisible();
    await expect(rows.locator(".playlist-title-line strong")).toHaveText(["After hours mix", "Blend 1", "Evening listening", "Focus"]);
    await expect(rows.filter({ hasText: "Uncharted" })).toHaveCount(0);
    await expect(rows.filter({ hasText: "Just Dropped" })).toHaveCount(0);
    const row = rows.filter({ hasText: keptMix.name });
    await expect(row.getByText("Mix · in your apps", { exact: true })).toBeVisible();
    await expect(row.getByText("1 song", { exact: true })).toBeVisible();
    await expect(row).not.toContainText("confirmed");
    const cover = row.locator(".playlist-art img");
    await expect(cover).toHaveAttribute("src", `/api/admin/mixes/${keptMix.id}/cover?v=${encodeURIComponent(keptMix.updatedAt!)}`);
    await expect(cover).toHaveAttribute("loading", "lazy");
    await expect.poll(() => cover.evaluate(image => (image as HTMLImageElement).naturalWidth)).toBeGreaterThan(0);
    if (width === 390) {
      await expectContainedMobileGeometry(page);
      for (const mixRow of await rows.filter({ hasText: "Mix · in your apps" }).all()) {
        const label = await mixRow.getByText("Mix · in your apps", { exact: true }).boundingBox();
        const count = await mixRow.getByText("1 song", { exact: true }).boundingBox();
        expect(label).not.toBeNull();
        expect(count).not.toBeNull();
        expect(count!.y - (label!.y + label!.height)).toBeGreaterThanOrEqual(0);
        expect(count!.y - (label!.y + label!.height)).toBeLessThanOrEqual(8);
      }
    }
    await capture(page, "playlist-added-mixes", width, theme);
    await page.getByRole("searchbox", { name: "Filter playlists" }).fill("blend");
    await expect(rows).toHaveCount(1);
    await expect(rows).toContainText(keptMix.name);
    await page.getByRole("searchbox", { name: "Filter playlists" }).fill("");
    for (const status of ["Ready", "Needs attention", "Paused"]) {
      await page.getByRole("button", { name: "Playlist status", exact: true }).click();
      await page.getByRole("option", { name: status, exact: true }).click();
      await expect(rows.getByText("Mix · in your apps", { exact: true })).toHaveCount(0);
    }
    await page.getByRole("button", { name: "Playlist status", exact: true }).click();
    await page.getByRole("option", { name: "All states", exact: true }).click();
    await row.getByRole("link", { name: `Open ${keptMix.name} mix`, exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/#/for-you\\?mix=${keptMix.id}$`));
    await expect(page.getByRole("dialog").getByRole("heading", { name: keptMix.name, exact: true })).toBeVisible();
    expect(reads.filter(path => path === `/api/admin/mixes/${id}`)).toHaveLength(0);
    expect(reads.filter(path => path === `/api/admin/mixes/${builtInMixes[3].id}`)).toHaveLength(0);
    await page.getByRole("dialog").getByRole("link", { name: "Manage in Playlists", exact: true }).click();
    await page.getByRole("button", { name: `Remove ${keptMix.name} from my apps`, exact: true }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Remove from my apps", exact: true }).click();
    await expect(rows.filter({ hasText: keptMix.name })).toHaveCount(0);
    await capture(page, "playlist-after-remove", width, theme);
    await page.goto(`/#/for-you?mix=${keptMix.id}`);
    await page.getByRole("dialog").getByRole("button", { name: "Add to my apps", exact: true }).click();
    await page.getByRole("dialog").getByRole("link", { name: "Manage in Playlists", exact: true }).click();
    await expect(rows.filter({ hasText: keptMix.name })).toHaveCount(1);
    await capture(page, "playlist-after-readd", width, theme);
  });
}

test("Kept mixes appear when there are no imported playlists", async ({ page }) => {
  await mockCopies(page, false, false, [], [keptMix]);
  await page.goto("/#/library/playlists");
  await expect(page.locator(".playlist-row")).toHaveCount(1);
  await expect(page.getByText("1 playlist", { exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: `Open ${keptMix.name} mix`, exact: true })).toBeVisible();
});

test("Removing a mix from playlists keeps it available to add again", async ({ page }) => {
  const cards = [{ ...keptMix }]; const reads = await mockCopies(page, false, false, [], cards);
  await page.goto("/#/library/playlists");
  const remove = page.getByRole("button", { name: `Remove ${keptMix.name} from my apps`, exact: true }); await remove.click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Cancel", exact: true }).click();
  expect(cards[0].inApps).toBe(true); await remove.click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Remove from my apps", exact: true }).click();
  await expect(page.locator(".playlist-row")).toHaveCount(0);
  expect(reads).toContain(`DELETE /api/admin/mixes/${keptMix.id}/in-apps`);
  await page.goto(`/#/for-you?mix=${keptMix.id}`);
  await page.getByRole("dialog").getByRole("button", { name: "Add to my apps", exact: true }).click();
  await expect(page.getByRole("dialog").getByText("In your apps", { exact: true })).toBeVisible();
  await page.getByRole("dialog").getByRole("link", { name: "Manage in Playlists", exact: true }).click();
  await expect(page.locator(".playlist-row")).toHaveCount(1);
});
test("A failed removal keeps the mix and can be retried", async ({ page }) => {
  const cards = [{ ...keptMix }]; await mockCopies(page, false, false, [], cards); let failing = true;
  await page.route("**/in-apps", route => failing ? route.fulfill({ status: 503, json: {} }) : route.fallback());
  await page.goto("/#/library/playlists");
  await page.getByRole("button", { name: `Remove ${keptMix.name} from my apps`, exact: true }).click();
  const dialog = page.getByRole("alertdialog"), confirm = dialog.getByRole("button", { name: "Remove from my apps", exact: true });
  await confirm.click(); await expect(dialog.getByRole("alert")).toContainText("Try again.");
  expect(cards[0].inApps).toBe(true); failing = false; await confirm.click();
  await expect(dialog).toHaveCount(0); await expect(page.locator(".playlist-row")).toHaveCount(0);
});
