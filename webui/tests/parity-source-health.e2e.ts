import { expect, test } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";
import { clickWhenStable, responses, mockApi, sourceHistory, sourcePause } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

for (const width of [320, 390, 1280]) {
  for (const theme of ["light", "dark"] as const) {
    test(`Source health history stays honest at ${width}px in ${theme}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 844 });
      await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
      await mockApi(page);
      await page.route("**/api/admin/providers/status", route => route.fulfill({ json: [sourcePause, {
        ...sourcePause, providerAccountId: null, providerAccountName: null, accountScope: "public", capability: "metadata",
      }] }));
      let reads = 0;
      await page.route("**/api/admin/providers/lumen-audio/health-history**", route => {
        reads++;
        return route.fulfill({ json: sourceHistory });
      });
      await page.goto("#/integrations/services/lumen-audio");
      const routing = page.getByRole("region", { name: "Automatic routing" });
      await expect(routing).toContainText("Paused from automatic use after repeated failures");
      await expect(routing).toContainText("Household account");
      await expect(routing).toContainText("Public access");
      await expect(routing).toContainText("Rate limited");
      await expect(routing).toContainText("Retry after");
      const panel = page.getByRole("dialog", { name: "Lumen Audio", exact: true });
      // Source-use settings load above the history; wait for their final layout before clicking.
      await expect(panel.getByRole("radiogroup", { name: "Use this source for you", exact: true })).toBeVisible();
      const history = page.getByRole("group", { name: /Recent diagnostic checks/ });
      await expect(history).not.toHaveAttribute("open", "");
      await expect(history).toContainText("3 passed · 3 failed · 3 neutral");
      await clickWhenStable(history.locator("summary"));
      await expect(history).toHaveAttribute("open", "");
      await expect(history.getByRole("listitem")).toHaveCount(6);
      await expect(history).toContainText("Under 1 ms");
      await expect(history).toContainText("Sample unavailable");
      await expect(history).not.toContainText("Invalid Date");
      await expect(history).not.toContainText("0 ms");
      await clickWhenStable(history.getByRole("button", { name: "Show more checks" }));
      await expect(history.getByRole("listitem")).toHaveCount(9);
      expect(reads).toBe(1);
      if (width < 768) await expectContainedMobileGeometry(page);
      await page.evaluate(() => (document.activeElement as HTMLElement)?.blur());
      await page.screenshot({ path: `test-results/source-health-${width}-${theme}.png`, fullPage: true });
    });
  }
}

test("Source health Resume now clears the pause and recovers from a failed request", async ({ page }) => {
  await mockApi(page);
  let resumed = false;
  let attempts = 0;
  await page.route("**/api/admin/providers/status", route => route.fulfill({ json: resumed ? [{
    ...sourcePause, canAttempt: true, automaticRetryAt: null, automaticPausedAt: null,
  }] : [sourcePause] }));
  await page.route("**/api/admin/providers/lumen-audio/health-history**", route => route.fulfill({ json: sourceHistory }));
  await page.route("**/api/admin/providers/lumen-audio/resume/streaming?accountId=account", route => {
    expect(route.request().method()).toBe("POST");
    attempts++;
    if (attempts === 1) return route.fulfill({ status: 503, json: { error: "Unavailable" } });
    resumed = true;
    return route.fulfill({ json: { resumed: true } });
  });
  await page.goto("#/integrations/services/lumen-audio");
  const routing = page.getByRole("region", { name: "Automatic routing" });
  await expect(routing).toContainText("Started");
  const strip = page.getByRole("region", { name: "Last 30 checks", exact: true }).getByRole("group", { name: /^9 recorded checks, oldest to newest;/ });
  await expect(strip).toBeVisible();
  await expect(strip.locator('[data-outcome]:not([data-outcome="empty"])')).toHaveCount(9);
  await routing.getByRole("button", { name: "Resume now" }).click();
  await expect(routing).toContainText("Couldn’t resume automatic use. Try again.");
  await routing.getByRole("button", { name: "Resume now" }).click();
  await expect(routing).toHaveCount(0);
  await expect(strip).toBeVisible();
  expect(attempts).toBe(2);
});

test("Source health history recovers from an error without busying Refresh", async ({ page }) => {
  await mockApi(page);
  let reads = 0;
  await page.route("**/api/admin/providers/lumen-audio/health-history**", route => {
    reads++;
    return reads === 1 ? route.fulfill({ status: 503, json: { error: "Unavailable" } }) :
      route.fulfill({ json: { ...sourceHistory, checks: [], counts: { passed: 0, failed: 0, neutral: 0 } } });
  });
  await page.goto("#/integrations/services/lumen-audio");
  const history = page.getByRole("group", { name: /Recent diagnostic checks/ });
  await history.locator("summary").click();
  await expect(history).toContainText("Couldn’t load recent checks.");
  await history.getByRole("button", { name: "Retry" }).click();
  await expect(page.getByRole("dialog").getByText("No checks yet", { exact: true })).toBeVisible();
  await expect(history).toHaveCount(0);
  expect(reads).toBe(2);
  await page.getByRole("dialog").getByRole("button", { name: "Close", exact: true }).click();
  await expect(page.getByRole("button", { name: "Refresh", exact: true })).toBeEnabled();
});

test("Source health history ignores a response after changing source", async ({ page }) => {
  await mockApi(page);
  let release!: () => void;
  const pending = new Promise<void>(resolve => { release = resolve; });
  let requested = false, returned = false;
  await page.route("**/api/admin/providers/lumen-audio/health-history**", async route => {
    requested = true;
    await pending;
    await route.fulfill({ json: sourceHistory }).catch(() => {}).finally(() => { returned = true; });
  });
  await page.goto("#/integrations/services/lumen-audio");
  await expect.poll(() => requested).toBe(true);
  await page.evaluate(() => { location.hash = "#/integrations/services/spotify"; });
  const panel = page.getByRole("dialog", { name: "Spotify", exact: true });
  await expect(panel.getByText("This source isn't checked automatically", { exact: true })).toBeVisible();
  release();
  await expect.poll(() => returned).toBe(true);
  await expect(panel).not.toContainText("Household account");
  await expect(panel.getByText("This source isn't checked automatically", { exact: true })).toBeVisible();
  await expect(panel).toBeVisible();
});

test("Source health retry follows server time and refreshes quietly once", async ({ page }) => {
  await page.clock.install({ time: new Date("2026-01-01T00:00:00Z") });
  await page.clock.pauseAt(new Date("2026-01-01T00:00:01Z"));
  await mockApi(page);
  let statusReads = 0;
  let historyReads = 0;
  await page.route("**/api/admin/providers/status", route => {
    statusReads++;
    return route.fulfill({ json: [{ ...sourcePause,
      automaticRetryAt: statusReads === 1 ? "2030-04-05T12:00:02.000Z" : null,
      canAttempt: statusReads > 1,
    }] });
  });
  await page.route("**/api/admin/providers/lumen-audio/health-history**", route => {
    historyReads++;
    return route.fulfill({ json: sourceHistory });
  });
  await page.goto("#/integrations/services/lumen-audio");
  const routing = page.getByRole("region", { name: "Automatic routing" });
  await expect(routing).toContainText("Paused from automatic use after repeated failures");
  await expect(page.getByRole("region", { name: "Last 30 checks", exact: true }).getByRole("group")).toBeVisible();
  expect(statusReads).toBe(1);
  expect(historyReads).toBe(1);
  await page.clock.runFor(2_100);
  await expect.poll(() => statusReads).toBe(2);
  await expect(routing).toHaveCount(0);
  await expect.poll(() => historyReads).toBe(2);
  await page.clock.runFor(2_100);
  expect(statusReads).toBe(2);
  expect(historyReads).toBe(2);
  // The five-second freshness poll is separate from the retry-window refresh.
  await page.clock.runFor(1_100);
  await expect.poll(() => statusReads).toBe(3);
  await page.getByRole("dialog").getByRole("button", { name: "Close", exact: true }).click();
  await expect(page.getByRole("button", { name: "Refresh", exact: true })).toBeEnabled();
});

test("Source health history stays private to administrators", async ({ page }) => {
  await mockApi(page);
  await page.route("**/api/admin/auth/me", route => route.fulfill({ json: {
    ...(responses["/api/admin/auth/me"] as object),
    user: { id: "user", allstarrUserId: "user", name: "Tester", isAdministrator: false },
  } }));
  let reads = 0;
  await page.route("**/api/admin/providers/*/health-history**", route => {
    reads++;
    return route.fulfill({ json: sourceHistory });
  });
  await page.goto("#/integrations/services/lumen-audio");
  const panel = page.getByRole("dialog", { name: "Lumen Audio", exact: true });
  await expect(panel.getByRole("heading", { name: "Lumen Audio", exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Last 30 checks", exact: true })).toHaveCount(0);
  await expect(page.getByRole("group", { name: /Recent diagnostic checks/ })).toHaveCount(0);
  expect(reads).toBe(0);
});
