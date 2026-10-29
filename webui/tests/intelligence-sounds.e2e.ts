import { expect, test, type Page } from "@playwright/test";
import { appearance, capture, mockIntelligence } from "./intelligence-fixtures";
import type { AudioMuseConnection, AudioMusePreview, AudioMuseStatus } from "../src/lib/api";

const seed = { id: "seed", title: "Golden hour", artist: "Mira", album: "Warm light" };
const tracks = Array.from({ length: 25 }, (_, i) => ({ id: `song-${i}`, title: ["Blue morning", "Quiet water", "Afterglow"][i % 3] + (i > 2 ? ` ${i + 1}` : ""), artist: ["North", "Cedar", "Willow"][i % 3], album: "Open skies" }));
const ready: AudioMuseStatus = { reason: null, accessibleLibrarySongs: 1400, analyzedSongs: 1240, coverageComplete: true };
const preview: AudioMusePreview = { reason: null, tracks, eligibleCount: 40, previewId: "preview", recipeVersion: null };

async function mockSounds(page: Page, administrator = false, initialStatus = ready) {
  const state = { status: { ...initialStatus }, connection: (initialStatus.reason === "not_enabled"
    ? { baseUrl: "", keyConfigured: false, revision: null }
    : { baseUrl: "http://sounds.invalid:8600", keyConfigured: true, revision: 1 }) as AudioMuseConnection,
    preview: structuredClone(preview), calls: [] as { path: string; method: string; body: Record<string, unknown> | null }[], reads: 0 };
  await mockIntelligence(page);
  await page.route("**/api/admin/auth/me", route => route.fulfill({ json: { authenticated: true, backend: "Jellyfin", features: { intelligence: false }, user: { id: "listener", name: "Listener", isAdministrator: administrator } } }));
  await page.route("**/api/admin/onboarding/status", route => route.fulfill({ json: { completed: true, shouldRedirectToSetup: false, setupOpen: false, completedSteps: ["backend-identity"], revision: 1, recoveryNotices: [] } }));
  await page.route("**/api/admin/audiomuse/**", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname.split("/").at(-1)!;
    const body = request.postData() ? request.postDataJSON() : null;
    state.calls.push({ path, method: request.method(), body });
    if (path === "status") { state.reads++; return route.fulfill({ json: state.status }); }
    if (path === "connection") {
      if (body) {
        state.connection = { baseUrl: body.baseUrl, keyConfigured: state.connection.keyConfigured || Boolean(body.apiKey), revision: 1 };
        state.status = { ...ready };
      }
      return route.fulfill({ json: state.connection });
    }
    if (path === "seeds") {
      expect(url.searchParams.get("query")).toBe("gold");
      return route.fulfill({ json: { ...preview, tracks: [seed] } });
    }
    if (path === "similar" || path === "mood") return route.fulfill({ json: state.preview });
    if (path === "save") return route.fulfill({ json: { reason: null, playlistId: "native-playlist" } });
    return route.fulfill({ status: 404 });
  });
  return state;
}

async function openSounds(page: Page) {
  await page.goto("/#/intelligence?section=sounds");
  await expect(page.getByRole("heading", { name: "Sounds like this", exact: true })).toBeVisible();
}
async function pickSeed(page: Page) {
  await page.getByRole("searchbox", { name: "Find a song from your library" }).fill("gold");
  await page.getByRole("button", { name: "Golden hour by Mira", exact: true }).click();
  await page.getByRole("region", { name: "Sounds like this", exact: true }).getByRole("button", { name: "Preview", exact: true }).click();
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Sounds preview and connection at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    const state = await mockSounds(page, true);
    await openSounds(page);
    await expect(page.getByText("Connected · 1,240 songs analyzed", { exact: true })).toBeVisible();
    await pickSeed(page);
    await expect(page.getByRole("heading", { name: "Songs that sound like Golden hour" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Songs that sound like Golden hour" })).toBeInViewport();
    await expect(page.getByRole("region", { name: "Playlist preview" }).getByRole("listitem")).toHaveCount(25);
    await capture(page, "sounds", width, theme);
    await page.getByRole("button", { name: "Edit connection", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "AudioMuse connection", exact: true });
    await expect(dialog.getByLabel("API key", { exact: true })).toHaveAttribute("type", "password");
    await capture(page, "sounds-connection", width, theme);
    await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    expect(state.calls.filter(call => call.method === "PUT")).toEqual([]);
  });
}

test("Administrator connects and a blank key keeps the saved key", async ({ page }) => {
  const state = await mockSounds(page, true, { ...ready, reason: "not_enabled", analyzedSongs: 0 });
  await openSounds(page);
  await expect(page.getByText("Not set up", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Edit connection" }).click();
  const dialog = page.getByRole("dialog", { name: "AudioMuse connection", exact: true });
  await dialog.getByLabel("AudioMuse address").fill("http://sounds.invalid:8600");
  await dialog.getByLabel("API key", { exact: true }).fill("test-secret");
  await dialog.getByRole("button", { name: "Save", exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByText("Key saved", { exact: true })).toBeVisible();
  await expect(page.getByText("Connected · 1,240 songs analyzed", { exact: true })).toBeVisible();
  await expect(page.locator("body")).not.toContainText("test-secret");
  await page.getByRole("button", { name: "Edit connection" }).click();
  await expect(dialog.getByLabel("API key", { exact: true })).toHaveValue("");
  await dialog.getByLabel("AudioMuse address").fill("http://sounds.invalid:8601");
  await dialog.getByRole("button", { name: "Save", exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(state.calls.filter(call => call.method === "PUT").map(call => call.body)).toEqual([
    { baseUrl: "http://sounds.invalid:8600", apiKey: "test-secret", revision: null },
    { baseUrl: "http://sounds.invalid:8601", revision: 1 },
  ]);
});

test("Listeners see Sounds only while connected and never see connection settings", async ({ page }) => {
  const state = await mockSounds(page, false, { ...ready, reason: "not_enabled" });
  await page.goto("/#/intelligence?section=sounds");
  await expect.poll(() => state.reads).toBeGreaterThan(0);
  await expect(page.getByRole("tab", { name: "Sounds", exact: true })).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Sounds like this", exact: true })).toHaveCount(0);
  state.status = { ...ready };
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(page.getByRole("tab", { name: "Sounds", exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Sounds like this", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Edit connection" })).toHaveCount(0);
  expect(state.calls.some(call => call.path === "connection")).toBe(false);
  state.status.reason = "not_reachable";
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(page.getByRole("tab", { name: "Sounds", exact: true })).toHaveCount(0);
});

test("Similar search previews and saves an editable playlist name", async ({ page }) => {
  const state = await mockSounds(page);
  await openSounds(page);
  const tabs = page.getByRole("navigation", { name: "Intelligence sections" }).getByRole("tab");
  await expect(tabs).toHaveText(["For you", "Sounds", "New releases", "Your taste", "Your stats"]);
  await pickSeed(page);
  await expect(page.getByText("Blue morning", { exact: true })).toBeVisible();
  await expect(page.getByLabel("Playlist name")).toHaveValue("Sounds like Golden hour");
  await page.getByLabel("Playlist name").fill("Evening listening");
  await page.getByRole("button", { name: "Save as playlist", exact: true }).click();
  await expect(page.getByText("Saved to your playlists", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Open", exact: true })).toBeVisible();
  expect(state.calls.find(call => call.path === "similar")?.body).toEqual({ seedId: "seed", n: 25 });
  expect(state.calls.find(call => call.path === "save")?.body).toEqual({ previewId: "preview", name: "Evening listening" });
});

test("Similar previews explain unanalyzed songs and small result pools", async ({ page }) => {
  const state = await mockSounds(page);
  state.preview = { ...preview, reason: "song_not_analyzed", tracks: [], previewId: null };
  await openSounds(page);
  await pickSeed(page);
  await expect(page.getByText("AudioMuse hasn't analyzed this song yet", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Save as playlist" })).toHaveCount(0);
  state.preview = { ...preview, tracks: tracks.slice(0, 6), eligibleCount: 6 };
  await page.getByRole("region", { name: "Sounds like this", exact: true }).getByRole("button", { name: "Preview", exact: true }).click();
  await expect(page.getByText("Only 6 similar songs in your library", { exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Playlist preview" }).getByRole("listitem")).toHaveCount(6);
});

for (const mood of ["Calm", "Workout"]) test(`${mood} previews analyzed songs and saves`, async ({ page }) => {
  const state = await mockSounds(page);
  await openSounds(page);
  await page.getByRole("article", { name: mood, exact: true }).getByRole("button", { name: "Preview", exact: true }).click();
  await expect(page.getByRole("heading", { name: `${mood} playlist`, exact: true })).toBeVisible();
  await expect(page.getByText("From 1,240 analyzed songs", { exact: true })).toBeVisible();
  await expect(page.getByLabel("Playlist name")).toHaveValue(mood);
  await page.getByRole("button", { name: "Save as playlist", exact: true }).click();
  await expect(page.getByText("Saved to your playlists", { exact: true })).toBeVisible();
  expect(state.calls.find(call => call.path === "mood")?.body).toEqual({ mood: mood.toLowerCase(), n: 25 });
});

test("Unavailable connection uses plain words and incomplete coverage is a lower bound", async ({ page }) => {
  const state = await mockSounds(page, true, { ...ready, reason: "not_reachable" });
  await openSounds(page);
  await expect(page.getByText("Can't reach AudioMuse", { exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Sounds like this" }).getByRole("button", { name: "Preview" })).toBeDisabled();
  state.status = { ...ready, analyzedSongs: 120, coverageComplete: false };
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(page.getByText("Connected · At least 120 songs analyzed", { exact: true })).toBeVisible();
  const reads = state.reads;
  await page.getByRole("button", { name: "Edit connection" }).click();
  await page.getByLabel("AudioMuse address").fill("http://draft.invalid");
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect.poll(() => state.reads).toBeGreaterThan(reads);
  await expect(page.getByLabel("AudioMuse address")).toHaveValue("http://draft.invalid");
  await page.getByRole("dialog").getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.getByRole("button", { name: "Refresh sounds" })).toHaveAttribute("aria-busy", "false");
});

test("Connection saves can be retried after a temporary error", async ({ page }) => {
  await mockSounds(page, true);
  let reject = true;
  await page.route("**/api/admin/audiomuse/connection", async route => {
    if (route.request().method() !== "PUT" || !reject) return route.fallback();
    reject = false;
    return route.fulfill({ status: 503, json: {} });
  });
  await openSounds(page);
  await page.getByRole("button", { name: "Edit connection" }).click();
  const dialog = page.getByRole("dialog", { name: "AudioMuse connection", exact: true });
  await dialog.getByLabel("AudioMuse address").fill("http://sounds.invalid:8600");
  await dialog.getByRole("button", { name: "Save", exact: true }).click();
  await expect(dialog.getByRole("alert")).toBeVisible();
  await expect(dialog.getByRole("button", { name: "Save", exact: true })).toBeEnabled();
  await dialog.getByRole("button", { name: "Save", exact: true }).click();
  await expect(dialog).toBeHidden();
});
