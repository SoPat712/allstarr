import { expect, test, type Page } from "@playwright/test";
import { mockForYou } from "./for-you-fixtures";
import type { AudioMuseConnection, AudioMuseStatus } from "../src/lib/api";

const ready: AudioMuseStatus = { reason: null, accessibleLibrarySongs: 1400, analyzedSongs: 1240, coverageComplete: true };

async function mockSoundSettings(page: Page, administrator = true, initialStatus = ready) {
  const state = {
    status: { ...initialStatus },
    connection: (initialStatus.reason === "not_enabled"
      ? { baseUrl: "", keyConfigured: false, revision: null }
      : { baseUrl: "http://sounds.invalid:8600", keyConfigured: true, revision: 1 }) as AudioMuseConnection,
    calls: [] as { path: string; method: string; body: Record<string, unknown> | null }[],
    reads: 0,
  };
  await mockForYou(page, { admin: administrator });
  await page.route("**/api/admin/audiomuse/**", async route => {
    const request = route.request(), path = new URL(request.url()).pathname.split("/").at(-1)!;
    const body = request.postData() ? request.postDataJSON() : null;
    state.calls.push({ path, method: request.method(), body });
    if (path === "status") {
      state.reads++;
      return route.fulfill({ json: state.status });
    }
    if (path === "connection") {
      if (body) {
        state.connection = { baseUrl: body.baseUrl, keyConfigured: state.connection.keyConfigured || Boolean(body.apiKey), revision: 1 };
        state.status = { ...ready };
      }
      return route.fulfill({ json: state.connection });
    }
    return route.fulfill({ status: 404 });
  });
  return state;
}

async function openSoundSettings(page: Page) {
  await page.goto("/#/settings/sound-analysis");
  await expect(page.getByRole("heading", { name: "Sound analysis (AudioMuse)", exact: true })).toBeVisible();
}

test("Administrator connects and a blank key keeps the saved key", async ({ page }) => {
  const state = await mockSoundSettings(page, true, { ...ready, reason: "not_enabled", analyzedSongs: 0 });
  await openSoundSettings(page);
  await expect(page.getByText("Not set up", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Edit connection", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "AudioMuse connection", exact: true });
  await expect(dialog.getByLabel("API key", { exact: true })).toHaveAttribute("type", "password");
  await dialog.getByLabel("AudioMuse address").fill("http://sounds.invalid:8600");
  await dialog.getByLabel("API key", { exact: true }).fill("test-secret");
  await dialog.getByRole("button", { name: "Save", exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByText("Key saved", { exact: true })).toBeVisible();
  await expect(page.getByText("Connected · 1,240 songs analyzed", { exact: true })).toBeVisible();
  await expect(page.locator("body")).not.toContainText("test-secret");
  await page.getByRole("button", { name: "Edit connection", exact: true }).click();
  await expect(dialog.getByLabel("API key", { exact: true })).toHaveValue("");
  await dialog.getByLabel("AudioMuse address").fill("http://sounds.invalid:8601");
  await dialog.getByRole("button", { name: "Save", exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(state.calls.filter(call => call.method === "PUT").map(call => call.body)).toEqual([
    { baseUrl: "http://sounds.invalid:8600", apiKey: "test-secret", revision: null },
    { baseUrl: "http://sounds.invalid:8601", revision: 1 },
  ]);
});

test("Listeners cannot access sound analysis connection settings", async ({ page }) => {
  const state = await mockSoundSettings(page, false);
  await page.goto("/#/settings/sound-analysis");
  await expect(page.getByRole("heading", { name: "This workspace is for administrators.", exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Sound analysis (AudioMuse)", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Edit connection", exact: true })).toHaveCount(0);
  expect(state.calls.some(call => call.path === "connection")).toBe(false);
});

test("Unavailable connection and incomplete coverage preserve an open draft on refresh", async ({ page }) => {
  const state = await mockSoundSettings(page, true, { ...ready, reason: "not_reachable" });
  await openSoundSettings(page);
  await expect(page.getByText("Can't reach AudioMuse", { exact: true })).toBeVisible();
  state.status = { ...ready, analyzedSongs: 120, coverageComplete: false };
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(page.getByText("Connected · At least 120 songs analyzed", { exact: true })).toBeVisible();
  const reads = state.reads;
  await page.getByRole("button", { name: "Edit connection", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "AudioMuse connection", exact: true });
  await dialog.getByLabel("AudioMuse address").fill("http://draft.invalid");
  state.connection.baseUrl = "http://changed.invalid:8600";
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect.poll(() => state.reads).toBeGreaterThan(reads);
  await expect(dialog.getByLabel("AudioMuse address")).toHaveValue("http://draft.invalid");
  await expect(dialog.getByRole("button", { name: "Cancel", exact: true })).toHaveAttribute("type", "button");
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.getByRole("button", { name: "Refresh sound analysis", exact: true })).toHaveAttribute("aria-busy", "false");
  expect(state.calls.filter(call => call.method === "PUT")).toEqual([]);
});

test("Connection saves can be retried after a temporary error", async ({ page }) => {
  const state = await mockSoundSettings(page);
  let reject = true;
  await page.route("**/api/admin/audiomuse/connection", async route => {
    if (route.request().method() !== "PUT" || !reject) return route.fallback();
    reject = false;
    return route.fulfill({ status: 503, json: {} });
  });
  await openSoundSettings(page);
  await page.getByRole("button", { name: "Edit connection", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "AudioMuse connection", exact: true });
  await dialog.getByLabel("AudioMuse address").fill("http://sounds.invalid:8601");
  await dialog.getByRole("button", { name: "Save", exact: true }).click();
  await expect(dialog.getByRole("alert")).toContainText("The connection could not be saved.");
  await expect(dialog.getByRole("button", { name: "Save", exact: true })).toBeEnabled();
  await expect(dialog.getByLabel("AudioMuse address")).toHaveValue("http://sounds.invalid:8601");
  await dialog.getByRole("button", { name: "Save", exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(state.connection.baseUrl).toBe("http://sounds.invalid:8601");
});
