import { expect, test } from "@playwright/test";
import { appearance, capture, mockForYou } from "./for-you-fixtures";
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Release inbox at ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme); await mockForYou(page); await page.goto("/#/for-you?section=releases&release=release");
  await expect(page.getByRole("button", { name: "Following (1)" })).toBeVisible(); await expect(page.getByText("Release Radar collects these every Friday.")).toBeVisible();
  await capture(page, "releases", width, theme); await page.getByRole("button", { name: "Following (1)" }).click(); await expect(page.getByRole("dialog")).toBeVisible();
});
