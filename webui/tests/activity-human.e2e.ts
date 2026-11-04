import { expect, test, type Locator, type Page } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";
import { capture } from "./for-you-fixtures";
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
async function mockActivity(page: Page, events: unknown[] = rows, options: { tasks?: unknown[]; job?: Record<string, unknown>; jobs?: Record<string, unknown>[]; attempts?: unknown[] } = {}) {
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
    if (path === "/api/admin/jobs") return reply({ jobs: (options.jobs ?? []).filter(item => url.searchParams.get("view") === "queue" ? item.state === "Running" : item.state !== "Running"), progress: [], hasMore: false });
    if (path === `/api/admin/jobs/${jobId}`) { reads.push(path); return reply({ job: { ...job, ...options.job }, attempts: options.attempts ?? [{ attemptNumber: 1, startedAt: "2026-10-26T12:00:03Z", completedAt: "2026-10-26T12:00:08Z", outcome: "failed", errorMessage: "The source did not return audio before the time limit." }], hasMore: false }); }
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return reply({});
  });
  return { reads, writes };
}
async function expectOutcome(badge: Locator, tone: string) {
  await expect(badge).toHaveClass(new RegExp(`badge-${tone}`));
  await expect(badge.locator("svg")).toHaveCount(1);
  const colors = await badge.evaluate(element => {
    const style = getComputedStyle(element);
    const luminance = (color: string) => {
      const channels = color.match(/[\d.]+/g)!.slice(0, 3).map(value => {
        const channel = Number(value) / 255;
        return channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4;
      });
      return channels[0] * .2126 + channels[1] * .7152 + channels[2] * .0722;
    };
    const ink = luminance(style.color), background = luminance(style.backgroundColor);
    return { color: style.color, background: style.backgroundColor, contrast: (Math.max(ink, background) + .05) / (Math.min(ink, background) + .05) };
  });
  expect(colors.contrast).toBeGreaterThanOrEqual(4.5);
  expect((await badge.textContent())?.trim()).toBeTruthy();
  return colors;
}
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Activity explains outcomes with color and context at ${width}px ${theme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 }); await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const fixture = await mockActivity(page); await page.goto("/#/activity");
    const groups = page.locator(".event-log-group"); await expect(groups).toHaveCount(6);
    const tones = ["success", "danger", "accent", "warning", "warning", "neutral"];
    const colors = [];
    for (let index = 0; index < tones.length; index++) {
      await expect(groups.nth(index).locator(".item-row .event-kind-icon")).toHaveAttribute("data-tone", tones[index]);
      colors.push(await expectOutcome(groups.nth(index).locator(".item-row [data-slot=badge]"), tones[index]));
    }
    expect(new Set(colors.slice(0, 4).map(value => value.color)).size).toBe(4);
    expect(new Set(colors.slice(0, 4).map(value => value.background)).size).toBe(4);
    const iconColor = async (index: number) => groups.nth(index).locator(".item-row .event-kind-icon").evaluate(element => getComputedStyle(element).color);
    expect(await iconColor(0)).not.toBe(await iconColor(5));
    expect(await iconColor(1)).not.toBe(await iconColor(0));
    expect(await iconColor(2)).not.toBe(await iconColor(0));
    await expect(groups.first().locator(".item-row")).toContainText("Housewife");
    await expect(groups.first().locator(".item-row")).toContainText("Apple Music");
    const failed = groups.nth(1); await failed.locator(".row-action").focus(); await page.keyboard.press("Enter");
    await expect(page.locator("[data-side-panel]")).toContainText(job.recovery);
    const action = page.locator("[data-side-panel]").getByRole("link", { name: "Open related work", exact: true });
    await expect(action).toHaveAttribute("href", `#/activity?view=jobs&job=${jobId}`);
    await expect(failed.locator(".item-row")).not.toContainText(jobId);
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.evaluate(() => window.scrollTo(0, 0)); await page.screenshot({ path: testInfo.outputPath(`activity-history-${width}-${theme}.png`), fullPage: true });
    await action.click();
    await expect(page.getByRole("tab", { name: "Jobs", exact: true })).toHaveAttribute("aria-selected", "true");
    const selected = page.locator("[data-side-panel]");
    await expect(selected.getByRole("heading", { name: /Keep a song/ })).toBeVisible();
    await expect(selected).toContainText(job.targetName); await expect(selected).toContainText(job.recovery);
    await expect(page.getByRole("region", { name: "Attempt history" })).toContainText("Attempt 1");
    await expectOutcome(selected.locator(".job-attempts [data-slot=badge]"), "danger");
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
  await expect(group.locator(".item-row [data-slot=badge]")).toHaveClass(/badge-success/);
  await group.locator(".row-action").click(); await expect(page.locator("[data-side-panel]")).toContainText("The first source timed out.");
  await page.locator("[data-side-panel]").getByRole("button", { name: "Close", exact: true }).click();
  await page.getByRole("radio", { name: /^Problems/ }).click();
  await expect(page.getByText("Nothing matches these filters", { exact: true })).toBeVisible();
});

test("Activity consolidates repeated song updates and expands all items", async ({ page }) => {
  const updates = Array.from({ length: 100 }, (_, index) => ({
    id: `update-${index}`, jobId: `00000000-0000-4000-8000-${String(index).padStart(12, "0")}`,
    kind: "job", source: "Recordings", label: "Song details updated", state: "succeeded",
    detail: "Song details updated: attempt 1 completed.", sourceTitle: `Song ${index + 1}`,
    action: "recording.tags.refresh", actorUserId: "viewer", actorDisplayName: "Josh",
    occurredAt: "2026-10-29T05:54:00Z",
  }));
  await mockActivity(page, [...updates, ...updates.slice(0, 2).map((item, index) => ({
    ...item, id: `failure-${index}`, jobId: `10000000-0000-4000-8000-${String(index).padStart(12, "0")}`,
    state: "failed", severity: "error", detail: `Song ${index + 1} could not be updated.`,
  }))]);
  await page.goto("/#/activity");
  const groups = page.locator(".event-log-group");
  await expect(groups).toHaveCount(2);
  const succeeded = groups.first(), failed = groups.last();
  await expect(succeeded.locator(".item-row")).toContainText("Song details updated · 100 songs");
  await expect(succeeded.locator(".item-row [data-slot=badge]")).toHaveText("Completed");
  await expectOutcome(succeeded.locator(".item-row [data-slot=badge]"), "success");
  await expect(succeeded.locator(".item-row .event-kind-icon")).toHaveAttribute("data-tone", "success");
  await expect(page.locator("[data-side-panel] .event-child").first()).not.toBeVisible();
  await succeeded.locator(".row-action").click();
  await expect(page.locator("[data-side-panel] .event-child")).toHaveCount(100);
  await expect(page.locator("[data-side-panel]").getByText("Song 1", { exact: true })).toBeVisible();
  await expect(page.locator("[data-side-panel]").getByText("Song 100", { exact: true })).toBeVisible();
  await page.locator("[data-side-panel]").getByRole("button", { name: "Close", exact: true }).click();
  await expect(failed.locator(".item-row")).toContainText("Song details updated · 2 songs");
  await expectOutcome(failed.locator(".item-row [data-slot=badge]"), "danger");
  await failed.locator(".row-action").click();
  await expect(page.locator("[data-side-panel] .event-child")).toHaveCount(2);
  await expect(page.locator("[data-side-panel]").getByText("Song 1 could not be updated.", { exact: true })).toBeVisible();
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Activity colors jobs tasks attempts and diagnostics at ${width}px ${theme}`, async ({ page }, testInfo) => {
  await page.setViewportSize({ width, height: 900 });
  await page.emulateMedia({ colorScheme: theme });
  await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
  const states = ["Succeeded", "Failed", "Running"];
  await mockActivity(page, [
    { ...rows[0], id: "diagnostic-success", kind: "extension", state: "success", label: "Source connected" },
    { ...rows[1], id: "diagnostic-error", kind: "extension", jobId: undefined, state: "error", label: "Source unavailable" },
    { ...rows[2], id: "diagnostic-running", kind: "extension", state: "running", label: "Checking source" },
  ], {
    jobs: states.map((state, index) => ({ ...job, id: index === 1 ? jobId : `${index}1111111-1111-4111-8111-111111111111`, type: `fixture.${index}`, state })),
    tasks: states.map((state, index) => ({ id: String(index), kind: "maintenance", name: `Task ${index + 1}`, state, enabled: true, cadence: "Hourly" })),
    attempts: states.map((state, index) => ({ attemptNumber: index + 1, startedAt: job.updatedAt, outcome: state.toLowerCase() })),
  });
  await page.goto("/#/activity");
  const diagnostics = page.locator(".event-log-group .item-row [data-slot=badge]");
  for (const [index, tone] of ["success", "danger", "accent"].entries()) await expectOutcome(diagnostics.nth(index), tone);
  await page.getByRole("tab", { name: "Jobs", exact: true }).click();
  const jobs = page.locator(".job-row"); await expect(jobs).toHaveCount(3);
  const colors = [];
  for (const [index, tone] of ["success", "danger", "accent"].entries()) {
    const row = jobs.filter({ has: page.locator(`.badge-${tone}`) });
    colors.push(await expectOutcome(row.locator(".row-pill [data-slot=badge]"), tone));
    await expect(row.locator(".event-kind-icon")).toHaveAttribute("data-tone", tone);
  }
  expect(new Set(colors.map(value => value.color)).size).toBe(3);
  await page.locator(`[data-job-id="${jobId}"]  .row-action`).click();
  for (const [index, tone] of ["success", "danger", "accent"].entries()) await expectOutcome(page.locator(".job-attempts [data-slot=badge]").nth(index), tone);
  if (width === 390) await expectContainedMobileGeometry(page);
  await page.screenshot({ path: testInfo.outputPath(`activity-jobs-${width}-${theme}.png`), fullPage: true });
  await page.locator("[data-side-panel]").getByRole("button", { name: "Close", exact: true }).click();
  await page.getByRole("tab", { name: "Tasks", exact: true }).click();
  for (const [index, tone] of ["success", "danger", "accent"].entries()) await expectOutcome(page.locator(".activity-task [data-slot=badge]").nth(index), tone);
  if (width === 390) await expectContainedMobileGeometry(page);
  await page.screenshot({ path: testInfo.outputPath(`activity-tasks-${width}-${theme}.png`), fullPage: true });
});
test("Activity keeps an older reference out of the headline and makes it traceable", async ({ page }) => {
  await mockActivity(page, [{ id: "legacy", kind: "job", source: "system", label: "recording.keep", state: "failed", detail: jobId, jobId, actorUserId: "viewer", occurredAt: "2026-10-26T12:00:00Z" }]);
  await page.goto("/#/activity"); const group = page.locator(".event-log-group");
  await expect(group.locator(".item-row")).toContainText("Keep a song"); await expect(group.locator(".item-row")).not.toContainText(jobId);
  await group.locator(".row-action").click();
  await expect(page.locator("[data-side-panel]").getByRole("link", { name: "Open related work", exact: true })).toHaveAttribute("href", `#/activity?view=jobs&job=${jobId}`);
  await page.locator("[data-side-panel]").getByText("Technical details", { exact: true }).click(); await expect(page.locator("[data-side-panel]")).toContainText(jobId);
});

test("Tasks explains Health and backup schedules with relevant settings links", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 });
  const fixture = await mockActivity(page, rows, { tasks: [
    { id: "health", name: "Health checks", kind: "health", state: "Needs attention", enabled: true, cadence: "Hourly", detail: "Health check (server checks): 3 passed, 2 not checked, 1 failed. Failed step: Search because backend authentication was rejected.", nextRunAt: "2026-10-27T15:00:00Z", actionHref: "#/settings/health", actionLabel: "Open Health" },
    { id: "backup", name: "Database and keyring backups", kind: "backup", state: "Scheduled", enabled: true, cadence: "Daily at 03:00 · UTC", detail: "Completed; detailed result not retained.", actionHref: "#/settings/backups", actionLabel: "Open Backups" },
  ] });
  await page.goto("/#/activity?view=tasks");
  const tasks = page.locator(".activity-task"); await expect(tasks).toHaveCount(2);
  await tasks.first().locator(".task-open").click();
  await expect(page.locator("[data-side-panel]")).toContainText("authentication was rejected");
  await expect(tasks.first().locator("[data-slot=badge]")).toHaveClass(/badge-warning/);
  await expect(tasks.last().locator("[data-slot=badge]")).toHaveClass(/badge-accent/);
  await expect(page.locator("[data-side-panel]").getByRole("link", { name: "Open Health" })).toHaveAttribute("href", "#/settings/health");
  await page.locator("[data-side-panel]").getByRole("button", { name: "Close", exact: true }).click();
  await tasks.last().locator(".task-open").click();
  await expect(page.locator("[data-side-panel]").getByRole("link", { name: "Open Backups" })).toHaveAttribute("href", "#/settings/backups");
  await expectContainedMobileGeometry(page); expect(fixture.writes).toEqual([]);
});
test("A completed job with missing Health checks stays neutral", async ({ page }) => {
  await mockActivity(page, rows, { job: { type: "health.smoke", displayName: "Health check", state: "Succeeded", displayState: "not_checked", summary: "Health check result unavailable; detailed results were not retained.", targetName: "Server", recovery: "Review the current checks in Health." } });
  await page.goto(`/#/activity?view=jobs&job=${jobId}`);
  const selected = page.locator("[data-side-panel]");
  await expect(selected.locator(".job-panel > [data-slot=badge]")).toHaveText("Not checked");
  await expect(selected.locator(".job-panel > [data-slot=badge]")).toHaveClass(/badge-neutral/);
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
  await expect(group.locator(".item-row [data-slot=badge]")).toHaveText("Warning · 2 events");
  await expectOutcome(group.locator(".item-row [data-slot=badge]"), "warning");
  await group.locator(".row-action").click();
  await expectOutcome(page.locator("[data-side-panel] .event-child [data-slot=badge]").nth(0), "neutral");
  await expectOutcome(page.locator("[data-side-panel] .event-child [data-slot=badge]").nth(1), "warning");
});


async function mockPagedActivity(page: Page, pages: number, visiblePerPage = 10, consolidate = false) {
  await mockActivity(page);
  const requests: URL[] = [];
  let active = 0, peak = 0;
  await page.route("**/api/admin/ui/activity?**", async route => {
    const url = new URL(route.request().url());
    requests.push(url); active++; peak = Math.max(peak, active);
    const index = Number(url.searchParams.get("beforeId") || 0);
    const items = Array.from({ length: 100 }, (_, offset) => ({
      id: `page-${index}-${offset}`, kind: offset < visiblePerPage ? "playlist" : "job",
      label: offset < visiblePerPage ? consolidate ? "Playlist updated" : `Evening playlist ${index}-${offset}` : "Background task",
      source: "System", detail: "Finished", state: "succeeded",
      occurredAt: new Date(Date.UTC(2026, 9, 26, 12, 0, -(index * 100 + offset))).toISOString(),
    }));
    await route.fulfill({ json: { items, hasMore: index + 1 < pages, nextCursor: items.at(-1)!.occurredAt, nextCursorId: String(index + 1) } });
    active--;
  });
  return { requests, peak: () => peak };
}

test("Activity tops up filtered pages and automatically loads earlier events on scroll", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 700 });
  await page.emulateMedia({ reducedMotion: "reduce" });
  const fixture = await mockPagedActivity(page, 6);
  await page.goto("/#/activity");
  await expect(page.locator(".event-log-group")).toHaveCount(30);
  expect(fixture.requests).toHaveLength(3);
  expect(fixture.requests.every(url => url.searchParams.get("limit") === "100")).toBe(true);
  expect(fixture.requests[1].searchParams.get("beforeId")).toBe("1");
  await page.getByRole("button", { name: "Load earlier events" }).scrollIntoViewIfNeeded();
  await expect(page.locator(".event-log-group")).toHaveCount(60);
  expect(fixture.requests).toHaveLength(6);
  expect(fixture.peak()).toBe(1);
});

test("Activity tops up when search filters leave fewer than twenty five events", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 700 });
  const fixture = await mockPagedActivity(page, 7);
  await page.goto("/#/activity");
  await expect(page.locator(".event-log-group")).toHaveCount(30);
  await page.getByRole("searchbox", { name: "Search", exact: true }).fill("Evening playlist 6-");
  await expect(page.locator(".event-log-group")).toHaveCount(10);
  await expect(page.getByRole("button", { name: "Load earlier events" })).toHaveCount(0);
  expect(fixture.requests).toHaveLength(7);
});

test("Activity bounds a routine-only initial load to eight sequential pages", async ({ page }) => {
  const fixture = await mockPagedActivity(page, 20, 0);
  await page.addInitScript(() => { window.IntersectionObserver = class {
    observe() {} unobserve() {} disconnect() {} takeRecords() { return []; }
    root = null; rootMargin = ""; thresholds = [];
  } as unknown as typeof IntersectionObserver; });
  await page.goto("/#/activity");
  await expect(page.getByRole("button", { name: "Load earlier events" })).toBeEnabled();
  expect(fixture.requests).toHaveLength(8);
  expect(fixture.peak()).toBe(1);
  await page.getByRole("button", { name: "Load earlier events" }).click();
  await expect.poll(() => fixture.requests.length).toBe(16);
});

test("Activity counts displayed groups when repeated events consolidate", async ({ page }) => {
  await page.addInitScript(() => { window.IntersectionObserver = class {
    observe() {} unobserve() {} disconnect() {} takeRecords() { return []; }
    root = null; rootMargin = ""; thresholds = [];
  } as unknown as typeof IntersectionObserver; });
  const fixture = await mockPagedActivity(page, 20, 100, true);
  await page.goto("/#/activity");
  await expect(page.getByRole("button", { name: "Load earlier events" })).toBeEnabled();
  expect(fixture.requests).toHaveLength(8);
  expect(await page.locator(".event-log-group").count()).toBeLessThan(25);
});

test("Activity uses readable retry badges and local retry times", async ({ page }) => {

  await mockActivity(page, [{ ...rows[1], state: "RetryScheduled", label: "mixes.refresh", displayName: "Mix refresh", recovery: "Retry scheduled for 2026-11-03 04:00:27 UTC." }]);
  await page.goto("/#/activity");
  const group = page.locator(".event-log-group");
  await expect(group.locator(".item-row [data-slot=badge]")).toHaveText("Retry scheduled");
  await group.locator(".row-action").click();
  await expect(page.locator("[data-side-panel] .event-child-meta [data-slot=badge]")).toHaveText("Retry scheduled");
  const localTime = await page.evaluate(() => new Intl.DateTimeFormat(undefined, { timeStyle: "short" }).format(new Date("2026-11-03T04:00:27Z")));
  await expect(page.locator("[data-side-panel] .notice-copy")).toContainText(localTime);
  await expect(page.locator("[data-side-panel] .notice-copy")).not.toContainText("UTC");
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Activity filters share a phone row at ${width}px ${theme}`, async ({ page }) => {
  await page.setViewportSize({ width, height: 900 });
  await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
  await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
  await mockActivity(page); await page.goto("/#/activity");
  const search = page.getByRole("searchbox", { name: "Search", exact: true });
  await expect(search).toBeVisible();
  if (width === 390) {
    const bar = page.locator(".event-log-chip-row");
    const geometry = await bar.evaluate(element => {
      const rect = element.getBoundingClientRect();
      return { width: element.clientWidth, content: element.scrollWidth, top: rect.top,
        controls: [...element.querySelectorAll<HTMLElement>("button, select")].filter(control => control.getBoundingClientRect().width).map(control => {
          const box = control.getBoundingClientRect(); return { top: box.top, bottom: box.bottom, width: box.width, height: box.height };
        }) };
    });
    expect(geometry.content).toBeGreaterThan(geometry.width);
    expect(Math.max(...geometry.controls.map(control => control.top)) - Math.min(...geometry.controls.map(control => control.top))).toBeLessThanOrEqual(4);
    for (const control of geometry.controls) { expect(control.height).toBeGreaterThanOrEqual(44); expect(control.width).toBeGreaterThanOrEqual(44); }
    const searchBox = (await search.boundingBox())!;
    expect(searchBox.y + searchBox.height).toBeLessThan(geometry.top);
    const problems = page.getByRole("radio", { name: /^Problems/ });
    await problems.focus();
    expect(await problems.evaluate(element => getComputedStyle(element).outlineStyle)).not.toBe("none");
    await page.keyboard.press("Enter"); await expect(problems).toHaveAttribute("aria-checked", "true");
    await page.getByRole("radio", { name: /^All / }).click();
    await bar.evaluate(element => element.scrollTo({ left: 0, behavior: "instant" }));
    await page.getByRole("button", { name: "Everyone", exact: true }).click();
    await expect(page.getByRole("button", { name: "Everyone", exact: true })).toHaveAttribute("aria-pressed", "true");
    await expect(bar.getByRole("button", { name: "Everyone", exact: true })).toBeFocused();
  } else {
    await expect(page.locator(".activity-toolbar").getByRole("group", { name: "Activity scope", exact: true })).toBeVisible();
  }
  await capture(page, "activity-filters", width, theme);
});
