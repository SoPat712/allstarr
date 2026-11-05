import { expect, type Locator, type Page } from "@playwright/test";
import { mockForYou } from "./for-you-fixtures";

export { appearance, capture, openHome } from "./for-you-fixtures";
export async function mockCovers(page: Page) {
  const state = await mockForYou(page, { custom: true, partial: true });
  state.cards.push({ ...state.cards[0], id: "similar", kind: "external", name: "Similar to your top tracks", section: "yourServices", sourceLabel: "Last.fm", description: "From Last.fm · kept in sync", inApps: true });
  await page.route("**/api/admin/playlist-links", route => route.fulfill({ json: { playlistLinks: [{ id: "similar", sourceProviderId: "lastfm", sourcePlaylistId: "similar", enabled: true }] } }));
  const tags = ["R&B", "Indie Rock", "Electronic", "Hip-Hop"];
  state.cards.slice(0, 4).forEach((card, i) => { card.name = `${tags[i]} Blend`; card.description = `Made for you · ${tags[i].toLowerCase()} · North, Cedar, Willow`; });
  state.cards.find(card => card.kind === "custom")!.name = "Evening Mix";
  await page.route("**/api/admin/playlist-sources", route => route.fulfill({ json: { accounts: [
    { id: "last", providerId: "lastfm", displayName: "Last.fm", scope: "personal" },
    { id: "listen", providerId: "listenbrainz", displayName: "ListenBrainz", scope: "personal" },
  ], blockedAccounts: [], providers: [] } }));
  return state;
}
export async function expectCovered(covers: Locator) {
  await expect(covers).not.toHaveCount(0);
  await expect.poll(() => covers.evaluateAll(elements => elements.flatMap(element => {
    const box = element.getBoundingClientRect();
    const shelf = element.closest(".mix-shelf")?.getBoundingClientRect();
    const images = [...element.querySelectorAll("img")];
    const loaded = images.every(image => {
      const rect = image.getBoundingClientRect();
      const visible = rect.bottom > 0 && rect.top < innerHeight && rect.right > 0 && rect.left < innerWidth &&
        (!shelf || rect.left < shelf.right && rect.right > shelf.left && rect.top < shelf.bottom && rect.bottom > shelf.top);
      return !!image.getAttribute("src") && (!visible || image.complete && image.naturalWidth > 0);
    });
    const valid = box.width > 0 && Math.abs(box.width - box.height) <= 1 && element.scrollWidth <= box.width + 1 &&
      (images.length ? loaded : getComputedStyle(element).backgroundImage.startsWith("linear-gradient("));
    return valid ? [] : [{ width: box.width, height: box.height, scrollWidth: element.scrollWidth, loaded }];
  }))).toEqual([]);
}
export async function coverState(cover: Locator) {
  return cover.evaluate(element => ({ background: getComputedStyle(element).backgroundImage,
    images: [...element.querySelectorAll("img")].map(image => image.getAttribute("src")) }));
}
