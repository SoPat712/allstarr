import { expect, test } from "@playwright/test";
import { appearance, capture, coverState, expectCovered, mockCovers } from "./covers-fixtures";
import { expectContainedMobileGeometry } from "./mobile-geometry";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

for (const width of [320, 390, 1280]) for (const theme of ["light", "dark"] as const) test(`Blend artwork cover at ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme); const state = await mockCovers(page); state.cards[0].inApps = false; await page.goto("/#/for-you?mix=daily-1");
  const cover = page.getByRole("dialog").locator(".mix-artwork");
  await expect(cover).toHaveCount(1); await expectCovered(cover);
  await expect(page.getByRole("dialog").getByRole("list", { name: "Songs", exact: true }).getByRole("listitem")).toHaveCount(8);
  const expectAligned = async () => {
    const dialog = page.getByRole("dialog");
    const boxes = await Promise.all([dialog.getByRole("button", { name: "Add to apps", exact: true }), dialog.getByRole("button", { name: "Refresh mix", exact: true })].map(control => control.boundingBox()));
    const centres = boxes.map(box => box!.y + box!.height / 2);
    expect(Math.max(...centres) - Math.min(...centres)).toBeLessThanOrEqual(1);
  };
  await expectAligned();
  if (width <= 390) await expectContainedMobileGeometry(page);
  const before = await coverState(cover); await page.waitForTimeout(400); expect(await coverState(cover)).toEqual(before);
  const firstSong = page.getByRole("dialog").getByRole("list", { name: "Songs", exact: true }).getByRole("listitem").first();
  const songTop = (await firstSong.boundingBox())!.y;
  await capture(page, "blend-before-add", width, theme);
  await page.getByRole("dialog").getByRole("button", { name: "Add to apps", exact: true }).click();
  await expect(page.getByRole("dialog").getByText("✓ In your apps", { exact: true })).toBeVisible();
  expect(await coverState(cover)).toEqual(before);
  if (width <= 390) await expectContainedMobileGeometry(page);
  else await expect.poll(async () => Math.abs((await firstSong.boundingBox())!.y - songTop)).toBeLessThanOrEqual(1);
  await capture(page, "blend-after-add", width, theme);
});

test.describe("Dense displays", () => {
  test.use({ deviceScaleFactor: 3 });
  test("Not-ready header cover stays undimmed and contained when resized", async ({ page }) => {
    await appearance(page, 1280, "light"); const state = await mockCovers(page);
    state.cards[0].readiness.ready = false;
    await page.goto("/#/for-you?mix=daily-1"); const cover = page.getByRole("dialog").locator(".mix-artwork");
    await expectCovered(cover); await expect(cover).toHaveCSS("opacity", "1");
    await expect(cover.locator("img")).toHaveCount(4);
    await page.setViewportSize({ width: 390, height: 900 }); await expectCovered(cover);
    await expect(cover).toHaveCSS("opacity", "1");
    await expectContainedMobileGeometry(page);
  });
});

test("Empty mix details show a palette gradient", async ({ page }) => {
  await appearance(page, 390, "dark"); const state = await mockCovers(page);
  state.songs = []; state.cards[0].coverArtwork = [];
  await page.goto("/#/for-you?mix=daily-1");
  const cover = page.getByRole("dialog").locator(".mix-artwork");
  await expectCovered(cover);
  await expect(cover.locator("img")).toHaveCount(0);
  await expect(cover).toHaveCSS("background-image", /linear-gradient/);
  await expectContainedMobileGeometry(page);
});
