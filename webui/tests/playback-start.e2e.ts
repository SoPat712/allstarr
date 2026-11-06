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
    const group = page.getByRole("radiogroup", { name: "Playback start", exact: true });
    const region = page.locator(".settings-row").filter({ has: group });
    const fast = group.getByRole("radio", { name: "Fast start", exact: true });
    const balanced = group.getByRole("radio", { name: "Balanced", exact: true });
    const best = group.getByRole("radio", { name: "Best quality", exact: true });
    await expect(balanced).toBeChecked();
    await expect(balanced).toHaveAttribute("tabindex", "0");
    await expect(fast).toHaveAttribute("tabindex", "-1");
    await expect(best).toHaveAttribute("tabindex", "-1");
    await expect(region).toContainText("Balanced favors quality when starts stay under 3 seconds.");
    await expect(page.getByRole("button", { name: "Save preferences", exact: true })).toHaveCount(0);

    await balanced.focus();
    await page.keyboard.press("ArrowLeft");
    await expect(fast).toBeChecked();
    await expect.poll(() => writes).toEqual([{
      method: "PUT",
      body: { explicitFilter: "All", showExternalLabel: true, showExplicitLabel: true,
        playbackStart: "FastStart", expectedRevision: "initial" },
    }]);
    for (const radio of [fast, balanced, best]) await expect(radio).toBeDisabled();
    releaseSave();
    await expect(page.getByRole("status").filter({ hasText: "Listening preferences saved." }).last()).toBeVisible();
    await expect(fast).toBeEnabled();
    await fast.focus();
    for (const [key, radio] of [["ArrowLeft", best], ["ArrowRight", fast], ["ArrowDown", balanced], ["ArrowRight", best]] as const) {
      await page.keyboard.press(key);
      await expect(radio).toBeChecked();
      await expect(radio).toBeEnabled();
      await radio.focus();
    }
    await expect(best).toBeChecked();
    await expect.poll(() => writes.length).toBe(5);
    expect(writes.slice(1).every(write => write.body.expectedRevision === "saved")).toBe(true);
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
    expect(await best.evaluate(element => getComputedStyle(element).transitionDuration)).toBe("0s");
    await expect(group.locator(".use-switch-thumb")).toHaveCount(0);
    await page.screenshot({ path: testInfo.outputPath(`playback-start-${width}-${theme}.png`) });
    await page.getByRole("button", { name: "Reset to household defaults", exact: true }).click();
    await expect(fast).toBeChecked();
    await expect(page.getByRole("status").filter({ hasText: "Household defaults restored." })).toBeVisible();
    expect(writes.at(-1)).toEqual({ method: "DELETE", body: { expectedRevision: "saved" } });
    await expect(page.getByText("Using household defaults", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Reset to household defaults", exact: true })).toBeDisabled();
  });
}

test("Playback start defaults to Balanced when older responses omit the preference", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 320, height: 900 });
  const writes: Record<string, unknown>[] = [];
  let savedPlayback: string | undefined;
  await mockPreferences(page, async route => {
    if (route.request().method() === "PUT") {
      writes.push(route.request().postDataJSON());
      savedPlayback = String(writes.at(-1)!.playbackStart);
    }
    return json(route, { values: { ...defaults, ...(savedPlayback ? { playbackStart: savedPlayback } : {}) }, householdDefaults: defaults, revision: "legacy", usesHouseholdDefaults: true });
  });
  await page.goto("/#/preferences");
  const group = page.getByRole("radiogroup", { name: "Playback start", exact: true });
  await expect(group.getByRole("radio", { name: "Balanced", exact: true })).toBeChecked();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  for (const name of ["Fast start", "Best quality", "Balanced"]) {
    const radio = group.getByRole("radio", { name, exact: true });
    await radio.click();
    await expect(radio).toBeChecked();
    await expect(radio).toBeEnabled();
    expect(await radio.locator("span").evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    expect(await radio.locator("span").evaluate(element => element.scrollHeight)).toBeLessThanOrEqual(40);
  }
  await group.scrollIntoViewIfNeeded();
  await page.screenshot({ path: testInfo.outputPath("playback-start-320.png") });
  await expect(page.getByRole("button", { name: "Save preferences", exact: true })).toHaveCount(0);
  await page.getByRole("switch", { name: "Show [A] for external tracks", exact: true }).click();
  await expect.poll(() => writes.at(-1)).toEqual({ explicitFilter: "All", showExternalLabel: false,
    showExplicitLabel: true, playbackStart: "Balanced", expectedRevision: "legacy" });
});

for (const width of [320, 390]) {
  test(`Playback start fits wider fallback labels at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: "light", reducedMotion: "reduce" });
    let playbackStart: PlaybackStart = "Balanced";
    await mockPreferences(page, route => {
      if (route.request().method() === "PUT") playbackStart = route.request().postDataJSON().playbackStart;
      return json(route, {
        values: { ...defaults, playbackStart },
        householdDefaults: { ...defaults, playbackStart: "Balanced" },
        revision: "initial", usesHouseholdDefaults: playbackStart === "Balanced",
      });
    });
    await page.goto("/#/preferences");
    const group = page.getByRole("radiogroup", { name: "Playback start", exact: true });
    await expect(group).toBeVisible();
    await page.addStyleTag({ content: '[role="radiogroup"][aria-label="Playback start"] [role="radio"] { font-family: monospace; letter-spacing: .15px; }' });
    for (const name of ["Fast start", "Balanced", "Best quality"]) {
      await group.getByRole("radio", { name, exact: true }).click();
      const selected = group.getByRole("radio", { name, exact: true });
      await expect(selected).toHaveAttribute("aria-checked", "true");
      expect(await selected.evaluate(element => {
        const sample = document.createElement("span");
        sample.style.backgroundColor = "var(--md-sys-color-secondary-container)";
        document.body.append(sample);
        const matches = getComputedStyle(element).backgroundColor === getComputedStyle(sample).backgroundColor;
        sample.remove();
        return matches;
      })).toBe(true);
      await expect(group.locator(":scope > [aria-hidden='true']")).toHaveCount(0);
      for (const radio of await group.getByRole("radio").all()) {
        const metrics = await radio.locator("span").evaluate(element => ({
          label: element.textContent, font: getComputedStyle(element).font,
          clientWidth: element.clientWidth, scrollWidth: element.scrollWidth, scrollHeight: element.scrollHeight,
        }));
        expect(metrics.scrollWidth, JSON.stringify(metrics)).toBeLessThanOrEqual(metrics.clientWidth);
        expect(metrics.scrollHeight, JSON.stringify(metrics)).toBeLessThanOrEqual(width === 390 ? 20 : 40);
      }
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await group.scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath(`playback-start-wide-font-${width}.png`) });
  });
}

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
    const form = page.getByRole("form", { name: "Defaults for everyone settings", exact: true });
    const group = form.getByRole("radiogroup", { name: "Playback start", exact: true });
    await expect(group.getByRole("radio", { name: "Balanced", exact: true })).toBeChecked();
    await expect(form.getByRole("button", { name: "Save changes to Defaults for everyone", exact: true })).toHaveCount(0);
    await group.getByRole("radio", { name: "Balanced", exact: true }).focus();
    await page.keyboard.press("ArrowRight");
    await expect(group.getByRole("radio", { name: "Best quality", exact: true })).toBeChecked();
    await expect.poll(() => writes).toEqual([{ updates: { PLAYBACK_START_PREFERENCE: "BestQuality" } }]);
    await expect(page.getByRole("status").filter({ hasText: "Saved · Playback start" })).toBeVisible();
    await expect(group.getByRole("radio", { name: "Best quality", exact: true })).toBeChecked();
    await expect(form.getByRole("button", { name: "Save changes to Defaults for everyone", exact: true })).toHaveCount(0);
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
