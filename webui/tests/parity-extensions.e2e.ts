import { expect, test } from "@playwright/test";
import { schema, routeRelease, mockApi, mockSignedSessionExtension } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

test("extension review scrolls long permission lists", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 720 });
  await mockApi(page);
  const reviewPackage = {
    extensionId: "lumen-audio", displayName: "Lumen Audio", lifecycle: "disabled", id: "new", version: "1.0.0",
    state: "reviewRequired", active: false, installed: true, permissionReviewRequired: true,
    capabilities: ["metadata", "streaming"], revision: 1, stagedAt: "2026-02-01",
  };
  await page.route("**/api/admin/extensions/packages", (route) => route.fulfill({
    contentType: "application/json", body: JSON.stringify([reviewPackage]),
  }));
  await page.route("**/packages/new/permissions", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify(Array.from({ length: 14 }, (_, index) => ({
      id: `network-${index}`, permissionKind: "network", permissionValue: `https://host-${index}.example.test/`,
      required: true, decision: "pending",
    }))),
  }));

  await page.goto("#/settings/extensions");
  await page.getByRole("list", { name: "Extensions", exact: true }).getByRole("button", { name: "Review", exact: true }).click();
  const review = page.getByRole("dialog", { name: "Review permissions" });
  await expect(review).toBeVisible();
  const body = review.locator(".extension-review-body");
  await body.hover();
  await page.mouse.wheel(0, 2000);
  await expect.poll(() => body.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
  await expect(review.getByText("Connect to host-13.example.test")).toBeInViewport();

  for (const name of ["Allow all", "Deny all"]) {
    const borderColor = await review.getByRole("button", { name }).evaluate((element) => getComputedStyle(element).borderTopColor);
    expect(borderColor).not.toBe("rgba(0, 0, 0, 0)");
  }
  await review.getByRole("button", { name: "Allow all" }).click();
  await review.getByRole("checkbox").click();
  await expect(review.getByRole("button", { name: "Approve and turn on" })).toBeEnabled();
});

test("extension updates can be reviewed together", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await mockApi(page);
  const extension = (id: string, extensionId: string, displayName: string, version: string, state: string, extra = {}) => ({
    id, extensionId, displayName, version, state, lifecycle: state, active: state === "active", installed: true,
    permissionReviewRequired: state === "reviewRequired", capabilities: ["metadata"], revision: 1,
    stagedAt: "2026-01-01", ...extra,
  });
  const packages = [
    extension("lumen-old", "lumen-audio", "Lumen Audio", "1.0.0", "active"),
    extension("pulse-old", "pulse-lyrics", "Pulse Lyrics", "1.0.0", "disabled"),
    extension("nova", "nova-art", "Nova Art", "0.1.0", "reviewRequired"),
  ];
  const updates: Record<string, ReturnType<typeof extension>> = {
    "lumen-audio": extension("lumen-new", "lumen-audio", "Lumen Audio", "2.0.0", "reviewRequired", { previousPackageId: "lumen-old", stagedAt: "2026-02-01" }),
    "pulse-lyrics": extension("pulse-new", "pulse-lyrics", "Pulse Lyrics", "1.1.0", "reviewRequired", { previousPackageId: "pulse-old", stagedAt: "2026-02-01" }),
  };
  await page.route("**/api/admin/extensions/packages", (route) => route.fulfill({ contentType: "application/json", body: JSON.stringify(packages) }));
  await page.route("**/api/admin/extensions/store", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      items: Object.values(updates).map((item) => ({
        id: item.extensionId, displayName: item.displayName, version: item.version,
        downloadUrl: `https://example.test/${item.extensionId}.zip`, sha256: "a".repeat(64), registryId: null, types: ["metadata"],
      })),
      errors: [],
    }),
  }));
  await page.route("**/api/admin/extensions/install", (route) => {
    const update = updates[route.request().postDataJSON().id];
    packages.push(update);
    return route.fulfill({ contentType: "application/json", body: JSON.stringify({ packageId: update.id, message: "Staged." }) });
  });
  const permission = (kind: string, value: string) => ({ id: `${kind}-${value}`, permissionKind: kind, permissionValue: value, required: true, decision: "pending" });
  const network = permission("network", "https://api.example.test/");
  const access: Record<string, unknown[]> = {
    "lumen-old": [network], "lumen-new": [network, permission("secret", "accountToken")],
    "pulse-old": [network], "pulse-new": [network], nova: [permission("cache", "metadata")],
  };
  const reviewed: string[] = [];
  const activated: string[] = [];
  await page.route("**/api/admin/extensions/packages/*/*", (route) => {
    const [id, step] = new URL(route.request().url()).pathname.split("/").slice(-2);
    const item = packages.find((entry) => entry.id === id)!;
    if (step === "permissions") return route.fulfill({ contentType: "application/json", body: JSON.stringify(access[id]) });
    (step === "review" ? reviewed : activated).push(id);
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ ...item, state: step === "review" ? "staged" : "active", permissionReviewRequired: false }),
    });
  });

  await page.goto("#/settings/extensions");
  const compactRows = page.getByRole("list", { name: "Extensions", exact: true }).getByRole("listitem");
  await expect(compactRows).toHaveCount(3);
  for (const row of await compactRows.all()) {
    await expect(row.locator(".item-row")).toHaveCount(1);
    await expect(row.locator(".row-controls > [data-slot=button]")).toHaveCount(1);
    await expect(row.locator("input, .extension-session, .source-row-capabilities")).toHaveCount(0);
  }
  await page.getByRole("button", { name: "Update and review all (3)" }).click();
  const review = page.getByRole("dialog", { name: "Update and review extensions" });
  const rows = review.getByRole("list", { name: "Extensions to approve" }).locator(":scope > li");
  await expect(rows).toHaveCount(3);
  await expect(rows.nth(0)).toContainText("1.0.0 → 2.0.0");
  await expect(rows.nth(0)).toContainText("New access");
  await expect(rows.nth(1)).toContainText("No new access");
  await expect(rows.nth(2)).toContainText("Version 0.1.0");
  await expect(review.getByRole("checkbox", { name: "Approve and turn on Pulse Lyrics" })).not.toBeChecked();
  await review.getByRole("checkbox", { name: "Approve and turn on Nova Art" }).click();
  await expect(review.getByRole("button", { name: "Approve 1" })).toBeDisabled();
  await review.getByRole("checkbox", { name: "Approve and turn on Nova Art" }).click();
  await review.getByText("I understand what these extensions can access.").click();
  await review.getByRole("button", { name: "Approve 2" }).click();
  await expect(review).toBeHidden();
  expect(reviewed).toEqual(["lumen-new", "nova"]);
  expect(activated).toEqual(["lumen-new", "nova"]);
});

test("extension updates explain access changes on mobile", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  const basePackage = {
    extensionId: "lumen-audio", displayName: "Lumen Audio", lifecycle: "disabled",
    active: false, installed: true, permissionReviewRequired: false, capabilities: ["metadata"],
    revision: 1,
  };
  await page.route("**/api/admin/extensions/packages", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([
      { ...basePackage, id: "old", version: "1.0.0", state: "active", active: true, stagedAt: "2026-01-01" },
      {
        ...basePackage, id: "new", version: "2.0.0", state: "reviewRequired",
        previousPackageId: "old", permissionReviewRequired: true,
        capabilities: ["metadata", "streaming"], stagedAt: "2026-02-01",
      },
    ]),
  }));
  const permission = (id: string, kind: string, value: string) => ({
    id, permissionKind: kind, permissionValue: value, required: true, decision: "pending",
  });
  await page.route("**/packages/old/permissions", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([permission("old-network", "network", "https://api.example.test/"), permission("old-cache", "cache", "metadata")]),
  }));
  await page.route("**/packages/new/permissions", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([permission("new-network", "network", "https://api.example.test/"), permission("new-secret", "secret", "accountToken")]),
  }));
  let reviewRequests = 0;
  await page.route("**/packages/new/review", (route) => {
    reviewRequests++;
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        ...basePackage, id: "new", version: "2.0.0", state: "staged",
        previousPackageId: "old", active: false, revision: 2,
      }),
    });
  });
  let cancellationRequests = 0;
  await page.route("**/packages/new/staging/cancel", (route) => {
    cancellationRequests++;
    return route.fulfill({ status: 410 });
  });
  let activationRequests = 0;
  let releaseActivation = () => {};
  const activationResponse = new Promise<void>((resolve) => {
    releaseActivation = resolve;
  });
  await page.route("**/packages/new/activate", async (route) => {
    activationRequests++;
    if (activationRequests === 1) {
      await activationResponse;
      return route.fulfill({
        status: 500,
        contentType: "application/json",
        body: JSON.stringify({ error: "Runtime failed to start." }),
      });
    }
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        ...basePackage, id: "new", version: "2.0.0", state: "active",
        active: true, previousPackageId: "old", revision: 3,
      }),
    });
  });

  await page.goto("#/settings/extensions");
  await page.getByRole("list", { name: "Extensions", exact: true }).getByRole("button", { name: "Review", exact: true }).click();
  let review = page.getByRole("dialog", { name: "Review permissions" });
  await expect(review).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(review).toBeHidden();
  await page.getByRole("list", { name: "Extensions", exact: true }).getByRole("button", { name: "Review", exact: true }).click();
  review = page.getByRole("dialog", { name: "Review permissions" });
  await review.getByRole("button", { name: "Close review" }).click();
  await expect(review).toBeHidden();
  expect(cancellationRequests).toBe(0);
  await page.getByRole("list", { name: "Extensions", exact: true }).getByRole("button", { name: "Review", exact: true }).click();
  review = page.getByRole("dialog", { name: "Review permissions" });
  await expect(review.getByText("Update from 1.0.0 to 2.0.0")).toBeVisible();
  await expect(review.getByText("New access", { exact: false })).toBeVisible();
  await expect(review.getByText("Removed access", { exact: false })).toBeVisible();
  await expect(review.getByRole("button", { name: "Approve and update" })).toBeInViewport();
  await review.getByRole("button", { name: "Allow all" }).click();
  await review.getByRole("checkbox").check();
  await review.getByRole("button", { name: "Approve and update" }).click();
  await expect(page.getByRole("alertdialog")).toHaveCount(0);
  await expect(review.getByRole("button", { name: "Updating…" })).toBeDisabled();
  await expect.poll(() => activationRequests).toBe(1);
  releaseActivation();
  await expect(review.getByRole("alert")).toHaveText("Runtime failed to start.");
  await review.getByRole("button", { name: "Try again" }).click();
  await expect(review).toBeHidden();
  expect(activationRequests).toBe(2);
  expect(reviewRequests).toBe(1);
});

test("failed extension packages can be retried without automatic activation", async ({ page }) => {
  await mockApi(page);
  let packageResponse: Record<string, unknown> = {
    id: "failed", extensionId: "failed-audio", displayName: "Failed Audio", version: "1.0.0",
    lifecycle: "failed", state: "failed", active: false, installed: false,
    permissionReviewRequired: false, hasPermissions: false, capabilities: ["streaming"],
    failureCode: "runtime_validation_failed", stagedAt: "2026-01-01", revision: 4,
  };
  let retryRequests = 0;
  let activationRequests = 0;
  await page.route("**/api/admin/extensions/packages", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([packageResponse]),
  }));
  await page.route("**/api/admin/extensions/packages/failed/retry", async (route) => {
    expect(route.request().method()).toBe("POST");
    expect(route.request().postDataJSON()).toEqual({ expectedRevision: 4 });
    retryRequests++;
    packageResponse = {
      ...packageResponse,
      lifecycle: "reviewrequired", state: "reviewrequired", permissionReviewRequired: true,
      hasPermissions: true, failureCode: null, revision: 5,
    };
    await route.fulfill({ contentType: "application/json", body: JSON.stringify(packageResponse) });
  });
  await page.route("**/api/admin/extensions/packages/failed/activate", async (route) => {
    activationRequests++;
    await route.fulfill({ status: 500, contentType: "application/json", body: JSON.stringify({ error: "activation must wait for review" }) });
  });

  await page.goto("#/integrations/extensions");
  const row = page.getByRole("list", { name: "Extensions", exact: true }).getByRole("listitem");
  await expect(row).toContainText("The extension failed runtime validation before it could start.");
  await row.getByRole("button", { name: "Manage extension" }).click();
  await expect(page.getByRole("menuitem", { name: "Retry" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Uninstall" })).toBeVisible();
  await page.keyboard.press("Escape");
  await row.getByRole("button", { name: "Retry" }).click();
  await expect.poll(() => retryRequests).toBe(1);
  await expect(row.getByRole("button", { name: "Review", exact: true })).toBeVisible();
  await expect(page.getByRole("status").filter({ hasText: "Review permissions before enabling" })).toBeVisible();
  expect(activationRequests).toBe(0);
});

test("disabled extensions with permissions require a fresh review before enabling", async ({ page }) => {
  await mockApi(page);
  let packageResponse: Record<string, unknown> = {
    id: "disabled", extensionId: "lumen-audio", displayName: "Lumen Audio", version: "1.0.0",
    lifecycle: "disabled", state: "disabled", active: false, installed: true,
    permissionReviewRequired: false, hasPermissions: true, capabilities: ["streaming"],
    stagedAt: "2026-01-01", revision: 3,
  };
  const writes: string[] = [];
  await page.route("**/api/admin/extensions/packages", (route) => route.fulfill({
    contentType: "application/json", body: JSON.stringify([packageResponse]),
  }));
  await page.route("**/api/admin/extensions/packages/disabled/permissions/revoke", async (route) => {
    writes.push("revoke");
    expect(route.request().postDataJSON()).toEqual({ expectedRevision: 3 });
    packageResponse = {
      ...packageResponse,
      lifecycle: "reviewrequired", state: "reviewrequired", permissionReviewRequired: true,
      active: false, installed: false, revision: 4,
    };
    await route.fulfill({ contentType: "application/json", body: JSON.stringify(packageResponse) });
  });
  await page.route("**/api/admin/extensions/packages/disabled/permissions", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([{ id: "permission", permissionKind: "network", permissionValue: "https://example.test/", required: true, decision: "approved" }]),
  }));

  await page.route("**/packages/disabled/review", route => {
    writes.push("review");
    expect(route.request().postDataJSON()).toEqual({ expectedRevision: 4, decisions: [{ kind: "network", value: "https://example.test/", approved: true }] });
    packageResponse = { ...packageResponse, state: "staged", permissionReviewRequired: false, revision: 5 };
    return route.fulfill({ json: packageResponse });
  });
  await page.route("**/packages/disabled/activate", route => {
    writes.push("activate");
    expect(route.request().postDataJSON()).toEqual({ expectedRevision: 5 });
    packageResponse = { ...packageResponse, state: "active", active: true, installed: true, revision: 6 };
    return route.fulfill({ json: packageResponse });
  });

  await page.goto("#/integrations/extensions");
  const row = page.getByRole("list", { name: "Extensions", exact: true }).getByRole("listitem");
  await expect(row.getByRole("button", { name: "Enable", exact: true })).toBeVisible();
  await row.getByRole("button", { name: "Enable", exact: true }).click();
  const review = page.getByRole("dialog", { name: "Review permissions", exact: true });
  await expect(review).toBeVisible();
  await expect(page.getByRole("alertdialog")).toHaveCount(0);
  expect(writes).toEqual([]);
  await expect(review.getByRole("button", { name: "Allow", exact: true })).toHaveAttribute("aria-pressed", "true");
  await review.getByRole("checkbox").check();
  await review.getByRole("button", { name: "Approve and turn on", exact: true }).click();
  await expect(review).toBeHidden();
  expect(writes).toEqual(["revoke", "review", "activate"]);
});

test("activation closes its confirmation before a delayed extension refresh", async ({ page }) => {
  await mockApi(page);
  const initial = {
    id: "staged", extensionId: "lumen-audio", displayName: "Lumen Audio", version: "1.0.0",
    lifecycle: "staged", state: "staged", active: false, installed: false,
    permissionReviewRequired: false, hasPermissions: false, capabilities: ["streaming"],
    stagedAt: "2026-01-01", revision: 1,
  };
  let packageGets = 0;
  const refresh = routeRelease();
  await page.route("**/api/admin/extensions/packages", async (route) => {
    packageGets++;
    if (packageGets > 1) await refresh.promise;
    await route.fulfill({ contentType: "application/json", body: JSON.stringify([initial]) });
  });
  let activationRequests = 0;
  await page.route("**/api/admin/extensions/packages/staged/activate", (route) => {
    activationRequests++;
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ ...initial, lifecycle: "active", state: "active", active: true, installed: true, revision: 2 }),
    });
  });

  await page.goto("#/integrations/extensions");
  const row = page.getByRole("list", { name: "Extensions", exact: true }).getByRole("listitem");
  await row.getByRole("button", { name: "Enable" }).click();
  const activation = page.getByRole("alertdialog", { name: "Turn on Lumen Audio?" });
  await activation.getByRole("button", { name: "Turn on" }).click();
  await expect.poll(() => activationRequests).toBe(1);
  await expect(activation).toBeHidden();
  await expect(page.getByText("Lumen Audio installed and enabled.", { exact: true })).toBeVisible();
  await expect(page.locator('[role="alert"]')).toHaveCount(0);
  refresh.release();
});

test("uninstall keeps refresh failures on the page after its dialog closes", async ({ page }) => {
  await mockApi(page);
  await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
    ...schema, providers: schema.providers.map(provider => provider.id === "lumen-audio" ? { ...provider, implementationOrigin: "extension" } : provider),
  } }));
  const initial = {
    id: "package", extensionId: "lumen-audio", displayName: "Lumen Audio", version: "1.0.0",
    lifecycle: "active", state: "active", active: true, installed: true,
    permissionReviewRequired: false, hasPermissions: false, capabilities: ["streaming"],
    stagedAt: "2026-01-01", revision: 1,
  };
  let uninstalled = false;
  await page.route("**/api/admin/extensions/packages", async (route) => {
    if (uninstalled) {
      await route.fulfill({ status: 503, contentType: "application/json", body: JSON.stringify({ error: "fixture unavailable" }) });
      return;
    }
    await route.fulfill({ contentType: "application/json", body: JSON.stringify([initial]) });
  });
  let uninstallRequests = 0;
  await page.route("**/api/admin/extensions/packages/package", async (route) => {
    expect(route.request().method()).toBe("DELETE");
    uninstallRequests++;
    uninstalled = true;
    await route.fulfill({ contentType: "application/json", body: JSON.stringify({ ...initial, state: "uninstalled", active: false, installed: false, revision: 2 }) });
  });

  await page.goto("#/integrations/services/lumen-audio");
  await page.getByRole("button", { name: "Remove extension…", exact: true }).click();
  const uninstall = page.getByRole("alertdialog", { name: "Uninstall Lumen Audio?" });
  await uninstall.getByRole("button", { name: "Uninstall" }).click();
  await expect.poll(() => uninstallRequests).toBe(1);
  await expect(uninstall).toBeHidden();
  await expect(page.locator(".degraded-banner").filter({ hasText: "Some extension data is unavailable." })).toBeVisible();
  await expect(page.locator('[role="alert"]')).toHaveCount(0);
});

test("removing a registry closes its confirmation before a delayed refresh", async ({ page }) => {
  await mockApi(page);
  const refresh = routeRelease();
  let registryGets = 0;
  await page.route("**/api/admin/extensions/registries", async (route) => {
    registryGets++;
    if (registryGets > 1) await refresh.promise;
    await route.fulfill({
      contentType: "application/json",
      body: JSON.stringify([{ id: "registry", name: "Community", registryUrl: "https://example.test/registry.json", enabled: true, revision: 1 }]),
    });
  });
  let removeRequests = 0;
  await page.route("**/api/admin/extensions/registries/registry*", async (route) => {
    expect(route.request().method()).toBe("DELETE");
    removeRequests++;
    await route.fulfill({ status: 204 });
  });

  await page.goto("#/integrations/extensions");
  await page.getByRole("button", { name: "Add extension" }).click();
  await page.getByRole("tab", { name: /Manage catalogs/ }).click();
  await page.getByRole("button", { name: "Remove" }).click();
  const removal = page.getByRole("alertdialog", { name: "Remove Community?" });
  await removal.getByRole("button", { name: "Remove registry" }).click();
  await expect.poll(() => removeRequests).toBe(1);
  await expect(removal).toBeHidden();
  await expect(page.getByText("Extension registry removed.", { exact: true })).toBeVisible();
  await expect(page.locator('[role="alert"]')).toHaveCount(0);
  refresh.release();
});

test("extension sign-in shows automatic renewal without starting verification", async ({ page }) => {
  await mockApi(page);
  const mock = await mockSignedSessionExtension(page, {
    state: "signed_in", expiresAt: "2099-06-15T12:00:00Z", renewsAutomatically: true,
    lastRenewalFailure: "provider_unavailable",
  });
  await page.goto("#/integrations/services/signed-audio");
  const panel = page.getByRole("region", { name: "Sign-in" });
  await expect(panel.getByText(`Renews automatically. Session valid until ${new Date("2099-06-15T12:00:00Z").toLocaleString("en-US", { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" })}.`, { exact: true })).toBeVisible();
  await expect(panel.getByRole("status")).toContainText("Last automatic renewal failed");
  await expect(panel.getByRole("status")).toContainText("Your sign-in was kept");
  await expect(panel.getByRole("button", { name: "Start verification" })).toHaveCount(0);
  expect(mock.writes).toHaveLength(0);
});

test("extension sign-in explains each verification step and failure", async ({ page }) => {
  await mockApi(page);
  const mock = await mockSignedSessionExtension(page, { state: "signed_out" });
  await page.goto("#/integrations/services/signed-audio");
  const panel = page.getByRole("region", { name: "Sign-in" });
  await expect(panel.getByText("Not signed in")).toBeVisible();

  mock.respond("start", { state: "signed_out", reasonCode: "provider_unavailable" });
  await panel.getByRole("button", { name: "Start verification" }).click();
  await expect(panel.getByRole("alert")).toContainText("unavailable right now");
  mock.respond("start", { state: "signed_out", reasonCode: "origin_not_approved" });
  await panel.getByRole("button", { name: "Start verification" }).click();
  await expect(panel.getByRole("alert")).toContainText("Review access");

  await panel.getByRole("button", { name: "Start verification" }).click();
  const link = panel.getByRole("link", { name: "Open verification page" });
  await expect(link).toBeFocused();
  await expect(link).toHaveAttribute("href", mock.pending.verificationUrl);
  await expect(link).toHaveAttribute("target", "_blank");
  await expect(link).toHaveAttribute("rel", "noopener noreferrer");
  await expect(panel.getByText("Opens verify.example.test in a new tab.")).toBeVisible();
  await expect(panel.getByText("Verification needed")).toBeVisible();

  await panel.getByRole("button", { name: "Complete sign-in" }).click();
  await expect(panel.getByRole("alert")).toContainText("Paste the link or code");
  expect(mock.writes.filter((write) => write.action === "grant")).toHaveLength(0);
  mock.respond("grant", { ...mock.pending, reasonCode: "grant_invalid" });
  await panel.getByLabel("Sign-in link or code").fill("not a grant");
  await panel.getByRole("button", { name: "Complete sign-in" }).click();
  await expect(panel.getByRole("alert")).toContainText("isn't a sign-in link");
  mock.respond("grant", { ...mock.pending, reasonCode: "grant_rejected" });
  await panel.getByLabel("Sign-in link or code").fill("signed-audio://auth?grant=stale");
  await panel.getByRole("button", { name: "Complete sign-in" }).click();
  await expect(panel.getByRole("alert")).toContainText("Start verification again");

  await panel.getByLabel("Sign-in link or code").fill("signed-audio://auth?grant=fresh");
  await panel.getByRole("button", { name: "Complete sign-in" }).click();
  await expect(panel.getByRole("status")).toHaveText("Signed in.");
  await expect(panel.getByText("Signed in", { exact: true })).toBeVisible();
  await expect(panel.getByText(`Session valid until ${new Date("2099-06-15T12:00:00Z").toLocaleString("en-US", { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" })}.`, { exact: true })).toBeVisible();
  await expect(panel.getByLabel("Sign-in link or code")).toHaveCount(0);
  expect(mock.writes.at(-1)).toEqual({ action: "grant", body: { grant: "signed-audio://auth?grant=fresh", expectedRevision: 1 } });

  const signOut = panel.getByRole("button", { name: "Sign out" });
  await expect(signOut).toBeFocused();
  await signOut.click();
  let confirm = page.getByRole("alertdialog", { name: "Sign out of Signed Audio?" });
  await confirm.getByRole("button", { name: "Cancel" }).click();
  await expect(confirm).toBeHidden();
  expect(mock.writes.filter((write) => write.action === "clear")).toHaveLength(0);
  await signOut.click();
  confirm = page.getByRole("alertdialog", { name: "Sign out of Signed Audio?" });
  await confirm.getByRole("button", { name: "Sign out" }).click();
  await expect(confirm).toBeHidden();
  await expect(panel.getByRole("status")).toHaveText("Signed out.");
  await expect(panel.getByText("Not signed in")).toBeVisible();
  expect(mock.writes.at(-1)).toEqual({ action: "clear", body: { expectedRevision: 1 } });
});

test("extension sign-in recovers from expiry, cancellation and package changes on a small screen", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  await mockApi(page);
  const mock = await mockSignedSessionExtension(page, "unavailable");
  await page.goto("#/integrations/services/signed-audio");
  const panel = page.getByRole("region", { name: "Sign-in" });
  await expect(panel.getByText("Sign-in status is unavailable right now.")).toBeVisible();

  await page.unroute("**/api/admin/extensions/packages/signed/session**");
  const expired = await mockSignedSessionExtension(page, { state: "expired", expiresAt: "2026-01-01T00:00:00Z" });
  await panel.getByRole("button", { name: "Check again" }).click();
  await expect(panel.getByText("Sign-in expired")).toBeVisible();
  expect(mock.writes).toHaveLength(0);

  const reconnect = panel.getByRole("button", { name: "Reconnect" });
  await reconnect.focus();
  await page.keyboard.press("Enter");
  await expect(panel.getByRole("link", { name: "Open verification page" })).toBeFocused();
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);

  await panel.getByRole("button", { name: "Cancel sign-in" }).click();
  await expect(panel.getByRole("status")).toHaveText("Sign-in cancelled.");
  await expect(panel.getByRole("button", { name: "Start verification" })).toBeFocused();

  expired.updatePackage();
  expired.respond("start", { error: "The extension resource changed before this update." }, 409);
  await page.keyboard.press("Enter");
  await expect(panel.getByRole("alert")).toContainText("This extension changed while you were signing in");
  await panel.getByRole("button", { name: "Start verification" }).click();
  await expect(panel.getByText("Verification needed")).toBeVisible();
  expect(expired.writes.map((write) => [write.action, write.body.expectedRevision]))
    .toEqual([["start", 1], ["clear", 1], ["start", 1], ["start", 2]]);
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
});

test("extension updates stay beside the shared management menu", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.route("**/api/admin/extensions/packages", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([{
      id: "old", extensionId: "lumen-audio", displayName: "Lumen Audio",
      version: "1.0.0", lifecycle: "active", state: "active", active: true,
      installed: true, permissionReviewRequired: false, hasPermissions: true, capabilities: ["metadata"],
      previousPackageId: "previous", registryId: "registry", stagedAt: "2026-01-01", revision: 1,
    }]),
  }));
  await page.route("**/api/admin/extensions/registries", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([{
      id: "registry", name: "Community", registryUrl: "https://example.test/registry.json",
      enabled: true, revision: 1,
    }]),
  }));
  await page.route("**/api/admin/extensions/store", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      items: [{
        id: "lumen-audio", displayName: "Lumen Audio", version: "2.0.0",
        downloadUrl: "https://example.test/lumen.zip", sha256: "a".repeat(64),
        registryId: null, types: ["metadata", "streaming"],
      }],
      errors: [],
    }),
  }));
  let reviewRequests = 0;
  await page.route("**/api/admin/extensions/packages/old/permissions/revoke", (route) => {
    reviewRequests += 1;
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        id: "old", extensionId: "lumen-audio", displayName: "Lumen Audio",
        version: "1.0.0", lifecycle: "reviewrequired", state: "reviewrequired",
        active: false, installed: false, permissionReviewRequired: true, hasPermissions: true,
        capabilities: ["metadata"], registryId: "registry", revision: 2,
      }),
    });
  });
  await page.route("**/api/admin/extensions/packages/old/permissions", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify([{
      id: "permission", permissionKind: "network", permissionValue: "https://example.test/",
      required: true, decision: "pending",
    }]),
  }));

  await page.goto("#/settings/extensions");
  const actions = page.getByRole("list", { name: "Extensions", exact: true }).locator(".row-controls");
  await expect(actions.getByRole("button", { name: "Update" })).toBeInViewport();
  await actions.getByRole("button", { name: "Manage extension" }).click();
  await expect(page.getByRole("menuitem", { name: /^(Disable|Pause)$/ })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Rollback" })).toHaveCount(0);
  await expect(page.getByRole("menuitem", { name: "Review access" })).toBeVisible();
  await page.getByRole("menuitem", { name: "Review access" }).click();
  const access = page.getByRole("alertdialog", { name: "Review access for Lumen Audio?" });
  await expect(access).toContainText("runtime will stop");
  await access.getByRole("button", { name: "Stop and review" }).click();
  await expect.poll(() => reviewRequests).toBe(1);
  const review = page.getByRole("dialog", { name: "Review permissions" });
  await expect(review).toBeVisible();
  await review.getByRole("button", { name: "Close review" }).click();
  await actions.getByRole("button", { name: "Manage extension" }).click();
  await expect(page.getByRole("menuitem", { name: "Uninstall" })).toBeVisible();
  await page.getByRole("menuitem", { name: "Uninstall" }).click();
  const uninstall = page.getByRole("alertdialog", { name: "Uninstall Lumen Audio?" });
  await expect(uninstall).toBeVisible();
  await uninstall.getByRole("button", { name: "Cancel" }).click();
  await page.getByRole("button", { name: "Add extension" }).click();
  await page.getByRole("tab", { name: /Manage catalogs/ }).click();
  await expect(page.getByText("Remove 1 installed package version from this catalog first.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Remove" })).toBeDisabled();
});

test("failed extension registry validation preserves the form", async ({ page }) => {
  await mockApi(page);
  await page.route("**/api/admin/extensions/registries", (route) => {
    if (route.request().method() !== "POST") return route.fallback();
    return route.fulfill({
      status: 400,
      contentType: "application/json",
      body: JSON.stringify({ error: "Registry could not be reached." }),
    });
  });
  await page.goto("#/integrations/extensions");
  await page.getByRole("button", { name: "Add extension" }).click();
  await page.getByRole("tab", { name: /Manage catalogs/ }).click();
  const name = page.getByLabel("Name");
  const url = page.getByLabel("Registry JSON URL");
  await name.fill("My catalog");
  await url.fill("https://example.test/registry.json");
  await page.getByRole("button", { name: "Validate and add" }).click();
  await expect(page.getByText("Registry could not be reached.")).toBeVisible();
  await expect(name).toHaveValue("My catalog");
  await expect(url).toHaveValue("https://example.test/registry.json");
});

test("extension catalogs explain why an unsigned package cannot be installed", async ({ page }) => {
  await mockApi(page);
  await page.route("**/api/admin/extensions/store", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      items: [{
        id: "unsigned", displayName: "Unsigned catalog entry", version: "1.0.0",
        downloadUrl: "https://example.test/unsigned.zip", sha256: "", registryId: "community",
        types: ["metadata"],
      }],
      errors: [],
    }),
  }));
  await page.goto("#/integrations/extensions");
  await page.getByRole("button", { name: "Add extension" }).click();

  const unavailable = page.getByRole("button", { name: "Cannot install Unsigned catalog entry: checksum unavailable" });
  await expect(unavailable).toBeDisabled();
  await expect(unavailable).toHaveText("Checksum unavailable");
  await expect(unavailable).toHaveAttribute("title", /required for a safe install/);
});
