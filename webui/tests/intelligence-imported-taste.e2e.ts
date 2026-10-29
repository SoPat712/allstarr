import { expect, test, type Page } from "@playwright/test";
import type { TasteProfile } from "../src/lib/api";
import { appearance, capture, mockForYou } from "./for-you-fixtures";

const toggleLabel = "Use imported history";

function initialTaste(useImportedHistory = false): TasteProfile {
  return {
    lately: { tags: [{ name: "Jazz", strength: 75, value: 3 }], artists: [] },
    usually: { tags: [{ name: "Soul", strength: 60, value: 8 }], artists: [] },
    latelyWeight: 0.5,
    nudges: [],
    paused: false,
    useImportedHistory,
  };
}

async function mockImportedTaste(page: Page, profile = initialTaste()) {
  const state = {
    profile: structuredClone(profile),
    reads: 0,
    failNext: false,
    mutations: [] as { method: string; path: string; body: Record<string, unknown> }[],
  };
  await mockForYou(page);
  await page.route("**/api/admin/taste", async route => {
    expect(route.request().method()).toBe("GET");
    state.reads++;
    return route.fulfill({ json: state.profile });
  });
  await page.route("**/api/admin/taste/imported-history", async route => {
    const request = route.request();
    const body = request.postDataJSON() as Record<string, unknown>;
    state.mutations.push({ method: request.method(), path: new URL(request.url()).pathname, body });
    expect(request.method()).toBe("PUT");
    if (state.failNext) {
      state.failNext = false;
      return route.fulfill({ status: 503, json: {} });
    }
    state.profile.useImportedHistory = body.enabled === true;
    return route.fulfill({ json: state.profile });
  });
  return state;
}

async function openTaste(page: Page) {
  await page.goto("/#/for-you?section=taste");
  await expect(page.getByRole("checkbox", { name: toggleLabel, exact: true })).toBeVisible();
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Imported taste setting at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    const state = await mockImportedTaste(page);
    await openTaste(page);
    await expect(page.getByRole("checkbox", { name: toggleLabel, exact: true })).not.toBeChecked();
    await expect(page.getByText("Imported plays shape your usual taste.", { exact: true })).toBeVisible();
    await expect(page.locator(".taste-settings .settings-row strong")).toHaveText(["Private listening (pause learning)", toggleLabel]);
    await capture(page, "imported-taste-setting", width, theme);
    expect(state.mutations).toEqual([]);
  });
}

test("Imported history can be enabled and disabled and keeps the saved value", async ({ page }) => {
  const state = await mockImportedTaste(page);
  await openTaste(page);
  const toggle = page.getByRole("checkbox", { name: toggleLabel, exact: true });
  await expect(toggle).not.toBeChecked();
  await toggle.click();
  await expect(toggle).toBeEnabled();
  await expect(toggle).toBeChecked();
  await page.reload();
  await expect(toggle).toBeChecked();
  await toggle.click();
  await expect(toggle).toBeEnabled();
  await expect(toggle).not.toBeChecked();
  await page.reload();
  await expect(toggle).not.toBeChecked();
  expect(state.mutations).toEqual([
    { method: "PUT", path: "/api/admin/taste/imported-history", body: { enabled: true } },
    { method: "PUT", path: "/api/admin/taste/imported-history", body: { enabled: false } },
  ]);
});

test("Imported history defaults to enabled when the current profile omits the setting", async ({ page }) => {
  const profile = initialTaste();
  delete profile.useImportedHistory;
  const state = await mockImportedTaste(page, profile);
  await openTaste(page);
  await expect(page.getByRole("checkbox", { name: toggleLabel, exact: true })).toBeChecked();
  expect(state.mutations).toEqual([]);
});

for (const enabled of [false, true]) test(`A failed imported history update keeps the saved ${enabled ? "enabled" : "disabled"} value`, async ({ page }) => {
  const state = await mockImportedTaste(page, initialTaste(enabled));
  await openTaste(page);
  const toggle = page.getByRole("checkbox", { name: toggleLabel, exact: true });
  await expect(toggle).toBeChecked({ checked: enabled });
  state.failNext = true;
  await toggle.click();
  await expect(page.getByText("Your taste could not be updated. Try again.", { exact: true })).toBeVisible();
  await expect(toggle).toBeEnabled();
  await expect(toggle).toBeChecked({ checked: enabled });
  expect(state.profile.useImportedHistory).toBe(enabled);
  expect(state.mutations).toEqual([
    { method: "PUT", path: "/api/admin/taste/imported-history", body: { enabled: !enabled } },
  ]);
  await toggle.click();
  await expect(toggle).toBeEnabled();
  await expect(toggle).toBeChecked({ checked: !enabled });
  expect(state.profile.useImportedHistory).toBe(!enabled);
});

test("Focus refresh reads the current imported history value", async ({ page }) => {
  const state = await mockImportedTaste(page);
  await openTaste(page);
  const toggle = page.getByRole("checkbox", { name: toggleLabel, exact: true });
  await expect(toggle).not.toBeChecked();
  for (const enabled of [true, false]) {
    const reads = state.reads;
    state.profile.useImportedHistory = enabled;
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await expect.poll(() => state.reads).toBeGreaterThan(reads);
    await expect(toggle).toBeChecked({ checked: enabled });
  }
  expect(state.mutations).toEqual([]);
});
