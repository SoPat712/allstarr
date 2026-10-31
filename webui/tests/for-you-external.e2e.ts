import { expect, test, type Page } from "@playwright/test";
import { appearance, capture, mockForYou, openHome } from "./for-you-fixtures";

async function add(page: Page) {
  await page.getByRole("region", { name: "From your services", exact: true }).locator(".mix-card").filter({ hasText: "Loved on Last.fm" }).getByRole("button", { name: "Add to my apps", exact: true }).click();
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
    await capture(page, "external-before-add", width, theme);
    await add(page);
    await expect(page.getByRole("region", { name: "From your services", exact: true }).locator(".mix-card").filter({ hasText: "Loved on Last.fm" }).getByText("In your apps", { exact: true })).toBeVisible();
    await capture(page, "external-after-add", width, theme);
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
  await page.route("**/api/admin/playlist-links/external?*", route => route.fulfill({ json: {
    id: "external", name: "Loved on Last.fm", sourceProviderId: "lastfm", targetProtocol: "jellyfin",
    trackCount: 10, matchedCount: 8, unmatchedCount: 2, unresolvedCount: 2, reviewCount: 0,
    routeCoverage: [], tracks: [], durationMs: 0, unknownDurationCount: 0,
  } }));
  await openHome(page); blockRefresh = true;
  await add(page);
  await expect(page.getByRole("alertdialog")).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Notifications" })).toContainText("Matching Loved on Last.fm · 0 of 0 songs");
  await expect.poll(() => polls).toBeGreaterThan(0);
  await page.goto("/#/library/playlists");
  await expect(page.getByRole("button", { name: "Open Loved on Last.fm playlist details", exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: "Open Loved on Last.fm mix", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Open Loved on Last.fm playlist details", exact: true }).click();
  await expect(page.getByRole("dialog").getByRole("heading", { name: "Loved on Last.fm", exact: true })).toBeVisible();
  await expect(page).toHaveURL(/#\/library\/playlists$/);
  const count = polls;
  await expect(page.getByRole("region", { name: "Notifications" }).getByRole("status")).toHaveCount(0);
  await page.waitForTimeout(2200);
  expect(polls).toBe(count);
});

test("External matching reports the plain failure reason", async ({ page }) => {
  await mockForYou(page);
  await page.route("**/api/admin/jobs/external-job?*", route => route.fulfill({ json: { job: { state: "Failed", lastErrorMessage: "Reconnect Last.fm to continue." }, attempts: [], progress: [] } }));
  await openHome(page); await add(page);
  const notifications = page.getByRole("region", { name: "Notifications" });
  await expect(notifications.getByRole("alert")).toHaveText(/Reconnect Last.fm to continue./);
  await expect(notifications.locator(".toast-busy")).toHaveCount(0);
});

test("External creation reports request errors in a toast", async ({ page }) => {
  await mockForYou(page);
  await page.route("**/api/admin/mixes/external", route => route.fulfill({ status: 409, json: { error: "Connect your personal source account first." } }));
  await openHome(page); await add(page);
  await expect(page.getByRole("region", { name: "Notifications" }).getByRole("alert")).toContainText("Connect your personal source account first.");
});

test("External matching stops after a bounded wait even when status hangs", async ({ page }) => {
  await mockForYou(page);
  await page.clock.install();
  await page.route("**/api/admin/jobs/external-job?*", () => new Promise(() => {}));
  await openHome(page); await add(page);
  const notifications = page.getByRole("region", { name: "Notifications" });
  await expect(page.getByRole("alertdialog")).toHaveCount(0);
  await expect(notifications.locator(".toast-busy")).toBeVisible();
  await page.clock.fastForward(15 * 60 * 1000);
  await expect(notifications).toContainText("Matching is taking longer than expected. Check your playlist again later.");
  await expect(notifications.locator(".toast-busy")).toHaveCount(0);
});

test("External add leaves a compact status on the card", async ({ page }) => {
  const state = await mockForYou(page); await openHome(page); await add(page);
  const card = page.getByRole("region", { name: "From your services", exact: true }).locator(".mix-card").filter({ hasText: "Loved on Last.fm" });
  await expect(card.getByText("In your apps", { exact: true })).toBeVisible();
  await expect(card.getByRole("link", { name: "Manage in Playlists", exact: true })).toHaveCount(0);
  await expect(card.getByRole("button", { name: "Add to my apps" })).toHaveCount(0);
  expect(state.calls.filter(call => call.path === "/api/admin/mixes/external" && call.method === "POST")).toHaveLength(1);
});
test("External suggestions respect household availability", async ({ page }) => {
  await mockForYou(page, { publish: false }); await openHome(page);
  const shelf = page.getByRole("region", { name: "From your services", exact: true });
  for (const button of await shelf.getByRole("button", { name: "Add to my apps", exact: true }).all()) await expect(button).toBeDisabled();
  await expect(page.getByText("Your admin hasn’t allowed adding mixes to apps.", { exact: true })).toBeVisible();
});
test("Removing an external playlist makes its recipe available again", async ({ page }) => {
  const state = await mockForYou(page); await openHome(page); await add(page);
  await page.goto("/#/library/playlists");
  await page.getByRole("button", { name: "Remove Loved on Last.fm from my apps", exact: true }).click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Remove from my apps", exact: true }).click();
  await expect(page.getByRole("button", { name: "Open Loved on Last.fm playlist details", exact: true })).toHaveCount(0);
  await openHome(page); await add(page);
  await expect.poll(() => state.calls.filter(call => call.path === "/api/admin/mixes/external" && call.method === "POST").length).toBe(2);
});
