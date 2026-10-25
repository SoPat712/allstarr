import { expect, test, type Page, type Route } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const artifact = {
  id: "11111111-1111-4111-8111-111111111111", fileName: "allstarr-backup-20261024-030000-11111111111141118111111111111111.zip",
  createdAt: "2026-10-24T03:00:00Z", verifiedAt: "2026-10-24T03:00:00Z", status: "verified", bytes: 2 * 1024 * 1024,
  sha256: null, schemaVersion: "fixture", applicationVersion: "3.1.0", includesKeyRing: true,
  origin: "manual", retentionBuckets: ["manual"],
};

async function reply(route: Route, data: unknown, status = 200) {
  return route.fulfill({ status, contentType: "application/json", body: JSON.stringify(data) });
}

async function mockBackups(page: Page, administrator = true) {
  const writes: Array<Record<string, unknown>> = [];
  const state = {
    storageReads: 0, creations: 0, mode: "idle", polls: 0, restorePending: false, ready: true, conflict: false,
    directoryState: "ready", executionFailure: false,
    policy: { enabled: true, timeUtc: "03:00", timeZone: "UTC", directory: "", dailyRetentionCount: 14,
      weeklyRetentionCount: 8, manualRetentionCount: 10, revision: "first" },
  };
  await page.route("**/api/admin/**", async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/admin/auth/me") return reply(route, { authenticated: true, backend: "Jellyfin",
      user: { id: "viewer", name: "Viewer", isAdministrator: administrator } });
    if (path === "/api/admin/auth/oidc/status") return reply(route, { enabled: false });
    if (path === "/api/admin/onboarding/status") return reply(route, { completed: true, setupOpen: false,
      shouldRedirectToSetup: false, schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"],
      completionSource: "setup-guide", revision: 1, recoveryNotices: [], migration: { available: false, completed: false, firstRun: false } });
    if (path === "/api/admin/ui/schema") return reply(route, { configSections: [] });
    if (path === "/api/admin/config") return reply(route, {});
    if (path === "/api/admin/storage/backups/configuration") {
      expect(route.request().method()).toBe("PUT");
      const input = route.request().postDataJSON() as Record<string, unknown>;
      writes.push(input);
      if (state.conflict) return reply(route, { error: "backup_configuration_changed", message: "Backup settings changed. Reload them before saving." }, 409);
      state.policy = { ...state.policy, ...input, revision: "second" };
      return reply(route, { backupPolicy: state.policy, backupStatus: status() });
    }
    if (path === "/api/admin/storage/backups") {
      state.creations++;
      if (state.mode === "create-failure") return reply(route, { error: "backup_unavailable", message: "The backup could not be queued. Try again." }, 503);
      state.mode = "queued";
      state.polls = 0;
      return reply(route, { jobId: "22222222-2222-4222-8222-222222222222", created: true, message: "Backup queued." }, 202);
    }
    if (path === "/api/admin/storage") {
      state.storageReads++;
      if (state.mode === "queued") {
        state.polls++;
        if (state.polls === 2) state.mode = "running";
      } else if (state.mode === "running") {
        state.polls++;
        if (state.polls >= 3) state.mode = state.executionFailure ? "failed" : "complete";
      }
      return reply(route, { storage: { provider: "Sqlite", readiness: state.ready ? "Ready" : "Unavailable" },
        restorePending: state.restorePending, backups: state.mode === "complete" ? [artifact] : [],
        backupPolicy: state.policy, backupStatus: status() });
    }
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return reply(route, {});
  });
  function status() {
    const active = state.mode === "queued" || state.mode === "running";
    return { state: active ? state.mode : "idle", directoryState: state.directoryState,
      activeJobId: active ? "22222222-2222-4222-8222-222222222222" : null,
      lastSuccessAt: state.mode === "complete" ? artifact.createdAt : null, nextRunAt: "2026-10-25T03:00:00Z",
      lastBytes: state.mode === "complete" ? artifact.bytes : null,
      lastFailure: state.mode === "failed" ? { code: "backup_directory_unavailable",
        message: "The backup directory is unavailable. Check its mount, permissions and free space.", at: "2026-10-24T03:01:00Z" } : null };
  }
  return { state, writes };
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Daily backup policy saves in Backups at ${width}px ${theme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    const mock = await mockBackups(page);
    await page.goto("/#/settings/backups");
    const form = page.getByRole("form", { name: "Backup schedule", exact: true });
    await expect(form).toBeVisible();
    await expect(form.getByRole("checkbox", { name: "Daily backups", exact: true })).toBeChecked();
    await expect(form.getByLabel("Backup time (UTC)", { exact: true })).toHaveValue("03:00");
    await expect(form.getByLabel("Daily copies", { exact: true })).toHaveValue("14");
    await expect(form.getByLabel("Weekly copies", { exact: true })).toHaveValue("8");
    await expect(form.getByLabel("Manual copies", { exact: true })).toHaveValue("10");
    await page.screenshot({ path: testInfo.outputPath(`backups-status-${width}-${theme}.png`) });
    const save = form.getByRole("button", { name: "Save schedule", exact: true });
    await expect(save).toBeDisabled();
    await form.getByLabel("Daily copies", { exact: true }).fill("0");
    await save.click();
    expect(mock.writes).toHaveLength(0);
    await form.getByLabel("Backup time (UTC)", { exact: true }).fill("02:30");
    await form.getByLabel("Backup folder", { exact: true }).fill("/mnt/backup-sync");
    await form.getByLabel("Daily copies", { exact: true }).fill("21");
    await form.getByLabel("Weekly copies", { exact: true }).fill("12");
    await form.getByLabel("Manual copies", { exact: true }).fill("7");
    await save.focus();
    await page.keyboard.press("Enter");
    await expect.poll(() => mock.writes.length).toBe(1);
    expect(mock.writes[0]).toEqual({ enabled: true, timeUtc: "02:30", directory: "/mnt/backup-sync",
      dailyRetentionCount: 21, weeklyRetentionCount: 12, manualRetentionCount: 7, expectedRevision: "first" });
    await expect(page.getByText("Backup schedule saved.", { exact: true })).toBeVisible();
    await expect(save).toBeDisabled();
    await page.getByRole("button", { name: "Dismiss notification", exact: true }).click();
    if (width === 390) await expectContainedMobileGeometry(page);
    await form.getByRole("heading", { name: "Backup schedule", exact: true }).click();
    await page.screenshot({ path: testInfo.outputPath(`backups-form-${width}-${theme}.png`) });
    await save.scrollIntoViewIfNeeded();
    await expect(save).toBeInViewport();
    await page.screenshot({ path: testInfo.outputPath(`backups-footer-${width}-${theme}.png`) });
    await page.screenshot({ path: testInfo.outputPath(`backups-${width}-${theme}.png`), fullPage: true });
  });
}

test("Back up now reports queued work until verification completes", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 900 });
  const mock = await mockBackups(page);
  await page.goto("/#/settings/backups");
  const run = page.getByRole("button", { name: "Back up now", exact: true });
  await run.click();
  await expect(run).toBeDisabled();
  await expect(page.getByText("Backup queued.", { exact: true })).toBeVisible();
  await expect(page.getByText(artifact.fileName, { exact: true })).toHaveCount(0);
  await expect(page.getByRole("status", { name: "Backup status", exact: true })).toHaveText("Queued");
  await expect(page.getByRole("status", { name: "Backup status", exact: true })).toHaveText("Backing up");
  await expect(page.getByText(artifact.fileName, { exact: true })).toBeVisible();
  await expect(run).toBeEnabled();
  await expect(page.getByRole("link", { name: "Download", exact: true })).toHaveAttribute("href", `/api/admin/storage/backups/${artifact.id}/download`);
  await expect(page.getByRole("article", { name: "Saved backups", exact: true }).getByText("Manual", { exact: true })).toBeVisible();
  expect(mock.state.creations).toBe(1);
  await expectContainedMobileGeometry(page);
  await page.getByRole("article", { name: "Saved backups", exact: true }).scrollIntoViewIfNeeded();
  await page.screenshot({ path: testInfo.outputPath("backups-saved-390.png") });
});

test("A staged restore disables backup work and policy saves", async ({ page }) => {
  const mock = await mockBackups(page);
  mock.state.restorePending = true;
  await page.goto("/#/settings/backups");
  await expect(page.getByRole("button", { name: "Back up now", exact: true })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Save schedule", exact: true })).toBeDisabled();
  expect(mock.state.creations).toBe(0);
});

test("Listeners never read backup policy or archives", async ({ page }) => {
  const mock = await mockBackups(page, false);
  await page.goto("/#/settings/backups");
  await expect(page.getByRole("heading", { name: "This workspace is for administrators.", exact: true })).toBeVisible();
  expect(mock.state.storageReads).toBe(0);
});

test("Refresh and revision conflicts preserve an unsaved backup policy", async ({ page }) => {
  const mock = await mockBackups(page);
  await page.goto("/#/settings/backups");
  const form = page.getByRole("form", { name: "Backup schedule", exact: true });
  const time = form.getByLabel("Backup time (UTC)", { exact: true });
  await time.fill("04:00");
  mock.state.policy = { ...mock.state.policy, timeUtc: "05:00", revision: "third" };
  await page.getByRole("button", { name: "Refresh status", exact: true }).click();
  await expect(page.getByText("Your unsaved edits are kept.", { exact: false })).toBeVisible();
  await expect(time).toHaveValue("04:00");
  mock.state.conflict = true;
  await form.getByRole("button", { name: "Save schedule", exact: true }).click();
  await expect.poll(() => mock.writes.length).toBe(1);
  expect(mock.writes[0].expectedRevision).toBe("first");
  await expect(page.getByRole("alert").filter({ hasText: "Reload them before saving." })).toBeVisible();
  await expect(time).toHaveValue("04:00");
  await page.getByRole("button", { name: "Reload server values", exact: true }).click();
  await expect(time).toHaveValue("05:00");
  await expect(form.getByRole("button", { name: "Save schedule", exact: true })).toBeDisabled();
  mock.state.conflict = false;
  await time.fill("06:00");
  await form.getByRole("button", { name: "Save schedule", exact: true }).click();
  await expect.poll(() => mock.writes.length).toBe(2);
  expect(mock.writes[1].expectedRevision).toBe("third");
});

test("Unavailable storage and a missing backup folder block new work truthfully", async ({ page }) => {
  const mock = await mockBackups(page);
  mock.state.ready = false;
  await page.goto("/#/settings/backups");
  const form = page.getByRole("form", { name: "Backup schedule", exact: true });
  const run = page.getByRole("button", { name: "Back up now", exact: true });
  await expect(page.getByRole("status", { name: "Backup status", exact: true })).toHaveText("Unavailable");
  await expect(run).toBeDisabled();
  await expect(form.getByLabel("Backup folder", { exact: true })).toBeDisabled();
  await expect(page.getByText("The backup list is unavailable until durable storage is ready.", { exact: true })).toBeVisible();
  await expect(page.getByText("No backups yet.", { exact: true })).toHaveCount(0);
  mock.state.ready = true;
  mock.state.directoryState = "unavailable";
  await page.getByRole("button", { name: "Refresh status", exact: true }).click();
  await expect(form.getByLabel("Backup folder", { exact: true })).toBeEnabled();
  await expect(run).toBeDisabled();
  await expect(page.getByRole("status", { name: "Backup status", exact: true })).toHaveText("Unavailable");
  expect(mock.state.creations).toBe(0);
});

test("A failed backup admission shows its safe reason and can be retried", async ({ page }) => {
  const mock = await mockBackups(page);
  mock.state.mode = "create-failure";
  await page.goto("/#/settings/backups");
  const run = page.getByRole("button", { name: "Back up now", exact: true });
  await run.click();
  await expect(page.getByRole("alert").filter({ hasText: "The backup could not be queued. Try again." })).toBeVisible();
  await expect(run).toBeEnabled();
  await expect(page.getByText(artifact.fileName, { exact: true })).toHaveCount(0);
  mock.state.mode = "idle";
  await run.click();
  await expect(page.getByText(artifact.fileName, { exact: true })).toBeVisible();
  expect(mock.state.creations).toBe(2);
});

test("Inactive settings never request backup policy", async ({ page }) => {
  const mock = await mockBackups(page);
  await page.goto("/#/settings/general");
  await expect(page.getByRole("heading", { name: "General", level: 2, exact: true })).toBeVisible();
  expect(mock.state.storageReads).toBe(0);
  await page.locator(".settings-nav").getByRole("link", { name: "Backups", exact: true }).click();
  await expect(page.getByRole("form", { name: "Backup schedule", exact: true })).toBeVisible();
  expect(mock.state.storageReads).toBe(1);
});

test("An accepted job can fail without appearing as a completed backup", async ({ page }) => {
  const mock = await mockBackups(page);
  mock.state.executionFailure = true;
  await page.goto("/#/settings/backups");
  const run = page.getByRole("button", { name: "Back up now", exact: true });
  await run.click();
  await expect(page.getByRole("status", { name: "Backup status", exact: true })).toHaveText("Queued");
  await expect(page.getByRole("status", { name: "Backup status", exact: true })).toHaveText("Last backup failed");
  await expect(page.getByText(/The backup directory is unavailable\. Check its mount, permissions and free space\./)).toBeVisible();
  await expect(page.getByText(artifact.fileName, { exact: true })).toHaveCount(0);
  await expect(page.getByText("Backup completed.", { exact: true })).toHaveCount(0);
  await expect(run).toBeEnabled();
});

test("Unknown backup facts stay neutral and changed archives cannot be downloaded", async ({ page }) => {
  const mock = await mockBackups(page);
  await page.route("**/api/admin/storage", route => reply(route, {
    storage: { provider: "Sqlite", readiness: "Ready" }, restorePending: false, backupPolicy: mock.state.policy,
    backupStatus: { state: "idle", directoryState: "ready", activeJobId: null, lastSuccessAt: "bad-date",
      nextRunAt: null, lastBytes: 0, lastFailure: null },
    backups: [{ ...artifact, createdAt: "bad-date", verifiedAt: null, bytes: 0, status: "changed", verification: "changed" }],
  }));
  await page.goto("/#/settings/backups");
  await expect(page.getByText("Size unavailable", { exact: false })).toHaveCount(2);
  await expect(page.getByText("File changed", { exact: false })).toBeVisible();
  await expect(page.getByRole("link", { name: "Download", exact: true })).toBeDisabled();
  await expect(page.getByRole("link", { name: "Download", exact: true })).not.toHaveAttribute("href");
  await expect(page.getByText(/Invalid Date|0 MiB/)).toHaveCount(0);
  await expect(page.getByRole("article", { name: "Database backup status", exact: true }).getByText("Not available", { exact: true })).toHaveCount(2);
});
