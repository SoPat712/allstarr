import { expect, test, type Page } from "@playwright/test";
import type { FollowedReleaseArtist, ReleaseInbox, ReleaseInboxEntry } from "../src/lib/api";
import { appearance, capture, mockIntelligence } from "./intelligence-fixtures";
import { expectContainedMobileGeometry, widenText } from "./mobile-geometry";

const followedArtists: FollowedReleaseArtist[] = [
  { providerId: "qobuz", artistId: "north", displayName: "North" },
  { providerId: "qobuz", artistId: "mira", displayName: "Mira" },
  { providerId: "qobuz", artistId: "cedar", displayName: "Cedar" },
  { providerId: "qobuz", artistId: "willow", displayName: "Willow", libraryArtistId: "native-willow", libraryImageTag: "native-picture" },
];

function release(id: string, overrides: Partial<ReleaseInboxEntry> = {}): ReleaseInboxEntry {
  return { id, title: id, artist: "North", releaseDate: "2026-10-27", datePrecision: "day",
    type: "album", isExplicit: false, edition: "", exclusionReason: null, sources: [], artworkUrl: `/api/admin/releases/artwork/qobuz/${encodeURIComponent(id)}`, ...overrides };
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
    coverage: followedArtists.map((artist, index) => ({ artist, status: index === 3 ? "library-only" : index === 1 ? "partial" : "unknown", checkedAt: null })),
  };
}

async function mockReleases(page: Page, initialInbox = populatedInbox(), initialArtists = followedArtists) {
  const state = {
    inbox: structuredClone(initialInbox), artists: structuredClone(initialArtists),
    mutations: [] as Array<{ method: string; path: string }>,
    reads: 0,
  };
  await mockIntelligence(page, { inbox: state.inbox, artists: state.artists });
  await page.route("**/api/admin/stats/art?**", route => route.fulfill({
    contentType: "image/png", body: Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6iUAAAAASUVORK5CYII=", "base64"),
  }));
  await page.route("**/api/admin/releases**", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (!["127.0.0.1", "localhost"].includes(url.hostname)) return route.abort();
    if (request.method() !== "GET") state.mutations.push({ method: request.method(), path: path + url.search });
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
      const artist = state.artists.find(item => path === `/api/admin/releases/artists/${encodeURIComponent(item.providerId)}/${encodeURIComponent(item.artistId)}` &&
        (item.libraryArtistId ? url.searchParams.get("libraryArtistId") === item.libraryArtistId : !url.searchParams.has("libraryArtistId")));
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
  await expect(page.getByRole("button", { name: /^Following \(/ })).toBeVisible();
}

async function openFollowConfirmation(page: Page) {
  await page.getByRole("button", { name: "Release actions", exact: true }).click();
  await page.getByRole("menuitem", { name: "Follow artists from my hearts", exact: true }).click();
}

test("Empty release actions fit with wider text at 390px", async ({ page }) => {
  await appearance(page, 390, "light");
  await mockReleases(page, { entries: [], excluded: [], coverage: [] }, []);
  await openReleases(page);
  const actions = page.locator(".empty-action");
  await expect(actions.getByRole("button", { name: "Follow my top artists", exact: true })).toBeVisible();
  await widenText(actions.locator("[data-slot=button]"));
  await expectContainedMobileGeometry(page);
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`New releases and followed artists at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    const state = await mockReleases(page);
    await openReleases(page);
    await expect(page.getByRole("button", { name: "Manage Willow", exact: true }).locator("img"))
      .toHaveAttribute("src", "/api/admin/stats/art?artist=Willow&libraryArtistId=native-willow&tag=native-picture");
    await expect(page.locator(".release-card")).toHaveCount(4);
    await expect(page.locator(".release-card h3")).toHaveText(["Aurora", "Contours", "Low Tide", "Slow Season"]);
    const album = page.getByRole("article", { name: "Aurora by North", exact: true });
    await expect(album).toContainText("Album · Explicit");
    await expect(album).toContainText("Oct 27, 2026 · Deluxe edition");
    await expect(page.getByRole("article", { name: "Contours by Mira", exact: true })).toContainText("EP · Clean");
    await expect(page.getByRole("article", { name: "Low Tide by Cedar", exact: true })).toContainText("Single · Explicit status unknown");
    await expect(page.getByRole("article", { name: "Slow Season by Willow", exact: true })).toContainText("September 2026");
    await page.getByText("Couldn't check 3 artists.", { exact: true }).click();
    const coverage = page.locator("details").filter({ hasText: "Couldn't check 3 artists." });
    await expect(coverage).toContainText("Some releases may be missing.");
    await expect(coverage.getByText("Willow", { exact: true })).toHaveCount(0);
    for (const name of ["North", "Mira", "Cedar"]) await expect(coverage.getByText(name, { exact: true })).toBeVisible();
    await page.getByText("Hidden: 7 releases", { exact: true }).click();
    const hidden = page.locator("details").filter({ hasText: "Hidden: 7 releases" });
    await expect(hidden).toContainText("Guest collection · North · Guest appearance");
    await expect(hidden).toContainText("Undated release · North · Date unavailable");
    await expect(hidden.getByText("Guest collection", { exact: true })).toBeVisible();
    await capture(page, "new-releases", width, theme);
    await page.getByRole("button", { name: "Following (4)", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Followed artists (4)", exact: true });
    await expect(dialog.getByRole("button", { name: "Unfollow North", exact: true })).toBeVisible();
    await expect(dialog.getByRole("searchbox", { name: "Find an artist to follow" })).toHaveCount(1);
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
    await openFollowConfirmation(page);
    const confirm = page.getByRole("alertdialog", { name: "Follow artists from your hearts", exact: true });
    await expect(confirm).toContainText("Follow up to 200 artists from songs you've hearted?");
    await expect(confirm.getByRole("button", { name: "Cancel", exact: true })).toHaveAttribute("type", "button");
    await capture(page, "new-releases-follow", width, theme);
    await confirm.getByRole("button", { name: "Cancel", exact: true }).click();
    expect(state.mutations).toEqual([]);
    await openFollowConfirmation(page);
    await confirm.getByRole("button", { name: "Follow artists from my hearts", exact: true }).click();
    await expect(confirm).toBeHidden();
    await expect(page.getByText("Following 37 new artists", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Following (37)", exact: true })).toBeVisible();
    expect(state.mutations).toEqual([{ method: "POST", path: "/api/admin/releases/artists/from-hearts" }]);
  });
}

test("Check again starts a release check and shows the results when it lands", async ({ page }) => {
  const state = await mockReleases(page);
  let checks = 0;
  await page.route("**/api/admin/releases/check", route => {
    checks++;
    state.inbox.coverage = state.inbox.coverage.map(item => ({ ...item, status: item.status === "library-only" ? item.status : "known" }));
    return route.fulfill({ json: { checking: true } });
  });
  await openReleases(page);
  const unchecked = page.locator("details").filter({ hasText: "Couldn't check 3 artists." });
  await expect(unchecked).toBeVisible();
  const readsBefore = state.reads;
  await page.getByRole("button", { name: "Check again", exact: true }).click();
  await expect(page.getByRole("button", { name: "Checking…", exact: true })).toBeDisabled();
  await expect.poll(() => checks).toBe(1);
  await expect(unchecked).toHaveCount(0, { timeout: 10_000 });
  expect(state.reads).toBeGreaterThan(readsBefore);
});

test("Dismissing a release updates the inbox and keeps other releases", async ({ page }) => {
  const state = await mockReleases(page);
  await openReleases(page);
  await page.getByRole("button", { name: "Actions for Aurora", exact: true }).click();
  await page.getByRole("menuitem", { name: "Dismiss Aurora", exact: true }).click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Dismiss release", exact: true }).click();
  await expect(page.getByRole("article", { name: "Aurora by North", exact: true })).toHaveCount(0);
  await expect(page.getByRole("article", { name: "Contours by Mira", exact: true })).toBeVisible();
  expect(state.mutations).toEqual([{ method: "DELETE", path: "/api/admin/releases/aurora" }]);
});

test("Unfollowing an artist updates the followed count and release inbox", async ({ page }) => {
  const state = await mockReleases(page);
  await openReleases(page);
  await page.getByRole("button", { name: "Following (4)", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Followed artists (4)", exact: true });
  await dialog.getByRole("button", { name: "Unfollow North", exact: true }).click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Unfollow artist", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Followed artists (3)", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Unfollow North", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Done", exact: true }).click();
  await expect(page.getByRole("button", { name: "Following (3)", exact: true })).toBeVisible();
  await expect(page.getByRole("article", { name: "Aurora by North", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Following (3)", exact: true }).click();
  await page.getByRole("button", { name: "Unfollow Willow", exact: true }).click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Unfollow artist", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Followed artists (2)", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Unfollow Willow", exact: true })).toHaveCount(0);
  expect(state.mutations).toEqual([
    { method: "DELETE", path: "/api/admin/releases/artists/qobuz/north" },
    { method: "DELETE", path: "/api/admin/releases/artists/qobuz/willow?libraryArtistId=native-willow" },
  ]);
});

test("Live refresh keeps the inbox visible and preserves an open artist dialog", async ({ page }) => {
  const state = await mockReleases(page);
  await openReleases(page);
  await expect(page.getByRole("article", { name: "Aurora by North", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Following (4)", exact: true }).click();
  const reads = state.reads;
  state.inbox.entries = [...state.inbox.entries, release("fresh", { title: "Fresh arrival", artist: "Willow" })];
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect.poll(() => state.reads).toBeGreaterThan(reads);
  await expect(page.getByRole("dialog", { name: "Followed artists (4)", exact: true })).toBeVisible();
  await expect(page.locator(".skeleton-panel")).toHaveCount(0);
  await page.getByRole("button", { name: "Done", exact: true }).click();
  await expect(page.getByRole("article", { name: "Fresh arrival by Willow", exact: true })).toBeVisible();
});

test("A release panel lists its tracks and folds the rest away", async ({ page }) => {
  await mockReleases(page);
  await page.route("**/api/admin/releases/aurora/tracks", route => route.fulfill({ json: { providerId: "qobuz", tracks: Array.from({ length: 9 }, (_, index) => ({
    number: index + 1, title: `Track ${index + 1}`, artists: "North", durationMs: 200_000 + index * 1000, isExplicit: index === 0,
  })) } }));
  await page.goto("/#/intelligence?section=releases&release=aurora");
  const panel = page.getByRole("dialog", { name: "Aurora", exact: true });
  const tracks = panel.getByRole("region", { name: "Tracks", exact: true });
  await expect(tracks).toContainText("From Qobuz");
  await expect(tracks.getByRole("listitem")).toHaveCount(6);
  await expect(tracks.getByRole("listitem").first()).toContainText("Track 1");
  await expect(tracks.getByRole("listitem").first()).toContainText("3:20");
  await tracks.getByRole("button", { name: "3 more tracks", exact: true }).click();
  await expect(tracks.getByRole("listitem")).toHaveCount(9);
  // Listeners can add a release to their apps; keeping files is for administrators.
  await expect(panel.getByRole("button", { name: "Keep album…", exact: true })).toHaveCount(0);
  const added: string[] = [];
  await page.route("**/api/admin/releases/aurora/apps", route => { added.push(route.request().method()); return route.fulfill({ json: { mixId: "mix", songCount: 9 } }); });
  await panel.getByRole("button", { name: "Add to apps", exact: true }).click();
  await expect(panel.getByRole("button", { name: "In your apps", exact: true })).toBeDisabled();
  await expect(page.getByText("Aurora is in your apps with 9 songs", { exact: true })).toBeVisible();
  expect(added).toEqual(["POST"]);
});

test("A release panel keeps duplicate track labels and shows an apps refusal", async ({ page }) => {
  await mockReleases(page);
  await page.route("**/api/admin/releases/aurora/tracks", route => route.fulfill({ json: {
    providerId: "qobuz",
    tracks: [
      { number: 1, title: "Intro", artists: "North", durationMs: 60_000, isExplicit: false },
      { number: 1, title: "Intro", artists: "North", durationMs: 70_000, isExplicit: false },
    ],
  } }));
  await page.route("**/api/admin/releases/aurora/apps", route => route.fulfill({
    status: 403,
    json: { error: "Adding mixes to apps is turned off for this household." },
  }));
  await page.goto("/#/intelligence?section=releases&release=aurora");
  const panel = page.getByRole("dialog", { name: "Aurora", exact: true });
  await expect(panel.getByRole("listitem")).toHaveCount(2);

  await panel.getByRole("button", { name: "Add to apps", exact: true }).click();

  await expect(page.getByText("Adding mixes to apps is turned off for this household.", { exact: true })).toBeVisible();
  await expect(panel.getByRole("button", { name: "Add to apps", exact: true })).toBeEnabled();
  await expect(page.getByText(/Aurora is in your apps/)).toHaveCount(0);
});
