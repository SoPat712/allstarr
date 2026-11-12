import { expect, test, type Page, type Route } from "@playwright/test";
import { resolve } from "node:path";

const recording = "10000000-0000-0000-0000-000000000003";
const runId = "20000000-0000-0000-0000-000000000001";
const lookId = "20000000-0000-0000-0000-000000000002";
const addedId = "20000000-0000-0000-0000-000000000003";
const cover = "data:image/svg+xml," + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="80" height="80"><defs><linearGradient id="c" x2=".5" y2="1"><stop stop-color="#ff8b00"/><stop offset=".6" stop-color="#ff3567"/><stop offset="1" stop-color="#5300a0"/></linearGradient></defs><path fill="url(#c)" d="M0 0h80v80H0z"/></svg>');
const file = { path: "Kendrick Lamar/Backseat Freestyle.flac", storage: "kept", title: "Backseat Freestyle", artist: "Kendrick Lamar", album: "good kid, m.A.A.d city (Deluxe)", fileName: "Backseat Freestyle.flac", size: 29000000, sizeFormatted: "29 MB", lastModified: "2026-11-06T14:00:00Z", lastAccessedAt: "2026-11-06T14:00:00Z", codec: "FLAC", quality: "Lossless", sampleRateHz: 44100, bitDepth: 16, durationMilliseconds: 212000, provider: "qobuz", publicationState: "Referenced", explicitlyKept: true, referenceCount: 1, removable: true };
const copies = [file, { ...file, path: "Nicole.flac", title: "Nicole Kidman", artist: "ADÉLA", album: "Nicole Kidman", bitDepth: 24, sampleRateHz: 48000 }, { ...file, path: "Stay.flac", title: "Stay Alive (Prod. SUGA of BTS)", artist: "Jung Kook", album: "Stay Alive" }, { ...file, path: "Glad.flac", title: "Glad He’s Gone", artist: "Tove Lo", album: "Sunshine Kitty" }, { ...file, path: "Tight.m4a", title: "Tight Up Skirt", artist: "Leigh-Anne", album: "Tight Up Skirt", codec: "AAC", bitDepth: null, quality: "AAC 256", bitrateKbps: 256 }];
async function json(route: Route, body: unknown, status = 200) { return route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) }); }
async function capture(page: Page, name: string) {
  const path = resolve(`test-results/promotions/${name}.png`);
  const title = page.locator('[data-side-panel][data-state="open"] [data-dialog-title]');
  if (await title.count()) await title.click();
  await page.screenshot({ path, fullPage: true });
  await test.info().attach(name, { path, contentType: "image/png" });
  console.log(`Saved screenshot: ${path}`);
}
async function mockPromotions(page: Page) {
  const mutations: { path: string; body: any }[] = [];
  await page.addInitScript(() => {
    class UpdateStream extends EventTarget {
      readyState = 1;
      onerror = null;
      constructor(_url: string) { super(); queueMicrotask(() => this.dispatchEvent(new MessageEvent("stream-status", { data: "{}" }))); }
      close() { this.readyState = 2; }
    }
    window.EventSource = UpdateStream as unknown as typeof EventSource;
  });
  const step = (name: string, outcome = "Complete", reason = "Complete") => ({ name, outcome, reason });
  const song = (title: string, track: number, outcome: string, steps: any[]) => ({ recordingId: track === 3 ? recording : `10000000-0000-0000-0000-${String(track).padStart(12, "0")}`, song: { title, artist: "Kendrick Lamar", album: file.album, track, codec: "flac", bitDepth: 24, sampleRate: 96000, coverArtUrl: cover }, provider: "qobuz", step: outcome === "Queued" ? "Acquire" : "Lyrics", outcome, reason: outcome === "Queued" ? "Waiting" : "Working on lyrics", steps });
  const done = [step("Acquire", "Complete", "Best copy acquired"), step("Quality", "Complete", "No upscaled files found"), step("Tags", "Complete", "Release metadata written"), step("Lyrics", "Complete", "Verified lyrics written"), step("ReplayGain", "Complete", "Track and album gain measured"), step("Placement", "Complete", "Placed in the library folder"), step("Refresh", "Complete", "Library rescan completed"), step("Handover", "Complete", "Library links confirmed")];
  const active: any = { id: runId, title: file.album, artist: file.artist, coverReference: cover, unit: "album", counts: { total: 4, added: 2, needsLook: 0 }, state: "active", outcome: "Running", step: "Lyrics", startedAt: "2026-11-06T14:02:00Z", finishedAt: null, tracks: [song("Sherane a.k.a Master Splinter’s Daughter", 1, "Placed", done), song("Backseat Freestyle", 3, "Placed", done), song("good kid", 10, "Running", [step("Acquire"), step("Quality"), step("Tags"), step("Lyrics", "Running", "Working on lyrics")]), song("m.A.A.d city", 11, "Queued", [])] };
  const needsLook: any = { id: lookId, title: "Midnights (3am Edition)", artist: "Taylor Swift", coverReference: cover, unit: "album", counts: { total: 3, added: 2, needsLook: 1 }, state: "needs-look", outcome: "Needs review", step: "Quality", startedAt: "2026-11-06T14:00:00Z", tracks: [...active.tracks.slice(0, 2), { ...song("Anti-Hero", 3, "Needs review", [step("Acquire"), step("Quality", "Needs review", "Quality review required")]), step: "Quality", song: { title: "Anti-Hero", artist: "Taylor Swift", album: "Midnights (3am Edition)", track: 3, codec: "flac", coverArtUrl: cover }, reason: "Quality review required", qualityCheck: { claimedSampleRate: 44100, claimedBitDepth: 16, effectiveBandwidthHz: 16000, verdict: "lossy-transcode-suspected", confidence: .65 }, nextSource: { provider: "apple-musickit", codec: "alac", bitDepth: 16, sampleRate: 44100 } }] };
  const added: any = { ...active, id: addedId, title: "Nicole Kidman", artist: "ADÉLA", state: "added", outcome: "Placed", counts: { total: 1, added: 1, needsLook: 0 }, finishedAt: "2026-11-06T13:30:00Z", tracks: [active.tracks[0]] };
  needsLook.tracks = needsLook.tracks.map((track: any, index: number) => index < 2 ? { ...track, recordingId: `10000000-0000-0000-0000-${String(index + 50).padStart(12, "0")}`, song: { ...track.song, title: ["Lavender Haze", "Maroon"][index], artist: needsLook.artist, album: needsLook.title, track: index + 1 } } : track);
  added.tracks = [{ ...active.tracks[0], recordingId: "10000000-0000-0000-0000-000000000052", song: { ...active.tracks[0].song, title: added.title, artist: added.artist, album: added.title, track: 1 }, reason: "Library links confirmed" }];
  active.profileSteps = [{ key: "tags-cover", title: "MusicBrainz tags and cover art", enabled: true }];
  const runs = [active, needsLook, added];
  await page.clock.install({ time: new Date("2026-11-06T15:00:00Z") });
  await page.route("**/api/admin/**", async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.pathname.endsWith("/auth/me")) return json(route, { authenticated: true, backend: "Jellyfin", user: { id: "owner", name: "joshp", isAdministrator: true } });
    if (url.pathname.endsWith("/auth/oidc/status")) return json(route, { enabled: false });
    if (url.pathname.endsWith("/onboarding/status")) return json(route, { completed: true, setupOpen: false, shouldRedirectToSetup: false, recoveryNotices: [] });
    if (url.pathname.endsWith("/updates/stream")) return route.fulfill({ contentType: "text/event-stream", body: ": ready\n\n" });
    if (url.pathname.endsWith("/ui/schema")) return json(route, { activeBackend: "Jellyfin", providers: [], settings: [], priorityGroups: [] });
    if (url.pathname === "/api/admin/downloads") return json(route, { storage: "kept", files: copies.map(item => ({ ...item, artworkUrl: cover })), count: 5, managedCount: 5, diagnosticCount: 0, totalSize: 145000000, totalSizeFormatted: "145 MB" });
    if (url.pathname.endsWith("/library-promotions/requests")) return json(route, []);
    if (url.pathname.endsWith("/library-promotions/kept")) return json(route, { items: copies.map((item, index) => ({ path: item.path, recordingId: index === 0 ? recording : `10000000-0000-0000-0000-${String(index + 20).padStart(12, "0")}`, backendLibraryId: "music", inLibrary: index === 2 })), backendLibraryId: "music", rootId: "30000000-0000-0000-0000-000000000001", alreadyInLibrary: 1 });
    if (url.pathname.endsWith("/library-promotions/preview")) return json(route, { canAddDirectly: true, destinationPath: "/music/Kendrick Lamar/good kid, m.A.A.d city (2012)/03 - Backseat Freestyle.flac", album: { title: file.album, artist: file.artist, year: 2012, songCount: 16, coverReference: cover }, alreadyInLibrary: 2, bestCopies: [{ provider: "qobuz", codec: "flac", bitDepth: 24, sampleRate: 96000, verdict: "meets-your-floor", reason: "Your floor is lossless 16/44 · keeps hi-res up to 24/96" }, { provider: "apple-musickit", codec: "alac", bitDepth: 24, sampleRate: 48000, verdict: "fallback", reason: "Next choice if qobuz fails" }, { provider: "deezer", codec: "mp3", verdict: "skipped-below-floor", reason: "Below your floor · never used for the library" }], profileSteps: [{ key: "quality", title: "Check the copy is really lossless", description: "Spectrum check for upscaled files", enabled: true }, { key: "tags", title: "MusicBrainz tags and cover art", description: "Release, recording and artist IDs · cover.jpg + embedded", enabled: true }, { key: "lyrics", title: "Synced lyrics", description: "Best-ranked version, only if its timing fits this file · .lrc next to the song", enabled: true }, { key: "gain", title: "ReplayGain", description: "Track and album gain with rsgain", enabled: true }, { key: "handover", title: "Switch to the library copy", description: "Playlists, mixes, hearts and plays move to the new file; the kept copy is removed", enabled: true }] });
    if (url.pathname === "/api/admin/library-promotions" && request.method() === "GET") {
      const filter = url.searchParams.get("state");
      return json(route, { items: runs.filter(run => filter === "all" || filter === "active" && ["active", "needs-look"].includes(run.state) || run.state === filter), nextCursor: null, counts: { active: runs.filter(run => ["active", "needs-look"].includes(run.state)).length, needsLook: runs.filter(run => run.state === "needs-look").length, added: runs.filter(run => run.state === "added").length, all: runs.length, addedToday: runs.filter(run => run.state === "added").length } });
    }
    if (request.method() !== "GET") mutations.push({ path: url.pathname, body: request.postDataJSON() });
    if (url.pathname === "/api/admin/library-promotions") return json(route, active, 202);
    const match = url.pathname.match(/\/library-promotions\/([^/]+)(.*)$/);
    if (match) {
      const run = runs.find(run => run.id === match[1]);
      if (match[2] === "/cancel") { run.state = "cancelled"; run.outcome = "Cancelled"; }
      if (match[2].endsWith("/review")) {
        const { action, reason } = request.postDataJSON();
        run.state = action === "skip" ? "added" : "active";
        run.outcome = "Queued"; run.counts.needsLook = 0;
        const track = run.tracks[2];
        track.outcome = action === "skip" ? "Skipped" : "Queued";
        track.lossy = action === "add-anyway";
        track.approvedByUserId = action === "add-anyway" ? "40000000-0000-0000-0000-000000000001" : null;
        track.approvalReason = action === "add-anyway" ? reason : null;
        if (action === "try-next-source") { track.steps = []; track.step = "Acquire"; track.qualityCheck = null; track.provider = null; track.reason = "Waiting for the next source"; }
        if (action === "skip") { run.finishedAt = "2026-11-06T14:50:00Z"; track.reason = "Skipped by the reviewer"; }
      }
      return json(route, run);
    }
    return json(route, {});
  });
  return mutations;
}

test("opens a kept song and starts the whole album with the real context", async ({ page }) => {
  const mutations = await mockPromotions(page);
  await page.goto("/#/library/kept");
  await expect(page.getByLabel("Kept totals")).toContainText("5 kept");
  await expect(page.getByLabel("Kept totals")).toContainText("4 lossless");
  await expect(page.getByLabel("Kept totals")).toContainText("1 already in your library");
  await page.getByRole("button", { name: "Open song details for Backseat Freestyle", exact: true }).click();
  const panel = page.getByRole("dialog", { name: file.album, exact: true });
  await expect(panel).toContainText("14 will be added");
  await expect(panel).toContainText("Meets your floor");
  await expect(panel).toContainText("Qobuz · FLAC 24-bit / 96 kHz");
  await expect(panel).toContainText("Next choice if Qobuz fails");
  await expect(panel).toContainText("03 - Backseat Freestyle.flac");
  await expect(panel).toContainText("What happens · your profile");
  await panel.getByRole("button", { name: "Add album to library", exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`to-library\\?run=${runId}`));
  await expect(page.getByRole("dialog", { name: file.album, exact: true })).toBeVisible();
  expect(mutations.find(item => item.path === "/api/admin/library-promotions")?.body).toMatchObject({ recordingId: recording, backendLibraryId: "music", unit: "album" });
});

test("kept selections survive a change to search filters", async ({ page }) => {
  const mutations = await mockPromotions(page);
  await page.goto("/#/library/kept");
  await page.getByRole("button", { name: "Add selected to library", exact: true }).click();
  await page.getByRole("checkbox", { name: "Select Backseat Freestyle", exact: true }).click();
  await page.getByRole("button", { name: "Actions for kept songs", exact: true }).click();
  await page.getByRole("menuitem", { name: "Search and sort", exact: true }).click();
  await page.getByRole("searchbox", { name: "Filter kept songs", exact: true }).fill("Nicole");
  const rows = page.getByRole("table", { name: "Kept songs", exact: true }).locator(".library-download-row");
  await expect(rows).toHaveCount(1);
  await expect(rows).toContainText("Nicole Kidman");
  await page.getByRole("radio", { name: "Lower quality 1", exact: true }).click();
  await expect(page.getByText("No songs match these filters", { exact: true })).toBeVisible();
  await page.getByRole("radio", { name: "All 5", exact: true }).click();
  await page.getByRole("searchbox", { name: "Filter kept songs", exact: true }).fill("");
  await expect(page.getByRole("checkbox", { name: "Select Backseat Freestyle", exact: true })).toBeChecked();
  await page.getByRole("searchbox", { name: "Filter kept songs", exact: true }).fill("Nicole");
  await expect(rows).toHaveCount(1);
  await page.getByRole("button", { name: "Add selected to library", exact: true }).click();
  const dialog = page.getByRole("alertdialog", { name: "Add selected albums to your library?", exact: true });
  await expect(dialog).toContainText("1 selected song");
  await dialog.getByRole("button", { name: "Add to library", exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`to-library\\?run=${runId}`));
  expect(mutations.find(item => item.path === "/api/admin/library-promotions")?.body).toMatchObject({ recordingId: recording, unit: "album" });
});

test("queue groups and filters use the returned counts", async ({ page }) => {
  await mockPromotions(page); await page.goto("/#/library/to-library");
  await expect(page.getByRole("region", { name: "Adding now", exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Needs a look", exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Added today", exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Waiting for approval", exact: true })).toHaveCount(0);
  await expect(page.getByLabel("Library additions")).toContainText("0 waiting for approval");
  await page.getByRole("radio", { name: "Needs a look 1", exact: true }).click();
  await expect(page.getByRole("button", { name: `Open ${file.album}`, exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Open Midnights (3am Edition)", exact: true })).toBeVisible();
  await page.getByRole("radio", { name: "Added 1", exact: true }).click();
  await expect(page.getByRole("button", { name: "Open Nicole Kidman", exact: true })).toBeVisible();
  await page.getByRole("radio", { name: "All 3", exact: true }).click();
  await expect(page.getByRole("button", { name: /^Open (good kid|Midnights|Nicole)/ })).toHaveCount(3);
});

test("a failed filter request does not leave rows from the previous filter", async ({ page }) => {
  await mockPromotions(page); await page.goto("/#/library/to-library");
  await expect(page.getByRole("button", { name: `Open ${file.album}`, exact: true })).toBeVisible();
  await page.route("**/api/admin/library-promotions?**", route => new URL(route.request().url()).searchParams.get("state") === "needs-look" ? json(route, { error: "Library additions are temporarily unavailable." }, 503) : route.fallback());
  await page.getByRole("radio", { name: "Needs a look 1", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("temporarily unavailable");
  await expect(page.getByRole("button", { name: `Open ${file.album}`, exact: true })).toHaveCount(0);
});

test("run progress timeline, album contents and cancel confirmation", async ({ page }) => {
  const mutations = await mockPromotions(page); await page.goto(`/#/library/to-library?run=${runId}`);
  const panel = page.getByRole("dialog", { name: file.album, exact: true });
  await expect(panel.getByRole("progressbar")).toHaveAttribute("aria-valuenow", "2");
  await expect(panel).toContainText("2 of 4 songs");
  await expect(panel).toContainText("Downloaded 3 songs from Qobuz");
  await expect(panel).toContainText("Tagging from MusicBrainz");
  await expect(panel).toContainText("Synced lyrics and ReplayGain");
  await expect(panel.locator(".promotion-songs .state-pill")).toHaveText(["Done", "Done", "Now", "Queued"]);
  await panel.getByRole("button", { name: "Open the album", exact: true }).click();
  await expect(panel).toContainText("Album songs");
  await page.clock.fastForward(2600);
  await expect(panel).toContainText("Album songs");
  await panel.getByRole("button", { name: "Back to progress", exact: true }).click();
  await panel.getByRole("button", { name: "Cancel…", exact: true }).click();
  const dialog = page.getByRole("alertdialog", { name: "Cancel adding to the library?", exact: true });
  expect(mutations).toHaveLength(0);
  await dialog.getByRole("button", { name: "Keep adding", exact: true }).click();
  expect(mutations).toHaveLength(0);
  await panel.getByRole("button", { name: "Cancel…", exact: true }).click();
  await dialog.getByRole("button", { name: "Cancel adding", exact: true }).click();
  await expect(dialog).not.toBeVisible();
  expect(mutations.at(-1)?.path).toBe(`/api/admin/library-promotions/${runId}/cancel`);
});

for (const action of ["try-next-source", "add-anyway", "skip"] as const) test(`needs a look action ${action}`, async ({ page }) => {
  const mutations = await mockPromotions(page); await page.goto(`/#/library/to-library?run=${lookId}`);
  const panel = page.getByRole("dialog", { name: "Anti-Hero", exact: true });
  await expect(panel).toContainText("This copy looks upscaled from MP3.");
  await expect(panel).toContainText("about 16 kHz");
  await expect(panel).toContainText("2 of 3 songs passed and were added");
  await expect(panel.getByRole("img", { name: /Measured sound cut-off/ })).toBeVisible();
  if (action === "add-anyway") {
    await panel.getByRole("button", { name: "Add anyway", exact: true }).click();
    const dialog = page.getByRole("alertdialog", { name: "Add this copy anyway?", exact: true });
    await expect(dialog.getByRole("button", { name: "Add anyway", exact: true })).toBeDisabled();
    await dialog.getByRole("textbox", { name: "Reason", exact: true }).fill("Only available mastering of this recording.");
    await dialog.getByRole("button", { name: "Add anyway", exact: true }).click();
    await expect(dialog).not.toBeVisible();
  } else await panel.getByRole("button", { name: action === "skip" ? "Skip" : "Try Apple Music", exact: true }).click();
  await expect.poll(() => mutations.length).toBe(1);
  expect(mutations[0]).toMatchObject({ path: `/api/admin/library-promotions/${lookId}/songs/${recording}/review`, body: { action } });
  const albumPanel = page.getByRole("dialog", { name: "Midnights (3am Edition)", exact: true });
  await expect(albumPanel.getByRole("progressbar")).toBeVisible();
  await expect(albumPanel.locator(".promotion-songs .state-pill")).toHaveText(["Done", "Done", action === "skip" ? "Skipped" : "Queued"]);
  if (action === "add-anyway") {
    expect(mutations[0].body.reason).toBe("Only available mastering of this recording.");
    await expect(albumPanel).toContainText("Lossy copy");
    await expect(albumPanel).toContainText("1 song accepted after review");
  }
});

for (const viewport of [{ width: 1280, theme: "dark" }, { width: 390, theme: "light" }, { width: 390, theme: "dark" }] as const) {
  test(`promotion screens fit at ${viewport.width} ${viewport.theme}`, async ({ page }) => {
    await page.setViewportSize({ width: viewport.width, height: 960 });
    await page.emulateMedia({ colorScheme: viewport.theme, reducedMotion: "reduce" });
    await mockPromotions(page);
    await page.goto("/#/library/kept");
    await expect(page.getByRole("heading", { name: "Kept songs", exact: true })).toBeVisible();
    await capture(page, `after-kept-${viewport.width}-${viewport.theme}`);
    await page.getByRole("button", { name: "Open song details for Backseat Freestyle", exact: true }).click();
    await expect(page.getByRole("dialog", { name: file.album, exact: true })).toContainText("What happens · your profile");
    await expectGeometry(page, viewport.width);
    await capture(page, `after-add-${viewport.width}-${viewport.theme}`);
    await page.goto(`/#/library/to-library?run=${runId}`);
    await expect(page.getByRole("progressbar")).toBeVisible();
    await expectGeometry(page, viewport.width);
    await capture(page, `after-queue-${viewport.width}-${viewport.theme}`);
    await page.goto(`/#/library/to-library?run=${lookId}`);
    await expect(page.getByRole("dialog", { name: "Anti-Hero", exact: true })).toContainText("This copy looks upscaled from MP3.");
    await expectGeometry(page, viewport.width);
    await capture(page, `after-look-${viewport.width}-${viewport.theme}`);
  });
}

async function expectGeometry(page: Page, width: number) {
  const panel = page.locator("[data-side-panel]");
  const hierarchy = await panel.evaluate(element => {
    const title = getComputedStyle(element.querySelector("[data-dialog-title]")!);
    const description = getComputedStyle(element.querySelector("[data-dialog-description]")!);
    return { titleSize: parseFloat(title.fontSize), descriptionSize: parseFloat(description.fontSize), titleWeight: parseFloat(title.fontWeight), descriptionWeight: parseFloat(description.fontWeight) };
  });
  expect(hierarchy.titleSize).toBeGreaterThan(hierarchy.descriptionSize);
  expect(hierarchy.titleWeight).toBeGreaterThan(hierarchy.descriptionWeight);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(await panel.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  if (width === 390) {
    const box = await panel.boundingBox();
    expect(box?.x).toBe(0); expect(box?.width).toBe(390); expect(box?.height).toBe(960);
    expect(await panel.locator("button").evaluateAll(buttons => { const hit = (element: Element) => { const box = element.getBoundingClientRect(), area = getComputedStyle(element, "::after"), touch = area.content === "none" ? 0 : 1; return { width: Math.max(box.width, touch * (parseFloat(area.minWidth) || 0)), height: Math.max(box.height, touch * (parseFloat(area.minHeight) || 0)) }; }; return buttons.filter(button => button.getBoundingClientRect().height > 0).every(button => hit(button).height >= 44 && hit(button).width >= 44); })).toBe(true);
  } else {
    await expect(panel).toHaveAttribute("data-mode", "docked");
    const left = (await panel.boundingBox())!.x;
    for (const selector of [".workspace-header", ".route-tabs"]) expect(await page.locator(selector).evaluate(element => element.getBoundingClientRect().right)).toBeLessThanOrEqual(left);
  }
}

test("open run polling pauses while hidden and stops when closed", async ({ page }) => {
  await mockPromotions(page);
  let reads = 0;
  page.on("request", request => { if (new URL(request.url()).pathname === `/api/admin/library-promotions/${runId}`) reads++; });
  await page.goto(`/#/library/to-library?run=${runId}`);
  const panel = page.getByRole("dialog", { name: file.album, exact: true });
  await expect(panel.getByRole("progressbar")).toBeVisible();
  const initial = reads;
  await page.clock.fastForward(2600);
  await expect.poll(() => reads).toBeGreaterThan(initial);
  await page.evaluate(() => { Object.defineProperty(document, "visibilityState", { configurable: true, value: "hidden" }); document.dispatchEvent(new Event("visibilitychange")); });
  const hidden = reads;
  await page.clock.fastForward(8000);
  expect(reads).toBe(hidden);
  await page.evaluate(() => { Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" }); document.dispatchEvent(new Event("visibilitychange")); });
  await expect.poll(() => reads).toBeGreaterThan(hidden);
  await panel.getByRole("button", { name: "Close", exact: true }).click();
  const closed = reads;
  await page.clock.fastForward(8000);
  expect(reads).toBe(closed);
});

test("only this song starts a single-song run", async ({ page }) => {
  const mutations = await mockPromotions(page); await page.goto("/#/library/kept");
  await page.getByRole("button", { name: "Open song details for Backseat Freestyle", exact: true }).click();
  await page.getByRole("button", { name: "Only this song", exact: true }).click();
  await expect.poll(() => mutations.length).toBe(1);
  expect(mutations[0].body.unit).toBe("song");
});

test("starts an album when the browser has no randomUUID function", async ({ page }) => {
  await page.addInitScript(() => Object.defineProperty(crypto, "randomUUID", { value: undefined }));
  const mutations = await mockPromotions(page); await page.goto("/#/library/kept");
  await page.getByRole("button", { name: "Open song details for Backseat Freestyle", exact: true }).click();
  await page.getByRole("button", { name: "Add album to library", exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`to-library\\?run=${runId}`));
  expect(mutations[0].body.requestKey).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
});
