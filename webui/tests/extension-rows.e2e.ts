import { expect, test, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { panelFixture } from "./shared-design-fixtures";
import type { ExtensionPackage } from "../src/lib/api";

async function fixture(page: Page) {
  const blocked = await panelFixture(page);
  const packages: ExtensionPackage[] = ["Deezer", "Lumen", "Pulse", "Nova", "Echo", "Prism"].map((displayName, index) => ({
    id: displayName.toLowerCase(), extensionId: displayName.toLowerCase(), displayName, version: "1.4.16",
    state: index === 4 ? "failed" : index === 5 ? "disabled" : "active", lifecycle: "installed",
    active: index < 4, installed: true, permissionReviewRequired: index === 1, hasPermissions: false,
    usesSignedSession: index === 0, capabilities: ["metadata", "streaming"], revision: 1,
    failureCode: index === 4 ? "activation_failed" : null,
    description: "Search the catalog and stream songs from your connected account.",
  }));
  await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
    activeBackend: "Jellyfin", settings: [], priorityGroups: [], configSections: [], providers: [
      { id: "qobuz", name: "Qobuz", categories: ["streaming"], runtimeCapabilities: [{ id: "streaming", ready: true, health: "healthy" }] },
      ...packages.map(item => ({ id: item.extensionId, name: item.displayName, categories: item.capabilities,
        implementationOrigin: "extension", usesSignedSession: item.usesSignedSession,
        runtimeCapabilities: [{ id: "streaming", ready: true, health: "healthy" }] })),
    ],
  } }));
  await page.route("**/api/admin/extensions/packages", route => route.fulfill({ json: packages }));
  await page.route("**/api/admin/extensions/store", route => route.fulfill({ json: { items: packages.slice(0, 4).map(item => ({
    id: item.extensionId, displayName: item.displayName, version: "1.4.17", types: item.capabilities,
  })), errors: [] } }));
  await page.route("**/api/admin/extensions/packages/deezer/session", route => route.fulfill({ json: {
    state: "verification_pending", verificationUrl: "https://verify.example.test/", accountId: null,
  } }));
  return blocked;
}

for (const width of [1280, 320, 390, 600, 601, 1100]) test(`extension rows stay compact at ${width} dark`, async ({ page }) => {
  await page.setViewportSize({ width, height: 900 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  await page.addInitScript(() => {
    localStorage.setItem("allstarr.theme", "dark");
    localStorage.setItem("allstarr.sources.filter", "all");
  });
  const blocked = await fixture(page);
  await page.goto("/#/sources/services");
  const list = page.getByRole("list", { name: "Extensions", exact: true });
  const rows = list.getByRole("listitem");
  await expect(rows).toHaveCount(6);
  await expect(page.getByRole("heading", { name: "Extensions · 6 need you", exact: true })).toBeVisible();
  const bulk = page.getByRole("button", { name: "Update and review all (4)", exact: true });
  await expect(bulk).toBeVisible();
  await expect(list.locator(".extension-session, .source-row-capabilities, .row-chips, input")).toHaveCount(0);
  await expect(page.getByLabel("Sign-in link or code", { exact: true })).toHaveCount(0);
  for (const [index, label] of ["Update", "Update", "Update", "Update", "Retry", "Enable"].entries()) {
    const row = rows.nth(index);
    await expect(row.locator(".item-row")).toHaveCount(1);
    await expect(row.locator(".row-controls > [data-slot=button]")).toHaveCount(1);
    await expect(row.getByRole("button", { name: label, exact: true })).toBeVisible();
    await expect(row.getByRole("button", { name: "Manage extension", exact: true })).toBeVisible();
    await expect(row.locator(".row-action")).toHaveAttribute("href", `#/sources/services/${["deezer", "lumen", "pulse", "nova", "echo", "prism"][index]}`);
    const pill = row.locator(".state-pill");
    await expect(pill).toHaveText(index === 1 ? "New access" : index === 4 ? "Failed" : index === 5 ? "Off" : "New version");
    await expect(pill).toHaveAttribute("data-tone", index === 1 ? "warn" : index === 4 ? "stop" : "neutral");
    if (width <= 600) await expect(pill).toBeHidden();
    else {
      await expect(pill).toBeVisible();
      const buttonText = (await row.locator(".row-controls > [data-slot=button]").innerText()).trim().toLowerCase();
      expect((await pill.innerText()).trim().toLowerCase().split(/\s+/), "pill and action do not repeat the same word").not.toContain(buttonText);
    }
  }
  await expect(rows.nth(0).locator(".row-copy p")).toHaveText("Update 1.4.16 → 1.4.17");
  await expect(rows.nth(1).locator(".row-copy p")).toHaveText("Update 1.4.16 → 1.4.17 · asks for new access");
  await expect(rows.nth(4).locator(".row-copy p")).toHaveText("Couldn't load: The extension could not be enabled.");
  await expect(rows.nth(5).locator(".row-copy p")).toHaveText("Turned off");
  await page.evaluate(async () => { await document.fonts.ready; });
  {
    const normal = page.getByRole("list", { name: "Services", exact: true }).locator(".item-row").first();
    const normalHeight = (await normal.boundingBox())!.height;
    for (const row of await rows.locator(".item-row").all())
      expect(Math.abs((await row.boundingBox())!.height - normalHeight), "extension and source row heights match").toBeLessThanOrEqual(2);
    expect(await list.locator(".row-copy h3").evaluateAll(elements => elements.filter(element =>
      element.scrollWidth > element.clientWidth + 1 || element.scrollHeight > element.clientHeight + 1,
    ).map(element => element.textContent)), "source names are not clipped").toEqual([]);
    expect(await list.locator(".row-copy p").evaluateAll(elements => {
      const context = document.createElement("canvas").getContext("2d")!;
      return elements.flatMap(element => {
        const style = getComputedStyle(element);
        context.font = style.font;
        const minimum = context.measureText("0").width * 6;
        const row = element.closest(".source-row")!;
        const link = row.querySelector(".row-action")!;
        return element.clientWidth + 1 < minimum || element.scrollHeight > element.clientHeight + 1 ||
          style.textOverflow !== "ellipsis" || style.whiteSpace !== "nowrap" ||
          row.getAttribute("title") !== element.textContent || link.getAttribute("aria-describedby") !== element.id
          ? [element.textContent] : [];
      });
    }), "single-line facts stay readable and expose their full text").toEqual([]);
    expect(await rows.locator(".row-controls button").evaluateAll(elements => elements.filter(element => {
      const bounds = element.getBoundingClientRect(), row = element.closest(".item-row")!.getBoundingClientRect();
      const range = document.createRange();
      range.selectNodeContents(element);
      return bounds.left < row.left || bounds.right > row.right || [...range.getClientRects()].some(rect =>
        rect.left < bounds.left - 1 || rect.right > bounds.right + 1 || rect.top < bounds.top - 1 || rect.bottom > bounds.bottom + 1);
    }).map(element => element.textContent)), "row controls are not clipped").toEqual([]);
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    if (width === 320) {
      const heading = await page.getByRole("heading", { name: "Extensions · 6 need you", exact: true }).boundingBox();
      expect((await bulk.boundingBox())!.y).toBeGreaterThanOrEqual(heading!.y + heading!.height);
    }
  }
  if (width === 1100) {
    await list.scrollIntoViewIfNeeded();
    await rows.first().getByRole("link", { name: "Open Deezer", exact: true }).click();
    const panel = page.locator("[data-side-panel]");
    await expect(panel).toBeVisible();
    await expect(panel.getByLabel("Sign-in link or code", { exact: true })).toBeVisible();
    await panel.getByLabel("Sign-in link or code", { exact: true }).scrollIntoViewIfNeeded();
    await expect(panel.locator(".extension-package .extension-row")).toHaveCount(1);
    await expect(panel.locator(".source-row-capabilities")).toBeVisible();
    await expect(list.locator("input")).toHaveCount(0);
    await expect(page).toHaveURL(/#\/sources\/services\/deezer$/);
  }
  if ([1280, 390, 1100].includes(width)) {
    await mkdir("test-results", { recursive: true });
    if (width === 390) await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    await page.screenshot({ path: `test-results/extensions-${width}-dark${width === 1100 ? "-panel" : ""}.png`, fullPage: width === 1280 });
  }
  expect(blocked).toEqual([]);
  expect(await page.pageErrors()).toEqual([]);
});

test("extension state remains visible without a row action on phones", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await fixture(page);
  await page.goto("/#/sources/services");
  const rows = page.getByRole("list", { name: "Extensions", exact: true }).getByRole("listitem");
  const row = rows.first();
  await expect(row.locator(".state-pill")).toBeHidden();
  await row.locator(".row-controls > [data-slot=button]").evaluate(element => element.remove());
  await expect(row.locator(".state-pill")).toBeVisible();
  await expect(row.locator(".state-pill")).toHaveText("New version");
  await expect(rows.nth(1).locator(".state-pill")).toBeHidden();
});

test("extension group counts updates and keeps unavailable data above its rows", async ({ page }) => {
  await fixture(page);
  const packages = ["deezer", "lumen"].map(extensionId => ({
    id: extensionId, extensionId, displayName: extensionId === "deezer" ? "Deezer" : "Lumen", version: "1.4.16",
    state: "active", lifecycle: "active", active: true, installed: true, permissionReviewRequired: false,
    hasPermissions: false, revision: 1,
  }));
  await page.route("**/api/admin/extensions/packages", route => route.fulfill({ json: packages }));
  await page.goto("/#/sources/services");
  await expect(page.getByRole("heading", { name: "Extension updates · 2", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Update and review all (2)", exact: true })).toBeVisible();
  packages[1] = { ...packages[1], state: "disabled", active: false };
  await page.route("**/api/admin/extensions/store", route => route.fulfill({ status: 503, json: { error: "Unavailable" } }));
  await page.reload();
  const banner = page.getByText("Some extension data is unavailable.", { exact: true });
  const row = page.getByRole("list", { name: "Extensions", exact: true }).getByRole("listitem");
  await expect(banner).toBeVisible();
  await expect(row).toHaveCount(1);
  await expect(row.locator(".row-copy p")).toHaveText("Turned off");
  const bounds = (await banner.boundingBox())!;
  expect(bounds.y + bounds.height).toBeLessThanOrEqual((await row.boundingBox())!.y);
  await expect(page.getByRole("button", { name: /Update and review all/ })).toHaveCount(0);
});
