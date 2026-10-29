import { expect, test } from "@playwright/test";
import { appearance, capture, expectPainted, mockCovers, pixels } from "./covers-fixtures";

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Drawn daily mix cover at ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme); await mockCovers(page); await page.goto("/#/for-you?mix=daily-1");
  const cover = page.getByRole("dialog").locator("canvas");
  await expect(cover).toHaveCount(1); await expectPainted(cover);
  await expect(page.getByRole("dialog").getByRole("list", { name: "Songs", exact: true }).getByRole("listitem")).toHaveCount(8);
  const before = await pixels(cover); await page.waitForTimeout(400); expect(await pixels(cover)).toBe(before);
  await capture(page, "daily", width, theme);
});

test.describe("Dense displays", () => {
  test.use({ deviceScaleFactor: 3 });
  test("Not-ready header cover stays undimmed and resizes at capped pixel density", async ({ page }) => {
  await appearance(page, 1280, "light"); const state = await mockCovers(page);
  state.cards[0].readiness.ready = false;
  await page.goto("/#/for-you?mix=daily-1"); const cover = page.getByRole("dialog").locator("canvas");
  await expectPainted(cover); await expect(cover).toHaveCSS("opacity", "1");
  expect(await cover.evaluate(element => (element as HTMLCanvasElement).width)).toBe(320);
  await page.setViewportSize({ width: 390, height: 900 }); await expectPainted(cover);
  await expect.poll(() => cover.evaluate(element => (element as HTMLCanvasElement).width)).toBe(240);
  await expect(cover).toHaveCSS("opacity", "1");
});

});
