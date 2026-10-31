import { expect, test, type Page } from "@playwright/test";
import type { ListeningStats } from "../src/lib/api";
import { appearance, capture, mockForYou } from "./for-you-fixtures";

const stats: ListeningStats = {
  period: { from: "2026-09-29T04:00:00Z", to: "2026-10-29T04:00:00Z", timeZoneId: "America/New_York" },
  playCount: 1234, activeDays: 23, importedCount: 450, observedSince: "2023-02-01T12:00:00Z",
  estimatedTimeFromSongLengthsMilliseconds: 111000000,
  coverage: { unknownSongIdentityCount: 7, unknownDurationCount: 4 },
  topSongs: [{ title: "Evening light", artist: "The Coast", album: "Open water", playCount: 31 }],
  topArtists: [{ artist: "The Coast", playCount: 57 }],
  topAlbums: [{ album: "Open water", artist: "The Coast", playCount: 46 }],
};

async function mockStats(page: Page, value: ListeningStats = stats) {
  const calls: string[] = [];
  await mockForYou(page);
  await page.route("**/api/admin/stats?*", route => {
    calls.push(new URL(route.request().url()).search);
    return route.fulfill({ json: value });
  });
  return calls;
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Recap listening details at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    await mockStats(page);
    await page.goto("/#/for-you?section=recap");
    await expect(page.getByRole("heading", { name: "Your listening", exact: true })).toBeVisible();
    await expect(page.getByText("1,234", { exact: true })).toBeVisible();
    await expect(page.getByText("about 30 hr 50 min from song lengths", { exact: true })).toBeVisible();
    await expect(page.getByRole("region", { name: "Top songs", exact: true })).toContainText("Evening light");
    await expect(page.getByRole("region", { name: "Top artists", exact: true })).toContainText("57 plays");
    await expect(page.getByRole("region", { name: "Top albums", exact: true })).toContainText("Open water");
    await expect(page.getByText("Imported from other services: 450 plays", { exact: true })).toBeVisible();
    await expect(page.getByText(/7 plays couldn't be identified/)).toBeVisible();
    await expect(page.getByRole("button", { name: /Share/ })).toHaveCount(0);
    await capture(page, "recap-listening-details", width, theme);
    if (width === 390) {
      const layout = await page.evaluate(() => {
        const workspace = document.querySelector<HTMLElement>(".workspace")!;
        return { available: workspace.clientWidth - parseFloat(getComputedStyle(workspace).paddingLeft) - parseFloat(getComputedStyle(workspace).paddingRight), actual: document.querySelector(".your-stats")!.getBoundingClientRect().width };
      });
      expect(layout.actual).toBeLessThanOrEqual(layout.available + 1);
      const bounds = await page.locator(".stats-lists > section").evaluateAll(nodes => nodes.map(node => node.getBoundingClientRect().top));
      expect(bounds[0]).toBeLessThan(bounds[1]);
      expect(bounds[1]).toBeLessThan(bounds[2]);
    }
  });
}

test("Recap periods and past years send separate exact selections", async ({ page }) => {
  const calls = await mockStats(page);
  await page.goto("/#/for-you?section=recap");
  await expect(page.getByText("1,234", { exact: true })).toBeVisible();
  for (const [label, period] of [["Last 7 days", "7d"], ["This year", "year"], ["All time", "all"], ["Last 30 days", "30d"]]) {
    await page.getByRole("navigation", { name: "Stats period" }).getByRole("tab", { name: label, exact: true }).click();
    await expect.poll(() => calls.at(-1)).toBe(`?period=${period}`);
  }
  await page.getByRole("button", { name: "Year in review", exact: true }).click();
  await page.getByRole("option", { name: "2024", exact: true }).click();
  await expect.poll(() => calls.at(-1)).toBe("?year=2024");
  await page.getByRole("button", { name: "Year in review", exact: true }).focus();
  await page.keyboard.press("Shift+Tab");
  await expect(page.getByRole("navigation", { name: "Stats period" }).getByRole("tab", { name: "Last 7 days", exact: true })).toBeFocused();
  await page.getByRole("navigation", { name: "Stats period" }).getByRole("tab", { name: "Last 7 days", exact: true }).click();
  await expect.poll(() => calls.at(-1)).toBe("?period=7d");
});

test("Empty stats explain what is counted", async ({ page }) => {
  await mockStats(page, { ...stats, playCount: 0, importedCount: 0, activeDays: 0, estimatedTimeFromSongLengthsMilliseconds: 0, observedSince: null, coverage: { unknownSongIdentityCount: 0, unknownDurationCount: 0 }, topSongs: [], topArtists: [], topAlbums: [] });
  await page.goto("/#/for-you?section=recap");
  await expect(page.getByText("No plays in this period.", { exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Top songs", exact: true })).toContainText("No songs yet.");
  await expect(page.getByText(/Counting starts with your first listen\./)).toBeVisible();
  await expect(page.getByText("Imported from other services: 0 plays", { exact: true })).toBeVisible();
});

test("Completed imported-only plays populate their original year", async ({ page }) => {
  const historical = { ...stats, period: { ...stats.period, from: "2015-01-01T05:00:00Z", to: "2016-01-01T05:00:00Z" }, playCount: 450, importedCount: 450, observedSince: "2015-10-16T23:45:56Z" };
  const calls = await mockStats(page, historical);
  await page.goto("/#/for-you?section=recap");
  await page.getByRole("button", { name: "Year in review", exact: true }).click();
  await page.getByRole("option", { name: "2015", exact: true }).click();
  await expect.poll(() => calls.at(-1)).toBe("?year=2015");
  await expect(page.locator(".stats-numbers").getByText("450", { exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Top songs", exact: true })).toContainText("Evening light");
  await expect(page.getByRole("region", { name: "Top artists", exact: true })).toContainText("The Coast");
  await expect(page.getByRole("region", { name: "Top albums", exact: true })).toContainText("Open water");
  await expect(page.getByText("Imported from other services: 450 plays", { exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "2015 · Year in music", exact: true })).toBeVisible();
  await expect(page.getByText("No plays in this period.", { exact: true })).toHaveCount(0);
});

test("A failed period change does not label the previous totals as the new period", async ({ page }) => {
  await mockStats(page);
  await page.route("**/api/admin/stats?period=7d", route => route.fulfill({ status: 503, json: { error: "Unavailable" } }));
  await page.goto("/#/for-you?section=recap");
  await expect(page.getByText("1,234", { exact: true })).toBeVisible();
  await page.getByRole("navigation", { name: "Stats period" }).getByRole("tab", { name: "Last 7 days", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Your stats are unavailable." })).toBeVisible();
  await expect(page.getByText("1,234", { exact: true })).toHaveCount(0);
});

test("Missing song lengths do not become a zero listening-time claim", async ({ page }) => {
  await mockStats(page, { ...stats, estimatedTimeFromSongLengthsMilliseconds: 0, coverage: { unknownSongIdentityCount: 7, unknownDurationCount: 1234 } });
  await page.goto("/#/for-you?section=recap");
  await expect(page.getByText("Song lengths aren't available for these plays.", { exact: true })).toBeVisible();
  await expect(page.getByText("about 0 min from song lengths", { exact: true })).toHaveCount(0);
});
