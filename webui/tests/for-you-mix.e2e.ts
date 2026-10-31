import { expect, test } from "@playwright/test";
import { appearance, capture, mockForYou, openHome, songs } from "./for-you-fixtures";
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Mix sheet at ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme); await mockForYou(page); await openHome(page);
  await page.getByRole("region", { name: "Made for you", exact: true }).getByRole("button").first().click();
  await expect(page).toHaveURL(/mix=daily-1/); await expect(page.getByRole("dialog").getByText("You play this a lot")).toBeVisible();
  await expect(page.getByRole("dialog").getByRole("list", { name: "Songs", exact: true }).getByRole("listitem")).toHaveCount(songs.length);
  await capture(page, "mix", width, theme); await page.goBack(); await expect(page.getByRole("dialog")).toHaveCount(0);
});
test("Mix actions open an explanation after the songs load", async ({ page }) => {
  const errors: string[] = []; page.on("pageerror", error => errors.push(error.message));
  await mockForYou(page); await page.goto("/#/for-you?mix=daily-1");
  await expect(page.getByRole("dialog").getByRole("list", { name: "Songs", exact: true }).getByRole("listitem")).toHaveCount(songs.length);
  await page.getByRole("button", { name: "Mix actions", exact: true }).click();
  await page.getByRole("menuitem", { name: "About this mix", exact: true }).click();
  await expect(page.getByText("Songs you know and close new picks, grouped around your taste. Refreshes every morning.", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Close explanation", exact: true }).click();
  await expect(page.getByRole("button", { name: "Mix actions", exact: true })).toBeFocused();
  expect(errors).toEqual([]);
});
test("Song feedback and preference undo", async ({ page }) => {
  const state = await mockForYou(page); await page.goto("/#/for-you?mix=daily-1");
  await page.getByRole("button", { name: "Actions for Blue morning", exact: true }).click(); await page.getByRole("menuitem", { name: "Not for me", exact: true }).click();
  await expect(page.getByRole("dialog").getByText("Blue morning", { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Undo", exact: true }).click(); await expect.poll(() => state.taste.nudges.length).toBe(0);
  expect(state.calls.some(call => call.method === "DELETE" && call.path === "/api/admin/taste/nudge")).toBe(true);
});
test("Adding a mix is one click and playlists is the place to manage it", async ({ page }) => {
  const state = await mockForYou(page); state.cards[0].inApps = false;
  await page.goto("/#/for-you?mix=daily-1");
  await page.getByRole("dialog").getByRole("button", { name: "Add to my apps", exact: true }).click();
  await expect(page.getByRole("dialog").getByText("In your apps", { exact: true })).toBeVisible();
  await expect(page.getByRole("dialog").getByRole("link", { name: "Manage in Playlists", exact: true })).toHaveAttribute("href", "#/library/playlists");
  expect(state.calls.filter(call => call.path.endsWith("/in-apps"))).toEqual([{ path: "/api/admin/mixes/daily-1/in-apps", method: "POST", body: null }]);
  await expect(page.getByRole("button", { name: /Remove from|In your apps/ })).toHaveCount(0);
});
for (const admin of [false, true]) test(`Household availability gates adding for ${admin ? "administrators" : "listeners"}`, async ({ page }) => {
  const state = await mockForYou(page, { admin, publish: false }); state.cards[0].inApps = false;
  await page.goto("/#/for-you?mix=daily-1");
  await expect(page.getByRole("dialog").getByRole("button", { name: "Add to my apps", exact: true })).toBeDisabled();
  await expect(page.getByRole("dialog").getByText("Your admin hasn’t allowed adding mixes to apps.", { exact: true })).toBeVisible();
  state.publish = true; await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(page.getByRole("dialog").getByRole("button", { name: "Add to my apps", exact: true })).toBeEnabled();
  expect(state.calls.some(call => call.path === "/api/admin/mixes/publish-setting")).toBe(false);
});
test("Added state survives the household gate and readiness", async ({ page }) => {
  const state = await mockForYou(page, { publish: false }); state.cards[0].readiness.ready = false;
  await page.goto("/#/for-you?mix=daily-1");
  await expect(page.getByRole("dialog").getByText("In your apps", { exact: true })).toBeVisible();
  await expect(page.getByRole("dialog").getByRole("link", { name: "Manage in Playlists" })).toBeVisible();
});
test("An add failure leaves the action available for retry", async ({ page }) => {
  const state = await mockForYou(page); state.cards[0].inApps = false; let failing = true;
  await page.route("**/api/admin/mixes/daily-1/in-apps", route => failing ? route.fulfill({ status: 503, json: {} }) : route.fallback());
  await page.goto("/#/for-you?mix=daily-1");
  const add = page.getByRole("dialog").getByRole("button", { name: "Add to my apps", exact: true }); await add.click();
  await expect(page.getByRole("alert")).toContainText("Your mix could not be updated. Try again.");
  await expect(add).toBeEnabled(); failing = false; await add.click();
  await expect(page.getByRole("dialog").getByText("In your apps", { exact: true })).toBeVisible();
});
test("Mix detail can retry after the first load fails", async ({ page }) => {
  await mockForYou(page); let failing = true;
  await page.route("**/api/admin/mixes/daily-1", route => failing ? route.fulfill({ status: 503, json: {} }) : route.fallback());
  await page.goto("/#/for-you?mix=daily-1");
  await expect(page.getByRole("dialog").getByRole("alert")).toContainText("Couldn't load this mix.");
  failing = false; await page.getByRole("dialog").getByRole("button", { name: "Try again", exact: true }).click();
  await expect(page.getByRole("dialog").getByRole("list", { name: "Songs", exact: true }).getByRole("listitem")).toHaveCount(songs.length);
});
test("Not-ready mix artwork keeps its colour and explanation", async ({ page }) => {
  const state = await mockForYou(page, { partial: true }); state.cards.find(card => card.id === "daily-4")!.inApps = false; await page.goto("/#/for-you?mix=daily-4");
  await expect(page.getByRole("dialog").locator("canvas")).toHaveCSS("opacity", "1");
  await expect(page.getByText("Play about 80 more songs, or import your listening history.", { exact: true })).toBeVisible();
  await expect(page.getByRole("dialog").getByRole("button", { name: "Add to my apps", exact: true })).toBeEnabled();
});
test("Refresh throttle stays in effect after a rejected request", async ({ page }) => {
  await mockForYou(page); await page.goto("/#/for-you?mix=daily-1");
  await page.getByRole("button", { name: "Refresh mix", exact: true }).click(); await expect(page.getByRole("button", { name: "Refresh mix", exact: true })).toBeDisabled();
});
test("Undo preserves other and subsequently edited preferences", async ({ page }) => {
  const state = await mockForYou(page); await page.goto("/#/for-you?mix=daily-1");
  await page.getByRole("button", { name: "Actions for Blue morning", exact: true }).click(); await page.getByRole("menuitem", { name: "Not for me", exact: true }).click();
  await expect(page.getByRole("button", { name: "Undo", exact: true })).toBeVisible();
  state.taste.nudges.push({ kind: "tag", name: "pop", mode: "more", expiresAt: null }); state.taste.nudges[0].mode = "never";
  await page.getByRole("button", { name: "Undo", exact: true }).click(); await expect(page.getByText("Your choice has changed since then. You can edit it in Your taste.")).toBeVisible();
  expect(state.taste.nudges.map(item => item.mode)).toEqual(["never", "more"]);
});
test("More today and hiding an artist use song actions", async ({ page }) => {
  const state = await mockForYou(page); await page.goto("/#/for-you?mix=daily-1");
  await page.getByRole("button", { name: "Actions for Blue morning", exact: true }).click(); await page.getByRole("menuitem", { name: "More like this", exact: true }).hover();
  await page.getByRole("menuitem", { name: "Today only", exact: true }).click();
  await expect.poll(() => state.calls.find(call => call.body?.kind === "moreLikeThis")?.body.todayOnly).toBe(true);
  await page.getByRole("button", { name: "Actions for Quiet water", exact: true }).click(); await page.getByRole("menuitem", { name: "Don't play this artist", exact: true }).click();
  await expect.poll(() => state.calls.some(call => call.body?.kind === "neverArtist")).toBe(true);
});

for (const dismiss of ["back", "close"] as const) test(`Mix ${dismiss} retains the loaded shelves and document`, async ({ page }) => {
  const state = await mockForYou(page);
  const documents: string[] = [];
  page.on("request", request => { if (request.isNavigationRequest() && request.resourceType() === "document") documents.push(request.url()); });
  await page.clock.install({ time: new Date("2026-10-29T12:00:00Z") });
  await page.clock.pauseAt(new Date("2026-10-29T12:00:01Z"));
  await openHome(page);
  const shelf = page.getByRole("region", { name: "Made for you", exact: true });
  await expect(shelf.locator(".mix-open")).toHaveCount(8);
  await shelf.evaluate(element => Reflect.set(element, "retainedShelf", true));
  const initialReads = state.calls.filter(call => call.path === "/api/admin/mixes").length;
  const initialDocuments = documents.length;
  let blockedReads = 0;
  await page.route(url => url.pathname === "/api/admin/mixes", () => { blockedReads++; return new Promise(() => {}); });
  await shelf.getByRole("button", { name: /^Open Blend 1/, exact: false }).click();
  await expect(page.getByRole("dialog").getByRole("list", { name: "Songs", exact: true }).getByRole("listitem")).toHaveCount(songs.length);
  if (dismiss === "back") await page.goBack();
  else await page.getByRole("button", { name: "Close mix", exact: true }).click();
  await expect(page).toHaveURL(/#\/for-you$/);
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(shelf).toBeVisible();
  expect(await shelf.evaluate(element => Reflect.get(element, "retainedShelf"))).toBe(true);
  await expect(page.getByRole("region", { name: "Loading Made for you", exact: true })).toHaveCount(0);
  expect(blockedReads).toBe(0);
  expect(state.calls.filter(call => call.path === "/api/admin/mixes")).toHaveLength(initialReads);
  expect(documents).toHaveLength(initialDocuments);
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await page.clock.runFor(300);
  await expect.poll(() => blockedReads).toBe(1);
  await expect(shelf).toBeVisible();
  expect(await shelf.evaluate(element => Reflect.get(element, "retainedShelf"))).toBe(true);
  await expect(page.getByRole("region", { name: "Loading Made for you", exact: true })).toHaveCount(0);
});

test("Available songs remain visible with a readiness warning", async ({ page }) => {
  const state = await mockForYou(page); state.cards[0].readiness = { ready: false, message: "More listening will improve this mix." };
  await page.goto("/#/for-you?mix=daily-1");
  await expect(page.getByRole("dialog").getByRole("list", { name: "Songs", exact: true }).getByRole("listitem")).toHaveCount(songs.length);
  await expect(page.getByRole("dialog").getByText("More listening will improve this mix.")).toBeVisible();
});
