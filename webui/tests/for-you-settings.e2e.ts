import { expect, test } from "@playwright/test";
import { appearance, capture, mockForYou } from "./for-you-fixtures";
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Sound analysis settings at ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme); const state = await mockForYou(page, { admin: true }); await page.goto("/#/settings/sound-analysis");
  await expect(page.getByText("Connected · 1,240 songs analyzed")).toBeVisible(); await capture(page, "settings", width, theme);
  await page.getByRole("button", { name: "Edit connection" }).click(); const dialog = page.getByRole("dialog", { name: "AudioMuse connection" });
  await expect(dialog.getByLabel("API key", { exact: true })).toHaveValue(""); await expect(dialog.getByLabel("API key", { exact: true })).toHaveAttribute("type", "password");
  await dialog.getByRole("button", { name: "Cancel" }).click(); expect(state.calls.some(call => call.method === "PUT")).toBe(false);
});
test("Connection editing keeps a blank key write-only and retries conflicts", async ({ page }) => {
  const state = await mockForYou(page, { admin: true }); let reject = true;
  await page.route("**/api/admin/audiomuse/connection", route => { if (route.request().method() === "PUT" && reject) { reject = false; return route.fulfill({ status: 409, json: {} }); } return route.fallback(); });
  await page.goto("/#/settings/sound-analysis"); await page.getByRole("button", { name: "Edit connection" }).click();
  const dialog = page.getByRole("dialog"); await dialog.getByLabel("AudioMuse address").fill("http://sounds.invalid:8601"); await dialog.getByRole("button", { name: "Save", exact: true }).click();
  await expect(dialog.getByRole("alert")).toContainText("The connection changed."); await dialog.getByRole("button", { name: "Cancel" }).click();
  await page.getByRole("button", { name: "Edit connection" }).click(); await dialog.getByLabel("API key", { exact: true }).fill("sample-key"); await dialog.getByRole("button", { name: "Save", exact: true }).click();
  await expect(dialog).toBeHidden(); expect(state.calls.filter(call => call.method === "PUT").at(-1)?.body.apiKey).toBe("sample-key");
  await page.getByRole("button", { name: "Edit connection" }).click(); await expect(dialog.getByLabel("API key", { exact: true })).toHaveValue("");
});
