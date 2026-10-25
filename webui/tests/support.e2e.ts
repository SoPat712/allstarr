import { expect, test, type Page, type Route } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const listenerId = "11111111-1111-4111-8111-111111111111";
const reportId = "22222222-2222-4222-8222-222222222222";
const snapshotId = "33333333-3333-4333-8333-333333333333";
const captured = {
  schemaVersion: 1, capturedAt: "2026-10-25T03:00:00Z", subjectUserId: listenerId, subjectName: "Listener",
  song: { itemId: "ext-deezer-song-fixture", externalSnapshotId: snapshotId, canonicalRecordingId: null,
    libraryTrackId: null, title: "A track with a long title for the phone layout", artist: "Fixture artist", album: null,
    durationMilliseconds: 180000, catalogProviderId: "deezer" },
  match: { decisionId: null, state: "accepted", decisionVersion: 1, confidence: 0.95,
    authorityScope: "automatic", reasonCodes: [] },
  playback: { providerId: "deezer", sourceConfirmed: true, providerAccountId: null, accountRevision: null,
    openedAt: "2026-10-25T02:59:00Z", observedAt: "2026-10-25T03:00:00Z", cached: false,
    seekRung: "buffered", selectionReason: "preference-balanced-fast", positionTicks: 10000000, clientName: "Feishin" },
  checks: [],
};

async function reply(route: Route, value: unknown, status = 200) {
  return route.fulfill({ status, contentType: "application/json", body: JSON.stringify(value) });
}

async function mockSupport(page: Page, administrator = false, backend = "Jellyfin") {
  const state = { url: "http://music.test:5274" as string | null, contextFailure: false, createFailure: false,
    conflict: false, createContextFailure: false, createGate: null as Promise<void> | null,
    reportReads: [] as Array<string | null>, creates: [] as Array<Record<string, unknown>>,
    previews: [] as unknown[], updates: [] as Array<Record<string, unknown>>, status: "new", revision: 1,
    note: "No sound after seeking.", paged: false, matchScope: "automatic", matchState: "accepted",
    settingsWrites: [] as Array<Record<string, string>> };
  const summary = () => ({ id: reportId, kind: "seek", status: state.status, notePreview: "No sound after seeking.",
    reporterName: "Listener", subjectName: "Listener", songTitle: captured.song.title, artist: captured.song.artist,
    providerId: "deezer", clientName: "Feishin", createdAt: captured.capturedAt, updatedAt: captured.capturedAt, revision: state.revision });
  await page.route("**/api/admin/**", async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    if (path === "/api/admin/auth/me") return reply(route, { authenticated: true, backend,
      user: { id: "listener", allstarrUserId: listenerId, name: "Listener", isAdministrator: administrator } });
    if (path === "/api/admin/auth/oidc/status") return reply(route, { enabled: false });
    if (path === "/api/admin/onboarding/status") return reply(route, { completed: true, setupOpen: false,
      shouldRedirectToSetup: false, schemaVersion: "onboarding-v1", completedSteps: [], completionSource: "setup-guide",
      revision: 1, recoveryNotices: [], migration: { available: false, completed: false, firstRun: false } });
    if (path === "/api/admin/ui/schema") return reply(route, { configSections: [{ id: "client-address", label: "Music-client address",
      fields: [{ key: "PUBLIC_LISTENER_URL", label: "Music-client address", type: "text", valuePath: "operatorSettings.Gateway:PublicListenerUrl", requiresRestart: false }] }],
      providers: [{ id: "deezer", name: "Deezer" }], priorityGroups: [], settings: [] });
    if (path === "/api/admin/access/oidc") return reply(route, { csrfToken: "fixture", settings: { enabled: false,
      authority: "", clientId: "", publicUrl: "", displayName: "Single sign-on", revision: 1 } });
    if (path === "/api/admin/config") {
      if (request.method() === "POST") {
        const updates = request.postDataJSON().updates as Record<string, string>;
        state.settingsWrites.push(updates);
        state.url = updates.PUBLIC_LISTENER_URL || null;
        return reply(route, { updatedKeys: ["PUBLIC_LISTENER_URL"], requiresRestart: false });
      }
      return reply(route, { operatorSettings: { "Gateway:PublicListenerUrl": state.url ?? "" } });
    }
    if (path === "/api/admin/ui/listener-context") return reply(route, { backend, username: "Listener",
      musicClientUrl: state.url, addressSource: state.url ? "configured" : "unconfigured" });
    if (path === "/api/admin/problem-reports/context") {
      const selector = request.postDataJSON();
      state.previews.push(selector);
      if (state.contextFailure) return reply(route, { code: "report_subject_unavailable", error: "Song details are no longer available." }, 404);
      return reply(route, selector ? { ...captured, match: { ...captured.match, authorityScope: state.matchScope, state: state.matchState },
        ...(!selector.playback ? { subjectUserId: null, subjectName: null, playback: null } : {}) }
        : { ...captured, subjectUserId: null, subjectName: null, song: null, match: null, playback: null });
    }
    if (path === "/api/admin/problem-reports" && request.method() === "POST") {
      const input = request.postDataJSON() as Record<string, unknown>;
      state.creates.push(input);
      if (state.createGate) await state.createGate;
      if (state.createContextFailure) return reply(route, { code: "report_subject_unavailable", error: "Song details are no longer available." }, 404);
      if (state.createFailure) return reply(route, { code: "report_unavailable", error: "The report could not be saved. Try again." }, 503);
      return reply(route, { id: reportId, status: "new", createdAt: captured.capturedAt, replayed: false }, 201);
    }
    if (path === "/api/admin/problem-reports") {
      const filter = url.searchParams.get("status");
      state.reportReads.push(filter);
      if (filter === "all") return reply(route, { code: "report_invalid", error: "The problem report is invalid." }, 400);
      const more = url.searchParams.get("cursor");
      return reply(route, { items: !filter || filter === state.status
        ? [summary(), ...(more ? [{ ...summary(), id: "44444444-4444-4444-8444-444444444444", songTitle: "Another reported song" }] : [])] : [],
        nextCursor: state.paged && !more ? "next-report-page" : null });
    }
    if (path === `/api/admin/problem-reports/${reportId}`) return reply(route, { ...summary(), note: state.note, context: captured });
    if (path === `/api/admin/problem-reports/${reportId}/status`) {
      expect(request.method()).toBe("PATCH");
      const input = request.postDataJSON() as Record<string, unknown>;
      state.updates.push(input);
      if (state.conflict) return reply(route, { code: "report_changed", error: "This report changed. Reload it before saving." }, 409);
      state.status = String(input.status);
      state.revision++;
      return reply(route, { id: reportId, status: state.status, revision: state.revision, updatedAt: captured.capturedAt });
    }
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    if (path.startsWith("/api/admin/activity")) return reply(route, { items: [], nextCursor: null });
    return reply(route, {});
  });
  return state;
}

test("Subsonic listeners get compatible apps and can enter an unconfigured address locally", async ({ page }) => {
  const state = await mockSupport(page, false, "Subsonic");
  state.url = null;
  await page.goto("/#/getting-started");
  await expect(page.getByText("Your administrator has not set a music-client address.", { exact: false })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Tempo", exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Finamp", exact: true })).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Open web player", exact: true })).toHaveCount(0);
  const address = page.getByLabel("Music-client address", { exact: true });
  await expect(address).toHaveValue("");
  await expect(page.getByRole("button", { name: "Copy server address" })).toBeDisabled();
  await address.fill("http://music.test:5274/gateway");
  await expect(page.getByRole("button", { name: "Copy server address" })).toBeEnabled();
  expect(state.url).toBeNull();
  await expect(page.getByRole("link", { name: "Set the household address" })).toHaveCount(0);
});

test("guide rejects private URL fields and offers copy selection when clipboard access is unavailable", async ({ page }) => {
  await page.addInitScript(() => Object.defineProperty(navigator, "clipboard", { value: undefined, configurable: true }));
  await mockSupport(page, true);
  await page.goto("/#/getting-started");
  const address = page.getByLabel("Music-client address", { exact: true });
  await expect(page.getByRole("link", { name: "Set the household address" })).toHaveAttribute("href", "#/settings/network");
  await address.fill("https://listener:private@music.test/?api_key=private");
  await expect(page.getByRole("button", { name: "Copy server address" })).toBeDisabled();
  await expect(page.getByRole("link", { name: "Open web player", exact: true })).toHaveCount(0);
  await address.fill("https://music.test/gateway%0a");
  await expect(page.getByRole("button", { name: "Copy server address" })).toBeDisabled();
  await address.fill("http://music.test:5274/gateway");
  await page.getByRole("button", { name: "Copy server address" }).click();
  await expect(address).toBeFocused();
  expect(await address.evaluate(input => (input as HTMLInputElement).selectionEnd)).toBe("http://music.test:5274/gateway".length);
});

test("the administrator address field loads its durable value and updates the guide without restart", async ({ page }) => {
  const state = await mockSupport(page, true);
  await page.goto("/#/settings/network");
  const form = page.getByRole("form", { name: "Music-client address settings", exact: true });
  const address = form.getByRole("textbox", { name: "Music-client address", exact: true });
  await expect(address).toHaveValue("http://music.test:5274");
  await address.fill("https://music.test/gateway");
  await form.getByRole("button", { name: "Save Music-client address", exact: true }).click();
  await expect.poll(() => state.settingsWrites.length).toBe(1);
  expect(state.settingsWrites[0]).toEqual({ PUBLIC_LISTENER_URL: "https://music.test/gateway" });
  await expect(address).toHaveValue("https://music.test/gateway");
  await page.goto("/#/getting-started");
  await expect(page.getByLabel("Music-client address", { exact: true })).toHaveValue("https://music.test/gateway");
});

test("a song report posts only server selectors and saves once while a request is pending", async ({ page }) => {
  const state = await mockSupport(page);
  let release!: () => void;
  state.createGate = new Promise<void>(resolve => release = resolve);
  await page.goto(`/#/report?item=ext-deezer-song-fixture&user=${listenerId}&device=fixture-device&title=Untrusted&source=untrusted&from=%2Flibrary%2Fplaylists`);
  await expect(page.getByText("Playing from", { exact: true })).toBeVisible();
  await expect(page.getByText("Feishin", { exact: true })).toBeVisible();
  await expect(page.getByText("Untrusted", { exact: true })).toHaveCount(0);
  const send = page.getByRole("button", { name: "Send report", exact: true });
  await expect(send).toBeDisabled();
  await page.getByLabel("What happened?", { exact: true }).fill(" No sound after seeking. ");
  await send.click();
  await expect.poll(() => state.creates.length).toBe(1);
  await expect(page.getByRole("button", { name: "Saving…" })).toBeDisabled();
  expect(state.creates[0]).toEqual({ requestId: expect.any(String), kind: "playback", note: "No sound after seeking.",
    selector: { itemId: "ext-deezer-song-fixture", playback: { subjectUserId: listenerId, deviceId: "fixture-device" } } });
  expect(Object.keys(state.creates[0])).toEqual(["requestId", "kind", "note", "selector"]);
  release();
  await expect(page.getByRole("heading", { name: "Report saved", level: 2 })).toBeVisible();
  await expect(page.getByRole("link", { name: "Back to music" })).toHaveAttribute("href", "#/library/playlists");
  await expect(page.getByRole("button", { name: "Send report" })).toHaveCount(0);
  expect(state.creates).toHaveLength(1);
});

test("failed report retries preserve the note and request identity, while an edited note gets a new identity", async ({ page }) => {
  const state = await mockSupport(page);
  state.createFailure = true;
  await page.goto("/#/report");
  const note = page.getByLabel("What happened?", { exact: true });
  await note.fill("Playback stopped.");
  const send = page.getByRole("button", { name: "Send report", exact: true });
  await send.click();
  await expect(page.getByRole("alert")).toContainText("The report could not be saved.");
  await expect(note).toHaveValue("Playback stopped.");
  await send.click();
  await expect.poll(() => state.creates.length).toBe(2);
  await expect(send).toBeEnabled();
  expect(state.creates[1].requestId).toBe(state.creates[0].requestId);
  await note.fill("Playback stopped after a seek.");
  state.createFailure = false;
  await send.click();
  await expect(page.getByRole("heading", { name: "Report saved", level: 2 })).toBeVisible();
  expect(state.creates[2].requestId).not.toBe(state.creates[0].requestId);
  expect(state.creates.every(input => !("selector" in input))).toBe(true);
});

test("expired playback context keeps the note until the listener explicitly drops those details", async ({ page }) => {
  const state = await mockSupport(page);
  await page.goto(`/#/report?item=ext-deezer-song-fixture&user=${listenerId}&device=fixture-device`);
  await expect(page.getByText("Playing from", { exact: true })).toBeVisible();
  const note = page.getByLabel("What happened?", { exact: true });
  await note.fill("The song stopped in Feishin.");
  state.createContextFailure = true;
  await page.getByRole("button", { name: "Send report", exact: true }).click();
  await expect(page.getByRole("button", { name: "Continue without playback details" })).toBeVisible();
  await expect(note).toHaveValue("The song stopped in Feishin.");
  await expect(page.getByRole("button", { name: "Send report", exact: true })).toBeDisabled();
  state.createContextFailure = false;
  await page.getByRole("button", { name: "Continue without playback details" }).click();
  await expect(page.getByText("Catalog source", { exact: true })).toBeVisible();
  await expect(page.getByText("Not captured", { exact: true })).toBeVisible();
  await expect(note).toHaveValue("The song stopped in Feishin.");
  await page.getByRole("button", { name: "Send report", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Report saved", level: 2 })).toBeVisible();
  expect(state.creates[1].selector).toEqual({ itemId: "ext-deezer-song-fixture" });
});

test("an incomplete report link allows a general report without losing a typed note", async ({ page }) => {
  const state = await mockSupport(page);
  await page.goto(`/#/report?item=ext-deezer-song-fixture&snapshot=${snapshotId}`);
  await expect(page.getByRole("alert")).toContainText("incomplete song details");
  await page.getByLabel("What happened?", { exact: true }).fill("I need help connecting my app.");
  await page.getByRole("button", { name: "Continue without song details" }).click();
  await expect(page.getByText("No song is attached.", { exact: false })).toBeVisible();
  await page.getByRole("button", { name: "Send report", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Report saved", level: 2 })).toBeVisible();
  expect(state.creates[0]).not.toHaveProperty("selector");
  expect(state.creates[0].note).toBe("I need help connecting my app.");
});

test("listener activity never opens or requests the administrator report inbox", async ({ page }) => {
  const state = await mockSupport(page);
  await page.goto(`/#/activity?view=reports&report=${reportId}`);
  await expect(page.getByRole("tab", { name: "History", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByRole("tab", { name: "Reports", exact: true })).toHaveCount(0);
  expect(state.reportReads).toEqual([]);
});

test("administrator inbox opens without changing status and protects a stale edit", async ({ page }) => {
  const state = await mockSupport(page, true);
  await page.goto("/#/activity");
  await expect(page.getByRole("tab", { name: "Reports", exact: true })).toBeVisible();
  expect(state.reportReads).toEqual([]);
  await page.getByRole("tab", { name: "Reports", exact: true }).click();
  await page.getByRole("button", { name: /^Open report for/ }).click();
  await expect(page.getByRole("article", { name: "Report note" })).toBeVisible();
  expect(state.updates).toEqual([]);
  const status = page.getByRole("button", { name: "Report status", exact: true });
  await status.click();
  await page.getByRole("option", { name: "Seen", exact: true }).click();
  state.conflict = true;
  state.status = "fixed"; state.revision = 2;
  await page.getByRole("button", { name: "Save status", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("This report changed.");
  await expect(page.getByRole("button", { name: "Save status", exact: true })).toBeDisabled();
  expect(state.updates[0]).toEqual({ status: "seen", expectedRevision: 1 });
  await page.getByRole("button", { name: "Reload report", exact: true }).click();
  await expect(status).toContainText("Fixed");
  state.conflict = false;
  await status.click();
  await page.getByRole("option", { name: "Seen", exact: true }).click();
  await page.getByRole("button", { name: "Save status", exact: true }).click();
  await expect(page.getByRole("form", { name: "Report status change" }).getByRole("status")).toHaveText("Report marked seen.");
  expect(state.updates[1]).toEqual({ status: "seen", expectedRevision: 2 });
  await page.getByRole("tab", { name: "All", exact: true }).click();
  await expect(page.getByRole("button", { name: /^Open report for/ })).toBeVisible();
  expect(state.reportReads.at(-1)).toBeNull();
  const reports = page.getByRole("tab", { name: "Reports", exact: true });
  await reports.focus();
  await page.keyboard.press("Home");
  await expect(page.getByRole("tab", { name: "History", exact: true })).toBeFocused();
  await page.keyboard.press("End");
  await expect(reports).toBeFocused();
  await expect(reports).toHaveAttribute("aria-selected", "true");
});

test("inbox paging sends the cursor and keeps existing reports without duplicate rows", async ({ page }) => {
  const state = await mockSupport(page, true);
  state.paged = true;
  await page.goto("/#/activity?view=reports");
  const list = page.getByRole("article", { name: "Report list", exact: true });
  await expect(list.getByRole("button", { name: /^Open report for/ })).toHaveCount(1);
  await page.getByRole("button", { name: "Load more reports", exact: true }).click();
  await expect(list.getByRole("button", { name: /^Open report for/ })).toHaveCount(2);
  await expect(list).toContainText("Another reported song");
  await expect(page.getByRole("button", { name: "Load more reports", exact: true })).toHaveCount(0);
});

test("a personal saved pin does not present the automatic match score as its confidence", async ({ page }) => {
  const state = await mockSupport(page);
  state.matchScope = "personal"; state.matchState = "pinned";
  await page.goto(`/#/report?snapshot=${snapshotId}`);
  const context = page.getByRole("article", { name: "Included report details" });
  await expect(context).toContainText("Personal choice");
  await expect(context.getByText("Pinned", { exact: true })).toBeVisible();
  await expect(context).not.toContainText("95%");
  await expect(context).not.toContainText("version 1");
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Report details and inbox fit ${width}px ${theme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    const state = await mockSupport(page, true);
    await page.goto(`/#/report?snapshot=${snapshotId}`);
    await expect(page.getByText("Catalog source", { exact: true })).toBeVisible();
    await page.getByLabel("What happened?", { exact: true }).fill("The displayed song differs from the recording. ".repeat(20));
    await page.getByText("Included diagnostics", { exact: true }).click();
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.evaluate(() => { (document.activeElement as HTMLElement)?.blur(); window.scrollTo(0, 0); });
    await page.screenshot({ path: testInfo.outputPath(`report-${width}-${theme}.png`), fullPage: true });
    state.note = "The displayed song differs from the recording. ".repeat(20) + "A".repeat(220);
    await page.goto(`/#/activity?view=reports&report=${reportId}`);
    await expect(page.getByRole("article", { name: "Report note" })).toBeVisible();
    await page.getByText("Included diagnostics", { exact: true }).click();
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.evaluate(() => { (document.activeElement as HTMLElement)?.blur(); window.scrollTo(0, 0); });
    await page.screenshot({ path: testInfo.outputPath(`inbox-${width}-${theme}.png`), fullPage: true });
  });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Getting started uses the listener connection at ${width}px ${theme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await mockSupport(page);
    await page.goto("/#/getting-started");
    await expect(page.getByRole("heading", { name: "Getting started", level: 1, exact: true })).toBeVisible();
    await expect(page.getByLabel("Music-client address", { exact: true })).toHaveValue("http://music.test:5274");
    await expect(page.getByLabel("Username", { exact: true })).toHaveValue("Listener");
    await expect(page.getByRole("heading", { name: "Feishin", exact: true })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Finamp", exact: true })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Jellyfin Web", exact: true })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Tempo", exact: true })).toHaveCount(0);
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.evaluate(() => { (document.activeElement as HTMLElement)?.blur(); window.scrollTo(0, 0); });
    await page.screenshot({ path: testInfo.outputPath(`getting-started-${width}-${theme}.png`), fullPage: true });
  });
}
