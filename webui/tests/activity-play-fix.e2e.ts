import { expect, test, type Locator, type Page } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const playId = "11111111-1111-4111-8111-111111111111";
const libraryId = "22222222-2222-4222-8222-222222222222";
const snapshotId = "33333333-3333-4333-8333-333333333333";
const personalAuthority = { id: "personal-version", revision: 4 };
const householdAuthority = { id: "household-version", revision: 4 };
const presented = { title: "Housewife", artist: "Tate McRae", album: "So Close to What", durationMilliseconds: 180000, artwork: null };
const streamedPlay = {
  id: playId, listenerId: "viewer", startedAt: "2026-10-29T12:00:00Z", outcome: "completed",
  request: { presented },
  resolution: { provider: "applemusic", cached: false, externalSnapshotId: snapshotId, authorityScope: "household", authorityId: "captured-version", authorityRevision: 1 },
  received: { tags: { title: "Housewife (Live)", artist: "Another performer", album: "Live at Night" }, durationSeconds: 240 },
  differences: ["title-mismatch", "artist-mismatch", "duration-mismatch"],
  timing: { elapsedMs: 1400, firstByteMs: 1200, headersMs: 1100, omittedPhases: 0, phases: [{ phase: "route", startedMs: 0, elapsedMs: 250 }, { phase: "source", provider: "applemusic", startedMs: 250, elapsedMs: 850 }] },
};
const libraryPlay = {
  ...streamedPlay, id: libraryId, startedAt: "2026-10-29T11:00:00Z",
  request: { presented: { ...presented, title: "Library favorite" } },
  resolution: { provider: "library", cached: false, externalSnapshotId: null, authorityScope: "automatic", authorityId: null, authorityRevision: null },
};
const match = {
  externalSnapshotId: snapshotId, providerId: "spotify", state: "matched", decisionSource: "manual",
  title: presented.title, artist: presented.artist, album: presented.album, durationMilliseconds: 180000,
  providerIdentities: [], reasons: [], warnings: [], allowedAuthorityScopes: ["personal", "household"],
  manualAuthorities: [
    { ...personalAuthority, scope: "personal", canEdit: true, kind: "local_match", authoritySnapshotId: snapshotId },
    { ...householdAuthority, scope: "household", canEdit: true, kind: "local_match", authoritySnapshotId: snapshotId },
  ],
  candidates: [
    { title: "Housewife — library version", artist: presented.artist, libraryTrackId: "library-song", isLocal: true, confidence: .98, durationMilliseconds: 180000 },
    { title: "Housewife — streaming version", artist: presented.artist, isLocal: false, providerTrackIds: { applemusic: "correct-version" }, confidence: .97, durationMilliseconds: 180000 },
  ],
};

type Write = { path: string; body: Record<string, unknown> };
async function mockPlays(page: Page, options: { administrator?: boolean; library?: boolean; automatic?: boolean; conflict?: "version" | "forget"; forgetRetry?: boolean; reportRetry?: boolean } = {}) {
  const reads: string[] = [], writes: Write[] = [];
  let changed = false, reportAttempts = 0;
  await page.route("**/api/admin/**", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    const reply = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    if (request.method() === "GET") reads.push(`${path}${url.search}`);
    if (!["GET", "HEAD"].includes(request.method())) {
      const body = request.postDataJSON() as Record<string, unknown>;
      writes.push({ path, body });
      if (path === `/api/admin/play-traces/${playId}/${options.conflict}` && !changed) {
        changed = true;
        return reply({ code: options.conflict === "version" ? "match_unavailable" : "match_changed", error: "The saved match changed." }, 409);
      }
      if (path === `/api/admin/play-traces/${playId}/report` && options.reportRetry && reportAttempts++ === 0)
        return reply({ error: "The report could not be sent. Try again." }, 503);
      if (path === `/api/admin/play-traces/${playId}/forget` && options.forgetRetry && !changed) {
        changed = true;
        return reply({ error: "The song could not be changed. Try again." }, 503);
      }
      return reply({ success: true });
    }
    if (path === "/api/admin/auth/me") return reply({ authenticated: true, backend: "Jellyfin", user: { id: "viewer", allstarrUserId: "viewer", name: "Josh", isAdministrator: options.administrator ?? false } });
    if (path === "/api/admin/auth/oidc/status") return reply({ enabled: false });
    if (path === "/api/admin/onboarding/status") return reply({ completed: true, shouldRedirectToSetup: false, setupOpen: false, completedSteps: ["backend-identity"], revision: 1, recoveryNotices: [] });
    if (path === "/api/admin/ui/schema") return reply({ configSections: [], providers: [{ id: "applemusic", name: "Apple Music", categories: ["streaming"] }], activeBackend: "Jellyfin" });
    if (path === "/api/admin/play-traces") return reply([
      streamedPlay,
      { ...streamedPlay, id: "other-listener", listenerId: "someone-else", request: { presented: { ...presented, title: "Someone else's song" } } },
      ...(options.library ? [libraryPlay] : []),
    ]);
    if (path === `/api/admin/play-traces/${playId}`) return reply(changed ? { ...streamedPlay, received: { ...streamedPlay.received, tags: { ...streamedPlay.received.tags, title: "Latest saved version" } } } : streamedPlay);
    if (path === `/api/admin/play-traces/${libraryId}`) return reply(libraryPlay);
    if (path.endsWith("/diagnostics")) return route.fulfill({ contentType: "text/plain", body: "Playback diagnostics" });
    if (path === "/api/admin/track-matches") return reply({ matches: [{ ...match, manualAuthorities: options.automatic ? [] : match.manualAuthorities.map(authority => ({ ...authority, revision: changed ? 5 : authority.revision })) }] });
    if (path.startsWith("/api/admin/track-matches/targets/")) return reply({ tracks: [], providers: ["applemusic"] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return reply({});
  });
  return { reads, writes };
}

async function openPlay(page: Page) {
  await page.goto("/#/activity?view=plays");
  const row = page.locator(".play-row").filter({ has: page.getByRole("button", { name: /Housewife/ }) });
  await row.locator(".play-toggle").click();
  await expect(row.getByRole("region", { name: "What played" })).toBeVisible();
  return row;
}

async function choose(page: Page, row: Locator, version: "library" | "streaming" = "library") {
  await row.getByRole("button", { name: "Wrong song?", exact: true }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole("button", { name: /reject/i })).toHaveCount(0);
  await dialog.locator(".candidate-card").filter({ hasText: `Housewife — ${version} version` }).getByRole("button", { name: "Choose candidate" }).click();
}

test("A listener compares their own play and saves a personal version", async ({ page }) => {
  const fixture = await mockPlays(page);
  const row = await openPlay(page);
  await expect(page.locator(".play-row")).toHaveCount(1);
  await expect(page.getByText("Someone else's song", { exact: true })).toHaveCount(0);
  const comparison = row.getByRole("region", { name: "What played" });
  await expect(comparison.locator("p").first()).toHaveText("Asked for: Housewife · Tate McRae · So Close to What · 3:00");
  await expect(comparison.locator("p").last()).toHaveText("Played: Housewife (Live) · Another performer · 4:00 · Apple Music");
  await expect(comparison.locator("p").first().locator(".play-difference")).toHaveCount(0);
  await expect(comparison.locator("p").last().locator(".play-difference")).toHaveCount(3);
  await expect(row.getByRole("group", { name: "Change it for" })).toHaveCount(0);
  await choose(page, row);
  await expect.poll(() => fixture.writes.length).toBe(1);
  expect(fixture.writes[0]).toEqual({ path: `/api/admin/play-traces/${playId}/version`, body: { targetType: "local", libraryTrackId: "library-song", authorityScope: "personal", expectedAuthority: personalAuthority } });
  expect(fixture.reads.some(path => path.includes(`externalSnapshotId=${snapshotId}`) && path.includes("pageSize=1"))).toBe(true);
  await expect.poll(() => fixture.reads.filter(path => path === `/api/admin/play-traces/${playId}`).length).toBeGreaterThan(1);
  await expect.poll(() => fixture.reads.filter(path => path.startsWith("/api/admin/play-traces?")).length).toBeGreaterThan(1);
});

test("An administrator saves a streaming version for everyone", async ({ page }) => {
  const fixture = await mockPlays(page, { administrator: true });
  const row = await openPlay(page);
  const scope = row.getByRole("group", { name: "Change it for" });
  await expect(scope.getByRole("button", { name: "Just me" })).toHaveAttribute("aria-pressed", "true");
  await scope.getByRole("button", { name: "Everyone", exact: true }).click();
  await expect(scope.getByRole("button", { name: "Everyone", exact: true })).toHaveAttribute("aria-pressed", "true");
  await choose(page, row, "streaming");
  await expect.poll(() => fixture.writes.length).toBe(1);
  expect(fixture.writes[0]).toEqual({ path: `/api/admin/play-traces/${playId}/version`, body: { targetType: "provider", externalProvider: "applemusic", externalId: "correct-version", authorityScope: "household", expectedAuthority: householdAuthority } });
});

test("Looking for a song again requires confirmation", async ({ page }) => {
  const fixture = await mockPlays(page);
  const row = await openPlay(page);
  await row.getByRole("button", { name: "Look for it again", exact: true }).click();
  const confirmation = page.getByRole("alertdialog", { name: "Look for this song again?" });
  await expect(confirmation).toBeVisible();
  expect(fixture.writes).toEqual([]);
  await confirmation.getByRole("button", { name: "Cancel", exact: true }).click();
  expect(fixture.writes).toEqual([]);
  await row.getByRole("button", { name: "Look for it again", exact: true }).click();
  await confirmation.getByRole("button", { name: "Look for it again", exact: true }).click();
  await expect.poll(() => fixture.writes.length).toBe(1);
  await expect(confirmation).not.toBeVisible();
  expect(fixture.writes[0]).toEqual({ path: `/api/admin/play-traces/${playId}/forget`, body: { authorityScope: "personal", expectedAuthority: personalAuthority } });
  await expect.poll(() => fixture.reads.filter(path => path === `/api/admin/play-traces/${playId}`).length).toBeGreaterThan(1);
  await expect.poll(() => fixture.reads.filter(path => path.startsWith("/api/admin/play-traces?")).length).toBeGreaterThan(1);
});

test("A failed rematch closes confirmation and allows a fresh retry", async ({ page }) => {
  const fixture = await mockPlays(page, { forgetRetry: true });
  const row = await openPlay(page);
  const confirmation = page.getByRole("alertdialog", { name: "Look for this song again?" });
  await row.getByRole("button", { name: "Look for it again", exact: true }).click();
  await confirmation.getByRole("button", { name: "Look for it again", exact: true }).click();
  await expect(page.getByText("The song could not be changed. Try again.", { exact: true })).toBeVisible();
  await expect(confirmation).not.toBeVisible();
  await row.getByRole("button", { name: "Look for it again", exact: true }).click();
  await expect(confirmation).toBeVisible();
  await confirmation.getByRole("button", { name: "Look for it again", exact: true }).click();
  await expect(page.getByText("Done. Allstarr will look again next time.", { exact: true })).toBeVisible();
  await expect(confirmation).not.toBeVisible();
  expect(fixture.writes).toHaveLength(2);
});

test("Looking for an automatically matched song again sends no saved authority", async ({ page }) => {
  const fixture = await mockPlays(page, { automatic: true });
  const row = await openPlay(page);
  await row.getByRole("button", { name: "Look for it again", exact: true }).click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Look for it again", exact: true }).click();
  await expect.poll(() => fixture.writes.length).toBe(1);
  expect(fixture.writes[0]).toEqual({ path: `/api/admin/play-traces/${playId}/forget`, body: { authorityScope: "personal", expectedAuthority: null } });
});

test("A report preserves its note and request reference when retried", async ({ page }) => {
  const fixture = await mockPlays(page, { reportRetry: true });
  const row = await openPlay(page);
  await row.getByRole("button", { name: "Report a problem", exact: true }).click();
  const form = page.getByRole("form", { name: "Report a problem" });
  await expect(form.getByLabel("Note (optional)")).toHaveAttribute("maxlength", "500");
  await form.getByLabel("Note (optional)").fill("The live version played instead of the studio recording.");
  await form.getByRole("button", { name: "Send report", exact: true }).click();
  await expect(page.getByText("The report could not be sent. Try again.", { exact: true })).toBeVisible();
  await expect(form.getByLabel("Note (optional)")).toHaveValue("The live version played instead of the studio recording.");
  await form.getByRole("button", { name: "Send report", exact: true }).click();
  await expect(form).not.toBeVisible();
  expect(fixture.writes).toHaveLength(2);
  expect(fixture.writes[0].path).toBe(`/api/admin/play-traces/${playId}/report`);
  expect(fixture.writes[0].body).toEqual({ requestId: expect.stringMatching(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i), note: "The live version played instead of the studio recording." });
  expect(fixture.writes[1]).toEqual(fixture.writes[0]);
});

for (const action of ["version", "forget"] as const) test(`A changed match reloads its authority and lets ${action} succeed on retry`, async ({ page }) => {
  const fixture = await mockPlays(page, { conflict: action });
  const row = await openPlay(page);
  if (action === "version") await choose(page, row);
  else {
    await row.getByRole("button", { name: "Look for it again", exact: true }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Look for it again", exact: true }).click();
  }
  await expect(page.getByText("This song's match changed. Showing the latest.", { exact: true })).toBeVisible();
  await expect(row.getByRole("region", { name: "What played" })).toContainText("Latest saved version");
  await expect(page.getByRole("alertdialog")).not.toBeVisible();
  expect(fixture.writes).toHaveLength(1);
  expect(fixture.writes[0].path).toBe(`/api/admin/play-traces/${playId}/${action}`);
  expect(fixture.writes[0].body.expectedAuthority).toEqual(personalAuthority);
  expect(fixture.reads.filter(path => path === `/api/admin/play-traces/${playId}`).length).toBeGreaterThan(1);
  await expect.poll(() => fixture.reads.filter(path => path.startsWith("/api/admin/track-matches?")).length).toBeGreaterThan(1);
  if (action === "version") await choose(page, row);
  else {
    await row.getByRole("button", { name: "Look for it again", exact: true }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Look for it again", exact: true }).click();
  }
  await expect(page.getByText(action === "version" ? "Saved. It will play this version next time." : "Done. Allstarr will look again next time.", { exact: true })).toBeVisible();
  await expect(page.getByRole("alertdialog")).not.toBeVisible();
  expect(fixture.writes).toHaveLength(2);
  expect(fixture.writes[1]).toEqual({ path: `/api/admin/play-traces/${playId}/${action}`, body: {
    ...(action === "version" ? { targetType: "local", libraryTrackId: "library-song" } : {}),
    authorityScope: "personal", expectedAuthority: { ...personalAuthority, revision: 5 },
  } });
  expect(fixture.writes.some(write => write.path.startsWith("/api/admin/track-matches"))).toBe(false);
});

test("A library play explains why there is nothing to change", async ({ page }) => {
  const fixture = await mockPlays(page, { library: true });
  await page.goto("/#/activity?view=plays");
  const row = page.locator(".play-row").filter({ has: page.getByRole("button", { name: /Library favorite/ }) });
  await row.locator(".play-toggle").click();
  await expect(row).toContainText("This played from your library, so there is nothing to change here.");
  await expect(row.getByRole("button", { name: "Copy diagnostics", exact: true })).toBeVisible();
  await expect(row.getByRole("button", { name: "Wrong song?", exact: true })).toHaveCount(0);
  await expect(row.getByRole("button", { name: "Look for it again", exact: true })).toHaveCount(0);
  await expect(row.getByRole("button", { name: "Report a problem", exact: true })).toHaveCount(0);
  expect(fixture.writes).toEqual([]);
});

async function expectPlayGeometry(page: Page, width: number) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  const sizes = await page.locator(".play-detail button:visible").evaluateAll(elements => elements.map(element => {
    const rect = element.getBoundingClientRect();
    return { name: element.textContent?.trim(), width: rect.width, height: rect.height };
  }));
  for (const size of sizes) {
    expect(size.width, `${size.name} width`).toBeGreaterThanOrEqual(44);
    expect(size.height, `${size.name} height`).toBeGreaterThanOrEqual(44);
  }
  if (width === 390) await expectContainedMobileGeometry(page);
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Play correction stays readable at ${width}px ${theme}`, async ({ page }, testInfo) => {
  await page.setViewportSize({ width, height: 1100 });
  await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
  await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
  const fixture = await mockPlays(page, { administrator: true });
  await page.goto("/#/activity?view=plays");
  const row = page.locator(".play-row").first();
  await expect(row.locator(".play-toggle")).toBeVisible();
  await expectPlayGeometry(page, width);
  const shot = async (state: string) => page.screenshot({ path: testInfo.outputPath(`plays-${state}-${width}-${theme}.png`), fullPage: true });
  await shot("collapsed");
  await row.locator(".play-toggle").click();
  const comparison = row.getByRole("region", { name: "What played" });
  await expect(comparison).toBeVisible();
  await expect(comparison.locator(".play-difference").first()).toBeVisible();
  await expect(row.getByText("How it started", { exact: true })).toBeVisible();
  const comparisonBox = await comparison.boundingBox();
  const stepBox = await row.getByRole("list", { name: "Start steps" }).locator("li").first().boundingBox();
  expect(Math.abs(comparisonBox!.x - stepBox!.x)).toBeLessThanOrEqual(1);
  for (const name of ["Wrong song?", "Look for it again", "Report a problem"]) await expect(row.getByRole("button", { name, exact: true })).toBeVisible();
  await expectPlayGeometry(page, width);
  await shot("expanded");
  await row.getByRole("button", { name: "Report a problem", exact: true }).click();
  const form = page.getByRole("form", { name: "Report a problem" });
  await form.getByLabel("Note (optional)").fill("A different recording played.");
  await expectPlayGeometry(page, width);
  await shot("report");
  await form.getByRole("button", { name: "Cancel", exact: true }).click();
  await row.getByRole("group", { name: "Change it for" }).getByRole("button", { name: "Everyone", exact: true }).click();
  await expectPlayGeometry(page, width);
  await shot("scope");
  expect(fixture.writes).toEqual([]);
});
