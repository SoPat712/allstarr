import { expect, test, type Locator, type Page } from "@playwright/test";
import { mkdir, readFile } from "node:fs/promises";

const timestamp = "2026-11-04T12:00:00Z";
const listenerId = "11111111-1111-4111-8111-111111111111";
const playlistId = "22222222-2222-4222-8222-222222222222";
const accountId = "33333333-3333-4333-8333-333333333333";
const scheduleId = "44444444-4444-4444-8444-444444444444";
const reportId = "55555555-5555-4555-8555-555555555555";
const jobId = "66666666-6666-4666-8666-666666666666";

async function fixture(page: Page) {
  const blocked: string[] = [];
  const providers = [
    { id: "qobuz", name: "Qobuz", implementationOrigin: "extension", categories: ["streaming"], runtimeCapabilities: [{ id: "streaming", health: "degraded", ready: false, canTest: true, reasonCode: "service_unavailable", testedAt: timestamp }] },
    { id: "apple-musickit", name: "Apple Music", categories: ["streaming", "playlists"], accountSettings: [{ key: "token", label: "Media User Token", type: "text", sensitive: true, required: true }], runtimeCapabilities: [{ id: "streaming", health: "healthy", ready: true, canTest: true }] },
    { id: "spotify", name: "Spotify", categories: ["metadata", "playlists"] },
    { id: "listenbrainz", name: "ListenBrainz", categories: ["scrobbling"] },
  ];
  const accounts = [{ id: accountId, providerId: "apple-musickit", displayName: "Listening account", ownerDisplayName: "Listener", ownerUserId: listenerId, scope: "Personal", canManage: true, canChangeAudience: true, revision: 1, enabled: true, secret: { configured: true, revoked: false }, createdAt: timestamp, configuredFields: ["token"] }];
  const schedule = { id: scheduleId, revision: 1, cronExpression: "0 4 * * *", timeZoneId: "UTC", enabled: true, overlapPolicy: "skip", misfirePolicy: "runOnce", nextRunAt: "2026-11-05T04:00:00Z" };
  const playlist = { id: playlistId, name: "Evening mix", enabled: true, revision: 1, sourceProviderId: "spotify", providerAccountId: accountId,
    sourcePlaylistId: "evening", targetProtocol: "jellyfin", targetBackendInstanceId: "home", targetPlaylistId: "evening-copy",
    mode: "hybrid", importMode: "linked", projectionMode: "resolved", materializationMode: "reconcile", trackRetention: "onDemand",
    mirrorStaleEntries: true, preserveManualEntries: true, syncName: true, syncDescription: true, syncArtwork: true,
    trackCount: 3, matchedCount: 3, unmatchedCount: 0, playableCount: 3, materializedCount: 3, lastRunCode: "order", routeCoverage: [{ providerId: "local", count: 2 }],
    metrics: { total: 3, matched: 3, unresolved: 0, review: 0, rejected: 0, playable: 3, materialized: 3 } };
  const file = { path: "cache/apple/Take On Me.flac", storage: "cache", title: "Take On Me", artist: "a-ha", album: "Hunting High and Low", fileName: "Take On Me.flac",
    size: 29_000_000, sizeFormatted: "29 MB", lastModified: timestamp, lastAccessedAt: timestamp, expiresAt: "2026-11-05T12:00:00Z",
    codec: "FLAC", quality: "Lossless", bitrateKbps: 1064, sampleRateHz: 44100, bitDepth: 16, channels: 2, durationMilliseconds: 229000,
    provider: "apple-musickit", externalId: "take-on-me", artworkUrl: null, publicationState: "Indexed", referenceCount: 0, explicitlyKept: false, removable: true };
  const matches = ["One", "Two"].map((id, index) => ({ externalSnapshotId: id, providerId: "spotify", state: index ? "accepted" : "ambiguous", confidence: .967, threshold: .88,
    title: `Evening song ${id}`, artist: "Example artist", album: "Original album", durationMilliseconds: 180000, isrc: "SOURCE123",
    allowedAuthorityScopes: ["personal", "household"], canonicalRecordingId: `recording-${id}`, providerIdentities: [], reasons: [], warnings: [], manualAuthorities: [],
    target: { providerId: "qobuz", externalId: `current-${id}`, title: `Evening song ${id} (Live)`, artist: "Example artist", album: "Deluxe album", durationMilliseconds: 182000, isrc: "OTHER123" },
    candidates: [{ isCurrent: true, isLocal: false, title: `Evening song ${id} (Live)`, artist: "Example artist", providerTrackIds: { qobuz: `current-${id}` }, confidence: .967 },
      { isLocal: false, title: `Evening song ${id}`, artist: "Example artist", album: "Original album", durationMilliseconds: 180000, providerTrackIds: { "apple-musickit": `other-${id}` }, confidence: .967, components: { title: 1, artist: 1, duration: 1 }, reasons: ["title_exact", "duration_close"] }] }));
  const job = { id: jobId, type: "playlist.materialize", displayName: "Update playlist", state: "Deferred", ownerName: "Listener", summary: "Progress saved. More songs remain.", availableAt: "2026-11-04T13:00:00Z", startedAt: timestamp, updatedAt: timestamp, createdAt: timestamp, canCancel: true, attemptCount: 1 };
  const task = { id: scheduleId, name: "Sync Evening mix for Listener, week of November 2", kind: "playlist", state: "Scheduled", enabled: true, ownerName: "Listener", cadence: "Daily at 04:00 · UTC", lastRunAt: timestamp, nextRunAt: schedule.nextRunAt, runPlaylistLinkId: playlistId };
  const report = { id: reportId, kind: "playback", status: "new", revision: 1, notePreview: "Playback stopped early.", note: "Playback stopped early.", reporterName: "Listener", songTitle: "Evening song", artist: "River", providerId: "jellyfin", clientName: "Music app", createdAt: timestamp, updatedAt: timestamp };
  const trace = { id: "recent", listenerId, startedAt: timestamp, outcome: "completed", request: { presented: { title: "Evening song", artist: "River" } }, resolution: { provider: "library", cached: true }, timing: { firstByteMs: 4600, elapsedMs: 60000, headersMs: 100, omittedPhases: 0, phases: [] } };
  const songs = Array.from({ length: 8 }, (_, index) => ({ mixId: "daily-1", generation: 1, source: "library", reason: index ? "Calm sound" : "Played 6 times in 30 days", score: 1, song: { id: `song-${index}`, canonicalRecordingId: null, title: ["Blue morning", "Quiet water", "Afterglow"][index % 3] + (index > 2 ? ` ${index + 1}` : ""), artist: ["North", "Cedar", "Willow"][index % 3], album: "Open skies" } }));
  const mixes = [1, 2, 3, 4, 5, 6, 7, 8].map(index => ({ id: `daily-${index}`, kind: `daily-${index}`, name: index === 2 ? "Hip-Hop Blend" : index === 3 ? "Acoustic Blend" : `Blend ${index}`, section: "madeForYou", sortOrder: index, description: "R&B · North, Cedar, Willow", sourceLabel: null, recipeVersion: null, updatedAt: timestamp, readiness: { ready: true, message: "Ready" }, inApps: true, appsAllowed: true, coverArtwork: songs.slice(0, 4).map(row => row.song.id) }));
  await page.route("**/*", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (!["127.0.0.1", "localhost"].includes(url.hostname)) { blocked.push(url.href); return route.abort(); }
    if (path.startsWith("/images/") || path === "/favicon.svg") {
      const asset = await readFile(new URL(`../../allstarr/wwwroot${path}`, import.meta.url)).catch(() => null);
      return asset ? route.fulfill({ body: asset, contentType: path.endsWith(".png") ? "image/png" : "image/svg+xml" }) : route.fulfill({ status: 404 });
    }
    if (path.startsWith("/fonts/")) {
      const asset = await readFile(new URL(`../../allstarr/wwwroot${path}`, import.meta.url)).catch(() => null);
      return asset ? route.fulfill({ body: asset, contentType: path.endsWith(".woff2") ? "font/woff2" : "font/ttf" }) : route.fulfill({ status: 404 });
    }
    if (!path.startsWith("/api/")) return route.continue();
    const reply = (json: unknown) => route.fulfill({ json });
    if (path.includes("/artwork/")) return route.fulfill({ contentType: "image/svg+xml", body: '<svg xmlns="http://www.w3.org/2000/svg" width="184" height="184"><rect width="184" height="184" fill="#476875"/></svg>' });
    if (path.endsWith("/auth/me")) return reply({ authenticated: true, backend: "Jellyfin", backendConfigured: true, features: { intelligence: false }, user: { id: "listener", allstarrUserId: listenerId, name: "Listener", isAdministrator: true } });
    if (path.endsWith("/auth/oidc/status")) return reply({ enabled: false });
    if (path.endsWith("/onboarding/status")) return reply({ completed: true, shouldRedirectToSetup: false, setupOpen: false, completedSteps: ["backend-identity"], revision: 1, recoveryNotices: [], migration: {} });
    if (path.endsWith("/ui/schema")) return reply({ activeBackend: "Jellyfin", providers, settings: [], priorityGroups: [], configSections: [] });
    if (path.endsWith("/updates/stream")) return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    if (path === "/api/admin/provider-accounts") return reply({ accounts, audienceUsers: [{ id: listenerId, displayName: "Listener" }] });
    if (path === "/api/admin/providers/status") return reply([{ provider: "qobuz", capability: "streaming", ready: false, health: "degraded", canTest: true, canAttempt: false, supported: true, enabled: true, reasonCode: "service_unavailable", testedAt: timestamp }]);
    if (path === "/api/admin/ui/provider-summaries" || path.endsWith("/scores")) return reply({ providers: [] });
    if (path.endsWith("/latest")) return reply({ measurements: [] });
    if (path.endsWith("/health-history")) return reply({ capturedAt: timestamp, counts: { passed: 1, failed: 1, neutral: 0 }, checks: [{ outcome: "failed", capability: "streaming", accountLabel: "Public", testedAt: timestamp }, { outcome: "passed", capability: "streaming", accountLabel: "Public", testedAt: "2026-11-03T12:00:00Z" }] });
    if (path.startsWith("/api/admin/providers/use")) return reply({ householdRevision: "1", personalRevision: "1", providers: providers.map(provider => ({ providerId: provider.id, householdMode: "automatic", personalMode: null, effectiveMode: "automatic", canChoosePersonal: true })) });
    if (path === "/api/admin/scrobbling/deliveries") return reply({ items: [] });
    if (path.endsWith("/auth/playlist-consent")) return reply({ supported: true, granted: true, needsSignIn: false, updatedAt: timestamp, queuedSyncs: 0 });
    if (path.endsWith("/extensions/packages")) return reply([{ id: "qobuz", extensionId: "qobuz", displayName: "Qobuz", version: "1.0.0", description: "Search and stream music from your connected source.", lifecycle: "active", state: "active", active: true, installed: true, permissionReviewRequired: false, hasPermissions: false, capabilities: ["streaming"], revision: 1 }, {
      id: "deezer", extensionId: "deezer", displayName: "Deezer", version: "1.0.0", description: "Search the catalog and stream full songs from your connected Deezer account.", lifecycle: "active", state: "active", active: true, installed: true, permissionReviewRequired: false, hasPermissions: false, capabilities: ["metadata", "streaming", "playlists"], revision: 1,
    }]);
    if (path.endsWith("/extensions/registries")) return reply([]);
    if (path.endsWith("/extensions/store")) return reply({ items: [{ id: "deezer", displayName: "Deezer", version: "2.0.0", types: ["metadata", "streaming", "playlists"] }], errors: [] });
    if (path.includes("/favorites")) return reply({ links: [] });
    if (path === "/api/admin/config") return reply({ library: { storageMode: "Cache", cacheDurationHours: 72 } });
    if (path === "/api/admin/downloads") {
      const kept = url.searchParams.get("storage") === "kept";
      const files = [file, { ...file, path: "cache/apple/Quiet water.flac", title: "Quiet water", artist: "Cedar", externalId: "quiet-water" }].map(item => kept ? { ...item, storage: "kept", explicitlyKept: true, canDeleteKeptCopy: true, referenceCount: 2, publicationState: "Referenced" } : item);
      return reply({ storage: kept ? "kept" : "cache", files, count: files.length, managedCount: files.length, diagnosticCount: 0, totalSize: 58_000_000, totalSizeFormatted: "58 MB" });
    }
    if (path === "/api/admin/playlist-links") return reply({ playlistLinks: [playlist] });
    if (path === `/api/admin/playlist-links/${playlistId}`) return reply({ ...playlist, schedule, snapshotId: "snapshot", localCount: 2, tracks: [], unresolvedCount: 0, reviewCount: 0, unknownDurationCount: 0, durationMs: 540000 });
    if (path === "/api/admin/playlist-sources") return reply({ accounts: [{ id: accountId, providerId: "spotify", displayName: "My Spotify", scope: "personal" }], blockedAccounts: [], providers: [{ id: "spotify", displayName: "Spotify" }] });
    if (path === "/api/admin/media-targets") return reply({ targets: [{ id: "home", protocol: "jellyfin", backendInstanceId: "home", displayName: "Jellyfin" }] });
    if (path.endsWith("/playlist-forks")) return reply([]);
    if (path === "/api/admin/track-matches") {
      const rows = url.searchParams.has("externalSnapshotId") ? matches.filter(item => item.externalSnapshotId === url.searchParams.get("externalSnapshotId")) : url.searchParams.get("state") === "attention" ? matches.filter(item => item.state === "ambiguous") : matches;
      return reply({ matches: rows, stats: { total: 2, matched: 1, accepted: 1, automatic: 1, manualMatches: 0, manualRejections: 0, suggested: 0, ambiguous: 1, review: 1, unresolved: 0, rejected: 0, attention: 1 }, pagination: { page: 1, pageSize: 200, total: rows.length, totalPages: 1 } });
    }
    if (path.includes("/track-matches/targets/")) return reply({ tracks: [], providers: [] });
    if (path === "/api/admin/jobs") return reply({ jobs: [job], progress: [], hasMore: false });
    if (path === `/api/admin/jobs/${jobId}`) return reply({ job, progress: [], attempts: [{ attemptNumber: 1, startedAt: timestamp, completedAt: timestamp, outcome: "deferred" }], hasMore: false });
    if (path === "/api/admin/activity/tasks") return reply({ items: [task], hasMore: false });
    if (path === "/api/admin/problem-reports") return reply({ items: [report], nextCursor: null });
    if (path === `/api/admin/problem-reports/${reportId}`) return reply({ ...report, context: { schemaVersion: 1, capturedAt: timestamp, subjectName: "Listener", song: { title: "Evening song", artist: "River", catalogProviderId: "jellyfin" }, match: null, playback: { clientName: "Music app" }, checks: [] } });
    if (path === "/api/admin/play-traces") return reply([trace]);
    if (path.startsWith("/api/admin/play-traces/")) return reply(trace);
    if (path === "/api/admin/ui/activity") return reply({ items: [], hasMore: false });
    if (path === "/api/admin/mixes") return reply(mixes);
    if (path === "/api/admin/mixes/publish-setting") return reply({ enabled: true });
    if (path.startsWith("/api/admin/mixes/")) return reply({ mix: mixes.find(item => item.id === path.split("/").at(-1)) ?? mixes[0], songs });
    if (path === "/api/admin/releases") return reply({ entries: [], excluded: [], coverage: [] });
    if (path === "/api/admin/history/imports") return reply([]);
    if (path === "/api/admin/audiomuse/status") return reply({ reason: null, accessibleLibrarySongs: 1400, analyzedSongs: 1240, coverageComplete: true });
    return reply({});
  });
  return blocked;
}

async function drawer(page: Page, state: "expanded" | "collapsed") {
  const toggle = page.locator(".sidebar-expander");
  await expect(toggle).toBeVisible();
  if ((await toggle.getAttribute("aria-expanded") === "true") !== (state === "expanded")) await toggle.click();
  await expect(toggle).toHaveAttribute("aria-expanded", String(state === "expanded"));
}

async function expectGeometry(page: Page) {
  const result = await page.evaluate(() => {
    const visible = (element: Element) => {
      const rect = element.getBoundingClientRect(), style = getComputedStyle(element);
      return rect.width > 0 && rect.height > 0 && style.display !== "none" && style.visibility !== "hidden";
    };
    const identify = (element: Element) => `${element.tagName.toLowerCase()}.${[...element.classList].slice(0, 2).join(".")} ${(element.textContent ?? "").replace(/\s+/g, " ").trim().slice(0, 65)}`;
    const filterEscapes: string[] = [];
    for (const group of document.querySelectorAll<HTMLElement>(".filter-segments")) {
      if (!visible(group)) continue;
      const bounds = group.getBoundingClientRect();
      for (const button of group.querySelectorAll("button")) {
        if (!visible(button)) continue;
        const rect = button.getBoundingClientRect();
        if (rect.left < bounds.left - 1 || rect.right > bounds.right + 1 || rect.top < bounds.top - 1 || rect.bottom > bounds.bottom + 1) filterEscapes.push(identify(button));
      }
    }
    const narrowText: string[] = [];
    const canvas = document.createElement("canvas"), context = canvas.getContext("2d")!;
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    let node: Node | null;
    while ((node = walker.nextNode())) {
      const text = node.textContent?.replace(/\s+/g, " ").trim() ?? "";
      const element = node.parentElement;
      if (text.length <= 8 || !element || element.closest("script, style, template, svg, [hidden], .sr-only, .visually-hidden") || !visible(element)) continue;
      const style = getComputedStyle(element), bounds = element.getBoundingClientRect();
      let left = Math.max(0, bounds.left), right = Math.min(innerWidth, bounds.right), top = bounds.top, bottom = bounds.bottom;
      let concealed = false;
      for (let ancestor: HTMLElement | null = element; ancestor; ancestor = ancestor.parentElement) {
        const ancestorStyle = getComputedStyle(ancestor), rect = ancestor.getBoundingClientRect();
        if (ancestorStyle.display === "none" || ancestorStyle.visibility === "hidden" || Number(ancestorStyle.opacity) === 0 || ancestorStyle.clipPath === "inset(50%)") { concealed = true; break; }
        if (/(hidden|clip|auto|scroll)/.test(ancestorStyle.overflowX)) { left = Math.max(left, rect.left); right = Math.min(right, rect.right); }
        if (/(hidden|clip|auto|scroll)/.test(ancestorStyle.overflowY)) { top = Math.max(top, rect.top); bottom = Math.min(bottom, rect.bottom); }
      }
      if (concealed || right <= left || bottom <= top) continue;
      const range = document.createRange(); range.selectNodeContents(node);
      if (![...range.getClientRects()].some(rect => rect.right > left && rect.left < right && rect.bottom > top && rect.top < bottom)) continue;
      const padding = Number.parseFloat(style.paddingLeft) + Number.parseFloat(style.paddingRight);
      const available = element instanceof HTMLElement && element.clientWidth ? element.clientWidth - padding : bounds.width;
      context.font = style.font;
      const minimum = context.measureText("0").width * 6;
      if (available + 1 < minimum) narrowText.push(`${identify(element)}: ${available.toFixed(1)}px < ${minimum.toFixed(1)}px`);
    }
    return { overflow: document.documentElement.scrollWidth - innerWidth, filterEscapes, narrowText };
  });
  expect(result.overflow, "document width stays inside the viewport").toBeLessThanOrEqual(1);
  expect(result.filterEscapes, "every filter button stays inside its outline").toEqual([]);
  expect(result.narrowText, "visible text longer than eight characters has at least six character widths").toEqual([]);
}

async function expectPanel(page: Page) {
  const panel = page.locator("[data-side-panel]");
  await expect(panel).toBeVisible();
  await expect(panel).toHaveAttribute("data-mode", /^(docked|modal)$/);
  if (await panel.getAttribute("data-mode") === "docked") {
    const list = page.locator(".panel-open, .activity-content").first();
    await expect(list).toBeVisible();
    expect((await list.boundingBox())!.width, "a docked panel leaves a readable list").toBeGreaterThanOrEqual(640);
    await expect(page.locator(".side-panel-overlay")).toBeHidden();
  } else {
    await expect(panel).toHaveAttribute("aria-modal", "true");
    await expect(page.locator(".side-panel-overlay")).toBeVisible();
  }
  return panel;
}

const screens = [
  { name: "sources-services", url: "/#/sources/services", opener: ".source-row .row-action", title: "Qobuz" },
  { name: "sources-accounts", url: "/#/sources/accounts", opener: 'a[aria-label="Open Apple Music account"]', title: "Apple Music" },
  { name: "library-playlists", url: "/#/library/playlists", opener: 'button[aria-label="Open Evening mix playlist details"]', title: "Evening mix" },
  { name: "library-cached", url: "/#/library/cached", opener: 'button[aria-label="Open song details for Take On Me"]', title: "Take On Me" },
  { name: "library-kept", url: "/#/library/kept", opener: 'button[aria-label="Open song details for Take On Me"]', title: "Take On Me" },
  { name: "library-matches", url: "/#/library/matches", opener: 'button[aria-label="Open match for Evening song One"]:visible', title: "Evening song One" },
  { name: "activity-jobs", url: "/#/activity?view=jobs", opener: ".job-row .row-action", title: "Update playlist" },
  { name: "activity-tasks", url: "/#/activity?view=tasks", opener: ".task-open", title: "Evening mix" },
  { name: "activity-reports", url: "/#/activity?view=reports", opener: ".report-list .row-action", title: "Playback" },
  { name: "activity-plays", url: "/#/activity?view=plays", opener: ".play-row .row-action", title: "Evening song" },
  { name: "for-you-mix", url: "/#/for-you", opener: 'button[aria-label="Open Blend 1"]', title: "Blend 1" },
] as const;
const appearances = [[1024, "dark"], [1100, "dark"], [1180, "dark"], [1280, "dark"], [1100, "light"]] as const;

for (const [width, theme] of appearances) for (const state of ["expanded", "collapsed"] as const) for (const screen of screens) {
  test(`${screen.name} at ${width} ${theme} with ${state} drawer`, async ({ page }) => {
    await page.clock.install({ time: new Date("2026-11-04T14:00:00Z") });
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const blocked = await fixture(page);
    await page.goto(screen.url);
    const opener = page.locator(screen.opener).first();
    await expect(opener).toBeVisible();
    await drawer(page, state);
    if (screen.name === "sources-services") await expect(page.locator(".extension-attention")).toContainText("Deezer");
    if (screen.name === "for-you-mix") {
      const cards = page.locator(".mix-shelf .mix-card");
      await expect(cards).toHaveCount(8);
      expect(await cards.evaluateAll(elements => elements.every(element => element.getBoundingClientRect().width >= 132))).toBe(true);
    }
    const content = page.locator(".sources-redesign, .library-playlists, .library-downloads, .mapping-page, .activity-content, .mix-shelf").first();
    const before = await content.boundingBox();
    await opener.click();
    const panel = await expectPanel(page);
    if (await panel.getAttribute("aria-modal") === "true") {
      const after = await content.boundingBox();
      expect(after!.width, "opening a modal sheet preserves the content width").toBeCloseTo(before!.width, 0);
      expect(after!.x, "opening a modal sheet preserves the content position").toBeCloseTo(before!.x, 0);
    }
    await expect(panel.getByRole("heading").first()).toHaveText(screen.title);
    if (screen.name === "sources-services") await expect(page.locator(".extension-attention")).toContainText("Deezer");
    if (screen.name === "for-you-mix") await expect(panel.getByRole("list", { name: "Songs", exact: true }).getByRole("listitem")).toHaveCount(8);
    await page.evaluate(async () => { await document.fonts.ready; });
    await mkdir("test-results/medium-width", { recursive: true });
    await page.screenshot({ path: `test-results/medium-width/${screen.name}-${width}-${theme}-${state}.png`, fullPage: true });
    await expectGeometry(page);
    expect(blocked, "fixtures do not request remote resources").toEqual([]);
    expect(await page.pageErrors()).toEqual([]);
  });
}

async function expectFocusLoop(page: Page, panel: Locator) {
  const targets = panel.locator('a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), summary, [tabindex="0"]');
  const visible = [];
  for (const target of await targets.all()) if (await target.isVisible() && await target.getAttribute("tabindex") !== "-1") visible.push(target);
  expect(visible.length).toBeGreaterThan(1);
  await visible.at(-1)!.focus();
  await page.keyboard.press("Tab");
  await expect(visible[0]).toBeFocused();
  await visible[0].focus();
  await page.keyboard.press("Shift+Tab");
  await expect(visible.at(-1)!).toBeFocused();
}

for (const name of ["sources-services", "library-cached", "activity-jobs"] as const) {
  test(`${name} modal keeps focus and restores its opener after closing`, async ({ page }) => {
    await page.setViewportSize({ width: 1100, height: 900 });
    await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
    await fixture(page);
    const screen = screens.find(item => item.name === name)!;
    await page.goto(screen.url);
    const opener = page.locator(screen.opener).first();
    await expect(opener).toBeVisible();
    await drawer(page, "expanded");
    await opener.focus();
    await page.keyboard.press("Enter");
    const panel = await expectPanel(page);
    await expect(panel).toHaveAttribute("data-mode", "modal");
    await expectFocusLoop(page, panel);
    await page.keyboard.press("Escape");
    await expect(panel).toBeHidden();
    await expect(opener).toBeFocused();
    await opener.press("Enter");
    await expect(panel).toBeVisible();
    await page.locator(".side-panel-overlay").click({ position: { x: 8, y: 450 } });
    await expect(panel).toBeHidden();
    await expect(opener).toBeFocused();
  });
}

test("panel mode follows drawer changes and resizing while it stays open", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  await fixture(page);
  await page.goto("/#/sources/services");
  const opener = page.locator(".source-row .row-action").first();
  await expect(opener).toBeVisible();
  await drawer(page, "collapsed");
  await opener.click();
  const panel = await expectPanel(page);
  await expect(panel).toHaveAttribute("data-mode", "docked");
  await panel.evaluate(element => Reflect.set(element, "retainedPanel", true));
  await drawer(page, "expanded");
  await expect(panel).toHaveAttribute("data-mode", "modal");
  await expectPanel(page);
  await page.setViewportSize({ width: 1600, height: 900 });
  await expect(panel).toHaveAttribute("data-mode", "docked");
  await expectPanel(page);
  await page.setViewportSize({ width: 1100, height: 900 });
  await expect(panel).toHaveAttribute("data-mode", "modal");
  await expectPanel(page);
  expect(await panel.evaluate(element => Reflect.get(element, "retainedPanel"))).toBe(true);
  await expectGeometry(page);
});


test("source cards and filter pills adapt to narrow containers on desktop", async ({ page }) => {
  await page.setViewportSize({ width: 1180, height: 900 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  await fixture(page);
  await page.goto("/#/sources/services");
  const section = page.locator(".extension-attention");
  await expect(section).toBeVisible();
  await section.evaluate(element => { element.style.width = "440px"; });
  const layout = section.locator(".extension-layout");
  await expect(layout).toBeVisible();
  const boxes = await layout.evaluate(element => {
    const box = (selector: string) => element.querySelector(selector)!.getBoundingClientRect().toJSON();
    return { row: element.getBoundingClientRect().toJSON(), icon: box(".provider-art"), title: box(".extension-title"), description: box(".extension-copy > p"), capabilities: box(".source-row-capabilities"), controls: box(".extension-controls") };
  });
  expect(boxes.title.left).toBeGreaterThan(boxes.icon.right);
  expect(boxes.description.top).toBeGreaterThanOrEqual(boxes.title.bottom);
  expect(boxes.description.left).toBeCloseTo(boxes.row.left, 0);
  expect(boxes.description.width).toBeCloseTo(boxes.row.width, 0);
  expect(boxes.controls.top).toBeGreaterThanOrEqual(boxes.capabilities.bottom);
  const filters = page.locator(".source-toolbar .filter-segments");
  await filters.evaluate(element => { element.style.maxWidth = "220px"; });
  expect(await filters.locator("button").evaluateAll(elements => new Set(elements.map(element => element.getBoundingClientRect().top)).size)).toBeGreaterThan(1);
  await expectGeometry(page);
});
