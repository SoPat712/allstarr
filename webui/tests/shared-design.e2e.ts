import { expect, test, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { panelFixture, settingsFixture, homeFixture } from "./shared-design-fixtures";

const capturePhase: string = "after";
const groups = [
  { name: "library", paths: ["library/playlists", "library/matches", "library/cached", "library/kept", "library/import"] },
  { name: "activity", paths: ["activity", "activity?view=plays", "activity?view=jobs", "activity?view=tasks", "activity?view=reports"] },
  { name: "sources", paths: ["sources/services", "sources/accounts", "connect", "report"] },
  { name: "settings", paths: ["settings", "settings/playback", "settings/library", "settings/discovery", "settings/history", "settings/people", "settings/alerts", "settings/storage", "settings/backups", "settings/health", "settings/setup", "settings/appearance", "preferences"] },
  { name: "for-you", paths: ["for-you", "for-you?section=releases", "for-you?section=taste", "for-you?section=recap"] },
  { name: "home", paths: [""] },
];

async function capture(page: Page, name: string, width: number, theme: string) {
  await page.evaluate(async () => { await document.fonts.ready; (document.activeElement as HTMLElement)?.blur(); window.scrollTo(0, 0); });
  const folder = `test-results/shared-design/${capturePhase}`;
  await mkdir(folder, { recursive: true });
  await page.screenshot({ path: `${folder}/${name}-${width}-${theme}.png`, fullPage: true });
}

async function sharedControls(page: Page, width: number) {
  const header = page.locator(".workspace-header");
  if (await page.locator(".activity-content").count()) {
    const scope = header.getByRole("group", { name: "Activity scope", exact: true });
    await expect(scope).toBeVisible();
    await expect(header.getByRole("button")).toHaveText(["Mine", "Everyone"]);
    await expect(scope.locator('[aria-pressed="true"]')).toHaveCount(1);
  } else await expect(header.getByRole("button")).toHaveCount(0);
  if (width === 1280) {
    expect(await page.evaluate(() => document.fonts.check('400 14px "Roboto"') && document.fonts.check('650 32px "Roboto Flex"'))).toBe(true);
    await expect(page.locator(".sidebar")).toHaveCSS("width", "200px");
    expect(await page.locator(".workspace-header h1").evaluate(element => ({ size: getComputedStyle(element).fontSize, weight: getComputedStyle(element).fontWeight }))).toEqual({ size: "32px", weight: "650" });
    await expect(page.locator('.desktop-navigation [aria-label="Main destinations"] a')).toHaveText(["Home", "Library", "For you", "Sources", "Activity", "Settings"]);
    await expect(page.locator('.desktop-navigation [aria-label="Help and connections"] a')).toHaveText(["Connect apps", "Report a problem"]);
    expect(await page.locator(".desktop-navigation a").evaluateAll(elements => elements.every(element => element.getBoundingClientRect().height <= 40))).toBe(true);
  }
  for (const input of await page.locator('.search-field input[type="search"]').all()) {
    if (!await input.isVisible()) continue;
    expect(await input.evaluate(element => { const style = getComputedStyle(element); return { pill: parseFloat(style.borderRadius) >= 22, border: style.borderTopWidth, background: style.backgroundColor }; })).toEqual({ pill: true, border: "1px", background: "rgba(0, 0, 0, 0)" });
  }
  for (const heading of await page.locator("table thead th").all()) {
    if (!await heading.isVisible()) continue;
    await expect(heading).toHaveCSS("text-transform", "uppercase");
    expect(await heading.evaluate(element => parseFloat(getComputedStyle(element).fontSize))).toBeCloseTo(10.88, 2);
  }
  for (const refresh of await page.locator('button[aria-label^="Refresh"]').all()) {
    if (!await refresh.isVisible()) continue;
    await expect(refresh).toHaveAttribute("data-variant", "text");
    await expect(refresh).toHaveAttribute("data-size", "icon");
    await expect(refresh).toHaveText("");
    await expect(refresh).toHaveAttribute("title", /Refresh/);
  }
  if (width === 390) {
    for (const control of await page.locator('.seg > :is(a, button, label), .tabs > :is(a, button)').all()) {
      if (!await control.isVisible()) continue;
      const bounds = await control.boundingBox();
      expect(bounds!.width).toBeGreaterThanOrEqual(44);
      expect(bounds!.height).toBeGreaterThanOrEqual(44);
    }
  }
  for (const count of await page.locator(".filter-segments .segment-count").all()) {
    await expect(count.locator("..")).toHaveAttribute("role", "radio");
    expect(await count.evaluate(element => parseFloat(getComputedStyle(element).borderRadius))).toBeGreaterThan(10);
  }
  for (const checkbox of await page.locator('.settings-surface [data-slot="checkbox"]').all()) {
    if (!await checkbox.isVisible()) continue;
    const bounds = await checkbox.boundingBox();
    expect(bounds!.height, "the checkbox stays square inside its larger touch target").toBeCloseTo(bounds!.width, 0);
    expect(await checkbox.evaluate(element => { const hit = getComputedStyle(element, "::after"); return parseFloat(hit.width) >= 44 && parseFloat(hit.height) >= 44; })).toBe(true);
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth), "viewport contains the shared surfaces").toBeLessThanOrEqual(1);
}

for (const width of [1280, 390]) for (const theme of ["dark", "light"] as const) for (const group of groups) {
  test(`Shared ${group.name} surfaces ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await page.route("**/*", route => ["127.0.0.1", "localhost"].includes(new URL(route.request().url()).hostname) ? route.continue() : route.abort());
    if (group.name === "settings") await settingsFixture(page);
    else if (["home", "for-you"].includes(group.name)) await homeFixture(page, true);
    else await panelFixture(page);
    for (const path of group.paths) {
      await page.goto(`/#/${path}`);
      await expect(page.locator(".workspace-header h1")).toBeVisible();
      await expect(page.locator(".skeleton-panel")).toHaveCount(0);
      await expect(page.locator('.workspace [aria-busy="true"]:not(.live-state)')).toHaveCount(0);
      await page.evaluate(async () => { await document.fonts.ready; });
      if (capturePhase === "after") {
        await sharedControls(page, width);
        if (["library/playlists", "library/matches", "library/cached", "library/kept"].includes(path)) {
          const list = page.locator(path.endsWith("playlists") ? ".library-rows" : path.endsWith("matches") ? ".mapping-rows" : path.endsWith("kept") ? ".promotion-list" : ".downloads-table-scroll");
          expect(await list.evaluate(element => ({ border: getComputedStyle(element).borderTopWidth, radius: getComputedStyle(element).borderRadius }))).toEqual({ border: "1px", radius: "14px" });
          const title = page.locator(path.endsWith("playlists") ? ".library-heading" : path.endsWith("matches") ? ".mapping-heading" : path.endsWith("kept") ? ".promotion-page-heading" : ".library-downloads-heading");
          await expect(title.locator("h2")).toHaveCSS("font-weight", "600");
          expect((await title.boundingBox())!.x).toBeCloseTo((await page.locator(".library-tabs").boundingBox())!.x, 0);
          if (path === "library/cached" && width === 1280) {
            expect(await page.locator(".library-download-row").first().locator("td").evaluateAll(cells => cells.every(cell => Math.abs(cell.getBoundingClientRect().bottom - cells[0].getBoundingClientRect().bottom) < 1))).toBe(true);
          }
          if (path === "library/kept" && width === 1280) {
            await expect(list.getByRole("columnheader")).toHaveText(["Song", "Source", "Quality", "Kept by", "Size", "Actions"]);
            await expect(list.locator(".kept-song-group > .library-download-row")).toHaveCount(2);
            const centers = await list.locator(".library-download-row").first().locator("td").evaluateAll(cells => cells.map(cell => {
              const box = cell.getBoundingClientRect(); return box.y + box.height / 2;
            }));
            expect(centers).toHaveLength(6);
            expect(Math.max(...centers) - Math.min(...centers)).toBeLessThanOrEqual(1);
          }
        }
        if (path === "settings/alerts") {
          const group = page.locator(".grouped-row").first();
          await expect(group).toHaveAttribute("data-tone", "warn");
          await expect(group.locator(".group-icon")).toBeVisible();
          await expect(group.locator(".group-reason")).toBeVisible();
          await expect(group.locator(".group-action").getByRole("link", { name: "Review sources" })).toBeVisible();
          await expect(group.locator(".group-members")).toBeHidden();
          await group.getByRole("button").focus();
          await page.keyboard.press("Enter");
          await expect(group.locator(".group-members")).toBeVisible();
          await page.keyboard.press("Enter");
        }
        for (const fold of await page.locator(".settings-fold > summary").all()) {
          if (!await fold.isVisible()) continue;
          const shown = await fold.evaluate(element => ({ marker: getComputedStyle(element).listStyleType, label: element.textContent?.trim() ?? "" }));
          expect(shown.marker).toBe("none");
          expect(shown.label).toMatch(/^(Advanced|Exact quality asked from each service|See the details|More filters|Copy details|Choose individual collections|See songs)/);
        }
        if (path === "settings") {
          if (width === 1280) expect((await page.locator(".settings-nav").boundingBox())!.width).toBe(170);
        }
      }
      await capture(page, path.replace(/[/?=]/g, "-") || "home", width, theme);
      if (capturePhase === "after" && path === "library/playlists") {
        await page.getByRole("button", { name: "Import playlist", exact: true }).click();
        const dialog = page.getByRole("dialog", { name: "Import a playlist" });
        await expect(dialog).toBeVisible();
        await expect(dialog.locator(".dialog-actions").getByRole("button")).toHaveText(["Cancel", "Next"]);
        await expect(dialog.getByRole("button", { name: "Cancel", exact: true })).toHaveClass(/text-\[var\(--color-signal-text\)\]/);
        await capture(page, "import-playlist-dialog", width, theme);
        await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
        await expect(dialog).toBeHidden();
      }
      if (capturePhase === "after" && path === "library/cached") {
        await page.getByRole("button", { name: "Remove all…", exact: true }).click();
        const confirmation = page.getByRole("alertdialog");
        await expect(confirmation).toBeVisible();
        await expect(confirmation.getByRole("button", { name: "Cancel", exact: true })).toHaveClass(/text-\[var\(--color-signal-text\)\]/);
        await capture(page, "cached-remove-confirmation", width, theme);
        await confirmation.getByRole("button", { name: "Cancel", exact: true }).click();
        await expect(confirmation).toBeHidden();
      }
    }
    expect(await page.pageErrors()).toEqual([]);
  });
}

for (const width of [1280, 390]) for (const theme of ["dark", "light"] as const) {
  test(`Grouped repeats keep repair controls visible ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await panelFixture(page);
    const destinationId = "33333333-3333-4333-8333-333333333333";
    await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: { accounts: [{ id: destinationId, providerId: "listenbrainz", displayName: "Listening history", ownerDisplayName: "Listener", scope: "Personal", enabled: true, canManage: true, revision: 1, secret: { configured: true, revoked: false } }], audienceUsers: [] } }));
    await page.route("**/api/admin/scrobbling/deliveries?*", route => route.fulfill({ json: { items: Array.from({ length: 6 }, (_, index) => ({ id: `listen-${index}`, revision: 1, targetId: "listenbrainz", state: "permanentfailure", providerCode: "destination-unbound", safeMessage: "The destination needs confirming", requiresReauthentication: false, sendCount: 0, canResend: true, suggestedDestinationAccountId: destinationId, availableDestinationAccountIds: [destinationId], updatedAt: "2026-11-04T12:00:00Z", submission: { title: `Evening song ${index + 1}`, artist: "River", occurredAt: "2026-11-04T12:00:00Z" } })), hasMore: false } }));
    await page.goto("/#/sources/accounts");
    const group = page.locator(".delivery-groups .grouped-row");
    await expect(group).toHaveCount(1);
    await expect(group.locator(".group-count")).toHaveText("6");
    await expect(group.locator(".group-reason")).toHaveText("The destination needs confirming");
    await expect(group.locator(".group-members")).toBeHidden();
    await expect(group.getByRole("button", { name: "Confirm destination", exact: true })).toHaveAttribute("data-variant", "outline");
    await expect(group.getByRole("button", { name: "Retry all", exact: true })).toHaveAttribute("data-variant", "text");
    await group.locator(".group-toggle").focus();
    await page.keyboard.press("Enter");
    await expect(group.locator(".listen-row")).toHaveCount(6);
    await expect(group.locator(".group-members")).toBeVisible();
    await group.locator(".group-chevron").click();
    await expect(group.locator(".group-members")).toBeHidden();
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    await capture(page, "sources-grouped-repeats", width, theme);
    expect(await page.pageErrors()).toEqual([]);
  });
}
