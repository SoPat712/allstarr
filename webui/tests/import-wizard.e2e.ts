import { expect, test, type Page, type Route } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const userId = "11111111-1111-4111-8111-111111111111";
const accountId = "22222222-2222-4222-8222-222222222222";
const runId = "33333333-3333-4333-8333-333333333333";
const jobId = "44444444-4444-4444-8444-444444444444";
const snapshotId = "55555555-5555-4555-8555-555555555555";
const account = { id: accountId, providerId: "spotify", displayName: "My Spotify", scope: "Personal",
  ownerUserId: userId, ownerDisplayName: "Alex", enabled: true, canManage: true, revision: 1,
  secret: { configured: true, revoked: false }, createdAt: "2026-10-24T12:00:00Z", updatedAt: "2026-10-24T12:00:00Z" };
const liked = { sourceId: "liked-songs", name: "Liked Songs", count: 3, kind: "liked-songs", support: "available" };
const savedAlbums = { sourceId: "saved-albums", name: "Saved albums", count: 2, kind: "saved-albums", support: "available" };
const followedArtists = { sourceId: "followed-artists", name: "Followed artists", count: 2, kind: "followed-artists", support: "available" };
const evening = { sourceId: "evening", name: "Evening mix", count: 4, kind: "playlist", support: "available" };
const weekend = { sourceId: "weekend", name: "Weekend", count: 2, kind: "playlist", support: "available" };
type Counts = { selected: number; local: number; streamed: number; accepted: number; review: number; unmatched: number; unsupported: number; skipped: number };
const counts = (overrides: Partial<Counts> = {}): Counts => ({ selected: 0, local: 0, streamed: 0, accepted: 0, review: 0, unmatched: 0, unsupported: 0, skipped: 0, ...overrides });
type Write = { path: string; body: Record<string, unknown> };
type RunState = "choosing" | "collecting" | "review" | "waiting" | "cancelled" | "applying" | "completed";

async function reply(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}

async function mockImport(page: Page, accounts = [account], withCollections = false) {
  const fixture = { state: "choosing" as RunState, phase: "collecting", fixed: false, appliedFixed: false, createError: false,
    albumChosen: false, artistSkipped: false, collectionChoiceError: false, unsupported: [] as { kind: string }[],
    reads: [] as string[], writes: [] as Write[], forbidden: [] as string[] };
  await page.addInitScript(() => {
    class FixtureStream {
      static instances: FixtureStream[] = [];
      listeners = new Map<string, EventListener[]>();
      constructor() { FixtureStream.instances.push(this); }
      addEventListener(type: string, listener: EventListener) { this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]); }
      close() { FixtureStream.instances = FixtureStream.instances.filter(item => item !== this); }
    }
    Object.defineProperty(window, "EventSource", { value: FixtureStream });
    Object.assign(window, { __emitMigrationUpdate: (id: string) => {
      const event = { data: JSON.stringify({ resource: "migration", resourceId: id, revision: Date.now() }), lastEventId: `migration-${Date.now()}` };
      for (const stream of FixtureStream.instances) for (const listener of stream.listeners.get("update") ?? []) listener(event as unknown as Event);
    } });
  });
  const summary = () => {
    const likedCounts = fixture.state === "collecting" ? counts({ selected: 1, local: 1, accepted: 1 }) : counts({ selected: 3, local: fixture.fixed ? 2 : 1, streamed: 1, accepted: fixture.fixed ? 3 : 2, review: fixture.fixed ? 0 : 1 });
    const eveningCounts = fixture.state === "collecting" ? counts({ selected: 1, unmatched: 1 }) : counts({ selected: 4, local: 1, streamed: 1, accepted: 2, unmatched: 1, unsupported: 1 });
    const result = { id: runId, run: { id: runId, providerAccountId: accountId, state: fixture.state, phase: fixture.phase, jobId },
      collections: [
        { id: "likes", sourceId: liked.sourceId, collection: { kind: liked.kind, name: liked.name, expected: 3, read: fixture.state === "collecting" ? 1 : 3, state: fixture.state === "completed" ? "applied" : fixture.state === "collecting" ? "collecting" : "review", keepInSync: false, error: fixture.state === "waiting" ? "Progress saved. Restore the same Spotify connection or destination access, then resume." : null, receiptRunId: runId }, counts: likedCounts, unread: fixture.state === "collecting" ? 2 : 0 },
        { id: "mix", sourceId: evening.sourceId, collection: { kind: evening.kind, name: evening.name, expected: 4, read: fixture.state === "collecting" ? 1 : 4, state: fixture.state === "completed" ? "applied" : fixture.state === "collecting" ? "matching" : "review", keepInSync: true, error: fixture.state === "waiting" ? "Progress saved. Restore the same Spotify connection or destination access, then resume." : null, receiptRunId: runId }, counts: eveningCounts, unread: fixture.state === "collecting" ? 3 : 0 },
      ], total: fixture.state === "collecting" ? counts({ selected: 2, local: 1, accepted: 1, unmatched: 1 }) : counts({ selected: 7, local: fixture.fixed ? 3 : 2, streamed: 2, accepted: fixture.fixed ? 5 : 4, review: fixture.fixed ? 0 : 1, unmatched: 1, unsupported: 1 }), provisional: fixture.state === "collecting" };
    if (withCollections) {
      for (const [choice, id, resolved] of [[savedAlbums, "albums", fixture.albumChosen], [followedArtists, "artists", fixture.artistSkipped]] as const) {
        const skipped = id === "artists" && resolved;
        const added = 1 + Number(resolved && !skipped);
        const resourceCounts = counts({ selected: 2, local: added, accepted: added, review: resolved ? 0 : 1, skipped: Number(skipped) });
        result.collections.push({ id, sourceId: choice.sourceId, collection: { kind: choice.kind, name: choice.name, expected: 2, read: 2, state: fixture.state === "completed" ? "applied" : "review", keepInSync: false, error: null, receiptRunId: runId }, counts: resourceCounts, unread: 0 });
        for (const key of Object.keys(result.total) as (keyof Counts)[]) result.total[key] += resourceCounts[key];
      }
    }
    return result;
  };
  const occurrence = (collectionId: string, position: number, title: string | null, state: number, status: number, kind: number | null, added = false) => ({
    collectionId, sourceEntryId: `${collectionId}-${position}`, position, externalSnapshotId: title === "Housewife" ? snapshotId : `${collectionId}-snapshot-${position}`,
    metadata: { title, artists: [title === "Housewife" ? "Tate McRae" : "Mira"], artworkUrl: null },
    preview: { state, status, resolvedRoute: kind === null ? null : { kind } },
    receipt: fixture.state === "completed" && !(title === "Housewife" && !fixture.appliedFixed) ? { outcome: added ? 2 : kind === null ? 4 : 1, outcomeCode: kind === null ? "unresolved" : "included" } : null,
  });
  await page.route("**/*", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (!["127.0.0.1", "localhost"].includes(url.hostname)) { fixture.forbidden.push(url.origin); return route.abort(); }
    if (!path.startsWith("/api/admin/")) return route.continue();
    if (request.method() === "GET") fixture.reads.push(`${path}${url.search}`);
    else fixture.writes.push({ path, body: request.postData() ? request.postDataJSON() : {} });
    if (path === "/api/admin/auth/me") return reply(route, { authenticated: true, backend: "Jellyfin", features: { intelligence: false }, user: { id: "backend-alex", allstarrUserId: userId, name: "Alex", isAdministrator: false } });
    if (path === "/api/admin/auth/oidc/status") return reply(route, { enabled: false });
    if (path === "/api/admin/onboarding/status") return reply(route, { completed: true, setupOpen: false, shouldRedirectToSetup: false, schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"], completionSource: "setup-guide", revision: 1, recoveryNotices: [], migration: { available: false, completed: false, firstRun: false } });
    if (path === "/api/admin/ui/schema") return reply(route, { activeBackend: "Jellyfin", settings: [], configSections: [], priorityGroups: [], providers: [{ id: "spotify", name: "Spotify", categories: ["playlist"], requiresAccount: true, description: "Connect Spotify to use your playlists.", useMode: "automatic", settings: [] }] });
    if (path === "/api/admin/provider-accounts") return reply(route, { accounts, audienceUsers: [], listenersCanConnectOwnAccounts: true });
    if (path === "/api/admin/providers/status") return reply(route, []);
    if (["/api/admin/ui/provider-summaries", "/api/admin/providers/use"].includes(path)) return reply(route, { providers: [] });
    if (path === "/api/admin/provider-diagnostics/deep-stream/latest") return reply(route, { measurements: [] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    if (path === "/api/admin/migrations" && request.method() === "POST") return fixture.createError ? reply(route, { error: "Spotify is temporarily unavailable. Try again." }, 503) : reply(route, { id: runId }, 202);
    if (path === `/api/admin/migrations/${runId}/discover`) return reply(route, url.searchParams.has("cursor")
      ? { items: [liked, weekend], nextCursor: null, unsupported: fixture.unsupported }
      : { items: [evening, ...(withCollections ? [followedArtists, savedAlbums] : []), liked], nextCursor: "next-playlists", unsupported: fixture.unsupported });
    if (path.endsWith("/review/albums/albums-1/choose") || path.endsWith("/review/artists/artists-1/choose")) {
      if (fixture.collectionChoiceError) return reply(route, { error: "Could not save." }, 503);
      if (path.includes("/albums/")) fixture.albumChosen = true; else fixture.artistSkipped = true;
      return route.fulfill({ status: 204, body: "" });
    }
    if (path === `/api/admin/migrations/${runId}/choose`) { fixture.state = "collecting"; return route.fulfill({ status: 202, body: "" }); }
    if (path === `/api/admin/migrations/${runId}` || path === `/api/admin/migrations/${runId}/summary`) return reply(route, summary());
    if (path === `/api/admin/migrations/${runId}/review`) {
      const id = url.searchParams.get("collectionId");
      if (["albums", "artists"].includes(id ?? "")) {
        const album = id === "albums";
        return reply(route, [0, 1].map(position => ({
          collectionId: id, sourceEntryId: `${id}-${position}`, position, externalSnapshotId: "",
          metadata: { title: album ? position ? "Evening" : "Daylight" : position ? "Mira" : "Noor", artists: album ? ["Mira"] : [], artworkUrl: null, year: album ? 2020 : null, trackCount: album ? 12 : null },
          preview: null, receipt: null,
          resource: { kind: album ? "saved-albums" : "followed-artists", state: position === 0 || album && fixture.albumChosen ? "accepted" : !album && fixture.artistSkipped ? "skipped" : "review", applied: fixture.state === "completed" && (position === 0 || album && fixture.albumChosen), chosen: null,
            candidates: position === 0 ? [] : [
              { providerId: "library", resourceId: album ? "evening-album" : "mira-artist", title: album ? "Evening" : "Mira", artist: album ? "Mira" : null, year: album ? 2020 : null, trackCount: album ? 12 : null, artworkUrl: null, albums: album ? [] : ["Evening", "Daylight"] },
              { providerId: "library", resourceId: album ? "evening-live" : "mira-other", title: album ? "Evening Live" : "Mira", artist: album ? "Mira" : null, year: album ? 2022 : null, trackCount: album ? 14 : null, artworkUrl: null, albums: album ? [] : ["Another sky"] },
            ] },
        })));
      }
      const rows = id === "likes" ? [
        occurrence("likes", 0, "Golden hour", 2, 0, 1, true), occurrence("likes", 1, "Night drive", 2, 10, 2, true),
        occurrence("likes", 2, "Housewife", fixture.fixed ? 2 : 1, fixture.fixed ? 0 : 3, fixture.fixed ? 1 : null, fixture.appliedFixed),
      ] : [occurrence("mix", 0, "Golden hour", 2, 1, 1, true), occurrence("mix", 1, "Night drive", 2, 10, 2), occurrence("mix", 2, "Lost recording", 0, 2, null), occurrence("mix", 3, null, 0, 2, null)];
      return reply(route, rows.slice(Number(url.searchParams.get("offset") ?? 0), Number(url.searchParams.get("offset") ?? 0) + Number(url.searchParams.get("limit") ?? 50)));
    }
    if (path === `/api/admin/migrations/${runId}/apply`) { fixture.appliedFixed = fixture.fixed; fixture.state = "applying"; return route.fulfill({ status: 202, body: "" }); }
    if (path === `/api/admin/migrations/${runId}/resume`) { fixture.state = "collecting"; return route.fulfill({ status: 202, body: "" }); }
    if (path === `/api/admin/migrations/${runId}/cancel`) { fixture.state = "cancelled"; return route.fulfill({ status: 202, body: "" }); }
    if (path === "/api/admin/ui/listener-context") return reply(route, { backend: "Jellyfin", username: "Alex", musicClientUrl: "https://music.example.com", addressSource: "configured" });
    if (path === "/api/admin/track-matches") return reply(route, { matches: [{ externalSnapshotId: snapshotId, providerId: "spotify", title: "Housewife", artist: "Tate McRae", album: "So Close to What", state: "review", durationMilliseconds: 180000, providerIdentities: [], reasons: [], warnings: [], allowedAuthorityScopes: ["personal"], manualAuthorities: [], candidates: [{ title: "Housewife — library version", artist: "Tate McRae", libraryTrackId: "library-song", isLocal: true, confidence: .98, durationMilliseconds: 180000 }] }] });
    if (path === `/api/admin/track-matches/${snapshotId}/resolve`) { fixture.fixed = true; return reply(route, { success: true }); }
    if (path.startsWith("/api/admin/track-matches/targets/")) return reply(route, { tracks: [], providers: [] });
    return reply(route, {});
  });
  return fixture;
}

async function focusRefresh(page: Page) { await page.evaluate(() => window.dispatchEvent(new Event("focus"))); }
async function liveRefresh(page: Page) {
  await page.evaluate(id => (window as unknown as { __emitMigrationUpdate: (id: string) => void }).__emitMigrationUpdate(id), jobId);
}
async function discover(page: Page) {
  await page.goto(`/#/library/import?account=${accountId}`);
  await expect(page.getByRole("heading", { name: "Bring your music", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Spotify account", exact: true })).toHaveText("My Spotify");
  await expect(page.getByRole("button", { name: "Refresh import progress", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Find my music", exact: true }).click();
  await expect(page.getByRole("checkbox", { name: "Select Liked Songs", exact: true })).toBeVisible();
}
async function choose(page: Page) {
  await page.getByRole("checkbox", { name: "Select Liked Songs", exact: true }).check();
  await page.getByRole("checkbox", { name: "Select Evening mix", exact: true }).check();
  await page.getByRole("checkbox", { name: "Keep Evening mix in sync", exact: true }).check();
  await page.getByRole("button", { name: "Start matching (2 selected)", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Matching your music…", exact: true })).toBeVisible();
}
async function capture(page: Page, step: string, width: number, theme: string) {
  if (width === 390) await expectContainedMobileGeometry(page);
  await page.screenshot({ path: test.info().outputPath(`import-${step}-${width}-${theme}.png`), fullPage: true });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Import chooses, matches, fixes and moves music at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const fixture = await mockImport(page);
    await discover(page);
    await expect(page.getByRole("checkbox", { name: /^Select / }).first()).toHaveAccessibleName("Select Liked Songs");
    await page.getByRole("button", { name: "Show more playlists", exact: true }).click();
    await expect(page.getByRole("checkbox", { name: "Select Weekend", exact: true })).toBeVisible();
    await expect(page.getByRole("checkbox", { name: "Select Liked Songs", exact: true })).toHaveCount(1);
    await expect(page.getByRole("button", { name: "Start matching (0 selected)", exact: true })).toBeDisabled();
    await expect(page.getByRole("checkbox", { name: "Keep Evening mix in sync", exact: true })).not.toBeChecked();
    await page.getByRole("button", { name: "Select all", exact: true }).click();
    await expect(page.getByRole("checkbox", { name: "Select Weekend", exact: true })).toBeChecked();
    await page.getByRole("button", { name: "Clear", exact: true }).click();
    await expect(page.getByRole("checkbox", { name: "Select Liked Songs", exact: true })).not.toBeChecked();
    await capture(page, "choose", width, theme);
    await choose(page);
    await expect(page.getByRole("progressbar", { name: "Music matched", exact: true })).toHaveAttribute("aria-valuenow", /^28\./);
    await expect(page.getByRole("list", { name: "Import progress" }).locator('[aria-current="step"]')).toHaveText("Match");
    await expect(page.getByRole("article", { name: "Matching progress" }).getByRole("button", { name: "Stop moving", exact: true })).toBeVisible();
    await capture(page, "matching", width, theme);
    expect(fixture.writes.find(write => write.path.endsWith("/choose"))?.body).toEqual({ Collections: [{ SourceId: liked.sourceId, KeepInSync: false }, { SourceId: evening.sourceId, KeepInSync: true }] });
    const creates = fixture.writes.filter(write => write.path === "/api/admin/migrations");
    expect(creates).toHaveLength(1);
    expect(creates[0].body).toEqual({ ProviderAccountId: accountId, RequestKey: expect.any(String) });
    const reads = fixture.reads.length;
    await page.reload();
    await expect(page.getByRole("heading", { name: "Matching your music…", exact: true })).toBeVisible();
    expect(fixture.reads.length).toBeGreaterThan(reads);
    expect(fixture.writes.filter(write => write.path === "/api/admin/migrations")).toHaveLength(1);
    fixture.state = "review"; fixture.phase = "review";
    await liveRefresh(page);
    for (const label of ["Ready", "Needs a look", "Not found", "Can't be moved"]) await expect(page.getByText(label, { exact: true }).first()).toBeVisible();
    for (const [index, value] of [4, 1, 1, 1].entries()) await expect(page.locator(".import-stat").nth(index).locator("strong")).toHaveText(String(value));
    await expect(page.getByText("2 in your library · 2 will stream", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Move 4 songs now", exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Fix Housewife", exact: true })).toBeVisible();
    await expect(page.getByRole("region", { name: "Review your music" }).locator(".import-actions").getByRole("button", { name: "Stop moving", exact: true })).toBeVisible();
    await capture(page, "review", width, theme);
    const summaryReads = fixture.reads.filter(path => path.includes("/summary")).length;
    const rowReads = fixture.reads.filter(path => path.includes("/review?")).length;
    await page.getByRole("button", { name: "Fix Housewife", exact: true }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByRole("heading", { name: "Housewife", exact: true })).toBeVisible();
    await dialog.getByRole("button", { name: "Choose candidate", exact: true }).click();
    await expect(dialog).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Move 5 songs now", exact: true })).toBeVisible();
    expect(fixture.reads.some(path => path.includes(`externalSnapshotId=${snapshotId}`))).toBe(true);
    expect(fixture.writes.find(write => write.path.endsWith(`/${snapshotId}/resolve`))?.body).toMatchObject({ targetType: "local", libraryTrackId: "library-song", authorityScope: "personal" });
    expect(fixture.reads.filter(path => path.includes("/summary")).length).toBeGreaterThan(summaryReads);
    expect(fixture.reads.filter(path => path.includes("/review?")).length).toBeGreaterThan(rowReads);
    await page.getByRole("button", { name: "Move 5 songs now", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Adding playlists and hearts…", exact: true })).toBeVisible();
    fixture.state = "completed"; fixture.phase = "done";
    await focusRefresh(page);
    await expect(page.getByRole("heading", { name: "Your music is in Allstarr", exact: true })).toBeVisible();
    await expect(page.getByText("Liked Songs — 3 hearts added", { exact: true })).toBeVisible();
    await expect(page.getByText("Evening mix — 1 song added · kept in sync", { exact: true })).toBeVisible();
    await expect(page.getByText("1 song still needs a look", { exact: true })).toBeVisible();
    await expect(page.getByRole("link", { name: "Open playlists", exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Move more music", exact: true })).toBeVisible();
    await capture(page, "done", width, theme);
    await page.getByRole("button", { name: "Review them", exact: true }).click();
    await expect(page.getByRole("button", { name: /^Move \d+ (?:more )?songs?(?: now)?$/ })).toHaveCount(0);
    expect(fixture.forbidden).toEqual([]);
    if (width === 390 && theme === "light") {
      await page.getByRole("button", { name: "Move more music", exact: true }).click();
      fixture.state = "choosing";
      await page.getByRole("button", { name: "Find my music", exact: true }).click();
      await expect(page.getByRole("button", { name: "Start matching (0 selected)", exact: true })).toBeDisabled();
      const nextCreate = fixture.writes.filter(write => write.path === "/api/admin/migrations").at(-1)!;
      expect(nextCreate.body.RequestKey).not.toBe(creates[0].body.RequestKey);
    }
  });
}

test("Import retries an uncertain discovery start with the same request key", async ({ page }) => {
  const fixture = await mockImport(page); fixture.createError = true;
  await page.goto(`/#/library/import?account=${accountId}`);
  await page.getByRole("button", { name: "Find my music", exact: true }).click();
  await expect(page.getByRole("alert")).toBeVisible();
  fixture.createError = false;
  await page.getByRole("button", { name: "Find my music", exact: true }).click();
  await expect(page.getByRole("checkbox", { name: "Select Liked Songs", exact: true })).toBeVisible();
  const writes = fixture.writes.filter(write => write.path === "/api/admin/migrations");
  expect(writes).toHaveLength(2);
  expect(writes[1].body).toEqual(writes[0].body);
  expect(writes[0].body.RequestKey).toEqual(expect.stringMatching(/^[0-9a-f-]{36}$/i));
});

test("Import resumes a saved run, retries waiting work and confirms before stopping", async ({ page }) => {
  const fixture = await mockImport(page, [{ ...account, id: "66666666-6666-4666-8666-666666666666", displayName: "Another Spotify" }, account]); fixture.state = "waiting";
  await page.addInitScript(({ key, id }) => localStorage.setItem(key, id), { key: `allstarr:migration:${accountId}`, id: runId });
  await page.goto("/#/library/import");
  await expect(page.getByText("Your progress is saved. Check your Spotify connection and playlist access, then try again.").first()).toBeVisible();
  await page.getByRole("button", { name: "Try again", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Matching your music…", exact: true })).toBeVisible();
  expect(fixture.writes.some(write => write.path.endsWith("/resume"))).toBe(true);
  expect(fixture.writes.some(write => write.path === "/api/admin/migrations")).toBe(false);
  const stopMoving = page.getByRole("article", { name: "Matching progress", exact: true }).getByRole("button", { name: "Stop moving", exact: true });
  await stopMoving.click();
  const dialog = page.getByRole("alertdialog");
  await expect(dialog).toBeVisible();
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  expect(fixture.writes.some(write => write.path.endsWith("/cancel"))).toBe(false);
  await stopMoving.click();
  await dialog.getByRole("button", { name: "Stop moving", exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(fixture.writes.filter(write => write.path.endsWith("/cancel"))).toHaveLength(1);
});

test("Import opens a run URL and refreshes progress on focus without creating another run", async ({ page }) => {
  const fixture = await mockImport(page); fixture.state = "collecting";
  await page.goto(`/#/library/import?account=${accountId}&run=${runId}`);
  await expect(page.getByRole("heading", { name: "Matching your music…", exact: true })).toBeVisible();
  fixture.state = "review";
  await focusRefresh(page);
  await expect(page.getByRole("button", { name: "Move 4 songs now", exact: true })).toBeVisible();
  expect(fixture.writes).toEqual([]);
});

test("Import explains when no Spotify account is connected", async ({ page }) => {
  await mockImport(page, []);
  await page.goto("/#/library/import");
  await expect(page.getByText(/^Connect your Spotify account first\.?$/)).toBeVisible();
});

test("Spotify Sources and Getting started lead to the music import", async ({ page }) => {
  await mockImport(page);
  await page.goto("/#/integrations/services/spotify");
  await expect(page.getByRole("link", { name: "Move your Spotify collection", exact: true })).toHaveAttribute("href", `#/library/import?account=${accountId}`);
  await page.goto("/#/getting-started");
  await expect(page.getByText("Coming from Spotify?", { exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: "Move your Spotify collection", exact: true })).toHaveAttribute("href", "#/library/import");
});

test("Import offers only newly accepted songs after completion", async ({ page }) => {
  const fixture = await mockImport(page); fixture.state = "completed";
  await page.goto(`/#/library/import?run=${runId}`);
  await expect(page.getByText("Liked Songs — 2 hearts added", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: /^Move \d+/ })).toHaveCount(0);
  await page.getByRole("button", { name: "Fix Housewife", exact: true }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Choose candidate", exact: true }).click();
  await expect(page.getByRole("button", { name: "Move 1 more song", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Move 1 more song", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Adding playlists and hearts…", exact: true })).toBeVisible();
  fixture.state = "completed";
  await focusRefresh(page);
  await expect(page.getByText("Liked Songs — 3 hearts added", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: /^Move \d+/ })).toHaveCount(0);
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Import reviews saved albums and followed artists at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const fixture = await mockImport(page, [account], true);
    await discover(page);
    const choiceNames = page.getByRole("checkbox", { name: /^Select / });
    for (const [index, name] of ["Liked Songs", "Saved albums", "Followed artists", "Evening mix"].entries()) await expect(choiceNames.nth(index)).toHaveAccessibleName(`Select ${name}`);
    await expect(page.getByText("2 albums", { exact: true })).toBeVisible();
    await expect(page.getByText("2 artists", { exact: true })).toBeVisible();
    await expect(page.getByText(/can't be moved yet/)).toHaveCount(0);
    for (const name of ["Saved albums", "Followed artists"]) await page.getByRole("checkbox", { name: `Select ${name}`, exact: true }).check();
    await expect(page.getByRole("checkbox", { name: /^Keep (Saved albums|Followed artists)/ })).toHaveCount(0);
    await page.getByRole("button", { name: "Start matching (2 selected)", exact: true }).click();
    expect(fixture.writes.find(write => write.path.endsWith("/choose"))?.body).toEqual({ Collections: [{ SourceId: savedAlbums.sourceId, KeepInSync: false }, { SourceId: followedArtists.sourceId, KeepInSync: false }] });
    fixture.state = "review";
    await focusRefresh(page);
    await page.locator("summary").filter({ hasText: "Saved albums" }).click();
    const chooseAlbum = page.getByRole("button", { name: "Choose Evening", exact: true });
    await chooseAlbum.click();
    const dialog = page.getByRole("dialog", { name: "Choose an album", exact: true });
    await expect(dialog.getByRole("heading", { name: "Choose an album", exact: true })).toBeVisible();
    await expect(dialog.getByRole("button", { name: "Choose Evening by Mira", exact: true })).toBeFocused();
    await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(dialog).toHaveCount(0);
    await expect(chooseAlbum).toBeFocused();
    await chooseAlbum.click();
    await expect(dialog.getByRole("button", { name: "Choose Evening by Mira", exact: true })).toBeFocused();
    await expect(dialog.getByText("Mira", { exact: true }).first()).toBeVisible();
    await expect(dialog.getByText("2020 · 12 songs", { exact: true })).toBeVisible();
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: test.info().outputPath(`import-album-choice-${width}-${theme}.png`), fullPage: false });
    fixture.collectionChoiceError = true;
    await dialog.getByRole("button", { name: "Choose Evening by Mira", exact: true }).click();
    await expect(dialog.getByRole("alert")).toBeVisible();
    fixture.collectionChoiceError = false;
    const reads = fixture.reads.length;
    await dialog.getByRole("button", { name: "Choose Evening by Mira", exact: true }).click();
    await expect(dialog).toHaveCount(0);
    expect(fixture.writes.filter(write => write.path.endsWith("/review/albums/albums-1/choose")).at(-1)?.body).toEqual({ providerId: "library", resourceId: "evening-album" });
    expect(fixture.reads.slice(reads).some(path => path.includes("collectionId=albums"))).toBe(true);
    await expect(page.getByRole("button", { name: "Choose Evening", exact: true })).toHaveCount(0);
    await page.locator("summary").filter({ hasText: "Followed artists" }).click();
    await page.getByRole("button", { name: "Choose Mira", exact: true }).click();
    const artistDialog = page.getByRole("dialog", { name: "Choose an artist", exact: true });
    await expect(artistDialog.getByText("Evening · Daylight", { exact: true })).toBeVisible();
    await artistDialog.getByRole("button", { name: "Skip this one", exact: true }).click();
    await expect(artistDialog).toHaveCount(0);
    expect(fixture.writes.find(write => write.path.endsWith("/review/artists/artists-1/choose"))?.body).toEqual({ skip: true });
    fixture.state = "completed";
    await focusRefresh(page);
    await expect(page.getByText("Saved albums — 2 albums added", { exact: true })).toBeVisible();
    await expect(page.getByText("Followed artists — 1 artist followed", { exact: true })).toBeVisible();
    expect(fixture.forbidden).toEqual([]);
  });
}

test("Import explains only collection kinds unavailable during discovery", async ({ page }) => {
  const fixture = await mockImport(page);
  fixture.unsupported = [{ kind: "followed-artists" }];
  await discover(page);
  await expect(page.getByText("Followed artists can't be moved yet.", { exact: true })).toBeVisible();
  await expect(page.getByText(/Saved albums.*can't be moved/)).toHaveCount(0);
});
