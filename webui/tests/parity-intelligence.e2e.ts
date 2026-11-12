import { expect, test } from "@playwright/test";
import { expectContainedMobileGeometry, touchTarget } from "./mobile-geometry";
import { responses, routeRelease, mockApi } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

test("core release exposes personal mixes without development APIs", async ({ page }) => {
  const intelligenceRequests: string[] = [];
  page.on("request", (request) => {
    if (new URL(request.url()).pathname.startsWith("/api/admin/intelligence")) {
      intelligenceRequests.push(request.url());
    }
  });
  await mockApi(page, { intelligenceEnabled: false });

  await page.goto("#/intelligence");

  await expect(page).toHaveURL(/#\/for-you$/);
  await expect(page.getByRole("heading", { name: "For you", exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Made for you", exact: true }).locator(".mix-card")).toHaveCount(8);
  await expect(page.getByRole("region", { name: "Moods", exact: true }).locator(".mix-card")).toHaveCount(5);
  await expect(page.getByRole("navigation", { name: "For you sections" }).getByRole("tab", { name: "For you", exact: true })).toHaveAttribute("aria-selected", "true");
  await page.goto("#/integrations/services?source=audiomuse-ai&section=configuration");
  const configure = page.getByRole("link", { name: "Configure", exact: true });
  await expect(configure).toHaveAttribute("href", "#/settings/discovery");
  await configure.click();
  await expect(page.getByRole("heading", { name: "Mixes and discovery", exact: true })).toBeVisible();
  await page.getByRole("region", { name: "AudioMuse", exact: true }).getByRole("button", { name: "Edit connection", exact: true }).click();
  await expect(page.getByRole("form", { name: "AudioMuse connection", exact: true })).toBeVisible();
  expect(intelligenceRequests).toEqual([]);
});

test("Intelligence history imports, corrections, and schedules use the selected scope", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  const overviewRequest = page.waitForRequest((request) => request.url().includes("/api/admin/intelligence/history/overview"));
  await page.goto("#/intelligence?section=history");
  await expect(page.getByRole("tab", { name: "History", exact: true })).toHaveAttribute("aria-selected", "true");
  expect(new URL((await overviewRequest).url()).searchParams.get("backendInstanceId")).toBe("main");

  await page.locator(".history-list").getByRole("button").click();
  const detail = page.getByRole("dialog", { name: "Edit listen" });
  await expect(detail).toBeVisible();
  await expect(detail.getByText("MusicBrainz details", { exact: true })).toBeVisible();
  await expect(detail.getByText("No lookup queued", { exact: true })).toBeVisible();
  await detail.getByLabel("Song").fill("Moon Song (live)");
  const correction = page.waitForRequest((request) => request.method() === "PUT" &&
    request.url().includes("/api/admin/intelligence/history/44444444-4444-4444-4444-444444444444"));
  await detail.getByRole("button", { name: "Save changes" }).click();
  expect((await correction).postDataJSON()).toMatchObject({
    protocol: "jellyfin", backendInstanceId: "main",
    title: "Moon Song (live)", expectedRevision: 2,
  });
  await detail.getByRole("button", { name: "Close listen details" }).click();

  await page.getByRole("tab", { name: "Import", exact: true }).click();
  await expect(page).toHaveURL(/#\/intelligence\?section=imports$/);
  const previews: string[] = [];
  page.on("request", (request) => {
    if (request.method() === "POST" && request.url().endsWith("/api/admin/intelligence/history/imports/preview")) previews.push(request.url());
  });
  await expect(page.getByLabel("History export files")).toHaveAttribute("multiple", "");
  await page.getByLabel("History export files").setInputFiles([
    { name: "Streaming_History.json", mimeType: "application/json", buffer: Buffer.from("[]") },
    { name: "ListenBrainz.jsonl", mimeType: "text/plain", buffer: Buffer.from("{}") },
  ]);
  await expect.poll(() => previews.length).toBe(2);
  await expect(page.getByText("9", { exact: true }).first()).toBeVisible();
  await expect(page.getByText("does not send them to Last.fm or ListenBrainz.", { exact: false }).first()).toBeVisible();
  await expect(page.getByRole("status").filter({ hasText: "Streaming_History.json" })).toContainText("previewed");
  await expect(page.getByRole("status").filter({ hasText: "ListenBrainz.jsonl" })).toContainText("previewed");
  const applies: Array<{ url: string; body: unknown }> = [];
  page.on("request", (request) => {
    if (request.method() === "POST" && request.url().match(/\/api\/admin\/intelligence\/history\/imports\/[^/]+\/apply$/))
      applies.push({ url: request.url(), body: request.postDataJSON() });
  });
  await expect(page.getByRole("checkbox", { name: "Include Streaming_History.json" })).toBeChecked();
  await expect(page.getByRole("checkbox", { name: "Include ListenBrainz.jsonl" })).toBeChecked();
  await page.getByRole("button", { name: "Add all 2 ready files" }).click();
  await expect.poll(() => applies.length).toBe(2);
  expect(applies.map((request) => request.url)).toEqual(expect.arrayContaining([
    expect.stringContaining("/55555555-5555-5555-5555-555555555555/apply"),
    expect.stringContaining("/66666666-6666-6666-6666-666666666666/apply"),
  ]));
  for (const request of applies) expect(request.body).toMatchObject({
    protocol: "jellyfin", backendInstanceId: "main", revision: "preview-revision",
  });

  await page.getByRole("tab", { name: "Automation" }).click();
  await expect(page.getByRole("button", { name: "Generated playlist destination" })).toContainText("Jellyfin Music");
  await page.getByRole("button", { name: "New schedule" }).click();
  await page.getByLabel("Playlist name").fill("Friday discoveries");
  const cadence = page.getByRole("button", { name: "When to create it" });
  await cadence.click();
  await page.getByRole("option", { name: "Every Friday at 8:00 AM" }).click();
  await expect(cadence).toContainText("Every Friday at 8:00 AM");
  const schedule = page.waitForRequest((request) => request.method() === "POST" &&
    request.url().endsWith("/api/admin/intelligence/schedules"));
  await page.getByRole("button", { name: "Create schedule" }).click();
  expect((await schedule).postDataJSON()).toMatchObject({
    protocol: "jellyfin", backendInstanceId: "main",
    name: "Friday discoveries", cronExpression: "0 8 * * 5", limit: 25,
  });
  await expect(page.getByText("Monday discoveries", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Remove" }).click();
  const removal = page.getByRole("alertdialog", { name: "Remove this automatic playlist schedule?" });
  const remove = page.waitForRequest((request) => request.method() === "DELETE" &&
    request.url().endsWith("/api/admin/intelligence/schedules/schedule-1"));
  await removal.getByRole("button", { name: "Remove schedule" }).click();
  await remove;
});

test("Intelligence keeps completed imports visible and explains retention", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  const longImportFile = `${"StreamingHistoryAudio".repeat(7)}.json`;
  await page.route("**/api/admin/intelligence/history/imports?*", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({ items: [{
      importId: "77777777-7777-7777-7777-777777777777",
      revision: "saved-revision",
      displayFileName: longImportFile,
      sizeBytes: 4096,
      expiresAt: "2026-08-20T00:00:00Z",
      state: "completed",
      importedRows: 18_240,
      duplicateRows: 0,
      resolvedRows: 12_000,
      unresolvedRows: 6_240,
      outboundReplay: false,
      preview: {
        format: "spotify-extended-history", fileRows: 18_240, musicRows: 18_240,
        completed: 18_240, partial: 0, skipped: 0, episodes: 0, nonTrack: 0, malformed: 0,
        duplicateInFile: 0, duplicateExisting: 0, newRows: 18_240, resolvedNewRows: 12_000,
        unresolvedNewRows: 6_240, rowsWithoutProviderIdentity: 0, sourceUserCount: 1,
        estimatedMusicBrainzLookups: 6_240, earliest: "2025-01-01T00:00:00Z",
        latest: "2020-12-31T00:00:00Z", reasonCounts: {}, outsideRetentionRows: 0,
      },
    }] }),
  }));
  await page.route("**/api/admin/intelligence/history/imports/77777777-7777-7777-7777-777777777777", (route) => {
    if (route.request().method() !== "DELETE") return route.fallback();
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ removedImport: true, removedListens: 18_240 }),
    });
  });

  await page.goto("#/intelligence?section=imports");
  await expect(page.getByText(longImportFile, { exact: true })).toBeVisible();
  await expect(page.getByText("18,240 added", { exact: false })).toBeVisible();
  await expect(page.getByRole("checkbox", { name: `Include ${longImportFile}` })).toHaveCount(0);
  await expect.poll(() => page.locator(".import-result > header").evaluate((element) =>
    element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  await expect(page.getByText(/no longer appear in Overview or History/)).toBeVisible();
  await page.getByRole("button", { name: "Undo import" }).click();
  const dialog = page.getByRole("alertdialog", { name: "Undo this history import?" });
  await expect(dialog).toContainText("18,240 were added");
  const removal = page.waitForRequest((request) => request.method() === "DELETE" &&
    request.url().endsWith("/api/admin/intelligence/history/imports/77777777-7777-7777-7777-777777777777"));
  await dialog.getByRole("button", { name: "Undo import" }).click();
  expect((await removal).postDataJSON()).toMatchObject({
    protocol: "jellyfin", backendInstanceId: "main",
    revision: "saved-revision", confirmed: true,
  });
  await expect(page.getByText(longImportFile, { exact: true })).toHaveCount(0);
});

test("Intelligence empty states explain how to get results", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.route("**/api/admin/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    let body: unknown;
    if (path === "/api/admin/intelligence")
      body = { ...(responses[path] as Record<string, unknown>), candidates: [], visualization: [], generatedSets: [], providers: [] };
    else if (path === "/api/admin/intelligence/history/overview") {
      const empty = { completedListens: 0, distinctTracks: 0, distinctArtists: 0, listeningTimeMilliseconds: 0, firstListen: null };
      body = { ...(responses[path] as Record<string, unknown>), allTime: empty, selected: empty, currentStreakDays: 0, longestStreakDays: 0 };
    } else if (path === "/api/admin/intelligence/history/activity")
      body = { ...(responses[path] as Record<string, unknown>), buckets: [], currentStreakDays: 0, longestStreakDays: 0 };
    else if (path.startsWith("/api/admin/intelligence/history/top/"))
      body = { ...(responses[path] as Record<string, unknown>), items: [] };
    else if (path === "/api/admin/intelligence/history")
      body = { ...(responses[path] as Record<string, unknown>), items: [], nextCursor: null };
    else {
      await route.fallback();
      return;
    }
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
  });

  await page.goto("#/intelligence?section=unknown");
  await expect(page.getByRole("tab", { name: "Overview" })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByRole("heading", { name: "Listening overview" })).toBeVisible();
  await page.getByRole("tab", { name: "Discover" }).click();
  const sourceLink = page.getByRole("link", { name: "Connect or configure a source" });
  await expect(sourceLink).toBeVisible();
  expect((await touchTarget(sourceLink)).height).toBeGreaterThanOrEqual(44);
  await expect(page.getByText("Turn on automatic history in Automation, then play music or import a history file.")).toBeVisible();
  const settingsLink = page.getByRole("link", { name: "Automation", exact: true });
  expect((await touchTarget(settingsLink)).height).toBeGreaterThanOrEqual(44);

  await page.getByRole("tab", { name: "Overview" }).click();
  await expect(page.getByText("No completed listens were recorded for this period. Play music or import a history file to build this recap.")).toBeVisible();
  await expect(page.getByText("No activity in this period. Play music or import a history file to begin.")).toBeVisible();
  await expect(page.getByText("Nothing to rank yet. Play music or import a history file to begin.")).toBeVisible();

  await page.getByRole("tab", { name: "History", exact: true }).click();
  await expect(page.getByText("No completed listens yet")).toBeVisible();
  await expect(page.getByText("Turn on automatic history in Automation, then play music or import a history file.")).toBeVisible();
});

test("All-time activity exposes imported years instead of clipping them to recent playback", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.route("**/api/admin/intelligence/history/activity?*", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      period: { from: "0001-01-01T00:00:00Z", to: "2026-08-27T00:00:00Z", timeZoneId: "UTC" },
      currentStreakDays: 1,
      longestStreakDays: 28,
      buckets: [
        { date: "2024-01-15", count: 120, importedCount: 120, playbackCount: 0, durationMilliseconds: 21_600_000 },
        { date: "2024-02-01", count: 60, importedCount: 60, playbackCount: 0, durationMilliseconds: 10_800_000 },
        { date: "2025-08-18", count: 10, importedCount: 10, playbackCount: 0, durationMilliseconds: 1_800_000 },
        { date: "2026-08-26", count: 1, importedCount: 0, playbackCount: 1, durationMilliseconds: 180_000 },
      ],
    }),
  }));

  await page.goto("#/intelligence");
  const activityCard = page.locator(".activity-card");
  const years = activityCard.getByRole("navigation", { name: "Activity year" });
  await expect(years.getByRole("tab", { name: "2024" })).toHaveAttribute("aria-selected", "true");
  await expect(activityCard.locator(".heatmap-range")).toContainText("180 imported · 0 playback");
  await expect(activityCard.locator('[data-heatmap-date="2024-01-15"]')).not.toHaveAttribute("data-intensity", "0");
  await expect(activityCard.getByRole("slider", { name: "Daily listening activity" })).toHaveAttribute("aria-valuemax", "365");

  await years.getByRole("tab", { name: "2026" }).click();
  await expect(activityCard.locator(".heatmap-range")).toContainText("0 imported · 1 playback");
  await expect(activityCard.locator('[data-heatmap-date="2026-08-26"]')).not.toHaveAttribute("data-intensity", "0");
  await activityCard.getByRole("tab", { name: "Monthly" }).click();
  await expect(activityCard.getByRole("slider", { name: "Monthly listening activity" })).toHaveAttribute("aria-valuemax", "7");
  await expectContainedMobileGeometry(page);
});

test("Subsonic Intelligence requests explicit scoped background access before indexing", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  const credentialReferenceId = "44444444-4444-4444-4444-444444444444";
  let indexed = false;
  let granted = false;
  await page.route("**/api/admin/media-targets", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({ targets: [{
      id: "subsonic-target", protocol: "subsonic", backendInstanceId: "primary",
      displayName: "Listener",
      credentialReferenceId: granted ? credentialReferenceId : null,
    }] }),
  }));
  await page.route("**/api/admin/library-index/counts", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({ trackCount: indexed ? 1 : 0 }),
  }));
  await page.route("**/api/admin/auth/playlist-consent", (route) => {
    granted = true;
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ supported: true, granted, updatedAt: "2026-08-26T18:00:00Z" }),
    });
  });
  await page.route("**/api/admin/library-index/enqueue", async (route) => {
    indexed = true;
    await route.fulfill({
      status: 202,
      contentType: "application/json",
      body: JSON.stringify({ jobId: "index-job", created: true, generation: 1 }),
    });
  });

  await page.goto("#/intelligence");
  await expect(page.getByText("Connect background access", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Connect access" }).click();
  const dialog = page.getByRole("dialog", { name: "Connect Subsonic for background features" });
  await expect(dialog).toContainText("Only your Allstarr account can use this credential.");
  await expect(dialog).toContainText("never included in job payloads or logs");
  await expect(dialog.getByLabel("Subsonic username")).toHaveCount(0);
  await dialog.getByLabel("Subsonic password").fill("test-password");
  const credentialRequest = page.waitForRequest((request) =>
    request.method() === "POST" && request.url().endsWith("/api/admin/auth/playlist-consent"));
  const indexRequest = page.waitForRequest((request) =>
    request.method() === "POST" && request.url().endsWith("/api/admin/library-index/enqueue"));
  await dialog.getByRole("button", { name: "Allow and index" }).click();
  expect((await credentialRequest).postDataJSON()).toEqual({
    password: "test-password",
  });
  expect((await indexRequest).postDataJSON()).toEqual({ credentialReferenceId, pageSize: 200 });
  await expect(dialog).toBeHidden();
  await expect(page.getByRole("status").filter({ hasText: "indexed and ready for Intelligence" })).toBeVisible();
  await expect(page.locator(".scope-value")).toContainText("Subsonic · Listener");
});

test("Intelligence partial and section error states stay announced", async ({ page }) => {
  await page.setViewportSize({ width: 768, height: 1024 });
  const historyRelease = routeRelease();
  let failHistory = false;
  await mockApi(page, {
    releasePath: "/api/admin/intelligence/history/activity",
    release: historyRelease.promise,
  });
  await page.route("**/api/admin/**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === "/api/admin/intelligence") {
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({
          ...(responses[url.pathname] as Record<string, unknown>),
          state: "degraded",
        }),
      });
      return;
    }
    if ((failHistory && url.pathname === "/api/admin/intelligence/history/activity") ||
      (url.pathname === "/api/admin/intelligence/history/imports/preview" && route.request().method() === "POST") ||
      (url.pathname === "/api/admin/intelligence/schedules" && route.request().method() === "POST")) {
      await route.fulfill({
        status: 503,
        contentType: "application/json",
        body: JSON.stringify({ error: "Fixture unavailable" }),
      });
      return;
    }
    await route.fallback();
  });

  await page.goto("#/intelligence?section=history");
  await expect(page.getByRole("status", { name: "Loading listening history" })).toBeVisible();
  historyRelease.release();
  await expect(page.locator(".history-list").getByText("Moon Song", { exact: true })).toBeVisible();
  await expect(page.locator(".degraded-banner")).toContainText("Existing results remain available.");

  await page.getByRole("tab", { name: "Discover" }).click();
  await expect(page.getByText("Future Song", { exact: true })).toBeVisible();
  failHistory = true;
  await page.getByRole("tab", { name: "History", exact: true }).click();
  await expect(page.locator(".history-workspace").getByRole("alert")).toContainText("Fixture unavailable");

  await page.getByRole("tab", { name: "Import", exact: true }).click();
  await page.getByLabel("History export files").setInputFiles({
    name: "Streaming_History.json", mimeType: "application/json", buffer: Buffer.from("[]"),
  });
  const queue = page.getByRole("list", { name: "Selected history exports" });
  await expect(queue).toHaveAttribute("aria-live", "polite");
  await expect(queue).toContainText("Fixture unavailable");

  await page.getByRole("tab", { name: "Automation" }).click();
  await page.getByRole("button", { name: "New schedule" }).click();
  await page.getByRole("button", { name: "Create schedule" }).click();
  await expect(page.locator(".intelligence-schedules").getByRole("alert")).toContainText("Fixture unavailable");

  const clear = page.getByRole("button", { name: "Turn off and clear" });
  await clear.click();
  const dialog = page.getByRole("alertdialog", { name: "Clear private listening data for this library?" });
  await expect(dialog).toBeVisible();
  await expect.poll(() => dialog.evaluate((element) => element.contains(document.activeElement))).toBe(true);
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  await expect(clear).toBeFocused();

  const scrollOwners = await page.locator(".intelligence-view").evaluate((element) => {
    const nested = [];
    for (let parent = element.parentElement; parent; parent = parent.parentElement) {
      const overflow = getComputedStyle(parent).overflowY;
      if (/(auto|scroll)/.test(overflow) && parent.scrollHeight > parent.clientHeight) nested.push(parent.className);
    }
    return {
      nested,
      document: document.scrollingElement!.scrollHeight > document.scrollingElement!.clientHeight,
    };
  });
  expect(scrollOwners).toEqual({ nested: [], document: true });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
});

test("Intelligence cards, table columns, and compact actions stay aligned", async ({ page }) => {
  await page.setViewportSize({ width: 820, height: 760 });
  await mockApi(page);
  await page.goto("#/intelligence");

  for (const [selector, count] of [
    [".history-stats > article", 5],
    [".history-breakdowns > .breakdown-card", 3],
  ] as const) {
    const cards = page.locator(selector);
    await expect(cards).toHaveCount(count);
    const boxes = await cards.evaluateAll((items) => items.map((item) => item.getBoundingClientRect().toJSON()));
    expect(Math.max(...boxes.map((box) => box.y)) - Math.min(...boxes.map((box) => box.y))).toBeLessThanOrEqual(1);
    expect(Math.max(...boxes.map((box) => box.width)) - Math.min(...boxes.map((box) => box.width))).toBeLessThanOrEqual(1);
  }

  await expect.poll(() => page.locator(".history-workspace .segmented-tabs [role=tab]")
    .evaluateAll((items) => items.every((item) => item.getBoundingClientRect().height >= 44))).toBe(true);

  for (const width of [820, 1280]) {
    await page.setViewportSize({ width, height: 760 });
    const header = page.locator(".history-column-head > span");
    const row = page.locator(".history-list > li > button").first();
    const [sourceHeading, timeHeading, source, time] = await Promise.all([
      header.nth(2).boundingBox(),
      header.nth(3).boundingBox(),
      row.locator(".history-route").boundingBox(),
      row.locator(".history-time").boundingBox(),
    ]);
    expect(sourceHeading && source).toBeTruthy();
    expect(timeHeading && time).toBeTruthy();
    expect(Math.abs(sourceHeading!.x - source!.x)).toBeLessThanOrEqual(5);
    expect(Math.abs(timeHeading!.x - time!.x)).toBeLessThanOrEqual(5);
    await expect.poll(() => page.locator(".history-list-card").evaluate((element) =>
      element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  }

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("#/intelligence?section=discover");
  const tools = page.locator(".heading-tools");
  const scopeValue = tools.locator(":scope > .scope-value");
  const runState = tools.locator(":scope > .badge");
  const refreshAction = tools.locator(":scope > [data-slot=button]");
  await expect(scopeValue).toBeVisible();
  await expect(runState).toBeVisible();
  await expect(refreshAction).toBeVisible();
  const [scope, state, action] = await Promise.all([
    scopeValue.boundingBox(),
    runState.boundingBox(),
    refreshAction.boundingBox(),
  ]);
  expect(scope && state && action).toBeTruthy();
  expect(Math.abs(scope!.x - state!.x)).toBeLessThanOrEqual(1);
  expect(Math.abs(scope!.x + scope!.width - action!.x - action!.width)).toBeLessThanOrEqual(1);
});

test("Intelligence keeps the most recently selected backend scope session-bound when responses finish out of order", async ({ page }) => {
  await mockApi(page);
  const firstBackend = routeRelease();
  const requestedScopes: URLSearchParams[] = [];
  await page.route("**/api/admin/media-targets", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      targets: [{
        id: "primary-target", protocol: "jellyfin", backendInstanceId: "main",
        displayName: "Primary listener",
      }, {
        id: "secondary-target", protocol: "subsonic", backendInstanceId: "secondary",
        displayName: "Secondary listener",
        credentialReferenceId: "44444444-4444-4444-4444-444444444444",
      }],
    }),
  }));
  await page.route("**/api/admin/intelligence?*", async (route) => {
    const query = new URL(route.request().url()).searchParams;
    requestedScopes.push(query);
    const backendInstanceId = query.get("backendInstanceId") ?? "main";
    const protocol = query.get("protocol") ?? "jellyfin";
    if (backendInstanceId === "main") await firstBackend.promise;
    const state = structuredClone(responses["/api/admin/intelligence"]) as Record<string, unknown>;
    state.scope = { protocol, backendInstanceId };
    state.candidates = (state.candidates as Array<Record<string, unknown>>).map((candidate, index) =>
      index === 0 ? { ...candidate, title: `${backendInstanceId} recommendation` } : candidate);
    return route.fulfill({ contentType: "application/json", body: JSON.stringify(state) });
  });

  await page.goto("#/intelligence?section=discover");
  await page.getByRole("button", { name: "Music library" }).click();
  await page.getByRole("option", { name: "Secondary listener · Subsonic" }).click();
  await expect(page.getByText("secondary recommendation", { exact: true })).toBeVisible();
  firstBackend.release();
  await expect(page.getByText("secondary recommendation", { exact: true })).toBeVisible();
  await expect(page.getByText("main recommendation", { exact: true })).toHaveCount(0);
  expect(requestedScopes.some((scope) =>
    scope.get("protocol") === "subsonic" && scope.get("backendInstanceId") === "secondary")).toBe(true);
  for (const query of requestedScopes) {
    expect(query.has("libraryScopeId")).toBe(false);
    expect(query.has("userId")).toBe(false);
    expect(query.has("ownerUserId")).toBe(false);
  }
});

test("copying a listening-app key confirms the action", async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { writeText: async () => undefined },
    });
  });
  await mockApi(page);
  await page.goto("#/intelligence?section=automation");
  const savePolicy = page.getByRole("button", { name: "Save settings" });
  await expect(savePolicy).toBeDisabled();
  const backgroundRefresh = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return response.request().method() === "GET" && url.pathname === "/api/admin/intelligence";
  });
  await page.getByRole("button", { name: "How long to keep listening history" }).click();
  await page.getByRole("option", { name: "90 days" }).click();
  await expect(savePolicy).toBeEnabled();
  await backgroundRefresh;
  await expect(savePolicy).toBeEnabled();
  const listeningApps = page.locator(".listening-apps-card");
  await listeningApps.getByRole("button", { name: "Create private key" }).click();
  await expect(listeningApps.getByRole("button", { name: "Copy" })).toBeVisible();
  await listeningApps.getByRole("button", { name: "Copy" }).click();
  await expect(listeningApps.getByRole("button", { name: "Copied" })).toBeVisible();
});

test("Mix details keep short local timestamps on desktop", async ({ page }) => {
  const { appearance, mockForYou } = await import("./for-you-fixtures");
  await appearance(page, 1280, "dark");
  const state = await mockForYou(page);
  state.cards.find(card => card.id === "daily-1")!.updatedAt = "2026-11-06T00:45:59Z";
  await page.goto("/#/for-you?mix=daily-1");
  const panel = page.getByRole("dialog", { name: "Blend 1", exact: true });
  await expect(panel).toBeVisible();
  await expect(panel.locator(".panel-header-extra")).toContainText("Refreshed Nov");
  await expect(panel.locator(".panel-header-extra")).not.toContainText("2026");
  await page.evaluate(async () => { await document.fonts.ready; });
  await page.screenshot({ path: test.info().outputPath("mix-details-1280-dark.png") });
  expect(await page.pageErrors()).toEqual([]);
});
