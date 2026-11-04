import { mkdir } from "node:fs/promises";
import { expect, test } from "@playwright/test";
import { appearance, mockForYou } from "./for-you-fixtures";

for (const view of ["taste", "history"] as const) for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`${view} list loading and retry at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    const fixture = await mockForYou(page);
    let release!: () => void;
    const gate = new Promise<void>(resolve => { release = resolve; });
    let reads = 0;
    const endpoint = view === "taste" ? "/api/admin/taste" : "/api/admin/ui/activity";
    await page.route(url => url.pathname === endpoint, async route => {
      reads++;
      if (reads === 1) {
        await gate;
        return route.fulfill({ status: 503, json: { error: "Unavailable" } });
      }
      return route.fulfill({ json: view === "taste" ? fixture.taste : { items: [], hasMore: false } });
    });
    await page.goto(view === "taste" ? "/#/for-you?section=taste" : "/#/activity?view=history");
    const skeleton = page.getByRole("status", { name: view === "taste" ? "Loading your taste" : "Loading Your activity", exact: true });
    await expect(skeleton).toBeVisible();
    await expect(skeleton).toHaveAttribute("aria-busy", "true");
    const rows = skeleton.locator(view === "taste" ? ".list-skeleton-row" : ".skeleton-row");
    await expect(rows).toHaveCount(6);
    const animationName = () => view === "taste"
      ? skeleton.locator(".list-skeleton-placeholder").first().evaluate(element => getComputedStyle(element).animationName)
      : rows.first().evaluate(element => getComputedStyle(element, "::after").animationName);
    const animation = await animationName();
    expect(animation).toBe("none");
    await page.emulateMedia({ reducedMotion: "no-preference" });
    await expect.poll(animationName).toContain("shimmer");
    await page.emulateMedia({ reducedMotion: "reduce" });
    const folder = new URL("../../test-results/uibugs/", import.meta.url).pathname;
    await mkdir(folder, { recursive: true });
    await page.screenshot({ path: `${folder}/${view}-loading-${width}-${theme}.png`, fullPage: true });
    release();
    const alert = page.getByRole("alert").filter({ hasText: view === "taste" ? "Your taste could not be loaded." : "History could not be loaded." });
    await expect(alert).toBeVisible();
    await expect(skeleton).toHaveCount(0);
    await page.screenshot({ path: `${folder}/${view}-error-${width}-${theme}.png`, fullPage: true });
    await alert.getByRole("button", { name: "Try again", exact: true }).click();
    await expect(alert).toHaveCount(0);
    if (view === "taste") await expect(page.getByRole("button", { name: "Reset taste", exact: true })).toBeVisible();
    else await expect(page.getByRole("region", { name: "Your activity", exact: true })).toBeVisible();
    expect(reads).toBeGreaterThanOrEqual(2);
    expect(await page.pageErrors()).toEqual([]);
  });
}
