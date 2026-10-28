import { expect, test, type Page, type Route } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}

async function mockListeners(page: Page, administrator = true) {
  const writes: Record<string, unknown>[] = [];
  let locked = false, revision = "first", conflict = false, unavailable = false, gets = 0;
  await page.route("**/api/admin/**", async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/admin/auth/me") return json(route, { authenticated: true, backend: "Jellyfin",
      user: { id: "admin", name: "Alex", isAdministrator: administrator } });
    if (path === "/api/admin/auth/oidc/status") return json(route, { enabled: false });
    if (path === "/api/admin/onboarding/status") return json(route, { completed: true,
      shouldRedirectToSetup: false, migration: { available: false }, recoveryNotices: [] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    if (path === "/api/admin/ui/schema") return json(route, { configSections: [], providers: [] });
    if (path === "/api/admin/provider-accounts") return json(route, {
      audienceUsers: [{ id: "sam", displayName: "Sam" }, { id: "pat", displayName: "Pat" }], accounts: [] });
    if (path === "/api/admin/playlist-links") return json(route, { playlistLinks: [{
      id: "12345678-1234-1234-1234-123456789abc", name: "Road trip", targetBackendInstanceId: "main", targetPlaylistId: "original" }] });
    if (path.endsWith("/playlist-forks")) return json(route, path.includes("/sam/") ? [{
      id: "copy", name: "Sam's road trip", originalPlaylistId: "allstarr-vpl-12345678123412341234123456789abc",
      originalTargetPlaylistId: "original", backendInstanceId: "main", createdAt: "2026-10-28T14:00:00Z" }] : []);
    if (path.endsWith("/clean-only-lock")) {
      if (route.request().method() === "PUT") {
        const body = route.request().postDataJSON(); writes.push(body);
        if (unavailable) return json(route, { error: "Unavailable" }, 503);
        if (conflict) { locked = true; revision = "elsewhere"; return json(route, { error: "Changed" }, 409); }
        expect(body.expectedRevision).toBe(revision);
        locked = body.locked; revision = "saved";
      } else gets++;
      return json(route, { values: { explicitFilter: locked ? "CleanOnly" : "All" }, householdDefaults: {},
        usesHouseholdDefaults: false, revision, cleanOnlyLocked: path.includes("/sam/") && locked });
    }
    return json(route, {});
  });
  return { writes, conflict() { conflict = true; }, unavailable() { unavailable = true; }, get gets() { return gets; } };
}

test("administrator toggles lock and reloads a revision conflict", async ({ page }) => {
  const fixture = await mockListeners(page);
  await page.goto("/#/settings/listeners");
  const sam = page.getByRole("region", { name: "Sam", exact: true });
  const toggle = sam.getByRole("checkbox", { name: "Lock clean only", exact: true });
  await toggle.click();
  await expect(toggle).toBeChecked();
  expect(fixture.writes).toEqual([{ locked: true, expectedRevision: "first" }]);
  fixture.conflict();
  const before = fixture.gets;
  await toggle.click();
  await expect(page.getByRole("status").filter({ hasText: "Changed elsewhere — reloaded." })).toBeVisible();
  await expect(toggle).toBeChecked();
  expect(fixture.writes[1]).toEqual({ locked: false, expectedRevision: "saved" });
  expect(fixture.gets).toBeGreaterThan(before);
});

test("failed lock save preserves the saved choice", async ({ page }) => {
  const fixture = await mockListeners(page);
  await page.goto("/#/settings/listeners");
  const toggle = page.getByRole("region", { name: "Sam", exact: true }).getByRole("checkbox", { name: "Lock clean only", exact: true });
  await expect(toggle).toBeEnabled();
  fixture.unavailable();
  await toggle.click();
  await expect(page.getByRole("alert").filter({ hasText: "Listener settings could not be saved." })).toBeVisible();
  await expect(toggle).not.toBeChecked();
});

test("listeners page is restricted to administrators", async ({ page }) => {
  await mockListeners(page, false);
  await page.goto("/#/settings/listeners");
  await expect(page.getByRole("heading", { name: "This workspace is for administrators." })).toBeVisible();
  await expect(page.getByRole("checkbox", { name: "Lock clean only" })).toHaveCount(0);
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`personal copies and empty list at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockListeners(page);
    await page.goto("/#/settings/listeners");
    const sam = page.getByRole("region", { name: "Sam", exact: true });
    const pat = page.getByRole("region", { name: "Pat", exact: true });
    await sam.getByText("Playlist copies", { exact: true }).click();
    await pat.getByText("Playlist copies", { exact: true }).click();
    await expect(sam.getByText("Sam's road trip → Road trip", { exact: true })).toBeVisible();
    await expect(pat.getByText("No personal copies", { exact: true })).toBeVisible();
    await expect(sam.getByText("This listener can't turn it off.", { exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: `test-results/shots-uisettings/listeners-${width}-${theme}.png`, fullPage: true });
  });
}
