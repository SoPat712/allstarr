import { expect, test, type Locator } from "@playwright/test";
import { appearance, capture, mockForYou } from "./for-you-fixtures";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Release inbox at ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme); await mockForYou(page); await page.goto("/#/for-you?section=releases&release=release");
  await expect(page.getByRole("button", { name: "Following (1)" })).toBeVisible(); await expect(page.getByText("Just Dropped collects these every Friday.")).toBeVisible();
  await capture(page, "releases", width, theme); await page.getByRole("button", { name: "Following (1)" }).click(); await expect(page.getByRole("dialog")).toBeVisible();
});

test("Release covers show in the shelf and the release inbox, and a missing cover keeps the placeholder", async ({ page }) => {
  await mockForYou(page);
  const release = (id: string, title: string) => ({ id, title, artist: "North", releaseDate: "2026-10-29", datePrecision: "day", type: "single", sources: [], isExplicit: false, edition: "", exclusionReason: null, artworkUrl: `/api/admin/releases/artwork/deezer/${id}` });
  const requested: string[] = [];
  await page.route("**/api/admin/releases", route => route.fulfill({ json: { entries: [release("42", "Open skies"), release("missing", "Quiet hours")], excluded: [], coverage: [] } }));
  await page.route("**/api/admin/releases/artwork/**", route => {
    const path = new URL(route.request().url()).pathname;
    requested.push(path);
    return path.endsWith("/missing") ? route.fulfill({ status: 204 }) : route.fulfill({ contentType: "image/svg+xml", body: '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64"><rect width="64" height="64" fill="#3b6ea5"/></svg>' });
  });
  const loaded = (image: Locator) => expect.poll(() => image.evaluate(element => (element as HTMLImageElement).naturalWidth)).toBeGreaterThan(0);
  await page.goto("/#/for-you");
  const shelf = page.getByRole("region", { name: "New releases", exact: true });
  const cover = shelf.getByRole("group", { name: "Open skies", exact: true }).locator("img");
  await expect(cover).toHaveAttribute("src", /\/api\/admin\/releases\/artwork\/deezer\/42$/);
  await loaded(cover);
  const missing = shelf.getByRole("group", { name: "Quiet hours", exact: true });
  await expect(missing.locator("img")).toHaveCount(0);
  await expect(missing.locator(".media-art")).toHaveText("♪");
  await page.getByRole("link", { name: "See all", exact: true }).click();
  const card = page.getByRole("article", { name: "Open skies by North", exact: true }).locator("img");
  await expect(card).toHaveAttribute("src", /\/api\/admin\/releases\/artwork\/deezer\/42$/);
  await loaded(card);
  await expect(page.getByRole("article", { name: "Quiet hours by North", exact: true }).locator(".media-art")).toHaveText("♫");
  expect([...new Set(requested)].sort()).toEqual(["/api/admin/releases/artwork/deezer/42", "/api/admin/releases/artwork/deezer/missing"]);
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
