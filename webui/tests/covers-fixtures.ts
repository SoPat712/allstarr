import { expect, type Locator, type Page } from "@playwright/test";
import { mockForYou } from "./for-you-fixtures";

export { appearance, capture, openHome } from "./for-you-fixtures";
export async function mockCovers(page: Page) {
  const state = await mockForYou(page, { custom: true });
  const tags = ["R&B", "Indie Rock", "Electronic", "Hip-Hop"];
  state.cards.slice(0, 4).forEach((card, i) => { card.name = `${tags[i]} Blend`; card.description = `Made for you · ${tags[i].toLowerCase()} · North, Cedar, Willow`; });
  state.cards.find(card => card.kind === "custom")!.name = "Evening Mix";
  await page.route("**/fonts/*.woff2", route => route.continue());
  await page.route("**/api/admin/playlist-sources", route => route.fulfill({ json: { accounts: [
    { id: "last", providerId: "lastfm", displayName: "Last.fm", scope: "personal" },
    { id: "listen", providerId: "listenbrainz", displayName: "ListenBrainz", scope: "personal" },
  ], blockedAccounts: [], providers: [] } }));
  return state;
}
export async function expectPainted(covers: Locator) {
  await expect.poll(() => covers.evaluateAll(canvases => canvases.every(element => {
    const canvas = element as HTMLCanvasElement;
    if (!canvas.width || canvas.width !== canvas.height) return false;
    const pixels = canvas.getContext("2d")!.getImageData(0, 0, canvas.width, canvas.height).data;
    const colours = new Set<number>();
    for (let i = 0; i < pixels.length; i += 4 * 37) {
      if (pixels[i + 3]) colours.add((pixels[i] << 16) | (pixels[i + 1] << 8) | pixels[i + 2]);
    }
    return colours.size > 20;
  }))).toBe(true);
}
export async function pixels(cover: Locator) {
  return cover.evaluate(element => (element as HTMLCanvasElement).toDataURL());
}
export async function trackDrawing(page: Page) {
  await page.addInitScript(() => {
    const draws = new WeakMap<HTMLCanvasElement, number>();
    const original = CanvasRenderingContext2D.prototype.clearRect;
    CanvasRenderingContext2D.prototype.clearRect = function(...args) {
      draws.set(this.canvas, (draws.get(this.canvas) ?? 0) + 1);
      return original.apply(this, args);
    };
    Object.assign(window, { coverDraws: (canvas: HTMLCanvasElement) => draws.get(canvas) ?? 0 });
  });
}
export const drawCount = (cover: Locator) => cover.evaluate(element => (window as unknown as { coverDraws: (canvas: HTMLCanvasElement) => number }).coverDraws(element as HTMLCanvasElement));
