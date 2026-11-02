import { expect, test } from "@playwright/test";
import { appearance, capture, mockForYou, openHome } from "./for-you-fixtures";
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Seed preview and save at ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme); const state = await mockForYou(page); await openHome(page);
  await page.getByRole("searchbox", { name: "Start a mix from a song or artist" }).fill("gold");
  await page.getByRole("button", { name: "Golden hour by Mira", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Mix from Golden hour" }); await expect(dialog.getByText("Calm sound").first()).toBeVisible();
  await capture(page, "preview", width, theme);
  await dialog.getByLabel("Mix name").fill("Evening listening"); await dialog.getByRole("button", { name: "Save to my mixes", exact: true }).click();
  await expect(page.getByRole("region", { name: "Your mixes", exact: true }).getByText("Evening listening")).toBeVisible();
  expect(state.calls.find(call => call.path === "/api/admin/mixes/start")?.body).toEqual({ seedKind: "song", seedId: "seed", length: 25 });
});
test("Library songs show while services are still searching", async ({ page }) => {
  await mockForYou(page); let finish!: () => void;
  const answered = new Promise<void>(resolve => { finish = resolve; }), asked: URL[] = [];
  await page.route("**/api/admin/track-matches/targets/provider?*", async route => {
    asked.push(new URL(route.request().url())); await answered;
    await route.fulfill({ json: { tracks: [{ id: "lucid", externalProvider: "catalog", externalId: "lucid", title: "Lucid dreams", artist: "Juice WRLD", artworkUrl: null }], providers: ["catalog"] } });
  });
  await openHome(page); await page.getByRole("searchbox", { name: "Start a mix from a song or artist" }).fill("gold");
  const mix = page.getByRole("region", { name: "Start a mix", exact: true }), pending = mix.getByRole("status").filter({ hasText: "Still searching services…" });
  await expect(mix.getByRole("button", { name: "Golden hour by Mira", exact: true })).toBeVisible(); await expect(pending).toBeVisible();
  await expect(mix.getByText("Searching…", { exact: true })).toBeHidden(); expect(asked.map(url => url.searchParams.get("answer"))).toEqual(["fast"]);
  finish(); await expect(pending).toBeHidden();
  await expect(mix.getByRole("button")).toHaveText([/Golden hour/, /Lucid dreams/]);
});
test("A late service answer for an earlier search is dropped", async ({ page }) => {
  await mockForYou(page); let finish!: () => void;
  const answered = new Promise<void>(resolve => { finish = resolve; });
  await page.route("**/api/admin/track-matches/targets/provider?*", async route => {
    const early = new URL(route.request().url()).searchParams.get("query") === "gold"; if (early) await answered;
    await route.fulfill({ json: { tracks: [{ id: early ? "early" : "late", externalProvider: "catalog", externalId: early ? "early" : "late", title: early ? "Old answer" : "Golden days", artist: "Sol", artworkUrl: null }], providers: ["catalog"] } });
  });
  await openHome(page); const search = page.getByRole("searchbox", { name: "Start a mix from a song or artist" }), mix = page.getByRole("region", { name: "Start a mix", exact: true });
  await search.fill("gold"); await expect(mix.getByRole("status").filter({ hasText: "Still searching services…" })).toBeVisible();
  await search.fill("golden"); await expect(mix.getByRole("button")).toHaveText([/Golden hour/, /Golden days/]);
  const late = page.waitForResponse(response => new URL(response.url()).searchParams.get("query") === "gold" && response.url().includes("/provider?"));
  finish(); await (await late).finished(); await page.evaluate(() => new Promise(requestAnimationFrame));
  await expect(mix.getByRole("button")).toHaveText([/Golden hour/, /Golden days/]);
});
test("Picking a library song before services answer keeps the list closed", async ({ page }) => {
  await mockForYou(page); let finish!: () => void;
  const answered = new Promise<void>(resolve => { finish = resolve; });
  await page.route("**/api/admin/track-matches/targets/provider?*", async route => { await answered; await route.fulfill({ json: { tracks: [{ id: "lucid", externalProvider: "catalog", externalId: "lucid", title: "Lucid dreams", artist: "Juice WRLD", artworkUrl: null }], providers: ["catalog"] } }); });
  await openHome(page); await page.getByRole("searchbox").fill("gold"); await page.getByRole("button", { name: "Golden hour by Mira", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Mix from Golden hour" })).toBeVisible();
  const late = page.waitForResponse(response => response.url().includes("/provider?"));
  finish(); await (await late).finished(); await page.evaluate(() => new Promise(requestAnimationFrame));
  await expect(page.locator(".seed-results")).toHaveCount(0);
});
test("A pending save keeps its preview stable", async ({ page }) => {
  await mockForYou(page); let finish!: () => void;
  const saved = new Promise<void>(resolve => { finish = resolve; });
  await page.route("**/api/admin/mixes/start/preview/save", async route => { await saved; await route.fulfill({ json: { id: "custom" } }); });
  await openHome(page); await page.getByRole("searchbox").fill("gold"); await page.getByRole("button", { name: "Golden hour by Mira", exact: true }).click();
  const dialog = page.getByRole("dialog"); await dialog.getByRole("button", { name: "Save to my mixes" }).click();
  await expect(dialog.getByRole("button", { name: "Mix length", exact: true })).toBeDisabled(); await expect(dialog.getByRole("button", { name: "Cancel" })).toBeDisabled();
  await expect(dialog.getByLabel("Mix name")).toBeDisabled(); await expect(dialog.getByText("Played 6 times in 30 days", { exact: true })).toBeVisible();
  finish(); await expect(dialog).toBeHidden();
});
