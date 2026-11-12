import { expect, test } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";
import { routeRelease, mockApi } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

test("Event log groups matching work and preserves actionable history", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  let currentRequests = 0;
  await page.route("**/api/admin/ui/activity?*", (route) => {
    const url = new URL(route.request().url());
    const older = url.searchParams.has("before");
    if (!older) currentRequests += 1;
    const current = ["First", "First", "Second"].map((playlistName, index) => ({
      id: `match-${index}`, kind: "matching", source: "lumen-audio",
      providerId: "lumen-audio", label: "Track matched", state: "accepted",
      detail: `Song ${index}`, occurredAt: `2026-01-02T00:00:0${2 - index}Z`,
      correlationId: "job-1", action: "track-match.evaluate", playlistName,
      sourceTitle: `Song ${index}`, targetProviderId: "library",
      targetTitle: `Local Song ${index}`, confidenceLabel: "96%",
      sourceProviderTrackId: `provider-${index}`, backendItemId: `backend-${index}`,
      artworkUrl: `/artwork-${index}.jpg`,
      technicalDetails: { titleSimilarity: "0.98" },
    }));
    const items = older
      ? [{
          id: "older", kind: "job", source: "system", label: "Playlist sync",
          state: "succeeded", detail: "Generation 1", occurredAt: "2026-01-01T00:00:00Z",
        }]
      : currentRequests > 1
        ? [{
            id: "live-event", kind: "job", source: "system", label: "New live event",
            state: "succeeded", detail: "Streamed update", occurredAt: "2026-01-03T00:00:00Z",
          }, ...current]
        : current;
    return route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        items,
        hasMore: !older,
        nextCursor: older ? null : "2026-01-02T00:00:00Z",
        nextCursorId: older ? null : "match-2",
      }),
    });
  });
  await page.goto("#/activity");

  await expect(page.getByText("Matched 3 songs in 2 playlists")).toBeVisible();
  const group = page.locator(".event-log-group").filter({ hasText: "Matched 3 songs" });
  await expect(page.locator(".event-log-group .item-row .activity-status-icon img")).toHaveCount(0);
  await expect(group.locator(".item-row .activity-status-icon svg")).toBeVisible();
  await expect(page.getByText("Playlist sync", { exact: true }).first()).toBeVisible();
  await expect(page.getByRole("button", { name: "Load earlier events" })).toHaveCount(0);
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  if (process.env.ALLSTARR_SCREENSHOT_DIR)
    await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/activity-populated-390.png`, fullPage: true });
  await expect(page.getByLabel("Search")).toBeVisible();
  await page.getByLabel("Search").fill("missing event");
  await expect(page.getByText("Nothing matches these filters")).toBeVisible();
  await page.getByRole("button", { name: "Clear filters" }).click();

  await group.locator(".row-action").focus();
  await page.keyboard.press("Enter");
  await expect(page.locator("[data-side-panel] .event-child .event-art > span").first()).toBeVisible();
  const technical = page.getByText("Technical details").first();
  await expect(technical).toBeVisible();
  await expect(page.getByText("Title Similarity").first()).toBeHidden();
  await technical.click();
  await expect(page.getByText("Media server item ID").first()).toBeVisible();
  await expect(page.getByText("Title Similarity").first()).toBeVisible();
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(page.locator(".event-log-group.streaming")).toHaveCount(1);
  const streamedGroup = page.locator(".event-log-group").filter({ hasText: "New live event" });
  await expect(streamedGroup.locator(".item-row").getByText("New live event", { exact: true })).toBeVisible();
  await expect(group.locator(".row-action")).toHaveAttribute("aria-pressed", "true");
  await page.locator("[data-side-panel]").getByRole("link", { name: "Review match" }).first().click();
  await expect(page).toHaveURL(/#\/library\/matches\?search=Song%200$/);
});

test("retry and refresh controls expose pending work", async ({ page }) => {
  const failures = ["/api/admin/ui/activity"];
  await mockApi(page, { fail: failures });
  let delayedRequest: Promise<void> | null = null;
  await page.route("**/api/admin/ui/activity?*", async (route) => {
    if (delayedRequest) await delayedRequest;
    return route.fallback();
  });
  await page.goto("#/activity");
  const errorSurface = page.getByRole("alert", { name: "Event log unavailable" });
  await expect(errorSurface).toBeVisible();

  failures.length = 0;
  const retry = routeRelease();
  delayedRequest = retry.promise;
  await errorSurface.getByRole("button", { name: "Try again" }).click();
  await expect(page.getByRole("status", { name: "Loading Event log" })).toHaveAttribute("aria-busy", "true");
  retry.release();
  delayedRequest = null;
  await expect(page.getByRole("region", { name: "Event log" })).toBeVisible();

  const refresh = routeRelease();
  delayedRequest = refresh.promise;
  await page.getByRole("button", { name: "Refresh", exact: true }).click();
  await expect(page.getByRole("button", { name: "Refreshing…" })).toBeDisabled();
  refresh.release();
  delayedRequest = null;
  await expect(page.getByRole("button", { name: "Refresh", exact: true })).toBeEnabled();
});

for (const width of [390, 1280]) {
  for (const theme of ["light", "dark"] as const) {
    for (const view of ["History", "Jobs", "Tasks"] as const) {
      test(`Activity views: ${view} at ${width}px in ${theme}`, async ({ page }) => {
        await page.setViewportSize({ width, height: 844 });
        await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
        await mockApi(page);
        let cancelled = false;
        let taskRuns = 0;
        const jobs = ["Pending", "Running", "RetryScheduled", "Deferred"].map((state, index) => ({
          id: `activity-job-${index}`, type: ["favorite.process", "playlist.materialize", "playback.signal", "recording.kept-cleanup"][index],
          state, ownerName: "Listener", attemptCount: 1, canCancel: true,
          availableAt: "2026-10-16T00:00:00Z", updatedAt: "2026-10-15T00:00:00Z", createdAt: "2026-10-15T00:00:00Z",
        }));
        await page.route("**/api/admin/jobs?*", (route) => {
          const query = new URL(route.request().url()).searchParams;
          const history = query.get("view") === "history";
          const older = query.has("before");
          return route.fulfill({ json: {
            jobs: history ? [{ ...jobs[0], id: older ? "older-finished" : "finished-job", state: "Failed", canCancel: false,
              type: "playlist.materialize", completedAt: "2026-10-15T00:00:00Z", lastErrorMessage: "The source is temporarily unavailable." }]
              : cancelled ? jobs.slice(1) : jobs,
            progress: history ? [] : [{ id: "progress", jobId: "activity-job-1", action: "copy", outcome: "running",
              createdAt: "2026-10-15T00:00:00Z", detailsJson: '{"completed":5,"total":10,"message":"Writing playlist"}' }],
            hasMore: history && !older, nextCursor: "2026-10-15T00:00:00Z", nextCursorId: "finished-job",
          }});
        });
        await page.route("**/api/admin/jobs/finished-job?*", (route) => route.fulfill({ json: {
          job: { ...jobs[0], id: "finished-job", state: "Failed" },
          attempts: [{ attemptNumber: 1, startedAt: "2026-10-15T00:00:00Z", completedAt: "2026-10-15T00:00:01Z",
            outcome: "failed", errorMessage: "The source is temporarily unavailable." }], hasMore: false,
        }}));
        await page.route("**/api/admin/jobs/activity-job-0/cancel", (route) => {
          cancelled = true;
          return route.fulfill({ status: 202, json: { state: "cancellation_requested" } });
        });
        await page.route("**/api/admin/activity/tasks", (route) => route.fulfill({ json: { items: [
          { id: "schedule-1", name: "Sync Evening playlist", kind: "playlist", ownerName: "Listener", enabled: true,
            lastRunAt: "2026-10-14T00:00:00Z", nextRunAt: "2026-10-16T00:00:00Z", state: "Scheduled",
            cadence: "Daily · UTC", runPlaylistLinkId: "playlist-1" },
          { id: "event-retention", name: "Activity retention", kind: "maintenance", enabled: true,
            lastRunAt: null, nextRunAt: null, state: "Waiting", cadence: "Every 6 hours", detail: "Runs automatically after startup." },
        ]}}));
        await page.route("**/api/admin/playlist-links/playlist-1/run", (route) => {
          taskRuns++;
          return route.fulfill({ status: 202, json: { jobId: "new-sync" } });
        });
        await page.goto("#/activity");
        await page.getByRole("tab", { name: view, exact: true }).click();
        await expect(page.getByRole("tab", { name: view, exact: true })).toHaveAttribute("aria-selected", "true");
        if (view === "Jobs") {
          await expect(page.locator('[data-job-id="activity-job-1"]')).toContainText("Writing playlist");
          await expect(page.getByRole("progressbar", { name: /50%/ })).toBeVisible();
          for (const state of ["Waiting", "Running"])
            await expect(page.getByText(state, { exact: true }).first()).toBeVisible();
          await page.locator(".job-row").getByRole("button", { name: "Cancel", exact: true }).first().click();
          await page.getByRole("alertdialog", { name: "Cancel this job?" }).getByRole("button", { name: "Cancel job", exact: true }).click();
          await expect.poll(() => cancelled).toBe(true);
          await expect(page.locator(".job-row").getByRole("button", { name: "Cancel", exact: true })).toHaveCount(3);
          await expect(page.getByRole("region", { name: "Running and waiting" })).toBeVisible();
          await expect(page.locator('[data-job-id="finished-job"]')).toContainText("The source is temporarily unavailable.");
          const finished = page.locator('[data-job-id="finished-job"]').getByRole("button", { name: /Update playlist/ });
          await expect(finished).toHaveAttribute("aria-pressed", "false");
          await finished.click();
          await expect(finished).toHaveAttribute("aria-pressed", "true");
          await expect(page.getByRole("region", { name: "Attempt history" })).toContainText("Attempt 1");
          await page.locator("[data-side-panel]").getByRole("button", { name: "Close", exact: true }).click();
          await page.getByRole("button", { name: "Load earlier jobs" }).click();
          await expect(page.locator('[data-job-id="older-finished"]')).toBeVisible();
        } else if (view === "History") {
          await expect(page.getByRole("region", { name: /Event log|Your activity/ })).toBeVisible();
          await expect(page.locator("[data-job-id]")).toHaveCount(0);
        } else {
          await expect(page.getByText("Evening playlist", { exact: true })).toBeVisible();
          await expect(page.getByText("Activity retention", { exact: true })).toBeVisible();
          await page.getByRole("button", { name: "Run now" }).click();
          await expect.poll(() => taskRuns).toBe(1);
          await expect(page.getByRole("status").filter({ hasText: "Task queued" })).toBeVisible();
        }
        await expectContainedMobileGeometry(page);
        if (process.env.ALLSTARR_SCREENSHOT_DIR)
          await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/activity-${view.toLowerCase()}-${width}-${theme}.png`, fullPage: true });
        await page.getByRole("tab", { name: view, exact: true }).focus();
        await page.keyboard.press("Home");
        await expect(page.getByRole("tab", { name: "History", exact: true })).toBeFocused();
        await expect(page.getByRole("tab", { name: "History", exact: true })).toHaveAttribute("aria-selected", "true");
      });
    }
  }
}

for (const view of ["Jobs", "Tasks"] as const) {
  test(`Activity ${view} recovers from errors into an accessible empty state`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await mockApi(page);
    let fail = true;
    await page.route(view === "Tasks" ? "**/api/admin/activity/tasks" : "**/api/admin/jobs?*", route =>
      route.fulfill(fail ? { status: 503, json: { error: "fixture-private-response" } } :
        { json: view === "Tasks" ? { items: [] } : { jobs: [], progress: [], hasMore: false } }));
    await page.goto("#/activity");
    await page.getByRole("tab", { name: view, exact: true }).click();
    const alert = page.getByRole("alert", { name: view + " unavailable", exact: true });
    await expect(alert).toBeVisible();
    await expect(page.getByText("fixture-private-response")).toHaveCount(0);
    fail = false;
    await alert.getByRole("button", { name: "Try again" }).click();
    await expect(page.getByText(view === "Jobs" ? "No background work yet" : "No scheduled tasks", { exact: true })).toBeVisible();
    await expectContainedMobileGeometry(page);
  });
}

test("Activity Jobs collapses repeated routine runs into one row", async ({ page }) => {
  await mockApi(page);
  const run = (id: string, time: string, extra = {}) => ({ id, type: "library.index", state: "Succeeded", ownerName: "Josh",
    source: "Jellyfin library", completedAt: time, updatedAt: time, ...extra });
  await page.route("**/api/admin/jobs?*", route => route.fulfill({ json: new URL(route.request().url()).searchParams.get("view") === "history"
    ? { jobs: [run("scan-3", "2026-10-15T13:00:00Z"), run("scan-2", "2026-10-15T12:30:00Z"), run("scan-1", "2026-10-15T12:00:00Z"),
      run("scan-failed", "2026-10-15T11:30:00Z", { state: "Failed", lastErrorMessage: "Jellyfin did not answer." })], progress: [], hasMore: false }
    : { jobs: [], progress: [], hasMore: false } }));
  await page.route("**/api/admin/jobs/scan-3?*", route => route.fulfill({ json: { job: run("scan-3", "2026-10-15T13:00:00Z"), attempts: [], hasMore: false } }));
  await page.goto("#/activity");
  await page.getByRole("tab", { name: "Jobs", exact: true }).click();
  const grouped = page.getByRole("button", { name: /Refresh library index · 3 runs/ });
  await expect(grouped).toBeVisible();
  await expect(page.getByRole("button", { name: /Refresh library index/ })).toHaveCount(2);
  await grouped.click();
  await expect(page.locator("[data-side-panel] .group-members .item-row")).toHaveCount(3);
  await expect(page.locator('[data-job-id="scan-failed"]')).toContainText("Jellyfin did not answer.");
});

test("Activity queue refreshes after reconnect and sends server-side filters", async ({ page }) => {
  await mockApi(page);
  await page.addInitScript(() => {
    let source: FixtureStream;
    class FixtureStream {
      listeners = new Map<string, Array<(event: MessageEvent<string>) => void>>();
      onerror?: () => void;
      constructor() { source = this; }
      addEventListener(name: string, listener: (event: MessageEvent<string>) => void) {
        this.listeners.set(name, [...this.listeners.get(name) ?? [], listener]);
        if (name === "stream-status") setTimeout(() => listener(new MessageEvent("stream-status", { data: "{}" })), 0);
      }
      close() {}
    }
    Object.defineProperty(window, "EventSource", { value: FixtureStream });
    window.__emitAllstarrUpdate = () => {
      source.onerror?.();
      for (const listener of source.listeners.get("stream-status") ?? []) listener(new MessageEvent("stream-status", { data: "{}" }));
    };
  });
  let completed = false;
  let selectedState = "";
  await page.route("**/api/admin/jobs?*", route => {
    const params = new URL(route.request().url()).searchParams;
    if (params.get("view") === "queue") selectedState = params.get("state") ?? "";
    return route.fulfill({ json: {
      jobs: params.get("view") !== "queue" || completed ? [] : [{ id: "live-work", type: "playlist.materialize", state: "Running", updatedAt: "2026-10-15T00:00:00Z", canCancel: true }],
      progress: [], hasMore: false,
    }});
  });
  await page.goto("#/activity");
  await page.getByRole("tab", { name: "Jobs", exact: true }).click();
  await expect(page.getByText("Update playlist", { exact: true })).toBeVisible();
  await page.getByText("More filters", { exact: true }).click();
  await page.getByRole("button", { name: "Job state", exact: true }).click();
  await page.getByRole("option", { name: "Running", exact: true }).click();
  await expect.poll(() => selectedState).toBe("Running");
  completed = true;
  await page.evaluate(() => window.__emitAllstarrUpdate?.());
  await expect(page.getByText("No jobs match these filters", { exact: true })).toBeVisible();
  await expect(page.getByRole("status", { name: "Live updates live", exact: true })).toBeVisible();
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Activity ownership scope at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await page.emulateMedia({ reducedMotion: "reduce" });
    await mockApi(page);
    await page.addInitScript(() => {
      let stream: FixtureStream;
      class FixtureStream {
        listeners = new Map<string, (event: MessageEvent<string>) => void>();
        constructor() { stream = this; }
        addEventListener(name: string, listener: (event: MessageEvent<string>) => void) { this.listeners.set(name, listener); }
        close() {}
      }
      Object.defineProperty(window, "EventSource", { value: FixtureStream });
      window.__emitAllstarrUpdate = () => stream.listeners.get("update")?.(new MessageEvent("update", {
        data: JSON.stringify({ eventId: "failed-foreign", resource: "job", resourceId: "other-scan", requiresAdministrator: true }),
      }));
    });
    const requests: Array<{ path: string; scope: string | null }> = [];
    for (const path of ["/api/admin/jobs", "/api/admin/ui/activity", "/api/admin/activity/tasks"]) {
      await page.route(`**${path}*`, route => {
        const url = new URL(route.request().url());
        const scope = url.searchParams.get("scope");
        requests.push({ path, scope });
        const everyone = scope === "everyone";
        if (path.endsWith("/jobs")) return route.fulfill({ json: {
          jobs: [{ id: "own-scan", type: "library.index", state: "Succeeded", ownerName: "Josh", source: "Jellyfin library", updatedAt: "2026-10-15T12:00:00Z" },
            ...(everyone ? [{ id: "other-scan", type: "library.index", state: "Succeeded", ownerName: "Alex", source: "Jellyfin library", updatedAt: "2026-10-15T12:00:00Z" }] : [])],
          progress: [], hasMore: false,
        } });
        if (path.endsWith("/tasks")) return route.fulfill({ json: { items: [{ id: "schedule", name: everyone ? "Alex's playlist" : "Josh's playlist", ownerName: everyone ? "Alex" : "Josh", kind: "playlist", cadence: "Daily", state: "Scheduled", enabled: true }] } });
        return route.fulfill({ json: { items: [{ id: "own-event", kind: "library", label: "Library scan completed", source: "Jellyfin library", providerId: "library", state: "succeeded", detail: "Indexed music", actorUserId: "private-owner-id", actorDisplayName: "Josh", occurredAt: "2026-10-15T12:00:00Z" },
          ...(everyone ? [{ id: "other-event", kind: "library", label: "Library scan completed", source: "Jellyfin library", providerId: "library", state: "succeeded", detail: "Indexed music", actorUserId: "private-other-id", actorDisplayName: "Alex", occurredAt: "2026-10-15T12:00:00Z" }] : [])], hasMore: false } });
      });
    }
    await page.goto("#/activity");
    const scope = page.getByRole("group", { name: "Activity scope" });
    await expect(scope.getByRole("button", { name: "Mine", exact: true })).toHaveAttribute("aria-pressed", "true");
    await page.getByRole("tab", { name: "Jobs", exact: true }).click();
    await expect(page.locator('[data-job-id="own-scan"]')).toBeVisible();
    await expect(page.locator('[data-job-id="other-scan"]')).toHaveCount(0);
    expect(requests.filter(r => r.path !== "/api/admin/activity/tasks").every(r => r.scope === "mine")).toBe(true);
    await page.evaluate(() => window.__emitAllstarrUpdate?.());
    await expect(page.getByRole("status").filter({ hasText: "Household work needs attention." })).toBeVisible();
    await expect(page.locator('[data-job-id="other-scan"]')).toHaveCount(0);
    await page.getByRole("button", { name: "Review household work", exact: true }).focus();
    await page.keyboard.press("Enter");
    await expect(scope.getByRole("button", { name: "Everyone", exact: true })).toBeFocused();
    await expect(scope.getByRole("button", { name: "Everyone", exact: true })).toHaveAttribute("aria-pressed", "true");
    await expect(page.locator('[data-job-id="other-scan"]')).toContainText("Alex");
    await page.getByRole("tab", { name: "History", exact: true }).click();
    const other = page.locator(".event-log-group").filter({ hasText: "Alex" });
    await expect(other).toBeVisible();
    await expect(other.locator(".item-row")).toContainText("Jellyfin library");
    await expect(page.getByText("Unknown Source", { exact: true })).toHaveCount(0);
    await expect(page.getByText("private-other-id", { exact: true })).not.toBeVisible();
    await other.locator(".row-action").click();
    await page.locator("[data-side-panel]").getByText("Technical details", { exact: true }).click();
    await expect(page.locator("[data-side-panel]").getByText("private-other-id", { exact: true })).toBeVisible();
    await expectContainedMobileGeometry(page);
    if (process.env.ALLSTARR_SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/activity-ownership-${width}-${theme}.png`, fullPage: true });
    await page.reload();
    await expect(scope.getByRole("button", { name: "Everyone", exact: true })).toHaveAttribute("aria-pressed", "true");
    await page.getByRole("tab", { name: "Jobs", exact: true }).click();
    await expect(page.locator('[data-job-id="other-scan"]')).toBeVisible();
    await page.getByRole("tab", { name: "Tasks", exact: true }).click();
    await expect(page.getByText("Alex's playlist", { exact: true })).toBeVisible();
    await scope.getByRole("button", { name: "Mine", exact: true }).click();
    await expect(page.getByText("Josh's playlist", { exact: true })).toBeVisible();
    await expect(page.getByText("Alex's playlist", { exact: true })).toHaveCount(0);
    await page.getByRole("tab", { name: "Jobs", exact: true }).click();
    await expect(page.locator('[data-job-id="own-scan"]')).toBeVisible();
    await expect(page.locator('[data-job-id="other-scan"]')).toHaveCount(0);
    await expect(page.locator(".event-log-group").filter({ hasText: "Alex" })).toHaveCount(0);
    await expectContainedMobileGeometry(page);
  });
}
