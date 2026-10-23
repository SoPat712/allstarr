import { expect, test, type Page, type Route } from "@playwright/test";

type PlaybackStart = "FastStart" | "Balanced" | "BestQuality";

const defaults = {
  explicitFilter: "All",
  showExternalLabel: true,
  showExplicitLabel: true,
  lyricsOrder: "backend,kept",
};

async function json(route: Route, body: unknown) {
  await route.fulfill({ contentType: "application/json", body: JSON.stringify(body) });
}

async function mockPreferences(page: Page, preferences: (route: Route) => Promise<void>, administrator = false) {
  await page.route("**/api/admin/**", async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/admin/auth/me") return json(route, {
      authenticated: true, backend: "Jellyfin",
      user: { id: "listener", name: "Listener", isAdministrator: administrator },
    });
    if (path === "/api/admin/auth/oidc/status") return json(route, { enabled: false });
    if (path === "/api/admin/onboarding/status") return json(route, {
      completed: true, setupOpen: false, shouldRedirectToSetup: false,
      schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"],
      completionSource: "setup-guide", revision: 1, recoveryNotices: [],
      migration: { available: false, completed: false, firstRun: false },
    });
    if (path === "/api/admin/preferences") return preferences(route);
    if (path === "/api/admin/updates/stream")
      return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return json(route, {});
  });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Playback start saves and resets using keyboard radios at ${width}px ${theme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    let playbackStart: PlaybackStart = "Balanced";
    let revision = "initial";
    let usesHouseholdDefaults = false;
    const writes: Array<{ method: string; body: Record<string, unknown> }> = [];
    let releaseSave: () => void = () => {};
    const saveReleased = new Promise<void>(resolve => { releaseSave = resolve; });
    await mockPreferences(page, async route => {
      const method = route.request().method();
      if (method !== "GET") {
        const body = route.request().postDataJSON() as Record<string, unknown>;
        writes.push({ method, body });
        if (method === "PUT") {
          await saveReleased;
          playbackStart = body.playbackStart as PlaybackStart;
          revision = "saved";
        } else {
          playbackStart = "FastStart";
          revision = "reset";
          usesHouseholdDefaults = true;
        }
      }
      return json(route, {
        values: { ...defaults, playbackStart },
        householdDefaults: { ...defaults, playbackStart: "FastStart" },
        revision, usesHouseholdDefaults,
      });
    });
    await page.goto("/#/preferences");
    const region = page.getByRole("region", { name: "Playback start", exact: true });
    const group = region.getByRole("radiogroup", { name: "Playback start", exact: true });
    const fast = group.getByRole("radio", { name: "Fast start", exact: true });
    const balanced = group.getByRole("radio", { name: "Balanced", exact: true });
    const best = group.getByRole("radio", { name: "Best quality", exact: true });
    await expect(balanced).toBeChecked();
    await expect(balanced).toHaveAttribute("tabindex", "0");
    await expect(fast).toHaveAttribute("tabindex", "-1");
    await expect(best).toHaveAttribute("tabindex", "-1");
    await expect(region).toContainText("lossless");
    await expect(region).toContainText("3 seconds");
    await expect(region).toContainText("highest quality");
    await expect(page.getByRole("button", { name: "Save preferences", exact: true })).toBeDisabled();

    await balanced.focus();
    await page.keyboard.press("ArrowLeft");
    await expect(fast).toBeChecked();
    await expect(fast).toBeFocused();
    await page.keyboard.press("ArrowLeft");
    await expect(best).toBeChecked();
    await expect(best).toBeFocused();
    await page.keyboard.press("ArrowRight");
    await expect(fast).toBeChecked();
    await page.keyboard.press("ArrowDown");
    await expect(balanced).toBeChecked();
    await expect(page.getByRole("button", { name: "Save preferences", exact: true })).toBeDisabled();
    await page.keyboard.press("ArrowRight");
    await expect(best).toBeChecked();
    await page.getByRole("button", { name: "Save preferences", exact: true }).click();
    await expect.poll(() => writes).toEqual([{
      method: "PUT",
      body: { explicitFilter: "All", showExternalLabel: true, showExplicitLabel: true,
        playbackStart: "BestQuality", expectedRevision: "initial" },
    }]);
    for (const radio of [fast, balanced, best]) await expect(radio).toBeDisabled();
    releaseSave();
    await expect(page.getByRole("status").filter({ hasText: "Listening preferences saved." })).toBeVisible();
    await expect(best).toBeChecked();
    await expect(best).toBeEnabled();
    await expect(page.getByRole("button", { name: "Save preferences", exact: true })).toBeDisabled();
    await region.scrollIntoViewIfNeeded();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const radio of [fast, balanced, best]) {
      const box = await radio.boundingBox();
      expect(box).not.toBeNull();
      expect(box!.height).toBeGreaterThanOrEqual(44);
      expect(box!.width).toBeGreaterThanOrEqual(44);
      expect(await radio.locator("span").evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
      expect(await radio.locator("span").evaluate(element => element.scrollHeight)).toBeLessThanOrEqual(20);
    }
    expect(await group.locator("[aria-hidden=true]").first().evaluate(element => getComputedStyle(element).transitionDuration)).toBe("0s");
    await page.screenshot({ path: testInfo.outputPath(`playback-start-${width}-${theme}.png`) });
    await page.getByRole("button", { name: "Reset to household defaults", exact: true }).click();
    await expect(fast).toBeChecked();
    await expect(page.getByRole("status").filter({ hasText: "Household defaults restored." })).toBeVisible();
    expect(writes[1]).toEqual({ method: "DELETE", body: { expectedRevision: "saved" } });
    await expect(page.getByText("Using household defaults", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Reset to household defaults", exact: true })).toBeDisabled();
  });
}

test("Playback start defaults to Balanced when older responses omit the preference", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 320, height: 900 });
  const writes: Record<string, unknown>[] = [];
  await mockPreferences(page, async route => {
    if (route.request().method() === "PUT") writes.push(route.request().postDataJSON());
    return json(route, { values: defaults, householdDefaults: defaults, revision: "legacy", usesHouseholdDefaults: true });
  });
  await page.goto("/#/preferences");
  const group = page.getByRole("radiogroup", { name: "Playback start", exact: true });
  await expect(group.getByRole("radio", { name: "Balanced", exact: true })).toBeChecked();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  for (const name of ["Fast start", "Best quality", "Balanced"]) {
    const radio = group.getByRole("radio", { name, exact: true });
    await radio.click();
    await expect(radio).toBeChecked();
    expect(await radio.locator("span").evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    expect(await radio.locator("span").evaluate(element => element.scrollHeight)).toBeLessThanOrEqual(40);
  }
  await group.scrollIntoViewIfNeeded();
  await page.screenshot({ path: testInfo.outputPath("playback-start-320.png") });
  await expect(page.getByRole("button", { name: "Save preferences", exact: true })).toBeDisabled();
  await page.getByRole("checkbox", { name: "Show [A] for external tracks", exact: true }).click();
  await page.getByRole("button", { name: "Save preferences", exact: true }).click();
  await expect.poll(() => writes[0]).toEqual({ explicitFilter: "All", showExternalLabel: false,
    showExplicitLabel: true, playbackStart: "Balanced", expectedRevision: "legacy" });
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Household Playback start uses the shared control and saves configuration at ${width}px ${theme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await mockPreferences(page, route => json(route, {}), true);
    await page.route("**/api/admin/ui/schema", route => json(route, {
      configSections: [{ id: "general", label: "Playback", fields: [{
        key: "PLAYBACK_START_PREFERENCE", label: "Playback start", type: "select",
        valuePath: "playback.startPreference", options: ["FastStart", "Balanced", "BestQuality"],
        helpText: "The default playback start choice for every listener.",
      }] }],
    }));
    let playbackStart: PlaybackStart = "Balanced";
    const writes: unknown[] = [];
    await page.route("**/api/admin/config", async route => {
      if (route.request().method() === "POST") {
        const body = route.request().postDataJSON();
        writes.push(body);
        playbackStart = body.updates.PLAYBACK_START_PREFERENCE;
        return json(route, { message: "Runtime configuration updated.", requiresRestart: false });
      }
      return json(route, { playback: { startPreference: playbackStart } });
    });
    await page.goto("/#/settings/general");
    const form = page.getByRole("form", { name: "Playback settings", exact: true });
    const group = form.getByRole("radiogroup", { name: "Playback start", exact: true });
    await expect(group.getByRole("radio", { name: "Balanced", exact: true })).toBeChecked();
    await expect(form.getByRole("button", { name: "Save Playback", exact: true })).toBeDisabled();
    await group.getByRole("radio", { name: "Balanced", exact: true }).focus();
    await page.keyboard.press("ArrowRight");
    await expect(group.getByRole("radio", { name: "Best quality", exact: true })).toBeChecked();
    await form.getByRole("button", { name: "Save Playback", exact: true }).click();
    await expect.poll(() => writes).toEqual([{ updates: { PLAYBACK_START_PREFERENCE: "BestQuality" } }]);
    await expect(page.getByRole("status").filter({ hasText: "Playback settings saved." })).toBeVisible();
    await expect(group.getByRole("radio", { name: "Best quality", exact: true })).toBeChecked();
    await expect(form.getByRole("button", { name: "Save Playback", exact: true })).toBeDisabled();
    for (const radio of await group.getByRole("radio").all()) {
      const box = await radio.boundingBox();
      expect(box).not.toBeNull();
      expect(box!.height).toBeGreaterThanOrEqual(44);
      expect(box!.width).toBeGreaterThanOrEqual(44);
      expect(await radio.locator("span").evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
      expect(await radio.locator("span").evaluate(element => element.scrollHeight)).toBeLessThanOrEqual(20);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`household-playback-start-${width}-${theme}.png`) });
  });
}
