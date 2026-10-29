import { expect, test, type Page } from "@playwright/test";
import type { FollowedReleaseArtist, ReleaseInbox, ReleaseInboxEntry } from "../src/lib/api";
import { appearance, capture, mockIntelligence } from "./intelligence-fixtures";

const followedArtists: FollowedReleaseArtist[] = [
  { providerId: "qobuz", artistId: "north", displayName: "North" },
  { providerId: "qobuz", artistId: "mira", displayName: "Mira" },
  { providerId: "qobuz", artistId: "cedar", displayName: "Cedar" },
  { providerId: "qobuz", artistId: "willow", displayName: "Willow" },
];

function release(id: string, overrides: Partial<ReleaseInboxEntry> = {}): ReleaseInboxEntry {
  return { id, title: id, artist: "North", releaseDate: "2026-10-27", datePrecision: "day",
    type: "album", isExplicit: false, edition: "", exclusionReason: null, sources: [], ...overrides };
}

function populatedInbox(): ReleaseInbox {
  return {
    entries: [
      release("aurora", { title: "Aurora", isExplicit: true, edition: "Deluxe edition" }),
      release("contours", { title: "Contours", artist: "Mira", type: "ep", releaseDate: "2026-10-21" }),
      release("low-tide", { title: "Low Tide", artist: "Cedar", type: "single", isExplicit: null, releaseDate: "2026-10-12" }),
      release("slow-season", { title: "Slow Season", artist: "Willow", releaseDate: "2026-09", datePrecision: "month" }),
    ],
    excluded: [
      ...Array.from({ length: 3 }, (_, index) => release(`Compilation ${index + 1}`, { exclusionReason: "compilation" })),
      ...Array.from({ length: 2 }, (_, index) => release(`Reissue ${index + 1}`, { exclusionReason: "reissue" })),
      release("Guest collection", { exclusionReason: "appearance" }),
      release("Undated release", { releaseDate: null, datePrecision: "unknown", exclusionReason: "date-unknown" }),
    ],
    coverage: followedArtists.map((artist, index) => ({ artist, status: index === 3 ? "known" : index === 1 ? "partial" : "unknown", checkedAt: null })),
  };
}

async function mockReleases(page: Page, initialInbox = populatedInbox(), initialArtists = followedArtists) {
  const state = {
    inbox: structuredClone(initialInbox), artists: structuredClone(initialArtists),
    mutations: [] as Array<{ method: string; path: string }>,
    reads: 0,
  };
  await mockIntelligence(page, { inbox: state.inbox, artists: state.artists });
  await page.route("**/api/admin/releases**", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (!["127.0.0.1", "localhost"].includes(url.hostname)) return route.abort();
    if (request.method() !== "GET") state.mutations.push({ method: request.method(), path });
    if (path === "/api/admin/releases" && request.method() === "GET") {
      state.reads++;
      return route.fulfill({ json: state.inbox });
    }
    if (path === "/api/admin/releases/artists" && request.method() === "GET") return route.fulfill({ json: state.artists });
    if (path === "/api/admin/releases/artists/from-hearts" && request.method() === "POST") {
      state.artists = [...state.artists, ...Array.from({ length: 37 }, (_, index) => ({
        providerId: "qobuz", artistId: `hearted-${index}`, displayName: `Hearted artist ${index + 1}`,
      }))];
      return route.fulfill({ json: { followed: 37 } });
    }
    if (path.startsWith("/api/admin/releases/artists/") && request.method() === "DELETE") {
      const artist = state.artists.find(item => path === `/api/admin/releases/artists/${encodeURIComponent(item.providerId)}/${encodeURIComponent(item.artistId)}`);
      if (artist) {
        state.artists = state.artists.filter(item => item !== artist);
        state.inbox.entries = state.inbox.entries.filter(item => item.artist !== artist.displayName);
        state.inbox.coverage = state.inbox.coverage.filter(item => item.artist.artistId !== artist.artistId || item.artist.providerId !== artist.providerId);
      }
      return route.fulfill({ status: 204 });
    }
    if (path.startsWith("/api/admin/releases/") && request.method() === "DELETE") {
      state.inbox.entries = state.inbox.entries.filter(item => path !== `/api/admin/releases/${encodeURIComponent(item.id)}`);
      return route.fulfill({ status: 204 });
    }
    return route.fallback();
  });
  await page.clock.setFixedTime(new Date("2026-10-28T12:00:00Z"));
  return state;
}

async function openReleases(page: Page) {
  await page.goto("/#/intelligence?section=releases");
  await expect(page.getByRole("button", { name: /^Followed artists \(/ })).toBeVisible();
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`New releases and followed artists at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    const state = await mockReleases(page);
    await openReleases(page);
    await expect(page.getByRole("region", { name: "This week", exact: true })).toContainText("Aurora");
    await expect(page.getByRole("region", { name: "Last week", exact: true })).toContainText("Contours");
    await expect(page.getByRole("region", { name: "Earlier", exact: true })).toContainText("Low Tide");
    const album = page.getByRole("article", { name: "Aurora by North", exact: true });
    await expect(album).toContainText("Album · Explicit");
    await expect(album).toContainText("Oct 27, 2026 · Deluxe edition");
    await expect(page.getByRole("article", { name: "Contours by Mira", exact: true })).toContainText("EP · Clean");
    await expect(page.getByRole("article", { name: "Low Tide by Cedar", exact: true })).toContainText("Single · Explicit status unknown");
    await expect(page.getByRole("article", { name: "Slow Season by Willow", exact: true })).toContainText("September 2026");
    await page.getByText("We couldn't check 3 artists", { exact: true }).click();
    const coverage = page.locator("details").filter({ hasText: "We couldn't check 3 artists" });
    await expect(coverage).toContainText("Some releases may be missing.");
    for (const name of ["North", "Mira", "Cedar"]) await expect(coverage.getByText(name, { exact: true })).toBeVisible();
    await page.getByText("Hidden: 7 releases", { exact: true }).click();
    const hidden = page.locator("details").filter({ hasText: "Hidden: 7 releases" });
    await expect(hidden).toContainText("Guest collection · North · Guest appearance");
    await expect(hidden).toContainText("Undated release · North · Date unavailable");
    await expect(hidden.getByText("Guest collection", { exact: true })).toBeVisible();
    await capture(page, "new-releases", width, theme);
    await page.getByRole("button", { name: "Followed artists (4)", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Followed artists (4)", exact: true });
    await expect(dialog.getByRole("button", { name: "Unfollow North", exact: true })).toBeVisible();
    await expect(dialog.getByRole("searchbox")).toHaveCount(0);
    await capture(page, "followed-artists", width, theme);
    await dialog.getByRole("button", { name: "Done", exact: true }).click();
    expect(state.mutations).toEqual([]);
  });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Empty releases and follow confirmation at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    const state = await mockReleases(page, { entries: [], excluded: [], coverage: [] }, []);
    await openReleases(page);
    await expect(page.getByText("Follow artists to see their new music here.", { exact: true })).toBeVisible();
    await capture(page, "new-releases-empty", width, theme);
    const followButton = page.getByRole("button", { name: "Follow artists from my hearts", exact: true });
    await followButton.click();
    const confirm = page.getByRole("alertdialog", { name: "Follow artists from my hearts", exact: true });
    await expect(confirm).toContainText("Follow up to 200 artists from songs you've hearted?");
    await capture(page, "new-releases-follow", width, theme);
    await confirm.getByRole("button", { name: "Cancel", exact: true }).click();
    expect(state.mutations).toEqual([]);
    await followButton.click();
    await confirm.getByRole("button", { name: "Follow artists", exact: true }).click();
    await expect(confirm).toBeHidden();
    await expect(page.getByText("Following 37 new artists", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Followed artists (37)", exact: true })).toBeVisible();
    expect(state.mutations).toEqual([{ method: "POST", path: "/api/admin/releases/artists/from-hearts" }]);
  });
}

test("Dismissing a release updates the inbox and keeps other releases", async ({ page }) => {
  const state = await mockReleases(page);
  await openReleases(page);
  await page.getByRole("button", { name: "Dismiss Aurora", exact: true }).click();
  await expect(page.getByRole("article", { name: "Aurora by North", exact: true })).toHaveCount(0);
  await expect(page.getByRole("article", { name: "Contours by Mira", exact: true })).toBeVisible();
  expect(state.mutations).toEqual([{ method: "DELETE", path: "/api/admin/releases/aurora" }]);
});

test("Unfollowing an artist updates the followed count and release inbox", async ({ page }) => {
  const state = await mockReleases(page);
  await openReleases(page);
  await page.getByRole("button", { name: "Followed artists (4)", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Followed artists (4)", exact: true });
  await dialog.getByRole("button", { name: "Unfollow North", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Followed artists (3)", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Unfollow North", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Done", exact: true }).click();
  await expect(page.getByRole("button", { name: "Followed artists (3)", exact: true })).toBeVisible();
  await expect(page.getByRole("article", { name: "Aurora by North", exact: true })).toHaveCount(0);
  expect(state.mutations).toEqual([{ method: "DELETE", path: "/api/admin/releases/artists/qobuz/north" }]);
});

test("Live refresh keeps the inbox visible and preserves an open artist dialog", async ({ page }) => {
  const state = await mockReleases(page);
  await openReleases(page);
  await expect(page.getByRole("article", { name: "Aurora by North", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Followed artists (4)", exact: true }).click();
  const reads = state.reads;
  state.inbox.entries = [...state.inbox.entries, release("fresh", { title: "Fresh arrival", artist: "Willow" })];
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect.poll(() => state.reads).toBeGreaterThan(reads);
  await expect(page.getByRole("dialog", { name: "Followed artists (4)", exact: true })).toBeVisible();
  await expect(page.locator(".skeleton-panel")).toHaveCount(0);
  await page.getByRole("button", { name: "Done", exact: true }).click();
  await expect(page.getByRole("article", { name: "Fresh arrival by Willow", exact: true })).toBeVisible();
});
