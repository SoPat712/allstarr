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
test("A pending save keeps its preview stable", async ({ page }) => {
  await mockForYou(page); let finish!: () => void;
  const saved = new Promise<void>(resolve => { finish = resolve; });
  await page.route("**/api/admin/mixes/start/preview/save", async route => { await saved; await route.fulfill({ json: { id: "custom" } }); });
  await openHome(page); await page.getByRole("searchbox").fill("gold"); await page.getByRole("button", { name: "Golden hour by Mira", exact: true }).click();
  const dialog = page.getByRole("dialog"); await dialog.getByRole("button", { name: "Save to my mixes" }).click();
  await expect(dialog.getByRole("button", { name: "Mix length", exact: true })).toBeDisabled(); await expect(dialog.getByRole("button", { name: "Cancel" })).toBeDisabled();
  await expect(dialog.getByLabel("Mix name")).toBeDisabled(); await expect(dialog.getByText("You play this a lot", { exact: true })).toBeVisible();
  finish(); await expect(dialog).toBeHidden();
});
