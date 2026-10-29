import { expect, test } from "@playwright/test";
import { appearance, capture, drawCount, expectPainted, mockCovers, openHome, pixels, trackDrawing } from "./covers-fixtures";

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Drawn home covers at ${width}px ${theme}`, async ({ page }) => {
  const errors: string[] = []; page.on("pageerror", error => errors.push(error.message));
  await appearance(page, width, theme); await mockCovers(page); await openHome(page);
  const covers = page.locator(".mix-shelf canvas");
  await expect(covers).toHaveCount(19); await expectPainted(covers);
  await expect(page.getByRole("region", { name: "New releases", exact: true }).locator("canvas")).toHaveCount(0);
  for (const shelf of ["Made for you", "Moods", "Your mixes", "New releases", "From your services"]) {
    await expect(page.getByRole("region", { name: shelf, exact: true })).toBeVisible();
  }
  expect(await page.evaluate(async () => Promise.all(['700 40px "Archivo Narrow"', '400 40px "Courier Prime"', '700 40px "Courier Prime"'].map(font => document.fonts.load(font).then(faces => faces.length))))).toEqual([1, 1, 1]);
  await capture(page, "home", width, theme);
  expect(errors).toEqual([]);
});

test("Reduced motion keeps liquid and focused printed covers still", async ({ page }) => {
  await appearance(page, 1280, "light"); await trackDrawing(page); await mockCovers(page); await openHome(page);
  const liquid = page.getByRole("region", { name: "Made for you", exact: true }).locator("canvas").first();
  const printed = page.getByRole("region", { name: "Moods", exact: true }).locator("canvas").first();
  await expectPainted(page.locator(".mix-shelf canvas"));
  await printed.locator("..").locator("..").focus(); await printed.hover();
  const before = [await pixels(liquid), await pixels(printed)], counts = [await drawCount(liquid), await drawCount(printed)];
  await page.waitForTimeout(600);
  expect([await pixels(liquid), await pixels(printed)]).toEqual(before);
  expect([await drawCount(liquid), await drawCount(printed)]).toEqual(counts);
});

test("Visible water drifts within its frame cap and printed covers return to still", async ({ page }) => {
  await appearance(page, 1280, "light"); await page.emulateMedia({ reducedMotion: "no-preference" });
  await trackDrawing(page); await mockCovers(page);
  await page.addInitScript(() => {
    let contexts = 0;
    const original = HTMLCanvasElement.prototype.getContext;
    HTMLCanvasElement.prototype.getContext = function(this: HTMLCanvasElement, ...args: Parameters<typeof original>) {
      if (args[0] === "webgl") contexts++;
      return original.apply(this, args);
    } as typeof original;
    Object.assign(window, { waterContexts: () => contexts });
  });
  await openHome(page); await expectPainted(page.locator(".mix-shelf canvas"));
  const liquid = page.getByRole("region", { name: "Made for you", exact: true }).locator("canvas").first();
  const sample = await liquid.evaluate(async element => {
    const canvas = element as HTMLCanvasElement;
    const draws = (window as unknown as { coverDraws: (canvas: HTMLCanvasElement) => number }).coverDraws;
    const before = canvas.toDataURL(), count = draws(canvas), start = performance.now();
    await new Promise(resolve => setTimeout(resolve, 650));
    const elapsed = performance.now() - start, frames = draws(canvas) - count;
    return { before, after: canvas.toDataURL(), elapsed, frames };
  });
  expect(sample.after).not.toBe(sample.before);
  expect(sample.frames).toBeGreaterThan(0);
  expect(sample.frames).toBeLessThanOrEqual(Math.ceil(sample.elapsed * 30 / 1000) + 1);
  expect(await page.evaluate(() => (window as unknown as { waterContexts: () => number }).waterContexts())).toBe(1);
  const printed = page.getByRole("region", { name: "Moods", exact: true }).locator("canvas").first();
  await page.mouse.move(0, 0); const still = await pixels(printed);
  await printed.hover(); await expect.poll(() => pixels(printed)).not.toBe(still);
  await page.mouse.move(0, 0); await expect.poll(() => pixels(printed)).toBe(still);
  await printed.locator("..").locator("..").focus(); await expect.poll(() => pixels(printed)).not.toBe(still);
  await printed.evaluate(element => (element.closest("button") as HTMLButtonElement).blur());
  await expect.poll(() => pixels(printed)).toBe(still);
  await liquid.evaluate(element => element.closest(".mix-shelf")!.scrollTo({ left: 3000, behavior: "instant" }));
  await page.waitForTimeout(100); const offscreen = await drawCount(liquid);
  await page.waitForTimeout(250); expect(await drawCount(liquid)).toBe(offscreen);
});

test("Water falls back to painted colours when the graphics context is unavailable", async ({ page }) => {
  await appearance(page, 390, "dark");
  await page.addInitScript(() => {
    const original = HTMLCanvasElement.prototype.getContext;
    HTMLCanvasElement.prototype.getContext = function(this: HTMLCanvasElement, ...args: Parameters<typeof original>) {
      return args[0] === "webgl" ? null : original.apply(this, args);
    } as typeof original;
  });
  await mockCovers(page); await openHome(page); await expectPainted(page.locator(".mix-shelf canvas"));
});
