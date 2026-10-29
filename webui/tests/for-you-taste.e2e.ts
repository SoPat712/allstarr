import { expect, test } from "@playwright/test";
import { appearance, capture, mockForYou } from "./for-you-fixtures";
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Taste steering at ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme); const state = await mockForYou(page); await page.goto("/#/for-you?section=taste");
  await expect(page.getByText("Lately you've been into", { exact: false })).toBeVisible();
  await page.getByRole("button", { name: "Steer R&B", exact: true }).click(); await page.getByRole("button", { name: "More", exact: true }).click();
  await expect.poll(() => state.taste.nudges[0]?.mode).toBe("more"); expect(state.calls.find(call => call.body?.mode === "more")?.body.todayOnly).toBe(true);
  await page.getByRole("button", { name: "Steer R&B", exact: true }).click(); await page.getByRole("button", { name: "Never", exact: true }).click();
  await expect(page.getByRole("region", { name: "Hidden", exact: true }).getByText("R&B", { exact: true })).toBeVisible();
  await capture(page, "taste", width, theme);
  await page.getByRole("region", { name: "Hidden", exact: true }).getByRole("button", { name: "Undo" }).click();
  await expect(page.getByText("Nothing hidden.")).toBeVisible();
  await page.getByRole("button", { name: "Reset taste", exact: true }).click(); await page.getByRole("checkbox", { name: "Also remove my nudges" }).click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Reset taste" }).click(); await expect.poll(() => state.taste.nudges.length).toBe(0);
});
