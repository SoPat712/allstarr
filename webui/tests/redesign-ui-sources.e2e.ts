import { expect, test, type Page } from "@playwright/test";
import { mkdir, readFile } from "node:fs/promises";

const providers = [
  { id: "qobuz", name: "Qobuz", implementationOrigin: "extension", categories: ["streaming"], runtimeCapabilities: [{ id: "streaming", health: "degraded", ready: false, canTest: true, reasonCode: "service_unavailable", testedAt: "2026-11-03T12:00:00Z" }] },
  { id: "apple-musickit", name: "Apple Music", categories: ["streaming", "playlists"], accountSettings: [{ key: "token", label: "Media User Token", type: "text", sensitive: true, required: true }], runtimeCapabilities: [{ id: "streaming", health: "healthy", ready: true, canTest: true }] },
  { id: "listenbrainz", name: "ListenBrainz", categories: ["scrobbling"], accountSettings: [{ key: "token", label: "Listening service token", type: "text", sensitive: true, required: true }] },
];

async function fixture(page: Page) {
  let granted = true, revision = 1;
  const useModes = new Map(providers.map(provider => [provider.id, "automatic"]));
  let accounts = ["apple-musickit", "listenbrainz"].map((providerId, i) => ({ id: `account-${i}`, providerId, displayName: "Listener", ownerDisplayName: "Listener", ownerUserId: "listener", scope: i ? "Personal" : "Shared", canManage: true, canChangeAudience: true, revision: 1, enabled: true, secret: { configured: true, revoked: false }, createdAt: "2026-10-28T12:00:00Z", configuredFields: ["token"] }));
  let listens = ["First song", "Second song"].map((title, index) => ({ id: `listen-${index}`, revision: 1, targetId: "listenbrainz", state: "permanentfailure", kind: "completed", canResend: true, canConfirm: true, canDismiss: false, providerCode: "destination-unbound", suggestedDestinationAccountId: "account-1", availableDestinationAccountIds: ["account-1"], requiresReauthentication: false, sendCount: 0, safeMessage: "The destination needs confirming", updatedAt: "2026-11-03T12:00:00Z", submission: { title, artist: "An artist", occurredAt: "2026-11-03T12:00:00Z" } }));
  const writes: { path: string; body: Record<string, unknown> }[] = [];
  await page.route("**/*", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (!["localhost", "127.0.0.1"].includes(url.hostname)) return route.abort();
    if (path.startsWith("/images/") || path === "/favicon.svg") {
      const asset = await readFile(new URL(`../../allstarr/wwwroot${path}`, import.meta.url)).catch(() => null);
      return asset ? route.fulfill({ body: asset, contentType: path.endsWith(".png") ? "image/png" : "image/svg+xml" }) : route.fulfill({ status: 404 });
    }
    if (path.startsWith("/fonts/")) return route.fulfill({ status: 204 });
    if (!path.startsWith("/api/")) return route.continue();
    let body: unknown = {};
    if (request.method() !== "GET") writes.push({ path, body: request.postData() ? request.postDataJSON() : {} });
    if (path.endsWith("/auth/me")) body = { authenticated: true, backend: "Jellyfin", user: { id: "listener", allstarrUserId: "listener", name: "Listener", isAdministrator: true } };
    if (path.endsWith("/onboarding/status")) body = { completed: true, shouldRedirectToSetup: false, setupOpen: false, completedSteps: [], recoveryNotices: [], migration: {} };
    if (path.endsWith("/ui/schema")) body = { activeBackend: "Jellyfin", providers };
    if (path === "/api/admin/provider-accounts") {
      if (request.method() === "POST") { const input = request.postDataJSON(); const account = { ...accounts[0], ...input, id: "new-account" }; accounts.push(account); body = account; }
      else body = { accounts, audienceUsers: [{ id: "listener", displayName: "Listener" }] };
    }
    if (path.startsWith("/api/admin/provider-accounts/account-") && request.method() === "PATCH") {
      const account = accounts.find(account => path.endsWith(account.id))!;
      account.enabled = request.postDataJSON().enabled; account.revision++; body = account;
    }
    if (path === "/api/admin/providers/status") body = [{ provider: "qobuz", capability: "streaming", ready: false, health: "degraded", canTest: true, canAttempt: false, supported: true, enabled: true, reasonCode: "service_unavailable", testedAt: "2026-11-03T12:00:00Z" }];
    if (path === "/api/admin/ui/provider-summaries") body = { providers: [] };
    if (path.endsWith("/scores")) body = { providers: [] };
    if (path.endsWith("/latest")) body = { measurements: [] };
    if (path.endsWith("/health-history")) body = { capturedAt: "2026-11-03T12:00:00Z", counts: { passed: 1, failed: 1, neutral: 0 }, checks: [{ outcome: "failed", capability: "streaming", accountLabel: "Public", testedAt: "2026-11-03T12:00:00Z" }, { outcome: "passed", capability: "streaming", accountLabel: "Public", testedAt: "2026-11-02T12:00:00Z" }] };
    if (path.startsWith("/api/admin/providers/use")) {
      if (request.method() === "PUT") { useModes.set(path.split("/").at(-1)!, request.postDataJSON().mode); revision++; }
      body = { householdRevision: String(revision), personalRevision: "1", providers: providers.map(provider => ({ providerId: provider.id, householdMode: useModes.get(provider.id), personalMode: null, effectiveMode: useModes.get(provider.id), canChoosePersonal: useModes.get(provider.id) !== "off" })) };
    }
    if (path.includes("/providers/test/")) body = { success: true, healthy: true };
    if (path === "/api/admin/scrobbling/deliveries") body = { items: listens };
    if (path.startsWith("/api/admin/provider-favorite-links")) body = { items: [], nextCursor: null, activeProtocol: "jellyfin" };
    if (path.endsWith("/resolve")) { listens = listens.filter(item => !path.includes(item.id)); body = {}; }
    if (path.endsWith("/auth/playlist-consent")) {
      if (request.method() === "DELETE") granted = false;
      if (request.method() === "POST") granted = true;
      body = { supported: true, granted, needsSignIn: false, updatedAt: granted ? "2026-11-03T12:00:00Z" : null, queuedSyncs: 2 };
    }
    if (path.endsWith("/extensions/packages") || path.endsWith("/extensions/registries")) body = [];
    if (path.endsWith("/extensions/store")) body = { items: [], errors: [] };
    if (path.includes("/favorites")) body = { links: [] };
    if (path.endsWith("/updates/stream")) return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return route.fulfill({ json: body });
  });
  return writes;
}

async function capture(page: Page, name: string) {
  await mkdir("test-results/ui-sources", { recursive: true });
  const dismiss = page.getByRole("region", { name: "Notifications", exact: true }).getByRole("button", { name: "Dismiss notification", exact: true });
  while (await dismiss.count()) await dismiss.first().click();
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: `test-results/ui-sources/${name}.png`, fullPage: true });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
}

test("Sources shows working low-quality playback with real-play start timing", async ({ page }) => {
  await fixture(page);
  const streaming = { id: "streaming", health: "healthy", ready: true, canTest: true, canAttempt: true,
    testedAt: "2026-11-03T12:00:00Z" };
  const deezer = { id: "deezer", name: "Deezer", implementationOrigin: "built-in", status: "healthy",
    categories: ["streaming"], useMode: "automatic", runtimeCapabilities: [streaming] };
  await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: { activeBackend: "Jellyfin", providers: [deezer] } }));
  await page.route("**/api/admin/providers/status", route => route.fulfill({ json: [{ ...streaming,
    provider: "deezer", capability: "streaming", supported: true, enabled: true }] }));
  await page.route("**/scores", route => route.fulfill({ json: { providers: [{
    providerId: "deezer", score: 60, uptimePercent: 95, checks: 20, days: [],
    quality: { typical: "MP3 128 kbps", best: "MP3 128 kbps", lossless: false, samples: 4 },
    capabilities: [{ capability: "streaming", score: 60, uptimePercent: 95, checks: 20, failures: 1,
      typicalLatencyMilliseconds: 100, lastFailureCode: "resolve",
      parts: { earned: 84, reliability: 50, reliabilityOutOf: 50, speed: 19, speedOutOf: 20,
        quality: 15, qualityOutOf: 30, cappedAt: 60, typicalMilliseconds: 750,
        realPlays: 4, realPlayFailures: 0, timedOnRealPlays: true } }],
  }] } }));
  await page.route("**/deep-stream/latest", route => route.fulfill({ json: { measurements: [{
    providerId: "deezer", health: "degraded", latencyMs: 0, bars: 0,
    testedAt: "2026-11-01T12:00:00Z", failureCode: null,
  }] } }));
  await page.goto("#/sources/services/deezer");
  const row = page.locator(".source-row").filter({ hasText: "Deezer" });
  await expect(row).toContainText("Working");
  await expect(row).toContainText("MP3 128 kbps");
  const panel = page.getByRole("dialog", { name: "Deezer", exact: true });
  await expect(panel.locator(".source-summary")).toContainText("Working");
  await expect(panel.locator(".source-facts dt").filter({ hasText: /^Quality$/ }).locator("+ dd")).toHaveText("MP3 128 kbps");
  await expect(panel.locator(".source-facts dt").filter({ hasText: /^Score$/ }).locator("+ dd")).toHaveText("60");
  await expect(panel.locator(".source-facts dt").filter({ hasText: /^Start time$/ }).locator("+ dd")).toHaveText("750 ms in real plays");
  expect(await page.pageErrors()).toEqual([]);
});

for (const [width, theme] of [[1280, "light"], [1280, "dark"], [390, "light"], [390, "dark"]] as const) {
  test(`Sources redesign services and source panel at ${width} ${theme}`, async ({ page }) => {
    await page.clock.install();
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const writes = await fixture(page);
    await page.goto("#/sources/services");
    await expect(page.getByRole("region", { name: "Services status" })).toContainText("1 needs attention");
    await expect(page.getByRole("radio", { name: "Needs attention 1" })).toHaveAttribute("aria-checked", "true");
    await expect(page.locator(".source-row")).toHaveCount(1);
    await capture(page, `services-${width}-${theme}`);
    await page.getByRole("radio", { name: "All 3", exact: true }).click();
    await expect(page.locator(".source-row")).toHaveCount(3);
    await expect(page.getByRole("list", { name: "Services", exact: true }).getByText("Qobuz", { exact: true })).toHaveCount(1);
    await page.reload();
    await expect(page.getByRole("radio", { name: "All 3", exact: true })).toHaveAttribute("aria-checked", "true");
    const documentRequests: string[] = [];
    page.on("request", request => { if (request.isNavigationRequest()) documentRequests.push(request.url()); });
    await page.getByRole("link", { name: "Open Qobuz", exact: true }).click();
    const panel = page.getByRole("dialog", { name: "Qobuz", exact: true });
    await expect(panel.getByRole("region", { name: "Last 30 checks", exact: true })).toBeVisible();
    const everyone = panel.getByRole("radiogroup", { name: "Use this source for everyone" });
    await everyone.getByRole("radio", { name: "Off", exact: true }).click();
    await expect(everyone.getByRole("radio", { name: "Off", exact: true })).toHaveAttribute("aria-checked", "true");
    await expect(page.getByRole("button", { name: "Undo", exact: true })).toBeVisible();
    await panel.getByRole("button", { name: "Next source", exact: true }).click();
    const nextPanel = page.locator("[data-side-panel]");
    await expect(nextPanel).not.toHaveAccessibleName("Qobuz");
    await expect(nextPanel.getByRole("radio", { name: "On", exact: true }).first()).toHaveAttribute("aria-checked", "true");
    await page.clock.fastForward(6000);
    await expect(nextPanel.getByRole("status").filter({ hasText: "Qobuz setting saved." })).toBeVisible();
    await nextPanel.getByRole("button", { name: "Undo", exact: true }).click();
    await nextPanel.getByRole("button", { name: "Previous source", exact: true }).click();
    await expect(everyone.getByRole("radio", { name: "On", exact: true })).toHaveAttribute("aria-checked", "true");
    await expect(panel.getByRole("button", { name: "Undo", exact: true })).toHaveCount(0);
    expect(writes.filter(write => write.path.includes("/providers/use/"))).toEqual([
      { path: "/api/admin/providers/use/qobuz", body: { mode: "off", scope: "household", expectedRevision: "1" } },
      { path: "/api/admin/providers/use/qobuz", body: { mode: "automatic", scope: "household", expectedRevision: "2" } },
    ]);
    await capture(page, `source-panel-${width}-${theme}`);
    await panel.getByRole("button", { name: "Close", exact: true }).click();
    await expect(panel).toBeHidden();
    expect(documentRequests).toEqual([]);
    await page.getByRole("button", { name: "Add extension", exact: true }).click();
    const extension = page.getByRole("dialog", { name: "Add an extension", exact: true });
    await expect(extension).toBeVisible();
    await capture(page, `extension-${width}-${theme}`);
    await extension.getByRole("button", { name: "Close add an extension" }).click();
    expect(await page.pageErrors()).toEqual([]);
  });

  test(`Sources redesign accounts, repair, connection and consent at ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const writes = await fixture(page);
    await page.goto("#/sources/accounts");
    await expect(page.getByRole("region", { name: "Accounts status" })).toContainText("2 accounts");
    await expect(page.getByRole("region", { name: "Accounts status" })).toContainText("2 waiting");
    await expect(page.locator(".grouped-row")).toHaveCount(1);
    const hearts = page.getByRole("region", { name: "Shared provider hearts", exact: true });
    await expect(hearts.getByRole("button", { name: "Review profiles", exact: true })).toBeDisabled();
    const jellyfinProfile = await hearts.getByRole("button", { name: "Jellyfin profile", exact: true }).boundingBox();
    const subsonicProfile = await hearts.getByRole("button", { name: "Subsonic profile", exact: true }).boundingBox();
    if (width > 650) {
      expect(subsonicProfile!.x).toBeGreaterThan(jellyfinProfile!.x + jellyfinProfile!.width);
      expect(subsonicProfile!.y).toBe(jellyfinProfile!.y);
    } else expect(subsonicProfile!.y).toBeGreaterThan(jellyfinProfile!.y + jellyfinProfile!.height);
    await page.getByRole("searchbox", { name: "Search accounts" }).fill("ListenBrainz");
    await expect(page.getByRole("link", { name: "Open Apple Music account" })).toBeHidden();
    await page.getByRole("searchbox", { name: "Search accounts" }).fill("");
    await capture(page, `accounts-${width}-${theme}`);
    const documentRequests: string[] = [];
    page.on("request", request => { if (request.isNavigationRequest()) documentRequests.push(request.url()); });
    await page.getByRole("link", { name: "Open ListenBrainz account" }).click();
    const panel = page.getByRole("dialog", { name: "ListenBrainz", exact: true });
    await expect(panel).toContainText("2 listens are waiting");
    await panel.getByRole("button", { name: "Previous account", exact: true }).click();
    const previousPanel = page.getByRole("dialog", { name: "Apple Music", exact: true });
    await expect(previousPanel).toBeVisible();
    await previousPanel.getByRole("button", { name: "Next account", exact: true }).click();
    await expect(panel).toContainText("2 listens are waiting");
    await capture(page, `account-panel-${width}-${theme}`);
    await panel.getByRole("button", { name: "Confirm destination", exact: true }).click();
    const confirm = page.getByRole("alertdialog", { name: "Confirm destination?" });
    await expect(confirm).toContainText("Listener");
    expect(writes.filter(write => write.path.endsWith("/resolve"))).toHaveLength(0);
    await confirm.getByRole("button", { name: "Retry to saved account" }).click();
    await expect.poll(() => writes.filter(write => write.path.endsWith("/resolve")).length).toBe(2);
    expect(writes.filter(write => write.path.endsWith("/resolve")).every(write => write.body.destinationAccountId === "account-1")).toBe(true);
    await panel.getByRole("button", { name: "Close", exact: true }).click();
    await expect(panel).toBeHidden();
    expect(documentRequests).toEqual([]);
    const consent = page.getByRole("region", { name: "Playlist editing in Jellyfin" });
    await consent.getByRole("button", { name: "Remove", exact: true }).click();
    expect(writes.filter(write => write.path.endsWith("/playlist-consent"))).toHaveLength(0);
    await page.getByRole("alertdialog").getByRole("button", { name: "Remove sign-in" }).click();
    await consent.getByRole("button", { name: "Allow editing" }).click();
    const grant = page.getByRole("dialog", { name: "Allow playlist editing?" });
    await grant.getByLabel("Jellyfin password").fill("fixture-password");
    await capture(page, `playlist-consent-${width}-${theme}`);
    await grant.getByRole("button", { name: "Allow editing", exact: true }).click();
    await expect(grant).toBeHidden();
    await page.getByRole("button", { name: "Connect account", exact: true }).click();
    const connect = page.getByRole("dialog", { name: "Connect an account", exact: true });
    await capture(page, `connect-service-${width}-${theme}`);
    await expect(connect.getByText("Apple Music", { exact: true })).toHaveCount(1);
    await connect.getByRole("radio", { name: /Apple Music/ }).click();
    await connect.getByRole("button", { name: "Next", exact: true }).click();
    const signIn = page.getByRole("dialog", { name: "Connect Apple Music", exact: true });
    await expect(signIn.getByText("Apple Music", { exact: true })).toHaveCount(1);
    await signIn.getByLabel("Media User Token").fill("fixture-token");
    await capture(page, `connect-sign-in-${width}-${theme}`);
    await signIn.getByRole("button", { name: "Connect", exact: true }).click();
    await expect(signIn).toBeHidden();
    expect(writes.find(write => write.path === "/api/admin/provider-accounts")?.body).toMatchObject({ providerId: "apple-musickit", scope: "Personal" });
    expect(await page.pageErrors()).toEqual([]);
  });
}

for (const count of [1, 2, 32]) {
  test(`Sources check strip keeps 30 fixed slots with ${count} recorded ${count === 1 ? "check" : "checks"}`, async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 844 });
    await fixture(page);
    const checks = Array.from({ length: count }, (_, index) => ({
      outcome: index % 3 === 0 ? "failed" : "passed", capability: "streaming", accountLabel: "Public",
      testedAt: new Date(Date.UTC(2026, 10, 3, 12, -index)).toISOString(), latencyMs: index % 3 === 1 ? 1500 : 50,
    }));
    await page.route("**/health-history?*", route => route.fulfill({ json: {
      capturedAt: "2026-11-03T12:00:00Z", counts: { passed: 0, failed: count, neutral: 0 }, checks,
    } }));
    await page.goto("#/sources/services/qobuz");
    const strip = page.getByRole("region", { name: "Last 30 checks", exact: true }).getByRole("group");
    await expect(strip).toBeVisible();
    const shown = Math.min(count, 30), slow = checks.slice(0, 30).filter(check => check.latencyMs >= 1000).length;
    const failed = checks.slice(0, 30).filter(check => check.outcome === "failed").length;
    await expect(strip).toHaveAccessibleName(`${shown} recorded ${shown === 1 ? "check" : "checks"}, oldest to newest; ${failed} failed, ${slow} slow, ${shown - failed - slow} ok, 0 not checked; ${30 - shown} empty slots. Slow means 1 second or more.`);
    await expect(strip.locator("[data-outcome]")).toHaveCount(30);
    expect(await strip.locator("[data-outcome]").evaluateAll(slots => slots.map(slot => slot.getAttribute("data-outcome"))))
      .toEqual([...Array(30 - shown).fill("empty"), ...checks.slice(0, 30).toReversed().map(check => check.outcome === "failed" ? "failed" : check.latencyMs >= 1000 ? "slow" : "ok")]);
    expect(await strip.evaluate(element => {
      const slots = [...element.children].map(slot => slot.getBoundingClientRect());
      const bounds = element.getBoundingClientRect();
      return slots.every(slot => slot.width === 5 && slot.left >= bounds.left && slot.right <= bounds.right);
    })).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
}

test("Sources check counts use singular in rows and the panel", async ({ page }) => {
  await fixture(page);
  await page.goto("#/sources/services");
  await expect(page.locator(".source-row").filter({ hasText: "Qobuz" })).toContainText("0 of 1 check passed");
  await page.getByRole("link", { name: "Open Qobuz", exact: true }).click();
  await expect(page.locator(".source-summary")).toContainText("0 of 1 check passed");
});

for (const width of [1280, 390]) {
  test(`Sources check card hover, keyboard and edge placement at ${width}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await fixture(page);
    await page.clock.install({ time: new Date("2026-11-03T12:05:00Z") });
    await page.route("**/health-history?*", route => route.fulfill({ json: {
      capturedAt: "2026-11-03T12:05:00Z", counts: { passed: 1, failed: 1, neutral: 1 }, checks: [
        { outcome: "failed", capability: "streaming", accountLabel: "Household", accountScope: "shared", reasonCode: "timeout", latencyMs: 1250, testedAt: "2026-11-03T12:00:00Z" },
        { outcome: "passed", capability: "lyrics", accountLabel: "Public", accountScope: "public", latencyMs: 42, testedAt: "2026-11-03T11:59:00Z" },
        { outcome: "neutral", capability: "streaming", accountLabel: "Public", accountScope: "public", reasonCode: "probe_not_available", latencyMs: null, testedAt: "2026-11-03T11:58:00Z" },
      ],
    } }));
    await page.goto("#/sources/services/qobuz");
    const panel = page.getByRole("dialog", { name: "Qobuz", exact: true });
    const strip = panel.getByRole("region", { name: "Last 30 checks", exact: true }).getByRole("group");
    const bars = strip.locator("[data-outcome]");
    const card = panel.getByRole("tooltip");
    await bars.last().hover();
    await expect(card).toBeVisible();
    await expect(card).toContainText("Streaming");
    await expect(card).toContainText("Household");
    await expect(card).toContainText("Failed");
    await expect(card).toContainText("Timed out");
    await expect(card).toContainText("1250 ms");
    await expect(card).toContainText("5 minutes ago");
    await expect(card.locator("time")).toHaveAttribute("datetime", "2026-11-03T12:00:00Z");
    const localTime = await page.evaluate(value => new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }).format(new Date(value)), "2026-11-03T12:00:00Z");
    await expect(card.locator("time")).toHaveText(localTime);
    if (width === 390) {
      expect((await strip.boundingBox())!.height).toBeGreaterThanOrEqual(44);
      await strip.hover({ position: { x: (await strip.boundingBox())!.width - 2, y: 1 } });
      await expect(card).toContainText("Failed");
    }
    await expect(bars.last()).toHaveAttribute("data-active", "true");
    await expect(strip).not.toBeFocused();
    await expect(card).toHaveAttribute("data-align", "end");
    const contained = async () => {
      expect(await card.evaluate(element => {
        const bounds = element.getBoundingClientRect();
        const panel = element.closest("[data-side-panel]")!.getBoundingClientRect();
        return bounds.left >= panel.left && bounds.right <= panel.right && bounds.top >= panel.top && bounds.bottom <= panel.bottom;
      })).toBe(true);
    };
    await contained();
    await card.hover();
    await expect(card).toBeVisible();
    await panel.getByRole("heading", { name: "Last 30 checks", exact: true }).hover();
    await expect(card).toBeHidden();
    await strip.focus();
    await expect(card).toContainText("Failed");
    await strip.press("ArrowLeft");
    await expect(card).toContainText("Lyrics");
    await expect(card).toContainText("Passed");
    await expect(card).toContainText("42 ms");
    await strip.press("ArrowLeft");
    await expect(card).toContainText("Neutral");
    await strip.press("Home");
    await expect(card).toHaveText("No check");
    await strip.press("ArrowLeft");
    await expect(bars.first()).toHaveAttribute("data-active", "true");
    await strip.press("End");
    await strip.press("ArrowRight");
    await expect(bars.last()).toHaveAttribute("data-active", "true");
    await strip.press("Escape");
    await expect(card).toBeHidden();
    await expect(panel).toBeVisible();
    await expect(strip).toBeFocused();
    await strip.press("ArrowLeft");
    await expect(card).toBeVisible();
    await strip.press("Tab");
    await expect(card).toBeHidden();
    await page.setViewportSize({ width, height: 500 });
    await bars.last().evaluate(element => element.scrollIntoView({ block: "end" }));
    await bars.last().hover();
    await expect(card).toHaveAttribute("data-side", "above");
    await contained();
    expect(await page.pageErrors()).toEqual([]);
  });

  test.describe(`Sources check card touch at ${width}`, () => {
    test.use({ hasTouch: true, viewport: { width, height: 900 } });
    test("opens and dismisses check details with taps", async ({ page }) => {
      await fixture(page);
      await page.goto("#/sources/services/qobuz");
      const panel = page.getByRole("dialog", { name: "Qobuz", exact: true });
      const strip = panel.getByRole("region", { name: "Last 30 checks", exact: true }).getByRole("group");
      const bars = strip.locator("[data-outcome]");
      const card = panel.getByRole("tooltip");
      await bars.last().tap();
      await expect(card).toContainText("Failed");
      await bars.first().tap();
      await expect(card).toHaveText("No check");
      await bars.first().tap();
      await expect(card).toBeHidden();
      await bars.last().tap();
      await expect(card).toBeVisible();
      await panel.getByRole("heading", { name: "Last 30 checks", exact: true }).tap();
      await expect(card).toBeHidden();
      expect(await page.pageErrors()).toEqual([]);
    });
  });
}

for (const width of [320, 1280]) {
  test(`Sources latest checks disclosure aligns with panel sections at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await fixture(page);
    await page.goto("#/sources/services/qobuz");
    const latest = page.locator('details[aria-label="Recent diagnostic checks"] summary strong');
    await expect(latest).toBeVisible();
    await expect(page.getByRole("heading", { name: "Last 30 checks", exact: true })).toBeVisible();
    const offset = await latest.evaluate(element => {
      const heading = element.closest("[data-side-panel]")!.querySelector(".check-history h3")!;
      return Math.abs(element.getBoundingClientRect().left - heading.getBoundingClientRect().left);
    });
    expect(offset).toBeLessThanOrEqual(1);
    const followsSettings = await latest.evaluate(element => {
      const panel = element.closest("[data-side-panel]")!;
      const settings = panel.querySelector(".source-use-settings")!;
      const facts = panel.querySelector(".source-facts")!;
      return Boolean(settings.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING) &&
        Boolean(facts.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING);
    });
    expect(followsSettings).toBe(true);
  });
}

test("Sources accounts status uses singular account and waiting counts", async ({ page }) => {
  await fixture(page);
  await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: {
    accounts: [{ id: "account-1", providerId: "listenbrainz", displayName: "Listener", scope: "Personal", enabled: true, secret: { configured: true } }], audienceUsers: [],
  } }));
  await page.route("**/api/admin/scrobbling/deliveries?*", route => route.fulfill({ json: { items: [{ id: "listen-1", targetId: "listenbrainz", state: "retrying", kind: "completed", canResend: true, submission: { title: "First song", artist: "An artist" } }] } }));
  await page.goto("#/sources/accounts");
  const status = page.getByRole("region", { name: "Accounts status" });
  await expect(status).toContainText("1 account");
  await expect(status).not.toContainText("1 accounts");
  await expect(status).toContainText("1 waiting");
});

test("Sources redesign keeps a removal error in its confirmation", async ({ page }) => {
  await fixture(page);
  await page.route("**/api/admin/auth/playlist-consent", route => route.request().method() === "DELETE"
    ? route.fulfill({ status: 503, json: { error: "Saved sign-in could not be removed. Try again." } }) : route.fallback());
  await page.goto("#/sources/accounts");
  await page.getByRole("region", { name: "Playlist editing in Jellyfin" }).getByRole("button", { name: "Remove", exact: true }).click();
  const confirmation = page.getByRole("alertdialog");
  await confirmation.getByRole("button", { name: "Remove sign-in" }).click();
  await expect(confirmation.getByRole("alert")).toContainText("Saved sign-in could not be removed");
  await expect(confirmation.getByRole("button", { name: "Remove sign-in" })).toBeEnabled();
});

for (const width of [320, 390, 430, 650]) for (const section of ["services", "accounts"]) {
  test(`Sources header actions stay contained at ${width}px in ${section}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await fixture(page);
    await page.goto(`#/sources/${section}`);
    const actions = page.locator(".sources-heading-actions");
    await expect(actions.getByRole("button", { name: section === "accounts" ? "Connect account" : "Add extension", exact: true })).toBeVisible();
    expect(await actions.evaluate(element => {
      const bounds = element.getBoundingClientRect();
      return bounds.left >= 0 && bounds.right <= innerWidth && [...element.querySelectorAll("button")].every(button => {
        const target = button.getBoundingClientRect();
        return target.left >= bounds.left && target.right <= bounds.right && target.width >= 43.99 && target.height >= 43.99;
      });
    })).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    if (section !== "services" || width >= 600) return;
    const filter = page.getByRole("radiogroup", { name: "Filter sources", exact: true });
    const radios = filter.getByRole("radio");
    expect(new Set(await radios.evaluateAll(items => items.map(item => Math.round(item.getBoundingClientRect().top)))).size).toBe(1);
    await radios.last().click();
    await page.reload();
    await expect(radios.last()).toHaveAttribute("aria-checked", "true");
    await expect.poll(() => radios.last().evaluate(item => {
      const box = item.parentElement!.getBoundingClientRect(), target = item.getBoundingClientRect();
      return target.left >= box.left - 1 && target.right <= box.right + 1;
    })).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
}

for (const section of ["services", "accounts"]) {
  test(`Sources ${section} keep brand images after equal definitions refresh`, async ({ page }) => {
    await fixture(page);
    await page.goto(`#/sources/${section}`);
    if (section === "services") await page.getByRole("radio", { name: "All 3", exact: true }).click();
    const marks = page.locator(section === "services" ? ".service-rows .provider-mark" : ".accounts-table .provider-mark");
    await expect(marks).toHaveCount(section === "services" ? 3 : 2);
    await expect.poll(() => marks.evaluateAll(elements => elements.every(element => {
      const image = element.querySelector("img");
      return image && image.complete && image.naturalWidth > 0 && !image.hidden && !element.querySelector(".provider-monogram");
    }))).toBe(true);
    const original = await marks.first().locator("img").elementHandle();
    for (let refresh = 0; refresh < 2; refresh++) {
      const responses = ["/ui/schema", "/provider-accounts", "/ui/provider-summaries", "/providers/status", "/deep-stream/latest", "/config"]
        .map(path => page.waitForResponse(response => new URL(response.url()).pathname.endsWith(path)));
      await page.evaluate(() => window.dispatchEvent(new Event("focus")));
      await Promise.all((await Promise.all(responses)).map(response => response.finished()));
      await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => resolve())));
      expect(await marks.first().locator("img").evaluate((element, previous) => element === previous, original)).toBe(true);
      await expect.poll(() => marks.evaluateAll(elements => elements.every(element => {
        const image = element.querySelector("img");
        return image && image.complete && image.naturalWidth > 0 && !image.hidden && !element.querySelector(".provider-monogram");
      }))).toBe(true);
    }
  });
}

for (const checked of [false, true]) {
  test(`Sources empty checks explain ${checked ? "pending checks" : "no automatic checks"}`, async ({ page }) => {
    await fixture(page);
    await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
      activeBackend: "Jellyfin", providers: [{ id: "musicbrainz", name: "MusicBrainz", categories: ["enrichment"],
        runtimeCapabilities: checked ? [{ id: "metadata", canTest: true, health: "unknown" }] : [] }],
    } }));
    await page.route("**/api/admin/providers/status", route => route.fulfill({ json: [] }));
    await page.route("**/health-history?*", route => route.fulfill({ json: {
      capturedAt: "2026-11-03T12:00:00Z", counts: { passed: 0, failed: 0, neutral: 0 }, checks: [],
    } }));
    await page.goto("#/sources/services/musicbrainz");
    const panel = page.getByRole("dialog", { name: "MusicBrainz", exact: true });
    await expect(panel.getByText(checked ? "No checks yet" : "This source isn't checked automatically", { exact: true })).toBeVisible();
    await expect(panel.getByRole("region", { name: "Last 30 checks", exact: true })).toHaveCount(0);
    await expect(panel.getByText("Checks could not be loaded")).toHaveCount(0);
    await expect(panel.getByText("Recent checks unavailable")).toHaveCount(0);
  });
}

test("Sources history load errors stay recoverable", async ({ page }) => {
  await fixture(page);
  await page.route("**/health-history?*", route => route.fulfill({ status: 503, json: { error: "Unavailable" } }));
  await page.goto("#/sources/services/qobuz");
  await expect(page.getByText("Checks could not be loaded", { exact: true })).toBeVisible();
  await expect(page.getByText("Recent checks unavailable", { exact: true })).toBeVisible();
  await expect(page.getByText("No checks yet", { exact: true })).toHaveCount(0);
  await page.route("**/health-history?*", route => route.fulfill({ json: {
    capturedAt: "2026-11-03T12:00:00Z", counts: { passed: 0, failed: 0, neutral: 0 }, checks: [],
  } }));
  await page.getByRole("button", { name: "Try again", exact: true }).click();
  await expect(page.getByText("No checks yet", { exact: true })).toBeVisible();
});


test("Account rows group duplicate services and keep account names separate from owners", async ({ page }) => {
  await fixture(page);
  await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: { accounts: [["apple-musickit", "Morning"], ["apple-musickit", "Evening"], ["qobuz", "Main"]].map(([providerId, name], index) => ({ id: `account-${index}`, providerId, displayName: name, ownerDisplayName: "Listener", scope: "Shared", canManage: true, enabled: true, revision: 1, secret: { configured: true, revoked: false }, createdAt: "2026-11-01T12:00:00Z" })), audienceUsers: [] } }));
  await page.goto("/#/sources/accounts");
  const table = page.getByRole("table", { name: "Accounts", exact: true });
  await expect(table.getByRole("columnheader", { name: "Owner", exact: true })).toBeVisible();
  await expect(table.locator("tbody tr")).toHaveCount(2);
  await expect(table).toContainText("Apple Music · 2 accounts");
  await expect(table.locator('[data-label="Account"]').first()).toHaveText("Morning, Evening");
  await expect(table.locator('[data-label="Owner"]').first()).toHaveText("Listener");
  await expect(table.locator("tbody tr").last()).toContainText("Service down");
  await page.getByRole("button", { name: "Connect account", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Connect an account", exact: true });
  await expect(dialog.getByLabel("Connection steps", { exact: true })).toBeVisible();
  await expect(dialog.getByRole("radiogroup", { name: "Service", exact: true })).toContainText("Built-in");
  const tiles = dialog.getByRole("radio");
  await tiles.first().focus();
  await page.keyboard.press("ArrowRight");
  await expect(tiles.nth(1)).toBeFocused();
  await expect(tiles.nth(1)).toHaveAttribute("aria-checked", "true");
});

for (const width of [390, 1280]) test(`Connection tile keyboard follows the grid at ${width}px`, async ({ page }) => {
  await fixture(page);
  await page.setViewportSize({ width, height: 900 });
  await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: { activeBackend: "Jellyfin", providers: Array.from({ length: 8 }, (_, index) => ({ id: `service-${index}`, name: `Service ${index + 1}`, categories: ["streaming"], implementationOrigin: index % 2 ? "extension" : "built-in", accountSettings: [{ key: "token", label: "Token", type: "text", sensitive: true }] })) } }));
  await page.goto("/#/sources/accounts");
  await page.getByRole("button", { name: "Connect account", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Connect an account", exact: true });
  const tiles = dialog.getByRole("radio");
  await tiles.first().focus();
  await page.keyboard.press("ArrowDown");
  await expect(tiles.nth(width === 390 ? 2 : 4)).toBeFocused();
  await expect(dialog.locator(".dialog-actions")).toHaveCSS("position", "sticky");
});

for (const counts of [{ waiting: 1, blocked: 2 }, { waiting: 0, blocked: 1 }, { waiting: 0, blocked: 0 }]) {
  test(`Sources accounts header separates ${counts.waiting} waiting and ${counts.blocked} unavailable listens`, async ({ page }) => {
    await fixture(page);
    await page.route("**/api/admin/scrobbling/deliveries?*", route => route.fulfill({ json: { items: [
      ...Array.from({ length: counts.waiting }, (_, index) => ({ id: `waiting-${index}`, kind: "completed", state: "retrying", canResend: true, targetId: "listenbrainz", submission: { title: "Song", artist: "Artist" } })),
      ...Array.from({ length: counts.blocked }, (_, index) => ({ id: `blocked-${index}`, kind: "completed", state: "permanentfailure", canResend: false, canDismiss: true, unavailableReason: "The saved listen is unavailable", targetId: "listenbrainz", submission: null })),
      { id: "notice", kind: "nowplaying", state: "permanentfailure", canResend: false, targetId: "listenbrainz" },
    ] } }));
    await page.goto("#/sources/accounts");
    await expect(page.locator(".delivery-groups")).toHaveAttribute("aria-busy", "false");
    await expect(page.getByRole("button", { name: "Refresh delivery", exact: true })).toHaveCount(0);
    if (!counts.waiting && !counts.blocked) await expect(page.getByRole("region", { name: "Scrobble delivery", exact: true })).toBeHidden();
    const status = page.getByRole("region", { name: "Accounts status" });
    if (counts.waiting) await expect(status).toContainText(`${counts.waiting} waiting`);
    else await expect(status).not.toContainText("waiting");
    if (counts.blocked) {
      const link = status.getByRole("link", { name: `${counts.blocked} can't be sent`, exact: true });
      await expect(link).toHaveAttribute("href", "#/sources/accounts?section=scrobble-problems");
      await link.click();
      await expect(page.locator("#scrobble-problems")).toBeFocused();
      await expect(page.locator("#scrobble-problems .grouped-row")).toHaveCount(counts.waiting ? 2 : 1);
    } else await expect(status).not.toContainText("can't be sent");
  });
}

test("Sources accounts header omits incomplete delivery totals until earlier listens load", async ({ page }) => {
  await fixture(page);
  await page.route("**/api/admin/scrobbling/deliveries?*", route => {
    const earlier = new URL(route.request().url()).searchParams.has("before");
    return route.fulfill({ json: { items: Array.from({ length: earlier ? 1 : 100 }, (_, index) => ({
      id: `${earlier ? "earlier" : "recent"}-${index}`, kind: "completed", state: "permanentfailure", targetId: "listenbrainz",
      canResend: false, canDismiss: true, submission: null, unavailableReason: "The saved listen is unavailable",
    })) } });
  });
  await page.goto("#/sources/accounts");
  const earlier = page.getByRole("button", { name: "Load earlier deliveries", exact: true });
  await expect(earlier).toBeVisible();
  const status = page.getByRole("region", { name: "Accounts status" });
  await expect(status).not.toContainText("waiting");
  await expect(status).not.toContainText("can't be sent");
  await earlier.click();
  await expect(status).toContainText("101 can't be sent");
  await expect(status).not.toContainText("waiting");
});
