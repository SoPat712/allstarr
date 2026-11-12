import { expect, test } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";
import { responses, routeRelease, mockApi } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

test("Durable onboarding controls first setup and targeted recovery", async ({ page }) => {
  await mockApi(page);
  let completed = false;
  let recovery = false;
  let releaseStatus!: () => void;
  const initialStatus = new Promise<void>((resolve) => { releaseStatus = resolve; });
  let waitForInitialStatus = true;
  await page.route("**/api/admin/onboarding/status", async (route) => {
    if (waitForInitialStatus) {
      waitForInitialStatus = false;
      await initialStatus;
    }
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        completed, setupOpen: false, shouldRedirectToSetup: !completed,
        schemaVersion: "onboarding-v1",
        completedSteps: completed ? ["backend-identity"] : [],
        completionSource: completed ? "setup-guide" : "none",
        completedAt: completed ? "2026-01-01" : null,
        revision: completed ? 2 : 0,
        recoveryNotices: recovery ? ["backend_identity_missing"] : [],
        migration: { available: true, completed: false, firstRun: true },
      }),
    });
  });
  await page.route("**/api/admin/onboarding/complete", (route) => {
    completed = true;
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        completed: true, setupOpen: false, shouldRedirectToSetup: false,
        schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"],
        completionSource: "setup-guide", completedAt: "2026-01-01", revision: 2,
        recoveryNotices: [],
        migration: { available: true, completed: false, firstRun: true },
      }),
    });
  });

  const navigation = page.goto("#/");
  await expect(page.getByRole("heading", { name: "Bringing your music universe online" })).toBeVisible();
  await expect(page.getByRole("status")).toHaveText("Loading Allstarr. Preparing your music control center.");
  await expect(page.locator("[role=progressbar]")).toHaveCount(0);
  releaseStatus();
  await navigation;
  const setup = page.getByRole("region", { name: "Set up Allstarr" });
  await expect(setup).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(setup).toBeVisible();
  await setup.getByRole("button", { name: "Finish setup" }).click();
  await expect(setup).toBeHidden();

  await page.evaluate(() => localStorage.clear());
  await page.reload();
  await expect(setup).toBeHidden();

  recovery = true;
  await page.reload();
  await expect(page.getByText("Media server connection needs attention.")).toBeVisible();
  await expect(page.getByText(/review the media server connection/i)).toBeVisible();
  await expect(setup).toBeHidden();
});

test("Signal boot exposes a retryable bootstrap failure", async ({ page }) => {
  const failing = ["/api/admin/auth/me"];
  await mockApi(page, { fail: failing });
  await page.goto("#/");

  await expect(page.getByRole("heading", { name: "Allstarr could not start." })).toBeVisible();
  await expect(page.getByRole("alert")).toContainText("Fixture unavailable");
  failing.length = 0;
  await page.getByRole("button", { name: "Try again" }).click();
  await expect(page.getByRole("heading", { name: "Home", level: 1 })).toBeVisible();
});

test("authentication operations expose pending state and recover from sign-out failure", async ({ page }) => {
  await mockApi(page);
  const login = routeRelease();
  await page.route("**/api/admin/auth/me", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({ authenticated: false, backend: "Jellyfin", user: null }),
  }));
  await page.route("**/api/admin/auth/login", async (route) => {
    await login.promise;
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify(responses["/api/admin/auth/me"]),
    });
  });
  await page.route("**/api/admin/auth/logout", (route) => route.fulfill({
    status: 503,
    contentType: "application/json",
    body: JSON.stringify({ error: "The media server did not confirm sign out." }),
  }));
  await page.goto("#/");
  await page.getByLabel("Username").fill("tester");
  await page.getByLabel("Password").fill("secret");
  await page.getByRole("button", { name: "Sign in" }).click();

  await expect(page.getByRole("button", { name: "Signing in…" })).toBeDisabled();
  await expect(page.getByLabel("Username")).toBeDisabled();
  await expect(page.getByLabel("Password")).toBeDisabled();
  login.release();
  await expect(page.getByRole("heading", { name: "Home", level: 1 })).toBeVisible();

  await page.getByRole("button", { name: "Sign out" }).click();
  const signOutError = page.getByRole("alert");
  await expect(signOutError).toContainText("Sign out failed.");
  await expect(signOutError).toContainText("The media server did not confirm sign out.");
  await expect(page.getByRole("button", { name: "Sign out", exact: true })).toBeEnabled();
  await page.getByRole("button", { name: "Dismiss sign out error" }).click();
  await expect(signOutError).toBeHidden();
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Media-server access warning and confirmed restore — ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.addInitScript((value) => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    let restored = false;
    let applied = 0;
    let fullPreviews = 0;
    await page.route("**/api/admin/access/backend", (route) => route.fulfill({ json: {
      settings: { supported: true, connectionSaved: true, configured: restored, needsReview: !restored, credentialKind: restored ? "api-key" : "", revision: "fixture" }, csrfToken: "fixture",
    } }));
    await page.route("**/api/admin/config/migration/status", (route) => route.fulfill({ json: {
      available: true, completed: true, firstRun: false, sourcePresent: true, serviceAccessAvailable: !restored,
    } }));
    await page.route("**/api/admin/config/migration/preview-mounted", (route) => { fullPreviews++; return route.fulfill({ json: {} }); });
    await page.route("**/api/admin/config/migration/preview-service-access", (route) => route.fulfill({ json: {
      previewToken: "access-only", revision: "fixture", canApply: true, items: [],
      backend: { backend: "Jellyfin", endpoint: "https://media.example.test", savesConnection: false, serviceAccessAction: "verify_then_encrypt" },
    } }));
    await page.route("**/api/admin/config/migration/apply", async (route) => {
      expect(route.request().postDataJSON()).toEqual({ previewToken: "access-only", revision: "fixture", confirmed: true });
      applied++; restored = true;
      return route.fulfill({ json: { success: true, alreadyApplied: false, backendServiceAccess: "verified", requiresRestart: true } });
    });
    await page.route("**/health", (route) => route.fulfill({ json: { status: "Healthy" } }));
    await page.goto("#/");
    const warning = page.getByRole("status").filter({ hasText: "Media-server access needs attention." });
    await expect(warning).toHaveCount(1);
    await expect(warning).toContainText("playlist sync and library scans");
    await expect.poll(() => warning.evaluate((element) => {
      const parent = element.getBoundingClientRect();
      return [...element.children].every((child) => {
        const rect = child.getBoundingClientRect();
        return rect.left >= parent.left && rect.right <= parent.right;
      });
    })).toBe(true);
    const link = warning.getByRole("link", { name: "Open media-server access" });
    await link.focus();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/#\/settings\/people$/);
    const restore = page.getByRole("region", { name: "Restore legacy media-server access" });
    await expect(restore).toBeVisible();
    expect(applied).toBe(0);
    await expect(restore).toContainText("verify its administrator permissions, encrypt it");
    await expectContainedMobileGeometry(page);
    await page.screenshot({ path: `test-results/service-access-${width}-${theme}.png`, fullPage: true });
    const button = restore.getByRole("button", { name: "Verify and restore access" });
    await button.focus();
    await page.keyboard.press("Enter");
    await expect.poll(() => applied).toBe(1);
    await expect(warning).toHaveCount(0);
    expect(fullPreviews).toBe(0);
    await expect(restore).toBeHidden();
  });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Media-server access confirms a restore whose reply is cut by the restart — ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.addInitScript((value) => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    let restored = false;
    await page.route("**/api/admin/access/backend", (route) => route.fulfill({ json: {
      settings: { supported: true, connectionSaved: true, configured: restored, needsReview: !restored, credentialKind: restored ? "api-key" : "", revision: "fixture" }, csrfToken: "fixture",
    } }));
    await page.route("**/api/admin/config/migration/status", (route) => route.fulfill({ json: {
      available: true, completed: true, firstRun: false, sourcePresent: true, serviceAccessAvailable: !restored,
    } }));
    await page.route("**/api/admin/config/migration/preview-service-access", (route) => route.fulfill({ json: {
      previewToken: "access-only", revision: "fixture", canApply: true, items: [],
      backend: { backend: "Jellyfin", endpoint: "https://media.example.test", savesConnection: false, serviceAccessAction: "verify_then_encrypt" },
    } }));
    await page.route("**/api/admin/config/migration/apply", (route) => { restored = true; return route.abort("connectionreset"); });
    await page.route("**/health", (route) => route.fulfill({ json: { status: "Healthy" } }));
    await page.goto("#/settings/access");
    const restore = page.getByRole("region", { name: "Restore legacy media-server access" });
    await restore.getByRole("button", { name: "Verify and restore access" }).click();
    const access = page.getByRole("article", { name: "Media-server access" });
    await expect(access.getByRole("status")).toContainText("Media-server access is restored.");
    await expect(access.getByText("Connected", { exact: true })).toBeVisible();
    await expect(access.getByText("Administrator access enables playlist order and removal sync.")).toBeVisible();
    await expect(restore).toBeHidden();
    await expect(page.getByRole("alert")).toHaveCount(0);
    await page.screenshot({ path: `test-results/service-access-restored-${width}-${theme}.png`, fullPage: true });
    await access.getByRole("button", { name: "Change access" }).click();
    const username = access.getByLabel("Jellyfin administrator username");
    await expect(username).toBeVisible();
    await expect(access.getByLabel("Jellyfin administrator password")).toBeVisible();
    expect(await username.evaluate((input) => {
      const style = getComputedStyle(input);
      return style.borderTopStyle !== "none" && parseFloat(style.borderTopWidth) >= 1 &&
        style.borderTopColor !== getComputedStyle(input.closest("article")!).backgroundColor;
    })).toBe(true);
    await expectContainedMobileGeometry(page);
    await page.screenshot({ path: `test-results/service-access-change-${width}-${theme}.png`, fullPage: true });
    await access.getByRole("button", { name: "Cancel" }).click();
    await expect(access.getByRole("button", { name: "Change access" })).toBeVisible();
  });
}

test("Media-server administrator access keeps the connected card concise", async ({ page }) => {
  await mockApi(page);
  await page.route("**/api/admin/access/backend", (route) => route.fulfill({ json: {
    settings: { supported: true, connectionSaved: true, configured: true, needsReview: false, credentialKind: "user-token", revision: "fixture" }, csrfToken: "fixture",
  } }));
  await page.goto("#/settings/access");
  const access = page.getByRole("article", { name: "Media-server access" });
  await expect(access.getByText("Connected", { exact: true })).toBeVisible();
  await expect(access.getByText("Save administrator credentials to enable full playlist order and removal sync.")).toHaveCount(0);
  await access.getByRole("button", { name: "Change access" }).click();
  await expect(access.getByLabel("Jellyfin administrator username")).toBeVisible();
  await expect(access.getByLabel("Jellyfin administrator password")).toBeVisible();
});
