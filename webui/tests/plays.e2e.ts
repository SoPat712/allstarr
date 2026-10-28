import { mkdir } from "node:fs/promises";
import { expect, test, type Page } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const listener = "11111111-1111-4111-8111-111111111111";
const other = "22222222-2222-4222-8222-222222222222";
const trace = (id: string, listenerId = listener) => ({
  id, listenerId, startedAt: "2026-10-28T12:00:00Z", outcome: "completed",
  request: { protocol: "Jellyfin", presented: { title: listenerId === listener ? "Evening song" : "Morning song", artist: "River", artwork: "/api/admin/test-artwork" } },
  resolution: { provider: "applemusic", cached: listenerId === other },
  timing: { elapsedMs: 200000, firstByteMs: 1200, headersMs: 1100, omittedPhases: 0, phases: [
    { phase: "Source", provider: "applemusic", startedMs: 100, elapsedMs: 1000 },
    { phase: "Route", provider: null, startedMs: 0, elapsedMs: 100 },
  ] },
});
const first = trace("33333333-3333-4333-8333-333333333333");
const redacted = '{"schemaVersion":1,"request":{"presented":{"title":"Evening song"}}}';
async function mockPlays(page: Page, administrator = false, rows = [first]) {
  const reads: string[] = [];
  await page.route("**/*", async route => {
    const url = new URL(route.request().url()), path = url.pathname;
    if (!["127.0.0.1", "localhost"].includes(url.hostname)) return route.abort();
    if (!path.startsWith("/api/admin/")) return route.continue();
    const reply = (body: unknown) => route.fulfill({ json: body });
    if (path === "/api/admin/auth/me") return reply({ authenticated: true, backend: "Jellyfin", user: { id: "backend-listener", allstarrUserId: listener, name: "Josh", isAdministrator: administrator } });
    if (path === "/api/admin/auth/oidc/status") return reply({ enabled: false });
    if (path === "/api/admin/onboarding/status") return reply({ completed: true, shouldRedirectToSetup: false, completedSteps: ["backend-identity"], recoveryNotices: [] });
    if (path === "/api/admin/ui/schema") return reply({ providers: [{ id: "applemusic", name: "Apple Music" }], configSections: [] });
    if (path === "/api/admin/ui/activity") return reply({ items: [], hasMore: false });
    if (path === "/api/admin/play-traces") { reads.push(path); return reply(rows); }
    if (path === `/api/admin/play-traces/${first.id}`) return reply(first);
    if (path.endsWith("/diagnostics")) return route.fulfill({ contentType: "application/json", body: redacted });
    if (path === "/api/admin/test-artwork") return route.fulfill({ contentType: "image/svg+xml", body: '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64"><rect width="64" height="64" fill="currentColor"/></svg>' });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return reply({});
  });
  return reads;
}
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Plays list details and diagnostics at ${width}px ${theme}`, async ({ page, context }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await context.grantPermissions(["clipboard-read", "clipboard-write"]);
    const reads = await mockPlays(page);
    await page.goto("/#/activity");
    await page.getByRole("tab", { name: "Plays", exact: true }).click();
    const row = page.getByRole("button", { name: /Evening song/ });
    await expect(row).toContainText("River");
    await expect(row).toContainText("Started in 1.2 s");
    await expect(row).toContainText("Apple Music");
    await expect(row.locator("[data-slot=badge]")).toHaveClass(/badge-success/);
    await expect(row.locator("img")).toBeVisible();
    await expect(page.getByRole("group", { name: "Activity scope" })).toHaveCount(0);
    const folder = "test-results/shots-uiactivity";
    await mkdir(folder, { recursive: true });
    await page.screenshot({ path: `${folder}/plays-list-${width}-${theme}.png`, fullPage: true });
    await row.focus(); await page.keyboard.press("Enter");
    const steps = page.getByRole("list", { name: "Start steps" }).getByRole("listitem");
    await expect(steps).toHaveCount(2);
    await expect(steps.first()).toContainText("Choosing where to play");
    await expect(steps.last()).toContainText("Opening the source · Apple Music");
    await expect(steps.last()).toContainText("1 second");
    await page.getByRole("button", { name: "Copy diagnostics", exact: true }).click();
    await expect(page.getByText("Copied — paste it to your admin.", { exact: true })).toBeVisible();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(redacted);
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: `${folder}/plays-details-${width}-${theme}.png`, fullPage: true });
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await expect.poll(() => reads.length).toBeGreaterThan(1);
    await expect(row).toHaveAttribute("aria-expanded", "true");
  });
}
test("Plays defaults to Mine and lets an administrator choose Everyone", async ({ page }) => {
  await mockPlays(page, true, [first, trace("44444444-4444-4444-8444-444444444444", other)]);
  await page.goto("/#/activity?view=plays");
  await expect(page.getByRole("button", { name: "Mine", exact: true })).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByRole("button", { name: /Evening song/ })).toBeVisible();
  await expect(page.getByRole("button", { name: /Morning song/ })).toHaveCount(0);
  await page.getByRole("button", { name: "Everyone", exact: true }).click();
  await expect(page.getByRole("button", { name: /Morning song/ })).toContainText("Saved copy");
  await page.getByRole("button", { name: "Mine", exact: true }).click();
  await expect(page.getByRole("button", { name: /Morning song/ })).toHaveCount(0);
});
test("Plays explains an empty history", async ({ page }) => {
  await mockPlays(page, false, []);
  await page.goto("/#/activity?view=plays");
  await expect(page.getByText("Play something and it will show up here.", { exact: true })).toBeVisible();
});
test("Mine finds the listener’s plays after a page of household plays", async ({ page }) => {
  await mockPlays(page, true);
  const household = Array.from({ length: 50 }, (_, index) => trace(`55555555-5555-4555-8555-${String(index).padStart(12, "0")}`, other));
  await page.route("**/api/admin/play-traces?**", route => route.fulfill({ json: new URL(route.request().url()).searchParams.has("before") ? [first] : household }));
  await page.goto("/#/activity?view=plays");
  await expect(page.getByRole("button", { name: /Evening song/ })).toBeVisible();
  await expect(page.getByRole("button", { name: /Morning song/ })).toHaveCount(0);
  await expect(page.getByText("Play something and it will show up here.", { exact: true })).toHaveCount(0);
});
test("Diagnostics can be copied when the clipboard service is unavailable", async ({ page, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await mockPlays(page);
  await page.goto("/#/activity?view=plays");
  await page.getByRole("button", { name: /Evening song/ }).click();
  await page.evaluate(() => Object.defineProperty(navigator, "clipboard", { configurable: true, value: undefined }));
  await page.getByRole("button", { name: "Copy diagnostics", exact: true }).click();
  await expect(page.getByText("Copied — paste it to your admin.", { exact: true })).toBeVisible();
  expect(await page.evaluate(() => { Reflect.deleteProperty(navigator, "clipboard"); return navigator.clipboard.readText(); })).toBe(redacted);
});
