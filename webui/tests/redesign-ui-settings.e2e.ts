import { expect, test, type Page } from "@playwright/test";

async function fixture(page: Page, libraryFailure = false) {
  const writes: Record<string, unknown>[] = [];
  let config = { clientApi: { enabled: false }, backendType: "Jellyfin", operatorSettings: { "Backend:MusicLibraryIds": "music", "Cors:AllowedOrigins": "https://app.invalid" }, audio: { quality: "CdLossless" }, jellyfin: { url: "http://media.invalid:8096" }, matching: { localPreferencePercent: 7 } };
  await page.route("**/api/admin/**", async route => {
    const path = new URL(route.request().url()).pathname;
    const reply = (json: unknown) => route.fulfill({ json });
    if (path.endsWith("/auth/me")) return reply({ authenticated: true, backend: "Jellyfin", user: { id: "admin", name: "Alex", isAdministrator: true } });
    if (path.endsWith("/auth/oidc/status")) return reply({ enabled: false });
    if (path.endsWith("/updates/stream")) return route.fulfill({ contentType: "text/event-stream", body: ": ready\n\n" });
    if (path.endsWith("/onboarding/status")) return reply({ completed: true, setupOpen: false, shouldRedirectToSetup: false, revision: 1, recoveryNotices: [], migration: {}, steps: [{ id: "backend-identity", title: "Your media server", explanation: "Connect your media server.", required: true, state: "pending" }] });
    if (path.endsWith("/ui/schema")) return reply({ providers: [{ id: "first", name: "First source" }, { id: "second", name: "Second source" }], priorityGroups: [{ id: "metadata", label: "Search", envKey: "METADATA_ORDER", providers: ["first", "second"] }, { id: "streaming", label: "Streaming", envKey: "STREAMING_ORDER", providers: ["second", "first"] }], configSections: [
      { id: "general", label: "Defaults for everyone", fields: [
        { key: "CLIENT_API_ENABLED", label: "Client API for apps", type: "toggle", valuePath: "clientApi.enabled" },
        { key: "BACKEND_TYPE", label: "Music server", type: "text", valuePath: "backendType", ownership: "deployment" },
        { key: "AUDIO_QUALITY", label: "Audio quality", type: "audio-quality", valuePath: "audio.quality" },
        { key: "MATCHING_LOCAL_PREFERENCE_PERCENT", label: "Prefer your own music when unsure", type: "number", valuePath: "matching.localPreferencePercent", min: 0, max: 20 },
      ] }, { id: "operator", label: "Library", fields: [
        { key: "BACKEND_MUSIC_LIBRARY_IDS", label: "Libraries to use", type: "text", valuePath: "operatorSettings.Backend:MusicLibraryIds", requiresRestart: true },
        { key: "CORS_ALLOWED_ORIGINS", label: "Browser origins", type: "text", valuePath: "operatorSettings.Cors:AllowedOrigins", requiresRestart: true },
      ] },
    ] });
    if (path.endsWith("/ui/music-libraries")) return libraryFailure
      ? route.fulfill({ status: 502, json: { error: "Music libraries could not be loaded. Check the media server connection." } })
      : reply([{ id: "music", name: "Music" }, { id: "books", name: "Concert recordings" }]);
    if (path.endsWith("/config")) {
      if (route.request().method() === "POST") {
        const body = route.request().postDataJSON(); writes.push(body);
        if (body.updates.CLIENT_API_ENABLED !== undefined) config = { ...config, clientApi: { enabled: body.updates.CLIENT_API_ENABLED === "true" } };
        if (body.updates.BACKEND_MUSIC_LIBRARY_IDS !== undefined) config.operatorSettings["Backend:MusicLibraryIds"] = body.updates.BACKEND_MUSIC_LIBRARY_IDS;
        if (body.updates.AUDIO_QUALITY) config.audio.quality = body.updates.AUDIO_QUALITY;
        if (body.updates.MATCHING_LOCAL_PREFERENCE_PERCENT !== undefined) config.matching.localPreferencePercent = Number(body.updates.MATCHING_LOCAL_PREFERENCE_PERCENT);
        return reply({ updatedKeys: Object.keys(body.updates), requiresRestart: false });
      }
      return reply(config);
    }
    if (path.endsWith("/access/oidc")) return reply({ settings: { enabled: false, authority: "", clientId: "", publicUrl: "", displayName: "Single sign-on", hasClientSecret: false, revision: "1" }, csrfToken: "test" });
    if (path.endsWith("/access/backend")) return reply({ settings: { supported: true, connectionSaved: true, configured: true, needsReview: false, revision: "1" }, csrfToken: "test" });
    if (path.endsWith("/storage/files")) return reply({ settings: { available: true, files: 0, pending: false }, csrfToken: "test" });
    if (path.endsWith("/providers/scores")) return reply({ providers: [{ providerId: "first", score: 100, uptimePercent: 100, checks: 1, days: [], capabilities: [{ capability: "metadata", score: 100, uptimePercent: 100, checks: 1, failures: 0 }] }] });
    if (path.endsWith("/provider-accounts")) return reply({ accounts: [], listenersCanConnectOwnAccounts: true, audienceUsers: [{ id: "sam", displayName: "Sam", isAdministrator: true, lastActiveAt: "2026-11-06T12:00:00Z", playsThisWeek: 36, playlistEditingAllowed: true, recentPlays: [{ title: "Home again", artist: "River", playedAt: "2026-11-06T12:00:00Z" }] }] });
    if (path.endsWith("/playlist-links")) return reply({ playlistLinks: [] });
    if (path.endsWith("/playlist-forks")) return reply([]);
    if (path.endsWith("/clean-only-lock")) return reply({ revision: "1", cleanOnlyLocked: false, values: {}, householdDefaults: {} });
    if (path.endsWith("/alerts/incidents")) return reply([1, 2].map(id => ({ id: String(id), kind: "source-failure", state: "open", message: "A source is repeatedly failing", action: "Review source health", resource: `Source ${id}` })));
    if (path.endsWith("/alerts")) return reply({ channels: [], deliveries: [], revision: 1, permittedDestinations: [], thresholds: null });
    if (path.endsWith("/health")) return reply({ protocol: "Jellyfin", configuration: { mode: "server-api-key", hasConfiguration: true, serviceCredentialReady: true, enabled: true, intervalMinutes: 60, searchQuery: "Sample", firstByteBudgetSeconds: 15, revision: 1 }, listeners: [], runs: [{ id: "run", protocol: "Jellyfin", mode: "server-api-key", startedAt: "2026-11-03T12:00:00Z", completedAt: "2026-11-03T12:00:01Z", passed: false, alertStatus: "none", steps: [{ name: "backend", status: "passed", latencyMilliseconds: 5, reason: null }, { name: "lyrics", status: "skipped", latencyMilliseconds: null, reason: "lyrics_unavailable" }] }] });
    if (path.endsWith("/storage")) return reply({ storage: { readiness: "Ready" }, backups: [{ id: "backup", fileName: "backup.zip", status: "verified", createdAt: "2026-11-03T03:00:00Z", bytes: 1048576, origin: "scheduled" }], restorePending: false, backupPolicy: { enabled: true, timeUtc: "03:00", directory: "", dailyRetentionCount: 14, weeklyRetentionCount: 8, manualRetentionCount: 10, revision: "1" }, backupStatus: { state: "idle", directoryState: "ready", lastSuccessAt: null } });
    if (path.endsWith("/downloads")) return reply({ totalSize: 1048576, count: 1, files: [] });
    if (path.endsWith("/audiomuse/status")) return reply({ reason: null, analyzedSongs: 10, coverageComplete: true });
    if (path.endsWith("/audiomuse/connection")) return reply({ baseUrl: "", keyConfigured: false, revision: 1 });
    if (path.endsWith("/preferences")) {
      const values = { lyricsOrder: "backend,kept", explicitFilter: "All", showExternalLabel: true, showExplicitLabel: true, playbackStart: "Balanced", qualityLimit: null };
      return reply({ values, householdDefaults: values, householdQuality: "BestAvailable", usesHouseholdDefaults: true, revision: "1", cleanOnlyLocked: false });
    }
    return reply({ enabled: false, categories: [], media: {}, memory: {}, metadata: {}, activity: {}, artworkLimits: {}, extensionStorage: {} });
  });
  return writes;
}

const pages = ["playback", "library", "discovery", "history", "people", "alerts", "storage", "backups", "health", "setup", "appearance"];
const headings: Record<string, string> = { playback: "Playback", library: "Library", discovery: "Mixes and discovery", history: "History and scrobbling",
  people: "People and access", alerts: "Alerts", storage: "Storage", backups: "Backups", health: "Health", setup: "Setup guide", appearance: "Appearance" };
for (const [width, theme] of [[1280, "light"], [1280, "dark"], [390, "light"], [390, "dark"]] as const) {
  test(`Settings pages and panels ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await page.emulateMedia({ reducedMotion: "reduce" });
    await fixture(page);
    for (const section of pages) {
      await page.goto(`/#/settings/${section}`);
      await expect(page.getByRole("heading", { name: headings[section], level: 2, exact: true })).toBeVisible();
      await expect(page.getByLabel("Loading items", { exact: true })).toHaveCount(0, { timeout: 20_000 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.screenshot({ path: `test-results/ui-settings/${section}-${width}-${theme}.png`, fullPage: true });
      const open = section === "people" ? page.getByRole("button", { name: "Open Sam", exact: true })
        : section === "health" ? page.getByRole("button", { name: "Open Server connection", exact: true }) : null;
      if (open) {
        await open.click();
        await expect(page.getByRole("dialog")).toBeVisible();
        await page.screenshot({ path: `test-results/ui-settings/${section}-panel-${width}-${theme}.png`, fullPage: true });
        await page.getByRole("button", { name: "Close", exact: true }).click();
        await expect(page.getByRole("dialog")).toHaveCount(0);
      }
      const capture = async (name: string) => page.screenshot({ path: `test-results/ui-settings/${name}-${width}-${theme}.png`, fullPage: true });
      if (section === "library") {
        await page.getByRole("checkbox", { name: "Concert recordings", exact: true }).check();
        await expect(page.getByRole("alertdialog")).toBeVisible(); await capture("restart-dialog");
        await page.getByRole("alertdialog").getByRole("button", { name: "Cancel", exact: true }).click();
      } else if (section === "alerts") {
        await page.getByRole("button", { name: "Add your channel", exact: true }).click();
        await expect(page.getByRole("dialog")).toBeVisible(); await capture("alert-channel-dialog");
        await page.getByRole("dialog").getByRole("button", { name: "Cancel", exact: true }).click();
      } else if (section === "backups") {
        await page.getByRole("button", { name: /More actions for/ }).click(); await page.getByRole("menuitem", { name: "Restore…", exact: true }).click();
        const dialog = page.getByRole("dialog"); await expect(dialog).toBeVisible();
        await expect(dialog.getByRole("button", { name: "Restore on restart", exact: true })).toBeDisabled();
        await dialog.getByLabel("Type RESTORE to confirm", { exact: true }).fill("RESTORE");
        await expect(dialog.getByRole("button", { name: "Restore on restart", exact: true })).toBeEnabled();
        await capture("restore-dialog"); await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
      } else if (section === "discovery") {
        await page.getByRole("button", { name: "Edit connection", exact: true }).click();
        const connection = page.getByRole("form", { name: "AudioMuse connection", exact: true }); await expect(connection).toBeVisible();
        await capture("sound-connection"); await connection.getByRole("button", { name: "Cancel", exact: true }).click();
        await expect(connection).toHaveCount(0);
      } else if (section === "setup") {
        await page.getByRole("button", { name: "Open", exact: true }).click();
        await expect(page.getByRole("heading", { name: "Your media server", exact: true })).toBeVisible();
        await expect(page.getByLabel("Loading items", { exact: true })).toHaveCount(0); await capture("setup-first-step");
      } else if (section === "playback") {
        await page.locator(".routing-group li.has-score .routing-copy").click();
        await expect(page.getByRole("dialog", { name: "First source", exact: true })).toBeVisible(); await capture("quality-score-panel");
        await page.getByRole("dialog").getByRole("button", { name: "Close", exact: true }).click();
        const filters = page.getByRole("radiogroup", { name: "Source orders", exact: true });
        await expect(filters.getByRole("radio", { name: /Search/ })).toContainText("2");
        await filters.getByRole("radio", { name: /Streaming/ }).click();
        await expect(page.getByRole("heading", { name: "Streaming source order", exact: true })).toBeVisible();
      }
    }
  });
}

test("Settings changes save with Undo and restart changes wait for confirmation", async ({ page }) => {
  const writes = await fixture(page);
  await page.goto("/#/settings/people");
  await page.getByRole("switch", { name: "Client API for apps", exact: true }).check();
  await expect.poll(() => writes.length).toBe(1);
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect.poll(() => writes.length).toBe(2);
  expect(writes[1]).toEqual({ updates: { CLIENT_API_ENABLED: "false" } });
  await page.goto("/#/settings/library");
  await page.getByRole("checkbox", { name: "Concert recordings", exact: true }).check();
  const dialog = page.getByRole("alertdialog");
  await expect(dialog).toContainText("Restart Allstarr to apply?");
  expect(writes.length).toBe(2);
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.getByRole("checkbox", { name: "Music", exact: true })).toBeChecked();
  await expect(page.getByRole("checkbox", { name: "Concert recordings", exact: true })).not.toBeChecked();
  await expect(page.getByText("Set in your deployment")).toBeVisible();
});

test("Settings search results open above the section links", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await fixture(page);
  await page.goto("/#/settings/library");
  await page.getByRole("navigation", { name: "Settings sections" }).getByRole("searchbox", { name: "Find a setting" }).fill("Libraries");
  const results = page.getByRole("navigation", { name: "Settings sections" }).getByRole("list", { name: "Matching settings" });
  await expect(results.getByRole("link").first()).toBeVisible();
  for (const result of await results.getByRole("listitem").all()) {
    const box = (await result.boundingBox())!;
    const onTop = await page.evaluate(({ x, y }) => document.elementFromPoint(x, y)?.closest(".settings-find-results") !== null,
      { x: box.x + box.width / 2, y: box.y + box.height / 2 });
    expect(onTop).toBe(true);
  }
});

test("Single sign-on fields and browser access stay folded until needed", async ({ page }) => {
  await fixture(page);
  await page.goto("/#/settings/people");
  await expect(page.getByLabel("Issuer address", { exact: true })).toHaveCount(0);
  await page.getByRole("switch", { name: "Enable single sign-on", exact: true }).check();
  await expect(page.getByLabel("Issuer address", { exact: true })).toBeVisible();
  await expect(page.getByLabel("Browser origins", { exact: true })).toBeHidden();
  await page.getByText("Advanced: browser origins, methods and headers", { exact: true }).click();
  await expect(page.getByLabel("Browser origins", { exact: true })).toBeVisible();
});

test("Health skips are neutral and repeated open problems are grouped", async ({ page }) => {
  await fixture(page);
  await page.goto("/#/settings/health");
  await expect(page.getByLabel("Page status").first()).toContainText("Passing");
  await expect(page.getByLabel("Page status").first()).toContainText("1 check passed");
  await expect(page.getByText("Partly checked", { exact: true })).toHaveCount(0);
  await expect(page.getByRole("group", { name: "Last 30 checks", exact: true })).toBeVisible();
  await page.goto("/#/settings/alerts");
  const group = page.getByRole("button", { name: /A source is repeatedly failing/ });
  await expect(group).toContainText("2");
  await group.click();
  await expect(page.getByText("Source 1", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Add your channel", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Add an alert channel", exact: true })).toBeVisible();
});

test("Restore waits for a verified safety backup before staging state", async ({ page }) => {
  await fixture(page);
  let staged = 0, created = 0;
  await page.route("**/api/admin/storage/backups", route => { created++; return route.fulfill({ json: { jobId: "safety", created: true } }); });
  await page.route("**/api/admin/storage/backups/backup/download", route => route.fulfill({ contentType: "application/zip", body: "test archive" }));
  await page.route("**/api/admin/storage/restore", route => { staged++; return route.fulfill({ json: { restartRequired: true, message: "Restore staged for the next restart." } }); });
  await page.goto("/#/settings/backups");
  await page.getByRole("button", { name: /More actions for/ }).click(); await page.getByRole("menuitem", { name: "Restore…", exact: true }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("checkbox", { name: "Back up the current state first", exact: true })).toBeChecked();
  await dialog.getByLabel("Type RESTORE to confirm", { exact: true }).fill("RESTORE");
  await page.route("**/api/admin/storage", route => route.fulfill({ json: { storage: { readiness: "Ready" }, backups: [], restorePending: false, backupStatus: { state: "idle", activeJobId: null, lastSuccessAt: "2026-11-04T03:00:00Z" } } }));
  await dialog.getByRole("button", { name: "Restore on restart", exact: true }).click();
  await expect.poll(() => staged).toBe(1); expect(created).toBe(1);
});

test("Quality changes save and undo, and source filters remember the selected list", async ({ page }) => {
  const writes = await fixture(page);
  await page.goto("/#/settings/quality");
  const quality = page.getByRole("slider", { name: "Audio quality", exact: true });
  await quality.focus(); await page.keyboard.press("ArrowRight");
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0]).toEqual({ updates: { AUDIO_QUALITY: "HiResLossless" } });
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect.poll(() => writes.length).toBe(2);
  expect(writes[1]).toEqual({ updates: { AUDIO_QUALITY: "CdLossless" } });
  await page.getByRole("switch", { name: "Prefer your own music when unsure", exact: true }).uncheck();
  await expect.poll(() => writes.length).toBe(3);
  expect(writes[2]).toEqual({ updates: { MATCHING_LOCAL_PREFERENCE_PERCENT: "0" } });
  await page.getByRole("radiogroup", { name: "Source orders", exact: true }).getByRole("radio", { name: /Streaming/ }).click();
  await page.reload();
  await expect(page.getByRole("heading", { name: "Streaming source order", exact: true })).toBeVisible();
});

test("Undo after a restart asks before restarting again", async ({ page }) => {
  const writes = await fixture(page);
  await page.goto("/#/settings/library");
  await page.evaluate(() => sessionStorage.setItem("allstarr.settings.restart-undo", JSON.stringify({ kind: "config", key: "BACKEND_MUSIC_LIBRARY_IDS", value: "music", label: "Libraries to use" })));
  await page.reload();
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect(page.getByRole("alertdialog")).toContainText("Restart Allstarr to undo?");
  await page.getByRole("alertdialog").getByRole("button", { name: "Cancel", exact: true }).click();
  expect(writes).toEqual([]);
});

test("A failed safety backup prevents restore staging", async ({ page }) => {
  await fixture(page);
  let staged = 0;
  await page.route("**/api/admin/storage/backups", route => route.fulfill({ status: 503, json: { error: "The backup folder is unavailable." } }));
  await page.route("**/api/admin/storage/restore", route => { staged++; return route.fulfill({ json: {} }); });
  await page.goto("/#/settings/backups");
  await page.getByRole("button", { name: /More actions for/ }).click(); await page.getByRole("menuitem", { name: "Restore…", exact: true }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel("Type RESTORE to confirm", { exact: true }).fill("RESTORE");
  await dialog.getByRole("button", { name: "Restore on restart", exact: true }).click();
  await expect(dialog.getByRole("alert")).toContainText("The backup folder is unavailable.");
  expect(staged).toBe(0);
});

test("Libraries save named selections and an empty value for all libraries", async ({ page }) => {
  const writes = await fixture(page);
  await page.goto("/#/settings/library");
  await expect(page.getByRole("group", { name: "Libraries to use", exact: true })).toBeVisible();
  await page.getByRole("checkbox", { name: "Concert recordings", exact: true }).check();
  await page.getByRole("alertdialog").getByRole("button", { name: "Restart and apply", exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0]).toEqual({ updates: { BACKEND_MUSIC_LIBRARY_IDS: "music,books" } });
  await page.getByRole("checkbox", { name: "All libraries", exact: true }).check();
  await page.getByRole("alertdialog").getByRole("button", { name: "Restart and apply", exact: true }).click();
  await expect.poll(() => writes.length).toBe(2);
  expect(writes[1]).toEqual({ updates: { BACKEND_MUSIC_LIBRARY_IDS: "" } });
  await expect(page.getByRole("checkbox", { name: "All libraries", exact: true })).toBeChecked();
});

test("Libraries retain manual ID entry when discovery fails", async ({ page }) => {
  const writes = await fixture(page, true);
  await page.goto("/#/settings/library");
  await expect(page.getByText("Music libraries could not be loaded. Check the media server connection.", { exact: true })).toBeVisible();
  const input = page.getByRole("textbox", { name: "Libraries to use", exact: true });
  await expect(input).toHaveValue("music");
  await input.fill("music,books"); await input.blur();
  await page.getByRole("alertdialog").getByRole("button", { name: "Restart and apply", exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0]).toEqual({ updates: { BACKEND_MUSIC_LIBRARY_IDS: "music,books" } });
});


test("Settings structure keeps source orders inside their card and playlist permissions together", async ({ page }) => {
  await fixture(page);
  await page.goto("/#/settings/playback");
  const card = page.locator(".routing-group");
  await expect(card.getByRole("radiogroup", { name: "Source orders", exact: true })).toBeVisible();
  await expect(card.getByText("Details", { exact: true })).toHaveCount(0);
  const policy = page.getByRole("form", { name: "Audio quality settings", exact: true });
  await expect(policy.locator(":scope > details.settings-fold")).toHaveText(/Exact quality asked from each service/);
  await page.goto("/#/settings/people");
  const permissions = page.getByRole("region", { name: "Playlist editing", exact: true });
  await expect(permissions.getByText("Per listener", { exact: true })).toBeVisible();
  await expect(permissions.getByText("Household fallback", { exact: true })).toBeVisible();
  await expect(permissions.getByRole("link", { name: "See who", exact: true })).toHaveAttribute("href", "#/settings/people");
});

test("Identical passing health runs share one history group", async ({ page }) => {
  await fixture(page);
  await page.route("**/api/admin/health", route => route.fulfill({ json: { protocol: "Jellyfin", configuration: { mode: "server-api-key", hasConfiguration: true, serviceCredentialReady: true, enabled: true, intervalMinutes: 60 }, listeners: [], runs: [1, 2].map(index => ({ id: String(index), protocol: "Jellyfin", mode: "server-api-key", startedAt: `2026-11-0${index}T12:00:00Z`, completedAt: `2026-11-0${index}T12:00:01Z`, alertStatus: "none", steps: [{ name: "backend", status: "passed", latencyMilliseconds: 5, reason: null }] })) } }));
  await page.goto("/#/settings/health");
  const history = page.getByRole("region", { name: "Check history", exact: true });
  await expect(history.locator(".grouped-row")).toHaveCount(1);
  await expect(history.getByRole("button", { name: /Passing/ })).toContainText("2");
  await expect(page.getByRole("group", { name: "Last 30 checks", exact: true }).locator(":scope > *")).toHaveCount(30);
  await history.getByRole("button", { name: /Passing/ }).click();
  await expect(history.locator(".item-row")).toHaveCount(2);
  const strip = page.getByRole("group", { name: "Last 30 checks", exact: true });
  await strip.focus();
  await page.keyboard.press("ArrowLeft");
  await page.keyboard.press("Enter");
  await expect(page.getByRole("dialog", { name: "Jellyfin check", exact: true })).toBeVisible();
});

test("A song.link key is requested with an action before showing a replacement field", async ({ page }) => {
  const writes = await fixture(page);
  await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: { providers: [], configSections: [{ id: "operator", label: "Links", fields: [{ key: "ODESLI_API_KEY", label: "song.link API key", type: "password", valuePath: "operatorSettings.Odesli:ApiKey", sensitive: true, requiresRestart: true }] }] } }));
  for (const [path, json] of [["**/api/admin/providers/status", []], ["**/api/admin/ui/provider-summaries", { providers: [] }], ["**/api/admin/providers/scores", { providers: [] }],
    ["**/api/admin/extensions/packages", []], ["**/api/admin/extensions/registries", []], ["**/api/admin/extensions/store", { items: [], errors: [] }]] as const)
    await page.route(path, route => route.fulfill({ json }));
  await page.goto("/#/integrations/services");
  await expect(page.getByRole("heading", { name: "Helpers for every source", exact: true })).toBeVisible();
  await expect(page.getByLabel("song.link API key", { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Add key…", exact: true }).click();
  await expect(page.getByLabel("song.link API key", { exact: true })).toBeVisible();
  expect(writes).toEqual([]);
});
