import { expect, test, type Page, type Request, type Route } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

type AuthorityOptions = {
  administrator: boolean;
  providers?: Record<string, unknown>[];
  match?: Record<string, unknown>;
  resolve?: (route: Route) => Promise<void>;
  authorityAction?: (route: Route) => Promise<void>;
};

function matchFixture(administrator: boolean) {
  return {
    externalSnapshotId: "displayed-snapshot",
    providerId: "spotify",
    state: "ambiguous",
    decisionSource: "manual_authority",
    confidence: .92,
    threshold: .9,
    title: "Layered song",
    artist: "Example artist",
    album: "Example album",
    durationMilliseconds: 180_000,
    allowedAuthorityScopes: administrator ? ["personal", "household"] : ["personal"],
    effectiveAuthorityScope: "personal",
    manualAuthorities: [
      {
        id: "personal-row",
        kind: "provider_match",
        revision: 7,
        reason: "My preferred recording",
        createdAt: "2026-10-08T12:00:00Z",
        effective: true,
        targetProviderId: "qobuz",
        targetExternalId: "personal-track",
        authoritySnapshotId: "displayed-snapshot",
        scope: "personal",
        canEdit: true,
      },
      {
        id: "household-row",
        kind: "local_match",
        revision: 11,
        reason: "Household library recording",
        createdAt: "2026-10-08T11:00:00Z",
        effective: false,
        authoritySnapshotId: "displayed-snapshot",
        scope: "household",
        canEdit: administrator,
      },
    ],
    providerIdentities: [],
    reasons: ["title_match"],
    warnings: [],
    candidates: [{
      libraryTrackId: "local-track",
      backendItemId: "local-track",
      isLocal: true,
      title: "Layered song",
      artist: "Example artist",
      album: "Example album",
      durationMilliseconds: 180_000,
      confidence: .92,
      components: { title: 1, artist: 1, album: 1, duration: 1, versionTags: 1, acceptanceQualified: 1, routingPriority: 0 },
      reasons: ["title_exact", "artist_exact", "album_exact", "duration_close"],
    }],
  };
}

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}

async function mockMatching(page: Page, options: AuthorityOptions) {
  await page.route("**/api/admin/**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === "/api/admin/auth/me") return json(route, {
      authenticated: true,
      backend: "Jellyfin",
      user: { id: "listener", name: options.administrator ? "Administrator" : "Listener", isAdministrator: options.administrator },
    });
    if (url.pathname === "/api/admin/auth/oidc/status") return json(route, { enabled: false });
    if (url.pathname === "/api/admin/onboarding/status") return json(route, {
      completed: true,
      setupOpen: false,
      shouldRedirectToSetup: false,
      schemaVersion: "onboarding-v1",
      completedSteps: ["backend-identity"],
      completionSource: "setup-guide",
      revision: 1,
      recoveryNotices: [],
      migration: { available: false, completed: false, firstRun: false },
    });
    if (url.pathname === "/api/admin/ui/schema") return json(route, {
      activeBackend: "Jellyfin",
      providers: options.providers ?? [
        { id: "spotify", name: "Spotify", categories: ["metadata"] },
        { id: "qobuz", name: "Qobuz", categories: ["streaming"] },
      ],
    });
    if (url.pathname === "/api/admin/track-matches/displayed-snapshot/resolve")
      return options.resolve?.(route) ?? json(route, { success: true });
    if (url.pathname.includes("/manual-authorities/"))
      return options.authorityAction?.(route) ?? json(route, { rematched: true, state: "accepted" });
    if (url.pathname === "/api/admin/track-matches/selection") return json(route, {
      matches: [{ ...matchFixture(options.administrator), ...options.match }],
    });
    if (url.pathname === "/api/admin/track-matches") return json(route, {
      matches: [{ ...matchFixture(options.administrator), ...options.match }],
      stats: {
        total: 1, playlistCount: 1, matched: 1, accepted: 1, unresolved: 0, suggested: 0, review: 1,
        ambiguous: 1, rejected: 0, attention: 1, manualMatches: 1, manualRejections: 0,
      },
      pagination: { page: 1, pageSize: 50, total: 1, totalPages: 1 },
    });
    if (url.pathname === "/api/admin/updates/stream")
      return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return json(route, {});
  });
}

test("match review hides unlabeled and internal rows while keeping saved manual targets", async ({ page }) => {
  await mockMatching(page, { administrator: false });
  const source = { ...matchFixture(false), candidates: [], providerIdentities: [
    { providerId: "allstarr", externalId: "recording", scope: "catalog", verification: "verified" },
  ] };
  await page.route("**/api/admin/track-matches?*", route => json(route, {
    matches: [
      { ...source, externalSnapshotId: "saved-provider", title: "Saved external source", state: "accepted",
        target: { providerId: "qobuz", externalId: "saved-target", title: "Saved provider recording", artist: "Saved artist" } },
      { ...source, externalSnapshotId: "saved-local", title: "Saved local source", state: "accepted",
        localTrack: { id: "local-recording", backendItemId: "local-recording", title: "Saved library recording" } },
      { ...source, externalSnapshotId: "saved-without-metadata", title: "Source with unavailable target details", state: "accepted",
        target: { providerId: "qobuz", externalId: "saved-without-metadata", title: null } },
      { ...source, externalSnapshotId: "unresolved-source", title: "Unmatched source song", state: "unresolved", manualAuthorities: [] },
      { ...source, externalSnapshotId: "untitled-source", title: null },
      { ...source, externalSnapshotId: "missing-artist", artist: null },
      { ...source, externalSnapshotId: "blank-source", title: " \t " },
      { ...source, externalSnapshotId: "internal-source", providerId: "allstarr", title: "Internal recording" },
      { ...source, externalSnapshotId: "missing-provider", providerId: "", title: "Missing provider" },
    ],
    stats: { total: 4, playlistCount: 1, matched: 3, accepted: 3, unresolved: 1, suggested: 0, review: 0,
      ambiguous: 0, rejected: 0, attention: 1, manualMatches: 3, manualRejections: 0 },
    pagination: { page: 1, pageSize: 50, total: 4, totalPages: 1 },
  }));
  await page.goto("#/library/mappings");
  await page.getByRole("radiogroup", { name: "Match filters" }).getByRole("radio", { name: /^All\b/ }).click();
  const rows = page.locator(".mapping-row");
  await expect(rows).toHaveCount(4);
  await expect(page.getByText("Unknown song", { exact: true })).toHaveCount(0);
  await expect(page.getByText(/Unknown source/)).toHaveCount(0);
  await expect(page.getByText("Internal recording", { exact: true })).toHaveCount(0);
  await expect(rows.filter({ hasText: "Saved external source" })).toContainText("Saved provider recording");
  await expect(rows.filter({ hasText: "Saved local source" })).toContainText("Saved library recording");
  const missing = rows.filter({ hasText: "Source with unavailable target details" });
  await expect(missing.locator(".plays-as")).toContainText("Qobuz");
  await expect(missing.locator(".plays-as small")).toHaveCount(0);
  await expect(missing.locator(".difference-chips")).toBeEmpty();
  await missing.locator(".open-match:visible, .row-action:visible").click();
  const panel = page.getByRole("dialog", { name: "Source with unavailable target details", exact: true });
  await expect(panel.locator(".comparison tbody tr")).toHaveCount(6);
  await expect(panel.locator(".comparison tbody tr td:last-child")).toHaveText(Array(6).fill("Can't compare"));
  await panel.getByRole("button", { name: "Close", exact: true }).click();
  await expect(rows.filter({ hasText: "Unmatched source song" })).toContainText("No match yet");
  await expect(page.getByRole("region", { name: "Page status", exact: true })).toContainText("4 songs");
  await expect(page.getByRole("navigation", { name: "Match review pages" })).toHaveCount(0);
  await page.getByRole("button", { name: "Select", exact: true }).click();
  await page.getByRole("checkbox", { name: "Select all songs on this page" }).check();
  await expect(page.getByRole("toolbar", { name: "Actions for selected songs" })).toContainText("4 selected");
});

for (const width of [390, 1280]) test(`match comparison preserves known fields when target details are missing at ${width}px`, async ({ page }) => {
  await page.setViewportSize({ width, height: 900 });
  await mockMatching(page, { administrator: false, match: {
    title: "Layered song (Live)", candidates: [],
    target: { providerId: "qobuz", externalId: "saved", title: null, artist: "Example artist" },
  } });
  await page.goto("#/library/mappings");
  const row = page.locator(".mapping-row");
  await expect(row).not.toContainText(/Metadata unavailable|Details unavailable|Same song|Not Live/);
  if (width < 900) await expect(row.locator(".row-copy p")).toHaveText("Qobuz");
  else {
    await expect(row.locator(".plays-as")).toContainText("Qobuz");
    await expect(row.locator(".plays-as small")).toHaveCount(0);
    await expect(row.locator(".difference-chips")).toBeEmpty();
  }
  await row.locator(".open-match:visible, .row-action:visible").click();
  const panel = page.getByRole("dialog", { name: "Layered song (Live)", exact: true });
  await expect(panel.locator(".comparison thead th").nth(2)).toHaveText("Plays as · Qobuz");
  await expect(panel.locator(".comparison tbody tr td:last-child")).toHaveText([
    "Can't compare", "Same", "Can't compare", "Can't compare", "Can't compare", "Can't compare",
  ]);
});

for (const theme of ["light", "dark"] as const) {
  test(`manual mapping row actions retain phone touch targets in ${theme} mode`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await mockMatching(page, { administrator: false });
    await page.goto("#/library/mappings");
    await expect(page.locator(".mapping-row").first()).toBeVisible();
    await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
    const panel = page.getByRole("dialog", { name: "Layered song", exact: true });
    await expect(panel.getByRole("button", { name: "Clear personal", exact: true })).toBeVisible();
    await expectContainedMobileGeometry(page);
  });
}

test("match panel shows field scores at 320px in dark mode", async ({ page }) => {
  await page.emulateMedia({ colorScheme: "dark" });
  await page.setViewportSize({ width: 320, height: 844 });
  await mockMatching(page, { administrator: false });
  await page.goto("#/library/mappings");
  await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
  const dialog = page.getByRole("dialog", { name: "Layered song" });
  await expect(dialog.locator(".evidence-chips")).toContainText("Title matches exactly");
  await expect(dialog.locator(".evidence-chips")).toContainText("Length within 1 second");
  await dialog.locator(".evidence details summary").click();
  await expect(dialog.locator(".evidence details")).toContainText("title");
  await expect(dialog.locator(".evidence details")).toContainText("duration");
});

for (const width of [390, 1280]) for (const colorScheme of ["light", "dark"] as const) {
  test(`interactive matching follows automatic order and keeps source priority at ${width}px ${colorScheme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
    const writes: Record<string, unknown>[] = [];
    await mockMatching(page, {
      administrator: false,
      providers: [
        { id: "spotify", name: "Spotify", categories: ["metadata"] },
        { id: "apple-musickit", name: "Apple Music", categories: ["streaming"] },
        { id: "deezer", name: "Deezer", categories: ["streaming"] },
        { id: "qobuz", name: "Qobuz", categories: ["streaming"] },
        { id: "amazon", name: "Amazon Music", categories: ["streaming"] },
      ],
      match: { title: "Power Trip (feat. Miguel)", artist: "J. Cole · Miguel", album: "Born Sinner (Deluxe Version)",
        durationMilliseconds: 241160, isExplicit: true, isrc: null, manualAuthorities: [], candidates: [] },
      resolve: async route => { writes.push(route.request().postDataJSON()); await json(route, { success: true }); },
    });
    await page.route("**/api/admin/track-matches/targets/*?*", async route => json(route, {
      tracks: new URL(route.request().url()).pathname.endsWith("/provider") ? [
        { id: "deezer", externalId: "deezer", externalProvider: "deezer", title: "Power Trip", artist: "J. Cole",
          album: "Born Sinner (Deluxe Version)", confidence: .992, routingPriority: 2, automaticOrder: 2, matchesSourceAlbum: true, isExplicit: true,
          media: { codec: "flac", bitDepth: 16, sampleRate: 44100 } },
        { id: "apple-standard", externalId: "apple-standard", externalProvider: "apple-download", title: "Power Trip (feat. Miguel)",
          artist: "J. Cole", album: "Born Sinner", confidence: .9545, routingPriority: 1, automaticOrder: 1, matchesSourceAlbum: false,
          media: { codec: "aac", bitrate: 256000 } },
        { id: "apple-deluxe", externalId: "apple-deluxe", externalProvider: "apple-download", title: "Power Trip (feat. Miguel)",
          artist: "J. Cole", album: "Born Sinner (Deluxe Version)", confidence: .9545, routingPriority: 1, automaticOrder: 0, matchesSourceAlbum: true, isExplicit: true,
          media: { codec: "alac", bitDepth: 24, sampleRate: 48000 } },
        { id: "qobuz", externalId: "qobuz", externalProvider: "qobuz", title: "Power Trip", artist: "J. Cole",
          confidence: .9544, routingPriority: 3, automaticOrder: 3, media: null },
        { id: "amazon", externalId: "amazon", externalProvider: "amazon", title: "Power Trip [feat. Miguel]",
          artist: "J. Cole feat. Miguel", confidence: .94, routingPriority: 5, automaticOrder: 4, media: null,
          warnings: ["limited_recording_metadata"] },
        { id: "clean", externalId: "clean", externalProvider: "apple-download", title: "Power Trip (feat. Miguel)",
          artist: "J. Cole", album: "Born Sinner", confidence: 0, routingPriority: 1, automaticOrder: 5, isExplicit: false,
          reasons: ["clean_edit_of_explicit"], media: null },
      ] : [],
    }));
    await page.goto("#/library/mappings");
    await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
    const dialog = page.getByRole("dialog", { name: "Power Trip (feat. Miguel)" });
    await expect(dialog.locator(".panel-header-extra")).toContainText("Explicit");
    await dialog.getByRole("button", { name: "Search for another version…", exact: true }).click();
    await expect(dialog).toContainText("Good matches of the same version follow your streaming priority, then the same album edition");
    await dialog.getByRole("button", { name: "Search", exact: true }).click();
    const rows = dialog.locator(".target-results .search-result");
    await expect(rows).toHaveCount(6);
    for (const row of await rows.all()) await row.locator("summary").click();
    await expect(rows.nth(0)).toContainText("Apple Music");
    await expect(rows.nth(0)).toContainText("Streaming priority 1");
    await expect(rows.nth(0)).toContainText("Same album edition");
    await expect(rows.nth(0).locator("dl > div").filter({ has: page.locator("dt", { hasText: "Content" }) })).toContainText("Explicit");
    await expect(rows.nth(0).locator("dl > div").filter({ has: page.locator("dt", { hasText: "Reported audio" }) })).toContainText("ALAC · 24-bit · 48 kHz");
    await expect(rows.nth(1)).toContainText("Apple Music");
    await expect(rows.nth(1)).not.toContainText("Same album edition");
    await expect(rows.nth(2)).toContainText("Deezer");
    await expect(rows.nth(3)).toContainText("95%");
    await expect(rows.nth(3)).toContainText("Audio format not reported");
    await expect(rows.nth(3)).not.toContainText("Not explicit");
    await expect(rows.nth(4)).toContainText("94%");
    await expect(rows.nth(4)).toContainText("Some recording details are missing");
    await expect(rows.nth(4)).toContainText("Audio format not reported");
    await expect(rows.nth(5)).toContainText("Not explicit");
    await expect(rows.nth(5)).toContainText("0%");
    for (let index = 0; index < 2; index++) await expect(rows.nth(index)).toContainText("95%");
    await expect(rows.nth(2)).toContainText("99%");
    const evidence = dialog.locator(".target-evidence").first();
    await expect(evidence).toContainText("Source ISRCNot provided");
    await expect(evidence).toContainText("the delivered format can differ");
    if (width === 390) await expectContainedMobileGeometry(page);
    const bounds = await dialog.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.y).toBeGreaterThanOrEqual(0);
    expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(901);
    if (width === 390) { expect(bounds!.y).toBe(0); expect(bounds!.height).toBe(900); }
    await dialog.locator(".panel-body").evaluate(element => { element.scrollTop = 0; });
    await page.screenshot({ path: testInfo.outputPath(`match-priority-${width}-${colorScheme}.png`) });
    await rows.nth(4).scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath(`match-limited-${width}-${colorScheme}.png`) });
    await rows.first().locator(".row-action").focus();
    await page.keyboard.press("Enter");
    expect(writes).toHaveLength(0);
    await expect(dialog.locator(".comparison thead th").nth(2)).toContainText("Apple Music");
    await dialog.getByRole("button", { name: "Use Apple Music version", exact: true }).click();
    await expect.poll(() => writes.length).toBe(1);
    expect(writes[0]).toMatchObject({ targetType: "provider", externalProvider: "apple-download", externalId: "apple-deluxe", authorityScope: "personal" });
    await expect(dialog).not.toBeVisible();
  });
}

for (const [width, colorScheme] of [[320, "light"], [1280, "dark"]] as const) {
  test(`manual-only source results come from the normal search and save an exact pin at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
    const queries: URL[] = [];
    const writes: Record<string, unknown>[] = [];
    await mockMatching(page, { administrator: false,
      providers: [{ id: "artistgrid", name: "ArtistGrid", useMode: "manualOnly", categories: ["metadata", "streaming"] }],
      match: { title: "Communicate (v3)", artist: "Ariana Grande", manualAuthorities: [], candidates: [] },
      resolve: async route => { writes.push(route.request().postDataJSON()); await json(route, { success: true }); },
    });
    await page.route("**/api/admin/track-matches/targets/*?*", async route => {
      const url = new URL(route.request().url()); queries.push(url);
      await json(route, { tracks: url.pathname.endsWith("/provider")
        ? [{ id: "exact-v3", externalId: "exact-v3", externalProvider: "artistgrid", title: "Communicate (v3)", artist: "Ariana Grande" }] : [], providers: ["artistgrid"] });
    });
    await page.goto("#/library/mappings");
    await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
    const dialog = page.getByRole("dialog", { name: "Communicate (v3)" });
    await expect(dialog.getByRole("button", { name: "Search another source" })).toHaveCount(0);
    await dialog.getByRole("button", { name: "Search for another version…", exact: true }).click();
    await dialog.getByRole("button", { name: "Search", exact: true }).click();
    await expect(dialog.locator(".target-results")).toContainText("Communicate (v3)");
    expect(queries.some(url => url.searchParams.has("provider"))).toBe(false);
    expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
    await dialog.locator(".target-results .search-result .row-action").click();
    expect(writes).toHaveLength(0);
    await dialog.getByRole("button", { name: "Use ArtistGrid version", exact: true }).click();
    await expect.poll(() => writes.length).toBe(1);
    expect(writes[0]).toMatchObject({ targetType: "provider", externalProvider: "artistgrid", externalId: "exact-v3", authorityScope: "personal" });
  });
}

const useSettings = (personalMode: string | null, householdMode = "manualOnly", canChoosePersonal = true) => ({
  personalRevision: "listener-revision", householdRevision: "household-revision",
  providers: [{ providerId: "artistgrid", householdMode, personalMode, effectiveMode: canChoosePersonal ? personalMode ?? householdMode : "off", canChoosePersonal }],
});

test("listener switches a source between On, Manual only and Off for themselves", async ({ page }) => {
  await mockMatching(page, { administrator: false,
    providers: [{ id: "artistgrid", name: "ArtistGrid", useMode: "manualOnly", categories: ["metadata", "streaming"], connectionKind: "built_in" }],
  });
  const writes: Record<string, unknown>[] = [];
  await page.route("**/api/admin/provider-accounts", route => json(route, { accounts: [], audienceUsers: [] }));
  await page.route("**/api/admin/providers/use**", async route => {
    if (route.request().method() === "PUT") writes.push(route.request().postDataJSON());
    await json(route, useSettings(writes.length ? "off" : null));
  });
  await page.goto("#/integrations/services/artistgrid");
  const use = page.getByRole("region", { name: "Use this source", exact: true });
  await expect(use.getByRole("radiogroup", { name: "Use this source for everyone" })).toHaveCount(0);
  const mine = use.getByRole("radiogroup", { name: "Use this source for you" });
  await expect(mine.getByRole("radio", { name: "Follow", exact: true })).toHaveAttribute("aria-checked", "true");
  await expect(use.getByText("Follows the setting for everyone.", { exact: true })).toBeVisible();
  await mine.getByRole("radio", { name: "Off", exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0]).toEqual({ mode: "off", scope: "personal", expectedRevision: "listener-revision" });
  await expect(mine.getByRole("radio", { name: "Off", exact: true })).toHaveAttribute("aria-checked", "true");
  await expect(use).toContainText("Off: not used or shown anywhere.");
});

test("match panel prefills the full title despite a legacy search query", async ({ page }) => {
  await mockMatching(page, {
    administrator: false,
    match: { title: "Layered song - Live", searchQuery: "Example artist Layered song" },
  });
  const queries: string[] = [];
  await page.route("**/api/admin/track-matches/targets/*?*", async (route) => {
    queries.push(new URL(route.request().url()).searchParams.get("query") ?? "");
    await json(route, { tracks: [], providers: [] });
  });
  await page.goto("#/library/mappings");
  await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
  const dialog = page.getByRole("dialog", { name: "Layered song - Live" });
  await dialog.getByRole("button", { name: "Search for another version…", exact: true }).click();
  await expect(dialog.getByLabel("Search local library and playable providers"))
    .toHaveValue("Layered song - Live");
  await dialog.getByRole("button", { name: "Search", exact: true }).click();
  await expect.poll(() => queries).toEqual(["Layered song - Live"]);
});

test("match panel keeps the current recording in comparison and alternatives in choices", async ({ page }) => {
  const candidate = matchFixture(false).candidates[0];
  await mockMatching(page, {
    administrator: false,
    match: {
      localTrack: { id: "local-track", backendItemId: "local-track", title: "Current recording", artist: "Example artist" },
      candidates: [
        { ...candidate, title: "Current recording", isCurrent: true },
        { ...candidate, libraryTrackId: "other-track", title: "Alternate recording", isCurrent: false },
      ],
    },
  });
  await page.goto("#/library/mappings");
  await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
  const dialog = page.getByRole("dialog", { name: "Layered song" });
  const candidates = dialog.locator(".candidate-card");
  await expect(dialog.locator(".comparison tbody tr").filter({ has: page.getByRole("rowheader", { name: "Title", exact: true }) })).toContainText("Current recording");
  await expect(candidates).toHaveCount(1);
  await expect(candidates.first()).toContainText("Alternate recording");
  await expect(candidates).not.toContainText("Current recording");
  await expect(candidates.locator(".row-action")).toHaveCount(1);
});

test("match titles mark only flagged ArtistGrid versions and preserve their scoring reason", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  const variants = [
    { id: "best", title: "Preferred version", provider: "artistgrid", isBestVersion: true },
    { id: "ordinary", title: "Ordinary version", provider: "artistgrid", isBestVersion: false },
    { id: "legacy", title: "Older result", provider: "artistgrid" },
    { id: "other", title: "Other source", provider: "qobuz", isBestVersion: true },
    { id: "local", title: "Library result", provider: "", isBestVersion: true },
  ];
  await mockMatching(page, {
    administrator: false,
    providers: [
      { id: "spotify", name: "Spotify", categories: ["metadata"] },
      { id: "artistgrid", name: "ArtistGrid", categories: ["metadata", "streaming"] },
      { id: "qobuz", name: "Qobuz", categories: ["streaming"] },
    ],
    match: { candidates: variants.map(({ id, title, provider, ...flags }) => ({
      ...matchFixture(false).candidates[0], libraryTrackId: id, backendItemId: id, title,
      isLocal: !provider, providerTrackIds: provider ? { [provider]: id } : {}, ...flags,
      reasons: id === "best" ? ["ArtistGrid best version +5"] : [],
    })) },
  });
  await page.route("**/api/admin/track-matches/targets/*?*", route => json(route, {
    tracks: variants.filter(item => new URL(route.request().url()).searchParams.get("includeLocal") === "true" ||
      Boolean(item.provider) === new URL(route.request().url()).pathname.endsWith("/provider"))
      .map(({ id, provider, ...track }) => ({ id, externalId: provider ? id : null, externalProvider: provider || null, ...track })),
    providers: ["artistgrid", "qobuz"],
  }));
  await page.goto("#/library/mappings");
  await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
  const dialog = page.getByRole("dialog", { name: "Layered song" });
  const candidates = dialog.locator(".candidate-card");
  await expect(candidates).toHaveCount(5);
  const marker = candidates.getByRole("img", { name: "Best version on ArtistGrid", exact: true });
  await expect(marker).toHaveCount(1);
  await expect(marker).toHaveAttribute("title", "Best version on ArtistGrid");
  await expect(candidates.filter({ hasText: "Preferred version" }).locator(".row-copy h3")).toContainText("⭐");
  await expect(candidates.filter({ hasText: "Preferred version" }).locator(".candidate-reasons")).toHaveText("ArtistGrid best version +5");
  await dialog.getByRole("button", { name: "Search for another version…", exact: true }).click();
  await dialog.getByRole("button", { name: "Search", exact: true }).click();
  const targets = dialog.locator(".target-results .search-result");
  await expect(targets).toHaveCount(5);
  await expect(targets.getByRole("img", { name: "Best version on ArtistGrid", exact: true })).toHaveCount(1);
  await expect(targets.filter({ hasText: "Preferred version" }).locator(".row-copy h3")).toContainText("⭐");
  await expect(targets.filter({ hasText: "Ordinary version" }).locator(".row-copy h3")).not.toContainText("⭐");
  await expect(targets.filter({ hasText: "Older result" }).locator(".row-copy h3")).not.toContainText("⭐");
  await expect(targets.filter({ hasText: "Other source" }).locator(".row-copy h3")).not.toContainText("⭐");
  await expect(targets.filter({ hasText: "Library result" }).locator(".row-copy h3")).not.toContainText("⭐");
  expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
});

test("listener uses personal authority and cannot choose or edit household", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  let resolution: Record<string, unknown> | null = null;
  await mockMatching(page, {
    administrator: false,
    resolve: async (route) => {
      resolution = route.request().postDataJSON() as Record<string, unknown>;
      await json(route, { success: true });
    },
  });
  await page.goto("#/library/mappings");
  await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
  const dialog = page.getByRole("dialog", { name: "Layered song" });
  const household = dialog.locator(".manual-authority-row").filter({ hasText: "Household" });
  await expect(household).toContainText("Superseded by personal");
  await expect(household.getByRole("button", { name: "Clear household", exact: true })).toBeDisabled();
  await expect(household.getByRole("button", { name: "Clear household and rematch", exact: true })).toBeDisabled();

  await expect(dialog.locator(".evidence-chips")).toContainText("Title matches exactly");
  await expect(dialog.locator(".evidence-chips")).toContainText("Artist matches exactly");
  await expect(dialog.locator(".evidence-chips")).toContainText("Album matches exactly");
  await expect(dialog.locator(".evidence-chips")).toContainText("Length within 1 second");
  await expect(dialog.locator(".evidence-chips")).toContainText("Version matches exactly");
  await dialog.locator(".evidence details summary").click();
  await expect(dialog.locator(".evidence details")).toContainText("versionTags");
  await expect(dialog.locator(".candidate-reasons")).toContainText("Title matches exactly");
  await expect(dialog.locator(".split-trigger")).toContainText("For me");
  await dialog.locator(".split-trigger").click();
  await expect(page.getByRole("menuitemradio", { name: "For the household", exact: true })).toHaveCount(0);
  await expect(page.getByRole("menuitemradio", { name: "For me", exact: true })).toHaveAttribute("aria-checked", "true");
  await page.getByRole("menuitemradio", { name: "For me", exact: true }).click();
  await dialog.getByRole("button", { name: "Close", exact: true }).click();
  await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
  await dialog.getByRole("button", { name: "Use Jellyfin version", exact: true }).click();
  await expect.poll(() => resolution).not.toBeNull();
  expect(resolution).toMatchObject({
    authorityScope: "personal",
    expectedAuthority: { id: "personal-row", revision: 7 },
  });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test("matching sends an actual local library filter as backendLibraryId", async ({ page }) => {
  await mockMatching(page, { administrator: true, match: {
    localTrack: { id: "local-track", backendItemId: "local-track", backendLibraryId: "music-library", title: "Layered song", artist: "Example artist" },
  } });
  await page.goto("#/library/mappings");
  await page.getByLabel("Library scope", { exact: true }).click();
  const filtered = page.waitForRequest((request) => {
    const url = new URL(request.url());
    return url.pathname === "/api/admin/track-matches" &&
      url.searchParams.get("backendLibraryId") === "music-library";
  });
  await page.getByRole("option", { name: "music-library", exact: true }).click();
  const query = new URL((await filtered).url()).searchParams;
  expect(query.has("libraryScopeId")).toBe(false);
});

test("administrator household save binds the household row while personal is effective", async ({ page }) => {
  let requestBody: Record<string, unknown> | null = null;
  await mockMatching(page, {
    administrator: true,
    resolve: async (route) => {
      requestBody = route.request().postDataJSON() as Record<string, unknown>;
      await json(route, { error: "The household authority changed on the server." }, 409);
    },
  });
  await page.goto("#/library/mappings");
  await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
  const dialog = page.getByRole("dialog", { name: "Layered song" });
  await expect(dialog.locator(".split-trigger")).toContainText("For the household");
  await dialog.locator(".split-trigger").click();
  await expect(page.getByRole("menuitemradio", { name: "For the household", exact: true })).toHaveAttribute("aria-checked", "true");
  await page.getByRole("menuitemradio", { name: "For the household", exact: true }).click();
  await dialog.locator(".candidate-card .row-action").click();
  expect(requestBody).toBeNull();
  await dialog.getByRole("button", { name: "Use Jellyfin version", exact: true }).click();

  await expect.poll(() => requestBody).not.toBeNull();
  expect(requestBody).toMatchObject({
    authorityScope: "household",
    expectedAuthority: { id: "household-row", revision: 11 },
  });
  await expect(dialog.getByRole("alert")).toContainText("household authority changed");
});

test("authority clear and rematch send their displayed scope and revision", async ({ page }) => {
  const requests: Request[] = [];
  await mockMatching(page, {
    administrator: true,
    authorityAction: async (route) => {
      requests.push(route.request());
      if (route.request().method() === "DELETE") return route.fulfill({ status: 204, body: "" });
      await json(route, { rematched: true, state: "accepted" });
    },
  });
  await page.goto("#/library/mappings");
  await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
  const panel = page.getByRole("dialog", { name: "Layered song", exact: true });
  const household = panel.locator(".manual-authority-row").filter({ hasText: "Household" });
  await household.getByRole("button", { name: "Clear household and rematch", exact: true }).click();
  const rematchDialog = page.getByRole("alertdialog", { name: "Clear household and rematch?" });
  await expect(rematchDialog).toContainText("personal decision remains effective");
  await rematchDialog.getByRole("button", { name: "Clear household and rematch", exact: true }).click();
  await expect.poll(() => requests.length).toBe(1);
  expect(new URL(requests.at(0)!.url()).pathname).toContain(
    "/displayed-snapshot/manual-authorities/household-row/rematch",
  );
  expect(requests.at(0)!.postDataJSON()).toEqual({
    kind: "local_match",
    expectedRevision: 11,
    authorityScope: "household",
  });

  await expect(panel).not.toBeVisible();
  await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
  const personal = panel.locator(".manual-authority-row").filter({ hasText: "Personal" });
  await personal.getByRole("button", { name: "Clear personal", exact: true }).click();
  const clearDialog = page.getByRole("alertdialog", { name: "Clear personal decision?" });
  await expect(clearDialog).toContainText("household decision beneath it will become effective");
  await clearDialog.getByRole("button", { name: "Clear personal", exact: true }).click();
  await expect.poll(() => requests.length).toBe(2);
  const clearUrl = new URL(requests.at(1)!.url());
  expect(clearUrl.pathname).toContain("/displayed-snapshot/manual-authorities/personal-row");
  expect(clearUrl.searchParams.get("authorityScope")).toBe("personal");
  expect(clearUrl.searchParams.get("kind")).toBe("provider_match");
  expect(clearUrl.searchParams.get("expectedRevision")).toBe("7");
});
