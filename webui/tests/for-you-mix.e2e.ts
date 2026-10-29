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
test("Publication gate and refresh throttle", async ({ page }) => {
  const state = await mockForYou(page, { admin: true, publish: false }); await page.goto("/#/for-you?mix=daily-1");
  await expect(page.getByRole("checkbox", { name: "Show in my apps" })).toBeDisabled(); await expect(page.getByText("Not published yet")).toBeVisible();
  state.publish = true; await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(page.getByRole("checkbox", { name: "Show in my apps" })).toBeEnabled(); await page.getByRole("checkbox", { name: "Show in my apps" }).click();
  await expect.poll(() => state.cards[0].showInApps).toBe(false);
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
