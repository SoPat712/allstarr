import { expect, test, type Page } from "@playwright/test";
import { appearance, mockForYou } from "./for-you-fixtures";
import { expectContainedMobileGeometry } from "./mobile-geometry";

async function capture(page: Page, name: string, width: number, theme: string) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  if (width === 390) await expectContainedMobileGeometry(page);
  const path = `test-results/kept-panel/${name}-${width}-${theme}.png`;
  await page.screenshot({ path, fullPage: !(await page.getByRole("dialog").count()) });
  await test.info().attach(name, { path, contentType: "image/png" });
}

const recording = "10000000-0000-0000-0000-000000000003";
const file = { path: "FLO/Access All Areas/Unknown - Check.flac", storage: "kept", title: "Check", artist: "FLO", album: "Access All Areas", fileName: "Unknown - Check.flac", size: 29000000, sizeFormatted: "29 MB", lastModified: "2026-11-06T14:00:00Z", lastAccessedAt: "2026-11-06T14:00:00Z", codec: "FLAC", quality: "Lossless", sampleRateHz: 44100, bitDepth: 16, durationMilliseconds: 165106, provider: "apple-musickit", externalId: "check", publicationState: "Referenced", explicitlyKept: true, referenceCount: 1, removable: true };
const profile = { namingScript: "%artist%/%title%.%_extension%", libraryRootId: null, qualityFloor: { codecFamily: "lossless", minBitDepth: 16, minSampleRate: 44100 }, hiResCeiling: { maxBitDepth: 24, maxSampleRate: 96000 }, fakeLosslessCheck: true, allowLossy: false, musicBrainzTags: true, coverMode: "both", lyricsMode: ".lrc", replayGain: true, defaultUnit: "album", afterAdding: true, removeKeptCopy: true };

async function fixture(page: Page, admin = true, membership: "found" | "empty" | "error" = "found", backendAvailable = true) {
  await mockForYou(page, { admin });
  await page.addInitScript(() => {
    class Updates extends EventTarget {
      readyState = 1;
      onerror = null;
      constructor(_url: string) { super(); (window as any).testUpdates = this; }
      close() { this.readyState = 2; }
    }
    window.EventSource = Updates as unknown as typeof EventSource;
  });
  const calls = { membership: 0, details: 0, previews: 0, downloads: 0, roots: [] as any[], mounted: false, linked: false };
  await page.route("**/api/admin/**", async route => {
    const url = new URL(route.request().url()), path = url.pathname;
    const reply = (body: unknown, status = 200) => route.fulfill({ json: body, status });
    if (path === "/api/admin/downloads") { calls.downloads++; return reply({ files: [{ ...file, sizeFormatted: `${28 + calls.downloads} MB` }], storage: "kept", count: 1, managedCount: 1, diagnosticCount: 0, totalSizeFormatted: "29 MB" }); }
    if (path === "/api/admin/library-promotions") return reply({ items: [], nextCursor: null, counts: { active: 0, needsLook: 0, added: 0, all: 0, addedToday: 0 } });
    if (path.endsWith("/library-promotions/requests")) return reply([]);
    if (path.endsWith("/library-promotions/kept")) return reply({ items: [{ path: file.path, recordingId: recording, backendLibraryId: backendAvailable ? "music" : null, inLibrary: false }], backendLibraryId: backendAvailable ? "music" : null, rootId: null, alreadyInLibrary: 0, setup: { ready: false, allowedBaseConfigured: false, reason: "library-folder-not-set", rootId: null } });
    if (path.endsWith("/playlist-links/membership")) { calls.membership++; expect(url.searchParams.get("canonicalRecordingId")).toBe(recording); return reply(membership === "error" ? { error: "Unavailable" } : { playlists: membership === "found" ? [{ id: "road", name: "On the Road" }] : [] }, membership === "error" ? 503 : 200); }
    if (path.startsWith("/api/admin/playlist-links/")) { calls.details++; return reply({}); }
    if (path.endsWith("/library-promotions/preview")) { calls.previews++; return reply({ reason: "library-folder-not-set", error: "Not set up" }, 503); }
    if (path.endsWith("/library-promotions/roots")) { if (route.request().method() === "PUT") calls.roots = route.request().postDataJSON(); return reply(calls.roots); }
    if (path.endsWith("/library-promotions/setup")) return reply({ ready: calls.roots.length > 0, allowedBaseConfigured: false, reason: calls.roots.length ? null : "library-folder-not-set", rootId: calls.roots[0]?.id ?? null });
    if (path.endsWith("/library-promotions/folders") && calls.linked) return reply({ available: true, reason: null, folders: [{ name: "Music", backendPath: "/media/music", path: "/media/.music", mapped: true, linked: true, reachable: true, writable: true, status: "writable", rootId: calls.roots[0]?.id ?? null, freeBytes: 33 * 1024 ** 3 }] });
    if (path.endsWith("/library-promotions/folders")) return reply({ available: true, reason: null, folders: [{ name: "Music", backendPath: "/media/music", path: calls.roots[0]?.path ?? "/media/music", mapped: (calls.roots[0]?.path ?? "/media/music") !== "/media/music", reachable: calls.mounted || !!calls.roots.length, writable: calls.mounted || !!calls.roots.length, status: calls.mounted || calls.roots.length ? "writable" : "unreachable", rootId: calls.roots[0]?.id ?? null, freeBytes: calls.mounted || calls.roots.length ? 33 * 1024 ** 3 : null }] });
    if (path.includes("/library-promotions/profiles/")) {
      if (path.endsWith("/preview")) return reply({ script: profile.namingScript, scriptOnly: false, tokens: [], examples: [], errors: [] });
      const household = { values: profile, addWithoutApproval: "admins" };
      return reply(path.endsWith("/household") ? household : { values: profile, householdDefault: household, usesHouseholdDefault: true });
    }
    return route.fallback();
  });
  return calls;
}

for (const width of [1280, 390]) {
  test(`Kept setup stays calm and membership answers once at ${width} dark`, async ({ page }) => {
    await appearance(page, width, "dark");
    const calls = await fixture(page);
    await page.goto("/#/library/kept");
    const add = page.getByRole("button", { name: "Add selected to library", exact: true });
    await expect(add).toBeDisabled(); await expect(add).toHaveAttribute("title", "Adding to your library isn't set up");
    await page.getByRole("button", { name: "Actions for Check", exact: true }).click();
    await expect(page.getByRole("menuitem", { name: "Add to library", exact: true })).toHaveAttribute("aria-disabled", "true");
    await page.keyboard.press("Escape");
    await page.getByRole("button", { name: "Open song details for Check", exact: true }).click();
    const panel = page.getByRole("dialog", { name: file.album, exact: true });
    await expect(panel.getByRole("heading", { name: "Adding to your library isn't set up", exact: true })).toBeVisible();
    await expect(panel).toContainText("Allstarr needs write access to the music folder Jellyfin reads.");
    await expect(panel.getByRole("link", { name: "Open library settings", exact: true })).toBeVisible();
    await expect(panel.getByRole("alert")).toHaveCount(0);
    await expect(panel.getByRole("link", { name: "On the Road", exact: true })).toHaveAttribute("href", "#/library/playlists?playlist=road");
    expect(calls.membership).toBe(1); expect(calls.details).toBe(0); expect(calls.previews).toBe(0);
    await capture(page, "kept-not-set-up", width, "dark");
    await page.evaluate(() => (window as any).testUpdates.dispatchEvent(new MessageEvent("update", { data: JSON.stringify({ resource: "kept", resourceId: "copy", revision: 1 }) })));
    await expect(panel).toContainText("30 MB");
    expect(calls.membership).toBe(1);
    await panel.getByRole("link", { name: "Open library settings", exact: true }).click();
    await expect(page.getByRole("heading", { name: "No library folder yet", exact: true })).toBeVisible();
    const folders = page.getByRole("list", { name: "Jellyfin music folders", exact: true });
    await expect(folders.getByRole("listitem")).toHaveCount(1);
    await expect(folders.getByRole("listitem").first()).toContainText("Can't reach");
    await expect(page.getByText("- /media/music:/media/music", { exact: true })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Quality", exact: true })).toBeVisible();
    await expect(page.getByRole("heading", { name: "File names and folders", exact: true })).toBeVisible();
    await capture(page, "library-no-folder", width, "dark");
  });
}

test("Listeners see setup help in the add panel", async ({ page }) => {
  await fixture(page, false);
  await page.goto(`/#/library/to-library?recording=${recording}`);
  const panel = page.getByRole("dialog");
  await expect(panel).toContainText("Ask an administrator to set it up.");
  await expect(panel.getByRole("link", { name: "Open library settings" })).toHaveCount(0);
  await expect(panel.getByRole("alert")).toHaveCount(0);
});

test("Empty membership finishes with a clear answer", async ({ page }) => {
  const calls = await fixture(page, true, "empty"); await page.goto("/#/library/kept");
  await page.getByRole("button", { name: "Open song details for Check", exact: true }).click();
  await expect(page.getByRole("dialog")).toContainText("Not in any of your playlists");
  expect(calls.membership).toBe(1);
});

test("Membership uses the kept recording when no destination library is available", async ({ page }) => {
  const calls = await fixture(page, true, "found", false); await page.goto("/#/library/kept");
  await page.getByRole("button", { name: "Open song details for Check", exact: true }).click();
  await expect(page.getByRole("dialog").getByRole("link", { name: "On the Road", exact: true })).toBeVisible();
  expect(calls.membership).toBe(1); expect(calls.previews).toBe(0);
});

test("Membership errors finish loading with the unavailable message", async ({ page }) => {
  await fixture(page, true, "error"); await page.goto("/#/library/kept");
  await page.getByRole("button", { name: "Open song details for Check", exact: true }).click();
  await expect(page.getByRole("dialog")).toContainText("Playlist membership could not be loaded.");
  await expect(page.getByText("Loading playlists…", { exact: true })).toHaveCount(0);
});

test("An administrator maps and approves a discovered music folder without a required base", async ({ page }) => {
  const calls = await fixture(page); await page.goto("/#/settings/adding-to-library");
  await page.getByRole("button", { name: "Map /media/music", exact: true }).click();
  await page.getByRole("textbox", { name: "Path inside Allstarr", exact: true }).fill("/music");
  await page.getByRole("button", { name: "Use this library folder", exact: true }).click();
  const row = page.getByRole("list", { name: "Jellyfin music folders", exact: true }).getByRole("listitem");
  await expect(row).toContainText("In use"); await expect(row).toContainText("Inside Allstarr: /music");
  await expect(page.getByRole("button", { name: "Change /media/music", exact: true })).toBeVisible();
  expect(calls.roots).toHaveLength(1); expect(calls.roots[0]).toMatchObject({ path: "/music", backendPath: "/media/music", name: "Music" });
  await expect(page.getByRole("heading", { name: "No library folder yet", exact: true })).toHaveCount(0);
});

test("A linked music folder shows where it leads and is used there", async ({ page }) => {
  const calls = await fixture(page); calls.linked = true; await page.goto("/#/settings/adding-to-library");
  const row = page.getByRole("list", { name: "Jellyfin music folders", exact: true }).getByRole("listitem");
  await expect(row).toContainText("Ready"); await expect(row).toContainText("Link to /media/.music");
  await page.getByRole("button", { name: "Use /media/music", exact: true }).click();
  await expect(row).toContainText("In use");
  expect(calls.roots).toHaveLength(1); expect(calls.roots[0]).toMatchObject({ path: "/media/.music", backendPath: "/media/music", name: "Music" });
});

test("A folder mounted at Jellyfin's path is used in one click", async ({ page }) => {
  const calls = await fixture(page); calls.mounted = true; await page.goto("/#/settings/adding-to-library");
  const row = page.getByRole("list", { name: "Jellyfin music folders", exact: true }).getByRole("listitem");
  await expect(row).toContainText("Ready"); await expect(row).toContainText("33 GiB free");
  await expect(page.getByText("- /media/music:/media/music", { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Use /media/music", exact: true }).click();
  await expect(row).toContainText("In use");
  expect(calls.roots).toHaveLength(1); expect(calls.roots[0]).toMatchObject({ path: "/media/music", backendPath: "/media/music", name: "Music" });
  await expect(page.getByRole("textbox", { name: "Path inside Allstarr", exact: true })).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "No library folder yet", exact: true })).toHaveCount(0);
});
