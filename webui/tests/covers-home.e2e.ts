import { expect, test } from "@playwright/test";
import { appearance, capture, drawCount, expectPainted, mockCovers, openHome, pixels, trackDrawing } from "./covers-fixtures";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Drawn home covers at ${width}px ${theme}`, async ({ page }) => {
  const errors: string[] = []; page.on("pageerror", error => errors.push(error.message));
  await page.addInitScript(() => {
    const sizes = new WeakMap<HTMLCanvasElement, number[]>(), original = CanvasRenderingContext2D.prototype.fillText;
    CanvasRenderingContext2D.prototype.fillText = function(...args: Parameters<typeof original>) {
      if (this.canvas.isConnected && this.font.includes("Courier Prime")) {
        const values = sizes.get(this.canvas) ?? [];
        values.push(Number(this.font.match(/(?:^|\s)([\d.]+)px(?:\s|$)/)?.[1]));
        sizes.set(this.canvas, values.slice(-500));
      }
      return original.apply(this, args);
    };
    Object.assign(window, { coverLabelSizes: (canvas: HTMLCanvasElement) => sizes.get(canvas) ?? [] });
  });
  await appearance(page, width, theme); await mockCovers(page); await openHome(page);
  const covers = page.locator(".mix-shelf canvas");
  await expect(covers).toHaveCount(19); await expectPainted(covers);
  const sizes = await covers.evaluateAll(elements => elements.flatMap(element => {
    const canvas = element as HTMLCanvasElement;
    return (window as unknown as { coverLabelSizes: (canvas: HTMLCanvasElement) => number[] }).coverLabelSizes(canvas)
      .map(size => size * canvas.getBoundingClientRect().width / canvas.width);
  }));
  expect(sizes.length).toBeGreaterThan(0);
  expect(sizes.every(Number.isFinite)).toBe(true);
  expect(Math.min(...sizes)).toBeGreaterThanOrEqual(12);
  await expect(page.getByRole("region", { name: "New releases", exact: true }).locator("canvas")).toHaveCount(0);
  for (const shelf of ["Made for you", "Moods", "Your mixes", "New releases", "From your services"]) {
    await expect(page.getByRole("region", { name: shelf, exact: true })).toBeVisible();
  }
  expect(await page.evaluate(async () => Promise.all(['700 40px "Archivo Narrow"', '400 40px "Courier Prime"', '700 40px "Courier Prime"'].map(font => document.fonts.load(font).then(faces => faces.length))))).toEqual([1, 1, 1]);
  for (const name of ["R&B Blend", "Indie Rock Blend", "Electronic Blend", "Hip-Hop Blend", "Uncharted", "Just Dropped", "Heavy Rotation", "Long Lost"]) {
    await expect(page.getByRole("region", { name: "Made for you", exact: true }).getByRole("button", { name: `Open ${name}`, exact: true })).toBeVisible();
  }
  const layout = await page.locator(".for-you").evaluate(element => {
    const shelves = [...element.querySelectorAll<HTMLElement>(":scope > section:has(.mix-shelf)")];
    const start = element.querySelector<HTMLElement>('section[aria-label="Start a mix"]')!;
    const moods = shelves[1], made = shelves[0];
    return {
      gaps: [moods.getBoundingClientRect().top - made.getBoundingClientRect().bottom, start.getBoundingClientRect().top - moods.getBoundingClientRect().bottom],
      cards: shelves.flatMap(shelf => [...shelf.querySelectorAll<HTMLElement>(".mix-card")].map(card => ({
        shelf: shelf.querySelector("h2")!.textContent,
        height: card.getBoundingClientRect().height,
        bottom: card.querySelector("small")!.getBoundingClientRect().bottom,
      }))),
    };
  });
  expect(layout.gaps[1]).toBeCloseTo(layout.gaps[0], 0);
  for (const shelf of new Set(layout.cards.map(card => card.shelf))) {
    const cards = layout.cards.filter(card => card.shelf === shelf);
    expect(new Set(cards.map(card => Math.round(card.height))).size).toBe(1);
    expect(new Set(cards.map(card => Math.round(card.bottom))).size).toBe(1);
  }
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
  await page.clock.install();
  await openHome(page); await expectPainted(page.locator(".mix-shelf canvas"));
  await page.clock.pauseAt(new Date(await page.evaluate(() => Date.now()) + 50));
  const liquid = page.getByRole("region", { name: "Made for you", exact: true }).locator("canvas").first();
  const before = await pixels(liquid), count = await drawCount(liquid);
  await page.clock.runFor(650);
  expect(await pixels(liquid)).not.toBe(before);
  expect(await drawCount(liquid) - count).toBeGreaterThan(0);
  expect(await drawCount(liquid) - count).toBeLessThanOrEqual(Math.ceil(650 * 30 / 1000) + 2);
  expect(await page.evaluate(() => (window as unknown as { waterContexts: () => number }).waterContexts())).toBe(1);
  const printed = page.getByRole("region", { name: "Moods", exact: true }).locator("canvas").first();
  await page.mouse.move(0, 0);
  await printed.evaluate(element => (element.closest("button") as HTMLButtonElement).blur());
  const still = await pixels(printed);
  const expectMoving = async () => {
    for (let step = 0; step < 20 && await pixels(printed) === still; step++) await page.clock.runFor(50);
    expect(await pixels(printed)).not.toBe(still);
  };
  await printed.hover(); await expectMoving();
  await page.mouse.move(0, 0); await expect.poll(() => pixels(printed)).toBe(still);
  await printed.locator("..").locator("..").focus(); await expectMoving();
  await printed.evaluate(element => (element.closest("button") as HTMLButtonElement).blur());
  await expect.poll(() => pixels(printed)).toBe(still);
  await liquid.evaluate(element => element.closest(".mix-shelf")!.scrollTo({ left: 3000, behavior: "instant" }));
  await expect(liquid).not.toBeInViewport();
  await expect.poll(async () => {
    const hiddenCount = await drawCount(liquid);
    await page.clock.runFor(300);
    return await drawCount(liquid) - hiddenCount;
  }).toBe(0);
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
