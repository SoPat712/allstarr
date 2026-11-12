import { expect, test, type Page, type Route } from "@playwright/test";
import { resolve } from "node:path";

const recording = "10000000-0000-0000-0000-000000000003";
const pendingId = "20000000-0000-0000-0000-000000000001";
const runId = "30000000-0000-0000-0000-000000000001";
const rootId = "40000000-0000-0000-0000-000000000001";
const cover = "data:image/svg+xml," + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="80" height="80"><defs><linearGradient id="g"><stop stop-color="#18c66b"/><stop offset="1" stop-color="#08723b"/></linearGradient></defs><path fill="url(#g)" d="M0 0h80v80H0z"/></svg>');
const album = { title: "SOS", artist: "SZA", year: 2022, songCount: 23, coverReference: cover };
const plan = { album, canAddDirectly: false, backendLibraryId: "music", rootId, alreadyInLibrary: 0, destinationPath: "/music/Requester/SZA/SOS/01 - SOS.flac", bestCopies: [{ provider: "qobuz", codec: "flac", bitDepth: 16, sampleRate: 44100, verdict: "meets-your-floor", reason: "Your floor is lossless 16/44" }], profileSteps: [{ key: "quality", title: "Check the copy is really lossless", description: "Off in your profile", enabled: false }, { key: "tags-cover", title: "MusicBrainz tags and cover art", description: "Release IDs and cover.jpg", enabled: true }] };
const request = { id: pendingId, requesterUserId: "sam", requester: { id: "sam", displayName: "Sam" }, recordingId: recording, ...album, unit: "album", state: "pending", createdAt: "2026-11-06T14:00:00Z", runId: null };
const done = (name: string, reason: string) => ({ name, outcome: "Complete", reason });
const running = { id: runId, ...album, unit: "album", state: "active", startedAt: "2026-11-06T14:02:00Z", counts: { total: 1, added: 0, needsLook: 0 }, step: "Lyrics", outcome: "Running", tracks: [{ recordingId: recording, song: { title: "SOS", artist: "SZA", album: "SOS", track: 1 }, provider: "qobuz", step: "Lyrics", outcome: "Running", reason: "Looking for matching lyrics", steps: [done("Acquire", "Verified copy downloaded"), done("Quality", "No upscaled files found"), done("Tags", "Release tags and cover written"), { name: "Lyrics", outcome: "Running", reason: "Looking for matching lyrics" }] }] };
async function json(route: Route, body: unknown, status = 200) { return route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) }); }
async function fixture(page: Page, administrator = true, pending = true, direct = false) {
  const state = { requests: (pending ? [structuredClone(request), ...(administrator ? [{ ...structuredClone(request), id: "20000000-0000-0000-0000-000000000002", title: "Short n’ Sweet", artist: "Sabrina Carpenter", songCount: 12, requester: { id: "pat", displayName: "Pat" }, requesterUserId: "pat" }] : [])] : []) as any[], runs: [] as any[], writes: [] as { path: string; body: any }[], failApprove: false, failPreview: false };
  await page.addInitScript(() => {
    class UpdateStream extends EventTarget { readyState = 1; onerror = null; close() { this.readyState = 2; } }
    window.EventSource = UpdateStream as unknown as typeof EventSource;
  });
  await page.clock.install({ time: new Date("2026-11-06T15:00:00Z") });
  await page.route("**/api/admin/**", async route => {
    const req = route.request(), url = new URL(req.url()), path = url.pathname, method = req.method();
    if (path.endsWith("/auth/me")) return json(route, { authenticated: true, backend: "Jellyfin", user: { id: administrator ? "owner" : "sam", allstarrUserId: administrator ? "owner" : "sam", name: administrator ? "joshp" : "Sam", isAdministrator: administrator } });
    if (path.endsWith("/auth/oidc/status")) return json(route, { enabled: false });
    if (path.endsWith("/onboarding/status")) return json(route, { completed: true, setupOpen: false, shouldRedirectToSetup: false, recoveryNotices: [] });
    if (path.endsWith("/ui/schema")) return json(route, { activeBackend: "Jellyfin", providers: [], settings: [], priorityGroups: [] });
    if (path.endsWith("/library-promotions/preview")) return json(route, { ...plan, canAddDirectly: direct });
    if (path === "/api/admin/library-promotions/requests" && method === "GET") {
      expect(url.searchParams.get("state")).toBe("pending");
      return json(route, state.requests.filter(item => item.state === "pending" && (administrator || item.requesterUserId === "sam")));
    }
    if (path === "/api/admin/library-promotions" && method === "GET") return json(route, { items: url.searchParams.get("state") === "added" ? [] : state.runs, nextCursor: null, counts: { active: state.runs.length, needsLook: 0, added: 0, all: state.runs.length, addedToday: 0 } });
    if (method !== "GET") state.writes.push({ path, body: req.postData() ? req.postDataJSON() : null });
    if (path === "/api/admin/library-promotions/requests" && method === "POST") { const receipt = structuredClone(request); state.requests.push(receipt); return json(route, { requested: true, request: receipt }, 202); }
    if (path === "/api/admin/library-promotions" && method === "POST") { state.runs.push(structuredClone(running)); return json(route, running, 202); }
    const match = path.match(/\/library-promotions\/requests\/([^/]+)(.*)$/);
    if (match) {
      const receipt = state.requests.find(item => item.id === match[1]);
      if (match[2] === "/preview") return state.failPreview ? json(route, { error: "The requester’s library is unavailable." }, 409) : json(route, plan);
      if (match[2] === "/approve") {
        if (state.failApprove) return json(route, { error: "The request changed. Try again." }, 409);
        receipt.state = "approved"; receipt.runId = runId; state.runs.push({ ...structuredClone(running), title: receipt.title });
      }
      if (match[2] === "/decline") { receipt.state = "declined"; receipt.declineNote = req.postDataJSON().note; }
      if (match[2] === "/cancel") receipt.state = "cancelled";
      return json(route, receipt);
    }
    if (path.endsWith(`/library-promotions/${runId}`)) return json(route, state.runs[0] ?? running);
    return json(route, {});
  });
  return state;
}
async function capture(page: Page, name: string) {
  const path = resolve(`test-results/requests/${name}.png`);
  const title = page.locator('[data-side-panel][data-state="open"] [data-dialog-title]');
  if (await title.count()) await title.click();
  await page.screenshot({ path, fullPage: true });
  await test.info().attach(name, { path, contentType: "image/png" });
}
async function geometry(page: Page, width: number) {
  const panel = page.locator("[data-side-panel]");
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(await panel.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  if (width === 390) {
    const box = await panel.boundingBox(); expect(box?.x).toBe(0); expect(box?.width).toBe(390); expect(box?.height).toBe(960);
    expect(await panel.locator("button").evaluateAll(buttons => { const hit = (element: Element) => { const box = element.getBoundingClientRect(), area = getComputedStyle(element, "::after"), touch = area.content === "none" ? 0 : 1; return { width: Math.max(box.width, touch * (parseFloat(area.minWidth) || 0)), height: Math.max(box.height, touch * (parseFloat(area.minHeight) || 0)) }; }; return buttons.filter(button => button.getBoundingClientRect().height > 0).every(button => hit(button).height >= 44 && hit(button).width >= 44); })).toBe(true);
  }
}

test("Admin reviews the requester’s profile and approves into adding now", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.addInitScript(() => localStorage.setItem("allstarr.theme", "dark"));
  const state = await fixture(page); await page.goto("/#/library/to-library");
  const group = page.getByRole("region", { name: "Waiting for approval", exact: true });
  await expect(group).toContainText("SZA · requested by Sam · 23 songs");
  await expect(group.locator(".state-pill")).toHaveText(["Request", "Request"]);
  await expect(page.getByLabel("Library additions")).toContainText("2 waiting for approval");
  await expect(page.getByRole("radio", { name: "Active 2", exact: true })).toBeVisible();
  await group.getByRole("button", { name: "Review", exact: true }).first().click();
  const panel = page.getByRole("dialog", { name: "SOS", exact: true });
  await expect(panel).toContainText("Requested by Sam"); await expect(panel).toContainText("Requester/SZA");
  await expect(panel.locator(".request-context time")).toHaveAttribute("datetime", request.createdAt);
  await expect(panel.locator(".request-context time")).toContainText("Nov 6,");
  await expect(panel.locator(".request-context time")).not.toContainText("2026");
  await expect(panel).toContainText("What happens · requester’s profile"); await expect(panel).toContainText("Off");
  await page.evaluate(async () => { await document.fonts.ready; });
  await page.screenshot({ path: test.info().outputPath("request-panel-1280-dark.png") });
  await panel.getByRole("button", { name: "Approve and add", exact: true }).click();
  await expect(page.getByRole("region", { name: "Adding now", exact: true })).toContainText("SOS");
  await expect(group).not.toContainText("SOS"); await expect(panel.getByRole("progressbar")).toBeVisible();
  expect(state.writes).toEqual([{ path: `/api/admin/library-promotions/requests/${pendingId}/approve`, body: null }]);
});
for (const note of ["", "We already have the edition we want."]) test(`Admin declines a request ${note ? "with a note" : "without a note"}`, async ({ page }) => {
  const state = await fixture(page); await page.goto(`/#/library/to-library?request=${pendingId}`);
  await page.getByRole("button", { name: "Decline…", exact: true }).click();
  const dialog = page.getByRole("alertdialog", { name: "Decline this request?", exact: true });
  if (note) await dialog.getByRole("textbox", { name: "Note (optional)", exact: true }).fill(note);
  await dialog.getByRole("button", { name: "Decline request", exact: true }).click();
  await expect(dialog).not.toBeVisible();
  await expect(page.getByRole("region", { name: "Waiting for approval", exact: true })).not.toContainText("SOS");
  expect(state.writes[0]).toEqual({ path: `/api/admin/library-promotions/requests/${pendingId}/decline`, body: note ? { note } : {} });
});
test("A failed approval keeps the request review open and retryable", async ({ page }) => {
  const state = await fixture(page); state.failApprove = true; await page.goto(`/#/library/to-library?request=${pendingId}`);
  const panel = page.getByRole("dialog", { name: "SOS", exact: true });
  await panel.getByRole("button", { name: "Approve and add", exact: true }).click();
  await expect(panel.getByRole("alert")).toContainText("The request changed");
  state.failApprove = false; await panel.getByRole("button", { name: "Approve and add", exact: true }).click();
  await expect(panel.getByRole("progressbar")).toBeVisible();
});
test("An unavailable requester preview prevents approval", async ({ page }) => {
  const state = await fixture(page); state.failPreview = true; await page.goto(`/#/library/to-library?request=${pendingId}`);
  const panel = page.getByRole("dialog", { name: "SOS", exact: true });
  await expect(panel.getByRole("alert")).toContainText("library is unavailable");
  await expect(panel.getByRole("button", { name: "Approve and add", exact: true })).toBeDisabled();
  expect(state.writes).toHaveLength(0);
});
test("A listener follows and cancels only their pending requests", async ({ page }) => {
  const state = await fixture(page, false); await page.goto("/#/library/to-library");
  const group = page.getByRole("region", { name: "Waiting for approval", exact: true });
  await expect(group).toContainText("requested by Sam"); await expect(group).not.toContainText("Pat");
  await expect(group.getByRole("button", { name: "Review", exact: true })).toHaveCount(0);
  await group.getByRole("button", { name: "Cancel request", exact: true }).click();
  await expect(group).toHaveCount(0); await expect(page.getByRole("radio", { name: "Active 0", exact: true })).toBeVisible();
  expect(state.writes[0].path).toBe(`/api/admin/library-promotions/requests/${pendingId}/cancel`);
});
test("A listener sheet follows approval and completion while open", async ({ page }) => {
  const state = await fixture(page, false); await page.goto(`/#/library/to-library?recording=${recording}`);
  const panel = page.getByRole("dialog", { name: "SOS", exact: true });
  await expect(panel).toContainText("Requested · waiting for an admin");
  state.requests[0].state = "approved"; state.requests[0].runId = runId;
  await page.clock.fastForward(2600);
  await expect(panel).toContainText("Approved · adding to the library");
  await expect(panel.getByRole("button", { name: "Cancel request", exact: true })).toHaveCount(0);
  await expect(page.getByLabel("Library additions")).toContainText("0 waiting for approval");
  state.requests[0].state = "added";
  await page.clock.fastForward(2600);
  await expect(panel).toContainText("In library");
});
test("Cancel closes a listener album preview without sending a request", async ({ page }) => {
  const state = await fixture(page, false, false); await page.goto(`/#/library/to-library?recording=${recording}`);
  const panel = page.getByRole("dialog", { name: "SOS", exact: true });
  await panel.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(panel).toHaveCount(0); expect(state.writes).toHaveLength(0);
});
test("Timeline dots and subtext distinguish completed, current and upcoming stages", async ({ page }) => {
  await fixture(page); await page.goto(`/#/library/to-library?run=${runId}`);
  const steps = page.locator(".promotion-timeline li"); await expect(steps).toHaveCount(6);
  expect(await steps.evaluateAll(items => items.map(item => item.getAttribute("data-state")))).toEqual(["complete", "complete", "complete", "current", "upcoming", "upcoming"]);
  const colors = await steps.evaluateAll(items => items.map((item, index) => {
    const token = index < 3 ? "--success" : index === 3 ? "--info" : "--color-edge-strong";
    const probe = document.createElement("i"); item.append(probe); probe.style.backgroundColor = `var(${token})`;
    const expected = getComputedStyle(probe).backgroundColor, actual = getComputedStyle(item, "::before").backgroundColor; probe.remove(); return { expected, actual };
  }));
  expect(colors.every(color => color.actual === color.expected)).toBe(true);
  await expect(steps.nth(3)).toContainText("Looking for matching lyrics"); await expect(steps.nth(4).locator("small")).toHaveText("Next"); await expect(steps.nth(5).locator("small")).toHaveText("After the library finds the files");
});
for (const viewport of [{ width: 1280, theme: "dark" }, { width: 390, theme: "light" }, { width: 390, theme: "dark" }] as const) {
  test(`Listener requests and cancels at ${viewport.width} ${viewport.theme}`, async ({ page }) => {
    await page.setViewportSize({ width: viewport.width, height: 960 }); await page.emulateMedia({ colorScheme: viewport.theme, reducedMotion: "reduce" });
    const state = await fixture(page, false, false); await page.goto(`/#/library/to-library?recording=${recording}`);
    const panel = page.getByRole("dialog", { name: "SOS", exact: true });
    await expect(panel).toContainText("An admin approves requests. You'll see it in your library when it's added.");
    await expect(panel).toContainText("Best copy found"); await expect(panel).toContainText("Qobuz · FLAC 16/44"); await expect(panel).toContainText("None of its 23 songs");
    await expect(panel.getByRole("button", { name: "Add album to library", exact: true })).toHaveCount(0);
    expect((await panel.getByRole("button", { name: "Request album", exact: true }).boundingBox())!.y).toBeLessThan(500);
    await geometry(page, viewport.width); await capture(page, `listener-${viewport.width}-${viewport.theme}`);
    await panel.getByRole("button", { name: "Request album", exact: true }).click();
    await expect(panel).toContainText("Requested · waiting for an admin"); await expect(panel.getByRole("button", { name: "Request album", exact: true })).toHaveCount(0);
    expect(state.writes[0]).toMatchObject({ path: "/api/admin/library-promotions/requests", body: { recordingId: recording, backendLibraryId: "music", rootId, unit: "album" } });
    await capture(page, `requested-${viewport.width}-${viewport.theme}`);
    await page.reload(); await expect(panel).toContainText("Requested · waiting for an admin");
    await panel.getByRole("button", { name: "Cancel request", exact: true }).click();
    await expect(panel.getByRole("button", { name: "Request album", exact: true })).toBeVisible();
    expect(state.writes.at(-1)?.path).toBe(`/api/admin/library-promotions/requests/${pendingId}/cancel`);
  });
  test(`Approval queue and review fit at ${viewport.width} ${viewport.theme}`, async ({ page }) => {
    await page.setViewportSize({ width: viewport.width, height: 960 }); await page.emulateMedia({ colorScheme: viewport.theme, reducedMotion: "reduce" });
    const state = await fixture(page);
    state.runs.push({ ...structuredClone(running), title: "good kid, m.A.A.d city (Deluxe)", artist: "Kendrick Lamar" });
    await page.goto("/#/library/to-library");
    await expect(page.getByRole("region", { name: "Waiting for approval", exact: true })).toContainText("Short n’ Sweet");
    expect(await page.getByRole("region", { name: "Waiting for approval", exact: true }).evaluate(element => getComputedStyle(element).borderLeftWidth)).toBe("0px");
    await capture(page, `queue-${viewport.width}-${viewport.theme}`);
    await page.getByRole("button", { name: "Open good kid, m.A.A.d city (Deluxe)", exact: true }).click();
    await expect(page.getByRole("progressbar")).toBeVisible();
    await geometry(page, viewport.width); await capture(page, `queue-run-${viewport.width}-${viewport.theme}`);
    await page.getByRole("button", { name: "Close", exact: true }).click();
    await page.getByRole("button", { name: "Review", exact: true }).first().click();
    await expect(page.getByRole("button", { name: "Approve and add", exact: true })).toBeEnabled();
    await geometry(page, viewport.width); await capture(page, `review-${viewport.width}-${viewport.theme}`);
  });
}
for (const administrator of [true, false]) test(`${administrator ? "Admin" : "Trusted listener"} keeps the direct add action`, async ({ page }) => {
  const state = await fixture(page, administrator, false, true); await page.goto(`/#/library/to-library?recording=${recording}`);
  await page.getByRole("button", { name: "Add album to library", exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`to-library\\?run=${runId}`));
  expect(state.writes[0].path).toBe("/api/admin/library-promotions");
});
