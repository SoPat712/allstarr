import { expect, test, type Page } from "@playwright/test";
import type { TasteProfile, TasteStrength } from "../src/lib/api";
import { appearance, capture, mockForYou } from "./for-you-fixtures";

function rows(first: string, prefix: string): TasteStrength[] {
  return Array.from({ length: 10 }, (_, index) => ({ name: index ? `${prefix} ${index + 1}` : first, strength: 100 - index * 9, value: 10 - index }));
}

function populatedTaste(): TasteProfile {
  return {
    lately: { tags: rows("R&B", "Recent tag"), artists: rows("Artist X", "Recent artist") },
    usually: { tags: rows("EDM", "Usual tag"), artists: rows("Artist Y", "Usual artist") },
    latelyWeight: 0.5,
    nudges: [
      { kind: "tag", name: "Jazz", mode: "more", expiresAt: "2026-10-29T04:00:00Z" },
      { kind: "tag", name: "Rock", mode: "less", expiresAt: null },
      { kind: "artist", name: "Hidden artist", mode: "never", expiresAt: null },
    ],
    paused: false,
  };
}

async function mockTaste(page: Page, initial = populatedTaste()) {
  const state = { profile: structuredClone(initial), reads: 0, mutations: [] as Array<{ method: string; path: string; query: string; body: Record<string, unknown> | null }> };
  await mockForYou(page);
  await page.route("**/api/admin/taste**", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (!["127.0.0.1", "localhost"].includes(url.hostname)) return route.abort();
    const body = request.postData() ? request.postDataJSON() : null;
    if (request.method() !== "GET") state.mutations.push({ method: request.method(), path, query: url.search, body });
    if (request.method() === "GET" && path === "/api/admin/taste") state.reads++;
    else if (path === "/api/admin/taste/nudge" && request.method() === "PUT") {
      state.profile.nudges = state.profile.nudges.filter(item => item.kind !== body.kind || item.name !== body.name);
      state.profile.nudges.push({ kind: body.kind, name: body.name, mode: body.mode, expiresAt: body.todayOnly ? "2026-10-29T04:00:00Z" : null });
    } else if (path === "/api/admin/taste/nudge" && request.method() === "DELETE") {
      state.profile.nudges = state.profile.nudges.filter(item => item.kind !== url.searchParams.get("kind") || item.name !== url.searchParams.get("name"));
    } else if (["/api/admin/taste/pause", "/api/admin/taste/resume"].includes(path)) state.profile.paused = path.endsWith("/pause");
    else if (path === "/api/admin/taste/reset") {
      state.profile.lately = { tags: [], artists: [] };
      state.profile.usually = { tags: [], artists: [] };
      if (body.clearNudges) state.profile.nudges = [];
    } else return route.fallback();
    return route.fulfill({ json: state.profile });
  });
  return state;
}

async function openTaste(page: Page) {
  await page.goto("/#/for-you?section=taste");
  await expect(page.getByRole("button", { name: "Reset taste", exact: true })).toBeVisible();
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Taste details and reset dialog at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    const state = await mockTaste(page);
    await openTaste(page);
    await expect(page.getByText("Lately you've been into R&B and Recent tag 2. Usually it's EDM, Usual tag 2 and Usual tag 3.", { exact: true })).toBeVisible();
    await expect(page.getByText("Today's listening counts most right now.", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Steer R&B", exact: true })).toBeVisible();
    const details = page.locator("details").filter({ hasText: "See the details" });
    await expect(details.getByRole("progressbar").first()).not.toBeVisible();
    await page.getByText("See the details", { exact: true }).click();
    const lately = details.locator(".taste-layers > section").filter({ has: page.getByRole("heading", { name: "Lately", exact: true }) });
    const usually = details.locator(".taste-layers > section").filter({ has: page.getByRole("heading", { name: "Usually", exact: true }) });
    for (const layer of [lately, usually]) {
      await expect(layer.getByRole("heading", { name: "Styles", exact: true })).toBeVisible();
      await expect(layer.getByRole("heading", { name: "Artists", exact: true })).toBeVisible();
      await expect(layer.getByRole("progressbar")).toHaveCount(10);
    }
    await expect(lately.getByText("Recent tag 5", { exact: true })).toBeVisible();
    await expect(lately.getByText("Recent tag 6", { exact: true })).toHaveCount(0);
    await expect(details.getByText("Recent tag 9", { exact: true })).toHaveCount(0);
    await expect(details.getByText("Usual artist 9", { exact: true })).toHaveCount(0);
    await expect(lately.getByRole("progressbar", { name: "R&B strength", exact: true })).toHaveAttribute("aria-valuenow", "100");
    const recentBox = await lately.boundingBox(), usualBox = await usually.boundingBox();
    expect(recentBox).not.toBeNull(); expect(usualBox).not.toBeNull();
    if (width === 390) expect(usualBox!.y).toBeGreaterThanOrEqual(recentBox!.y + recentBox!.height);
    else expect(Math.abs(usualBox!.y - recentBox!.y)).toBeLessThan(2);
    await expect(page.getByText("More Jazz · today", { exact: true })).toBeVisible();
    await expect(page.getByText("Less Rock · until I change it", { exact: true })).toBeVisible();
    await expect(page.getByRole("region", { name: "Hidden", exact: true }).getByText("Hidden artist", { exact: true })).toBeVisible();
    await capture(page, "taste-details", width, theme);
    await page.getByRole("button", { name: "Reset taste", exact: true }).click();
    const dialog = page.getByRole("alertdialog", { name: "Reset taste", exact: true });
    await expect(dialog.getByRole("checkbox", { name: "Also remove my nudges", exact: true })).not.toBeChecked();
    await capture(page, "taste-reset", width, theme);
    await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    expect(state.mutations).toEqual([]);
  });
}

for (const mode of ["more", "less"] as const) for (const todayOnly of [true, false]) {
  test(`${mode} applies ${todayOnly ? "today only" : "until changed"} with the browser timezone`, async ({ page }) => {
    const state = await mockTaste(page);
    await openTaste(page);
    const label = mode === "more" ? "More" : "Less";
    await page.getByRole("button", { name: "Steer R&B", exact: true }).click();
    await page.getByRole("navigation", { name: "Duration for R&B", exact: true }).getByRole("tab", { name: todayOnly ? "Today only" : "Until I change it", exact: true }).click();
    await page.getByRole("button", { name: label, exact: true }).click();
    await expect(page.getByRole("region", { name: "Active nudges", exact: true }).getByText(`${label} R&B · ${todayOnly ? "today" : "until I change it"}`, { exact: true })).toBeVisible();
    const timeZoneId = await page.evaluate(() => Intl.DateTimeFormat().resolvedOptions().timeZone);
    expect(state.mutations).toEqual([{ method: "PUT", path: "/api/admin/taste/nudge", query: "", body: { kind: "tag", name: "R&B", mode, todayOnly, timeZoneId } }]);
  });
}

test("Never hides an artist persistently and Undo removes the preference", async ({ page }) => {
  const state = await mockTaste(page);
  await openTaste(page);
  await page.getByRole("button", { name: "Steer Artist X", exact: true }).click();
  await expect(page.getByRole("navigation", { name: "Duration for Artist X", exact: true }).getByRole("tab", { name: "Today only", exact: true })).toHaveAttribute("aria-selected", "true");
  await page.getByRole("button", { name: "Never", exact: true }).click();
  const hidden = page.getByRole("region", { name: "Hidden", exact: true });
  await expect(hidden.getByText("Artist X", { exact: true })).toBeVisible();
  await hidden.getByRole("button", { name: "Undo Artist X", exact: true }).click();
  await expect(hidden.getByText("Artist X", { exact: true })).toHaveCount(0);
  expect(state.mutations[0].body).toEqual({ kind: "artist", name: "Artist X", mode: "never", todayOnly: false, timeZoneId: await page.evaluate(() => Intl.DateTimeFormat().resolvedOptions().timeZone) });
  expect(state.mutations[1]).toEqual({ method: "DELETE", path: "/api/admin/taste/nudge", query: "?kind=artist&name=Artist+X", body: null });
});

test("Pause learning can be resumed", async ({ page }) => {
  const state = await mockTaste(page);
  await openTaste(page);
  const toggle = page.getByRole("checkbox", { name: "Private listening (pause learning)", exact: true });
  await toggle.check();
  await expect(toggle).toBeChecked();
  await toggle.uncheck();
  await expect(toggle).not.toBeChecked();
  expect(state.mutations.map(({ method, path }) => ({ method, path }))).toEqual([
    { method: "POST", path: "/api/admin/taste/pause" }, { method: "POST", path: "/api/admin/taste/resume" },
  ]);
});

for (const clearNudges of [false, true]) test(`Reset taste ${clearNudges ? "clears" : "keeps"} nudges`, async ({ page }) => {
  const state = await mockTaste(page);
  await openTaste(page);
  await page.getByRole("button", { name: "Reset taste", exact: true }).click();
  const dialog = page.getByRole("alertdialog", { name: "Reset taste", exact: true });
  if (clearNudges) await dialog.getByRole("checkbox", { name: "Also remove my nudges", exact: true }).check();
  await dialog.getByRole("button", { name: "Reset taste", exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByText("Play some music and your taste will show up here.", { exact: true })).toBeVisible();
  await expect(page.getByText(clearNudges ? "No active nudges." : "More Jazz · today", { exact: true })).toBeVisible();
  const hidden = page.getByRole("region", { name: "Hidden", exact: true });
  await expect(hidden.getByText(clearNudges ? "Nothing hidden." : "Hidden artist", { exact: true })).toBeVisible();
  expect(state.mutations).toEqual([{ method: "POST", path: "/api/admin/taste/reset", query: "", body: { clearNudges } }]);
});

test("Empty taste uses the usual listening explanation", async ({ page }) => {
  await mockTaste(page, { lately: { tags: [], artists: [] }, usually: { tags: [], artists: [] }, latelyWeight: 0.49, nudges: [], paused: false });
  await openTaste(page);
  await expect(page.getByText("Play some music and your taste will show up here.", { exact: true })).toBeVisible();
  await expect(page.getByText("Your usual taste counts most right now.", { exact: true })).toBeVisible();
  await expect(page.getByText("No active nudges.", { exact: true })).toBeVisible();
  await expect(page.getByText("Nothing hidden.", { exact: true })).toBeVisible();
});

test("Live refresh preserves an open reset dialog and visible lists", async ({ page }) => {
  const state = await mockTaste(page);
  await openTaste(page);
  await page.getByRole("button", { name: "Reset taste", exact: true }).click();
  const reads = state.reads;
  state.profile.lately.tags[0].name = "Fresh tag";
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect.poll(() => state.reads).toBeGreaterThan(reads);
  await expect(page.getByRole("alertdialog", { name: "Reset taste", exact: true })).toBeVisible();
  await expect(page.locator(".skeleton-panel")).toHaveCount(0);
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.getByRole("button", { name: "Steer Fresh tag", exact: true })).toBeVisible();
});
