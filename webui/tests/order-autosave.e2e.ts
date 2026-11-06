import { expect, test, type Page } from "@playwright/test";
import { appearance, capture, mockForYou } from "./for-you-fixtures";

async function routing(page: Page, conflict = false, slow = false) {
  await mockForYou(page, { admin: true });
  let order = "deezer,soundcloud,backend", release: (() => void) | undefined;
  const writes: Record<string, string>[] = [];
  await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
    providers: [{ id: "deezer", name: "Deezer" }, { id: "soundcloud", name: "SoundCloud" }, { id: "backend", name: "Media server" }],
    configSections: [], priorityGroups: [{ id: "metadata", label: "Search", envKey: "MULTI_PROVIDER_METADATA_ORDER", providers: order.split(",") }],
  } }));
  await page.route("**/api/admin/providers/scores", route => route.fulfill({ json: { providers: [
    { providerId: "deezer", capabilities: [{ capability: "metadata", score: 50 }] },
    { providerId: "soundcloud", capabilities: [{ capability: "metadata", score: 90 }] },
  ] } }));
  await page.route("**/api/admin/config", async route => {
    if (route.request().method() === "POST") {
      const updates = route.request().postDataJSON().updates;
      writes.push(updates);
      if (slow && writes.length === 1) await new Promise<void>(resolve => { release = resolve; });
      if (conflict) { order = "backend,soundcloud,deezer"; return route.fulfill({ status: 409, json: { error: "Order changed in another session." } }); }
      order = updates.MULTI_PROVIDER_METADATA_ORDER;
      return route.fulfill({ json: { message: "Saved", updatedKeys: Object.keys(updates) } });
    }
    return route.fulfill({ json: { providers: { metadataOrder: order } } });
  });
  await page.goto("/#/settings/routing");
  await expect(page.locator(".routing-group li strong")).toHaveText(["Deezer", "SoundCloud", "Media server"]);
  return { writes, release: () => release?.() };
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Source order saves and undoes at ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme);
  const fixture = await routing(page);
  if (width <= 560) {
    const heights = await page.locator(".score-toggle").evaluateAll(buttons => buttons.map(button => button.getBoundingClientRect().height));
    expect(heights).toHaveLength(2);
    expect(Math.min(...heights)).toBeGreaterThanOrEqual(44);
  }
  await page.clock.install(); await page.clock.pauseAt(new Date());
  await expect(page.locator(".routing-group").getByText("Save order", { exact: true })).toHaveCount(0);
  await page.getByLabel("Reorder SoundCloud", { exact: true }).click();
  const up = page.getByRole("button", { name: "Move SoundCloud up", exact: true });
  await up.focus(); await page.keyboard.press("Enter");
  await expect(page.locator(".routing-group").getByRole("status")).toHaveText("SoundCloud moved to position 1.");
  await expect(page.getByRole("region", { name: "Notifications", exact: true }).getByRole("status")).toHaveCount(0);
  await page.clock.runFor(600);
  await page.getByLabel("Reorder Media server", { exact: true }).click();
  await page.getByRole("button", { name: "Move Media server up", exact: true }).click();
  await page.clock.runFor(799); expect(fixture.writes).toEqual([]);
  await page.clock.runFor(1);
  await expect.poll(() => fixture.writes).toEqual([{ MULTI_PROVIDER_METADATA_ORDER: "soundcloud,backend,deezer" }]);
  await expect(page.getByText("Order saved", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Move Media server up", exact: true })).toBeFocused();
  await expect(page.getByRole("button", { name: "Move Deezer down", exact: true })).toBeHidden();
  await capture(page, "routing-undo", width, theme);
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await page.clock.runFor(800);
  await expect.poll(() => fixture.writes.at(-1)).toEqual({ MULTI_PROVIDER_METADATA_ORDER: "deezer,soundcloud,backend" });
  await expect(page.locator(".routing-group li strong")).toHaveText(["Deezer", "SoundCloud", "Media server"]);
});

test("Source order saves drag and score changes automatically", async ({ page }) => {
  const fixture = await routing(page);
  const rows = page.locator(".routing-group li");
  await rows.nth(1).dispatchEvent("dragstart"); await rows.first().dispatchEvent("drop"); await rows.first().dispatchEvent("dragend");
  await expect(page.locator(".routing-group").getByRole("status")).toHaveText("SoundCloud moved to position 1.");
  await expect.poll(() => fixture.writes.at(-1)).toEqual({ MULTI_PROVIDER_METADATA_ORDER: "soundcloud,deezer,backend" });
  await page.getByRole("button", { name: "Sort by score", exact: true }).click();
  await expect.poll(() => fixture.writes.at(-1)).toEqual({ MULTI_PROVIDER_METADATA_ORDER: "backend,soundcloud,deezer" });
});

test("Source order reloads after a conflict without retrying stale moves", async ({ page }) => {
  const fixture = await routing(page, true);
  await page.getByLabel("Reorder SoundCloud", { exact: true }).click();
  await page.getByRole("button", { name: "Move SoundCloud up", exact: true }).click();
  await expect(page.getByText("Order changed in another session.", { exact: true })).toBeVisible();
  await expect(page.locator(".routing-group li strong")).toHaveText(["Media server", "SoundCloud", "Deezer"]);
  expect(fixture.writes).toHaveLength(1);
});

test("Source order retains moves made while a save is pending", async ({ page }) => {
  const fixture = await routing(page, false, true);
  await page.getByLabel("Reorder SoundCloud", { exact: true }).click();
  await page.getByRole("button", { name: "Move SoundCloud up", exact: true }).click();
  await expect.poll(() => fixture.writes.length).toBe(1);
  await page.getByLabel("Reorder Media server", { exact: true }).click();
  await page.getByRole("button", { name: "Move Media server up", exact: true }).click();
  fixture.release();
  await expect.poll(() => fixture.writes.at(-1)).toEqual({ MULTI_PROVIDER_METADATA_ORDER: "soundcloud,backend,deezer" });
  await expect(page.locator(".routing-group li strong")).toHaveText(["SoundCloud", "Media server", "Deezer"]);
});

test("Source order conflict waits for a pending refresh before reloading", async ({ page }) => {
  await page.addInitScript(() => {
    const json = Response.prototype.json;
    Response.prototype.json = async function() {
      try { return await json.call(this); }
      finally { if (this.status === 409) Object.assign(window, { orderConflictRead: true }); }
    };
  });
  const fixture = await routing(page, true);
  let release!: () => void, held = false;
  const pending = new Promise<void>(resolve => { release = resolve; });
  await page.route("**/api/admin/config", async route => {
    if (route.request().method() !== "GET" || held) return route.fallback();
    held = true;
    await pending;
    return route.fulfill({ json: { providers: { metadataOrder: "deezer,soundcloud,backend" } } });
  });
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect.poll(() => held).toBe(true);
  await page.getByLabel("Reorder SoundCloud", { exact: true }).click();
  await page.getByRole("button", { name: "Move SoundCloud up", exact: true }).click();
  await page.waitForFunction(() => (window as unknown as { orderConflictRead?: boolean }).orderConflictRead);
  release();
  await expect(page.locator(".routing-group li strong")).toHaveText(["Media server", "SoundCloud", "Deezer"]);
  await expect(page.getByText("Order changed in another session.", { exact: true })).toBeVisible();
  expect(fixture.writes).toHaveLength(1);
});

for (const conflict of [false, true]) test(`Personal lyrics order ${conflict ? "reloads on conflict" : "saves without submitting other preferences"}`, async ({ page }) => {
  await mockForYou(page);
  await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: { providers: [{ id: "lrclib", name: "LRCLib", categories: ["lyrics"] }] } }));
  const defaults = { explicitFilter: "All", showExternalLabel: false, showExplicitLabel: false, playbackStart: "Balanced", lyricsOrder: "backend,kept,lrclib" };
  let snapshot = { values: { ...defaults }, householdDefaults: defaults, usesHouseholdDefaults: false, householdQuality: "BestAvailable", revision: "1" };
  const writes: Record<string, unknown>[] = [];
  await page.route("**/api/admin/preferences", route => {
    if (route.request().method() === "PUT") {
      const body = route.request().postDataJSON(); writes.push(body);
      expect(body.expectedRevision).toBe(snapshot.revision);
      if (conflict) {
        snapshot = { ...snapshot, revision: "2", values: { ...defaults, lyricsOrder: "lrclib,kept,backend" } };
        return route.fulfill({ status: 409, json: { error: "Conflict" } });
      }
      snapshot = { ...snapshot, revision: String(Number(snapshot.revision) + 1), values: { ...snapshot.values, ...body } };
    }
    return route.fulfill({ json: snapshot });
  });
  await page.goto("/#/preferences");
  if (!conflict) {
    await page.getByRole("button", { name: "Explicit songs", exact: true }).click();
    await page.getByRole("option", { name: "Hide clean/edited versions", exact: true }).click();
    await expect.poll(() => writes.length).toBe(1);
    expect(writes[0]).toMatchObject({ explicitFilter: "ExplicitOnly", expectedRevision: "1" });
    expect(writes[0]).not.toHaveProperty("lyricsOrder");
  }
  await page.getByRole("button", { name: "Move LRCLib up", exact: true }).click();
  if (conflict) {
    await expect(page.locator(".degraded-banner")).toContainText("These preferences changed in another session. Reload the latest settings before continuing.");
    await page.getByRole("button", { name: "Reload latest", exact: true }).click();
    await expect(page.locator(".lyrics-order-list strong")).toHaveText(["LRCLib", "Kept LRC", "Media server lyrics"]);
  } else {
    await expect.poll(() => writes.length).toBe(2);
    expect(writes[1]).toMatchObject({ lyricsOrder: "backend,lrclib,kept", explicitFilter: "ExplicitOnly", expectedRevision: "2" });
    await expect(page.getByRole("button", { name: "Explicit songs", exact: true })).toContainText("Hide clean/edited versions");
    await page.getByRole("status").filter({ hasText: "Order saved" }).getByRole("button", { name: "Undo", exact: true }).click();
    await expect.poll(() => writes.length).toBe(3);
    expect(writes[2]).toMatchObject({ lyricsOrder: "backend,kept,lrclib", explicitFilter: "ExplicitOnly", expectedRevision: "3" });
  }
});
