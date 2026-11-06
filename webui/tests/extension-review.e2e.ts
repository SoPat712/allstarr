import { expect, test } from "@playwright/test";
import type { ExtensionPackage } from "../src/lib/api";

for (const scenario of [
  { name: "catalog downloads turn on from one access review", update: false, running: false, needsReview: true },
  { name: "reviewed catalog downloads turn on in one step", update: false, running: false, needsReview: false },
  { name: "running extension updates use one access review", update: true, running: true, needsReview: true },
  { name: "reviewed updates can turn on from their row after startup fails", update: true, running: true, needsReview: true, retryFromRow: true },
  { name: "switched off extension updates save access and stay off", update: true, running: false, needsReview: true },
  { name: "updates with unchanged access turn on in one step", update: true, running: true, needsReview: false },
  { name: "switched off updates with unchanged access stay off", update: true, running: false, needsReview: false },
]) test(scenario.name, async ({ page }) => {
  const extension: ExtensionPackage = {
    id: "lumen-new", extensionId: "lumen-audio", displayName: "Lumen Audio", version: "1.0.0",
    lifecycle: scenario.needsReview ? "reviewrequired" : "staged", state: scenario.needsReview ? "reviewrequired" : "staged", active: false, installed: false,
    previousPackageId: scenario.update ? "lumen-old" : null, stagedAt: "2026-11-06",
    permissionReviewRequired: scenario.needsReview, hasPermissions: true, capabilities: ["metadata", "streaming"],
    revision: 1,
  };
  const previous: ExtensionPackage = { ...extension, id: "lumen-old", version: "0.9.0", previousPackageId: null,
    active: scenario.running, installed: true, state: scenario.running ? "active" : "disabled",
    permissionReviewRequired: false, stagedAt: "2026-11-05" };
  let saved: ExtensionPackage | null = null;
  let activations = 0;
  const reviews: unknown[] = [];
  const permission = {
    id: "lumen-network", permissionKind: "network", permissionValue: "https://music.example.test/",
    required: true, decision: scenario.needsReview ? "pending" : "approved",
  };
  const schema = {
    activeBackend: "Jellyfin", settings: [], priorityGroups: [],
    providers: [{ id: extension.extensionId, name: extension.displayName, categories: ["metadata", "streaming"] }],
  };

  await page.route("**/api/admin/**", async route => {
    const path = new URL(route.request().url()).pathname;
    let body: unknown = {};
    if (path === "/api/admin/auth/me") body = {
      authenticated: true, backend: "Jellyfin", backendConfigured: true,
      features: { intelligence: false }, user: { id: "operator", name: "Operator", isAdministrator: true },
    };
    else if (path === "/api/admin/onboarding/status") body = {
      completed: true, setupOpen: false, shouldRedirectToSetup: false, schemaVersion: "setup-v1",
      completedSteps: [], completionSource: "setup", revision: 1, recoveryNotices: [],
      migration: { available: false, completed: false, firstRun: false },
    };
    else if (path === "/api/admin/updates/stream") {
      await route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
      return;
    }
    else if (path === "/api/admin/ui/schema") body = schema;
    else if (path === "/api/admin/provider-accounts") body = { accounts: [], audienceUsers: [] };
    else if (path === "/api/admin/ui/provider-summaries") body = { providers: [] };
    else if (path === "/api/admin/providers/status") body = [];
    else if (path === "/api/admin/provider-diagnostics/deep-stream/latest") body = { measurements: [] };
    else if (path === "/api/admin/extensions/packages") body = [...(scenario.update ? [previous] : []), ...(saved ? [saved] : [])];
    else if (path === "/api/admin/extensions/registries") body = [];
    else if (path === "/api/admin/extensions/store") body = { items: saved ? [] : [{
      id: extension.extensionId, displayName: extension.displayName, version: extension.version,
      downloadUrl: "https://music.example.test/extension.zip", sha256: "a".repeat(64), types: extension.capabilities,
    }], errors: [] };
    else if (path === "/api/admin/extensions/install") {
      saved = { ...extension };
      body = { packageId: extension.id, message: "Download verified." };
    }
    else if (path.endsWith("/permissions")) body = [{ ...permission, decision: saved && !saved.permissionReviewRequired ? "approved" : permission.decision }];
    else if (path === "/api/admin/extensions/packages/lumen-new/review") {
      reviews.push(route.request().postDataJSON());
      saved = { ...extension, lifecycle: "staged", state: "staged", permissionReviewRequired: false, revision: 2 };
      body = saved;
    }
    else if (path === "/api/admin/extensions/packages/lumen-new/activate") {
      activations++;
      if ("retryFromRow" in scenario && scenario.retryFromRow && activations === 1) {
        await route.fulfill({ status: 503, json: { error: "Runtime could not start." } });
        return;
      }
      expect(route.request().postDataJSON()).toEqual({ expectedRevision: scenario.needsReview ? 2 : 1 });
      saved = { ...extension, lifecycle: "active", state: "active", active: true, installed: true,
        permissionReviewRequired: false, revision: 3 };
      body = saved;
    }
    else if (path.endsWith("/health-history")) body = { checks: [], counts: { passed: 0, failed: 0, neutral: 0 } };
    await route.fulfill({ json: body });
  });

  await page.goto("#/sources/services");
  if (scenario.update) {
    await page.getByRole("button", { name: "Update 0.9.0 → 1.0.0", exact: true }).click();
  } else {
    await page.getByRole("button", { name: "Add extension" }).click();
    const catalog = page.getByRole("dialog", { name: "Add an extension", exact: true });
    await catalog.getByRole("button", { name: "Download and review", exact: true }).click();
  }
  const review = page.getByRole("dialog", { name: "Review permissions", exact: true });
  await expect(review).toBeVisible();
  await expect(review.getByRole("heading", { name: "What it can do" })).toBeVisible();
  await expect(review.getByRole("list", { name: "Requested network hosts" })).toContainText("music.example.test");
  expect(activations).toBe(0);
  expect(reviews).toHaveLength(0);

  if (scenario.needsReview) await review.getByRole("button", { name: "Allow all", exact: true }).click();
  await review.getByRole("checkbox").check();
  const keepOff = scenario.update && !scenario.running;
  const label = scenario.needsReview ? keepOff ? "Approve, keep off" : scenario.update ? "Approve and update" : "Approve and turn on" : scenario.update ? "Update" : "Turn on";
  await review.getByRole("button", { name: label, exact: true }).click();
  const retryFromRow = "retryFromRow" in scenario && scenario.retryFromRow;
  if (retryFromRow) {
    await expect(review.getByRole("alert")).toHaveText("Runtime could not start.");
    await expect(review.getByRole("button", { name: "Try again", exact: true })).toBeEnabled();
    await review.getByRole("button", { name: "Close review", exact: true }).click();
    await page.locator(".extension-row").getByRole("button", { name: "Enable", exact: true }).click();
    const turnOn = page.getByRole("alertdialog", { name: "Turn on Lumen Audio?", exact: true });
    await expect(turnOn.getByRole("list", { name: "Approved network hosts" })).toContainText("music.example.test");
    await turnOn.getByRole("button", { name: "Turn on", exact: true }).click();
    await expect(turnOn).toBeHidden();
  }
  await expect(review).toBeHidden();
  await expect(page.getByRole("alertdialog")).toHaveCount(0);
  expect(reviews).toEqual(scenario.needsReview ? [{ expectedRevision: 1, decisions: [{ kind: "network", value: permission.permissionValue, approved: true }] }] : []);
  expect(activations).toBe(keepOff ? 0 : retryFromRow ? 2 : 1);
  if (keepOff) {
    await expect(page.getByRole("status").filter({ hasText: "Lumen Audio updated. It stays off." })).toBeVisible();
    await expect(page.locator(".extension-row").getByRole("button", { name: "Enable", exact: true })).toBeVisible();
    return;
  }
  const notification = page.getByRole("status").filter({ hasText: scenario.update ? "Lumen Audio updated and enabled." : "Lumen Audio installed and enabled." });
  await expect(notification).toBeVisible();
  await notification.getByRole("button", { name: "Open source", exact: true }).click();
  await expect(page).toHaveURL(/#\/sources\/services\/lumen-audio$/);
  expect(await page.pageErrors()).toEqual([]);
});
