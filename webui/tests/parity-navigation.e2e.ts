import { expect, test, type Page } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";
import { schema, mockApi, expectUnderlineTabs } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

test("Slim sidebar centers navigation and profile controls", async ({ page }) => {
  await page.setViewportSize({ width: 835, height: 762 });
  await mockApi(page);
  await page.goto("#/");
  const sidebar = await page.locator(".sidebar").boundingBox();
  const home = await page.locator(".desktop-navigation").getByRole("link", { name: "Home", exact: true }).boundingBox();
  const sourcesIcon = await page.locator(".desktop-navigation").getByRole("link", { name: "Sources" }).locator("svg").boundingBox();
  const profile = await page.locator(".profile .avatar").boundingBox();
  expect(sidebar && home && sourcesIcon && profile).toBeTruthy();
  const center = (box: NonNullable<typeof sidebar>) => box.x + box.width / 2;
  expect(Math.abs(center(home!) - center(sidebar!))).toBeLessThanOrEqual(1);
  expect(Math.abs(center(sourcesIcon!) - center(sidebar!))).toBeLessThanOrEqual(1);
  expect(Math.abs(center(profile!) - center(sidebar!))).toBeLessThanOrEqual(1);
});

for (const width of [835, 1280]) for (const theme of ["light", "dark"]) {
  test(`Desktop navigation stays pinned while long pages scroll at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
      ...schema, configSections: [{ id: "general", label: "General", fields: Array.from({ length: 24 }, (_, i) => ({
        key: `SCROLL_FIELD_${i}`, label: `Read-only setting ${i + 1}`, type: "text", valuePath: "deployment.backend", readOnly: true,
      })) }],
    } }));
    await page.goto("#/settings/advanced");
    await expect(page.getByText("Read-only setting 24", { exact: true })).toBeVisible();
    await page.evaluate(() => new Promise<void>(resolve =>
      requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
    await page.getByRole("heading", { name: "Settings", level: 1 }).click();
    const sidebar = page.locator(".sidebar");
    const initial = await sidebar.boundingBox();
    const profile = await page.locator(".profile").boundingBox();
    expect(initial!.height).toBe(844);
    expect(initial!.width).toBe(width === 835 ? 80 : 200);
    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    expect(await page.evaluate(() => window.scrollY)).toBeGreaterThan(844);
    const scrolled = await sidebar.boundingBox();
    const scrolledProfile = await page.locator(".profile").boundingBox();
    expect(scrolled!.y).toBe(0);
    expect(scrolled!.height).toBe(initial!.height);
    expect(scrolledProfile!.y).toBe(profile!.y);
    expect((await page.locator(".workspace").boundingBox())!.x).toBe(initial!.width);
    await expect(sidebar).toHaveCSS("position", "fixed");
    await page.screenshot({ path: test.info().outputPath(`sidebar-page-bottom-${width}-${theme}.png`) });
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({ path: test.info().outputPath(`sidebar-long-${width}-${theme}.png`), fullPage: true });
    expect((await sidebar.boundingBox())!.y).toBe(0);
    await page.getByRole("button", { name: width === 835 ? "Expand sidebar" : "Collapse sidebar", exact: true }).click();
    await expect.poll(async () => (await sidebar.boundingBox())!.width).toBe(width === 835 ? 200 : 80);
    await expect(page.locator(".brand > span:last-child")).toHaveCSS("opacity", width === 835 ? "1" : "0");
    expect((await page.locator(".workspace").boundingBox())!.x).toBe(width === 835 ? 200 : 80);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: test.info().outputPath(`sidebar-scrolled-${width}-${theme}.png`) });
  });
}

for (const width of [835, 1280]) {
  test(`Short desktop navigation keeps the profile and every destination reachable at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 420 });
    await mockApi(page);
    await page.goto("#/settings/advanced");
    const profile = await page.locator(".profile").boundingBox();
    expect(profile!.y).toBeGreaterThanOrEqual(0);
    expect(profile!.y + profile!.height).toBeLessThanOrEqual(420);
    const nav = page.locator(".desktop-navigation");
    expect(await nav.evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true);
    const scroll = await page.evaluate(() => window.scrollY);
    const settingsLink = nav.getByRole("link", { name: "Settings", exact: true });
    await settingsLink.focus();
    await expect(settingsLink).toBeInViewport({ ratio: 1 });
    const link = await settingsLink.boundingBox();
    expect(link!.y + link!.height).toBeLessThanOrEqual(profile!.y);
    expect(await page.evaluate(() => window.scrollY)).toBe(scroll);
    await page.screenshot({ path: test.info().outputPath(`sidebar-short-${width}.png`) });
  });
}

for (const theme of ["light", "dark"]) {
  test(`Pinned navigation preserves the phone bottom bar in ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    await page.goto("#/settings/advanced");
    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    const bar = await page.locator(".sidebar").boundingBox();
    expect(bar!.x).toBe(0);
    expect(bar!.width).toBe(390);
    expect(bar!.y + bar!.height).toBe(844);
    await expect(page.locator(".desktop-navigation")).toBeHidden();
    await expectContainedMobileGeometry(page);
    await page.screenshot({ path: test.info().outputPath(`sidebar-phone-${theme}.png`) });
  });
}

for (const theme of ["light", "dark"] as const) {
  for (const width of [390, 1280]) {
    test(`Page navigation uses sliding underline tabs in ${theme} at ${width}px`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.addInitScript(mode => localStorage.setItem("allstarr.theme", mode), theme);
      await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
      await page.route("**/*", route => ["localhost", "127.0.0.1"].includes(new URL(route.request().url()).hostname) ? route.fallback() : route.abort());
      await mockApi(page);
      for (const [href, label, next] of [
        ["#/library/playlists", "Library sections", "Matches"],
        ["#/for-you", "For you sections", "Recap"],
        ["#/sources/services", "Source sections", "Accounts"],
        ["#/activity?view=history", "Activity views", "Plays"],
      ]) {
        await page.goto(href);
        await page.mouse.move(0, 0);
        const tabs = page.getByRole("navigation", { name: label, exact: true }).getByRole("tablist");
        await expectUnderlineTabs(tabs);
        await tabs.getByRole("tab", { name: next, exact: true }).click();
        await page.mouse.move(0, 0);
        await expect(tabs.getByRole("tab", { name: next, exact: true })).toHaveAttribute("aria-selected", "true");
        await expectUnderlineTabs(tabs);
      }
    });
  }

  test(`Segmented controls share borders and selected colors across workspaces in ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.addInitScript(mode => localStorage.setItem("allstarr.theme", mode), theme);
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.route("**/*", route => ["localhost", "127.0.0.1"].includes(new URL(route.request().url()).hostname) ? route.fallback() : route.abort());
    await mockApi(page);
    const screens = [
      ["#/sources/services/lumen-audio", "Use this source for you"],
      ["#/library/cached", "Cached song filters"],
      ["#/activity?view=history", "History filters"],
      ["#/activity?view=plays", "Play filters"],
      ["#/activity?view=tasks", "Task filters"],
      ["#/settings/appearance", "Color theme"],
      ["#/settings/listeners", "Filter listeners"],
      ["#/for-you?section=recap", "Stats period"],
    ];
    for (const [href, label] of screens) {
      await page.goto(href);
      const group = page.getByRole("radiogroup", { name: label, exact: true });
      await expect(group).toBeVisible();
      await expect(group).toHaveClass(/\bseg\b/);
      const mismatches = await page.locator(".seg").evaluateAll(groups => {
        const probe = document.createElement("span");
        document.body.append(probe);
        const color = (token: string) => {
          probe.style.color = `var(${token})`;
          return getComputedStyle(probe).color;
        };
        const edge = color("--color-edge-strong"), ink = color("--color-ink");
        const tonal = color("--md-sys-color-secondary-container"), onTonal = color("--md-sys-color-on-secondary-container");
        const errors: string[] = [];
        for (const group of groups) {
          const name = group.getAttribute("aria-label") ?? group.className;
          const style = getComputedStyle(group);
          probe.style.borderRadius = group.classList.contains("wrapped") ? "var(--radius-list)" : "999px";
          const radius = getComputedStyle(probe).borderRadius;
          if ([style.borderTopWidth, style.borderRightWidth, style.borderBottomWidth, style.borderLeftWidth].some(width => width !== "1px") || style.borderTopStyle !== "solid" || style.borderTopColor !== edge || style.borderRadius !== radius || style.columnGap !== "0px") errors.push(`${name}: outline`);
          for (const [index, control] of [...group.querySelectorAll(":scope > a, :scope > button")].entries()) {
            const selected = control.matches('[aria-selected="true"], [aria-checked="true"], [aria-pressed="true"]');
            const item = getComputedStyle(control);
            if (item.backgroundColor !== (selected ? tonal : "rgba(0, 0, 0, 0)") || item.color !== (selected ? onTonal : ink)) errors.push(`${name}: ${control.textContent} colors`);
            if (index > 0 && (item.borderInlineStartWidth !== "1px" || item.borderInlineStartColor !== edge)) errors.push(`${name}: divider`);
            if (!["none", "normal"].includes(getComputedStyle(control, "::after").content)) errors.push(`${name}: underline`);
          }
          if (group.querySelector(".slide-indicator, .use-switch-thumb")) errors.push(`${name}: old indicator`);
        }
        probe.remove();
        return errors;
      });
      expect(mismatches, href).toEqual([]);
    }
  });
}

for (const [administrator, backend] of [[true, "Jellyfin"], [false, "Navidrome"]] as const) {
  test(`Desktop drawer groups destinations and identifies the ${administrator ? "administrator" : "listener"}`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await mockApi(page, { intelligenceEnabled: false });
    await page.route("**/api/admin/auth/me", route => route.fulfill({ json: { authenticated: true, backend, user: { id: "user", name: "Taylor", isAdministrator: administrator } } }));
    await page.goto("#/settings");
    const nav = page.locator(".desktop-navigation");
    await expect(nav.getByRole("group", { name: "Main destinations", exact: true }).getByRole("link")).toHaveText(["Home", "Library", "For you", "Sources", "Activity", "Settings"]);
    const secondary = nav.getByRole("group", { name: "Help and connections", exact: true });
    await expect(secondary.getByRole("link")).toHaveText(["Connect apps", "Report a problem"]);
    await expect(secondary).toHaveCSS("border-top-width", "0px");
    expect(await secondary.evaluate(element => Number.parseFloat(getComputedStyle(element).marginTop))).toBeGreaterThan(0);
    await expect(page.locator(".brand small")).toHaveText(backend);
    await expect(page.locator(".profile strong")).toHaveText("Taylor");
    await expect(page.locator(".profile small")).toHaveText(administrator ? "Administrator" : "Listener");
    await expect(page.getByRole("button", { name: "Sign out", exact: true })).toBeVisible();
    await expect(nav.getByRole("link", { name: "Listening settings", exact: true })).toHaveCount(0);
    if (administrator) await expect(page.getByText(/^Allstarr · beta /)).toBeVisible();
    const settingsNav = page.getByRole("navigation", { name: "Settings categories", exact: true }).first();
    await expect(settingsNav.getByText("You", { exact: true })).toBeVisible();
    await settingsNav.getByRole("link", { name: /^Listening/ }).click();
    await expect(page).toHaveURL(/#\/settings\/listening$/);
    await expect(nav.getByRole("link", { name: "Settings", exact: true })).toHaveAttribute("aria-current", "page");
  });
}

test("Shared selects and settings tabs preserve selection without remounting", async ({ page }) => {
  await mockApi(page);
  await page.goto("#/settings/general");
  const selectedNav = page.locator(".desktop-navigation")
    .getByRole("link", { name: "Settings", exact: true });
  const selectedNavBackground = await selectedNav.evaluate((element) =>
    getComputedStyle(element).backgroundColor);
  await selectedNav.hover();
  await expect(selectedNav).toHaveCSS("background-color", selectedNavBackground);
  await page.goto("#/settings/appearance");
  await page.getByRole("radiogroup", { name: "Color theme", exact: true }).getByRole("radio", { name: "Dark", exact: true }).click();
  await expect(page.locator("html")).toHaveClass(/dark/);
  await page.goto("#/library/playlists");
  const tabs = page.locator(".library-tabs");
  await tabs.evaluate((element) => element.setAttribute("data-instance", "stable"));
  const routing = page.getByRole("tab", { name: "Matches" });
  await routing.click();
  await expect(tabs).toHaveAttribute("data-instance", "stable");
  await expectUnderlineTabs(tabs);
});

test("Legacy Library links open their current shared views", async ({ page }) => {
  await mockApi(page);
  for (const route of ["#/library", "#/library/link", "#/library/injected", "#/library/external"]) {
    await page.goto(route);
    await expect(page.getByRole("heading", { name: "Playlists", exact: true })).toBeVisible();
    await expect(page.getByRole("tab", { name: "Playlists" })).toHaveAttribute("aria-selected", "true");
  }
  for (const route of ["#/library/missing", "#/library/migration"]) {
    await page.goto(route);
    await expect(page.getByRole("tab", { name: "Matches" })).toHaveAttribute("aria-selected", "true");
  }
});

test("Legacy integration links open their canonical tabs", async ({ page }) => {
  await mockApi(page);
  for (const [route, tab] of [
    ["#/sources", "Sources"],
    ["#/settings/accounts", "Sources"],
    ["#/settings/extensions", "Sources"],
    ["#/settings/routing", "Settings"],
  ] as const) {
    await page.goto(route);
    await expect(page.getByRole("heading", { name: tab, level: 1 })).toBeVisible();
  }
});

test("Compact count-bearing route tabs stay separated and scroll the active tab into view", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.goto("#/integrations/extensions");
  await page.getByRole("button", { name: "Add extension" }).click();

  const tabs = page.getByRole("dialog", { name: "Add an extension" }).locator(".extension-tabs");
  await expect(tabs).toBeVisible();
  const initial = await tabs.evaluate((element) => ({
    right: element.getBoundingClientRect().right,
  }));
  expect(initial.right).toBeLessThanOrEqual(390);

  const activity = tabs.getByRole("tab", { name: /Manage catalogs/ });
  await activity.click();
  await expect(activity).toHaveAttribute("aria-selected", "true");
  await expect.poll(() => tabs.evaluate((element) => {
    const active = element.querySelector<HTMLElement>('[aria-selected="true"]');
    if (!active) return false;
    const container = element.getBoundingClientRect();
    const item = active.getBoundingClientRect();
    return item.left >= container.left - 1 && item.right <= container.right + 1;
  })).toBe(true);
});

test("Narrow workspace controls and contextual tabs stay separated", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  await mockApi(page);
  await page.goto("#/intelligence");

  const [heading, actions, liveState] = await Promise.all([
    page.locator(".workspace-header h1").boundingBox(),
    page.locator(".workspace-header > .flex").boundingBox(),
    page.locator(".workspace-header .live-state").boundingBox(),
  ]);
  expect(heading && actions && liveState).toBeTruthy();
  expect(heading!.x + heading!.width).toBeLessThanOrEqual(actions!.x - 7);
  expect(liveState!.width).toBeLessThanOrEqual(44);
  await expect(page.locator(".workspace-header .live-state")).toHaveAttribute("aria-label", /Live updates/);

  const periods = page.locator(".period-tabs");
  const before = await periods.evaluate((element) => ({
    clientWidth: element.clientWidth,
    scrollWidth: element.scrollWidth,
    overflowX: getComputedStyle(element).overflowX,
  }));
  expect(before.scrollWidth).toBeGreaterThan(before.clientWidth);
  expect(before.overflowX).toBe("auto");
  const custom = periods.getByRole("tab", { name: "Custom" });
  await custom.click();
  await expect(custom).toHaveAttribute("aria-selected", "true");
  await expect.poll(() => periods.evaluate((element) => {
    const active = element.querySelector<HTMLElement>('[aria-selected="true"]');
    if (!active) return false;
    const container = element.getBoundingClientRect();
    const item = active.getBoundingClientRect();
    return item.left >= container.left - 1 && item.right <= container.right + 1;
  })).toBe(true);
});

test("Mobile primary navigation stays contained and moves secondary tasks into More", async ({ page }) => {
  const screenshotTheme = process.env.ALLSTARR_SCREENSHOT_THEME;
  if (screenshotTheme === "light" || screenshotTheme === "dark")
    await page.addInitScript((theme) => localStorage.setItem("allstarr.theme", theme), screenshotTheme);
  await mockApi(page);

  for (const viewport of [
    { width: 320, height: 844 },
    { width: 390, height: 844 },
    { width: 430, height: 932 },
  ]) {
    await page.setViewportSize(viewport);
    await page.goto("#/activity");

    const navigation = page.locator(".mobile-navigation");
    await expect(navigation).toBeVisible();
    await expect(page.locator(".desktop-navigation")).toBeHidden();
    await expect(navigation.locator(":scope > :is(a, button)")).toHaveCount(4);
    await expect(navigation.getByRole("link", { name: "Activity" })).toHaveAttribute("aria-current", "page");
    await expect(navigation.getByRole("link", { name: "Insights" })).toHaveCount(0);

    const geometry = await navigation.locator(":scope > :is(a, button)").evaluateAll((items) => {
      const parent = items[0].parentElement!.getBoundingClientRect();
      return items.map((item) => {
        const rect = item.getBoundingClientRect();
        const label = item.querySelector<HTMLElement>(".nav-label")!;
        return {
          contained: rect.left >= parent.left - 1 && rect.right <= parent.right + 1,
          fillsTrack: item !== items.at(-1) || Math.abs(rect.right - parent.right) <= 1,
          width: rect.width,
          height: rect.height,
          labelContained: label.scrollWidth <= label.clientWidth,
        };
      });
    });
    expect(geometry.every(({ contained, fillsTrack, width, height, labelContained }) =>
      contained && fillsTrack && Math.abs(width - geometry[0].width) <= 1 && height >= 44 && labelContained)).toBe(true);
    if (process.env.ALLSTARR_SCREENSHOT_DIR)
      await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/${screenshotTheme ?? "system"}-mobile-${viewport.width}-activity.png` });

    const more = navigation.getByRole("button", { name: "More destinations" });
    await more.click();
    const sheet = page.getByRole("dialog", { name: "More" });
    await expect(sheet).toBeVisible();
    await expect(sheet.getByRole("link", { name: /Sources/ })).toBeVisible();
    await expect(sheet.getByRole("link", { name: /Settings/ })).toBeVisible();
    await expect(sheet.getByRole("link", { name: /Listening settings/ })).toBeVisible();
    await expect(sheet.getByRole("link", { name: /Intelligence|Insights/ })).toHaveCount(0);
    expect(await sheet.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(true);
    if (process.env.ALLSTARR_SCREENSHOT_DIR)
      await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/${screenshotTheme ?? "system"}-mobile-${viewport.width}-more.png` });

    const actionRows = await sheet.locator(".mobile-menu-actions button").evaluateAll((buttons) =>
      buttons.map((button) => Math.round(button.getBoundingClientRect().y)));
    expect(new Set(actionRows).size).toBe(viewport.width <= 380 ? 2 : 1);

    await sheet.getByRole("link", { name: /Settings/ }).click();
    await expect(sheet).toBeHidden();
    await expect(page).toHaveURL(/#\/settings$/);
    await expect(more).toHaveAttribute("aria-pressed", "true");
  }
});

for (const theme of ["light", "dark"] as const) {
  test(`Navigation has one visible primary menu across breakpoints in ${theme}`, async ({ page }) => {
    await page.addInitScript((mode) => localStorage.setItem("allstarr.theme", mode), theme);
    await page.emulateMedia({ reducedMotion: theme === "dark" ? "reduce" : "no-preference" });
    await mockApi(page);
    await page.goto("#/library/playlists");
    for (const width of [1606, 901, 900, 761, 760, 390, 320]) {
      await page.setViewportSize({ width, height: 844 });
      const primary = page.getByRole("navigation", { name: "Primary", exact: true });
      await expect(primary).toHaveCount(1);
      for (const name of ["Home", "Library", "Activity"])
        await expect(primary.getByRole("link", { name, exact: true })).toHaveCount(1);
      await expect(primary.getByRole("link", { name: "Library", exact: true }))
        .toHaveAttribute("aria-current", "page");
      await expect(page.locator(".profile a[href='#/settings']")).toHaveCount(0);
      if (width > 760) {
        await expect(page.locator(".mobile-navigation")).toBeHidden();
        await expect(primary.getByRole("link")).toHaveText(["Home", "Library", "For you", "Sources", "Activity", "Settings", "Connect apps", "Report a problem"]);
        await expect(primary.getByRole("link", { name: "Report a problem", exact: true })).toHaveCount(1);
        await expect(primary.getByRole("link", { name: "Connect apps", exact: true })).toHaveCount(1);
        await expect(primary.getByRole("link", { name: "Settings", exact: true })).toHaveCount(1);
        await expect(page.getByRole("button", { name: "More destinations" })).toHaveCount(0);
      } else {
        await expect(page.locator(".desktop-navigation")).toBeHidden();
        await expect(primary.getByRole("link")).toHaveCount(3);
        await expect(primary.getByRole("button", { name: "More destinations" })).toBeVisible();
      }
      if (process.env.ALLSTARR_SCREENSHOT_DIR && [1606, 900, 390].includes(width))
        await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/${theme}-navigation-${width}.png` });
    }
  });
}

test("More restores keyboard focus and closes when returning to desktop", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.goto("#/library/playlists");
  const more = page.getByRole("button", { name: "More destinations" });
  await more.focus();
  await more.press("Enter");
  const sheet = page.getByRole("dialog", { name: "More", exact: true });
  await expect(sheet).toBeVisible();
  await expect(page.locator(".mobile-navigation button")).toHaveAttribute("aria-expanded", "true");
  await expect(sheet.getByRole("link")).toHaveCount(5);
  await expect(sheet.getByRole("link", { name: /Report a problem/ })).toHaveCount(1);
  await expect(sheet.getByRole("link", { name: /^Connect apps\b/ })).toContainText("Connect music apps and get help");
  await page.keyboard.press("Escape");
  await expect(sheet).toBeHidden();
  await expect(more).toBeFocused();
  await more.press("Enter");
  await expect(sheet).toBeVisible();
  await page.setViewportSize({ width: 1280, height: 800 });
  await expect(sheet).toBeHidden();
  await expect(page.getByRole("button", { name: "Collapse sidebar" })).toBeFocused();
  await expect(page.getByRole("navigation", { name: "Primary", exact: true })).toHaveCount(1);
  await page.getByRole("link", { name: "Settings", exact: true }).click();
  await expect(page).toHaveURL(/#\/settings$/);
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(sheet).toBeHidden();
  await more.click();
  await expect(sheet.getByRole("link", { name: /Settings/ })).toHaveAttribute("aria-current", "page");
});

test("Profile artwork is stable in full, slim, and mobile navigation", async ({ page }) => {
  await mockApi(page);
  await page.route("**/api/admin/auth/me/avatar?user=user", (route) => route.fulfill({
    status: 200,
    contentType: "image/gif",
    body: Buffer.from("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==", "base64"),
  }));

  for (const width of [1280, 924, 390]) {
    await page.setViewportSize({ width, height: 844 });
    await page.goto("#/");
    if (width === 390) await page.getByRole("button", { name: "More destinations" }).click();
    const avatar = page.locator(width === 390 ? ".mobile-sheet-profile .avatar" : ".profile .avatar");
    await expect(avatar).toBeVisible();
    await expect(avatar.locator("img")).toBeVisible();
    await expect.poll(async () => (await avatar.boundingBox())?.width ?? 0).toBe(36);
    if (width === 924) {
      await page.getByRole("button", { name: "Collapse sidebar" }).click();
      await expect(page.locator(".app-shell")).toHaveClass(/slim/);
      await expect.poll(async () =>
        (await page.locator(".desktop-navigation").getByRole("link", { name: "Library" }).boundingBox())?.width ?? 0
      ).toBe(40);
      await page.getByRole("button", { name: "Expand sidebar" }).click();
      await expect(page.locator(".app-shell")).not.toHaveClass(/slim/);
    }
    if (width === 390) {
      await expect(page.locator(".mobile-navigation").getByRole("link", { name: "Settings" })).toHaveCount(0);
      await expect(page.getByRole("dialog", { name: "More" }).getByRole("link", { name: /Settings/ })).toBeVisible();
      await expect.poll(async () => (await page.locator(".sidebar").boundingBox())?.height ?? 0).toBeLessThan(80);
      await page.getByRole("button", { name: "Close more destinations" }).click();
    }
  }

  await page.route("**/api/admin/auth/me/avatar?user=user", (route) => route.fulfill({ status: 404 }));
  await page.reload();
  await page.getByRole("button", { name: "More destinations" }).click();
  await expect(page.locator(".mobile-sheet-profile .avatar > span")).toHaveText("T");
  await expect.poll(async () => (await page.locator(".mobile-sheet-profile .avatar").boundingBox())?.width ?? 0).toBe(36);
});

test("Segmented navigation and unified provider filters support keyboard use", async ({ page }) => {
  await mockApi(page);
  await page.goto("#/library/playlists");
  await page.getByRole("tab", { name: "Playlists" }).focus();
  await page.keyboard.press("ArrowRight");
  await expect(page).toHaveURL(/#\/library\/matches$/);
  await expect(page.getByRole("tab", { name: "Matches" })).toHaveAttribute("aria-selected", "true");

  await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
  const dialog = page.getByRole("dialog", { name: "Test song - Remix", exact: true });
  await dialog.getByRole("button", { name: "Search for another version…", exact: true }).click();
  await dialog.getByLabel("Search local library and playable providers").fill("Kiss Me More");
  await dialog.getByRole("button", { name: "Search", exact: true }).click();
  await dialog.getByRole("button", { name: /Jellyfin 1/ }).focus();
  await page.keyboard.press("Space");
  await expect(dialog.getByRole("button", { name: /Jellyfin 1/ })).toHaveAttribute("aria-pressed", "true");

  await page.goto("#/settings/extensions");
  await page.getByRole("button", { name: "Add extension" }).click();
  await page.getByRole("tab", { name: /^Catalog/ }).focus();
  await page.keyboard.press("ArrowRight");
  await expect(page.getByRole("tab", { name: "From a link" })).toHaveAttribute("aria-selected", "true");
});

test("Responsive boundaries preserve navigation and download identity", async ({ page }) => {
  await mockApi(page);
  await page.goto("#/");
  for (const width of [760, 761, 900, 901]) {
    await page.setViewportSize({ width, height: 844 });
    const navigation = page.locator(width <= 760 ? ".mobile-navigation" : ".desktop-navigation");
    await expect(navigation.getByRole("link")).toHaveCount(width <= 760 ? 3 : 8);
    await expect(navigation.locator(width <= 760 ? ":scope > :is(a, button)" : ".navigation-group > a")).toHaveCount(width <= 760 ? 4 : 8);
    await expect(page.locator(".sidebar")).toHaveCSS("position", "fixed");
    if (width === 900)
      await expect.poll(async () => (await page.locator(".sidebar").boundingBox())?.width ?? 0).toBe(80);
    if (width === 901)
      await expect.poll(async () => (await page.locator(".sidebar").boundingBox())?.width ?? 0).toBe(200);
    if (width === 760) {
      const boxes = await navigation.locator(":scope > :is(a, button)").evaluateAll((links) =>
        links.map((link) => link.getBoundingClientRect().height));
      expect(boxes.every((height) => height >= 44)).toBe(true);
    }
    await expect.poll(() => page.evaluate(() =>
      document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  }

  for (const width of [800, 801, 1050, 1051, 1100, 1101]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto("#/library/cached");
    const row = page.locator(".download-row");
    await expect(row.locator(".source-name", { hasText: "Lumen Audio" })).toBeVisible();
    await expect(row.getByRole("button", { name: "Keep", exact: true })).toBeVisible();
    await expect(row.getByRole("button", { name: /Actions for/ })).toBeVisible();
    await expect.poll(() => page.evaluate(() =>
      document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  }
});

test("Sidebar uses an integrated expander and deterministic slim breakpoint", async ({ page }) => {
  await mockApi(page);
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("#/");
  const shell = page.locator(".app-shell");
  const expander = page.getByRole("button", { name: "Collapse sidebar" });
  await expect(expander).toBeVisible();
  const [expanderBox, brandBox] = await Promise.all([
    expander.boundingBox(),
    page.getByRole("link", { name: "Allstarr home" }).boundingBox(),
  ]);
  expect(expanderBox!.x).toBeGreaterThan(brandBox!.x);
  expect(expanderBox!.y).toBeLessThan(brandBox!.y + brandBox!.height);
  expect(expanderBox!.y + expanderBox!.height).toBeGreaterThan(brandBox!.y);
  await expect(page.locator(".desktop-navigation").getByRole("link")).toHaveCount(8);
  await expect(page.locator(".desktop-navigation").getByRole("link", { name: "Connect apps", exact: true })).toBeVisible();
  await expect(page.locator(".desktop-navigation").getByRole("link", { name: "Intelligence" })).toHaveCount(0);
  const libraryIcon = page.locator(".desktop-navigation")
    .getByRole("link", { name: "Library" }).locator("svg");
  const expandedMark = await expander.locator("svg").innerHTML();
  await expander.click();
  await expect(shell).toHaveClass(/slim/);
  await expect(page.getByRole("button", { name: "Expand sidebar" })).toBeVisible();
  await expect.poll(async () => (await page.locator(".sidebar").boundingBox())?.width ?? 0).toBe(80);
  const collapsedIcon = await libraryIcon.boundingBox();
  expect(Math.abs(collapsedIcon!.x + collapsedIcon!.width / 2 - 40)).toBeLessThanOrEqual(0.5);
  const collapsedMark = await page.getByRole("button", { name: "Expand sidebar" })
    .locator("svg").innerHTML();
  expect(collapsedMark).not.toBe(expandedMark);
  const library = page.locator(".desktop-navigation").getByRole("link", { name: "Library" });
  await expect.poll(async () => {
    const [link, icon] = await Promise.all([library.boundingBox(), library.locator("svg").boundingBox()]);
    return Math.abs((link!.y + link!.height / 2) - (icon!.y + icon!.height / 2));
  }).toBeLessThan(0.5);

  await page.setViewportSize({ width: 850, height: 800 });
  await expect(page.getByRole("button", { name: "Expand sidebar" })).toBeVisible();
  await expect.poll(async () => (await page.locator(".sidebar").boundingBox())?.width ?? 0).toBe(80);

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.locator(".mobile-navigation").getByRole("link")).toHaveCount(3);
  await expect(page.locator(".mobile-navigation > :is(a, button)")).toHaveCount(4);
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
});
