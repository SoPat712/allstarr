import { expect, test } from "@playwright/test";
import { expectContainedMobileGeometry, widenText } from "./mobile-geometry";
import { openSourceSettings, waitForSettledOptions, schema, responses, routeRelease, mockApi, mockSignedSessionExtension } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

for (const width of [390, 1280]) {
  test(`Spotify Advanced settings reset individual fields at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await mockApi(page);
    const overrides = [
      ["SPOTIFY_PATHFINDER_ENDPOINT", "Pathfinder endpoint", "pathfinderEndpoint", "https://service.test/query"],
      ["SPOTIFY_LIBRARY_QUERY_HASH", "Library query hash", "libraryQueryHash", "a".repeat(64)],
      ["SPOTIFY_PLAYLIST_QUERY_HASH", "Playlist query hash", "playlistQueryHash", "b".repeat(64)],
      ["SPOTIFY_LIKED_SONGS_QUERY_HASH", "Liked Songs query hash", "likedSongsQueryHash", "c".repeat(64)],
      ["SPOTIFY_TOTP_SECRETS_URL", "Public TOTP secrets URL", "totpSecretsUrl", "https://service.test/secrets.json"],
      ["SPOTIFY_TOKEN_ENDPOINT", "Token endpoint", "tokenEndpoint", "https://service.test/token"],
    ];
    await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
      ...schema, providers: schema.providers.map(provider => provider.id !== "spotify" ? provider : {
        ...provider, connectionKind: "operator_managed", configSchema: [
          { key: "SPOTIFY_API_CACHE_DURATION_MINUTES", label: "Cache minutes", type: "number", valuePath: "spotifyApi.cacheDurationMinutes" },
          { key: "SPOTIFY_API_PREFER_ISRC_MATCHING", label: "Prefer ISRC matching", type: "toggle", valuePath: "spotifyApi.preferIsrcMatching" },
          ...overrides.map(([key, label, path, value]) => ({ key, label, type: "text", valuePath: `spotifyApi.${path}`,
            advanced: true, defaultValueJson: JSON.stringify(value) })),
        ],
      }),
    } }));
    let saved: Record<string, string> | undefined;
    const savedValues: Record<string, string> = {};
    await page.route("**/api/admin/config", route => {
      if (route.request().method() === "POST") {
        saved = route.request().postDataJSON().updates;
        Object.assign(savedValues, saved);
        return route.fulfill({ json: { message: "Runtime configuration updated." } });
      }
      return route.fulfill({ json: { ...(responses["/api/admin/config"] as object), spotifyApi: {
        cacheDurationMinutes: savedValues.SPOTIFY_API_CACHE_DURATION_MINUTES ?? 60, preferIsrcMatching: true,
        ...Object.fromEntries(overrides.map(([key, , path]) => [path, savedValues[key] ?? "custom-override"])),
      } } });
    });
    await page.goto("#/integrations/services/spotify");
    const form = page.getByRole("form", { name: "Spotify settings" });
    await expect(form.getByLabel("Cache minutes")).toBeVisible();
    await expect(form.getByRole("checkbox", { name: "Prefer ISRC matching" })).toBeVisible();
    const advanced = form.locator("details");
    await expect(advanced).not.toHaveAttribute("open", "");
    await expect(form.getByLabel("Pathfinder endpoint", { exact: true })).toBeHidden();
    await advanced.getByText("Advanced", { exact: true }).click();
    for (const [key, label, , value] of overrides) {
      await expect(form.locator(`input[name="${key}"]`)).toHaveValue("custom-override");
      await form.getByRole("button", { name: `Reset ${label} to default`, exact: true }).click();
      await expect.poll(() => saved?.[key]).toBe(value);
      await expect(form.getByRole("button", { name: `Reset ${label} to default`, exact: true })).toBeEnabled();
      await expect(form.locator(`input[name="${key}"]`)).toHaveValue(value);
    }
    await expect.poll(() => saved).toBeDefined();
    await form.getByLabel("Cache minutes").fill("45");
    if (width < 768) await expectContainedMobileGeometry(page);
    await advanced.getByText("Advanced", { exact: true }).click();
    await form.getByLabel("Cache minutes").press("Tab");
    await expect.poll(() => saved).toEqual({ SPOTIFY_API_CACHE_DURATION_MINUTES: "45" });
  });
}

for (const [width, theme] of [[1280, "dark"], [390, "light"]] as const) {
  test(`Services page parity at ${width} ${theme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    await page.goto("#/sources/services");
    await expect(page.getByRole("region", { name: "Services status", exact: true })).toBeVisible();
    await expect(page.locator(".source-row").first()).toBeVisible();
    await page.evaluate(async () => { await document.fonts.ready; });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`services-${width}-${theme}.png`), fullPage: true });
  });
}

for (const route of ["#/integrations/services", "#/integrations/extensions"]) {
  test(`${route} filters fit with wider text at 667x375`, async ({ page }) => {
    await page.setViewportSize({ width: 667, height: 375 });
    await page.emulateMedia({ reducedMotion: "reduce" });
    await mockApi(page);
    await page.goto(route);
    const search = page.getByRole("searchbox", { name: "Search sources", exact: true });
    await expect(search).toBeVisible();
    await widenText(page.locator(".source-toolbar input, .source-toolbar .filter-segments button"));
    const box = await search.boundingBox();
    const field = await page.locator(".source-toolbar .search-field").boundingBox();
    expect(box!.x).toBeGreaterThanOrEqual(field!.x);
    expect(box!.x + box!.width).toBeLessThanOrEqual(field!.x + field!.width + 1);
    await expectContainedMobileGeometry(page);
  });
}

test("provider readiness separates provider names from account labels", async ({ page }) => {
  await mockApi(page);
  const definitions = [
    { id: "lastfm", name: "Last.fm" }, { id: "listenbrainz", name: "ListenBrainz" },
    { id: "spotify", name: "Spotify" }, { id: "deezer", name: "Deezer" },
    { id: "apple-musickit", name: "Apple Music" },
  ];
  await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: { ...schema, providers: definitions } }));
  const accountResponse = responses["/api/admin/provider-accounts"] as { accounts: Record<string, unknown>[] };
  await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: {
    ...accountResponse, accounts: definitions.map((provider, index) => ({
      ...accountResponse.accounts[0], id: `account-${provider.id}`, providerId: provider.id,
      displayName: index === 3 ? "Road trip" : `My ${provider.id}`,
      scope: index === 3 ? "Shared" : "Personal", customLabel: index === 3 ? "Road trip" : null,
    })),
  } }));
  await page.goto("#/sources/accounts");
  const rows = page.getByRole("table", { name: "Accounts", exact: true }).locator("tbody tr");
  await expect(rows.locator('td[data-label="Service"] strong')).toHaveText(definitions.map(provider => provider.name).toSorted((left, right) => left.localeCompare(right)));
  const deezer = rows.filter({ hasText: "Deezer" });
  await expect(deezer.locator('td[data-label="Account"]')).toHaveText("Road trip");
  await expect(deezer.locator('td[data-label="Shared with"]')).toHaveText("Everyone");
  await expect(rows.filter({ hasText: "Last.fm" }).locator('td[data-label="Shared with"]')).toHaveText("Just Tester");
  await expect(rows.locator('td[data-label="State"]')).toHaveText(Array(5).fill("Working"));
  if (process.env.ALLSTARR_SCREENSHOT_DIR)
    await page.getByRole("table", { name: "Accounts", exact: true }).screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/provider-readiness.png` });
});

test("account titles use provider definitions and keep custom labels in subtitles", async ({ page }) => {
  await mockApi(page);
  const accountResponse = responses["/api/admin/provider-accounts"] as { accounts: Record<string, unknown>[] };
  await page.route("**/api/admin/provider-accounts", (route) => route.fulfill({ json: {
    ...accountResponse,
    accounts: [{ ...accountResponse.accounts[0],
      displayName: "Road trip", sourceDisplayName: "Road trip · Tester", customLabel: "Road trip", scope: "Shared" }],
  } }));
  await page.goto("#/integrations/accounts");
  const row = page.getByRole("table", { name: "Accounts", exact: true }).locator("tbody tr").first();
  await expect(row.locator('td[data-label="Service"] strong')).toHaveText("Lumen Audio");
  await expect(row.locator('td[data-label="Account"]')).toHaveText("Road trip");
  await expect(row.locator('td[data-label="Shared with"]')).toHaveText("Everyone");
  await row.getByRole("link", { name: "Open Lumen Audio account", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Lumen Audio", level: 2 })).toBeVisible();
  await expect(page.getByRole("dialog", { name: "Lumen Audio", exact: true }).locator(".source-facts")).toContainText("Shared with everyone · Road trip");
  await expect(page.locator(".service-account.focused")).toContainText("Shared with everyone · Road trip");
});

test("Service settings actions keep phone touch targets", async ({ page }) => {
  await mockApi(page);
  await page.goto("#/sources/services/spotify");
  const settings = page.getByRole("region", { name: "Spotify settings and tests", exact: true });
  await page.getByText("Settings, accounts and tests", { exact: true }).click();
  await expect(settings).toBeVisible();
  const actions = settings.locator("button, a[data-slot='button']");
  expect(await actions.count()).toBeGreaterThan(0);
  for (const width of [320, 390, 650]) {
    await page.setViewportSize({ width, height: 844 });
    const heights = await actions.evaluateAll(elements => elements
      .filter(element => element.getClientRects().length > 0)
      .map(element => { const area = getComputedStyle(element, "::after"); return Math.max(element.getBoundingClientRect().height, area.content === "none" ? 0 : parseFloat(area.minHeight) || 0); }));
    expect(heights.length).toBeGreaterThan(0);
    expect(heights.every(height => height >= 44)).toBe(true);
  }
});

test("Apple accounts explain public web-token failures without asking for a developer token", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  const fixture = responses["/api/admin/provider-accounts"] as { accounts: Record<string, unknown>[] };
  await page.route("**/api/admin/provider-accounts", (route) => route.fulfill({ json: {
    listenersCanConnectOwnAccounts: true, audienceUsers: [], accounts: [{ ...fixture.accounts[0],
      providerId: "apple-musickit", displayName: "Personal Apple", sourceDisplayName: "Apple Music",
      configuration: { storefront: "us" }, configuredFields: ["musicUserToken", "storefront"],
    }],
  } }));
  await page.route("**/api/admin/providers/status", (route) => route.fulfill({ json: [{
    provider: "apple-musickit", providerAccountId: "account", capability: "playlists", accountScope: "personal",
    supported: true, enabled: true, configuration: "configured", health: "degraded", ready: false,
    canAttempt: true, canTest: true, reasonCode: "apple-web-token-unavailable",
  }] }));
  let appleCtsRequest: unknown;
  await page.route("**/api/admin/provider-diagnostics/deep-stream", async (route) => {
    appleCtsRequest = route.request().postDataJSON();
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ succeeded: true, providerId: "apple-musickit", clickToStreamMilliseconds: 42 }),
    });
  });
  await page.goto("#/integrations/accounts");
  await page.getByRole("link", { name: "Open Apple Music account" }).click();
  await expect(page.getByText("Apple's web-player token couldn't be fetched. Run this account's test again later.")).toBeVisible();
  await expect(page.getByLabel(/Developer token/i)).toHaveCount(0);
  await expectContainedMobileGeometry(page);
  await page.locator(".service-account").getByRole("button", { name: "Measure start time" }).click();
  await expect(page.getByText("Apple Music click-to-stream measured.")).toBeVisible();
  expect(appleCtsRequest).toEqual({ providerId: "apple-musickit", providerAccountId: "account", quality: 0 });
});

test("Integrations keep primary actions visible and report scoped degradation", async ({ page, context }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  let appleState = {
    state: "needs_login", ready: false, staged: true, daemon_running: true,
    wrapper_healthy: true, logged_in: false, login_state: "logged_out", api_version: "2",
  };
  await page.route("**/api/admin/apple-download/status", (route) => route.fulfill({
    contentType: "application/json", body: JSON.stringify(appleState),
  }));
  await page.route("**/api/admin/apple-download/login", (route) => {
    appleState = { ...appleState, state: "needs_login", login_state: "awaiting_2fa" };
    return route.fulfill({ contentType: "application/json", body: JSON.stringify(appleState) });
  });
  await page.route("**/api/admin/apple-download/login/2fa", (route) => {
    appleState = { ...appleState, state: "ready", ready: true, logged_in: true, login_state: "authenticated" };
    return route.fulfill({ contentType: "application/json", body: JSON.stringify(appleState) });
  });
  await page.route("**/api/admin/ui/schema", (route) => route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify({
      ...schema,
      providers: [
        ...schema.providers,
        { id: "disabled-source", name: "Disabled Source", status: "disabled", categories: ["metadata"] },
      ],
    }),
  }));
  await page.goto("#/sources");
  const lumenSource = page.locator(".source-row").filter({ hasText: "Lumen Audio" });
  await expect(lumenSource).toContainText("Not measured yet");
  await lumenSource.getByRole("link").first().click();
  const sourceDetails = page.getByRole("dialog", { name: "Lumen Audio", exact: true });
  await sourceDetails.locator(".source-more > summary").click();
  await expect(sourceDetails.getByRole("heading", { name: "Lumen Audio", level: 2 })).toBeVisible();
  await expect(sourceDetails.getByText("Access token")).toBeVisible();
  await expect(sourceDetails.getByText("Stored", { exact: true })).toBeVisible();
  await expect(sourceDetails.getByText("Region")).toBeVisible();
  await expect(sourceDetails.getByText("ca", { exact: true })).toBeVisible();
  await expect(sourceDetails.getByRole("button", { name: "Connect account", exact: true })).toBeVisible();
  await sourceDetails.getByRole("button", { name: "Edit configuration" }).click();
  const sourceEditor = page.getByRole("dialog", { name: "Configure Lumen Audio" });
  await expect(sourceEditor.getByRole("button", { name: "Region" })).toHaveText("ca");
  await expect(sourceEditor.getByLabel("Access token")).toHaveValue("");
  await page.keyboard.press("Escape");
  await page.goto("#/integrations/services");
  const disabledSource = page.locator(".source-row").filter({ hasText: "Disabled Source" });
  await page.getByRole("radio", { name: /^Off/ }).click();
  const disabledStatus = disabledSource.locator(".state-pill");
  await expect(disabledStatus).toHaveText("Off");
  await expect(page.locator(".source-row").filter({ hasText: "Apple Music" })).toHaveCount(0);
  await page.getByRole("radio", { name: /^All / }).click();
  await page.locator(".source-row").filter({ hasText: "Apple Music" }).getByRole("link").first().click();
  await expect(page.getByRole("button", { name: "Measure start time" })).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Apple Music setup" })).toBeVisible();
  await page.getByText("Advanced server settings", { exact: true }).click();
  await expect(page.getByLabel("Optional playback gateway URL")).toBeVisible();
  await page.getByRole("button", { name: "Sign in to Apple Music", exact: true }).click();
  const appleManager = page.getByRole("dialog", { name: "Sign in to Apple Music" });
  await appleManager.getByLabel("Apple ID").fill("tester@example.test");
  await appleManager.getByLabel("Password").fill("password");
  await appleManager.getByRole("button", { name: "Sign in", exact: true }).click();
  await appleManager.getByLabel("2FA code").fill("123456");
  await appleManager.getByRole("button", { name: "Submit 2FA" }).click();
  await expect(appleManager).toHaveCount(0);
  await expect(page.getByRole("status").filter({ hasText: "Signed in to Apple Music" })).toBeVisible();
  const appleChecklist = page.locator(".apple-checklist li");
  const appleProgress = await appleChecklist.evaluateAll(items => items.map(item => {
    const rect = item.getBoundingClientRect(); return { contained: item.scrollWidth <= item.clientWidth, width: rect.width, x: rect.x };
  }));
  expect(appleProgress).toHaveLength(3);
  expect(appleProgress.every(item => item.contained)).toBe(true);
  expect(Math.max(...appleProgress.map(item => item.width)) - Math.min(...appleProgress.map(item => item.width))).toBeLessThanOrEqual(1);
  expect(Math.max(...appleProgress.map(item => item.x)) - Math.min(...appleProgress.map(item => item.x))).toBeLessThanOrEqual(1);
  await expectContainedMobileGeometry(page);
  await page.goto("#/integrations/accounts");
  await page.getByRole("link", { name: "Open Lumen Audio account" }).click();
  const accountDetails = page.locator(".service-account.focused");
  await expect(accountDetails.getByRole("button", { name: "Test connection" })).toBeVisible();
  await expect(accountDetails.getByRole("button", { name: "Edit configuration" })).toBeVisible();
  const metadataCapability = accountDetails.locator(".service-check").filter({ hasText: "Metadata" });
  await expect(metadataCapability.locator('[data-slot="badge"]')).toHaveText("Ready");
  const successColor = await page.evaluate(() => {
    const sample = document.createElement("span");
    sample.style.color = "var(--color-success)";
    document.body.append(sample);
    const value = getComputedStyle(sample).color;
    sample.remove();
    return value;
  });
  await expect(metadataCapability.locator('[data-slot="badge"]')).toHaveCSS("color", successColor);
  await expect(metadataCapability.getByRole("button", { name: "Test", exact: true })).toHaveAttribute("data-slot", "button");

  const listener = await context.newPage();
  await mockApi(listener);
  await listener.route("**/api/admin/auth/me", (route) => route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify({
      authenticated: true, backend: "Jellyfin",
      user: { id: "listener", name: "Listener", isAdministrator: false },
    }),
  }));
  await listener.route("**/api/admin/provider-accounts", (route) => route.fulfill({
    status: 503,
    contentType: "application/json",
    body: JSON.stringify({ error: "Account scope unavailable" }),
  }));
  await listener.goto("#/integrations/accounts");
  await expect(listener.getByText("Source readiness may be stale.")).toBeVisible();
  await expect(listener.getByRole("button", { name: "Connect account" }).first()).toBeEnabled();
});

test("Score details open from the whole row, close again, and dim what does not count", async ({ page }) => {
  await mockApi(page);
  const parts = (speedOutOf: number, qualityOutOf: number) => ({ earned: 82, reliability: 50, reliabilityOutOf: 50, speed: speedOutOf ? 2 : 0, speedOutOf, quality: qualityOutOf, qualityOutOf, cappedAt: null, typicalMilliseconds: 7146 });
  const feature = (capability: string, value: number, speedOutOf: number, qualityOutOf: number) => ({
    capability, score: value, uptimePercent: 100, checks: 10, failures: 0, typicalLatencyMilliseconds: 7146, lastFailureCode: null, parts: parts(speedOutOf, qualityOutOf),
  });
  await page.route("**/api/admin/providers/scores", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({ qualityTarget: "CdLossless", providers: [{
      providerId: "lumen-audio", score: 82, uptimePercent: 100, checks: 20, days: [],
      quality: { typical: "Lossless 16-bit/44.1 kHz", best: null, lossless: true, samples: 5 },
      capabilities: [feature("download", 100, 0, 40), feature("streaming", 82, 20, 30)],
    }] }),
  }));
  await page.goto("#/integrations/routing");
  const row = page.locator(".routing-group li.has-score").first();
  const chip = row.locator(".score-toggle");
  await expect(row.locator(".score-chip")).toHaveText("82");
  await expect(chip).toHaveAccessibleName(/Show details/);
  await expect(chip).toHaveAttribute("aria-expanded", "false");

  await row.locator(".routing-copy").click();
  const panel = page.getByRole("dialog", { name: "Lumen Audio", exact: true });
  const details = panel.locator(".score-details");
  await expect(details).toBeVisible();
  await expect(chip).toHaveAccessibleName(/Hide details/);
  await expect(details.locator(".score-part").filter({ hasText: "Quality" })).toContainText("Meets your CD lossless setting");
  await expect(details.locator(".score-part").filter({ hasText: "Has the song" })).toContainText("Still measuring");
  await expect(details.locator(".score-row.current")).toContainText("Streaming");
  await expect(details.locator(".score-row.dimmed")).toContainText("Download");

  await panel.getByRole("button", { name: "Close", exact: true }).click();
  await expect(details).toHaveCount(0);
  await chip.click();
  await expect(details).toBeVisible();
  await panel.getByRole("button", { name: "Close", exact: true }).click();
  await expect(details).toHaveCount(0);
});

test("Sources background refresh stays quiet and manual refresh joins it", async ({ page }) => {
  await page.clock.install();
  await mockApi(page);
  await page.goto("#/integrations/services");
  const refresh = page.getByRole("button", { name: "Refresh", exact: true });
  await expect(refresh).toBeEnabled();
  const started = routeRelease();
  const finish = routeRelease();
  let reads = 0;
  await page.route("**/api/admin/ui/schema", async route => {
    reads++;
    started.release();
    await finish.promise;
    return route.fulfill({ json: schema });
  });
  await page.clock.runFor(20_500);
  await started.promise;
  await expect(refresh).toBeEnabled();
  await expect(page.getByRole("button", { name: "Refreshing…", exact: true })).toHaveCount(0);
  await refresh.click();
  await expect(page.getByRole("button", { name: "Refreshing…", exact: true })).toBeDisabled();
  expect(reads).toBe(1);
  finish.release();
  await expect(refresh).toBeEnabled();
  expect(reads).toBe(1);
});

test("Sources keeps never-connected built-ins in the available catalog", async ({ page }) => {
  await mockApi(page);
  await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
    ...schema, providers: [...schema.providers, ...["Koito", "Multi-scrobbler", "Qobuz"].map(name => ({
      id: name.toLowerCase(), name, status: "needs_config", implementationOrigin: "builtin",
      categories: ["scrobbling"], accountSettings: [{ key: "token", label: "Access token", type: "text", required: true }],
    }))],
  } }));
  await page.goto("#/integrations/services");
  const services = page.getByRole("list", { name: "Services", exact: true });
  await page.getByRole("radio", { name: /^In use/ }).click();
  await expect(page.getByRole("radio", { name: /^In use/ })).toHaveAttribute("aria-checked", "true");
  for (const name of ["Koito", "Multi-scrobbler", "Qobuz"])
    await expect(services.getByText(name, { exact: true })).toHaveCount(0);
  await page.getByRole("radio", { name: /^Needs attention/ }).click();
  for (const name of ["Koito", "Multi-scrobbler", "Qobuz"])
    await expect(services.getByText(name, { exact: true })).toHaveCount(0);
  await page.getByRole("radio", { name: /^Off/ }).click();
  for (const name of ["Koito", "Multi-scrobbler", "Qobuz"])
    await expect(services.getByText(name, { exact: true })).toHaveCount(0);
  await page.getByRole("radio", { name: /^All / }).click();
  for (const name of ["Koito", "Multi-scrobbler", "Qobuz"])
    await expect(services.getByText(name, { exact: true })).toBeVisible();
});

test("Sources ignores unrelated stream events and coalesces source checks", async ({ page }) => {
  await page.clock.install({ time: new Date("2026-01-01T00:00:00Z") });
  await page.clock.pauseAt(new Date("2026-01-01T00:00:01Z"));
  await page.addInitScript(() => {
    class FixtureStream {
      static instance: FixtureStream;
      listeners = new Map<string, EventListener[]>();
      constructor() { FixtureStream.instance = this; }
      addEventListener(type: string, listener: EventListener) {
        this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
      }
      close() {}
    }
    Object.defineProperty(window, "EventSource", { value: FixtureStream });
    let batch = 0;
    window.__emitAllstarrUpdate = () => {
      const updates = batch++ === 0
        ? [{ resource: "job", resourceId: "play", data: { type: "playback.signal" } }, { resource: "track-match", resourceId: "match" }]
        : [{ resource: "audit", resourceId: "check-1", data: { category: "provider-check" } }, { resource: "audit", resourceId: "check-2", data: { category: "provider-status" } }];
      for (const [index, update] of updates.entries())
        for (const listener of FixtureStream.instance.listeners.get("update") ?? [])
          listener(new MessageEvent("update", { data: JSON.stringify(update), lastEventId: `${batch}-${index}` }));
    };
  });
  await mockApi(page);
  let reads = 0;
  await page.route("**/api/admin/ui/schema", route => {
    reads++;
    return route.fulfill({ json: schema });
  });
  await page.goto("#/integrations/services");
  const refresh = page.getByRole("button", { name: "Refresh", exact: true });
  await expect(refresh).toBeEnabled();
  expect(reads).toBe(1);
  await page.evaluate(() => window.__emitAllstarrUpdate?.());
  await page.clock.runFor(250);
  expect(reads).toBe(1);
  await page.evaluate(() => window.__emitAllstarrUpdate?.());
  await page.clock.runFor(249);
  expect(reads).toBe(1);
  await page.clock.runFor(1);
  await expect.poll(() => reads).toBe(2);
  await expect(refresh).toBeEnabled();
});

for (const width of [390, 1280]) {
  for (const theme of ["light", "dark"] as const) {
    test(`Sources and playback stay readable at ${width}px in ${theme}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
      await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
      await mockApi(page);
      await page.goto("#/integrations/services");
      await expect(page.getByRole("radio", { name: /^In use/ })).toBeVisible();
      await page.getByRole("radio", { name: /^All / }).click();
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
      if (width === 390) await expectContainedMobileGeometry(page);
      await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR ?? "test-results"}/sources-quiet-${width}-${theme}.png`, fullPage: true });
      await page.goto("#/");
      const playing = page.getByRole("region", { name: "Now playing" });
      await expect(playing).toBeVisible();
      const bar = playing.locator(".playback-bar > span").first();
      await expect(bar).toHaveCSS("transition-property", "none");
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
      if (width === 390) await expectContainedMobileGeometry(page);
      await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR ?? "test-results"}/playback-smooth-${width}-${theme}.png`, fullPage: true });
      await page.emulateMedia({ reducedMotion: "no-preference" });
      await expect(bar).toHaveCSS("transition-property", "width");
      await expect(bar).toHaveCSS("transition-duration", "0.2s");
    });
  }
}

test("Source deep links are consumed once and account actions expose their pending state", async ({ page }) => {
  await mockApi(page);
  await page.goto("#/integrations/services?source=lumen-audio");
  const sourceDetails = page.getByRole("heading", { name: "Lumen Audio", level: 2 });
  await expect(sourceDetails).toBeVisible();
  await page.getByRole("dialog", { name: "Lumen Audio", exact: true }).getByRole("button", { name: "Close", exact: true }).click();
  await expect(page).toHaveURL(/#\/sources\/services$/);
  await page.getByRole("button", { name: "Refresh", exact: true }).click();
  await expect(page.getByRole("button", { name: "Refresh", exact: true })).toBeEnabled();
  await expect(sourceDetails).toBeHidden();

  const toggleAccount = routeRelease();
  const account = (responses["/api/admin/provider-accounts"] as { accounts: Array<Record<string, unknown>> }).accounts[0];
  await page.route("**/api/admin/provider-accounts/account", async (route) => {
    if (route.request().method() !== "PATCH") return route.fallback();
    await toggleAccount.promise;
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ ...account, enabled: false, revision: 2 }),
    });
  });
  await page.goto("#/integrations/accounts");
  const actions = page.getByRole("button", { name: "Actions for Lumen account" });
  await actions.click();
  await page.getByRole("menuitem", { name: /^(Disable|Pause)$/ }).click();
  await expect(actions).toBeDisabled();
  toggleAccount.release();
  await expect(actions).toBeEnabled();
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Apple setup checklist and owner at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    await page.route("**/api/admin/auth/me", route => route.fulfill({ json: {
      authenticated: true, backend: "Jellyfin", user: { id: "backend-admin", allstarrUserId: "admin-owner", name: "Josh", isAdministrator: true },
    } }));
    const base = (responses["/api/admin/provider-accounts"] as { accounts: Record<string, unknown>[] }).accounts[0];
    let connected = false;
    let staged = false;
    let needsAttention = false;
    let savedOwner: string | undefined;
    let state = "logged_out";
    await page.route("**/api/admin/apple-download/status", route => route.fulfill({ json: {
      staged, daemon_running: true, wrapper_healthy: true, logged_in: state === "authenticated", login_state: state,
      account: { state, maskedIdentifier: "j***@example.test" }, api_version: "2",
    } }));
    await page.route("**/api/admin/config", route => route.fulfill({ json: {
      ...(responses["/api/admin/config"] as object), appleDownload: { baseUrl: "http://bundled-gateway.test", endpointManagedByDeployment: true },
    } }));
    await page.route("**/api/admin/providers/test/apple-musickit?**", route => route.fulfill({ json: { success: true, healthy: true } }));
    await page.route("**/api/admin/provider-accounts", route => {
      if (route.request().method() === "POST") {
        savedOwner = route.request().postDataJSON().ownerUserId;
        return route.fulfill({ json: { ...base, providerId: "apple-musickit", ownerUserId: savedOwner } });
      }
      return route.fulfill({ json: {
      audienceUsers: [{ id: "other-owner", displayName: "Allstarr" }, { id: "admin-owner", displayName: "Josh" }],
      accounts: connected ? [{ ...base, providerId: "apple-musickit", ownerUserId: "other-owner", ownerDisplayName: "Allstarr",
        creatorDisplayName: "Josh", createdByUserId: "admin-owner", configuration: { storefront: "us" }, displayName: "Apple account", secret: { configured: !needsAttention, revoked: needsAttention } }] : [],
    } });
    });
    await page.goto("#/integrations/services/apple-musickit");
    const checklist = page.getByRole("region", { name: "Apple Music setup" });
    await expect(checklist.getByText("Not connected", { exact: true })).toBeVisible();
    await expect(checklist.getByText("Not set up", { exact: true })).toBeVisible();
    await expect(checklist.getByText("Use the same Apple account for steps 1 and 2.")).toBeVisible();
    await page.getByRole("button", { name: "Connect account", exact: true }).click();
    const connect = page.locator(".connect-source-dialog");
    await expect(connect.getByRole("button", { name: "Account owner" })).toHaveText("Josh (you)");
    await connect.getByRole("button", { name: "Account owner" }).click();
    await waitForSettledOptions(page.getByRole("listbox"), "Josh (you)");
    await page.getByRole("option", { name: "Allstarr", exact: true }).click();
    await expect(connect.getByRole("button", { name: "Account owner" })).toHaveText("Allstarr");
    await expect(connect.getByText("Only that user's listening will use this account.")).toBeVisible();
    await connect.getByRole("button", { name: "Account owner" }).click();
    await waitForSettledOptions(page.getByRole("listbox"), "Josh (you)");
    await page.getByRole("option", { name: "Josh (you)", exact: true }).click();
    await expect(connect.getByRole("button", { name: "Account owner" })).toHaveText("Josh (you)");
    await connect.getByLabel("Media user token").fill("fixture-account-token");
    await connect.getByRole("button", { name: /^(Connect|Save changes)$/ }).click();
    await expect(connect).toHaveCount(0);
    expect(savedOwner).toBe("admin-owner");
    await expect(page.getByRole("button", { name: "Connect account", exact: true })).toBeFocused();
    connected = true; staged = true;
    for (const [loginState, label] of [["logged_out", "Signed out"], ["awaiting_2fa", "Waiting for 2FA code"], ["authenticated", "Signed in"], ["account_mismatch", "Use the same Apple account"]]) {
      state = loginState; await page.reload();
      await expect(checklist).toContainText(label);
      await expect(checklist).toContainText("Connected (us)");
    }
    needsAttention = true; state = "authenticated"; await page.reload();
    await expect(checklist.getByText("Needs attention", { exact: true })).toBeVisible();
    await expect(checklist).toContainText("j***@example.test");
    await page.getByText("Advanced server settings", { exact: true }).click();
    await expect(page.locator(".source-configuration-form output")).toHaveText("http://bundled-gateway.test");
    await expect(page.locator('input[name="APPLE_DOWNLOAD_URL"]')).toHaveCount(0);
    await expect(page.getByText("Configured by deployment.", { exact: true })).toBeVisible();
    await expect(page.locator(".service-account-title")).toContainText("Personal · belongs to Allstarr (added by Josh)");
    await expectContainedMobileGeometry(page);
    const gaps = await page.locator(".service-page").evaluate(element => {
      const cards = [...element.children].filter(item => item.classList.contains("settings-card"));
      return cards.slice(1).map((card, index) => card.getBoundingClientRect().top - cards[index].getBoundingClientRect().bottom);
    });
    expect(gaps.length).toBeGreaterThan(0);
    expect(gaps.every(gap => Math.abs(gap - 12) <= 1)).toBe(true);
    await page.screenshot({ path: `/tmp/allstarr-apple-checklist-${width}-${theme}.png`, fullPage: true });
  });

  test(`Apple sign-in waits for setup and closes with toast at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    let release!: () => void;
    const delayed = { promise: new Promise<void>(resolve => { release = resolve; }), resolve: () => release() };
    let signedIn = false;
    await page.route("**/api/admin/apple-download/status", async route => {
      await delayed.promise;
      return route.fulfill({ json: { staged: true, daemon_running: true, wrapper_healthy: true,
        logged_in: signedIn, login_state: signedIn ? "authenticated" : "logged_out" } });
    });
    await page.route("**/api/admin/apple-download/login", route => {
      signedIn = true; return route.fulfill({ json: { logged_in: true, login_state: "authenticated" } });
    });
    await page.goto("#/integrations/services/apple-musickit");
    await page.getByRole("button", { name: "Sign in to Apple Music", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Sign in to Apple Music" });
    await expect(dialog.getByText("Checking setup…", { exact: true })).toBeVisible();
    await expect(dialog.getByRole("button", { name: "Upload package" })).toHaveCount(0);
    delayed.resolve();
    await dialog.getByLabel("Apple ID", { exact: true }).fill("fixture@example.test");
    await dialog.getByLabel("Password", { exact: true }).fill("fixture-password");
    await expect(dialog.locator("details")).not.toHaveAttribute("open", "");
    await dialog.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(dialog).toHaveCount(0);
    await expect(page.getByRole("status").filter({ hasText: "Signed in to Apple Music" })).toBeVisible();
    await expect(page.getByRole("region", { name: "Apple Music setup" })).toContainText("Signed in");
    await expect(page.getByRole("button", { name: "Manage Apple Music sign-in", exact: true })).toBeFocused();
    await expectContainedMobileGeometry(page);
  });

  test(`Shared toasts stack, pause and stay above navigation at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await page.emulateMedia({ reducedMotion: "reduce" }); await mockApi(page);
    let measurements = 0;
    await page.route("**/api/admin/provider-diagnostics/deep-stream", route => {
      measurements++;
      return route.fulfill({ json: {
        succeeded: true, providerId: "lumen-audio", clickToStreamMilliseconds: 42,
      } });
    });
    await page.clock.install({ time: new Date("2026-01-01T00:00:00Z") });
    await page.goto("#/integrations/services/lumen-audio");
    await page.locator(".source-more > summary").click();
    const action = page.locator(".service-account").getByRole("button", { name: "Measure start time", exact: true });
    const region = page.getByRole("region", { name: "Notifications" });
    const panel = page.getByRole("dialog", { name: "Lumen Audio", exact: true });
    await expect(action).toBeEnabled();
    await action.evaluate(element => element.scrollIntoView({ block: "start" }));
    await page.clock.pauseAt(new Date("2026-01-01T01:00:00Z"));
    for (let index = 0; index < 4; index++) {
      await action.click(); await expect(action).toBeEnabled();
      await expect(region.locator(".toast")).toHaveCount(Math.min(index + 1, 3));
    }
    const item = region.locator(".toast").last();
    await expect(item).toHaveAttribute("role", "status"); await expect(item).toHaveAttribute("aria-live", "polite");
    await expect(item).toHaveCSS("animation-duration", "0s");
    const box = await region.boundingBox(); expect(box).not.toBeNull();
    await expect(panel).toHaveCSS("overflow", "clip");
    expect(await panel.evaluate(element => element.scrollTop)).toBe(0);
    expect(await item.getByRole("button", { name: "Dismiss notification" }).evaluate(element => {
      const box = element.getBoundingClientRect();
      return element.contains(document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2));
    })).toBe(true);
    if (width === 390) {
      const nav = await page.locator(".mobile-navigation").boundingBox();
      expect(box!.x).toBeCloseTo(16, 0); expect(box!.width).toBeCloseTo(width - 32, 0);
      expect(box!.y + box!.height).toBeLessThanOrEqual(nav!.y - 8);
    } else {
      expect(width - box!.x - box!.width).toBeCloseTo(24, 0);
    }
    const footer = await page.locator(".side-panel .panel-footer").boundingBox();
    expect(footer!.y + footer!.height).toBeCloseTo(844, 0);
    expect(box!.y + box!.height).toBeLessThanOrEqual(footer!.y - 8);
    const body = await page.locator(".side-panel .panel-body").boundingBox();
    expect(body!.y + body!.height).toBeLessThanOrEqual(box!.y - 8);
    await item.hover(); await page.clock.runFor(6000); await expect(item).toBeVisible();
    const dismiss = item.getByRole("button", { name: "Dismiss notification" });
    await dismiss.focus(); await expect(dismiss).toBeFocused(); await page.mouse.move(0, 0);
    await page.clock.runFor(6000); await expect(item).toBeVisible();
    await expect(dismiss).toBeFocused();
    await expect(region.locator(".toast")).toHaveCount(1);
    await page.screenshot({ path: `/tmp/allstarr-toasts-${width}-${theme}.png` });
    await page.keyboard.press("Enter"); await expect(region.locator(".toast")).toHaveCount(0);
    expect(measurements).toBe(4);
    await expect(panel).toBeVisible();
  });
}

test("Source selection and entered credentials survive a readiness refresh", async ({ page }) => {
  await mockApi(page);
  let connectedProvider = "lumen-audio";
  const fixture = responses["/api/admin/provider-accounts"] as { accounts: Record<string, unknown>[] };
  await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: {
    ...fixture, accounts: [{ ...fixture.accounts[0], providerId: connectedProvider }],
  } }));
  await page.addInitScript(() => {
    let revision = 1;
    const listeners = new Map<string, ((event: MessageEvent) => void)[]>();
    class FixtureEventSource {
      addEventListener(name: string, listener: (event: MessageEvent) => void) { listeners.set(name, [...(listeners.get(name) ?? []), listener]); }
      removeEventListener() {} close() {}
    }
    Object.defineProperty(window, "EventSource", { value: FixtureEventSource });
    window.__emitAllstarrUpdate = () => {
      for (const listener of listeners.get("update") ?? []) listener(new MessageEvent("update", { data: JSON.stringify({ resource: "provider-accounts", revision: ++revision }) }));
    };
  });
  await page.goto("#/integrations/accounts");
  await page.getByRole("button", { name: "Connect account" }).click();
  const dialog = page.locator(".connect-source-dialog");
  await expect(dialog.locator(".provider-monogram")).toHaveCount(0);
  const optionsBefore = await dialog.getByRole("radio").allTextContents();
  connectedProvider = "listenbrainz";
  const firstRefresh = page.waitForResponse(response => response.url().endsWith("/api/admin/ui/schema"));
  await page.evaluate(() => window.__emitAllstarrUpdate?.());
  await firstRefresh;
  await expect(dialog.getByRole("radio")).toHaveText(optionsBefore);
  await dialog.getByRole("radio", { name: /ListenBrainz/ }).click();
  await dialog.getByRole("button", { name: "Next", exact: true }).click();
  await dialog.getByLabel("ListenBrainz token").fill("fixture-token");
  const refreshed = page.waitForResponse(response => response.url().endsWith("/api/admin/ui/schema"));
  await page.evaluate(() => window.__emitAllstarrUpdate?.());
  await refreshed;
  await expect(dialog.getByText("ListenBrainz", { exact: true })).toBeVisible();
  await expect(dialog.getByLabel("ListenBrainz token")).toHaveValue("fixture-token");
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Household account audiences at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    const fixture = responses["/api/admin/provider-accounts"] as { accounts: Record<string, unknown>[]; audienceUsers: object[] };
    let account: Record<string, unknown> & { supportedAudiences: string[]; sharingDescription: string } = { ...fixture.accounts[0], supportedAudiences: ["Personal", "Shared"], sharingDescription: "Everyone in the household can play songs from this account." };
    const writes: Record<string, unknown>[] = [];
    await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: { ...fixture, accounts: [account] } }));
    await page.route("**/api/admin/provider-accounts/account/audience", route => {
      const input = route.request().postDataJSON();
      writes.push(input);
      account = { ...account, ...input, revision: Number(account.revision ?? 1) + 1 };
      return route.fulfill({ json: account });
    });
    await page.goto("#/integrations/accounts");
    await expect(page.getByRole("button", { name: "Actions for Lumen account", exact: true })).toBeVisible();
    for (const providerId of ["apple-musickit", "spotify", "deezer", "qobuz", "musixmatch", "lastfm", "listenbrainz", "multi-scrobbler", "koito", "spotiflac-apple-music"]) {
      const scrobbling = ["lastfm", "listenbrainz", "multi-scrobbler", "koito"].includes(providerId);
      account = { ...account, providerId, scope: "Personal", ownerUserId: "user",
        sharingDescription: scrobbling ? "Plays from listeners without their own account are sent here."
          : "Everyone in the household can play songs from this account." };
      await page.reload();
      await page.getByRole("button", { name: "Actions for Lumen account", exact: true }).click();
      await page.getByRole("menuitem", { name: "Edit access", exact: true }).click();
      const dialog = page.locator(".access-dialog");
      await expect(dialog.getByRole("radio", { name: /^Personal/ })).toBeVisible();
      await expect(dialog.getByRole("radio", { name: /^Shared/ })).toBeVisible();
      await expect(dialog).toContainText(account.sharingDescription);
      if (providerId === "apple-musickit" || providerId === "lastfm") {
        await dialog.getByRole("radio", { name: "Shared", exact: true }).focus();
        await page.screenshot({ path: `test-results/household-${providerId}-${width}-${theme}.png`, fullPage: true });
      }
      await dialog.getByRole("radio", { name: /^Shared/ }).check();
      await dialog.getByRole("button", { name: "Save access", exact: true }).click();
      await page.getByRole("button", { name: "Share with household", exact: true }).click();
      await expect(dialog).toBeHidden();
      expect(writes.at(-1)).toMatchObject({ scope: "Shared", ownerUserId: null });
    }
    account = { ...account, supportedAudiences: ["Personal"] };
    await page.reload();
    await page.getByRole("button", { name: "Actions for Lumen account", exact: true }).click();
    await page.getByRole("menuitem", { name: "Edit access", exact: true }).click();
    await expect(page.locator(".access-dialog").getByRole("radio", { name: /^Shared/ })).toHaveCount(0);
    await expect(page.locator(".access-dialog").getByRole("radio", { name: /^Personal/ })).toBeVisible();
    await page.screenshot({ path: `test-results/account-audience-${width}-${theme}.png`, fullPage: true });
  });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Signed connections keep their audience and selected sign-in at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    const mock = await mockSignedSessionExtension(page, { state: "signed_in", accountId: "account" });
    await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: { ...schema,
      providers: [...schema.providers, { id: "signed-audio", name: "Signed Audio", categories: ["streaming"], accountSettings: [] }],
    } }));
    const fixture = responses["/api/admin/provider-accounts"] as { accounts: Record<string, unknown>[]; audienceUsers: object[] };
    let accounts: Record<string, unknown>[] = [{ ...fixture.accounts[0], providerId: "signed-audio", displayName: "Signed Audio",
      scope: "Shared", ownerUserId: null, supportedAudiences: ["Personal", "Shared"] }];
    const audienceWrites: Record<string, unknown>[] = [];
    await page.route("**/api/admin/provider-accounts", route => {
      if (route.request().method() === "POST") {
        const input = route.request().postDataJSON();
        accounts.push({ ...accounts[0], ...input, id: "personal-connection", scope: "Personal", ownerUserId: "user" });
        return route.fulfill({ json: accounts.at(-1) });
      }
      return route.fulfill({ json: { ...fixture, accounts } });
    });
    await page.route("**/api/admin/provider-accounts/account/audience", route => {
      const input = route.request().postDataJSON();
      audienceWrites.push(input);
      accounts[0] = { ...accounts[0], ...input, revision: Number(accounts[0].revision ?? 1) + 1 };
      return route.fulfill({ json: accounts[0] });
    });
    await page.route("**/api/admin/extensions/packages/signed/session?**", route => route.fulfill({ json: {
      state: new URL(route.request().url()).searchParams.get("accountId") === "account" ? "signed_in" : "signed_out",
      accountId: new URL(route.request().url()).searchParams.get("accountId"),
    } }));
    await page.goto("#/integrations/services/signed-audio");
    await openSourceSettings(page, "Signed Audio");
    const panel = page.locator(".extension-session");
    const audience = panel.locator(".extension-session-connection");
    await expect(audience).toContainText("Who can use this sign-in");
    await expect(audience).toContainText("Everyone in the household");
    await expect(panel.getByRole("button", { name: "Signed connection", exact: true })).toHaveCount(0);
    await expect(panel.getByRole("button", { name: /Add Personal connection/ })).toHaveCount(0);
    await audience.getByRole("button", { name: "Change", exact: true }).click();
    const dialog = page.locator(".access-dialog");
    await dialog.getByRole("radio", { name: "Personal", exact: true }).check();
    await dialog.getByRole("button", { name: "Save access", exact: true }).click();
    await expect(dialog).toBeHidden();
    expect(audienceWrites.at(-1)).toMatchObject({ scope: "Personal", ownerUserId: "user" });
    await expect(audience).toContainText("Only Tester");
    await expect(panel.getByRole("button", { name: "Sign out", exact: true })).toBeVisible();
    await audience.getByRole("button", { name: "Change", exact: true }).click();
    await dialog.getByRole("radio", { name: "Shared", exact: true }).check();
    await dialog.getByRole("button", { name: "Save access", exact: true }).click();
    await page.getByRole("button", { name: "Share with household", exact: true }).click();
    await expect(dialog).toBeHidden();
    await expect(audience).toContainText("Everyone in the household");
    expect(await panel.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(true);
    await page.screenshot({ path: `test-results/signed-audience-${width}-${theme}.png`, fullPage: true });
  });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`A source waiting for sign-in says so and leads to Reconnect at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    await mockSignedSessionExtension(page, { state: "expired", accountId: "account", expiresAt: "2026-10-12T18:22:29Z" });
    await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: { ...schema,
      providers: [...schema.providers, { id: "signed-audio", name: "Signed Audio", categories: ["streaming"], accountSettings: [], usesSignedSession: true, implementationOrigin: "extension", originName: "SpotiFLAC" }],
    } }));
    const fixture = responses["/api/admin/provider-accounts"] as { accounts: Record<string, unknown>[]; audienceUsers: object[] };
    await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: { ...fixture, accounts: [{ ...fixture.accounts[0],
      providerId: "signed-audio", displayName: "Signed Audio", scope: "Shared", ownerUserId: null, supportedAudiences: ["Personal", "Shared"],
      secret: { configured: false, revoked: false } }] } }));
    await page.route("**/api/admin/providers/status", route => route.fulfill({ json: ["metadata", "streaming"].map(capability => ({
      provider: "signed-audio", providerAccountId: "account", providerAccountName: "Signed Audio", capability, accountScope: "shared",
      supported: true, enabled: true, configuration: "configured", health: "degraded", ready: false, canAttempt: true, canTest: true,
      reasonCode: "sign_in_needed", testedAt: "2026-10-16T18:57:05Z",
    })) }));
    await page.goto("#/integrations/services/signed-audio");
    const panel = page.getByRole("dialog", { name: "Signed Audio", exact: true });
    await expect(panel.locator(".source-summary .state-pill")).toHaveText("Needs sign-in");
    await expect(panel.locator(".panel-title")).toContainText("Extension");
    await expect(page.getByText("Degraded", { exact: true })).toHaveCount(0);
    await expect(page.getByText(/Setup needed/)).toHaveCount(0);
    const check = page.locator(".service-check").filter({ hasText: "Streaming" }).first();
    await expect(check).toContainText("Needs sign-in");
    // The account says it once, in its banner, instead of on every check.
    await expect(check).not.toContainText("Sign in again");
    const banner = page.locator(".account-sign-in");
    await expect(banner).toContainText("Sign in to Signed Audio again");
    await expect(banner).toContainText("Metadata and Streaming need it.");
    await page.screenshot({ path: `test-results/needs-verification-${width}-${theme}.png`, fullPage: true });
    await banner.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(page.locator(".extension-session").getByRole("button", { name: "Reconnect", exact: true })).toBeFocused();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`An account without its sign-in shows one banner and compact checks at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: { ...schema,
      providers: [...schema.providers.filter((item: { id: string }) => item.id !== "deezer"), { id: "deezer", name: "Deezer", categories: ["metadata", "streaming", "downloads", "playlists"],
        accountSettings: [{ key: "arl", label: "ARL cookie", type: "password", sensitive: true, required: true }] }],
    } }));
    const fixture = responses["/api/admin/provider-accounts"] as { accounts: Record<string, unknown>[]; audienceUsers: object[] };
    await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: { ...fixture, accounts: [{ ...fixture.accounts[0],
      id: "deezer-account", providerId: "deezer", displayName: "Deezer", scope: "Shared", ownerUserId: null, supportedAudiences: ["Personal", "Shared"],
      enabled: true, canManage: true, secret: { configured: false, revoked: false } }] } }));
    await page.route("**/api/admin/providers/status", route => route.fulfill({ json: [
      ["metadata", true, null], ["streaming", false, "Forbidden"], ["downloads", false, "sign_in_needed"], ["playlists", false, "sign_in_needed"],
    ].map(([capability, ready, reasonCode]) => ({
      provider: "deezer", providerAccountId: "deezer-account", providerAccountName: "Deezer", capability, accountScope: "shared",
      supported: true, enabled: true, configuration: "configured", health: ready ? "healthy" : "degraded", ready, canAttempt: true, canTest: true,
      reasonCode, testedAt: "2026-11-12T10:00:00Z",
    })) }));
    await page.goto("#/integrations/services/deezer");
    await page.getByText("Settings, accounts and tests", { exact: true }).click();
    const account = page.locator(".service-account").first();
    const banner = account.locator(".account-sign-in");
    await expect(banner).toContainText("Deezer isn't signed in");
    await expect(banner).toContainText("Downloads and Playlists need it.");
    await expect(account).not.toContainText("Sign-in section");
    await page.screenshot({ path: `test-results/account-sign-in-${width}-${theme}.png`, fullPage: true });
    await account.screenshot({ path: `test-results/account-card-${width}-${theme}.png` });
    for (const name of ["Downloads", "Playlists"]) {
      const row = account.locator(".service-check").filter({ hasText: name });
      await expect(row).toContainText("Needs sign-in");
      // Name and state share one line.
      const [label, state] = [await row.locator(".service-check-name").boundingBox(), await row.locator(".service-check-status").boundingBox()];
      expect(Math.abs(label!.y + label!.height / 2 - (state!.y + state!.height / 2))).toBeLessThan(6);
    }
    for (const button of await account.locator('[data-slot="button"]').all())
      expect((await button.boundingBox())!.height, await button.innerText()).toBeLessThanOrEqual(40);
    // Banner, then checks, then the account's actions.
    const actions = account.locator(".service-account-actions [data-slot=button]").first();
    await expect(actions).toBeVisible();
    expect((await banner.boundingBox())!.y).toBeLessThan((await account.locator(".service-checks").boundingBox())!.y);
    expect((await account.locator(".service-checks").boundingBox())!.y).toBeLessThan((await actions.boundingBox())!.y);
    await banner.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(page.getByRole("dialog").filter({ hasText: "ARL cookie" }).last()).toBeVisible();
  });
}

test("Provider marks display a PNG color master when SVG is unavailable", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
    ...schema, providers: [...schema.providers, { id: "lastfm", name: "Last.fm", categories: ["scrobbling"] }],
  } }));
  await page.goto("#/integrations/services/lastfm");
  const mark = page.getByRole("dialog", { name: "Last.fm", exact: true }).locator(".panel-artwork .provider-mark img");
  await expect(mark).toHaveAttribute("src", /\/brands\/lastfm\/color\.png$/);
  await expect(mark).toBeVisible();
  expect(await mark.evaluate(image => (image as HTMLImageElement).naturalWidth)).toBeGreaterThan(0);
});

for (const [width, colorScheme] of [[390, "light"], [390, "dark"], [1280, "light"], [1280, "dark"]] as const) {
  test(`Source use switch sets the household and personal positions at ${width}px ${colorScheme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
    await mockApi(page);
    await page.route("**/api/admin/ui/schema", (route) => route.fulfill({ json: {
      ...schema, providers: [...schema.providers, { id: "artistgrid", name: "ArtistGrid", icon: "extension", useMode: "manualOnly",
        categories: ["metadata", "streaming"], runtimeCapabilities: [] }],
    } }));
    const writes: Record<string, unknown>[] = [];
    const settings = (householdMode: string, personalMode: string | null) => ({
      personalRevision: "personal-1", householdRevision: "household-1",
      providers: [{ providerId: "artistgrid", householdMode, personalMode, effectiveMode: householdMode === "off" ? "off" : personalMode ?? householdMode, canChoosePersonal: householdMode !== "off" }],
    });
    await page.route("**/api/admin/providers/use**", (route) => {
      if (route.request().method() === "PUT") writes.push(route.request().postDataJSON());
      return route.fulfill({ json: writes.length === 0 ? settings("manualOnly", "automatic") : writes.length === 1 ? settings("manualOnly", null) : settings("off", null) });
    });
    const markRequests: string[] = [];
    await page.route(/\/images\/providers\/brands\/artistgrid\/color\.(?:svg|png)$/, route => {
      markRequests.push("color");
      return route.fallback();
    });
    await page.route("**/images/providers/artistgrid.svg", (route) => {
      markRequests.push("legacy");
      return route.fulfill({ status: 404 });
    });
    await page.goto("#/integrations/services/artistgrid");
    await expect(page.getByRole("heading", { name: "ArtistGrid", level: 2 })).toBeVisible();
    const use = page.getByRole("region", { name: "Use this source", exact: true });
    const everyone = use.getByRole("radiogroup", { name: "Use this source for everyone" });
    const mine = use.getByRole("radiogroup", { name: "Use this source for you" });
    await expect(everyone.getByRole("radio", { name: "Manual only", exact: true })).toHaveAttribute("aria-checked", "true");
    await expect(mine.getByRole("radio", { name: "On", exact: true })).toHaveAttribute("aria-checked", "true");
    await expect(use).toContainText("Your own setting.");
    const mark = page.getByRole("dialog", { name: "ArtistGrid", exact: true }).locator(".panel-artwork .provider-mark img");
    await expect(mark).toHaveAttribute("src", /\/brands\/artistgrid\/color\.svg$/);
    await expect(mark).toBeVisible();
    await expect.poll(() => mark.evaluate(image => (image as HTMLImageElement).naturalWidth)).toBeGreaterThan(0);
    expect(markRequests).toContain("color");
    expect(markRequests).not.toContain("legacy");
    expect(await use.getByRole("radio").evaluateAll(controls => controls.every(control => {
      const bounds = control.getBoundingClientRect();
      return bounds.width >= 44 && bounds.height >= 44;
    }))).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: `test-results/source-use-${width}-${colorScheme}.png`, fullPage: false });
    await mine.getByRole("radio", { name: "Follow", exact: true }).click();
    await expect.poll(() => writes.length).toBe(1);
    expect(writes[0]).toEqual({ mode: null, scope: "personal", expectedRevision: "personal-1" });
    await expect(use).toContainText("Follows the setting for everyone.");
    await everyone.getByRole("radio", { name: "Manual only", exact: true }).focus();
    await page.keyboard.press("ArrowRight");
    await expect.poll(() => writes.length).toBe(2);
    expect(writes[1]).toEqual({ mode: "off", scope: "household", expectedRevision: "household-1" });
    await expect(use).toContainText("An administrator turned this source off for everyone.");
    await expect(mine.getByRole("radio", { name: "Off", exact: true })).toBeDisabled();
  });
}
