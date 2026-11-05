import { expect, test } from "@playwright/test";
import { appearance, capture, coverState, expectCovered, mockCovers, openHome } from "./covers-fixtures";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Home artwork and gradient covers at ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme); await mockCovers(page); await openHome(page);
  const covers = page.locator(".mix-shelf .mix-artwork");
  await expect(covers).toHaveCount(20); await expectCovered(covers);
  const made = page.getByRole("region", { name: "Made for you", exact: true });
  await expect(made.locator(".mix-artwork").first().locator("img")).toHaveCount(4);
  const empty = made.getByRole("group", { name: "Hip-Hop Blend", exact: true }).locator(".mix-artwork");
  await expect(empty.locator("img")).toHaveCount(0);
  await expect(empty).toHaveCSS("background-image", /linear-gradient/);
  for (const shelf of ["Made for you", "Moods", "Your mixes", "New releases", "From your services"]) {
    await expect(page.getByRole("region", { name: shelf, exact: true })).toBeVisible();
  }
  for (const name of ["R&B Blend", "Indie Rock Blend", "Electronic Blend", "Hip-Hop Blend", "Uncharted", "Just Dropped", "Heavy Rotation", "Long Lost"]) {
    await expect(page.getByRole("region", { name: "Made for you", exact: true }).getByRole("button", { name: `Open ${name}`, exact: true })).toBeVisible();
  }
  const layout = await page.locator(".for-you").evaluate(element => {
    const shelves = [...element.querySelectorAll<HTMLElement>(":scope > section:has(.mix-shelf)")];
    const moods = shelves[1], made = shelves[0];
    return {
      gaps: [moods.getBoundingClientRect().top - made.getBoundingClientRect().bottom],
      cards: shelves.flatMap(shelf => [...shelf.querySelectorAll<HTMLElement>(".mix-card")].map(card => ({
        shelf: shelf.querySelector("h2")!.textContent,
        height: card.getBoundingClientRect().height,
        bottom: card.querySelector(".add-chip, small, .mix-open:last-child")!.getBoundingClientRect().bottom,
      }))),
    };
  });
  expect(layout.gaps[0]).toBeGreaterThan(0);
  for (const shelf of new Set(layout.cards.map(card => card.shelf))) {
    const cards = layout.cards.filter(card => card.shelf === shelf);
    expect(new Set(cards.map(card => Math.round(card.height))).size).toBe(1);
    expect(new Set(cards.map(card => Math.round(card.bottom))).size).toBe(1);
  }
  await capture(page, "home", width, theme);
});

for (const reducedMotion of ["reduce", "no-preference"] as const) test(`Covers stay still on hover and focus with ${reducedMotion} motion`, async ({ page }) => {
  await appearance(page, 1280, "light"); await page.emulateMedia({ reducedMotion });
  await mockCovers(page); await openHome(page);
  const cover = page.getByRole("region", { name: "Made for you", exact: true }).locator(".mix-artwork").first();
  await expectCovered(cover);
  const before = await coverState(cover);
  await cover.hover(); await cover.locator("..").locator("..").focus();
  await expect.poll(() => coverState(cover)).toEqual(before);
  await expect(page.locator(".mix-shelf canvas")).toHaveCount(0);
});

test("Empty covers render without a graphics context", async ({ page }) => {
  await appearance(page, 390, "dark");
  await page.addInitScript(() => { HTMLCanvasElement.prototype.getContext = () => null; });
  const state = await mockCovers(page);
  state.cards.forEach(card => { card.coverArtwork = []; });
  await openHome(page);
  await expectCovered(page.locator(".mix-shelf .mix-artwork"));
  await expect(page.locator(".mix-shelf canvas")).toHaveCount(0);
});
