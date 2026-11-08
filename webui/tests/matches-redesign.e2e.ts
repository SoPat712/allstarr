import { expect, test, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const makeSong = (id: string, state: string, confidence: number | null) => ({
  externalSnapshotId: id, providerId: "spotify", state, decisionSource: "track_match_decision", confidence, threshold: .88,
  title: `Song ${id}`, artist: "Example artist", album: "Original album", durationMilliseconds: 180000, isrc: "SOURCE123",
  allowedAuthorityScopes: ["personal", "household"], canonicalRecordingId: `recording-${id}`,
  providerIdentities: [], reasons: [], warnings: [], manualAuthorities: [],
  target: state === "unresolved" ? null : { providerId: "qobuz", externalId: `current-${id}`, title: `Song ${id} (Live)`, artist: "Example artist", album: "Deluxe album", durationMilliseconds: 182000, isrc: "OTHER123" },
  candidates: state === "unresolved" ? [] : [
    { isCurrent: true, isLocal: false, title: `Song ${id} (Live)`, artist: "Example artist", providerTrackIds: { qobuz: `current-${id}` }, confidence: confidence ?? 0 },
    { isLocal: false, title: `Song ${id}`, artist: "Example artist", album: "Original album", durationMilliseconds: 180000,
      providerTrackIds: { "apple-download": `better-${id}` }, confidence: .967, components: { title: 1, artist: 1, duration: 1 }, reasons: ["title_exact", "duration_close"] },
  ],
});

async function fixture(page: Page, options: { needs?: boolean; administrator?: boolean } = {}) {
  const needs = options.needs !== false, administrator = options.administrator !== false;
  const songs = needs ? [makeSong("One", "ambiguous", .967), makeSong("Two", "suggested", .967), makeSong("Three", "unresolved", null)] : [];
  songs.push(makeSong("Four", "accepted", .99), makeSong("Five", "accepted", .98));
  const writes: Record<string, unknown>[] = [];
  await page.route("**/api/**", async route => {
    const url = new URL(route.request().url());
    let body: unknown = {};
    if (url.pathname.endsWith("/auth/me")) body = { authenticated: true, backend: "Local library", user: { id: "listener", name: "Listener", isAdministrator: administrator } };
    if (url.pathname.endsWith("/onboarding/status")) body = { completed: true, shouldRedirectToSetup: false, setupOpen: false, completedSteps: [], recoveryNotices: [], migration: {} };
    if (url.pathname.endsWith("/ui/schema")) body = { activeBackend: "Local library", providers: [
      { id: "spotify", name: "Spotify", categories: ["metadata"] }, { id: "qobuz", name: "Qobuz", categories: ["streaming"] }, { id: "apple-download", name: "Apple Music", categories: ["streaming"] },
    ] };
    if (url.pathname === "/api/admin/playlist-links") body = { playlistLinks: [{ id: "first", name: "First playlist" }, { id: "second", name: "Second playlist" }] };
    if (url.pathname.startsWith("/api/admin/playlist-links/")) body = { tracks: songs.map(item => ({ externalSnapshotId: item.externalSnapshotId })) };
    if (url.pathname.endsWith("/resolve")) { writes.push(route.request().postDataJSON()); body = { success: true }; }
    if (url.pathname.endsWith("/rematch")) body = { rematched: true, state: "suggested" };
    if (url.pathname === "/api/admin/track-matches") {
      const state = url.searchParams.get("state");
      const filtered = songs.filter(item => state === "attention" ? ["ambiguous", "suggested"].includes(item.state)
        : state === "unresolved" ? item.state === state : state === "automatic" ? item.state === "accepted" : state === "manual_matches" || state === "manual_rejections" ? false : true);
      const rows = url.searchParams.has("externalSnapshotId") ? songs.filter(item => item.externalSnapshotId === url.searchParams.get("externalSnapshotId")) : filtered;
      body = { matches: rows.map(item => ({ ...item, allowedAuthorityScopes: administrator ? ["personal", "household"] : ["personal"] })),
        stats: { total: songs.length, matched: 2, accepted: 2, automatic: 2, manualMatches: 0, manualRejections: 0, suggested: needs ? 1 : 0, ambiguous: needs ? 1 : 0, review: needs ? 2 : 0, unresolved: needs ? 1 : 0, rejected: 0, attention: needs ? 2 : 0 },
        pagination: { page: 1, pageSize: 200, total: rows.length, totalPages: 1 } };
    }
    if (url.pathname.endsWith("/current")) body = { target: songs.find(item => url.pathname.includes(`/${item.externalSnapshotId}/`))?.target ?? null };
    if (url.pathname.endsWith("/targets/provider")) body = { tracks: [], providers: [] };
    if (url.pathname.endsWith("/updates/stream")) return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    await route.fulfill({ json: body });
  });
  return writes;
}

async function openSong(page: Page, title = "Song One") {
  const row = page.locator(".mapping-row").filter({ has: page.getByText(title, { exact: true }) });
  const opener = row.locator(".open-match:visible, .row-action:visible");
  await opener.click();
  return { panel: page.getByRole("dialog", { name: title, exact: true }), opener };
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Matches redesign status, rows, panel, bulk and screenshots at ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await fixture(page);
    await page.goto("#/library/matches");
    const status = page.getByRole("region", { name: "Page status", exact: true });
    await expect(status).toContainText("5 songs from 2 playlists");
    await expect(status).toContainText("Matches above 88% are accepted on their own");
    await expect(page.getByRole("img", { name: /^Matched by Allstarr:/ })).toHaveAttribute("aria-label", "Matched by Allstarr: 2, Chosen by you: 0, Unsure: 2, No match: 1");
    await expect(page.getByRole("radio", { name: "Needs a look 3", exact: true })).toHaveAttribute("aria-checked", "true");
    await expect(page.locator(".mapping-row")).toHaveCount(3);
    for (const id of ["One", "Two"]) {
      const pill = page.locator(`.mapping-row[data-snapshot="${id}"] .state-pill`);
      await expect(pill).toHaveText("97%");
      await expect(pill).toHaveAttribute("data-tone", "warn");
    }
    await expect(page.locator('.mapping-row[data-snapshot="Three"] .state-pill')).toHaveAttribute("data-tone", "stop");
    await expect(page.getByText("Song One", { exact: true })).toHaveCount(1);
    await expect(page.getByRole("button", { name: "Open match for Song One", exact: true })).toBeVisible();
    if (width >= 900) {
      const row = page.locator(".mapping-row").filter({ hasText: "Song One" });
      for (const chip of ["Live", "Deluxe album", "+2 s", "Different ISRC"]) await expect(row.locator(".difference-chips")).toContainText(chip);
    }
    await mkdir("test-results/patterns", { recursive: true });
    await page.screenshot({ path: `test-results/patterns/${width === 390 ? "phone-list" : "list"}-${theme}.png` });
    const { panel, opener } = await openSong(page);
    await expect(panel).toBeVisible();
    await expect(panel.locator(".candidate-card .state-pill")).toHaveText("97%");
    await panel.locator(".evidence details summary").click();
    await expect(panel.locator(".evidence details dl > div").filter({ has: page.locator("dt", { hasText: /^Match score$/ }) }).locator("dd")).toHaveText("97%");
    await panel.locator(".evidence details summary").click();
    await expect(panel.getByRole("table")).toContainText("Original");
    await expect(panel).toContainText("Better choices");
    await expect(panel).toContainText("in First playlist, Second playlist");
    await panel.getByRole("button", { name: "More", exact: true }).focus();
    await page.keyboard.press("ArrowDown");
    const songTwo = page.getByRole("dialog", { name: "Song Two", exact: true });
    await expect(songTwo).toBeVisible();
    await expect(page.getByRole("menu")).toHaveCount(0);
    await expect(page.locator('.mapping-row[data-snapshot="Two"]')).toHaveClass(/chosen/);
    await songTwo.getByRole("button", { name: "Search for another version…", exact: true }).click();
    const versionSearch = songTwo.getByRole("textbox", { name: "Search local library and playable providers", exact: true });
    await versionSearch.focus();
    await page.keyboard.press("ArrowUp");
    await page.keyboard.press("ArrowDown");
    await expect(songTwo).toBeVisible();
    await expect(versionSearch).toBeFocused();
    await songTwo.getByRole("button", { name: "Close", exact: true }).focus();
    await page.keyboard.press("ArrowUp");
    await expect(panel).toBeVisible();
    await panel.getByRole("navigation", { name: "Browse items" }).getByRole("button", { name: "Next song", exact: true }).click();
    await expect(songTwo).toBeVisible();
    await songTwo.getByRole("navigation", { name: "Browse items" }).getByRole("button", { name: "Previous song", exact: true }).click();
    await expect(panel).toBeVisible();
    const bounds = await panel.boundingBox();
    expect(bounds!.height).toBeLessThanOrEqual(900);
    if (width === 390) { expect(bounds!.width).toBe(390); expect(bounds!.y).toBe(0); }
    else { expect(bounds!.width).toBe(420); await expect(page.locator(".mapping-rows")).toBeVisible(); }
    await page.screenshot({ path: `test-results/patterns/${width === 390 ? "phone-sheet" : "panel"}-${theme}.png` });
    await panel.getByRole("button", { name: "Nothing fits", exact: true }).click();
    const confirm = page.getByRole("alertdialog", { name: "Nothing fits?", exact: true });
    await expect(confirm).toBeVisible();
    await confirm.getByRole("button", { name: "Cancel", exact: true }).click();
    await page.keyboard.press("Escape");
    await expect(panel).toBeHidden();
    await expect(opener).toBeFocused();
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");
    const nextPanel = page.getByRole("dialog", { name: "Song Two", exact: true });
    await expect(nextPanel).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(nextPanel).toBeHidden();
    await page.getByRole("button", { name: "Select", exact: true }).click();
    await page.getByRole("button", { name: "Select all 3 in this filter", exact: true }).click();
    const bar = page.getByRole("toolbar", { name: "Actions for selected songs" });
    await expect(bar).toContainText("3 selected");
    for (const label of ["Accept", "Rematch", "Reject"]) await expect(bar.getByRole("button", { name: label, exact: true })).toBeVisible();
    await bar.scrollIntoViewIfNeeded();
    await page.screenshot({ path: `test-results/patterns/bulk-bar-${width}-${theme}.png` });
    await page.keyboard.press("?");
    await expect(page.getByRole("dialog", { name: "Matches shortcuts" })).toBeVisible();
  });
}

for (const scope of ["For me", "For the household"]) test(`Matches redesign chooses a preview ${scope}`, async ({ page }) => {
  const writes = await fixture(page);
  await page.goto("#/library/matches");
  const { panel } = await openSong(page);
  await panel.locator(".candidate-card .row-action").click();
  await expect(panel.getByRole("table")).toContainText("Same");
  await panel.locator(".split-trigger").click();
  await page.getByRole("menuitemradio", { name: scope, exact: true }).click();
  await panel.getByRole("button", { name: "Use Apple Music version", exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0]).toMatchObject({ targetType: "provider", externalProvider: "apple-download", externalId: "better-One", authorityScope: scope === "For me" ? "personal" : "household", expectedAuthority: null });
  await expect(panel).toBeHidden();
});

test("Matches redesign defaults to All when clear and remembers a chosen filter", async ({ page }) => {
  await fixture(page, { needs: false });
  await page.goto("#/library/matches");
  await expect(page.getByRole("radio", { name: "All 2", exact: true })).toHaveAttribute("aria-checked", "true");
  await expect(page.getByText("Nothing needs you right now", { exact: true })).toBeVisible();
  for (const [id, label] of [["Four", "99%"], ["Five", "98%"]]) {
    const pill = page.locator(`.mapping-row[data-snapshot="${id}"] .state-pill`);
    await expect(pill).toHaveText(label);
    await expect(pill).toHaveAttribute("data-tone", "ok");
  }
  await page.getByRole("radio", { name: "Matched by Allstarr 2", exact: true }).click();
  await page.reload();
  await expect(page.getByRole("radio", { name: "Matched by Allstarr 2", exact: true })).toHaveAttribute("aria-checked", "true");
});

test("Matches redesign keyboard avoids text fields and confirms scope for acceptance", async ({ page }) => {
  const writes = await fixture(page, { administrator: false });
  await page.goto("#/library/matches");
  await expect(page.getByRole("searchbox", { name: "Search", exact: true })).toBeVisible();
  await page.keyboard.press("/");
  await expect(page.getByRole("searchbox", { name: "Search", exact: true })).toBeFocused();
  await expect(page.getByRole("searchbox", { name: "Search", exact: true })).toHaveValue("");
  await page.keyboard.press("/");
  await expect(page.getByRole("searchbox", { name: "Search", exact: true })).toHaveValue("/");
  await page.getByRole("searchbox", { name: "Search", exact: true }).fill("");
  await page.keyboard.type("ar?");
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await page.getByRole("searchbox", { name: "Search", exact: true }).fill("");
  await page.getByRole("radio", { name: "Needs a look 3", exact: true }).focus();
  await page.keyboard.press("/");
  await expect(page.getByRole("searchbox", { name: "Search", exact: true })).toBeFocused();
  await expect(page.getByRole("searchbox", { name: "Search", exact: true })).toHaveValue("");
  await page.getByRole("heading", { name: "Matches", exact: true }).click();
  const { panel } = await openSong(page);
  await panel.locator(".candidate-card .row-action").click();
  await panel.getByRole("heading", { name: "Song One", exact: true, level: 2 }).click();
  await page.keyboard.press("a");
  const scope = page.getByRole("alertdialog", { name: "Use this version?" });
  await expect(scope).toBeVisible();
  await expect(scope.getByRole("button", { name: "Save version for" })).toContainText("For me");
  await scope.getByRole("button", { name: "Use version", exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].authorityScope).toBe("personal");
});

test("Matches redesign queues search while loading and releases shortcuts on navigation", async ({ page }) => {
  await fixture(page);
  await page.route("**/api/admin/playlist-links", route => route.fulfill({ json: { playlistLinks: [] } }));
  let release!: () => void;
  const pending = new Promise<void>(resolve => { release = resolve; });
  await page.route("**/api/admin/track-matches?*", async route => {
    await pending;
    await route.fallback();
  });
  await page.goto("#/library/matches");
  await expect(page.getByRole("region", { name: "Loading match review" })).toBeVisible();
  try {
    await page.keyboard.press("/");
  } finally { release(); }
  await expect(page.getByRole("searchbox", { name: "Search", exact: true })).toBeFocused();
  await page.evaluate(() => { window.location.hash = "/library/playlists"; });
  await expect(page.getByRole("heading", { name: "No playlists yet", exact: true })).toBeVisible();
  await expect(page.locator(".mapping-page")).toHaveCount(0);
  expect(await page.evaluate(() => {
    const event = new KeyboardEvent("keydown", { key: "/", bubbles: true, cancelable: true });
    document.body.dispatchEvent(event);
    return event.defaultPrevented;
  })).toBe(false);
  await page.evaluate(() => { window.location.hash = "/library/mappings"; });
  await expect(page.getByRole("searchbox", { name: "Search", exact: true })).toBeVisible();
  await page.getByRole("heading", { name: "Matches", exact: true }).click();
  await page.keyboard.press("/");
  await expect(page.getByRole("searchbox", { name: "Search", exact: true })).toBeFocused();
});

test("Matches redesign renders each song once when the layout changes", async ({ page }) => {
  await fixture(page);
  await page.goto("#/library/matches");
  for (const width of [1280, 390, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    const row = page.locator('.mapping-row[data-snapshot="One"]');
    await expect(row.getByText("Song One", { exact: true })).toHaveCount(1);
    await expect(row.getByRole("button", { name: "Open match for Song One", exact: true })).toBeVisible();
    await expect(row.locator(".phone-cell")).toHaveCount(width < 900 ? 1 : 0);
    await expect(row.locator(".source-cell")).toHaveCount(width < 900 ? 0 : 1);
  }
});

for (const width of [320, 390, 760, 899]) test(`Matches redesign provides full touch targets at ${width}px`, async ({ page }) => {
  await page.setViewportSize({ width, height: 900 });
  await page.emulateMedia({ reducedMotion: "reduce" });
  await fixture(page);
  await page.goto("#/library/matches");
  await expect(page.locator(".mapping-row").first()).toBeVisible();
  for (const control of [page.getByRole("button", { name: "Refresh", exact: true }), page.getByRole("button", { name: "More actions for Song One", exact: true })]) {
    const bounds = await control.boundingBox();
    expect(bounds!.width).toBeGreaterThanOrEqual(44);
    expect(bounds!.height).toBeGreaterThanOrEqual(44);
  }
  const { panel } = await openSong(page);
  const subtitle = panel.locator(".panel-header-extra");
  await expect(subtitle).toHaveText("Original album · 3:00 · in First playlist, Second playlist");
  expect(await subtitle.evaluate(element => {
    const bounds = element.getBoundingClientRect();
    const range = document.createRange();
    range.selectNodeContents(element);
    return [...range.getClientRects()].every(rect => rect.left >= bounds.left - 1 && rect.right <= bounds.right + 1);
  })).toBe(true);
  for (const label of ["Previous song", "Next song", "More", "Close", "Search for another version…"]) {
    const bounds = await panel.getByRole("button", { name: label, exact: true }).boundingBox();
    expect(bounds!.width).toBeGreaterThanOrEqual(44);
    expect(bounds!.height).toBeGreaterThanOrEqual(44);
  }
  if (width <= 760) await expectContainedMobileGeometry(page);
});

test.describe("Matches redesign with a coarse pointer", () => {
  test.use({ hasTouch: true });
  test("provides full touch targets on a wide screen", async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await fixture(page);
    await page.goto("#/library/matches");
    expect(await page.evaluate(() => matchMedia("(pointer: coarse)").matches)).toBe(true);
    const refresh = await page.getByRole("button", { name: "Refresh", exact: true }).boundingBox();
    expect(refresh!.width).toBeGreaterThanOrEqual(44);
    expect(refresh!.height).toBeGreaterThanOrEqual(44);
    const { panel } = await openSong(page);
    for (const label of ["Previous song", "Next song", "More", "Close", "Search for another version…"]) {
      const bounds = await panel.getByRole("button", { name: label, exact: true }).boundingBox();
      expect(bounds!.width).toBeGreaterThanOrEqual(44);
      expect(bounds!.height).toBeGreaterThanOrEqual(44);
    }
  });
});

test("Matches redesign keeps desktop status, rows and panel controls compact", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await fixture(page);
  await page.goto("#/library/matches");
  const status = page.getByRole("region", { name: "Page status", exact: true });
  await expect(status).toHaveCSS("border-top-width", "0px");
  await expect(status).toHaveCSS("padding", "0px");
  await expect(status.locator(".status-items strong")).toHaveText("5 songs from 2 playlists");
  await expect(status.locator(".status-dot")).toHaveCount(0);
  const legendLines = await page.locator(".match-legend li").evaluateAll(items => new Set(items.map(item => item.getBoundingClientRect().top)).size);
  expect(legendLines).toBe(1);
  const bar = await page.locator(".match-status-bar").boundingBox();
  const statusBounds = await status.boundingBox();
  expect(bar!.width).toBeCloseTo(statusBounds!.width, 0);
  const headingBounds = await page.locator(".mapping-heading").boundingBox();
  expect(bar!.width).toBeCloseTo(headingBounds!.width, 0);
  const rematchBounds = await page.locator(".match-title").getByRole("button", { name: "Rematch all", exact: true }).boundingBox();
  expect(rematchBounds!.x + rematchBounds!.width).toBeCloseTo(headingBounds!.x + headingBounds!.width, 0);
  const ink = await page.locator(".mapping-queue").evaluate(element => getComputedStyle(element).color);
  const row = page.locator('.mapping-row[data-snapshot="One"]');
  await expect(row.locator(".source-copy strong")).toHaveCSS("color", ink);
  await expect(row.locator(".plays-as > span")).toHaveCSS("color", ink);
  const menu = row.getByRole("button", { name: "More actions for Song One", exact: true });
  const chevron = row.getByRole("button", { name: "Show match for Song One", exact: true });
  const menuBounds = await menu.boundingBox(), chevronBounds = await chevron.boundingBox();
  expect(menuBounds!.width).toBe(32);
  expect(chevronBounds!.width).toBe(32);
  expect(menuBounds!.y).toBeCloseTo(chevronBounds!.y, 0);
  expect(chevronBounds!.x).toBeGreaterThanOrEqual(menuBounds!.x + menuBounds!.width);
  await page.mouse.click(menuBounds!.x + menuBounds!.width / 2, menuBounds!.y - 4);
  await expect(page.getByRole("menu")).toBeVisible();
  await page.keyboard.press("Escape");
  const unresolved = page.locator('.mapping-row[data-snapshot="Three"]');
  await expect(unresolved.locator(".plays-as")).toHaveText("No match yet");
  await expect(unresolved.locator(".state-pill")).toHaveText("No match");
  await expect(unresolved.locator(".difference-chips")).toBeEmpty();
  for (const width of [1280, 1100]) {
    await page.setViewportSize({ width, height: 900 });
    const search = page.getByRole("searchbox", { name: "Search", exact: true });
    expect((await search.boundingBox())!.width).toBeGreaterThanOrEqual(280);
    const controls = await page.locator(".matches-toolbar").evaluate(element => [...element.children].map(child => {
      const rect = child.getBoundingClientRect();
      return { y: rect.y, height: rect.height };
    }));
    const centers = controls.map(rect => rect.y + rect.height / 2);
    expect(Math.max(...centers) - Math.min(...centers)).toBeLessThanOrEqual(1);
    const table = await page.locator(".matches-table").boundingBox(), column = await page.locator(".mapping-rows").boundingBox();
    const edges = await page.locator(".mapping-rows").evaluate(element => {
      const style = getComputedStyle(element);
      return parseFloat(style.borderLeftWidth) + parseFloat(style.borderRightWidth);
    });
    expect(table!.width).toBeCloseTo(column!.width - edges, 0);
    const chips = await row.locator(".difference-chips span").evaluateAll(items => items.map(item => item.getBoundingClientRect().top));
    expect(new Set(chips).size).toBeLessThanOrEqual(2);
  }
  await page.setViewportSize({ width: 1280, height: 900 });
  const placeholderFits = await page.getByRole("searchbox", { name: "Search", exact: true }).evaluate(input => {
    const style = getComputedStyle(input), canvas = document.createElement("canvas"), context = canvas.getContext("2d")!;
    context.font = style.font;
    return context.measureText(input.getAttribute("placeholder")!).width <= input.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
  });
  expect(placeholderFits).toBe(true);
  const { panel } = await openSong(page);
  expect((await panel.locator(".panel-header").boundingBox())!.height).toBeLessThanOrEqual(110);
  for (const label of ["Previous song", "Next song", "More", "Close"]) {
    const control = panel.getByRole("button", { name: label, exact: true });
    expect((await control.boundingBox())!.width).toBe(32);
    expect((await control.boundingBox())!.height).toBe(32);
    const hit = await control.evaluate(element => {
      const style = getComputedStyle(element, "::after");
      return { width: parseFloat(style.minWidth), height: parseFloat(style.minHeight), overflow: getComputedStyle(element).overflow };
    });
    expect(hit.width).toBeGreaterThanOrEqual(44);
    expect(hit.height).toBeGreaterThanOrEqual(44);
    expect(hit.overflow).toBe("visible");
  }
  await expect(panel.locator(".panel-header-extra")).toHaveText("Original album · 3:00 · in First playlist, Second playlist");
  const searchToggle = panel.getByRole("button", { name: "Search for another version…", exact: true });
  expect((await searchToggle.boundingBox())!.height).toBe(32);
  await expect(searchToggle).toHaveAttribute("aria-expanded", "false");
  const versionSearch = panel.getByRole("textbox", { name: "Search local library and playable providers", exact: true });
  await expect(versionSearch).toHaveCount(0);
  await searchToggle.click();
  await expect(searchToggle).toHaveAttribute("aria-expanded", "true");
  await expect(versionSearch).toBeFocused();
  await searchToggle.click();
  await expect(versionSearch).toHaveCount(0);
  const reject = await panel.getByRole("button", { name: "Nothing fits", exact: true }).boundingBox();
  const use = await panel.getByRole("button", { name: "Use Qobuz version", exact: true }).boundingBox();
  expect(reject!.y + reject!.height / 2).toBeCloseTo(use!.y + use!.height / 2, 0);
  expect(use!.x).toBeGreaterThan(reject!.x + reject!.width);
  const footer = await panel.locator(".panel-footer").boundingBox();
  await panel.locator(".panel-body").evaluate(element => element.scrollTop = element.scrollHeight);
  expect((await panel.locator(".panel-footer").boundingBox())!.y).toBe(footer!.y);
  await expect(panel.getByRole("heading", { name: "Better choices", exact: true })).toHaveCSS("text-transform", "uppercase");
  const more = await panel.getByRole("button", { name: "More", exact: true }).boundingBox();
  await page.mouse.click(more!.x + more!.width / 2, more!.y - 4);
  await expect(page.getByRole("menu")).toBeVisible();
});

test("Matches redesign Nothing fits saves a rejection after confirmation", async ({ page }) => {
  const writes = await fixture(page);
  await page.goto("#/library/matches");
  const { panel } = await openSong(page);
  await panel.getByRole("button", { name: "Nothing fits", exact: true }).click();
  expect(writes).toHaveLength(0);
  await page.getByRole("alertdialog", { name: "Nothing fits?" }).getByRole("button", { name: "Reject match", exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0]).toMatchObject({ targetType: "reject", authorityScope: "household" });
});

test("Matches redesign keeps save failures visible while version search is collapsed", async ({ page }) => {
  await fixture(page);
  await page.route("**/api/admin/track-matches/One/resolve", route => route.fulfill({ status: 409, json: { error: "The saved version changed. Try again." } }));
  await page.goto("#/library/matches");
  const { panel } = await openSong(page);
  await panel.getByRole("button", { name: "Use Qobuz version", exact: true }).click();
  await expect(panel.getByRole("alert")).toBeVisible();
  await expect(panel.getByRole("button", { name: "Search for another version…", exact: true })).toHaveAttribute("aria-expanded", "false");
});


test("Opening a match refreshes current recording facts and late facts cannot replace the next song", async ({ page }) => {
  await fixture(page);
  let finish!: () => void;
  const pending = new Promise<void>(resolve => { finish = resolve; });
  await page.route(url => url.pathname.endsWith("/current"), async route => {
    const id = new URL(route.request().url()).pathname.split("/").at(-2)!;
    if (id === "One") await pending;
    const song = makeSong(id, "ambiguous", .9);
    song.target = { ...song.target!, title: `Loaded ${id}`, album: "Actual current album" };
    await route.fulfill({ json: { target: song.target } });
  });
  await page.goto("/#/library/matches");
  const { panel } = await openSong(page);
  await expect(panel.getByRole("status")).toHaveText("Loading current recording details…");
  await panel.getByRole("button", { name: "Next song", exact: true }).click();
  const second = page.getByRole("dialog", { name: "Song Two", exact: true });
  await expect(second.getByRole("table").getByText("Loaded Two", { exact: true })).toBeVisible();
  const late = page.waitForResponse(response => response.url().endsWith("/One/current"));
  finish(); await (await late).finished();
  await expect(second.getByRole("table").getByText("Loaded Two", { exact: true })).toBeVisible();
  await expect(second.getByText("Loaded One", { exact: true })).toHaveCount(0);
});


test("Match status and legend format large counts and put rematch before filters", async ({ page }) => {
  await fixture(page);
  await page.route(url => url.pathname === "/api/admin/track-matches", route => route.fulfill({ json: {
    matches: [makeSong("One", "ambiguous", .74)],
    stats: { total: 4999, matched: 4998, accepted: 4998, automatic: 4991, manualMatches: 7, manualRejections: 0, suggested: 0, ambiguous: 1, review: 1, unresolved: 0, rejected: 0, attention: 1 },
    pagination: { page: 1, pageSize: 50, total: 1, totalPages: 1 },
  } }));
  await page.goto("/#/library/matches");
  await expect(page.getByRole("region", { name: "Page status", exact: true })).toContainText("4,999 songs");
  await expect(page.getByRole("region", { name: "Page status", exact: true })).toContainText("1 needs a look");
  await expect(page.locator(".match-legend")).toContainText("4,991");
  const rematch = await page.getByRole("button", { name: "Rematch all", exact: true }).boundingBox();
  const filters = await page.getByRole("radiogroup", { name: "Match filters", exact: true }).boundingBox();
  expect(rematch!.y).toBeLessThan(filters!.y);
  await expect(page.getByRole("button", { name: "Select", exact: true })).toHaveClass(/border-\[var\(--color-edge-strong\)\]/);
});

test("Live changes wait for a slow match list instead of restarting it", async ({ page }) => {
  await page.addInitScript(() => {
    class FixtureStream {
      static instances: FixtureStream[] = [];
      listeners = new Map<string, EventListener[]>();
      constructor() { FixtureStream.instances.push(this); }
      addEventListener(type: string, listener: EventListener) { this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]); }
      close() { FixtureStream.instances = FixtureStream.instances.filter(item => item !== this); }
    }
    let revision = 0;
    Object.defineProperty(window, "EventSource", { value: FixtureStream });
    Object.assign(window, { __emitMatchUpdate: () => {
      revision++;
      const event = { data: JSON.stringify({ resource: "track-match", resourceId: `song-${revision}`, revision }), lastEventId: `match-${revision}` };
      for (const stream of FixtureStream.instances) for (const listener of stream.listeners.get("update") ?? []) listener(event as unknown as Event);
    } });
  });
  await fixture(page);
  let lists = 0;
  await page.route(url => url.pathname === "/api/admin/track-matches", async route => {
    lists++;
    await new Promise(resolve => setTimeout(resolve, 1500));
    await route.fallback();
  });
  await page.goto("/#/library/matches");
  for (let index = 0; index < 8; index++) {
    await page.evaluate(() => (window as unknown as { __emitMatchUpdate: () => void }).__emitMatchUpdate());
    await page.waitForTimeout(300);
  }
  await expect(page.locator(".mapping-row").first()).toBeVisible({ timeout: 10_000 });
  expect(lists).toBeLessThanOrEqual(4);
});
