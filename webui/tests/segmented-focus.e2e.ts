import { expect, test, type Locator, type Page, type TestInfo } from "@playwright/test";
import { homeFixture, panelFixture, settingsFixture } from "./shared-design-fixtures";

async function capture(page: Page, info: TestInfo, name: string) {
  const path = info.outputPath(`${name}.png`);
  await page.screenshot({ path, fullPage: true });
  await info.attach(name, { path, contentType: "image/png" });
}

async function keyboardFocus(page: Page, control: Locator) {
  await control.click();
  await page.keyboard.press("Shift+Tab");
  await page.keyboard.press("Tab");
  await expect(control).toBeFocused();
}

async function expectRing(control: Locator) {
  await expect(control).toHaveCSS("outline-style", "solid");
  await expect(control).toHaveCSS("outline-width", "2px");
  await expect(control).toHaveCSS("outline-offset", "-3px");
}

async function expectEnds(page: Page, group: Locator) {
  await expect(group).toBeVisible();
  const controls = group.locator(":scope > :is(a, button, label)");
  for (const control of [controls.first(), controls.last()]) {
    await control.click();
    await expect(control).toHaveCSS("outline-style", "none");
    await keyboardFocus(page, control);
    await expectRing(control);
  }
  const radius = await group.evaluate(element => getComputedStyle(element).borderStartStartRadius);
  await expect(controls.first()).toHaveCSS("border-start-start-radius", radius);
  await expect(controls.last()).toHaveCSS("border-end-end-radius", radius);
}

async function sourceChoices(page: Page) {
  let householdMode = "automatic", personalMode: string | null = null;
  await page.route("**/api/admin/providers/use**", route => {
    if (route.request().method() === "PUT") {
      const choice = route.request().postDataJSON();
      if (choice.scope === "household") householdMode = choice.mode;
      else personalMode = choice.mode;
    }
    return route.fulfill({ json: { householdRevision: "1", personalRevision: "1", providers: ["qobuz", "apple-musickit", "spotify", "listenbrainz"].map(providerId => ({ providerId, householdMode, personalMode, effectiveMode: personalMode ?? householdMode, canChoosePersonal: true })) } });
  });
}

async function playbackChoices(page: Page) {
  const defaults = { lyricsOrder: "backend,kept", explicitFilter: "All", showExternalLabel: true, showExplicitLabel: true, playbackStart: "Balanced", qualityLimit: null };
  let values = { ...defaults };
  await page.route("**/api/admin/preferences", route => {
    if (route.request().method() === "PUT") values = { ...values, ...route.request().postDataJSON() };
    return route.fulfill({ json: { values, householdDefaults: defaults, householdQuality: "BestAvailable", usesHouseholdDefaults: false, revision: "1", cleanOnlyLocked: false } });
  });
}

for (const width of [1280, 390]) for (const theme of ["dark", "light"] as const) {
  test(`History segment focus follows input and pill ends at ${width} ${theme}`, async ({ page }, info) => {
    await page.clock.install({ time: new Date("2026-11-04T14:00:00Z") });
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await panelFixture(page);
    await page.goto("/#/activity?view=history");
    const group = page.getByRole("radiogroup", { name: "History filters", exact: true });
    const controls = group.getByRole("radio");
    const first = controls.first(), last = controls.last();
    await expect(group).toBeVisible();
    await page.evaluate(async () => { await document.fonts.ready; });
    await first.click();
    await expect(first).toHaveAttribute("aria-checked", "true");
    await capture(page, info, `history-${width}-${theme}-mouse`);
    await expect(first).toHaveCSS("outline-style", "none");

    await keyboardFocus(page, first);
    await expectRing(first);
    await capture(page, info, `history-${width}-${theme}-keyboard-start`);
    await expect(first).toHaveCSS("border-start-start-radius", "999px");
    await expect(first).toHaveCSS("border-end-start-radius", "999px");
    await expect(first).toHaveCSS("border-start-end-radius", "0px");
    await page.keyboard.press("ArrowRight");
    await expect(controls.nth(1)).toBeFocused();
    await expectRing(controls.nth(1));
    await expect(controls.nth(1)).toHaveCSS("border-radius", "0px");
    await expect(first).toHaveAttribute("aria-checked", "true");
    await page.keyboard.press("End");
    await expect(last).toBeFocused();
    await expectRing(last);
    await capture(page, info, `history-${width}-${theme}-keyboard-end`);
    await expect(last).toHaveCSS("border-start-end-radius", "999px");
    await expect(last).toHaveCSS("border-end-end-radius", "999px");
    await expect(last).toHaveCSS("border-start-start-radius", "0px");
    await page.keyboard.press("Space");
    await expect(last).toHaveAttribute("aria-checked", "true");

    const scroller = page.locator(".event-log-chip-row .filter-segments");
    for (const key of ["Home", "End"]) {
      await page.keyboard.press(key);
      const end = key === "Home" ? first : last;
      await expect(end).toBeFocused();
      await expect.poll(() => end.evaluate(element => {
        const group = element.parentElement!, outer = group.getBoundingClientRect(), inner = element.getBoundingClientRect();
        return { visible: inner.left >= outer.left && inner.right <= outer.right + 1 && inner.top >= outer.top && inner.bottom <= outer.bottom + 1,
          left: inner.left - outer.left, right: outer.right - inner.right, scrollLeft: group.scrollLeft, focused: element === document.activeElement };
      }), { message: "the focused end and its inset ring fit inside the History scroller" }).toMatchObject({ visible: true, focused: true });
    }
    // Selection and navigation can happen before the deferred selection scroll runs.
    await first.evaluate(element => {
      (element as HTMLButtonElement).click();
      element.focus();
      element.dispatchEvent(new KeyboardEvent("keydown", { key: "End", bubbles: true, cancelable: true }));
    });
    await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
    await expect(last).toBeFocused();
    await expect(first).toHaveAttribute("aria-checked", "true");
    await expect.poll(() => last.evaluate(element => {
      const outer = element.parentElement!.getBoundingClientRect(), inner = element.getBoundingClientRect();
      return inner.left >= outer.left && inner.right <= outer.right + 1;
    }), { message: "a pending selection scroll keeps keyboard focus visible" }).toBe(true);
    if (width === 390) expect(await scroller.evaluate(element => element.scrollWidth > element.clientWidth)).toBe(true);
    await group.evaluate(element => element.setAttribute("dir", "rtl"));
    await expect(first).toHaveCSS("border-top-right-radius", "999px");
    await expect(last).toHaveCSS("border-top-left-radius", "999px");
    expect(await page.pageErrors()).toEqual([]);
  });

  test(`Shared segment users keep mouse and keyboard focus at ${width} ${theme}`, async ({ page }, info) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await panelFixture(page);
    await sourceChoices(page);
    for (const path of ["activity", "activity?view=plays", "activity?view=jobs", "activity?view=tasks", "activity?view=reports", "sources/services", "sources/services/qobuz", "sources/accounts", "library/playlists", "library/matches", "library/cached", "library/kept", "library/import", "settings/appearance", "settings/people", "report"]) {
      await page.goto(`/#/${path}`);
      await expect(page.locator(".workspace-header h1")).toBeVisible();
      await expect(page.locator(".skeleton-panel")).toHaveCount(0);
      await expect(page.getByLabel("Loading items", { exact: true })).toHaveCount(0);
      if (path === "sources/services/qobuz") {
        await expect(page.locator("[data-side-panel]")).toBeVisible();
        await expect(page.getByRole("radiogroup", { name: "Use this source for you", exact: true })).toBeVisible();
      }
      const modal = page.locator('[data-side-panel][aria-modal="true"]');
      const surface = await modal.count() ? modal : page;
      for (const group of await surface.locator(".seg").all()) {
        if (!await group.isVisible()) continue;
        const controls = group.locator(":scope > :is(a, button, label)");
        const first = controls.first(), last = controls.last();
        for (const end of [first, last]) {
          await end.click();
          await expect(end).toHaveCSS("outline-style", "none");
          await keyboardFocus(page, end);
          await expectRing(end);
        }
        const radius = await group.evaluate(element => getComputedStyle(element).borderStartStartRadius);
        await expect(first).toHaveCSS("border-start-start-radius", radius);
        await expect(last).toHaveCSS("border-end-end-radius", radius);
        if (width === 390) for (const control of await controls.all()) {
          const bounds = (await control.boundingBox())!;
          expect(bounds.width).toBeGreaterThanOrEqual(44);
          expect(bounds.height).toBeGreaterThanOrEqual(44);
        }
      }
      if (path === "settings/appearance") await page.getByRole("radiogroup", { name: "Color theme", exact: true }).getByRole("radio", { name: theme === "dark" ? "Dark" : "Light", exact: true }).click();
      await capture(page, info, `${path.replace(/[/?=]/g, "-")}-${width}-${theme}`);
    }
    await settingsFixture(page);
    await page.goto("/#/settings/sources");
    await expect(page.locator(".routing-view")).toBeVisible();
    await expect(page.locator(".routing-view")).toHaveAttribute("aria-busy", "false");
    for (const group of await page.locator(".routing-view .seg").all()) {
      const first = group.locator(":scope > button").first();
      await first.click();
      await expect(first).toHaveCSS("outline-style", "none");
      await keyboardFocus(page, first);
      await expectRing(first);
    }
    expect(await page.pageErrors()).toEqual([]);
  });

  test(`Segment links and input labels use keyboard focus at ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await panelFixture(page);
    await page.goto("/#/activity");
    await expect(page.getByRole("radiogroup", { name: "History filters", exact: true })).toBeVisible();
    await page.locator(".workspace").evaluate(element => {
      const group = document.createElement("div");
      group.className = "seg";
      group.setAttribute("aria-label", "Focus examples");
      group.innerHTML = '<a href="#example">First</a><button type="button">Middle</button><label><input type="checkbox">Last</label>';
      group.querySelector("a")!.addEventListener("click", event => event.preventDefault());
      element.append(group);
    });
    const group = page.locator('.seg[aria-label="Focus examples"]');
    const first = group.locator("a"), label = group.locator("label"), input = label.locator("input");
    await first.click();
    await expect(first).toHaveCSS("outline-style", "none");
    await input.click();
    await expect(label).toHaveCSS("outline-style", "none");
    await keyboardFocus(page, first);
    await expectRing(first);
    await page.keyboard.press("Tab");
    await page.keyboard.press("Tab");
    await expect(input).toBeFocused();
    await expectRing(label);
    await expect(label).toHaveCSS("border-start-end-radius", "999px");
    await group.evaluate(element => element.classList.add("wrapped"));
    await expect(first).toHaveCSS("border-start-start-radius", "14px");
    await expect(label).toHaveCSS("border-end-end-radius", "14px");
    await group.evaluate(element => element.remove());
  });

  test(`Segment dialogs and additional settings keep focus at ${width} ${theme}`, async ({ page }, info) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await panelFixture(page);
    await page.goto("/#/library/playlists");
    await expect(page.getByRole("radiogroup", { name: "Playlist filters", exact: true })).toBeVisible();
    await page.getByRole("button", { name: "Import playlist", exact: true }).click();
    const sources = page.getByRole("navigation", { name: "Choose playlist source", exact: true }).getByRole("tablist");
    await expectEnds(page, sources);
    await page.keyboard.press("Home");
    await expect(sources.getByRole("tab").first()).toBeFocused();
    await expect(sources.getByRole("tab").first()).toHaveAttribute("aria-selected", "true");
    await page.keyboard.press("ArrowRight");
    await expect(sources.getByRole("tab").last()).toBeFocused();
    await expect(sources.getByRole("tab").last()).toHaveAttribute("aria-selected", "true");
    await expectRing(sources.getByRole("tab").last());
    await capture(page, info, `playlist-dialog-${width}-${theme}`);
    await page.getByRole("dialog").getByRole("button", { name: "Cancel", exact: true }).click();
    await page.goto("/#/sources/services");
    await expect(page.getByRole("radiogroup", { name: "Filter sources", exact: true })).toBeVisible();
    await page.getByRole("button", { name: "Add extension", exact: true }).click();
    await expectEnds(page, page.getByRole("navigation", { name: "Ways to add an extension", exact: true }).getByRole("tablist"));
    await capture(page, info, `extension-dialog-${width}-${theme}`);
    await page.keyboard.press("Escape");
    await settingsFixture(page);
    await playbackChoices(page);
    await page.goto("/#/settings/appearance");
    const colors = page.getByRole("radiogroup", { name: "Color theme", exact: true });
    await expect(colors).toBeVisible();
    await keyboardFocus(page, colors.getByRole("radio").first());
    await page.keyboard.press("End");
    await expect(colors.getByRole("radio").last()).toBeFocused();
    await expect(colors.getByRole("radio").last()).toHaveAttribute("aria-checked", "true");
    await expectRing(colors.getByRole("radio").last());
    await page.keyboard.press("Home");
    await expect(colors.getByRole("radio").first()).toHaveAttribute("aria-checked", "true");
    await colors.getByRole("radio", { name: theme === "dark" ? "Dark" : "Light", exact: true }).click();
    await page.goto("/#/settings/preferences");
    await expectEnds(page, page.getByRole("radiogroup", { name: "Playback start", exact: true }));
    await capture(page, info, `playback-setting-${width}-${theme}`);
    await page.route("**/api/admin/health", route => route.fulfill({ json: {
      protocol: "Jellyfin", configuration: { mode: "server-api-key", hasConfiguration: true, serviceCredentialReady: true, enabled: true, intervalMinutes: 60, searchQuery: "Sample", firstByteBudgetSeconds: 15, revision: 1 }, listeners: [],
      runs: [false, true].map((passed, index) => ({ id: `run-${index}`, protocol: "Jellyfin", mode: "server-api-key", startedAt: "2026-11-03T12:00:00Z", completedAt: "2026-11-03T12:00:01Z", passed, alertStatus: "none", steps: [{ name: "backend", status: passed ? "passed" : "failed", latencyMilliseconds: 5, reason: null }] })),
    } }));
    await page.goto("/#/settings/health");
    await expectEnds(page, page.getByRole("radiogroup", { name: "Filter checks", exact: true }));
    if (width === 390) {
      await page.goto("/#/settings/sources");
      await expectEnds(page, page.getByRole("radiogroup", { name: "Source orders", exact: true }));
    }
    expect(await page.pageErrors()).toEqual([]);
  });

  test(`For you segments keep mouse and keyboard focus at ${width} ${theme}`, async ({ page }, info) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await panelFixture(page);
    await homeFixture(page, true);
    for (const [path, label] of [["releases", "Release type"], ["recap", "Stats period"]]) {
      await page.goto(`/#/for-you?section=${path}`);
      await expectEnds(page, page.getByRole("radiogroup", { name: label, exact: true }));
      await capture(page, info, `${path}-${width}-${theme}`);
    }
    await page.goto("/#/for-you?section=taste");
    await page.getByRole("button", { name: "Steer R&B", exact: true }).click();
    await expectEnds(page, page.getByRole("navigation", { name: "Duration for R&B", exact: true }).getByRole("tablist"));
    await capture(page, info, `taste-duration-${width}-${theme}`);
    expect(await page.pageErrors()).toEqual([]);
  });
}
