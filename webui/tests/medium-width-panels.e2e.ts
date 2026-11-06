import { expect, test, type Locator, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { panelFixture as fixture } from "./shared-design-fixtures";

async function drawer(page: Page, state: "expanded" | "collapsed") {
  if (page.viewportSize()!.width < 900) return;
  const toggle = page.locator(".sidebar-expander");
  await expect(toggle).toBeVisible();
  if ((await toggle.getAttribute("aria-expanded") === "true") !== (state === "expanded")) await toggle.click();
  await expect(toggle).toHaveAttribute("aria-expanded", String(state === "expanded"));
}

async function expectGeometry(page: Page) {
  const result = await page.evaluate(() => {
    const visible = (element: Element) => {
      const rect = element.getBoundingClientRect(), style = getComputedStyle(element);
      return rect.width > 0 && rect.height > 0 && style.display !== "none" && style.visibility !== "hidden";
    };
    const identify = (element: Element) => `${element.tagName.toLowerCase()}.${[...element.classList].slice(0, 2).join(".")} ${(element.textContent ?? "").replace(/\s+/g, " ").trim().slice(0, 65)}`;
    const filterEscapes: string[] = [];
    for (const group of document.querySelectorAll<HTMLElement>(".filter-segments")) {
      if (!visible(group)) continue;
      const bounds = group.getBoundingClientRect();
      for (const button of group.querySelectorAll("button")) {
        if (!visible(button)) continue;
        const rect = button.getBoundingClientRect();
        if (rect.left < bounds.left - 1 || rect.right > bounds.right + 1 || rect.top < bounds.top - 1 || rect.bottom > bounds.bottom + 1) filterEscapes.push(identify(button));
      }
    }
    const narrowText: string[] = [];
    const canvas = document.createElement("canvas"), context = canvas.getContext("2d")!;
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    let node: Node | null;
    while ((node = walker.nextNode())) {
      const text = node.textContent?.replace(/\s+/g, " ").trim() ?? "";
      const element = node.parentElement;
      if (text.length <= 8 || !element || element.closest("script, style, template, svg, [hidden], .sr-only, .visually-hidden") || !visible(element)) continue;
      const style = getComputedStyle(element), bounds = element.getBoundingClientRect();
      let left = Math.max(0, bounds.left), right = Math.min(innerWidth, bounds.right), top = bounds.top, bottom = bounds.bottom;
      let concealed = false;
      for (let ancestor: HTMLElement | null = element; ancestor; ancestor = ancestor.parentElement) {
        const ancestorStyle = getComputedStyle(ancestor), rect = ancestor.getBoundingClientRect();
        if (ancestorStyle.display === "none" || ancestorStyle.visibility === "hidden" || Number(ancestorStyle.opacity) === 0 || ancestorStyle.clipPath === "inset(50%)") { concealed = true; break; }
        if (/(hidden|clip|auto|scroll)/.test(ancestorStyle.overflowX)) { left = Math.max(left, rect.left); right = Math.min(right, rect.right); }
        if (/(hidden|clip|auto|scroll)/.test(ancestorStyle.overflowY)) { top = Math.max(top, rect.top); bottom = Math.min(bottom, rect.bottom); }
      }
      if (concealed || right <= left || bottom <= top) continue;
      const range = document.createRange(); range.selectNodeContents(node);
      if (![...range.getClientRects()].some(rect => rect.right > left && rect.left < right && rect.bottom > top && rect.top < bottom)) continue;
      const padding = Number.parseFloat(style.paddingLeft) + Number.parseFloat(style.paddingRight);
      const available = element instanceof HTMLElement && element.clientWidth ? element.clientWidth - padding : bounds.width;
      context.font = style.font;
      const minimum = context.measureText("0").width * 6;
      if (available + 1 < minimum) narrowText.push(`${identify(element)}: ${available.toFixed(1)}px < ${minimum.toFixed(1)}px`);
    }
    return { overflow: document.documentElement.scrollWidth - innerWidth, filterEscapes, narrowText };
  });
  expect(result.overflow, "document width stays inside the viewport").toBeLessThanOrEqual(1);
  expect(result.filterEscapes, "every filter button stays inside its outline").toEqual([]);
  expect(result.narrowText, "visible text longer than eight characters has at least six character widths").toEqual([]);
}

async function expectPanel(page: Page) {
  const panel = page.locator("[data-side-panel]");
  await expect(panel).toBeVisible();
  await expect(panel).toHaveAttribute("data-mode", /^(docked|modal)$/);
  if (await panel.getAttribute("data-mode") === "docked") {
    const list = page.locator(".panel-open, .activity-content").first();
    await expect(list).toBeVisible();
    expect((await list.boundingBox())!.width, "a docked panel leaves a readable list").toBeGreaterThanOrEqual(520);
    await expect(page.locator(".side-panel-overlay")).toBeHidden();
  } else {
    await expect(panel).toHaveAttribute("aria-modal", "true");
    await expect(page.locator(".side-panel-overlay")).toBeVisible();
  }
  return panel;
}

const screens = [
  { name: "sources-services", url: "/#/sources/services", opener: ".source-row .row-action", title: "Qobuz" },
  { name: "sources-accounts", url: "/#/sources/accounts", opener: 'a[aria-label="Open Apple Music account"]', title: "Apple Music" },
  { name: "library-playlists", url: "/#/library/playlists", opener: 'button[aria-label="Open Evening mix playlist details"]', title: "Evening mix" },
  { name: "library-cached", url: "/#/library/cached", opener: 'button[aria-label="Open song details for Take On Me"]', title: "Take On Me" },
  { name: "library-kept", url: "/#/library/kept", opener: 'button[aria-label="Open song details for Take On Me"]', title: "Take On Me" },
  { name: "library-matches", url: "/#/library/matches", opener: 'button[aria-label="Open match for Evening song One"]:visible', title: "Evening song One" },
  { name: "activity-jobs", url: "/#/activity?view=jobs", opener: ".job-row .row-action", title: "Update playlist" },
  { name: "activity-tasks", url: "/#/activity?view=tasks", opener: ".task-open", title: "Evening mix" },
  { name: "activity-reports", url: "/#/activity?view=reports", opener: ".report-list .row-action", title: "Playback" },
  { name: "activity-plays", url: "/#/activity?view=plays", opener: ".play-row .row-action", title: "Evening song" },
  { name: "for-you-mix", url: "/#/for-you", opener: 'button[aria-label="Open Blend 1"]', title: "Blend 1" },
] as const;
const appearances = [[1024, "dark"], [1100, "dark"], [1180, "dark"], [1280, "dark"], [1100, "light"], [1280, "light"], [390, "dark"], [390, "light"]] as const;

for (const [width, theme] of appearances) for (const state of ["expanded", "collapsed"] as const) for (const screen of screens) {
  test(`${screen.name} at ${width} ${theme} with ${state} drawer`, async ({ page }) => {
    await page.clock.install({ time: new Date("2026-11-04T14:00:00Z") });
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const blocked = await fixture(page);
    await page.goto(screen.url);
    const opener = page.locator(screen.opener).first();
    await expect(opener).toBeVisible();
    if (screen.name === "library-matches") {
      expect((await page.locator(".mapping-heading").boundingBox())!.x).toBeCloseTo((await page.locator(".library-tabs").boundingBox())!.x, 0);
      await expect(page.locator(".mapping-rows")).toHaveCSS("border-radius", "14px");
    }
    await drawer(page, state);
    if (screen.name === "sources-services") await expect(page.locator(".extension-attention")).toContainText("Deezer");
    if (screen.name === "for-you-mix") {
      const cards = page.locator(".mix-shelf .mix-card");
      await expect(cards).toHaveCount(8);
      expect(await cards.evaluateAll(elements => elements.every(element => element.getBoundingClientRect().width >= 132))).toBe(true);
    }
    const content = page.locator(".sources-redesign, .library-playlists, .library-downloads, .mapping-page, .activity-content, .mix-shelf").first();
    const before = await content.boundingBox();
    await opener.click();
    const panel = await expectPanel(page);
    if (await panel.getAttribute("aria-modal") === "true") {
      const after = await content.boundingBox();
      expect(after!.width, "opening a modal sheet preserves the content width").toBeCloseTo(before!.width, 0);
      expect(after!.x, "opening a modal sheet preserves the content position").toBeCloseTo(before!.x, 0);
    }
    await expect(panel.getByRole("heading").first()).toHaveText(screen.title);
    if (width === 1280) {
      await expect(panel).toHaveAttribute("data-mode", "docked");
      expect((await panel.boundingBox())!.width).toBeLessThanOrEqual(420);
      expect(await panel.locator("[data-dialog-title]").evaluate(element => getComputedStyle(element).fontWeight)).toBe("600");
      for (const description of await panel.locator("[data-dialog-description]").all()) {
        expect(await description.evaluate(element => ({ wrap: getComputedStyle(element).whiteSpace, overflow: getComputedStyle(element).textOverflow }))).toEqual({ wrap: "nowrap", overflow: "ellipsis" });
      }
      const header = page.locator(".workspace-header");
      expect((await header.boundingBox())!.x + (await header.boundingBox())!.width).toBeLessThanOrEqual((await panel.boundingBox())!.x);
      const tabs = page.locator(".workspace > .segmented-nav");
      if (await tabs.count()) {
        expect((await tabs.boundingBox())!.x + (await tabs.boundingBox())!.width).toBeLessThanOrEqual((await panel.boundingBox())!.x);
      }
    }
    if (screen.name === "sources-services") await expect(page.locator(".extension-attention")).toContainText("Deezer");
    if (screen.name === "for-you-mix") await expect(panel.getByRole("list", { name: "Songs", exact: true }).getByRole("listitem")).toHaveCount(8);
    await page.evaluate(async () => { await document.fonts.ready; });
    await mkdir("../artifacts/shared-design/after-panels", { recursive: true });
    await page.screenshot({ path: `../artifacts/shared-design/after-panels/${screen.name}-${width}-${theme}-${state}.png`, fullPage: true });
    await expectGeometry(page);
    expect(blocked, "fixtures do not request remote resources").toEqual([]);
    expect(await page.pageErrors()).toEqual([]);
  });
}

async function expectFocusLoop(page: Page, panel: Locator) {
  const targets = panel.locator('a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), summary, [tabindex="0"]');
  const visible = [];
  for (const target of await targets.all()) if (await target.isVisible() && await target.getAttribute("tabindex") !== "-1") visible.push(target);
  expect(visible.length).toBeGreaterThan(1);
  await visible.at(-1)!.focus();
  await page.keyboard.press("Tab");
  await expect(visible[0]).toBeFocused();
  await visible[0].focus();
  await page.keyboard.press("Shift+Tab");
  await expect(visible.at(-1)!).toBeFocused();
}

for (const name of ["sources-services", "library-cached", "activity-jobs"] as const) {
  test(`${name} modal keeps focus and restores its opener after closing`, async ({ page }) => {
    await page.setViewportSize({ width: 1100, height: 900 });
    await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
    await fixture(page);
    const screen = screens.find(item => item.name === name)!;
    await page.goto(screen.url);
    const opener = page.locator(screen.opener).first();
    await expect(opener).toBeVisible();
    await drawer(page, "expanded");
    await opener.focus();
    await page.keyboard.press("Enter");
    const panel = await expectPanel(page);
    await expect(panel).toHaveAttribute("data-mode", "modal");
    await expectFocusLoop(page, panel);
    await page.keyboard.press("Escape");
    await expect(panel).toBeHidden();
    await expect(opener).toBeFocused();
    await opener.press("Enter");
    await expect(panel).toBeVisible();
    await page.locator(".side-panel-overlay").click({ position: { x: 8, y: 450 } });
    await expect(panel).toBeHidden();
    await expect(opener).toBeFocused();
  });
}

test("panel mode follows drawer changes and resizing while it stays open", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  await fixture(page);
  await page.goto("/#/sources/services");
  const opener = page.locator(".source-row .row-action").first();
  await expect(opener).toBeVisible();
  await drawer(page, "collapsed");
  await opener.click();
  const panel = await expectPanel(page);
  await expect(panel).toHaveAttribute("data-mode", "docked");
  await panel.evaluate(element => Reflect.set(element, "retainedPanel", true));
  await drawer(page, "expanded");
  await expect(panel).toHaveAttribute("data-mode", "docked");
  await page.setViewportSize({ width: 1071, height: 900 });
  await expect(panel).toHaveAttribute("data-mode", "modal");
  await expectPanel(page);
  await page.setViewportSize({ width: 1600, height: 900 });
  await expect(panel).toHaveAttribute("data-mode", "docked");
  await expectPanel(page);
  await page.setViewportSize({ width: 1100, height: 900 });
  await expect(panel).toHaveAttribute("data-mode", "modal");
  await expectPanel(page);
  expect(await panel.evaluate(element => Reflect.get(element, "retainedPanel"))).toBe(true);
  await expectGeometry(page);
});


test("source cards and filter pills adapt to narrow containers on desktop", async ({ page }) => {
  await page.setViewportSize({ width: 1180, height: 900 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  await fixture(page);
  await page.goto("/#/sources/services");
  const section = page.locator(".extension-attention");
  await expect(section).toBeVisible();
  await section.evaluate(element => { element.style.width = "440px"; });
  const layout = section.locator(".extension-layout");
  await expect(layout).toBeVisible();
  const boxes = await layout.evaluate(element => {
    const box = (selector: string) => element.querySelector(selector)!.getBoundingClientRect().toJSON();
    return { row: element.getBoundingClientRect().toJSON(), icon: box(".provider-art"), title: box(".extension-title"), description: box(".extension-copy > p"), capabilities: box(".source-row-capabilities"), controls: box(".extension-controls") };
  });
  expect(boxes.title.left).toBeGreaterThan(boxes.icon.right);
  expect(boxes.description.top).toBeGreaterThanOrEqual(boxes.title.bottom);
  expect(boxes.description.left).toBeCloseTo(boxes.row.left, 0);
  expect(boxes.description.width).toBeCloseTo(boxes.row.width, 0);
  expect(boxes.controls.top).toBeGreaterThanOrEqual(boxes.capabilities.bottom);
  const filters = page.locator(".source-toolbar .filter-segments");
  await filters.evaluate(element => { element.style.maxWidth = "220px"; });
  expect(await filters.locator("button").evaluateAll(elements => new Set(elements.map(element => element.getBoundingClientRect().top)).size)).toBeGreaterThan(1);
  await expectGeometry(page);
});
