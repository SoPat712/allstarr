import { expect, test } from "@playwright/test";
import { mockForYou } from "./for-you-fixtures";

for (const width of [1280, 390]) for (const theme of ["dark", "light"] as const) {
  test(`Mix publication health shares playlist badges and filters (${width} ${theme})`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => {
      localStorage.setItem("allstarr.theme", value);
      localStorage.setItem("allstarr.library.playlists.filter", "all");
    }, theme);
    const state = await mockForYou(page);
    state.cards = state.cards.filter(item => ["on-repeat", "daily-1", "workout"].includes(item.id));
    for (const item of state.cards) {
      item.inApps = true;
      item.songCount = 8;
      item.publication = {
        status: item.id === "on-repeat" ? "healthy" : item.id === "daily-1" ? "updating" : "needsLook",
        lastPublishedAt: "2026-11-09T05:00:00Z",
        reason: item.id === "workout" ? "Served by Allstarr. This mix is past its refresh time. Try refreshing it again." : null,
      };
    }
    await page.route("**/api/admin/playlist-links", route => route.fulfill({ json: { playlistLinks: [{
      id: "stored", name: "Discover Weekly", enabled: true, inApps: true, sourceProviderId: "spotify",
      sourcePlaylistId: "weekly", targetProtocol: "jellyfin", targetPlaylistId: "saved-weekly",
      mode: "materialized", importMode: "linked", playlistKind: "feed", countsTowardLibrary: false,
      trackCount: 8, unmatchedCount: 0, routeCoverage: [], metrics: { total: 8, review: 0, rejected: 0 },
    }] } }));
    await page.goto("/#/library/playlists");
    const rows = page.locator(".playlist-row");
    await expect(rows).toHaveCount(4);
    await expect(rows.filter({ hasText: "Heavy Rotation" }).locator(".state-pill")).toHaveText("Healthy");
    await expect(rows.filter({ hasText: "Blend 1" }).locator(".state-pill")).toHaveText("Updating");
    await expect(rows.filter({ hasText: "Workout" }).locator(".state-pill")).toHaveText("Needs a look");
    await expect(rows.filter({ hasText: "Discover Weekly" }).locator(".state-pill")).toHaveText("Healthy");
    await expect(page.getByRole("radio", { name: "Healthy 2", exact: true })).toBeVisible();
    await expect(page.getByRole("radio", { name: "Needs a look 1", exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`mix-health-${width}-${theme}.png`), fullPage: true });

    await page.getByRole("radio", { name: "Healthy 2", exact: true }).click();
    await expect(rows).toHaveCount(2);
    await expect(rows.filter({ hasText: "Heavy Rotation" })).toBeVisible();
    await expect(rows.filter({ hasText: "Discover Weekly" })).toBeVisible();
    await page.getByRole("radio", { name: "Needs a look 1", exact: true }).click();
    await expect(rows).toHaveCount(1);
    await expect(rows).toContainText("Workout");
    await page.getByRole("link", { name: "Open Workout mix", exact: true }).click();
    const panel = page.getByRole("dialog");
    await expect(panel.locator(".state-pill")).toHaveText("Needs a look");
    await expect(panel.getByText(state.cards.find(item => item.id === "workout")!.publication!.reason!, { exact: true })).toBeVisible();
    await expect(panel.getByText(/^Published /)).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`mix-health-detail-${width}-${theme}.png`), fullPage: true });
    expect(await page.pageErrors()).toEqual([]);
  });
}
