import { expect, test } from "@playwright/test";
import { appearance, capture, mockForYou, openHome } from "./for-you-fixtures";
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Mix shelves at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme); await mockForYou(page, { partial: true, custom: true }); await openHome(page);
    await expect(page.getByRole("region", { name: "Made for you", exact: true }).getByRole("button")).toHaveCount(8);
    if (width === 390) expect(await page.getByRole("region", { name: "Made for you", exact: true }).evaluate(el => el.scrollWidth > el.clientWidth)).toBe(true);
    await capture(page, "home", width, theme);
  });
  test(`New listener at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme); await mockForYou(page, { brandNew: true, admin: true, publish: false }); await openHome(page);
    await expect(page.getByText("Your mixes will appear here. Play some music, or import your history to start right away.")).toBeVisible();
    await capture(page, "home-new", width, theme);
  });
}
test("Checklist keeps history and dismissal without an admin publishing action", async ({ page }) => {
  const state = await mockForYou(page, { admin: true, publish: false, brandNew: true }); await openHome(page);
  await expect(page.getByText("Mixes are only visible here for now")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Show in apps", exact: true })).toHaveCount(0);
  expect(state.calls.some(call => call.path === "/api/admin/mixes/publish-setting")).toBe(false);
  await page.getByRole("button", { name: "Import history", exact: true }).click(); await expect(page.getByRole("dialog", { name: "Bring in your history" })).toBeVisible();
  await expect(page.getByLabel("History file")).toHaveAttribute("multiple", "");
  await page.getByRole("button", { name: "Close history" }).click();
  await page.getByRole("button", { name: "Hide", exact: true }).click(); await page.reload(); await expect(page.getByRole("heading", { name: "Make your mixes better" })).toHaveCount(0);
});
test("Legacy section routes redirect and listeners never see administrative setup", async ({ page }) => {
  await mockForYou(page, { brandNew: true }); await page.goto("/#/intelligence?section=sounds");
  await expect(page).toHaveURL(/#\/for-you/); await expect(page.getByRole("tab", { name: "For you", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByText("Turn on sound analysis for mood mixes")).toHaveCount(0);
  await page.goto("/#/intelligence?section=stats"); await expect(page.getByRole("tab", { name: "Recap", exact: true })).toHaveAttribute("aria-selected", "true");
});
test("External mix opt-in stays on one artwork card", async ({ page }) => {
  const state = await mockForYou(page); await openHome(page); await page.getByRole("button", { name: /Loved on Last.fm/ }).click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Keep in apps" }).click();
  await expect.poll(() => state.calls.some(call => call.path === "/api/admin/mixes/external" && call.body?.kind === "loved")).toBe(true);
});
test("Ready listeners see music without the completed checklist", async ({ page }) => {
  await mockForYou(page); await openHome(page); await expect(page.getByRole("heading", { name: "Make your mixes better" })).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Moods", exact: true }).getByRole("button")).toHaveCount(5);
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

test("Not-ready shelf artwork stays visible with its readiness description", async ({ page }) => {
  await mockForYou(page, { partial: true }); await openHome(page);
  const mix = page.getByRole("region", { name: "Made for you", exact: true }).getByRole("button", { name: /Daily Mix 4/ });
  await expect(mix.locator("canvas")).toHaveCSS("opacity", "1");
  await expect(mix).toContainText("Needs more listening");
});
