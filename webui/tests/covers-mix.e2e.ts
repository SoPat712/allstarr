import { expect, test } from "@playwright/test";
import { appearance, capture, expectPainted, mockCovers, pixels } from "./covers-fixtures";
import { expectContainedMobileGeometry } from "./mobile-geometry";

for (const width of [320, 390, 1280]) for (const theme of ["light", "dark"] as const) test(`Drawn Blend cover at ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme); const state = await mockCovers(page); state.cards[0].inApps = false; await page.goto("/#/for-you?mix=daily-1");
  const cover = page.getByRole("dialog").locator("canvas");
  await expect(cover).toHaveCount(1); await expectPainted(cover);
  await expect(page.getByRole("dialog").getByRole("list", { name: "Songs", exact: true }).getByRole("listitem")).toHaveCount(8);
  const expectAligned = async () => {
    const dialog = page.getByRole("dialog");
    const boxes = await Promise.all([dialog.locator(".app-state"), dialog.getByRole("button", { name: "Refresh mix", exact: true }), dialog.getByRole("button", { name: "Mix actions", exact: true })].map(control => control.boundingBox()));
    const centres = boxes.map(box => box!.y + box!.height / 2);
    expect(Math.max(...centres) - Math.min(...centres)).toBeLessThanOrEqual(1);
  };
  await expectAligned();
  if (width <= 390) await expectContainedMobileGeometry(page);
  const before = await pixels(cover); await page.waitForTimeout(400); expect(await pixels(cover)).toBe(before);
  const firstSong = page.getByRole("dialog").getByRole("list", { name: "Songs", exact: true }).getByRole("listitem").first();
  const songTop = (await firstSong.boundingBox())!.y;
  await capture(page, "blend-before-add", width, theme);
  await page.getByRole("dialog").getByRole("button", { name: "Add to my apps", exact: true }).click();
  await expect(page.getByRole("dialog").getByText("In your apps", { exact: true })).toBeVisible();
  expect(await pixels(cover)).toBe(before);
  await expectAligned();
  if (width <= 390) await expectContainedMobileGeometry(page);
  await expect.poll(async () => Math.abs((await firstSong.boundingBox())!.y - songTop)).toBeLessThanOrEqual(1);
  await capture(page, "blend-after-add", width, theme);
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
