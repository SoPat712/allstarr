import { expect, test } from "@playwright/test";
import { appearance, capture, mockForYou, openHome } from "./for-you-fixtures";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Mix shelves at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme); await mockForYou(page, { partial: true, custom: true }); await openHome(page);
    await expect(page.getByRole("region", { name: "Made for you", exact: true }).locator(".mix-open")).toHaveCount(8);
    if (width === 390) expect(await page.getByRole("region", { name: "Made for you", exact: true }).locator(".mix-shelf").evaluate(el => el.scrollWidth > el.clientWidth)).toBe(true);
    await capture(page, "home", width, theme);
  });
  test(`New listener at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme); await mockForYou(page, { brandNew: true, admin: true, publish: false }); await openHome(page);
    await expect(page.getByText("Your mixes will appear here. Play some music, or import your history to start right away.")).toBeVisible();
    await capture(page, "home-new", width, theme);
  });
}
for (const width of [390, 899, 900, 1280]) test(`Mix shelves keep one row and every card reachable by keyboard at ${width}px`, async ({ page }) => {
  await appearance(page, width, "light"); await mockForYou(page); await openHome(page);
  for (const shelf of await page.locator(".mix-shelf").all()) {
    const cards = shelf.locator(".mix-card");
    const geometry = await cards.evaluateAll(elements => elements.map(element => {
      const box = element.getBoundingClientRect(); return { top: Math.round(box.top), width: box.width };
    }));
    expect(new Set(geometry.map(card => card.top)).size).toBe(1);
    expect(geometry.every(card => card.width >= 132)).toBe(true);
    const needsScroll = await shelf.evaluate(element => element.scrollWidth > element.clientWidth);
    if (await cards.count() === 8) expect(needsScroll).toBe(true);
    const controls = shelf.locator("button:not([disabled])");
    await controls.first().focus();
    for (let index = 0; index < await controls.count(); index++) {
      await expect(controls.nth(index)).toBeFocused();
      await expect(controls.nth(index)).toBeInViewport();
      if (index + 1 < await controls.count()) await page.keyboard.press("Tab");
    }
  }
});

test("Checklist keeps history and dismissal without an admin publishing action", async ({ page }) => {
  const state = await mockForYou(page, { admin: true, publish: false, brandNew: true }); await openHome(page);
  await expect(page.getByText("Mixes are only visible here for now")).toHaveCount(0);
  expect(state.calls.some(call => call.path === "/api/admin/mixes/publish-setting")).toBe(false);
  await page.getByRole("button", { name: "Import history", exact: true }).click(); await expect(page.getByRole("dialog", { name: "Bring in your history" })).toBeVisible();
  await expect(page.getByLabel("History file")).toHaveAttribute("multiple", "");
  await page.getByRole("button", { name: "Close history" }).click();
  await page.getByRole("button", { name: "Hide tips", exact: true }).click(); await page.reload();
  await expect(page.getByRole("heading", { name: "Made for you", exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Get more from your mixes" })).toHaveCount(0);
});
test("Legacy section routes redirect and listeners never see administrative setup", async ({ page }) => {
  await mockForYou(page, { brandNew: true }); await page.goto("/#/intelligence?section=sounds");
  await expect(page).toHaveURL(/#\/for-you/); await expect(page.getByRole("tab", { name: "For you", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByText("Turn on sound analysis for mood mixes")).toHaveCount(0);
  await page.goto("/#/intelligence?section=stats"); await expect(page.getByRole("tab", { name: "Recap", exact: true })).toHaveAttribute("aria-selected", "true");
});
test("External mix opt-in stays on one artwork card", async ({ page }) => {
  const state = await mockForYou(page); await openHome(page); await page.getByRole("region", { name: "From your services", exact: true }).locator(".mix-card").filter({ hasText: "Loved on Last.fm" }).getByRole("button", { name: "Add Loved on Last.fm", exact: true }).last().click();
  await expect.poll(() => state.calls.some(call => call.path === "/api/admin/mixes/external" && call.body?.kind === "loved")).toBe(true);
});
test("Ready listeners see music without the completed checklist", async ({ page }) => {
  await mockForYou(page); await openHome(page); await expect(page.getByRole("heading", { name: "Get more from your mixes" })).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Moods", exact: true }).locator(".mix-open")).toHaveCount(5);
});
test("A failed mix request can retry in place and background reads stay silent", async ({ page }) => {
  await mockForYou(page); let failing = true;
  await page.route("**/api/admin/mixes?**", route => failing ? route.fulfill({ status: 503, json: {} }) : route.fallback());
  await page.goto("/#/for-you"); await expect(page.getByRole("alert")).toContainText("Couldn't load your mixes."); failing = false;
  await page.getByRole("button", { name: "Try again", exact: true }).click(); await openHome(page);
  await page.evaluate(() => window.dispatchEvent(new Event("focus"))); await expect(page.getByRole("button", { name: "Refresh page" })).toHaveAttribute("aria-busy", "false");
});
test("Loading shelves never push the page wider than the window", async ({ page }) => {
  await page.setViewportSize({ width: 1000, height: 800 }); await mockForYou(page, { partial: true });
  await page.route(url => url.pathname === "/api/admin/mixes", () => new Promise(() => {}));
  await openHome(page).catch(() => {});
  await expect(page.getByRole("region", { name: "Loading Made for you" })).toBeVisible();
  const refresh = page.getByRole("button", { name: "Refresh page" }); await expect(refresh).toBeVisible();
  const box = await refresh.boundingBox(); expect(box!.x + box!.width).toBeLessThanOrEqual(1000);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(1000);
});

test("Not-ready mixes are dimmed with a reason and open without an Add chip", async ({ page }) => {
  await mockForYou(page, { partial: true }); await openHome(page);
  const mix = page.getByRole("region", { name: "Made for you", exact: true }).getByRole("button", { name: /Blend 4/ });
  await expect(mix.locator(".artwork")).toHaveCSS("opacity", "0.6");
  await expect(mix).toContainText("Play about 80 more songs");
  await expect(page.getByRole("group", { name: "Blend 4", exact: true }).getByRole("button", { name: "Add Blend 4", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Add Blend 2", exact: true })).toBeVisible();
  await mix.click();
  await expect(page.getByRole("dialog")).toBeVisible();
});

test("Shelf Add is one click, keeps card height, and the card opens separately", async ({ page }) => {
  const state = await mockForYou(page, { partial: true }); await openHome(page);
  const card = page.getByRole("group", { name: "Blend 2", exact: true });
  const add = card.getByRole("button", { name: "Add Blend 2", exact: true });
  await expect(add).toHaveText("Add to apps");
  expect(await add.evaluate(button => button.parentElement === button.parentElement?.querySelector(".mix-open")?.parentElement)).toBe(true);
  const height = (await card.boundingBox())!.height;
  await add.click();
  await expect(card.getByText("✓ In your apps", { exact: true })).toBeVisible();
  await expect(add).toHaveCount(0);
  expect((await card.boundingBox())!.height).toBe(height);
  expect(state.calls.filter(call => call.path === "/api/admin/mixes/daily-2/in-apps")).toHaveLength(1);
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await card.getByRole("button", { name: "Open Blend 2", exact: true }).click();
  await expect(page.getByRole("dialog").getByRole("list", { name: "Songs", exact: true })).toBeVisible();
});

test("Completed history removes the listening tip even with an unready mix", async ({ page }) => {
  await mockForYou(page, { partial: true });
  await page.route("**/api/admin/history/imports", route => route.fulfill({ json: [{ state: 3, importedRows: 62000 }] }));
  await openHome(page);
  const checklist = page.getByRole("region", { name: "Get more from your mixes", exact: true });
  await expect(checklist).toHaveCount(0);
  await expect(checklist.getByRole("button", { name: "Import history", exact: true })).toHaveCount(0);
});

test("Service suggestion artwork also adds without opening a panel", async ({ page }) => {
  const state = await mockForYou(page); await openHome(page);
  await page.getByRole("group", { name: "Similar to your top tracks", exact: true }).getByRole("button", { name: "Add Similar to your top tracks", exact: true }).first().click();
  await expect.poll(() => state.calls.filter(call => call.path === "/api/admin/mixes/external" && call.body?.kind === "similar").length).toBe(1);
  await expect(page.getByRole("dialog")).toHaveCount(0);
});

test.describe("Cover touch targets", () => {
  test.use({ hasTouch: true });
  test("Add to apps has a forty-four pixel target while adding", async ({ page }) => {
    await appearance(page, 390, "light"); const state = await mockForYou(page); await openHome(page);
    let release!: () => void;
    const waiting = new Promise<void>(resolve => { release = resolve; });
    await page.route("**/api/admin/mixes/daily-2/in-apps", async route => { await waiting; await route.fallback(); });
    const add = page.getByRole("button", { name: "Add Blend 2", exact: true });
    const target = await add.evaluate(button => {
      const box = button.getBoundingClientRect();
      return { coarse: matchMedia("(pointer: coarse)").matches, width: box.width, height: box.height };
    });
    expect(target.coarse).toBe(true); expect(target.width).toBeGreaterThanOrEqual(44); expect(target.height).toBeGreaterThanOrEqual(44);
    await add.click(); await expect(add).toHaveText("Adding…"); await expect(add).toBeDisabled();
    release(); await expect(add).toHaveCount(0);
    expect(state.calls.filter(call => call.path === "/api/admin/mixes/daily-2/in-apps")).toHaveLength(1);
  });
});

test("Tips list only unfinished actions", async ({ page }) => {
  await mockForYou(page, { partial: true, admin: true }); await openHome(page);
  const tips = page.getByRole("region", { name: "Get more from your mixes", exact: true });
  await expect(tips.locator(".check-row")).toHaveCount(1);
  await expect(tips.getByRole("button", { name: "Import history", exact: true })).toBeVisible();
  await expect(tips.getByText("Connect Last.fm or ListenBrainz for more discoveries")).toHaveCount(0);
  await expect(tips.getByText("Turn on sound analysis for mood mixes")).toHaveCount(0);
  const close = await tips.getByRole("button", { name: "Hide tips" }).boundingBox();
  expect(close!.width).toBeGreaterThanOrEqual(44); expect(close!.height).toBeGreaterThanOrEqual(44);
});

test("Finished tips stay absent when another mix cannot be built", async ({ page }) => {
  const state = await mockForYou(page, { admin: true });
  state.cards.find(card => card.id === "discover-weekly")!.readiness = { ready: false, message: "Connect a service with recommendations, or add unheard tagged songs to your library." };
  await openHome(page);
  await expect(page.getByRole("region", { name: "Get more from your mixes", exact: true })).toHaveCount(0);
  await expect(page.getByRole("group", { name: "Uncharted", exact: true })).toContainText("No new songs to suggest yet");
});
