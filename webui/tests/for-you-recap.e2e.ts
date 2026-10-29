import { expect, test } from "@playwright/test";
import { appearance, capture, mockForYou } from "./for-you-fixtures";
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Listening recap at ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme); await mockForYou(page); await page.goto("/#/for-you?section=recap");
  await expect(page.getByRole("heading", { name: "Your listening", exact: true })).toBeVisible(); await expect(page.getByRole("heading", { name: "Bring in your history", exact: true })).toBeVisible();
  await expect(page.getByLabel("History file")).toHaveAttribute("multiple", "");
  await page.getByRole("button", { name: "Year in review", exact: true }).click(); await page.getByRole("option", { name: "2025", exact: true }).click();
  await expect(page.getByRole("region", { name: "Year in music" })).toBeVisible(); await capture(page, "recap", width, theme);
});
test("A past year shows the available yearly highlights", async ({ page }) => {
  const state = await mockForYou(page); await page.goto("/#/for-you?section=recap");
  await page.getByRole("button", { name: "Year in review", exact: true }).click(); await page.getByRole("option", { name: "2025", exact: true }).click();
  const year = page.getByRole("region", { name: "Year in music" }); await expect(year.getByText("North", { exact: true })).toBeVisible();
  await expect(year.getByText("Blue morning", { exact: true })).toBeVisible(); await expect(year.getByText("about 5 hours")).toBeVisible();
  await expect(year.getByText("Not available yet")).toHaveCount(2); expect(state.calls.some(call => call.path === "/api/admin/stats")).toBe(true);
});
