import { expect, test, type Page, type Route } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const personal = { id: "11111111-1111-4111-8111-111111111111", displayName: "My phone", kind: "ntfy", enabled: true, household: false, hasDestination: true, revision: 2 };
const household = { ...personal, id: "22222222-2222-4222-8222-222222222222", displayName: "Household chat", kind: "discord", household: true };
async function reply(route: Route, data: unknown, status = 200) {
  return route.fulfill({ status, contentType: "application/json", body: JSON.stringify(data) });
}
async function mockAlerts(page: Page, administrator = true) {
  const state = { reads: 0, tests: 0, fail: "", open: true, channels: [personal, ...(administrator ? [household] : [])].map(channel => ({ ...channel })),
    deliveries: [{ id: "delivery", channelId: personal.id, status: "failed", createdAt: "2026-10-28T12:01:00Z", safeError: "The destination rejected the notification." }],
    revision: 3, permittedDestinations: ["notify.example"],
    thresholds: { warningBytes: 5 * 1024 ** 3, warningPercent: 5, criticalBytes: 1024 ** 3, criticalPercent: 1, hysteresisBytes: 1024 ** 3, hysteresisPercent: 1, cooldownMinutes: 30 },
    gate: null as Promise<void> | null };
  const writes: Array<{ path: string; body: Record<string, unknown> }> = [];
  const snapshot = () => ({ channels: state.channels, deliveries: state.deliveries, revision: state.revision,
    thresholds: administrator ? state.thresholds : null, permittedDestinations: administrator ? state.permittedDestinations : null });
  await page.route("**/api/admin/**", async route => {
    const path = new URL(route.request().url()).pathname, method = route.request().method();
    if (path === "/api/admin/auth/me") return reply(route, { authenticated: true, backend: "Jellyfin", user: { id: "viewer", name: "Viewer", isAdministrator: administrator } });
    if (path === "/api/admin/auth/oidc/status") return reply(route, { enabled: false });
    if (path === "/api/admin/onboarding/status") return reply(route, { completed: true, setupOpen: false, shouldRedirectToSetup: false,
      schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"], revision: 1, recoveryNotices: [], migration: { available: false, completed: false, firstRun: false } });
    if (path === "/api/admin/ui/schema") return reply(route, { configSections: [] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    if (path === "/api/admin/alerts/incidents") return reply(route, state.open ? [{ id: "incident", kind: "source-failure", state: "open", severity: "warning", openedAt: "2026-10-28T12:00:00Z",
      message: "A source is repeatedly failing or automatically paused.", action: "Review source health in Sources." }, { id: "resolved", state: "resolved", message: "Resolved incident" }] : []);
    if (path === "/api/admin/alerts") {
      state.reads++; if (state.gate) await state.gate;
      if (state.fail === "read") return reply(route, { error: "Alerts are temporarily unavailable." }, 503);
      return reply(route, snapshot());
    }
    if (path.startsWith("/api/admin/alerts/")) {
      if (path.endsWith("/test")) {
        expect(method).toBe("POST"); state.tests++;
        if (state.fail === "test") return reply(route, { error: "Wait one minute before sending another test." }, 409);
        state.deliveries = [{ id: "queued-test", channelId: personal.id, status: "queued", createdAt: "2026-10-28T12:05:00Z", safeError: "" }];
        return reply(route, { deliveryId: "queued-test", preview: { message: "This is a test notification from Allstarr." } }, 202);
      }
      const body = route.request().postDataJSON() as Record<string, unknown>;
      writes.push({ path, body });
      if (state.fail === "save") return reply(route, { error: "Alert settings changed. Reload before retrying." }, 409);
      if (path.endsWith("/thresholds")) { state.thresholds = body.thresholds as typeof state.thresholds; state.revision++; return reply(route, snapshot()); }
      if (path.endsWith("/destinations")) { state.permittedDestinations = body.permittedDestinations as string[]; state.revision++; return reply(route, snapshot()); }
      const old = state.channels.find(channel => path.endsWith(channel.id));
      const channel = { ...personal, displayName: body.displayName, kind: body.kind, enabled: body.enabled, household: body.household,
        id: old?.id ?? "33333333-3333-4333-8333-333333333333", revision: (old?.revision ?? 0) + 1 } as typeof personal;
      state.channels = [...state.channels.filter(item => item.id !== channel.id), channel];
      return reply(route, channel, old ? 200 : 201);
    }
    return reply(route, {});
  });
  return { state, writes };
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Alerts and channel editor fit at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    const mock = await mockAlerts(page);
    await page.goto("/#/settings/alerts");
    const incidents = page.getByRole("region", { name: "Open problems", exact: true });
    await expect(incidents).toContainText("A source is repeatedly failing");
    await expect(incidents).toContainText("Started");
    await expect(page.getByText("Resolved incident", { exact: true })).toHaveCount(0);
    await expect(page.getByRole("region", { name: "Your alert channels", exact: true })).toContainText("Could not deliver");
    await expect(page.getByRole("region", { name: "Household alerts", exact: true })).toContainText("Discord");
    expect(mock.state.tests).toBe(0);
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: `test-results/shots-uisettings/alerts-main-${width}-${theme}.png`, fullPage: true });
    await page.getByRole("button", { name: "Edit My phone", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Edit alert channel", exact: true });
    await expect(dialog.getByLabel("Destination URL", { exact: true })).toHaveValue("");
    await expect(dialog).toContainText("Saved");
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: `test-results/shots-uisettings/alerts-dialog-${width}-${theme}.png`, fullPage: true });
    await page.keyboard.press("Escape");
    await expect(dialog).toHaveCount(0);
  });
}

test("Channels save without reading destinations and tests queue only on explicit click", async ({ page }) => {
  const { state, writes } = await mockAlerts(page);
  await page.goto("/#/settings/alerts");
  await page.getByRole("button", { name: "Edit My phone", exact: true }).click();
  let dialog = page.getByRole("dialog");
  await dialog.getByLabel("Channel name", { exact: true }).fill("My alerts");
  await dialog.getByRole("button", { name: "Save channel", exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].body).toEqual({ displayName: "My alerts", kind: "ntfy", enabled: true, household: false, expectedRevision: 2 });
  await page.getByRole("checkbox", { name: "Enable My alerts", exact: true }).click();
  await expect.poll(() => writes.length).toBe(2);
  expect(writes[1].body).toMatchObject({ enabled: false, expectedRevision: 3 });
  await expect(page.getByRole("button", { name: "Send test to My alerts", exact: true })).toBeDisabled();
  await page.getByRole("checkbox", { name: "Enable My alerts", exact: true }).click();
  await expect(page.getByRole("button", { name: "Send test to My alerts", exact: true })).toBeEnabled();
  state.fail = "save";
  await page.getByRole("checkbox", { name: "Enable My alerts", exact: true }).click();
  await expect(page.getByRole("checkbox", { name: "Enable My alerts", exact: true })).toBeChecked();
  await expect(page.getByText("Alert settings changed. Reload before retrying.", { exact: true })).toBeVisible();
  state.fail = "";
  expect(state.tests).toBe(0);
  await page.getByRole("button", { name: "Send test to My alerts", exact: true }).click();
  await expect(page.getByText("Test notification queued. Check last delivery for the result.", { exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Your alert channels", exact: true })).toContainText("Waiting to send");
  state.fail = "test";
  await page.getByRole("button", { name: "Send test to My alerts", exact: true }).click();
  await expect(page.getByText("Wait one minute before sending another test.", { exact: true })).toBeVisible();
  const dismiss = page.getByRole("button", { name: "Dismiss notification", exact: true });
  while (await dismiss.count()) await dismiss.first().click();
  await page.getByRole("button", { name: "Add your channel", exact: true }).click();
  dialog = page.getByRole("dialog");
  await dialog.getByLabel("Channel type", { exact: true }).click();
  await page.getByRole("option", { name: "Webhook", exact: true }).click();
  await dialog.getByLabel("Channel name", { exact: true }).fill("My webhook");
  await dialog.getByLabel("Destination URL", { exact: true }).fill("https://notify.example/private?token=secret-value");
  state.fail = "save";
  await dialog.getByRole("button", { name: "Save channel", exact: true }).click();
  await expect(dialog.getByRole("alert")).toContainText("Reload");
  state.fail = "";
  await dialog.getByRole("button", { name: "Save channel", exact: true }).click();
  await expect(dialog).toHaveCount(0);
  const channel = page.getByRole("listitem").filter({ has: page.getByText("My webhook", { exact: true }) });
  await expect(channel).toContainText("Saved · notify.example");
  await expect(page.getByText("secret-value", { exact: false })).toHaveCount(0);
  expect(writes.at(-1)?.body).toMatchObject({ kind: "webhook", destination: "https://notify.example/private?token=secret-value", expectedRevision: 0 });
  await page.getByRole("button", { name: "Add household channel", exact: true }).click();
  await page.getByRole("dialog").getByLabel("Channel name", { exact: true }).fill("Shared webhook");
  await page.getByRole("dialog").getByLabel("Destination URL", { exact: true }).fill("https://notify.example/shared");
  await page.getByRole("dialog").getByRole("button", { name: "Save channel", exact: true }).click();
  await expect.poll(() => writes.at(-1)?.body.household).toBe(true);
});

test("Disk thresholds and allowed hosts validate, save revisions, and preserve edits during focus refresh", async ({ page }) => {
  const { state, writes } = await mockAlerts(page);
  await page.goto("/#/settings/alerts");
  const thresholds = page.getByRole("form", { name: "Disk alert thresholds", exact: true });
  await expect(thresholds.getByLabel("Warning free space (GiB)", { exact: true })).toHaveValue("5");
  await expect(thresholds.getByLabel("Warning free space (%)", { exact: true })).toBeHidden();
  await thresholds.getByText("Advanced", { exact: true }).click();
  await expect(thresholds.getByLabel("Warning free space (%)", { exact: true })).toBeVisible();
  await thresholds.getByText("Advanced", { exact: true }).click();
  await thresholds.getByLabel("Critical free space (GiB)", { exact: true }).fill("6");
  await expect(thresholds.getByRole("button", { name: "Save thresholds", exact: true })).toBeDisabled();
  await thresholds.getByLabel("Critical free space (GiB)", { exact: true }).fill("0.5");
  const before = state.reads;
  let release!: () => void; state.gate = new Promise(resolve => { release = resolve; });
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect.poll(() => state.reads).toBeGreaterThan(before);
  await expect(page.locator(".refresh-spinning")).toHaveCount(0);
  release(); state.gate = null;
  await expect(thresholds.getByLabel("Critical free space (GiB)", { exact: true })).toHaveValue("0.5");
  await thresholds.getByRole("button", { name: "Save thresholds", exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].body).toEqual({ thresholds: { ...state.thresholds, criticalBytes: 536870912 }, expectedRevision: 3 });
  const hosts = page.getByRole("form", { name: "Allowed destination hosts", exact: true });
  await hosts.getByLabel("Allowed hosts", { exact: true }).fill("https://notify.example/path");
  await expect(hosts.getByRole("button", { name: "Save hosts", exact: true })).toBeDisabled();
  await hosts.getByLabel("Allowed hosts", { exact: true }).fill("notify.example\nwebhook.example:8443\nNOTIFY.example");
  await hosts.getByRole("button", { name: "Save hosts", exact: true }).click();
  await expect.poll(() => writes.length).toBe(2);
  expect(writes[1].body).toEqual({ permittedDestinations: ["notify.example", "webhook.example:8443"], expectedRevision: 4 });
  state.gate = new Promise(resolve => { release = resolve; });
  await page.getByRole("button", { name: "Refresh alerts", exact: true }).click();
  await expect(page.locator(".refresh-spinning")).toHaveCount(1);
  release(); state.gate = null;
  await expect(page.locator(".refresh-spinning")).toHaveCount(0);
  state.fail = "read";
  await page.getByRole("button", { name: "Refresh alerts", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("temporarily unavailable");
  state.fail = ""; state.open = false;
  await page.getByRole("button", { name: "Refresh alerts", exact: true }).click();
  await expect(page.getByText("No open problems.", { exact: true })).toBeVisible();
});

test("Listeners see personal alerts and no household controls", async ({ page }) => {
  await mockAlerts(page, false);
  await page.goto("/#/settings/alerts");
  await expect(page.getByRole("region", { name: "Your alert channels", exact: true })).toBeVisible();
  for (const name of ["Household alerts", "Disk alert thresholds", "Allowed destination hosts"])
    await expect(page.getByRole("region", { name, exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Add household channel", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Add your channel", exact: true }).click();
  await expect(page.getByRole("dialog")).toBeVisible();
  await expect(page.getByRole("dialog")).not.toContainText("Household");
});

test("Destination replacements keep only the current saved host", async ({ page }) => {
  const { state } = await mockAlerts(page);
  await page.goto("/#/settings/alerts");
  const channel = page.getByRole("listitem").filter({ has: page.getByText("My phone", { exact: true }) });
  await page.getByRole("button", { name: "Edit My phone", exact: true }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel("Destination URL", { exact: true }).fill("https://notify.example/private?token=first-secret");
  await dialog.getByRole("button", { name: "Save channel", exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(channel).toContainText("Saved · notify.example");
  await page.getByRole("button", { name: "Edit My phone", exact: true }).click();
  await expect(dialog.getByLabel("Destination URL", { exact: true })).toHaveValue("");
  await dialog.getByLabel("Destination URL", { exact: true }).fill("https://replacement.example/hidden?token=second-secret");
  await dialog.getByRole("button", { name: "Save channel", exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(channel).toContainText("Saved · replacement.example");
  await expect(channel).not.toContainText("notify.example");
  await page.getByRole("checkbox", { name: "Enable My phone", exact: true }).click();
  await expect(page.getByRole("checkbox", { name: "Enable My phone", exact: true })).not.toBeChecked();
  await expect(channel).toContainText("Saved · replacement.example");
  expect(await page.locator("body").textContent()).not.toMatch(/first-secret|second-secret|\/hidden|\/private/);
  state.channels = state.channels.map(item => item.id === personal.id ? { ...item, revision: item.revision + 1 } : item);
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(channel).not.toContainText("replacement.example");
  await expect(channel).toContainText("Saved");
});
