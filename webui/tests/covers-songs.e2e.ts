import { expect, test } from "@playwright/test";
import { appearance, mockForYou, openHome } from "./for-you-fixtures";
import { expectCovered } from "./covers-fixtures";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const collage = (service: boolean) => `<svg xmlns="http://www.w3.org/2000/svg" width="600" height="600" viewBox="0 0 600 600">
  <g><rect width="300" height="300" fill="#9a91fa"/><circle cx="150" cy="130" r="90" fill="#572a1a"/><text x="35" y="265" fill="white" font-size="30">${service ? "Open skies" : "Northern lights"}</text></g>
  <g transform="translate(300 0)"><rect width="300" height="300" fill="#3db1db"/><path d="M0 220L150 40L300 220Z" fill="#9e2392"/><text x="35" y="265" fill="white" font-size="30">${service ? "Quiet water" : "Higher ground"}</text></g>
  <g transform="translate(0 300)"><rect width="300" height="300" fill="#9e2392"/><circle cx="150" cy="130" r="90" fill="#3db1db"/><text x="35" y="265" fill="white" font-size="30">${service ? "Afterglow" : "Momentum"}</text></g>
  <g transform="translate(300 300)"><rect width="300" height="300" fill="#572a1a"/><path d="M0 220L150 40L300 220Z" fill="#9a91fa"/><text x="35" y="265" fill="white" font-size="30">${service ? "Blue morning" : "Second wind"}</text></g>
</svg>`;

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Song collages on mood and discovery cards at ${width}px ${theme}`, async ({ page }, testInfo) => {
  await appearance(page, width, theme);
  const state = await mockForYou(page);
  for (const kind of ["workout", "discover-weekly"]) {
    state.cards.find(card => card.kind === kind)!.coverArtwork = [`/api/admin/mixes/${kind}/cover?v=with-song-art`];
    await page.route(`**/api/admin/mixes/${kind}/cover?*`, route => route.fulfill({ contentType: "image/svg+xml", body: collage(kind === "discover-weekly") }));
  }
  const empty = state.cards.find(card => card.kind === "release-radar")!;
  empty.coverArtwork = []; empty.readiness = { ready: false, message: "Needs more listening" };
  await openHome(page);
  for (const [kind, name] of [["workout", "Workout"], ["discover-weekly", "Uncharted"]]) {
    const card = page.getByRole("group", { name, exact: true });
    await card.scrollIntoViewIfNeeded();
    const cover = card.locator(".mix-artwork");
    await expectCovered(cover);
    await expect(cover.locator("img")).toHaveCount(1);
    await expect(cover.locator("img")).toHaveAttribute("src", `/api/admin/mixes/${kind}/cover?v=with-song-art`);
    await expect(cover.locator("img")).toHaveJSProperty("naturalWidth", 600);
    await page.screenshot({ path: testInfo.outputPath(`${kind}-${width}-${theme}.png`) });
  }
  const emptyCover = page.getByRole("group", { name: "Just Dropped", exact: true }).locator(".mix-artwork");
  await expect(emptyCover.locator("img")).toHaveCount(0);
  await expect(emptyCover).toHaveCSS("background-image", /linear-gradient/);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  if (width === 390) await expectContainedMobileGeometry(page);
  expect(await page.pageErrors()).toEqual([]);
});
