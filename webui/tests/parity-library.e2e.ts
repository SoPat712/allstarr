import { expect, test } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";
import { responses, routeRelease, mockApi } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

test("Full rematch replaces automatic decisions in durable batches and protects manual choices", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.goto("#/library/mappings");

  await page.getByRole("button", { name: "Rematch all" }).click();
  const dialog = page.getByRole("dialog", { name: "Rematch every automatic decision" });
  await expect(dialog).toBeVisible();
  const dialogBox = await dialog.boundingBox();
  expect(dialogBox).toBeTruthy();
  expect(dialogBox!.x).toBeLessThanOrEqual(1);
  expect(Math.abs(dialogBox!.width - 390)).toBeLessThanOrEqual(1);
  expect(dialogBox!.height).toBeLessThanOrEqual(844 * 0.92 + 1);
  await expect(dialog).toContainText("115 tracks will be reprocessed");
  await expect(dialog).toContainText("25 tracks at a time");
  await expect(dialog.getByText("Protected manual").locator("..").getByRole("definition")).toHaveText("5");
  const apply = dialog.getByRole("button", { name: "Rematch 115 tracks" });
  await expect(apply).toBeDisabled();
  await dialog.getByRole("checkbox").check();
  await expect(apply).toBeEnabled();
  await expect(apply).toBeInViewport();
  const request = page.waitForRequest((item) =>
    item.method() === "POST" && item.url().endsWith("/api/admin/track-matches/rematch-all/apply"));
  await apply.click();
  expect((await request).postDataJSON()).toEqual({ confirmationId: "b".repeat(64) });
  await expect(dialog).toBeHidden();
  await expect(page.getByText("Rematching 115 tracks in durable batches.")).toBeVisible();
});

test("Manual and Rejected tabs expose explicit rematch and delete controls", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.route("**/api/admin/track-matches?*", (route) => {
    const state = new URL(route.request().url()).searchParams.get("state");
    const authority = state === "manual_rejections"
      ? {
          id: "rejection", kind: "rejection", revision: 2, authoritySnapshotId: "snapshot",
          reason: "Wrong recording", createdAt: "2026-01-01T00:00:00Z", effective: true,
        }
      : {
          id: "authority", kind: "local_match", revision: 3, authoritySnapshotId: "snapshot",
          reason: "Selected from the local library", createdAt: "2026-01-01T00:00:00Z", effective: true,
        };
    const visible = state === "manual_matches" || state === "manual_rejections";
    return route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        matches: visible ? [{
          externalSnapshotId: "snapshot", providerId: "lumen-audio",
          state: state === "manual_rejections" ? "rejected" : "pinned",
          decisionSource: "manual_authority", confidence: 1, threshold: .9,
          title: "Manual song", artist: "Artist", album: "Album", durationMilliseconds: 180_000,
          manualAuthorities: [authority], providerIdentities: [], reasons: [], warnings: [], candidates: [],
          localTrack: state === "manual_matches" ? {
            id: "local", backendItemId: "local", title: "Manual song", artist: "Artist",
            album: "Album", durationMilliseconds: 180_000,
          } : null,
        }] : [],
        stats: {
          total: 2, matched: 1, accepted: 1, unresolved: 0, suggested: 0, review: 0,
          rejected: 1, attention: 0, manualMatches: 1, manualRejections: 1,
        },
        pagination: { page: 1, pageSize: 50, total: visible ? 1 : 0, totalPages: 1 },
      }),
    });
  });
  await page.route("**/api/admin/track-matches/snapshot/manual-authorities/authority/rematch", (route) =>
    route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ rematched: true, state: "accepted" }) }));
  await page.route("**/api/admin/track-matches/snapshot/manual-authorities/rejection?*", (route) =>
    route.fulfill({ status: 204, body: "" }));
  await page.goto("#/library/mappings");

  await page.getByRole("radio", { name: "Chosen by you 1", exact: true }).click();
  await expect(page.locator(".mapping-row .state-pill")).toHaveText("By you");
  await expect(page.locator(".mapping-row .state-pill")).toHaveAttribute("data-tone", "neutral");
  await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
  const manualMatch = page.locator(".manual-authority-row");
  await expect(manualMatch).toContainText("Manual Jellyfin match");
  const rematch = page.waitForRequest((request) =>
    request.method() === "POST" && request.url().endsWith("/manual-authorities/authority/rematch"));
  await manualMatch.getByRole("button", { name: "Clear personal and rematch" }).click();
  const rematchDialog = page.getByRole("alertdialog", { name: "Clear personal and rematch?" });
  await expect(rematchDialog).toContainText("matching algorithm recalculates");
  await rematchDialog.getByRole("button", { name: "Clear personal and rematch" }).click();
  expect((await rematch).postDataJSON()).toEqual({ kind: "local_match", expectedRevision: 3, authorityScope: "personal" });

  await page.getByRole("radio", { name: "Rejected 1", exact: true }).click();
  await expect(page.locator(".mapping-row .state-pill")).toHaveText("Rejected");
  await expect(page.locator(".mapping-row .state-pill")).toHaveAttribute("data-tone", "stop");
  await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
  const manualRejection = page.locator(".manual-authority-row");
  await expect(manualRejection).toContainText("Manual rejection");
  const deletion = page.waitForRequest((request) =>
    request.method() === "DELETE" && request.url().includes("/manual-authorities/rejection?"));
  await manualRejection.getByRole("button", { name: "Clear personal", exact: true }).click();
  const deleteDialog = page.getByRole("alertdialog", { name: "Clear personal decision?" });
  await deleteDialog.getByRole("button", { name: "Clear personal", exact: true }).click();
  const deleteUrl = new URL((await deletion).url());
  expect(deleteUrl.searchParams.get("kind")).toBe("rejection");
  expect(deleteUrl.searchParams.get("expectedRevision")).toBe("2");
  expect(deleteUrl.searchParams.get("authorityScope")).toBe("personal");
});

test("Matches deep links preserve full title, provider search and review decisions", async ({ page }) => {
  await mockApi(page);
  const searches: Record<string, string>[] = [];
  await page.route("**/api/admin/track-matches/targets/provider?*", route => {
    searches.push(Object.fromEntries(new URL(route.request().url()).searchParams));
    return route.fulfill({ json: { tracks: [{ id: "provider-track", externalId: "provider-track", externalProvider: "lumen-audio",
      title: "Kiss Me More", artist: "Doja Cat", album: "Planet Her", confidence: .967, durationMilliseconds: 208000 }], providers: ["lumen-audio"] } });
  });
  await page.goto("#/library/mappings?search=Test%20song&review=snapshot");
  const panel = page.getByRole("dialog", { name: "Test song - Remix", exact: true });
  await expect(panel).toBeVisible();
  await panel.getByRole("button", { name: "Search for another version…", exact: true }).click();
  const input = panel.getByLabel("Search local library and playable providers");
  await expect(input).toHaveValue("Test song - Remix");
  await expect(panel).toContainText("US-AAA-26-00001");
  await expect(panel).not.toContainText("MusicBrainz album");
  await input.fill("Kiss Me More");
  await panel.getByRole("button", { name: "Search", exact: true }).click();
  await expect(panel.locator(".target-results .search-result")).toHaveCount(1);
  expect(searches).toEqual([{ query: "Kiss Me More", externalSnapshotId: "snapshot", limit: "50", includeLocal: "true" }]);
  await expect(panel.locator(".target-results .state-pill")).toHaveText("97%");
  await panel.locator(".target-results .row-action").click();
  await expect(panel.getByRole("table")).toContainText("Kiss Me More");
  const write = page.waitForRequest(request => request.url().endsWith("/snapshot/resolve") && request.method() === "POST");
  await panel.getByRole("button", { name: "Use Lumen Audio version", exact: true }).click();
  expect((await write).postDataJSON()).toMatchObject({ targetType: "provider", externalProvider: "lumen-audio", externalId: "provider-track" });
  await expect(panel).toBeHidden();
  const request = page.waitForRequest(request => new URL(request.url()).searchParams.get("sort") === "confidence_desc");
  await page.getByRole("button", { name: "Sort matches", exact: true }).click();
  await page.getByRole("option", { name: "Most sure first", exact: true }).click();
  await request;
  await page.getByRole("radio", { name: "Needs a look 1", exact: true }).click();
  await expect(page.locator(".mapping-row")).toHaveCount(1);
});

test("Match review pages from cache and acts on several selected songs", async ({ page }) => {
  await mockApi(page);
  const match = (id: string, title: string) => ({
    externalSnapshotId: id, providerId: "lumen-audio", state: "suggested", decisionSource: "automatic", confidence: 0.82,
    threshold: 0.9, title, artist: "Artist", album: "Album", durationMilliseconds: 180_000, providerIdentities: [],
    reasons: [], warnings: [], candidates: [], manualAuthorities: [],
  });
  const pages: Record<string, ReturnType<typeof match>[]> = { "1": [match("one", "First song"), match("two", "Second song")], "2": [match("three", "Third song")] };
  let holdFirstPage = false;
  const rematched: string[] = [];
  await page.route((url) => url.pathname === "/api/admin/track-matches/selection", async (route) =>
    route.fulfill({ json: { matches: Object.values(pages).flat() } }));
  await page.route((url) => url.pathname === "/api/admin/track-matches", async (route) => {
    const requested = new URL(route.request().url()).searchParams.get("page") ?? "1";
    if (requested === "1" && holdFirstPage) await new Promise(() => {});
    await route.fulfill({ json: { matches: pages[requested], pagination: { page: Number(requested), pageSize: 2, total: 3, totalPages: 2 },
      stats: { total: 3, playlistCount: 1, matched: 0, accepted: 0, unresolved: 0, suggested: 3, review: 3, rejected: 0, attention: 3 } } });
  });
  await page.route("**/api/admin/track-matches/*/rematch", (route) => {
    rematched.push(new URL(route.request().url()).pathname.split("/")[4]);
    return route.fulfill({ json: { rematched: true, state: "suggested" } });
  });
  await page.addInitScript(() => localStorage.setItem("matches.filter", "all"));
  await page.goto("#/library/mappings");
  await expect(page.getByText("First song", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Next" }).click();
  await expect(page.getByText("Third song", { exact: true })).toBeVisible();
  holdFirstPage = true;
  await page.getByRole("button", { name: "Previous" }).click();
  await expect(page.getByText("First song", { exact: true })).toBeVisible({ timeout: 1000 });
  holdFirstPage = false;

  await page.getByRole("button", { name: "Select", exact: true }).click();
  await page.getByRole("checkbox", { name: "Select all songs on this page" }).check();
  const bar = page.getByRole("toolbar", { name: "Actions for selected songs" });
  await expect(bar).toContainText("2 selected");
  await page.getByRole("checkbox", { name: "Select Second song" }).uncheck();
  await expect(page.getByRole("checkbox", { name: "Select all songs on this page" })).toHaveJSProperty("indeterminate", true);
  await page.getByRole("checkbox", { name: "Select Second song" }).check();
  await bar.getByRole("button", { name: "Rematch", exact: true }).click();
  await expect.poll(() => rematched.toSorted()).toEqual(["one", "two"]);
  await expect(page.getByRole("status").filter({ hasText: "Rematched 2 songs." })).toBeVisible();
  await expect(bar).toHaveCount(0);
});

test("Mappings keep phone filters, differences and menus readable", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.goto("#/library/mappings");
  const filters = page.getByRole("radiogroup", { name: "Match filters" });
  await expect(filters).toBeVisible();
  // On a phone the filters stay one row that scrolls sideways.
  await expect.poll(() => filters.getByRole("radio").evaluateAll(items => new Set(items.map(item => Math.round(item.getBoundingClientRect().top))).size)).toBe(1);
  await expect.poll(() => filters.evaluate(rail => rail.scrollWidth > rail.clientWidth && getComputedStyle(rail).overflowX === "auto")).toBe(true);
  await expect.poll(() => filters.getByRole("radio").evaluateAll(items => items.every(item => item.getBoundingClientRect().height >= 44))).toBe(true);
  const row = page.locator(".mapping-row").first();
  await expect(row.locator(".item-row")).toContainText("Not Remix");
  const more = row.getByRole("button", { name: "More actions for Test song - Remix" });
  await expect(more).toBeInViewport();
  await more.click();
  await expect(page.getByRole("menuitem", { name: "Rematch", exact: true })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Report a problem", exact: true })).toBeVisible();
  await page.keyboard.press("Escape");
});

test("Cached and Kept keep media facts and actions readable on mobile", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.goto("#/library/cached");
  await expect(page.getByRole("button", { name: "Source", exact: true })).toContainText("All sources");
  const cached = page.locator(".download-row");
  await expect(cached.getByRole("button", { name: "Keep", exact: true })).toBeVisible();
  await expect(cached.getByRole("link", { name: "Download", exact: true })).toHaveCount(0);
  await cached.getByRole("button", { name: /Open song details/ }).click();
  const panel = page.getByRole("dialog");
  await expect(panel).toContainText("900 kbps");
  await expect(panel).toContainText("1000 KiB");
  await expect(panel.getByLabel("Song facts", { exact: true }).getByText("Last played", { exact: true })).toHaveCount(0);
  await expect(panel.getByLabel("Song facts", { exact: true }).getByText("Not provided", { exact: true })).toHaveCount(0);
  await expect(panel.locator(".settings-fold")).not.toHaveAttribute("open");
  await expect(panel.getByRole("link", { name: "Download", exact: true })).toBeVisible();
  await panel.getByRole("button", { name: "Close", exact: true }).click();
  const title = "Stay Alive (Prod. SUGA of BTS)";
  const artist = "Jung Kook";
  const album = "Stay Alive (Original Soundtrack from 7FATES: CHAKHO)";
  const recordingId = "10000000-0000-0000-0000-000000000003";
  await page.route("**/api/admin/downloads?storage=kept", route => route.fulfill({ json: {
    storage: "kept", files: [{
      path: "/managed/song.flac", storage: "kept", title, artist, album, fileName: "song.flac",
      size: 1_024_000, sizeFormatted: "1000 KiB", codec: "FLAC", bitDepth: 16, sampleRateHz: 44_100,
      lastModified: "2026-01-01", explicitlyKept: false, removable: true,
    }], count: 1, managedCount: 1, diagnosticCount: 0, totalSize: 1_024_000, totalSizeFormatted: "1000 KiB",
  } }));
  await page.route("**/api/admin/library-promotions/kept", route => route.fulfill({ json: {
    items: [{ path: "/managed/song.flac", recordingId, backendLibraryId: "music", inLibrary: false }],
    backendLibraryId: "music", rootId: null, alreadyInLibrary: 0,
  } }));
  await page.route("**/api/admin/library-promotions/preview", route => route.fulfill({ json: {
    album: { title: album, artist, songCount: 1 }, canAddDirectly: true, alreadyInLibrary: 0,
    destinationPath: "/music/Jung Kook/Stay Alive/01 - Stay Alive.flac",
    bestCopies: [{ provider: "qobuz", codec: "flac", bitDepth: 16, sampleRate: 44100, verdict: "meets-your-floor" }],
    profileSteps: [],
  } }));
  await page.goto("#/library/kept");
  const kept = page.getByRole("region", { name: "Kept songs", exact: true });
  const row = kept.locator(".library-download-row");
  await expect(row.locator(".song-title > strong")).toBeVisible();
  await expect(row.locator(".song-title > strong")).toHaveText(title);
  await expect(row.locator(".song-description > small:not(.phone-facts)")).toHaveText(`${artist} · ${album}`);
  await expect(row.locator(".phone-facts")).toContainText("FLAC 16/44.1");
  await expect(kept.getByRole("button", { name: "Keep", exact: true })).toHaveCount(0);
  const expectReadableRow = async () => {
    expect(await row.locator(".song-title > strong, .song-description > small").evaluateAll(elements =>
      elements.filter(element => element.scrollWidth > element.clientWidth).map(element => ({ text: element.textContent, width: element.clientWidth, content: element.scrollWidth })))).toEqual([]);
    await expectContainedMobileGeometry(page);
  };
  await expectReadableRow();
  const opener = row.getByRole("button", { name: `Open song details for ${title}`, exact: true });
  await opener.focus();
  await opener.press("Enter");
  await expect(panel).toHaveAccessibleName(album);
  await expect(panel).toContainText("Qobuz · FLAC 16-bit / 44.1 kHz");
  await expect(panel).toContainText("01 - Stay Alive.flac");
  await expect(panel.getByRole("button", { name: "Only this song", exact: true })).toBeVisible();
  await expect(panel.getByRole("button", { name: "Add album to library", exact: true })).toBeVisible();
  await expect(panel.getByRole("link", { name: "Download", exact: true })).toHaveAttribute("href", /downloads\/file/);
  await expectContainedMobileGeometry(page);
  await panel.getByRole("button", { name: "Close", exact: true }).click();
  await kept.getByRole("button", { name: "Add selected to library", exact: true }).click();
  await expect(row.getByRole("checkbox", { name: `Select ${title}`, exact: true })).toBeVisible();
  await expectReadableRow();
  await expect(row.getByRole("link", { name: "Download", exact: true })).toHaveAttribute("href", /downloads\/file/);
  await row.getByRole("button", { name: `Actions for ${title}`, exact: true }).click();
  await page.getByRole("menuitem", { name: "Remove", exact: true }).click();
  const removal = page.getByRole("alertdialog", { name: "Remove this song?", exact: true });
  await expect(removal).toContainText("audio file and its lyrics");
  await expectContainedMobileGeometry(page);
  await removal.getByRole("button", { name: "Cancel", exact: true }).click();
});

test("Cached owns track storage and retention controls", async ({ page }) => {
  await mockApi(page);
  let saved: Record<string, string> | null = null;
  await page.route("**/api/admin/config", (route) => {
    if (route.request().method() === "POST") {
      saved = route.request().postDataJSON().updates;
      return route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({ message: "Runtime configuration updated.", updatedKeys: Object.keys(saved ?? {}) }),
      });
    }
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify(responses["/api/admin/config"]),
    });
  });

  await page.goto("#/library/cached");
  await expect(page.getByText("Playback cache on", { exact: true })).toHaveCount(0);
  await page.getByRole("link", { name: "Cache settings", exact: true }).click();
  await expect(page.locator('input[name="CACHE_DURATION_HOURS"]')).toHaveValue("24");
  await page.getByText("Advanced: cache times, file checks and cleanup", { exact: true }).click();
  await expect(page.locator('input[name="CACHE_TRANSCODE_MINUTES"]')).toHaveValue("60");
  await page.locator('input[name="CACHE_DURATION_HOURS"]').fill("48");
  await page.locator('input[name="CACHE_DURATION_HOURS"]').blur();
  await expect.poll(() => saved).toEqual({ CACHE_DURATION_HOURS: "48" });
  await expect(page.getByText("Saved · Keep cached songs for (hours)", { exact: true })).toBeVisible();

  await page.goto("#/settings/general");
  await expect(page.getByRole("button", { name: "Storage mode" })).toHaveCount(0);
  await expect(page.getByText("Track cache hours")).toHaveCount(0);
  await expect(page.getByText("Transcode cache minutes")).toHaveCount(0);
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Cached explains disabled playback caching at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript((value) => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    let mode = "Permanent";
    let hours = 1;
    let saved: Record<string, string> | null = null;
    const writes: string[] = [];
    page.on("request", request => {
      if (request.method() === "POST") writes.push(new URL(request.url()).pathname);
    });
    await page.route("**/api/admin/config", route => {
      if (route.request().method() === "POST") {
        saved = route.request().postDataJSON().updates;
        if (saved!.STORAGE_MODE) mode = saved!.STORAGE_MODE;
        if (saved!.CACHE_DURATION_HOURS) hours = Number(saved!.CACHE_DURATION_HOURS);
        return route.fulfill({ json: { message: "Runtime configuration updated.", requiresRestart: false } });
      }
      return route.fulfill({ json: {
        ...(responses["/api/admin/config"] as object), library: { storageMode: mode, cacheDurationHours: hours },
      } });
    });
    await page.route("**/api/admin/downloads?storage=cache", route => route.fulfill({ json: {
      storage: "cache", files: [], count: 0, managedCount: 0, diagnosticCount: 0, totalSize: 0, totalSizeFormatted: "0 B",
    } }));
    await page.goto("#/library/cached");
    await expect(page.getByText("Playback cache off", { exact: true })).toBeVisible();
    await expect(page.getByText(/New playback is not being added to Cached/)).toBeVisible();
    await page.getByRole("link", { name: "Cache settings", exact: true }).click();
    await expect(page.getByRole("switch", { name: "Cache played songs", exact: true })).not.toBeChecked();
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: test.info().outputPath(`cache-off-${width}-${theme}.png`) });
    await page.getByRole("switch", { name: "Cache played songs", exact: true }).check();
    await expect.poll(() => saved).toEqual({ STORAGE_MODE: "Cache" });
    await expect(page.getByRole("switch", { name: "Cache played songs", exact: true })).toBeEnabled();
    await page.locator('input[name="CACHE_DURATION_HOURS"]').fill("72");
    await page.locator('input[name="CACHE_DURATION_HOURS"]').blur();
    await expect.poll(() => saved).toEqual({ CACHE_DURATION_HOURS: "72" });
    await expect(page.locator('input[name="CACHE_DURATION_HOURS"]')).toHaveValue("72");
    expect(writes).toEqual(["/api/admin/config", "/api/admin/config"]);
    const dismiss = page.getByRole("button", { name: "Dismiss notification", exact: true });
    while (await dismiss.count()) await dismiss.first().click();
    await page.locator('input[name="CACHE_DURATION_HOURS"]').click();
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    if (width === 390) {
      await page.locator('input[name="CACHE_DURATION_HOURS"]').scrollIntoViewIfNeeded();
      const save = await page.locator('input[name="CACHE_DURATION_HOURS"]').boundingBox();
      const navigation = await page.locator(".mobile-navigation").boundingBox();
      expect(save!.y + save!.height).toBeLessThanOrEqual(navigation!.y);
    }
    await page.screenshot({ path: test.info().outputPath(`cache-on-${width}-${theme}.png`) });
  });
}

test("mapping and playlist review actions communicate work instead of silently ignoring input", async ({ page }) => {
  await mockApi(page);
  const mappingRematch = routeRelease();
  await page.route("**/api/admin/track-matches/snapshot/rematch", async (route) => {
    await mappingRematch.promise;
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ rematched: true, state: "suggested" }),
    });
  });
  await page.goto("#/library/mappings");
  const mappingRow = page.locator(".mapping-row").first();
  await mappingRow.getByRole("button", { name: /More actions/ }).filter({ visible: true }).click();
  await page.getByRole("menuitem", { name: "Rematch", exact: true }).click();
  await expect(mappingRow.getByRole("button", { name: /More actions/ }).filter({ visible: true })).toBeDisabled();
  mappingRematch.release();
  await expect(page.getByText("Test song - Remix rematched.", { exact: true })).toBeVisible();

  const matchDetails = routeRelease();
  await page.route("**/api/admin/track-matches?*", async (route) => {
    const snapshot = new URL(route.request().url()).searchParams.get("externalSnapshotId");
    if (snapshot === "snapshot") await matchDetails.promise;
    return route.fallback();
  });
  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
  const playlist = page.getByRole("dialog", { name: "Test playlist" });
  await playlist.getByRole("button", { name: "Open mapping details for Test song" }).click();
  await expect(playlist.getByRole("button", { name: "Loading mapping details for Test song" })).toBeDisabled();
  await expect(playlist.getByText("Loading Test song…", { exact: true })).toBeVisible();
  matchDetails.release();
  await expect(page.getByRole("dialog", { name: "Test song" })).toBeVisible();
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Match review dialog is readable and keeps its decision controls in reach — ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.addInitScript((value) => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    await page.goto("#/library/playlists");
    await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
    await page.getByRole("button", { name: "Open mapping details for Test song" }).click();
    const dialog = page.getByRole("dialog", { name: "Test song" });
    await expect(dialog).toBeVisible();
    await page.screenshot({ path: `test-results/match-dialog-${width}-${theme}.png` });
    await dialog.evaluate((element) => element.scrollTo(0, element.scrollHeight));
    await page.screenshot({ path: `test-results/match-dialog-${width}-${theme}-end.png` });
  });
}
