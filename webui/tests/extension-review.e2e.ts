import { expect, test } from "@playwright/test";
import type { ExtensionPackage } from "../src/lib/api";

test("catalog downloads wait for access review and explicit activation", async ({ page }) => {
  const extension: ExtensionPackage = {
    id: "lumen-new", extensionId: "lumen-audio", displayName: "Lumen Audio", version: "1.0.0",
    lifecycle: "reviewrequired", state: "reviewrequired", active: false, installed: false,
    permissionReviewRequired: true, hasPermissions: true, capabilities: ["metadata", "streaming"],
    revision: 1,
  };
  let saved: ExtensionPackage | null = null;
  let activations = 0;
  const reviews: unknown[] = [];
  const permission = {
    id: "lumen-network", permissionKind: "network", permissionValue: "https://music.example.test/",
    required: true, decision: "pending",
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
    else if (path === "/api/admin/extensions/packages") body = saved ? [saved] : [];
    else if (path === "/api/admin/extensions/registries") body = [];
    else if (path === "/api/admin/extensions/store") body = { items: saved ? [] : [{
      id: extension.extensionId, displayName: extension.displayName, version: extension.version,
      downloadUrl: "https://music.example.test/extension.zip", sha256: "a".repeat(64), types: extension.capabilities,
    }], errors: [] };
    else if (path === "/api/admin/extensions/install") {
      saved = { ...extension };
      body = { packageId: extension.id, message: "Download verified." };
    }
    else if (path === "/api/admin/extensions/packages/lumen-new/permissions") body = [permission];
    else if (path === "/api/admin/extensions/packages/lumen-new/review") {
      reviews.push(route.request().postDataJSON());
      saved = { ...extension, lifecycle: "staged", state: "staged", permissionReviewRequired: false, revision: 2 };
      body = saved;
    }
    else if (path === "/api/admin/extensions/packages/lumen-new/activate") {
      activations++;
      saved = { ...extension, lifecycle: "active", state: "active", active: true, installed: true,
        permissionReviewRequired: false, revision: 3 };
      body = saved;
    }
    else if (path.endsWith("/health-history")) body = { checks: [], counts: { passed: 0, failed: 0, neutral: 0 } };
    await route.fulfill({ json: body });
  });

  await page.goto("#/sources/services");
  await page.getByRole("button", { name: "Add extension" }).click();
  const catalog = page.getByRole("dialog", { name: "Add an extension", exact: true });
  await catalog.getByRole("button", { name: "Download and review", exact: true }).click();
  const review = page.getByRole("dialog", { name: "Review permissions", exact: true });
  await expect(review).toBeVisible();
  await expect(review.getByRole("heading", { name: "What it can do" })).toBeVisible();
  await expect(review.getByRole("list", { name: "Requested network hosts" })).toContainText("music.example.test");
  expect(activations).toBe(0);
  expect(reviews).toHaveLength(0);

  await review.getByRole("button", { name: "Allow all", exact: true }).click();
  await review.getByRole("checkbox").check();
  await review.getByRole("button", { name: "Approve", exact: true }).click();
  const activation = page.getByRole("alertdialog", { name: "Activate Lumen Audio?", exact: true });
  await expect(activation).toBeVisible();
  expect(activations).toBe(0);
  expect(reviews).toEqual([{ expectedRevision: 1, decisions: [{ kind: "network", value: permission.permissionValue, approved: true }] }]);
  await activation.getByRole("button", { name: "Activate extension", exact: true }).click();
  await expect.poll(() => activations).toBe(1);
  const notification = page.getByRole("status").filter({ hasText: "Lumen Audio installed and enabled." });
  await expect(notification).toBeVisible();
  await notification.getByRole("button", { name: "Open source", exact: true }).click();
  await expect(page).toHaveURL(/#\/sources\/services\/lumen-audio$/);
  expect(await page.pageErrors()).toEqual([]);
});
