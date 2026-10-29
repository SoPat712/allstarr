import { expect, test } from "@playwright/test";
import { appearance, brainzId, capture, mockIntelligence, playlistId, sourcePlaylist } from "./intelligence-fixtures";

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`For you at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    await mockIntelligence(page);
    await page.goto("/#/intelligence");
    await expect(page.getByRole("heading", { name: "Intelligence", exact: true })).toBeVisible();
    await expect(page.getByRole("navigation", { name: "Intelligence sections" }).getByRole("tab", { name: "For you", exact: true })).toHaveAttribute("aria-selected", "true");
    await expect(page.getByRole("article", { name: "Weekly Jams" })).toBeVisible();
    await expect(page.getByRole("article", { name: "Weekly Jams" })).not.toContainText("Song count");
    await expect(page.getByRole("article", { name: "Similar to your top tracks" })).toContainText("Suggestions from Last.fm");
    await capture(page, "for-you", width, theme);
    if (width === 390) await expect(page.locator("nav.mobile-navigation").getByRole("link", { name: "Insights" })).toBeVisible();
    else await expect(page.locator("nav.desktop-navigation").getByRole("link", { name: "Intelligence" })).toBeVisible();
    const targets = await page.locator(".for-you button, .for-you a").evaluateAll(nodes => nodes.filter(node => node.getBoundingClientRect().height < 44).map(node => node.textContent));
    expect(targets).toEqual([]);
  });
}

test("For you explains disconnected and not-ready sources", async ({ page }) => {
  await mockIntelligence(page, { disconnected: true });
  await page.goto("/#/intelligence?section=for-you");
  await expect(page.getByText("Connect ListenBrainz to see playlists made from your listening")).toBeVisible();
  await expect(page.getByRole("link", { name: "Connect ListenBrainz", exact: true })).toHaveAttribute("href", "#/integrations/accounts?source=listenbrainz&connect=1");
  await expect(page.getByRole("link", { name: "Connect Last.fm", exact: true })).toBeVisible();
  await page.unrouteAll({ behavior: "wait" });
  await mockIntelligence(page, { notReady: true });
  await page.reload();
  await expect(page.getByText("ListenBrainz hasn't made playlists for you yet. They appear after about a week of listening.")).toBeVisible();
});

test("For you explains a missing Last.fm API key", async ({ page }) => {
  await mockIntelligence(page, { keyRequired: true });
  await page.goto("/#/intelligence");
  await expect(page.getByText("Needs Last.fm API key.", { exact: true })).toBeVisible();
  await expect(page.getByRole("article", { name: "Weekly Jams" })).toBeVisible();
});

test("Adding a playlist updates its card and opens the saved playlist", async ({ page }) => {
  const calls = await mockIntelligence(page);
  await page.goto("/#/intelligence");
  const card = page.getByRole("article", { name: "Weekly Jams" });
  await card.getByRole("button", { name: "Add to my playlists" }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("heading", { name: "Import a playlist" })).toBeVisible();
  await expect(dialog.getByRole("checkbox", { name: /Follow newest edition/ })).toBeChecked();
  await dialog.getByRole("checkbox", { name: /Follow newest edition/ }).uncheck();
  await dialog.getByRole("radio", { name: /Import once/ }).check();
  await dialog.getByRole("button", { name: "Continue", exact: true }).click();
  await dialog.getByRole("button", { name: "Continue", exact: true }).click();
  await dialog.getByRole("button", { name: "Import playlist", exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(card).toContainText("Added ✓");
  await expect(card.getByRole("link", { name: "Open", exact: true })).toHaveAttribute("href", "#/library/playlists?playlist=saved-playlist");
  expect(calls.find(call => call.path === "/api/admin/playlist-links" && call.method === "POST")?.body).toMatchObject({ sourceProviderId: "listenbrainz", sourcePlaylistId: playlistId, importMode: "oneTime" });
});

test("Created-for-you playlists follow the newest edition by default", async ({ page }) => {
  const calls = await mockIntelligence(page);
  await page.goto("/#/intelligence");
  const card = page.getByRole("article", { name: "Weekly Jams" });
  await card.getByRole("button", { name: "Add to my playlists" }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("checkbox", { name: /Follow newest edition/ })).toBeChecked();
  await expect(dialog.getByRole("radio", { name: /Import once/ })).toBeDisabled();
  await dialog.getByRole("button", { name: "Continue", exact: true }).click();
  await dialog.getByRole("button", { name: "Continue", exact: true }).click();
  await dialog.getByRole("button", { name: "Import and link", exact: true }).click();
  await expect(card).toContainText("Added ✓");
  expect(calls.find(call => call.path === "/api/admin/playlist-links" && call.method === "POST")?.body).toMatchObject({ importMode: "linked", followNewest: true });
  expect(calls.find(call => call.path === "/api/admin/playlist-links" && call.method === "POST")?.body).not.toHaveProperty("followNewestKind");
  expect(calls.some(call => call.path === "/api/admin/playlist-links/saved-playlist/schedules" && call.method === "POST")).toBe(true);
});

test("Top tracks period selects the source for the next import", async ({ page }) => {
  const calls = await mockIntelligence(page);
  await page.goto("/#/intelligence");
  await page.getByRole("button", { name: "Top tracks period" }).click();
  await page.getByRole("option", { name: "Last year", exact: true }).click();
  await page.getByRole("article", { name: "Your top tracks", exact: true }).getByRole("button", { name: "Add to my playlists" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByRole("button", { name: "Continue", exact: true }).click();
  await dialog.getByRole("button", { name: "Continue", exact: true }).click();
  await dialog.getByRole("button", { name: "Import playlist", exact: true }).click();
  await expect(page.getByRole("article", { name: "Your top tracks", exact: true })).toContainText("Added ✓");
  expect(calls.find(call => call.path === "/api/admin/playlist-links" && call.method === "POST")?.body?.sourcePlaylistId).toBe("top:12month:50");
});

test("Live refresh preserves loaded ListenBrainz pages", async ({ page }) => {
  await mockIntelligence(page);
  let cursorReads = 0;
  await page.route(`**/api/admin/playlist-sources/${brainzId}/playlists?*`, route => {
    const cursor = new URL(route.request().url()).searchParams.get("cursor");
    if (cursor) cursorReads++;
    return route.fulfill({ json: { items: cursor ? [{ ...sourcePlaylist, id: "own-playlist", name: "Long drive", description: "Creator: Listener", trackCount: 12 }] : [sourcePlaylist], nextCursor: cursor ? null : "next", isPartial: !cursor } });
  });
  await page.goto("/#/intelligence");
  await page.getByRole("button", { name: "More playlists", exact: true }).click();
  await expect(page.getByRole("article", { name: "Long drive", exact: true })).toBeVisible();
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect.poll(() => cursorReads).toBeGreaterThan(1);
  await expect(page.getByRole("article", { name: "Long drive", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "More playlists", exact: true })).toHaveCount(0);
});
