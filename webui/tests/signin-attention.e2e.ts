import { expect, test, type Page } from "@playwright/test";
import { homeFixture } from "./shared-design-fixtures";

// Home's overview as the shared fixture serves it, plus accounts whose sign-in a service refused.
async function expiredSignIns(page: Page, accounts: { providerId: string; accountId: string; accountName: string }[]) {
  await page.route("**/api/admin/ui/home", route => route.fulfill({ json: {
    schema: { providers: [{ id: "catalog", name: "Music service" }, { id: "deezer", name: "Deezer" }], activeBackend: "Jellyfin" },
    status: { backendType: "Jellyfin", durableStorage: { readiness: "Ready" } },
    stats: { linkedPlaylists: 24, playableTracks: 1224, unresolvedTracks: 1, activeJobs: 1,
      completedListens: 25, currentWeekListens: 36, previousWeekListens: 24, scrobbleDeliveries: 6 },
    providerHealth: { providers: [{ providerId: "catalog", enabledAccountCount: 1, capabilityTotal: 4, healthyCapabilityCount: 3, failedCapabilityCount: 1 }] },
    activity: { items: [], hasMore: false },
    signInExpiredAccounts: accounts,
  } }));
}

for (const administrator of [true, false]) {
  test(`An expired sign-in leads Home's attention line for ${administrator ? "an administrator" : "a listener"}`, async ({ page }) => {
    await homeFixture(page, administrator);
    await expiredSignIns(page, [{ providerId: "deezer", accountId: "deezer-1", accountName: "Josh's Deezer" }]);
    await page.goto("/#/");
    const banner = page.getByRole("region", { name: "Needs your attention", exact: true });
    await expect(banner.getByText("Deezer sign-in expired", { exact: true })).toBeVisible();
    await expect(banner.getByRole("link", { name: "Reconnect", exact: true })).toHaveAttribute("href", "#/integrations/accounts");
  });
}

test("Several expired sign-ins are counted in one line", async ({ page }) => {
  await homeFixture(page, true);
  await expiredSignIns(page, [
    { providerId: "deezer", accountId: "deezer-1", accountName: "Josh's Deezer" },
    { providerId: "catalog", accountId: "catalog-1", accountName: "Household" },
  ]);
  await page.goto("/#/");
  const banner = page.getByRole("region", { name: "Needs your attention", exact: true });
  await expect(banner.getByText("2 sign-ins expired", { exact: true })).toBeVisible();
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Home's attention button keeps its label inside at ${width}px ${theme}`, async ({ page }) => {
  await page.setViewportSize({ width, height: 844 }); await page.emulateMedia({ colorScheme: theme });
  await homeFixture(page, true);
  await expiredSignIns(page, []);
  await page.goto("/#/");
  const banner = page.getByRole("region", { name: "Needs your attention", exact: true });
  const action = banner.getByRole("link", { name: "Review sources", exact: true });
  await expect(action).toBeVisible();
  await banner.screenshot({ path: test.info().outputPath(`attention-${width}-${theme}.png`) });
  const fit = await action.evaluate(element => ({ content: element.scrollWidth, box: element.clientWidth, height: element.getBoundingClientRect().height }));
  expect(fit.content).toBeLessThanOrEqual(fit.box);
  expect(fit.height).toBeLessThanOrEqual(40);
});
