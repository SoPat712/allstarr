import { expect, test } from "@playwright/test";
import { appearance, mockForYou, openHome } from "./for-you-fixtures";
import { expectContainedMobileGeometry } from "./mobile-geometry";
import type { MixCard } from "../src/lib/api";

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Service feeds open and add at ${width}px ${theme}`, async ({ page }, testInfo) => {
    await appearance(page, width, theme);
    const state = await mockForYou(page);
    const feed = (id: string, name: string, source: string, label: string, inApps: boolean): MixCard => ({
      ...state.cards[0], id, name, kind: "external", section: "yourServices", source, sourcePlaylistId: id,
      sourceLabel: label, inApps, appsAllowed: true, description: `From ${label}`,
    });
    state.cards.push(feed("weekly", "Discover Weekly", "spotify", "Spotify", true), feed("favorites", "Favorites Mix", "apple-musickit", "Apple Music", false));
    await openHome(page);
    const shelf = page.getByRole("region", { name: "From your services", exact: true });
    const spotify = shelf.getByRole("group", { name: "Discover Weekly", exact: true });
    const apple = shelf.getByRole("group", { name: "Favorites Mix", exact: true });
    await expect(spotify).toHaveCount(1);
    await expect(spotify.getByText("✓ In your apps · kept in sync", { exact: true })).toBeVisible();
    await expect(apple.getByRole("button", { name: "Add Favorites Mix", exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: testInfo.outputPath(`services-${width}-${theme}.png`), fullPage: true });
    await apple.getByRole("button", { name: "Open Favorites Mix", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Favorites Mix", exact: true })).toBeVisible();
    await expect(page.getByText("Blue morning", { exact: true }).first()).toBeVisible();
    await openHome(page);
    await shelf.getByRole("group", { name: "Favorites Mix", exact: true }).getByRole("button", { name: "Add Favorites Mix", exact: true }).click();
    await expect(shelf.getByRole("group", { name: "Favorites Mix", exact: true }).getByText("✓ In your apps · kept in sync", { exact: true })).toBeVisible();
    expect(state.calls.filter(call => call.path === "/api/admin/mixes/favorites/in-apps" && call.method === "POST")).toHaveLength(1);
    expect(state.calls.filter(call => call.path === "/api/admin/mixes/external")).toHaveLength(0);
    expect(await page.pageErrors()).toEqual([]);
  });
}
