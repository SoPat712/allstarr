import { mkdir } from "node:fs/promises";
import { expect, test, type Page } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const userId = "11111111-1111-4111-8111-111111111111";
const jobId = "22222222-2222-4222-8222-222222222222";
const linkId = "33333333-3333-4333-8333-333333333333";
const scheduleId = "44444444-4444-4444-8444-444444444444";
const reportId = "55555555-5555-4555-8555-555555555555";
const timestamp = "2026-11-04T12:00:00Z";
const task = { id: scheduleId, name: "Sync Weekly Exploration for listener, week of November 2", kind: "playlist",
  state: "Scheduled", enabled: true, ownerName: "Listener", cadence: "Daily at 04:00 · UTC", lastRunAt: timestamp,
  nextRunAt: "2026-11-05T04:00:00Z", runPlaylistLinkId: linkId };
const waiting = { id: jobId, type: "playlist.materialize", displayName: "Update playlist", state: "Deferred", ownerName: "Listener",
  summary: "Progress saved. More songs remain.", availableAt: "2026-11-04T13:00:00Z", startedAt: timestamp,
  updatedAt: timestamp, createdAt: timestamp, canCancel: true, attemptCount: 1 };
const trace = (id: string, firstByteMs: number, outcome: string) => ({ id, listenerId: userId, startedAt: timestamp, outcome,
  request: { presented: { title: "Evening song", artist: "River" } }, resolution: { provider: "library", cached: true },
  timing: { firstByteMs, elapsedMs: 60000, headersMs: 100, omittedPhases: 0, phases: [] } });

async function fixture(page: Page) {
  let schedule = { id: scheduleId, cronExpression: "0 4 * * *", timeZoneId: "UTC", overlapPolicy: "skip",
    misfirePolicy: "runOnce", enabled: true, revision: 1, nextRunAt: task.nextRunAt };
  let link = { id: linkId, name: "Weekly Exploration", sourceProviderId: "listenbrainz", revision: 1, mode: "hybrid", projectionMode: "resolved",
    materializationMode: "reconcile", importMode: "linked", trackRetention: "onDemand", scheduleId,
    mirrorStaleEntries: true, preserveManualEntries: true, syncName: true, syncDescription: true, syncArtwork: true };
  let status = "new", revision = 1;
  const writes: Array<{ path: string; body: Record<string, unknown> }> = [];
  const report = () => ({ id: reportId, kind: "playback", status, revision, notePreview: "Playback stopped early.",
    note: "Playback stopped early.", reporterName: "Listener", songTitle: "Evening song", artist: "River",
    providerId: "jellyfin", clientName: "Music app", createdAt: timestamp, updatedAt: timestamp });
  await page.route("**/*", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (!["localhost", "127.0.0.1"].includes(url.hostname)) return route.abort();
    if (!path.startsWith("/api/admin/")) return route.continue();
    const reply = (json: unknown) => route.fulfill({ json });
    if (!["GET", "HEAD"].includes(request.method())) {
      const body = request.postDataJSON() ?? {};
      writes.push({ path, body });
      if (path.endsWith("/cancel")) return reply({ state: "cancellation_requested" });
      if (path.endsWith("/status")) { status = body.status; revision++; return reply({ ...report() }); }
      if (path.endsWith("/run")) return reply({ jobId, created: true });
      if (path.includes("/schedules/")) { schedule = { ...schedule, ...body, revision: schedule.revision + 1 }; return reply(schedule); }
      if (path === "/api/admin/playlist-links/" + linkId) { link = { ...link, ...body, revision: link.revision + 1 }; return reply(link); }
    }
    if (path === "/api/admin/auth/me") return reply({ authenticated: true, backend: "Jellyfin", user: { id: "listener", allstarrUserId: userId, name: "Listener", isAdministrator: true } });
    if (path === "/api/admin/auth/oidc/status") return reply({ enabled: false });
    if (path === "/api/admin/onboarding/status") return reply({ completed: true, shouldRedirectToSetup: false, completedSteps: ["backend-identity"], recoveryNotices: [] });
    if (path === "/api/admin/ui/schema") return reply({ providers: [{ id: "listenbrainz", name: "ListenBrainz" }], configSections: [], activeBackend: "Jellyfin" });
    if (path === "/api/admin/ui/activity") return reply({ items: [
      { id: "event", kind: "playlist", label: "Playlist updated", detail: "Weekly Exploration is ready.", state: "succeeded", occurredAt: timestamp, actorDisplayName: "Listener", source: "Jellyfin" },
      { id: "problem", kind: "extension", label: "Source unavailable", detail: "Try again later.", state: "failed", severity: "error", occurredAt: timestamp, source: "Music source" },
    ], hasMore: false });
    if (path === "/api/admin/activity/tasks") return reply({ items: [{ ...task, enabled: schedule.enabled }], hasMore: false });
    if (path === "/api/admin/playlist-links") return reply({ playlistLinks: [link] });
    if (path === "/api/admin/playlist-links/" + linkId) return reply({ id: linkId, name: link.name, sourceProviderId: "listenbrainz", targetProtocol: "Jellyfin", schedule, syncState: "Succeeded", tracks: [] });
    if (path === "/api/admin/jobs") return reply({ jobs: url.searchParams.get("view") === "queue" ? [waiting] : [], progress: [], hasMore: false });
    if (path === "/api/admin/jobs/" + jobId) return reply({ job: waiting, attempts: [{ attemptNumber: 1, startedAt: timestamp, completedAt: timestamp, outcome: "deferred" }], hasMore: false });
    if (path === "/api/admin/play-traces") return reply([trace("recent", 4600, "completed"), trace("previous", 400, "cancelled")]);
    if (path.startsWith("/api/admin/play-traces/")) return reply(trace(path.split("/").at(-1)!, 4600, "completed"));
    if (path === "/api/admin/problem-reports") return reply({ items: !url.searchParams.has("status") || url.searchParams.get("status") === status ? [report()] : [], nextCursor: null });
    if (path === "/api/admin/problem-reports/" + reportId) return reply({ ...report(), context: { schemaVersion: 1, capturedAt: timestamp, subjectName: "Listener", song: { title: "Evening song", artist: "River", catalogProviderId: "jellyfin" }, match: null, playback: { clientName: "Music app" }, checks: [] } });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return reply({});
  });
  return { writes, schedule: () => schedule };
}

const variants = [
  { width: 1280, height: 900, theme: "light" }, { width: 1280, height: 900, theme: "dark" },
  { width: 390, height: 900, theme: "light" }, { width: 320, height: 900, theme: "light" },
  { width: 667, height: 375, theme: "light" },
] as const;
const pages = [
  { view: "history", status: "History status", filters: "History filters", row: ".event-log-group .row-action", title: "Playlist updated" },
  { view: "plays", status: "Plays status", filters: "Play filters", row: ".play-row .row-action", title: "Evening song" },
  { view: "jobs", status: "Jobs status", filters: "Job filters", row: ".job-row .row-action", title: "Update playlist" },
  { view: "tasks", status: "Tasks status", filters: "Task filters", row: ".task-open", title: "Weekly Exploration" },
  { view: "reports", status: "Reports status", filters: "Report status", row: ".report-list .row-action", title: "Playback" },
];
for (const variant of variants) for (const screen of pages) {
  test("Activity redesign " + screen.view + " at " + variant.width + " " + variant.theme, async ({ page }) => {
    await page.setViewportSize({ width: variant.width, height: variant.height });
    await page.emulateMedia({ colorScheme: variant.theme, reducedMotion: "reduce" });
    await page.addInitScript(theme => localStorage.setItem("allstarr.theme", theme), variant.theme);
    await fixture(page);
    await page.goto("/#/activity?view=" + screen.view);
    await expect(page.getByRole("region", { name: screen.status, exact: true })).toBeVisible();
    const filters = page.getByRole("radiogroup", { name: screen.filters, exact: true });
    await expect(filters).toBeVisible();
    await expect(filters.getByRole("radio").first()).toContainText(/\d/);
    await expect(page.locator(screen.row).first()).toBeVisible();
    const emptyFilter = screen.view === "tasks" ? "Paused" : screen.view === "reports" ? "Fixed" : screen.view === "jobs" ? "Failed" : "Problems";
    await filters.getByRole("radio", { name: new RegExp("^" + emptyFilter) }).click();
    if (screen.view === "history") await expect(page.locator(screen.row)).toHaveCount(1);
    else await expect(page.locator(screen.row)).toHaveCount(0);
    await filters.getByRole("radio").first().click();
    await expect(page.locator(screen.row).first()).toBeVisible();
    if (screen.view === "plays") { await expect(page.locator(".play-row")).toHaveCount(1); await expect(page.locator(".play-row")).toContainText("×2"); await expect(page.locator(".play-row")).toContainText("0.4–4.6 s"); }
    await expectContainedMobileGeometry(page);
    const folder = "test-results/ui-activity";
    await mkdir(folder, { recursive: true });
    const prefix = folder + "/" + screen.view + "-" + variant.width + "-" + variant.theme;
    await page.screenshot({ path: prefix + ".png", fullPage: true });
    await page.locator(screen.row).first().click();
    const panel = page.getByRole("dialog", { name: screen.title, exact: true });
    await expect(panel).toBeVisible(); await expect(panel.getByRole("heading").first()).toContainText(screen.title);
    await expectContainedMobileGeometry(page);
    await page.screenshot({ path: prefix + "-panel.png" });
    await panel.getByRole("button", { name: "Close", exact: true }).click();
    await expect(panel).not.toBeVisible();
    await expect(page.locator(screen.row).first()).toBeFocused();
  });
}

for (const [view, endpoint, label] of [
  ["history", "/api/admin/ui/activity", "Loading Event log"],
  ["plays", "/api/admin/play-traces", "Loading plays"],
  ["jobs", "/api/admin/jobs", "Loading jobs"],
  ["tasks", "/api/admin/activity/tasks", "Loading tasks"],
  ["reports", "/api/admin/problem-reports", "Loading reports"],
] as const) {
  test("Activity " + view + " keeps its loading name", async ({ page }) => {
    await fixture(page);
    let release!: () => void;
    const pending = new Promise<void>(resolve => { release = resolve; });
    await page.route(url => url.pathname === endpoint, async route => { await pending; await route.fallback(); });
    await page.goto("/#/activity?view=" + view);
    try { await expect(page.getByRole("status", { name: label, exact: true })).toBeVisible(); }
    finally { release(); }
    await expect(page.getByRole("status", { name: label, exact: true })).toHaveCount(0);
    await expect(page.locator(pages.find(screen => screen.view === view)!.row).first()).toBeVisible();
  });
}

test("Activity schedule saves immediately, can undo, and confirms a pause", async ({ page }) => {
  const state = await fixture(page);
  await page.goto("/#/activity?view=tasks");
  await page.locator(".task-open").click();
  const panel = page.locator("[data-side-panel]");
  await panel.getByLabel("Time", { exact: true }).fill("06:30");
  await panel.getByLabel("Time", { exact: true }).press("Tab");
  await expect.poll(() => state.schedule().cronExpression).toBe("30 6 * * *");
  await panel.getByRole("button", { name: "Close", exact: true }).click();
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect.poll(() => state.schedule().cronExpression).toBe("0 4 * * *");
  await page.locator(".task-open").click();
  await panel.getByRole("button", { name: "Pause schedule…" }).click();
  const confirmation = page.getByRole("alertdialog", { name: "Pause this schedule?" });
  await expect(confirmation).toBeVisible();
  expect(state.schedule().enabled).toBe(true);
  await confirmation.getByRole("button", { name: "Pause schedule", exact: true }).click();
  await expect.poll(() => state.schedule().enabled).toBe(false);
});

test("Activity report marks fixed and keeps captured details", async ({ page }) => {
  const state = await fixture(page);
  await page.goto("/#/activity?view=reports");
  await page.locator(".report-list .row-action").click();
  const panel = page.locator("[data-side-panel]");
  await expect(panel.getByRole("article", { name: "Included report details" })).toContainText("Evening song");
  await panel.getByRole("button", { name: "Mark fixed", exact: true }).click();
  await expect.poll(() => state.writes.at(-1)?.body).toEqual({ status: "fixed", expectedRevision: 1 });
  await expect(panel.getByRole("button", { name: "Fixed", exact: true })).toBeDisabled();
});

test("Activity filters apply at once and survive reopening", async ({ page }) => {
  await fixture(page);
  await page.goto("/#/activity");
  const problems = page.getByRole("radio", { name: /^Problems/ });
  await problems.click();
  await expect(page.locator(".event-log-group")).toHaveCount(1);
  await page.reload();
  await expect(problems).toHaveAttribute("aria-checked", "true");
  await expect(page.locator(".event-log-group")).toHaveCount(1);
});

test("Activity report filters change during loading without showing a stale response", async ({ page }) => {
  await fixture(page);
  let release: () => void = () => {};
  const gate = new Promise<void>(resolve => { release = resolve; });
  await page.route("**/api/admin/problem-reports?**", async route => {
    const status = new URL(route.request().url()).searchParams.get("status");
    if (status === "new") await gate;
    await route.fulfill({ json: { items: [], nextCursor: null } });
  });
  await page.goto("/#/activity?view=reports");
  await page.getByRole("radio", { name: /^Seen/ }).click();
  await expect(page.getByText("No seen reports", { exact: true })).toBeVisible();
  release();
  await expect(page.getByRole("radio", { name: /^Seen/ })).toHaveAttribute("aria-checked", "true");
  await expect(page.getByText("No seen reports", { exact: true })).toBeVisible();
});

test("Activity task can run again after a virtual sync finishes without changing its receipt", async ({ page }) => {
  await fixture(page);
  let finished = false;
  await page.route("**/api/admin/jobs/" + jobId + "?**", route => route.fulfill({ json: {
    job: { ...waiting, state: finished ? "Succeeded" : "Running" }, attempts: [], hasMore: false,
  } }));
  await page.goto("/#/activity?view=tasks");
  await page.locator(".activity-task").getByRole("button", { name: "Run now", exact: true }).click();
  await expect(page.locator(".activity-task").getByRole("button", { name: "Queued", exact: true })).toBeDisabled();
  finished = true;
  await page.getByRole("button", { name: "Refresh tasks", exact: true }).click();
  await expect(page.locator(".activity-task").getByRole("button", { name: "Run now", exact: true })).toBeEnabled();
});

test("Activity job cancellation waits for confirmation", async ({ page }) => {
  const state = await fixture(page);
  await page.goto("/#/activity?view=jobs");
  const cancel = page.locator(".job-row").getByRole("button", { name: "Cancel", exact: true });
  await cancel.click();
  const confirmation = page.getByRole("alertdialog", { name: "Cancel this job?" });
  await expect(confirmation).toBeVisible();
  expect(state.writes).toEqual([]);
  await confirmation.getByRole("button", { name: "Keep running", exact: true }).click();
  expect(state.writes).toEqual([]);
  await cancel.click();
  await confirmation.getByRole("button", { name: "Cancel job", exact: true }).click();
  await expect.poll(() => state.writes.at(-1)?.path).toBe("/api/admin/jobs/" + jobId + "/cancel");
});

test("Activity update rules save and undo with their current revision", async ({ page }) => {
  const state = await fixture(page);
  await page.goto("/#/activity?view=tasks");
  await page.locator(".task-open").click();
  await page.getByRole("switch", { name: "Drop songs the source drops", exact: true }).uncheck();
  await expect.poll(() => state.writes.at(-1)?.body).toMatchObject({ expectedRevision: 1, mirrorStaleEntries: false, preserveManualEntries: true });
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect.poll(() => state.writes.at(-1)?.body).toMatchObject({ expectedRevision: 2, mirrorStaleEntries: true, preserveManualEntries: true });
});

test("Activity report list stays available when its count overview fails", async ({ page }) => {
  await fixture(page);
  await page.route("**/api/admin/problem-reports?**", route =>
    new URL(route.request().url()).searchParams.has("status") ? route.fallback() :
      route.fulfill({ status: 503, json: { error: "Unavailable" } }));
  await page.goto("/#/activity?view=reports");
  await expect(page.locator(".report-list .row-action")).toHaveCount(1);
  await expect(page.locator(".report-list")).toContainText("Evening song");
});
