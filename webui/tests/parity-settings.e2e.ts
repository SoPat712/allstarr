import { expect, test } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";
import { schema, responses, mockApi } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

test("Settings fields share columns, control baselines, and a compact action rail", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const screenshotTheme = process.env.ALLSTARR_SCREENSHOT_THEME;
  if (screenshotTheme === "light" || screenshotTheme === "dark")
    await page.addInitScript((theme) => localStorage.setItem("allstarr.theme", theme), screenshotTheme);
  await mockApi(page);
  await page.route("**/api/admin/ui/schema", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      ...schema,
      configSections: [{
        id: "general",
        label: "General",
        fields: [
          {
            key: "BACKEND",
            label: "Backend",
            type: "text",
            valuePath: "deployment.backend",
            ownership: "deployment",
            readOnly: true,
            helpText: "Edit the deployment configuration and recreate the container to apply this value.",
          },
          {
            key: "TEST_DOWNLOAD_MODE",
            label: "Download mode",
            type: "select",
            valuePath: "downloads.mode",
            options: ["Track", "Album"],
          },
          {
            key: "TEST_EXPLICIT_FILTER",
            label: "Explicit filter",
            type: "select",
            valuePath: "playback.explicitFilter",
            options: ["All", "Clean", "Explicit"],
          },
        ],
      }],
    }),
  }));
  await page.route("**/api/admin/config", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      deployment: { backend: "Jellyfin" },
      downloads: { mode: "Track" },
      playback: { explicitFilter: "All" },
    }),
  }));

  await page.goto("#/settings/advanced");
  const section = page.getByRole("form", { name: "More settings" });
  const fields = section.locator(".setting-field");
  await expect(fields).toHaveCount(3);

  const fieldBoxes = await fields.evaluateAll((items) => items.map((item) => {
    const box = item.getBoundingClientRect();
    return { x: box.x, width: box.width };
  }));
  expect(Math.max(...fieldBoxes.map((box) => box.x)) - Math.min(...fieldBoxes.map((box) => box.x))).toBeLessThanOrEqual(1);
  expect(Math.max(...fieldBoxes.map((box) => box.width)) - Math.min(...fieldBoxes.map((box) => box.width))).toBeLessThanOrEqual(1);

  const controls = section.locator(".setting-field :is(.locked-setting, .select-trigger)");
  const controlBoxes = await controls.evaluateAll((items) => items.map((item) => item.getBoundingClientRect().x));
  expect(Math.max(...controlBoxes) - Math.min(...controlBoxes)).toBeLessThanOrEqual(1);
  if (process.env.ALLSTARR_SCREENSHOT_DIR)
    await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/settings-aligned-fields.png`, fullPage: true });

  await page.setViewportSize({ width: 390, height: 844 });
  const mobileBoxes = await fields.evaluateAll((items) => items.map((item) => {
    const box = item.getBoundingClientRect();
    return { x: box.x, width: box.width };
  }));
  expect(Math.max(...mobileBoxes.map((box) => box.x)) - Math.min(...mobileBoxes.map((box) => box.x))).toBeLessThanOrEqual(1);
  expect(Math.max(...mobileBoxes.map((box) => box.width)) - Math.min(...mobileBoxes.map((box) => box.width))).toBeLessThanOrEqual(1);
  await expect(section.getByRole("button", { name: "Save changes to General" })).toHaveCount(0);
  if (process.env.ALLSTARR_SCREENSHOT_DIR)
    await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/settings-aligned-fields-mobile.png`, fullPage: true });
  await section.getByRole("button", { name: "Download mode", exact: true }).click();
  await page.getByRole("option", { name: "Album", exact: true }).click();
  await expect(page.getByText("Saved · Download mode", { exact: true })).toBeVisible();
});

test("Settings loads only the active section owners", async ({ page }) => {
  const requests: string[] = [];
  page.on("request", (request) => requests.push(new URL(request.url()).pathname));
  await mockApi(page);
  await page.goto("#/settings/general");
  await expect(page.getByRole("heading", { name: "Sound quality", level: 2 })).toBeVisible();
  expect(requests).toContain("/api/admin/config");
  expect(requests).not.toContain("/api/admin/provider-accounts");
  expect(requests).not.toContain("/api/admin/storage");
  expect(requests).not.toContain("/api/admin/cache");

  requests.length = 0;
  await page.getByRole("navigation", { name: "Settings categories" }).getByRole("link", { name: /^Storage and speed/ }).click();
  await expect(page.getByRole("heading", { name: "Storage and speed", level: 2 })).toBeVisible();
  await expect.poll(() => requests.includes("/api/admin/cache/maintenance/preview")).toBe(true);
  expect(requests).toContain("/api/admin/cache");
  await expect.poll(() => requests.includes("/api/admin/storage")).toBe(true);
  expect(requests).not.toContain("/api/admin/provider-accounts");

  requests.length = 0;
  await page.getByRole("navigation", { name: "Settings categories" }).getByRole("link", { name: /^Backups and health/ }).click();
  await expect(page.getByRole("heading", { name: "Backups and health", level: 2 })).toBeVisible();
  await expect.poll(() => requests.includes("/api/admin/storage")).toBe(true);
  expect(requests).not.toContain("/api/admin/config");
  expect(requests).not.toContain("/api/admin/cache");

  requests.length = 0;
  await page.getByRole("link", { name: /^Walk through setup again/ }).click();
  await page.getByRole("button", { name: "Review previous settings", exact: true }).click();
  await expect.poll(() => requests.includes("/api/admin/config/migration/status")).toBe(true);
});

test("Maintenance creates verified backups, lists downloads, and reports failures", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  await mockApi(page);
  const backup = {
    id: "11111111-1111-4111-8111-111111111111",
    status: "verified",
    createdAt: "2026-09-30T14:20:00Z",
    verifiedAt: "2026-09-30T14:20:01Z",
    fileName: "allstarr-backup-20260930.zip",
    bytes: 2_097_152,
    sha256: "a".repeat(64),
    schemaVersion: "1",
    applicationVersion: "1.0.0",
    includesKeyRing: true,
  };
  let created = false;
  let failCreate = false;
  await page.route("**/api/admin/storage/backups", async (route) => {
    if (route.request().method() === "POST") {
      if (failCreate) {
        await route.fulfill({ status: 503, contentType: "application/json", body: JSON.stringify({ error: "Backup creation failed." }) });
        return;
      }
      created = true;
      await route.fulfill({ status: 202, contentType: "application/json", body: JSON.stringify({
        jobId: "22222222-2222-4222-8222-222222222222", created: true, message: "Backup queued.",
      }) });
      return;
    }
    await route.continue();
  });
  await page.route("**/api/admin/storage", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      storage: { provider: "Sqlite", readiness: "Ready" },
      backups: created ? [backup] : [],
    }),
  }));

  await page.goto("#/settings/maintenance");
  const card = page.getByRole("article", { name: "Saved backups", exact: true });
  await page.getByRole("button", { name: "Back up now" }).click();
  const row = card.locator(`tr[title="${backup.fileName}"]`);
  await expect(row).toBeVisible();
  await expect(row).toContainText(new Date(backup.createdAt).toLocaleString("en-US", { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }));
  await expect(row).toContainText("Verified");
  await expect(card.getByRole("link", { name: "Download" })).toHaveAttribute(
    "href", "/api/admin/storage/backups/11111111-1111-4111-8111-111111111111/download",
  );

  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  failCreate = true;
  await page.getByRole("button", { name: "Back up now" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Backup creation failed." })).toBeVisible();
});

test("Maintenance stages a backup restore only after keyboard confirmation", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  await mockApi(page);
  let restorePending = false;
  let failRestore = true;
  let restoreRequests = 0;
  await page.route("**/api/admin/storage", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      storage: { provider: "Sqlite", readiness: "Ready" },
      backups: [],
      restorePending,
    }),
  }));
  await page.route("**/api/admin/storage/restore", async (route) => {
    restoreRequests += 1;
    expect(route.request().postData() ?? "").toContain('name="backup"');
    if (failRestore) {
      await route.fulfill({ status: 400, contentType: "application/json", body: JSON.stringify({ error: "Backup restore failed." }) });
      return;
    }
    restorePending = true;
    await route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ restartRequired: true, message: "Restart Allstarr to finish restoring." }),
    });
  });

  await page.goto("#/settings/maintenance");
  const card = page.getByRole("article", { name: "Saved backups", exact: true });
  await card.getByLabel("Backup ZIP file").setInputFiles({
    name: "restore.zip", mimeType: "application/zip", buffer: Buffer.from("fixture zip"),
  });
  const dialog = page.getByRole("dialog", { name: "Restore a backup?", exact: true });
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText("Replaced");
  await expect(dialog).toContainText("Kept as they are");
  await expect(dialog.getByRole("button", { name: "Restore on restart", exact: true })).toBeDisabled();
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden(); expect(restoreRequests).toBe(0);
  await card.getByLabel("Backup ZIP file").setInputFiles({ name: "restore.zip", mimeType: "application/zip", buffer: Buffer.from("fixture zip") });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole("checkbox", { name: "Back up the current state first", exact: true })).toBeChecked();
  await dialog.getByRole("checkbox", { name: "Back up the current state first", exact: true }).uncheck();
  await dialog.getByLabel("Type RESTORE to confirm", { exact: true }).fill("RESTORE");
  const confirm = dialog.getByRole("button", { name: "Restore on restart", exact: true });
  await confirm.focus(); await page.keyboard.press("Enter");
  await expect(dialog.getByRole("alert")).toHaveText("Backup restore failed.");
  await expect(confirm).toBeEnabled(); expect(restoreRequests).toBe(1);

  failRestore = false;
  await dialog.getByRole("button", { name: "Restore on restart" }).focus();
  await expect(dialog.getByRole("button", { name: "Restore on restart" })).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page.getByText("A restore is staged. Restart Allstarr to finish replacing saved state and credentials.")).toBeVisible();
  await expect(card.getByLabel("Backup ZIP file")).toBeDisabled();
  expect(restoreRequests).toBe(2);
  await page.reload();
  await page.goto("#/settings/maintenance");
  await expect(page.getByText("A restore is staged. Restart Allstarr to finish replacing saved state and credentials.")).toBeVisible();
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
});

test("Audio quality labels choose a step and the track follows", async ({ page }) => {
  await mockApi(page);
  await page.goto("#/settings/quality");
  const slider = page.getByRole("slider", { name: "Audio quality" });
  await page.locator(".audio-quality-steps button", { hasText: "CD lossless" }).click();
  await expect(slider).toHaveAttribute("aria-valuetext", /CD lossless/);
  await expect(page.locator(".audio-quality-steps .active")).toHaveText("CD lossless");
  await expect(page.locator(".quality-slider")).toHaveAttribute("style", /--position:\s*0\.5/);
});

test("Audio quality supports keyboard changes, provider outcomes, save, and reload", async ({ page }) => {
  await mockApi(page);
  let quality = "BestAvailable";
  let saved = "";
  await page.route("**/api/admin/config", (route) => {
    if (route.request().method() === "POST") {
      saved = route.request().postDataJSON().updates.AUDIO_QUALITY;
      quality = saved;
      return route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({ message: "Runtime configuration updated.", updatedKeys: ["AUDIO_QUALITY"] }),
      });
    }
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        ...(responses["/api/admin/config"] as Record<string, unknown>),
        audio: { quality },
      }),
    });
  });

  await page.goto("#/settings/quality");
  const slider = page.getByRole("slider", { name: "Audio quality" });
  await expect(slider).toHaveAttribute("aria-valuetext", /Best available/);
  await slider.focus();
  await page.keyboard.press("Home");
  await expect(slider).toHaveAttribute("aria-valuetext", /Data saver/);
  await page.keyboard.press("End");
  await expect(slider).toHaveAttribute("aria-valuetext", /Best available/);
  await page.keyboard.press("Home");
  await page.keyboard.press("ArrowRight");
  await expect(slider).toHaveAttribute("aria-valuetext", /High lossy/);
  await expect(page.getByText(/Smaller files and streams with high sound quality/)).toBeVisible();

  await page.getByRole("button", { name: /What each source sends/ }).click();
  const sends = page.getByRole("dialog", { name: "What each source sends" });
  await expect(sends.getByRole("listitem").filter({ hasText: "Apple Music" })).toContainText("Asks for AAC 320 kbps");
  await expect(sends.getByRole("listitem").filter({ hasText: "Deezer" })).toContainText("Asks for MP3 320 kbps");
  await sends.getByRole("button", { name: "Close", exact: true }).click();
  await page.getByText("Preference window", { exact: true }).click();
  await expect(page.getByLabel("Preference window (points)", { exact: true })).toHaveValue("7");
  await expect.poll(() => saved).toBe("High");
  await page.reload();
  await expect(page.getByRole("slider", { name: "Audio quality" }))
    .toHaveAttribute("aria-valuetext", /High lossy/);
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.locator(".quality-control")).toBeVisible();
  expect(await page.locator(".quality-control").evaluate((element) =>
    element.scrollWidth <= element.clientWidth)).toBe(true);
});

test("Settings preserves dirty drafts during live refresh and shows one page at a time on a phone", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  await page.addInitScript(() => {
    class TestEventSource {
      static instance: TestEventSource | undefined;
      listeners = new Map<string, EventListener[]>();

      constructor() {
        TestEventSource.instance = this;
      }

      addEventListener(type: string, listener: EventListener) {
        this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
      }

      close() {}
    }

    Object.defineProperty(window, "EventSource", { value: TestEventSource });
    window.__emitAllstarrUpdate = () => {
      const event = {
        data: JSON.stringify({ resource: "config", resourceId: "runtime", revision: 2 }),
        lastEventId: "settings-2",
      };
      for (const listener of TestEventSource.instance?.listeners.get("update") ?? [])
        listener(event as unknown as Event);
    };
  });
  await mockApi(page);
  let serverValue = 1;
  await page.route("**/api/admin/config", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      ...(responses["/api/admin/config"] as Record<string, unknown>),
      cache: { searchResultsMinutes: serverValue, mediaMaximumMegabytes: 512 },
    }),
  }));
  await page.goto("#/settings/storage");
  await page.getByText("Advanced: cache times, file checks and cleanup", { exact: true }).click();
  const cache = page.getByRole("form", { name: "Retention and cache settings" });
  const input = cache.getByLabel("Search results minutes");
  await input.fill("27");
  await input.evaluate((element) => element.setAttribute("data-instance", "draft"));
  await input.focus();
  serverValue = 9;
  await page.evaluate(() => window.__emitAllstarrUpdate?.());

  await expect(page.getByText("Your unsaved edits are kept.")).toBeVisible();
  await expect(input).toHaveValue("27");
  await expect(input).toHaveAttribute("data-instance", "draft");
  await expect(input).toBeFocused();
  await page.getByRole("button", { name: "Reload server values" }).click();
  await expect(input).toHaveValue("9");

  // A phone shows one page at a time: the category list, or a page with a way back to it.
  const categories = page.getByRole("navigation", { name: "Settings categories", exact: true });
  await expect(categories).toBeHidden();
  await page.getByRole("link", { name: "Settings", exact: true }).and(page.locator(".settings-back")).click();
  await expect(page).toHaveURL(/#\/settings$/);
  await expect(categories).toBeVisible();
  const rows = await categories.getByRole("link").evaluateAll((items) => items.map((item) => item.getBoundingClientRect().height));
  expect(rows.every((height) => height >= 44)).toBe(true);
  await categories.getByRole("link", { name: /^Backups and health/ }).click();
  await expect(page.getByRole("heading", { name: "Backups and health", level: 2 })).toBeVisible();
  await expect(categories).toBeHidden();
});

test("Maintenance previews, retries, and applies a legacy import on mobile", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  await page.goto("#/settings/setup");
  await page.getByRole("button", { name: "Review previous settings", exact: true }).click();
  const card = page.locator(".maintenance-card").filter({ hasText: "Import from Allstarr v2" });
  const legacyFile = {
    name: "allstarr.env",
    mimeType: "text/plain",
    buffer: Buffer.from("CACHE_LYRICS_DAYS=21"),
  };

  await card.getByLabel("Legacy environment file").setInputFiles(legacyFile);
  await card.getByRole("button", { name: "Preview import" }).click();
  await expect(card.getByText("Imported accounts remain disabled until reviewed.")).toBeVisible();
  await card.getByText("Review 1 parsed setting", { exact: true }).click();
  await expect(card.getByText("CACHE_LYRICS_DAYS", { exact: true }).last()).toBeVisible();
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  await card.getByRole("button", { name: "Discard and retry" }).click();
  await expect(card.getByRole("button", { name: "Preview import" })).toBeVisible();

  await card.getByLabel("Legacy environment file").setInputFiles(legacyFile);
  await card.getByRole("button", { name: "Preview import" }).click();
  await card.getByRole("checkbox").check();
  await card.getByRole("button", { name: "Import preview" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Legacy settings imported." })).toBeVisible();
});

test("Maintenance reports cache budgets and confirms category purge", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const requests: string[] = [];
  page.on("request", (request) => requests.push(new URL(request.url()).pathname));
  await mockApi(page);
  await page.goto("#/settings/storage");
  await page.getByText("Advanced: cache times, file checks and cleanup", { exact: true }).click();
  await expect(page.getByRole("form", { name: "Retention and cache settings" })).toBeVisible();
  const card = page.locator(".cache-diagnostics-card");

  await expect(card.getByText("Memory hit / miss")).toBeVisible();
  await expect(card.getByText("Downloads avoided")).toBeVisible();
  await card.getByText("Limits and cleanup preview").click();
  await expect(card.getByText("Stale account scopes")).toBeVisible();
  await card.getByText("Category budgets").click();
  await expect(card.getByText(/media-assets/)).toBeVisible();
  await card.getByRole("button", { name: "Purge", exact: true }).click();
  const dialog = page.getByRole("alertdialog", { name: "Remove cached files?" });
  await expect(dialog).toBeVisible();
  await dialog.getByRole("button", { name: "Remove cache" }).click();
  await expect.poll(() => requests).toContain("/api/admin/cache/categories/Artwork");
  await expect(dialog).toBeHidden();
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
});

test("Administrators can reopen durable setup from Maintenance", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  await mockApi(page);
  await page.goto("#/settings/maintenance");
  await page.getByRole("link", { name: /^Walk through setup again/ }).click();
  const setup = page.getByRole("region", { name: "Set up Allstarr" });
  await expect(setup).toBeVisible();
  await setup.getByRole("button", { name: "Open", exact: true }).click();
  await expect(setup.getByRole("heading", { name: "Review previous settings", exact: true })).toBeVisible();
  await setup.getByRole("button", { name: "Back to all steps", exact: true }).click();
  await expect(setup.getByRole("list", { name: "Setup steps", exact: true })).toBeVisible();
  await setup.getByRole("button", { name: "Finish setup" }).scrollIntoViewIfNeeded();
  await expect(setup.getByRole("button", { name: "Finish setup" })).toBeInViewport();
  await page.keyboard.press("Escape");
  await expect(setup).toBeVisible();
  await setup.getByRole("button", { name: "Finish setup", exact: true }).click();
  await expect(page).toHaveURL(/#\/$/);
  await expect(setup).toBeHidden();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test("Quality and routing lives in Settings, not Sources", async ({ page }) => {
  await mockApi(page);
  await page.goto("#/integrations/routing");
  await expect(page.getByRole("heading", { name: "Music sources", level: 2 })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Settings", level: 1 })).toBeVisible();
  await page.goto("#/settings/quality");
  await expect(page.getByRole("slider", { name: "Audio quality" })).toBeVisible();
  await page.goto("#/integrations/services");
  await expect(page.getByRole("tab", { name: /routing/i })).toHaveCount(0);
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Metadata search priority stays editable at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    const description = "Orders sources among similarly relevant search results. Title, artist, and requested versions determine relevance. Playback uses Streaming and Download order below.";
    await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: { ...schema,
      providers: [...schema.providers, { id: "deezer", name: "Deezer" }, { id: "soundcloud", name: "SoundCloud" }],
      priorityGroups: [{ id: "metadata", label: "Metadata search order", description,
        envKey: "MULTI_PROVIDER_METADATA_ORDER", providers: ["deezer", "soundcloud"] }],
    } }));
    let order = "deezer,soundcloud";
    const writes: Record<string, string>[] = [];
    await page.route("**/api/admin/config", route => {
      if (route.request().method() === "POST") {
        const updates = route.request().postDataJSON().updates;
        writes.push(updates);
        order = updates.MULTI_PROVIDER_METADATA_ORDER;
        return route.fulfill({ json: { message: "Saved", updatedKeys: Object.keys(updates) } });
      }
      return route.fulfill({ json: { ...(responses["/api/admin/config"] as object), providers: { metadataOrder: order } } });
    });
    await page.goto("#/integrations/routing");
    const group = page.locator(".routing-group");
    await expect(group.getByRole("heading", { name: "Search source order", exact: true })).toBeVisible();
    await expect(page.locator(".settings-page-head").getByText("Everyone", { exact: true })).toBeVisible();
    await expect(group.getByText("Drag to reorder. Saves as you drop.", { exact: true })).toBeVisible();
    await expect(group.getByRole("listitem").first()).toContainText("Deezer");
    await group.locator('summary[aria-label="Reorder SoundCloud"]').click();
    await group.getByRole("button", { name: "Move SoundCloud up", exact: true }).click();
    await expect.poll(() => writes.at(-1)?.MULTI_PROVIDER_METADATA_ORDER).toBe("soundcloud,deezer");
    await expect(group.getByRole("listitem").first()).toContainText("SoundCloud");
    await page.screenshot({ path: `test-results/search-order-${width}-${theme}.png`, fullPage: true });
  });
}

test("Cross-service links explain built-in lookups and preserve the optional masked key", async ({ page }) => {
  await page.route("**/*", route => new URL(route.request().url()).hostname === "127.0.0.1" ? route.continue() : route.abort());
  await mockApi(page);
  const writes: Record<string, string>[] = [];
  await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
    ...schema,
    configSections: [{ id: "operator", label: "Service settings", fields: [
      { key: "ODESLI_ENABLED", label: "Enable cross-service song links", type: "toggle", valuePath: "operatorSettings.Odesli:Enabled" },
      { key: "ODESLI_RATE_LIMIT_MS", label: "Optional song.link request interval (ms)", type: "number", valuePath: "operatorSettings.Odesli:RateLimitMs" },
      { key: "ODESLI_API_KEY", label: "Optional song.link API key", type: "password", sensitive: true, requiresRestart: true, valuePath: "operatorSettings.Odesli:ApiKey" },
    ] }],
  } }));
  await page.route("**/api/admin/config", route => {
    if (route.request().method() === "POST") {
      writes.push(route.request().postDataJSON().updates);
      return route.fulfill({ json: { requiresRestart: false } });
    }
    return route.fulfill({ json: { ...(responses["/api/admin/config"] as Record<string, unknown>), operatorSettings: {
      "Odesli:Enabled": true, "Odesli:RateLimitMs": 6000, "Odesli:ApiKey": "********",
    } } });
  });
  await page.goto("#/settings/advanced");
  const card = page.getByRole("form", { name: "song.link settings" });
  await expect(card.getByText("Find links to the same song across your enabled services.", { exact: true })).toBeVisible();
  await expect(card.getByText("Optional. With a key, the official API is tried first. Leave unchanged to keep it.")).toBeVisible();
  const key = card.getByLabel("song.link API key", { exact: true });
  await expect(key).toHaveCount(0);
  await card.getByRole("button", { name: "Change key…", exact: true }).click();
  await expect(key).toHaveAttribute("type", "password");
  await expect(key).toHaveValue("");
  await expect(key).toHaveAttribute("placeholder", "Leave unchanged to keep the saved key");
  expect(writes).toEqual([]);
  await key.fill("replacement-fixture-key"); await key.blur();
  await expect(page.getByRole("alertdialog")).toBeVisible();
  expect(writes).toEqual([]);
  await page.getByRole("alertdialog").getByRole("button", { name: "Restart and apply", exact: true }).click();
  await expect.poll(() => writes[0]?.ODESLI_API_KEY).toBe("replacement-fixture-key");
  await expect(page.getByRole("alertdialog")).toBeHidden();
  await expect(key).toHaveValue("");
  await card.getByRole("button", { name: "Remove key…", exact: true }).click();
  await expect(page.getByRole("alertdialog")).toContainText("Remove the key and restart Allstarr?");
  expect(writes).toHaveLength(1);
  await page.getByRole("alertdialog").getByRole("button", { name: "Remove key and restart", exact: true }).click();
  await expect.poll(() => writes[1]?.ODESLI_API_KEY).toBe("");
});

for (const width of [390, 1280]) for (const scheme of ["light", "dark"] as const) {
  test(`Challenge solver network setting tests the current address at ${width}px ${scheme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), scheme);
    await page.route("**/*", route => new URL(route.request().url()).hostname === "127.0.0.1" ? route.continue() : route.abort());
    await mockApi(page);
    await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
      ...schema,
      configSections: [{ id: "operator", label: "Service settings", fields: [
        { key: "CHALLENGE_SOLVER_URL", label: "Challenge solver URL", type: "text", valuePath: "operatorSettings.ChallengeSolver:Url",
          placeholder: "http://flaresolverr:8191", helpText: "Optional. Your FlareSolverr or Byparr address, for example http://flaresolverr:8191. Used only for sources that sit behind Cloudflare checks." },
        { key: "CHALLENGE_SOLVER_TIMEOUT_SECONDS", label: "Challenge solver timeout (seconds)", type: "number", valuePath: "operatorSettings.ChallengeSolver:TimeoutSeconds" },
      ] }],
    } }));
    let solverUrl = "http://saved-solver:8191";
    await page.route("**/api/admin/config", route => {
      if (route.request().method() === "POST") { solverUrl = route.request().postDataJSON().updates.CHALLENGE_SOLVER_URL ?? solverUrl; return route.fulfill({ json: { requiresRestart: false } }); }
      return route.fulfill({ json: { ...(responses["/api/admin/config"] as Record<string, unknown>), operatorSettings: { "ChallengeSolver:Url": solverUrl, "ChallengeSolver:TimeoutSeconds": 60 } } });
    });
    const requests: unknown[] = [];
    await page.route("**/api/admin/challenge-solver/test", route => {
      expect(route.request().method()).toBe("POST");
      requests.push(route.request().postDataJSON());
      return route.fulfill({ json: { ok: true, message: "Solver reachable", version: "unknown" } });
    });
    await page.goto("#/settings/people");
    await expect(page.getByRole("heading", { name: "People and sign-in", level: 2 })).toBeVisible();
    await expect(page.getByLabel(/^Challenge solver URL/)).toHaveCount(0);
    await page.goto("#/settings/advanced");
    await expect(page.getByRole("heading", { name: "Cloudflare checks", exact: true })).toBeVisible();
    const url = page.getByRole("textbox", { name: /^Challenge solver URL/ });
    await expect(url).toHaveValue("http://saved-solver:8191");
    await expect(url).toHaveAttribute("placeholder", "http://flaresolverr:8191");
    await expect(page.getByLabel(/^Challenge solver timeout/)).toHaveValue("60");
    await url.fill("http://flaresolverr:8191");
    await page.getByRole("button", { name: "Test", exact: true }).click();
    await expect(page.getByRole("status").filter({ hasText: "Solver reachable" })).toBeVisible();
    expect(requests).toEqual([{ url: "http://flaresolverr:8191", timeoutSeconds: 60 }]);
    await expect(url).toHaveValue("http://flaresolverr:8191");
    await expect(page.getByRole("button", { name: /Save changes/ })).toHaveCount(0);
    await expectContainedMobileGeometry(page);
    await page.screenshot({ path: `test-results/solver-${width}-${scheme}.png`, fullPage: true });
  });
}
