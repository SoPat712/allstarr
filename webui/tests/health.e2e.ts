import { expect, test, type Page, type Route } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

function snapshot(configured = false) {
  return {
    configuration: {
      enabled: false, intervalMinutes: 30, listenerUserId: configured ? "test-listener" : null,
      searchQuery: "Get Lucky Daft Punk", firstByteBudgetSeconds: 15, revision: configured ? 1 : 0,
      hasListenerCredential: configured, hasWebhook: false, nextRunAt: null as string | null,
    },
    protocol: "Jellyfin", inactiveProtocol: "Subsonic",
    listeners: [
      { id: "test-listener", displayName: "Test listener", protocol: "Jellyfin", isSynthetic: configured },
      { id: "second-listener", displayName: "Other listener", protocol: "Jellyfin", isSynthetic: false },
    ],
    runs: [] as Array<{
      id: string; protocol: string; startedAt: string; completedAt: string; passed: boolean;
      steps: Array<{ name: string; status: string; latencyMilliseconds: number; reason: string | null }>;
      alertStatus: string;
    }>,
    runningJobId: null as string | null,
  };
}

async function respond(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}

async function mockHealth(page: Page, health: (route: Route) => Promise<void>, administrator = true) {
  await page.route("**/api/admin/**", async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/admin/auth/me") return respond(route, {
      authenticated: true, backend: "Jellyfin",
      user: { id: "viewer", name: "Viewer", isAdministrator: administrator },
    });
    if (path === "/api/admin/auth/oidc/status") return respond(route, { enabled: false });
    if (path === "/api/admin/onboarding/status") return respond(route, {
      completed: true, setupOpen: false, shouldRedirectToSetup: false, schemaVersion: "onboarding-v1",
      completedSteps: ["backend-identity"], completionSource: "setup-guide", revision: 1,
      recoveryNotices: [], migration: { available: false, completed: false, firstRun: false },
    });
    if (path === "/api/admin/ui/schema") return respond(route, { configSections: [] });
    if (path.startsWith("/api/admin/health")) return health(route);
    if (path === "/api/admin/updates/stream")
      return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return respond(route, {});
  });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Health defaults and configuration keep secrets private at ${width}px ${theme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    let view = snapshot();
    const writes: Record<string, unknown>[] = [];
    await mockHealth(page, async route => {
      if (route.request().method() === "PUT") {
        const body = route.request().postDataJSON();
        writes.push(body);
        view = snapshot(true);
        view.configuration.enabled = body.enabled;
        view.configuration.intervalMinutes = body.intervalMinutes;
        view.configuration.firstByteBudgetSeconds = body.firstByteBudgetSeconds;
        view.configuration.hasWebhook = !body.clearWebhook;
        view.configuration.revision = writes.length;
      }
      return respond(route, view);
    });
    await page.goto("/#/settings/health");
    await expect(page.getByRole("heading", { name: "Health", exact: true })).toBeVisible();
    await expect(page.getByText("Subsonic is not exposed by this deployment.", { exact: true })).toBeVisible();
    await expect(page.getByText("No checks yet", { exact: true })).toBeVisible();
    if (width === 390) await expectContainedMobileGeometry(page);
    await expect(page.getByRole("button", { name: "Run now", exact: true })).toBeDisabled();
    await expect(page.getByLabel("Enable scheduled checks", { exact: true })).not.toBeChecked();
    const form = page.getByRole("form", { name: "Health monitoring", exact: true });
    await expect(form.getByLabel("Check interval (minutes)")).toHaveValue("30");
    await expect(form.getByLabel("Search query", { exact: true })).toHaveValue("Get Lucky Daft Punk");
    await expect(form.getByLabel("First audio byte budget (seconds)")).toHaveValue("15");
    await expect(form.getByLabel("Listener username", { exact: true })).toHaveValue("");
    await expect(form.getByLabel("Listener password", { exact: true })).toHaveValue("");
    await expect(form.getByLabel("Webhook URL", { exact: true })).toHaveValue("");
    await expect(form).toContainText("permanently");
    await expect(form).toContainText("even after monitoring is disabled");
    await expect(form).toContainText("listening history, recommendations and external scrobbles");
    await form.getByRole("button", { name: "Health listener", exact: true }).focus();
    await page.keyboard.press("Enter");
    await page.getByRole("option", { name: "Test listener", exact: true }).click();
    await form.getByLabel("Listener username", { exact: true }).fill("fixture-listener");
    await form.getByLabel("Listener password", { exact: true }).fill("fixture-password");
    await form.getByLabel("Webhook URL", { exact: true }).fill("https://example.invalid/fixture-webhook");
    await form.getByLabel("Enable scheduled checks", { exact: true }).check();
    await form.getByLabel("Check interval (minutes)").fill("60");
    await form.getByRole("button", { name: "Save health settings", exact: true }).click();
    await expect.poll(() => writes[0]).toEqual({
      enabled: true, listenerUserId: "test-listener", intervalMinutes: 60,
      searchQuery: "Get Lucky Daft Punk", firstByteBudgetSeconds: 15, expectedRevision: 0,
      username: "fixture-listener", password: "fixture-password",
      webhookUrl: "https://example.invalid/fixture-webhook", clearWebhook: false,
    });
    await expect(page.getByRole("status").filter({ hasText: "Health settings saved." })).toBeVisible();
    for (const label of ["Listener username", "Listener password", "Webhook URL"])
      await expect(form.getByLabel(label, { exact: true })).toHaveValue("");
    await expect(form.getByLabel("Check interval (minutes)")).toHaveValue("60");
    await expect(page.getByRole("button", { name: "Run now", exact: true })).toBeEnabled();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    const saveButton = form.getByRole("button", { name: "Save health settings", exact: true });
    await saveButton.scrollIntoViewIfNeeded();
    await expect(form.getByText(/^Saving permanently marks this listener/)).toBeInViewport();
    if (width === 390) {
      const buttonBox = await saveButton.boundingBox();
      const navigationBox = await page.locator(".mobile-navigation").boundingBox();
      expect(buttonBox!.y + buttonBox!.height).toBeLessThanOrEqual(navigationBox!.y);
    }
    await page.screenshot({ path: testInfo.outputPath(`health-form-${width}-${theme}.png`) });
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({ path: testInfo.outputPath(`health-${width}-${theme}.png`) });

    await form.getByLabel("Enable scheduled checks", { exact: true }).uncheck();
    await form.getByRole("button", { name: "Save health settings", exact: true }).click();
    await expect.poll(() => writes.length).toBe(2);
    expect(writes[1]).toEqual({ enabled: false, listenerUserId: "test-listener", intervalMinutes: 60,
      searchQuery: "Get Lucky Daft Punk", firstByteBudgetSeconds: 15, expectedRevision: 1, clearWebhook: false });
    await form.getByLabel("Remove saved webhook", { exact: true }).check();
    await form.getByRole("button", { name: "Save health settings", exact: true }).click();
    await expect.poll(() => writes.length).toBe(3);
    expect(writes[2].clearWebhook).toBe(true);
    expect(writes[2]).not.toHaveProperty("webhookUrl");
    await form.getByRole("button", { name: "Health listener", exact: true }).click();
    await page.getByRole("option", { name: "Other listener", exact: true }).click();
    await expect(form.getByLabel("Listener username", { exact: true })).toHaveAttribute("required", "");
    await expect(form.getByLabel("Listener password", { exact: true })).toHaveAttribute("required", "");
  });
}

test("Health runs while schedule is off, polls pending work and shows failure and recovery", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  let view = snapshot(true);
  let runs = 0;
  let polls = 0;
  let reads = 0;
  await mockHealth(page, async route => {
    if (route.request().method() === "POST") {
      runs++;
      view.runningJobId = `pending-${runs}`;
      return respond(route, { jobId: view.runningJobId }, 202);
    }
    reads++;
    if (view.runningJobId) {
      polls++;
      view.runningJobId = null;
      view.runs.unshift({ id: `check-${runs}`, protocol: "Jellyfin", startedAt: "2026-10-23T12:00:00Z",
        completedAt: "2026-10-23T12:00:20Z", passed: runs > 1,
        steps: [
          { name: "search", status: "passed", latencyMilliseconds: 240, reason: null },
          { name: "playback", status: runs > 1 ? "passed" : "failed", latencyMilliseconds: runs > 1 ? 800 : 16000,
            reason: runs > 1 ? null : "timeout" },
          { name: "cleanup", status: "passed", latencyMilliseconds: 120, reason: null },
        ], alertStatus: runs > 1 ? "delivered" : "failed" });
    }
    return respond(route, view);
  });
  await page.goto("/#/settings/health");
  await page.getByLabel("Search query", { exact: true }).fill("Unsaved query");
  await page.getByRole("button", { name: "Run now", exact: true }).click();
  await expect(page.getByRole("status").filter({ hasText: "A check is running." })).toBeVisible();
  await expect(page.getByRole("button", { name: "Run now", exact: true })).toBeDisabled();
  const latest = page.getByRole("article", { name: "Latest health check", exact: true });
  await expect(latest).toContainText("The step exceeded its time limit.");
  await expect(latest).toContainText("Playback");
  await expect(latest).toContainText("16 s");
  await expect(latest).toContainText("Alert delivery failed");
  await expect(page.getByLabel("Search query", { exact: true })).toHaveValue("Unsaved query");
  await expect.poll(() => polls).toBe(1);
  await expectContainedMobileGeometry(page);
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: testInfo.outputPath("health-failure-390-dark.png") });
  await page.getByRole("button", { name: "Run now", exact: true }).click();
  await expect(latest).toContainText("Passed");
  await expect(latest).toContainText("Alert delivered");
  await expect(page.getByRole("region", { name: "Check history", exact: true })).toContainText("Failed");
  await expect.poll(() => polls).toBe(2);
  const completedReads = reads;
  await page.waitForTimeout(2500);
  expect(reads).toBe(completedReads);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: testInfo.outputPath("health-recovery-390-dark.png") });
});

test("Health explains skipped checks and source availability at 320px", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 320, height: 900 });
  const view = snapshot(true);
  view.runs.push({ id: "unavailable", protocol: "Jellyfin", startedAt: "2026-10-23T12:00:00Z",
    completedAt: "2026-10-23T12:00:20Z", passed: false,
    steps: [
      { name: "search", status: "failed", latencyMilliseconds: 120, reason: "no_eligible_song" },
      { name: "playback", status: "skipped", latencyMilliseconds: 0, reason: "search_required" },
      { name: "cleanup", status: "passed", latencyMilliseconds: 20, reason: null },
    ], alertStatus: "not_configured" });
  await mockHealth(page, route => respond(route, view));
  await page.goto("/#/settings/health");
  const latest = page.getByRole("article", { name: "Latest health check", exact: true });
  await expect(latest).toContainText("No eligible provider song was found. Check the query and automatic sources.");
  await expect(latest).toContainText("Search must pass before this step can run.");
  await expect(latest).toContainText("Skipped");
  await expectContainedMobileGeometry(page);
  await page.screenshot({ path: testInfo.outputPath("health-unavailable-320.png") });
});

test("Health identifies a download seek failure independently of playback seeking", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 900 });
  const view = snapshot(true);
  view.runs.push({ id: "download-range", protocol: "Jellyfin", startedAt: "2026-10-23T12:00:00Z",
    completedAt: "2026-10-23T12:00:20Z", passed: false,
    steps: [
      { name: "seek", status: "passed", latencyMilliseconds: 50, reason: null },
      { name: "download", status: "passed", latencyMilliseconds: 800, reason: null },
      { name: "download-seek", status: "failed", latencyMilliseconds: 40, reason: "invalid_range" },
      { name: "cleanup", status: "passed", latencyMilliseconds: 20, reason: null },
    ], alertStatus: "none" });
  await mockHealth(page, route => respond(route, view));
  await page.goto("/#/settings/health");
  const latest = page.getByRole("article", { name: "Latest health check", exact: true });
  await expect(latest).toContainText("Seek in download failed.");
  await expect(latest).toContainText("The source did not return the requested byte range.");
  const steps = latest.getByRole("list", { name: "Check steps", exact: true });
  await expect(steps.getByRole("listitem").filter({ has: page.getByText("Seek", { exact: true }) })).toContainText("Passed");
  await expect(steps.getByRole("listitem").filter({ has: page.getByText("Seek in download", { exact: true }) })).toContainText("Failed");
  await expectContainedMobileGeometry(page);
  await page.screenshot({ path: testInfo.outputPath("health-download-seek-390.png") });
});

test("Health conflicts keep edits and offer a safe reload", async ({ page }) => {
  const view = snapshot(true);
  let conflict = true;
  const writes: unknown[] = [];
  await mockHealth(page, async route => {
    if (route.request().method() === "PUT") {
      writes.push(route.request().postDataJSON());
      if (conflict) return respond(route, { code: "revision_conflict", error: "revision_conflict" }, 409);
    }
    return respond(route, view);
  });
  await page.goto("/#/settings/health");
  await page.getByLabel("Check interval (minutes)").fill("60");
  await page.getByRole("button", { name: "Save health settings", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("Health settings changed. Reload settings and try again.");
  await expect(page.getByLabel("Check interval (minutes)")).toHaveValue("60");
  view.configuration.revision = 2;
  conflict = false;
  await page.getByRole("button", { name: "Reload health settings", exact: true }).click();
  await expect(page.getByLabel("Check interval (minutes)")).toHaveValue("30");
  await page.getByLabel("Check interval (minutes)").fill("45");
  await page.getByRole("button", { name: "Save health settings", exact: true }).click();
  await expect.poll(() => writes.length).toBe(2);
  expect(writes[1]).toMatchObject({ expectedRevision: 2, intervalMinutes: 45 });
});

for (const [code, message] of [
  ["check_running", "A health check is already running. Refresh results to follow it."],
  ["not_configured", "Configure and save a listener before running a check."],
]) {
  test(`Health explains ${code} without exposing a response payload`, async ({ page }) => {
    await mockHealth(page, route => route.request().method() === "POST"
      ? respond(route, { code, error: code }, 409) : respond(route, snapshot(true)));
    await page.goto("/#/settings/health");
    await page.getByRole("button", { name: "Run now", exact: true }).click();
    await expect(page.getByRole("alert")).toHaveText(message);
    await expect(page.getByRole("button", { name: "Refresh results", exact: true })).toBeEnabled();
  });
}

test("Health is inaccessible to a non-administrator", async ({ page }) => {
  let healthRequests = 0;
  await mockHealth(page, async route => { healthRequests++; return respond(route, snapshot()); }, false);
  await page.goto("/#/settings/health");
  await expect(page.getByRole("heading", { name: "This workspace is for administrators.", exact: true })).toBeVisible();
  await expect(page.getByRole("form", { name: "Health monitoring", exact: true })).toHaveCount(0);
  expect(healthRequests).toBe(0);
});
