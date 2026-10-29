import { expect, test, type Page } from "@playwright/test";
import { appearance, capture, mockForYou, openHome } from "./for-you-fixtures";

async function keep(page: Page) {
  await page.getByRole("button", { name: /Loved on Last.fm/ }).click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Keep in apps" }).click();
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`External matching notifications at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    await mockForYou(page);
    let completed = 0, state = "Running", polls = 0;
    await page.route("**/api/admin/jobs/external-job?*", route => {
      polls++;
      return route.fulfill({ json: { job: { id: "external-job", state }, attempts: [], progress: [{ id: "progress", action: "playlist.match", outcome: "running", detailsJson: JSON.stringify({ stage: "playlist.match", completed, total: 10 }), createdAt: "2026-10-29T12:00:00Z" }] } });
    });
    await openHome(page);
    await keep(page);
    await expect(page.getByRole("alertdialog")).toHaveCount(0);
    const notifications = page.getByRole("region", { name: "Notifications" });
    await expect(notifications.getByRole("status")).toHaveText(/Matching Loved on Last.fm · 0 of 10 songs/);
    await expect(notifications.locator(".toast-busy")).toBeVisible();
    await capture(page, "external-matching", width, theme);
    completed = 4;
    await expect(notifications.getByRole("status")).toHaveText(/Matching Loved on Last.fm · 4 of 10 songs/);
    await expect(notifications.getByRole("status")).toHaveCount(1);
    state = "Succeeded";
    await expect(notifications.getByRole("status")).toContainText("Loved on Last.fm is ready · 8 of 10 songs matched");
    await expect(notifications.locator(".toast-busy")).toHaveCount(0);
    await capture(page, "external-ready", width, theme);
    const finalPolls = polls;
    await notifications.getByRole("button", { name: "Open", exact: true }).click();
    await expect(page).toHaveURL(/mix=external/);
    await expect(page.getByRole("heading", { name: "Loved on Last.fm", exact: true })).toBeVisible();
    expect(polls).toBe(finalPolls);
    await page.goto("/#/library/playlists");
    await expect(page.getByRole("button", { name: "Open Loved on Last.fm playlist details", exact: true })).toBeVisible();
    await capture(page, "external-playlists", width, theme);
  });
}

test("External matching closes before background refresh and stops when leaving", async ({ page }) => {
  await mockForYou(page);
  let polls = 0, blockRefresh = false;
  await page.route("**/api/admin/mixes?*", route => blockRefresh ? new Promise(() => {}) : route.fallback());
  await page.route("**/api/admin/jobs/external-job?*", route => {
    polls++;
    return route.fulfill({ json: { job: { state: "Pending" }, attempts: [], progress: [] } });
  });
  await openHome(page); blockRefresh = true;
  await keep(page);
  await expect(page.getByRole("alertdialog")).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Notifications" })).toContainText("Matching Loved on Last.fm · 0 of 0 songs");
  await expect.poll(() => polls).toBeGreaterThan(0);
  await page.goto("/#/library/playlists");
  await expect(page.getByRole("button", { name: "Open Loved on Last.fm playlist details", exact: true })).toBeVisible();
  const count = polls;
  await expect(page.getByRole("region", { name: "Notifications" }).getByRole("status")).toHaveCount(0);
  await page.waitForTimeout(2200);
  expect(polls).toBe(count);
});

test("External matching reports the plain failure reason", async ({ page }) => {
  await mockForYou(page);
  await page.route("**/api/admin/jobs/external-job?*", route => route.fulfill({ json: { job: { state: "Failed", lastErrorMessage: "Reconnect Last.fm to continue." }, attempts: [], progress: [] } }));
  await openHome(page); await keep(page);
  const notifications = page.getByRole("region", { name: "Notifications" });
  await expect(notifications.getByRole("alert")).toHaveText(/Reconnect Last.fm to continue./);
  await expect(notifications.locator(".toast-busy")).toHaveCount(0);
});

test("External creation reports request errors in a toast", async ({ page }) => {
  await mockForYou(page);
  await page.route("**/api/admin/mixes/external", route => route.fulfill({ status: 409, json: { error: "Connect your personal source account first." } }));
  await openHome(page); await keep(page);
  await expect(page.getByRole("region", { name: "Notifications" }).getByRole("alert")).toContainText("Connect your personal source account first.");
});

test("External matching stops after a bounded wait even when status hangs", async ({ page }) => {
  await mockForYou(page);
  await page.clock.install();
  await page.route("**/api/admin/jobs/external-job?*", () => new Promise(() => {}));
  await openHome(page); await keep(page);
  const notifications = page.getByRole("region", { name: "Notifications" });
  await expect(page.getByRole("alertdialog")).toHaveCount(0);
  await expect(notifications.locator(".toast-busy")).toBeVisible();
  await page.clock.fastForward(15 * 60 * 1000);
  await expect(notifications).toContainText("Matching is taking longer than expected. Check your playlist again later.");
  await expect(notifications.locator(".toast-busy")).toHaveCount(0);
});
