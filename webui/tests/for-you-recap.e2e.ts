import { expect, test } from "@playwright/test";
import { appearance, capture, mockForYou } from "./for-you-fixtures";
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Listening recap at ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme); await mockForYou(page); await page.goto("/#/for-you?section=recap");
  await expect(page.getByRole("heading", { name: "Your listening", exact: true })).toBeAttached(); await expect(page.getByRole("heading", { name: "Bring in your history", exact: true })).toBeVisible();
  await expect(page.getByLabel("History file")).toHaveAttribute("multiple", "");
  await page.getByRole("button", { name: "Year in review", exact: true }).click(); await page.getByRole("option", { name: "2025", exact: true }).click();
  await expect(page.getByText("2025 in music", { exact: true })).toBeVisible(); await capture(page, "recap", width, theme);
});
test("A past year shows the available yearly highlights", async ({ page }) => {
  const state = await mockForYou(page); await page.goto("/#/for-you?section=recap");
  await page.getByRole("button", { name: "Year in review", exact: true }).click(); await page.getByRole("option", { name: "2025", exact: true }).click();
  const summary = page.getByRole("region", { name: "Summary" }); await expect(summary.getByText("North", { exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Top songs", exact: true }).getByText("Blue morning", { exact: true })).toBeVisible(); await expect(summary.getByText("5 hours", { exact: true })).toBeVisible();
  expect(state.calls.some(call => call.path === "/api/admin/stats")).toBe(true);
});
