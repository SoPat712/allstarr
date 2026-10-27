import { expect, test, type Page } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";
const jobId = "11111111-1111-4111-8111-111111111111";
const rows = [
  { id: "cache", kind: "caching", label: "Song cached", state: "verified", detail: "Housewife · Tate McRae · Complete original file saved for 72 hours.", sourceTitle: "Housewife", sourceArtist: "Tate McRae", providerId: "applemusic", source: "Apple Music" },
  { id: "failure", jobId, kind: "job", label: "Download a song", state: "failed", severity: "error", detail: "Power Trip · Apple Music could not deliver audio.", recovery: "Review the source connection or choose another ready source." },
  { id: "active", kind: "playlist", label: "Update playlist", state: "running", detail: "Evening mix · 5 of 12 songs updated." },
  { id: "retry", kind: "scrobble", label: "Send a listen", state: "retrying", detail: "Housewife · Last.fm is temporarily unavailable.", recovery: "A retry is scheduled. Your listen remains queued." },
  { id: "health", kind: "job", label: "Check Jellyfin server", state: "partly_checked", detail: "4 checks passed; 3 were not checked. No artwork or lyrics were available." },
  { id: "cancel", kind: "job", label: "Update favorites", state: "cancelled", detail: "Cancelled before changes were applied." },
].map((row, index) => ({ ...row, source: row.source ?? "System", actorUserId: "viewer", actorDisplayName: "Josh", occurredAt: `2026-10-26T12:00:0${9 - index}Z` }));
const job = { id: jobId, type: "recording.keep", displayName: "Keep a song", state: "Failed", source: "Apple Music", ownerName: "Josh", targetName: "Power Trip · J. Cole", summary: "Audio could not be downloaded from Apple Music.", recovery: "Review the source connection or choose another ready source.", lastErrorMessage: "The source did not return audio before the time limit.", updatedAt: "2026-10-26T12:00:08Z", completedAt: "2026-10-26T12:00:08Z", attemptCount: 1 };
async function mockActivity(page: Page, events: unknown[] = rows, options: { tasks?: unknown[]; job?: Record<string, unknown> } = {}) {
  const reads: string[] = [], writes: string[] = [];
  await page.route("**/api/admin/**", async route => {
    const url = new URL(route.request().url()), path = url.pathname;
    if (!["GET", "HEAD"].includes(route.request().method())) writes.push(path);
    const reply = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    if (path === "/api/admin/auth/me") return reply({ authenticated: true, backend: "Jellyfin", user: { id: "viewer", name: "Josh", isAdministrator: true } });
    if (path === "/api/admin/auth/oidc/status") return reply({ enabled: false });
    if (path === "/api/admin/onboarding/status") return reply({ completed: true, shouldRedirectToSetup: false, setupOpen: false, completedSteps: ["backend-identity"], revision: 1, recoveryNotices: [] });
    if (path === "/api/admin/ui/schema") return reply({ configSections: [], providers: [{ id: "applemusic", name: "Apple Music" }, { id: "lastfm", name: "Last.fm" }], activeBackend: "Jellyfin" });
    if (path === "/api/admin/ui/activity") return reply({ items: events, hasMore: false });
    if (path === "/api/admin/activity/tasks") return reply({ items: options.tasks ?? [], hasMore: false });
    if (path === "/api/admin/jobs") return reply({ jobs: [], progress: [], hasMore: false });
    if (path === `/api/admin/jobs/${jobId}`) { reads.push(path); return reply({ job: { ...job, ...options.job }, attempts: [{ attemptNumber: 1, startedAt: "2026-10-26T12:00:03Z", completedAt: "2026-10-26T12:00:08Z", outcome: "failed", errorMessage: "The source did not return audio before the time limit." }], hasMore: false }); }
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return reply({});
  });
  return { reads, writes };
}
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Activity explains outcomes with color and context at ${width}px ${theme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 }); await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const fixture = await mockActivity(page); await page.goto("/#/activity");
    const groups = page.locator(".event-log-group"); await expect(groups).toHaveCount(6);
    const tones = ["success", "danger", "accent", "warning", "neutral", "neutral"];
    for (let index = 0; index < tones.length; index++) {
      await expect(groups.nth(index).locator(":scope > summary .event-kind-icon")).toHaveAttribute("data-tone", tones[index]);
      await expect(groups.nth(index).locator(":scope > summary [data-slot=badge]")).toHaveClass(new RegExp(`badge-${tones[index]}`));
    }
    const iconColor = async (index: number) => groups.nth(index).locator(":scope > summary .event-kind-icon").evaluate(element => getComputedStyle(element).color);
    expect(await iconColor(0)).not.toBe(await iconColor(4));
    expect(await iconColor(1)).not.toBe(await iconColor(0));
    await expect(groups.first().locator(":scope > summary")).toContainText("Housewife");
    await expect(groups.first().locator(":scope > summary")).toContainText("Apple Music");
    const failed = groups.nth(1); await failed.locator(":scope > summary").focus(); await page.keyboard.press("Enter");
    await expect(failed).toContainText(job.recovery);
    const action = failed.getByRole("link", { name: "Open related work", exact: true });
    await expect(action).toHaveAttribute("href", `#/activity?view=jobs&job=${jobId}`);
    await expect(failed.locator(":scope > summary")).not.toContainText(jobId);
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.evaluate(() => window.scrollTo(0, 0)); await page.screenshot({ path: testInfo.outputPath(`activity-history-${width}-${theme}.png`) });
    await action.click();
    await expect(page.getByRole("tab", { name: "Jobs", exact: true })).toHaveAttribute("aria-selected", "true");
    const selected = page.locator(`[data-job-id="${jobId}"]`);
    await expect(selected.getByRole("button", { name: /Keep a song/ })).toHaveAttribute("aria-expanded", "true");
    await expect(selected).toContainText(job.targetName); await expect(selected).toContainText(job.recovery);
    await expect(page.getByRole("region", { name: "Attempt history" })).toContainText("Attempt 1");
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: testInfo.outputPath(`activity-job-${width}-${theme}.png`) });
    expect(fixture.reads.length).toBeGreaterThan(0); expect(fixture.writes).toEqual([]);
  });
}
test("Activity lifecycle uses the latest result while retaining earlier failures", async ({ page }) => {
  const events = [
    { id: "done", state: "succeeded", detail: "Power Trip is now kept.", occurredAt: "2026-10-26T12:00:09Z" },
    { id: "retry", state: "failed", detail: "The first source timed out.", severity: "error", occurredAt: "2026-10-26T12:00:05Z" },
    { id: "start", state: "running", detail: "Downloading Power Trip.", occurredAt: "2026-10-26T12:00:00Z" },
  ].map(item => ({ ...item, label: "Keep a song", jobId, actorUserId: "viewer", actorDisplayName: "Josh", kind: "job", source: "Apple Music" }));
  await mockActivity(page, events); await page.goto("/#/activity");
  const group = page.locator(".event-log-group"); await expect(group).toHaveCount(1);
  await expect(group.locator(":scope > summary [data-slot=badge]")).toHaveClass(/badge-success/);
  await group.locator(":scope > summary").click(); await expect(group).toContainText("The first source timed out.");
  await page.getByRole("button", { name: "Problems only" }).click();
  await expect(page.getByText("Nothing matches these filters", { exact: true })).toBeVisible();
});
test("Activity keeps an older reference out of the headline and makes it traceable", async ({ page }) => {
  await mockActivity(page, [{ id: "legacy", kind: "job", source: "system", label: "recording.keep", state: "failed", detail: jobId, jobId, actorUserId: "viewer", occurredAt: "2026-10-26T12:00:00Z" }]);
  await page.goto("/#/activity"); const group = page.locator(".event-log-group");
  await expect(group.locator(":scope > summary")).toContainText("Keep a song"); await expect(group.locator(":scope > summary")).not.toContainText(jobId);
  await group.locator(":scope > summary").click();
  await expect(group.getByRole("link", { name: "Open related work", exact: true })).toHaveAttribute("href", `#/activity?view=jobs&job=${jobId}`);
  await group.getByText("Technical details", { exact: true }).click(); await expect(group).toContainText(jobId);
});

test("Tasks explains Health and backup schedules with relevant settings links", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 });
  const fixture = await mockActivity(page, rows, { tasks: [
    { id: "health", name: "Health checks", kind: "health", state: "Needs attention", enabled: true, cadence: "Hourly", detail: "Health check (server checks): 3 passed, 2 not checked, 1 failed. Failed step: Search because backend authentication was rejected.", nextRunAt: "2026-10-27T15:00:00Z", actionHref: "#/settings/health", actionLabel: "Open Health" },
    { id: "backup", name: "Database and keyring backups", kind: "backup", state: "Scheduled", enabled: true, cadence: "Daily at 03:00 · UTC", detail: "Completed; detailed result not retained.", actionHref: "#/settings/backups", actionLabel: "Open Backups" },
  ] });
  await page.goto("/#/activity?view=tasks");
  const tasks = page.locator(".activity-task"); await expect(tasks).toHaveCount(2);
  await expect(tasks.first()).toContainText("authentication was rejected");
  await expect(tasks.first().locator("[data-slot=badge]")).toHaveClass(/badge-warning/);
  await expect(tasks.last().locator("[data-slot=badge]")).toHaveClass(/badge-accent/);
  await expect(tasks.first().getByRole("link", { name: "Open Health" })).toHaveAttribute("href", "#/settings/health");
  await expect(tasks.last().getByRole("link", { name: "Open Backups" })).toHaveAttribute("href", "#/settings/backups");
  await expectContainedMobileGeometry(page); expect(fixture.writes).toEqual([]);
});
test("A completed job with missing Health checks stays neutral", async ({ page }) => {
  await mockActivity(page, rows, { job: { type: "health.smoke", displayName: "Health check", state: "Succeeded", displayState: "not_checked", summary: "Health check result unavailable; detailed results were not retained.", targetName: "Server", recovery: "Review the current checks in Health." } });
  await page.goto(`/#/activity?view=jobs&job=${jobId}`);
  const selected = page.getByRole("region", { name: "Selected operation" });
  await expect(selected.locator("[data-slot=badge]")).toHaveText("Not checked");
  await expect(selected.locator("[data-slot=badge]")).toHaveClass(/badge-neutral/);
  await expect(selected).toContainText("result unavailable");
});
test("An unavailable related job gives a readable explanation without changes", async ({ page }) => {
  const fixture = await mockActivity(page);
  await page.route(`**/api/admin/jobs/${jobId}?**`, route => route.fulfill({ status: 404, json: { error: "Not found" } }));
  await page.goto(`/#/activity?view=jobs&job=${jobId}`);
  await expect(page.getByRole("alert")).toContainText("unavailable or you no longer have permission");
  expect(fixture.writes).toEqual([]);
});

test("Diagnostic event groups do not pretend to be unfinished jobs", async ({ page }) => {
  await mockActivity(page, [
    { id: "info", kind: "extension", label: "Runtime log", state: "info", detail: "Extension started.", action: "runtime.log", correlationId: "startup", occurredAt: "2026-10-26T12:00:01Z" },
    { id: "warning", kind: "extension", label: "Runtime log", state: "warning", severity: "warning", detail: "Optional source unavailable.", action: "runtime.log", correlationId: "startup", occurredAt: "2026-10-26T12:00:00Z" },
  ]);
  await page.goto("/#/activity");
  const group = page.locator(".event-log-group"); await expect(group).toHaveCount(1);
  await expect(group.locator(":scope > summary [data-slot=badge]")).toHaveText("2 events");
});
