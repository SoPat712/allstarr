import { expect, test, type Page, type Route } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

type PreferenceState = {
  explicitFilter: string;
  showExternalLabel: boolean;
  showExplicitLabel: boolean;
  lyricsOrder?: string;
  qualityLimit?: string;
  householdQuality?: string;
  revision: string;
  usesHouseholdDefaults: boolean;
  cleanOnlyLocked?: boolean;
};

function response(state: PreferenceState) {
  return {
    Values: {
      LyricsOrder: state.lyricsOrder ?? "spotify,lrclib,lyricsplus",
      QualityLimit: state.qualityLimit ?? null,
      ExplicitFilter: state.explicitFilter,
      ShowExternalLabel: state.showExternalLabel,
      ShowExplicitLabel: state.showExplicitLabel,
    },
    HouseholdDefaults: {
      LyricsOrder: "spotify,lrclib,lyricsplus",
      ExplicitFilter: "All",
      ShowExternalLabel: true,
      ShowExplicitLabel: true,
    },
    HouseholdQuality: state.householdQuality ?? "CdLossless",
    UsesHouseholdDefaults: state.usesHouseholdDefaults,
    Revision: state.revision,
    CleanOnlyLocked: state.cleanOnlyLocked ?? false,
  };
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`listener clean only unlocked and locked at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    let locked = false, selected = "All";
    await mockShell(page, false, async route => {
      if (route.request().method() === "PUT") {
        expect(route.request().postDataJSON().explicitFilter).toBe("CleanOnly");
        selected = "CleanOnly";
      }
      return fulfillJson(route, response({ explicitFilter: locked ? "CleanOnly" : selected,
        showExternalLabel: true, showExplicitLabel: true, revision: "current",
        usesHouseholdDefaults: false, cleanOnlyLocked: locked }));
    });
    await page.goto("/#/preferences");
    const clean = page.getByRole("radio", { name: "Clean only", exact: false });
    await expect(page.getByText("Hides explicit songs and songs whose rating isn't known.", { exact: true })).toBeVisible();
    await clean.check();
    await page.screenshot({ path: `test-results/shots-uisettings/clean-only-unlocked-${width}-${theme}.png`, fullPage: true });
    await expect(page.getByRole("status").filter({ hasText: "Listening preferences saved." }).last()).toBeVisible();
    await expect(clean).toBeChecked();
    locked = true;
    await page.reload();
    await expect(clean).toBeChecked();
    const radios = page.getByRole("group", { name: "Release filter" }).getByRole("radio");
    await expect(radios).toHaveCount(3);
    for (const radio of await radios.all())
      await expect(radio).toBeDisabled();
    await expect(page.getByText("Your administrator turned this on.", { exact: true })).toBeVisible();
    await expect(page.getByRole("switch", { name: "Show [E] for explicit tracks", exact: true })).toBeEnabled();
    await page.screenshot({ path: `test-results/shots-uisettings/clean-only-locked-${width}-${theme}.png`, fullPage: true });
    if (width === 390) await expectContainedMobileGeometry(page);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
}

async function fulfillJson(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}

async function mockShell(page: Page, administrator: boolean, preferences: (route: Route) => Promise<void>) {
  await page.route("**/api/admin/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/admin/auth/me") return fulfillJson(route, {
      authenticated: true,
      backend: "Jellyfin",
      user: { id: "listener", name: administrator ? "Administrator" : "Listener", isAdministrator: administrator },
    });
    if (path === "/api/admin/auth/oidc/status") return fulfillJson(route, { enabled: false });
    if (path === "/api/admin/onboarding/status") return fulfillJson(route, {
      completed: true,
      setupOpen: false,
      shouldRedirectToSetup: false,
      schemaVersion: "onboarding-v1",
      completedSteps: ["backend-identity"],
      completionSource: "setup-guide",
      revision: 1,
      recoveryNotices: [],
      migration: { available: false, completed: false, firstRun: false },
    });
    if (path === "/api/admin/preferences") return preferences(route);
    if (path === "/api/admin/updates/stream")
      return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return fulfillJson(route, {});
  });
}

test("listener autosaves each change and resets with the latest returned revision", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  const writes: Array<{ method: string; body: Record<string, unknown> }> = [];
  let state: PreferenceState = {
    explicitFilter: "All",
    showExternalLabel: true,
    showExplicitLabel: true,
    revision: "revision-1",
    usesHouseholdDefaults: true,
  };
  await mockShell(page, false, async (route) => {
    const method = route.request().method();
    if (method === "GET") return fulfillJson(route, response(state));
    const body = route.request().postDataJSON() as Record<string, unknown>;
    writes.push({ method, body });
    if (method === "PUT") {
      expect(body.expectedRevision).toBe(state.revision);
      expect(body.explicitFilter).toBe("ExplicitOnly");
      state = {
        explicitFilter: String(body.explicitFilter),
        showExternalLabel: Boolean(body.showExternalLabel),
        showExplicitLabel: Boolean(body.showExplicitLabel),
        revision: `revision-${writes.length + 1}`,
        usesHouseholdDefaults: false,
      };
      return fulfillJson(route, response(state));
    }
    expect(method).toBe("DELETE");
    expect(body).toEqual({ expectedRevision: "revision-4" });
    state = {
      explicitFilter: "All",
      showExternalLabel: true,
      showExplicitLabel: true,
      revision: "revision-3",
      usesHouseholdDefaults: true,
    };
    return fulfillJson(route, response(state));
  });

  await page.goto("/");
  await page.getByRole("button", { name: "More destinations" }).click();
  await page.getByRole("link", { name: /Listening preferences/ }).click();
  await expect(page).toHaveURL(/#\/preferences$/);
  await expect(page.getByRole("heading", { name: "Listening preferences", level: 1 })).toBeVisible();
  await expect(page.getByText("affect only your listener account", { exact: false })).toBeVisible();
  await expect(page.getByText("Native library titles remain untouched", { exact: false })).toBeVisible();

  await page.getByRole("radio", { name: "Hide clean/edited versions" }).check();
  await expect(page.getByRole("switch", { name: "Show [A] for external tracks", exact: true })).toBeEnabled();
  await page.getByRole("switch", { name: "Show [A] for external tracks", exact: true }).click();
  await expect(page.getByRole("switch", { name: "Show [E] for explicit tracks", exact: true })).toBeEnabled();
  await page.getByRole("switch", { name: "Show [E] for explicit tracks", exact: true }).click();
  await expect(page.getByRole("status").filter({ hasText: "Listening preferences saved." }).last()).toBeVisible();
  await expect(page.getByText("Personal choices", { exact: true })).toBeVisible();

  await page.getByRole("button", { name: "Reset to household defaults" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Household defaults restored." })).toBeVisible();
  await expect(page.getByRole("radio", { name: /^All\b/ })).toBeChecked();
  await expect(page.getByRole("switch", { name: "Show [A] for external tracks", exact: true })).toBeChecked();
  await expect(page.getByRole("switch", { name: "Show [E] for explicit tracks", exact: true })).toBeChecked();
  expect(writes.map(({ method }) => method)).toEqual(["PUT", "PUT", "PUT", "DELETE"]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test("administrator reloads a 409 conflict before saving against the refreshed revision", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  let getCount = 0;
  const putBodies: Array<Record<string, unknown>> = [];
  await mockShell(page, true, async (route) => {
    if (route.request().method() === "GET") {
      getCount++;
      return fulfillJson(route, response(getCount === 1 ? {
        explicitFilter: "All", showExternalLabel: true, showExplicitLabel: true,
        revision: "revision-1", usesHouseholdDefaults: true,
      } : {
        explicitFilter: "LegacyMode", showExternalLabel: false, showExplicitLabel: true,
        revision: "revision-server", usesHouseholdDefaults: false,
      }));
    }
    const body = route.request().postDataJSON() as Record<string, unknown>;
    putBodies.push(body);
    if (putBodies.length === 1)
      return fulfillJson(route, { error: "Preferences changed." }, 409);
    return fulfillJson(route, response({
      explicitFilter: String(body.explicitFilter),
      showExternalLabel: Boolean(body.showExternalLabel),
      showExplicitLabel: Boolean(body.showExplicitLabel),
      revision: "revision-next",
      usesHouseholdDefaults: false,
    }));
  });

  await page.goto("/");
  await page.getByRole("link", { name: "Listening preferences" }).click();
  await page.getByRole("radio", { name: "Clean only" }).check();
  await expect(page.locator(".preferences-view").getByRole("alert")).toContainText("changed in another session");
  await page.getByRole("button", { name: "Reload latest" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Saved explicit filter: LegacyMode" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Save preferences" })).toHaveCount(0);
  await page.getByRole("radio", { name: "Clean only" }).check();
  await expect(page.getByRole("status").filter({ hasText: "Listening preferences saved." }).last()).toBeVisible();

  expect(putBodies[0].expectedRevision).toBe("revision-1");
  expect(putBodies[1].expectedRevision).toBe("revision-server");
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`personal lyrics order saves and resets at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    let state: PreferenceState = { explicitFilter: "All", showExternalLabel: true, showExplicitLabel: true,
      lyricsOrder: "spotify,lrclib,lyricsplus", revision: "initial", usesHouseholdDefaults: true };
    const writes: Record<string, unknown>[] = [];
    await mockShell(page, false, async route => {
      if (route.request().method() === "GET") return fulfillJson(route, response(state));
      const body = route.request().postDataJSON() as Record<string, unknown>;
      writes.push(body);
      if (route.request().method() === "PUT") {
        state = { ...state, lyricsOrder: String(body.lyricsOrder), revision: "saved", usesHouseholdDefaults: false };
      } else {
        expect(body).toEqual({ expectedRevision: "saved" });
        state = { ...state, lyricsOrder: "spotify,lrclib,lyricsplus", revision: "reset", usesHouseholdDefaults: true };
      }
      return fulfillJson(route, response(state));
    });
    await page.route("**/api/admin/ui/schema", route => fulfillJson(route, { providers: [
      { id: "spotify", name: "Spotify", categories: ["lyrics"] },
      { id: "lrclib", name: "LRCLIB", categories: ["lyrics"] },
      { id: "lyricsplus", name: "LyricsPlus", categories: ["lyrics"] },
    ] }));
    await page.goto("/#/preferences");
    const region = page.getByRole("region", { name: "Lyrics source order" });
    await expect(region).toBeVisible();
    await expect(region.getByRole("listitem")).toHaveText(["Media server lyrics", "Kept LRC", "Spotify", "LRCLIB", "LyricsPlus"]);
    await expect(region.getByRole("button", { name: "Move Spotify up", exact: true })).toBeEnabled();
    await expect(region.getByRole("button", { name: "Move Kept LRC up", exact: true })).toBeEnabled();
    await expect(region.getByRole("button", { name: "Move Kept LRC down", exact: true })).toBeEnabled();
    await region.getByRole("button", { name: "Move Kept LRC down", exact: true }).click();
    await region.getByRole("button", { name: "Move Media server lyrics down", exact: true }).focus();
    await page.keyboard.press("Enter");
    await expect(region.getByRole("listitem")).toHaveText(["Spotify", "Media server lyrics", "Kept LRC", "LRCLIB", "LyricsPlus"]);
    await expect(page.getByRole("button", { name: "Save preferences", exact: true })).toHaveCount(0);
    await expect.poll(() => writes[0]).toEqual({ explicitFilter: "All", showExternalLabel: true, showExplicitLabel: true,
      playbackStart: "Balanced", lyricsOrder: "spotify,backend,kept,lrclib,lyricsplus", expectedRevision: "initial" });
    const savedToast = page.getByRole("status").filter({ hasText: "Order saved" });
    await expect(savedToast).toBeVisible();
    await expect(savedToast.getByRole("button", { name: "Undo", exact: true })).toBeVisible();
    await expect(page.getByText("Personal choices", { exact: true })).toBeVisible();
    await page.getByRole("button", { name: "Reset to household defaults", exact: true }).click();
    await expect(region.getByRole("listitem")).toHaveText(["Media server lyrics", "Kept LRC", "Spotify", "LRCLIB", "LyricsPlus"]);
    while (await page.getByRole("button", { name: "Dismiss notification", exact: true }).count())
      await page.getByRole("button", { name: "Dismiss notification", exact: true }).first().click();
    await page.getByRole("heading", { name: "Lyrics source order", exact: true }).click();
    if (width < 600) await expectContainedMobileGeometry(page);
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({ path: `test-results/lyrics-movable-local-${width}-${theme}.png`, fullPage: true });
    await expect(page.getByText("Using household defaults", { exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const button of await region.getByRole("button").all()) {
      const box = await button.boundingBox();
      expect(box).not.toBeNull();
      expect(box!.width).toBeGreaterThanOrEqual(43);
      expect(box!.height).toBeGreaterThanOrEqual(43);
    }
  });
}


test("Undo restores one preference without replacing later choices", async ({ page }) => {
  let state: PreferenceState = { explicitFilter: "All", showExternalLabel: true, showExplicitLabel: true,
    revision: "revision-1", usesHouseholdDefaults: true };
  const writes: Array<Record<string, unknown>> = [];
  await mockShell(page, false, async route => {
    if (route.request().method() === "GET") return fulfillJson(route, response(state));
    expect(route.request().method()).toBe("PUT");
    const body = route.request().postDataJSON() as Record<string, unknown>;
    expect(body.expectedRevision).toBe(state.revision);
    writes.push(body);
    state = { ...state, showExternalLabel: Boolean(body.showExternalLabel),
      showExplicitLabel: Boolean(body.showExplicitLabel), revision: `revision-${writes.length + 1}`,
      usesHouseholdDefaults: false };
    return fulfillJson(route, response(state));
  });
  await page.goto("/#/preferences");
  const external = page.getByRole("switch", { name: "Show [A] for external tracks", exact: true });
  const explicit = page.getByRole("switch", { name: "Show [E] for explicit tracks", exact: true });
  await external.click();
  await expect(external).toBeEnabled();
  await explicit.click();
  await expect(explicit).toBeEnabled();
  await page.getByRole("status").filter({ hasText: "Listening preferences saved." }).first()
    .getByRole("button", { name: "Undo", exact: true }).click();
  await expect(external).toBeChecked();
  await expect(explicit).not.toBeChecked();
  await expect.poll(() => writes.length).toBe(3);
  expect(writes[2]).toMatchObject({ showExternalLabel: true, showExplicitLabel: false, expectedRevision: "revision-3" });
});

for (const administrator of [false, true]) {
  test(`quality autosave inherits the household limit for ${administrator ? "administrators" : "listeners"}`, async ({ page }) => {
    let state: PreferenceState = { explicitFilter: "All", showExternalLabel: true, showExplicitLabel: true,
      revision: "quality-1", usesHouseholdDefaults: false, householdQuality: "CdLossless" };
    const writes: Array<Record<string, unknown>> = [];
    await mockShell(page, administrator, async route => {
      if (route.request().method() === "GET") return fulfillJson(route, response(state));
      const body = route.request().postDataJSON() as Record<string, unknown>;
      expect(body.expectedRevision).toBe(state.revision);
      writes.push(body);
      state = { ...state, qualityLimit: String(body.qualityLimit), revision: `quality-${writes.length + 1}` };
      return fulfillJson(route, response(state));
    });
    await page.goto("/#/preferences");
    const quality = page.getByRole("button", { name: "Quality limit", exact: true });
    await expect(page.getByRole("link", { name: "Change the household limit", exact: true })).toHaveCount(administrator ? 1 : 0);
    await quality.click();
    await expect(page.getByRole("option", { name: "Best available", exact: true })).toHaveCount(0);
    await page.getByRole("option", { name: "High lossy · about 320 kbps", exact: true }).click();
    await expect(quality).toBeEnabled();
    await quality.click();
    await page.getByRole("option", { name: "Same as household · CD lossless · 16-bit/44.1 kHz", exact: true }).click();
    await expect.poll(() => writes.length).toBe(2);
    expect(writes.map(body => body.qualityLimit)).toEqual(["High", ""]);
    await expect(quality).toContainText("Same as household");
    await expect(page.getByRole("button", { name: "Save preferences", exact: true })).toHaveCount(0);
  });
}


test("Lyrics source order can be dragged and saves the new order", async ({ page }) => {
  let state: PreferenceState = { explicitFilter: "All", showExternalLabel: true, showExplicitLabel: true,
    lyricsOrder: "spotify,lrclib,lyricsplus", revision: "initial", usesHouseholdDefaults: true };
  const writes: Record<string, unknown>[] = [];
  await mockShell(page, false, async route => {
    if (route.request().method() === "PUT") {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      writes.push(body);
      state = { ...state, lyricsOrder: String(body.lyricsOrder), revision: "saved", usesHouseholdDefaults: false };
    }
    return fulfillJson(route, response(state));
  });
  await page.route("**/api/admin/ui/schema", route => fulfillJson(route, { providers: [
    { id: "spotify", name: "Spotify", categories: ["lyrics"] },
    { id: "lrclib", name: "LRCLIB", categories: ["lyrics"] },
    { id: "lyricsplus", name: "LyricsPlus", categories: ["lyrics"] },
  ] }));
  await page.goto("/#/preferences");
  const region = page.getByRole("region", { name: "Lyrics source order", exact: true });
  await region.getByRole("listitem").filter({ hasText: "Spotify" }).dragTo(region.getByRole("listitem").first());
  await expect.poll(() => writes[0]?.lyricsOrder).toBe("spotify,backend,kept,lrclib,lyricsplus");
  await expect(region.getByRole("listitem")).toHaveText(["Spotify", "Media server lyrics", "Kept LRC", "LRCLIB", "LyricsPlus"]);
  await expect(page.getByRole("status").filter({ hasText: "Order saved" })).toBeVisible();
});
