import { expect, test, type Locator } from "@playwright/test";
import { appearance, mockForYou, openHome } from "./for-you-fixtures";
import { expectContainedMobileGeometry } from "./mobile-geometry";
import type { MixCard } from "../src/lib/api";

async function expectAlignedActions(card: Locator) {
  await expect.poll(() => card.evaluate(card => {
    const row = [...card.parentElement!.children].filter(other => Math.abs(other.getBoundingClientRect().top - card.getBoundingClientRect().top) < 1);
    const actions = row.map(other => other.querySelector<HTMLElement>(".add-chip, small"))
      .filter(action => action !== null).map(action => action.getBoundingClientRect().bottom);
    return Math.max(...actions) - Math.min(...actions);
  }), { message: "service card actions align even when one has a saved-from line" }).toBeLessThanOrEqual(1);
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Service feeds open and add at ${width}px ${theme}`, async ({ page }, testInfo) => {
    await appearance(page, width, theme);
    const state = await mockForYou(page);
    const favoriteName = "Favorites Mix for a quiet evening";
    const owner = "Riju Patra and the extended family listening group with friends and neighbours";
    const feed = (id: string, name: string, source: string, label: string, inApps: boolean): MixCard => ({
      ...state.cards[0], id, name, kind: "external", section: "yourServices", source, sourcePlaylistId: id,
      coverArtwork: source === "spotify" || source === "apple-musickit" ? [`/api/admin/mixes/${id}/cover?v=fixture`] : [], songCount: source === "spotify" ? 30 : null,
      sourceLabel: label, inApps, appsAllowed: true, description: `From ${label}`,
      publication: { status: "healthy", lastPublishedAt: "2026-11-09T12:00:00Z", reason: null },
    });
    await page.route("**/api/admin/mixes/*/cover?*", route => route.fulfill({ contentType: "image/svg+xml", body: '<svg xmlns="http://www.w3.org/2000/svg" width="184" height="184"><rect width="184" height="184" fill="#16805d"/><text x="12" y="100" fill="white" font-size="22">Service cover</text></svg>' }));
    state.cards.push(feed("weekly", "Discover Weekly", "spotify", "Spotify", true), feed("favorites", favoriteName, "apple-musickit", "Apple Music", false));
    state.cards.find(card => card.id === "favorites")!.savedFrom = "Riju Patra";
    for (let index = 0; index < 44; index++) {
      const sources = ["spotify", "apple-musickit", "lastfm", "listenbrainz"];
      const labels = ["Spotify", "Apple Music", "Last.fm", "ListenBrainz"];
      const extra = feed(`extra-${index}`, `Service mix ${String(index).padStart(2, "0")}`, sources[index % 4], labels[index % 4], index === 3);
      if (index === 12) extra.evidence = { fromMusic: 12 } as MixCard["evidence"];
      if (index === 4) extra.savedFrom = "Riju Patra";
      if (index === 1) extra.savedFrom = owner;
      if (index === 8) extra.readiness = { ready: false, message: "Preparing songs from your service…" };
      if (index === 2) extra.sourcePlaylistId = "loved";
      if (index === 6) extra.sourcePlaylistId = "top:1month:50";
      if (index === 10) extra.sourcePlaylistId = "similar";
      if (index === 0) { extra.songCount = 0; extra.readiness = { ready: false, message: "No playable songs right now." }; }
      if (index === 2) extra.coverArtwork = state.cards[0].coverArtwork;
      state.cards.push(extra);
    }
    await page.route("**/api/admin/mixes/extra-8", route => route.fulfill({ json: { mix: state.cards.find(card => card.id === "extra-8"), songs: [] } }));
    await openHome(page);
    const shelf = page.getByRole("region", { name: "From your services", exact: true });
    const spotify = shelf.getByRole("group", { name: "Discover Weekly", exact: true });
    const apple = shelf.getByRole("group", { name: favoriteName, exact: true });
    await expect(spotify).toHaveCount(1);
    await expect(spotify.getByRole("button", { name: /^Remove .+ from apps$/ })).toBeVisible();
    await expect(apple.getByRole("button", { name: `Add ${favoriteName}`, exact: true })).toBeVisible();
    const neighbour = shelf.getByRole("group", { name: "Service mix 01", exact: true });
    const titleHeights = await Promise.all([apple, neighbour].map(card => card.locator("strong").evaluate(element => element.getBoundingClientRect().height)));
    expect(titleHeights[0]).toBeGreaterThan(titleHeights[1]);
    await expect(neighbour.getByText(`Saved from ${owner}`, { exact: true })).toHaveCSS("-webkit-line-clamp", "2");
    expect(await neighbour.locator(".saved-from").evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true);
    await expectAlignedActions(apple);
    await apple.evaluate(element => element.scrollIntoView({ block: "center", inline: "nearest", behavior: "instant" }));
    await page.screenshot({ path: testInfo.outputPath(`services-shelf-${width}-${theme}.png`) });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    if (width === 390) await expectContainedMobileGeometry(page);
    await expect(spotify.locator("img")).toHaveAttribute("src", "/api/admin/mixes/weekly/cover?v=fixture");
    await expect(spotify.getByText("From Spotify · 30 songs · kept in sync", { exact: true })).toBeVisible();
    await shelf.getByRole("button", { name: "See all", exact: true }).click();
    await expect(shelf.getByRole("button", { name: "Show less", exact: true })).toHaveAttribute("aria-expanded", "true");
    await expect(shelf.getByRole("group")).toHaveCount(46);
    await expect(shelf.getByRole("group").nth(0)).toHaveAttribute("aria-label", "Service mix 03");
    await expect(shelf.getByRole("group").nth(1)).toHaveAttribute("aria-label", "Discover Weekly");
    await expect(shelf.getByRole("group").last()).toHaveAttribute("aria-label", "Service mix 40");
    await expect(shelf.getByRole("group", { name: "Service mix 00", exact: true }).getByText("From Spotify · 0 songs", { exact: true })).toBeVisible();
    await expect(shelf.getByRole("group", { name: "Service mix 04", exact: true }).getByText("Saved from Riju Patra", { exact: true })).toBeVisible();
    const saved = shelf.getByRole("group", { name: "Service mix 04", exact: true });
    await expectAlignedActions(apple);
    await expectAlignedActions(saved);
    await expect(shelf.getByRole("group", { name: "Service mix 08", exact: true }).getByText("Preparing songs…", { exact: true })).toBeVisible();
    await expect(shelf.getByRole("group", { name: "Service mix 12", exact: true }).getByText("From Spotify · 12 from your library · 30 songs", { exact: true })).toBeVisible();
    const geometry = await shelf.getByRole("group").evaluateAll(cards => cards.map(card => { const rect = card.getBoundingClientRect(); return { left: rect.left, right: rect.right, top: rect.top }; }));
    expect(geometry.every(rect => rect.left >= 0 && rect.right <= width)).toBe(true);
    expect(geometry.at(-1)!.top).toBeGreaterThan(geometry[0].top);
    await apple.evaluate(element => element.scrollIntoView({ block: "center", inline: "nearest", behavior: "instant" }));
    await page.screenshot({ path: testInfo.outputPath(`services-footer-${width}-${theme}.png`) });
    await shelf.getByRole("group").last().getByRole("button", { name: "Open Service mix 40", exact: true }).scrollIntoViewIfNeeded();
    await expect(shelf.getByRole("group").last().getByRole("button", { name: "Open Service mix 40", exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({ path: testInfo.outputPath(`services-${width}-${theme}.png`), fullPage: true });
    await shelf.getByRole("button", { name: "Show less", exact: true }).click();
    await expect(shelf.getByRole("button", { name: "See all", exact: true })).toHaveAttribute("aria-expanded", "false");
    await apple.getByRole("button", { name: `Open ${favoriteName}`, exact: true }).click();
    await expect(page.getByRole("heading", { name: favoriteName, exact: true })).toBeVisible();
    await expect(page.getByRole("dialog", { name: favoriteName, exact: true }).locator(`img[src="/api/admin/mixes/favorites/cover?v=fixture"]`)).toHaveCount(1);
    await expect(page.getByText("Blue morning", { exact: true }).first()).toBeVisible();
    await openHome(page);
    await shelf.getByRole("group", { name: favoriteName, exact: true }).getByRole("button", { name: `Add ${favoriteName}`, exact: true }).click();
    await expect(shelf.getByRole("group", { name: favoriteName, exact: true }).getByRole("button", { name: /^Remove .+ from apps$/ })).toBeVisible();
    expect(state.calls.filter(call => call.path === "/api/admin/mixes/favorites/in-apps" && call.method === "POST")).toHaveLength(1);
    expect(state.calls.filter(call => call.path === "/api/admin/mixes/external")).toHaveLength(0);
    await apple.getByRole("button", { name: `Open ${favoriteName}`, exact: true }).click();
    await expect(page.getByRole("dialog", { name: favoriteName, exact: true }).getByText("Healthy", { exact: true })).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath(`services-health-${width}-${theme}.png`) });
    await openHome(page);
    await shelf.getByRole("button", { name: "See all", exact: true }).click();
    await shelf.getByRole("group", { name: "Service mix 08", exact: true }).getByRole("button", { name: "Open Service mix 08", exact: true }).click();
    const preparing = page.getByRole("dialog", { name: "Service mix 08", exact: true });
    await expect(preparing.getByText("Preparing songs from your service…", { exact: true })).toBeVisible();
    await expect(preparing.getByRole("button", { name: "Connect", exact: true })).toHaveCount(0);
    await expect(preparing.getByText("0 songs picked for you", { exact: true })).toHaveCount(0);
    await page.screenshot({ path: testInfo.outputPath(`services-preparing-${width}-${theme}.png`) });
    expect(await page.pageErrors()).toEqual([]);
  });
}
