import { expect, test } from "@playwright/test";
import { appearance, capture, mockForYou } from "./for-you-fixtures";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Release inbox at ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme); await mockForYou(page); await page.goto("/#/for-you?section=releases&release=release");
  await expect(page.getByRole("button", { name: "Following (1)" })).toBeVisible(); await expect(page.getByText("Just Dropped collects these every Friday.")).toBeVisible();
  await capture(page, "releases", width, theme); await page.getByRole("button", { name: "Following (1)" }).click(); await expect(page.getByRole("dialog")).toBeVisible();
});

test("Repeated release entries render in shelves and the release inbox", async ({ page }) => {
  await mockForYou(page);
  const entry = { id: "release", title: "Open skies", artist: "North", releaseDate: "2026-10-29", type: "album", sources: [], isExplicit: false, edition: "" };
  await page.route("**/api/admin/releases", route => route.fulfill({ json: { entries: [entry, entry], excluded: [], coverage: [] } }));
  await page.goto("/#/for-you");
  await expect(page.getByRole("region", { name: "New releases", exact: true }).locator(".mix-card")).toHaveCount(2);
  await page.getByRole("link", { name: "See all", exact: true }).click();
  await expect(page.locator(".release-card")).toHaveCount(2);
});
