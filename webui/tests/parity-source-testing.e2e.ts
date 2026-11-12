import { expect, test } from "@playwright/test";
import { openSourceSettings, schema, mockApi, tryCatalog, tryTracks, mockTry } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

test("Playback diagnostic errors stay readable and persistent", async ({ page }) => {
  await mockApi(page); await page.clock.install();
  await page.route("**/api/admin/provider-diagnostics/deep-stream", route => route.fulfill({ status: 422, json: {
    stage: "first-byte", failureCode: "endpoint-unreachable", error: "raw-private-provider-payload",
  } }));
  await page.goto("#/integrations/services/lumen-audio");
    await page.locator(".source-more > summary").click();
  await page.getByRole("button", { name: "Measure start time", exact: true }).click();
  const notification = page.getByRole("alert").filter({ hasText: "audio" });
  await expect(notification).toBeVisible(); await expect(page.getByText("raw-private-provider-payload")).toHaveCount(0);
  await page.clock.runFor(10000); await expect(notification).toBeVisible();
  const dismiss = notification.getByRole("button", { name: "Dismiss notification" });
  await dismiss.focus(); await expect(dismiss).toBeFocused();
  await page.keyboard.press("Escape"); await expect(notification).toHaveCount(0);
  await expect(page.getByRole("dialog", { name: "Lumen Audio", exact: true })).toBeVisible();
});

test("A running source test shows progress until its result arrives", async ({ page }) => {
  await mockApi(page);
  let finish!: () => void;
  const answered = new Promise<void>(resolve => { finish = resolve; });
  await page.route("**/api/admin/providers/test/lumen-audio/metadata?**", async route => {
    await answered;
    await route.fulfill({ json: { success: false, healthy: false, reasonCode: "timeout" } });
  });
  await page.goto("#/integrations/accounts");
  await page.getByRole("link", { name: "Open Lumen Audio account" }).click();
  const row = page.locator(".service-account.focused .service-check").filter({ hasText: "Metadata" });
  await row.getByRole("button", { name: "Test", exact: true }).click();
  const region = page.getByRole("region", { name: "Notifications" });
  await expect(row.getByRole("button", { name: "Testing…" })).toBeDisabled();
  await expect(region.getByRole("status")).toContainText("Testing Lumen Audio Metadata…");
  await expect(region.locator(".toast-busy")).toHaveCount(1);
  finish();
  await expect(region.getByRole("alert")).toContainText("Lumen Audio Metadata needs attention.");
  await expect(region.getByRole("status")).toHaveCount(0);
  await expect(row.getByRole("button", { name: "Test", exact: true })).toBeEnabled();
});

for (const providerId of ["artistgrid", "lumen-audio"]) {
  test(`source test titles gate best-version markers by source for ${providerId}`, async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 844 });
    await mockApi(page);
    const name = providerId === "artistgrid" ? "ArtistGrid" : "Lumen Audio";
    await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
      ...schema, providers: [{ id: providerId, name, connectionKind: "built_in", categories: ["metadata"],
        runtimeCapabilities: [{ id: "metadata", supported: true, canTest: true, status: "ready" }] }],
    } }));
    await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: { accounts: [], audienceUsers: [] } }));
    await page.route(`**/api/admin/providers/${providerId}/try**`, route => {
      if (route.request().method() === "GET") return route.fulfill({ json: {
        provider: providerId, capabilities: [{ capability: "metadata", accountRequirement: "none",
          operations: [{ id: "search", inputs: ["query", "kind"], kinds: ["tracks"] }] }],
      } });
      return route.fulfill({ json: { provider: providerId, capability: "metadata", operation: "search", succeeded: true, elapsedMs: 20,
        tracks: [
          { ...tryTracks[0], id: "best", title: "Preferred version", isBestVersion: true },
          { ...tryTracks[0], id: "ordinary", title: "Ordinary version", isBestVersion: false },
          { ...tryTracks[0], id: "legacy", title: "Older result" },
        ],
      } });
    });
    await page.goto(`#/integrations/services/${providerId}`);
    await openSourceSettings(page, name);
    await page.getByRole("button", { name: "Open Metadata tests" }).click();
    const dialog = page.getByRole("dialog", { name: `Test ${name}` });
    await dialog.getByRole("searchbox", { name: "Search" }).fill("version");
    await dialog.getByRole("button", { name: "Search", exact: true }).click();
    const rows = dialog.getByRole("list", { name: "Results" }).getByRole("listitem");
    await expect(rows).toHaveCount(3);
    const marker = rows.getByRole("img", { name: "Best version on ArtistGrid", exact: true });
    await expect(marker).toHaveCount(providerId === "artistgrid" ? 1 : 0);
    if (providerId === "artistgrid") {
      await expect(marker).toHaveAttribute("title", "Best version on ArtistGrid");
      await expect(rows.filter({ hasText: "Preferred version" }).locator("strong")).toContainText("⭐");
    }
    expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  });
}

test("Source test panel Find chips support keyboard selection and one tab stop", async ({ page }) => {
  await mockApi(page);
  const requests: Record<string, unknown>[] = [];
  await mockTry(page, requests);
  await page.goto("#/integrations/accounts");
  await page.getByRole("link", { name: "Open Lumen Audio account" }).click();
  await page.locator(".service-account.focused .service-check").filter({ hasText: "Metadata" })
    .getByRole("button", { name: "Open Metadata tests" }).click();
  const dialog = page.getByRole("dialog", { name: "Test Lumen Audio" });
  const group = dialog.getByRole("radiogroup", { name: "Find" });
  const songs = group.getByRole("radio", { name: "Songs", exact: true });
  await expect(songs).toHaveAttribute("tabindex", "0");
  await expect(group.locator('[role="radio"][tabindex="0"]')).toHaveCount(1);
  await songs.focus();
  for (const [key, label] of [
    ["ArrowRight", "Albums"], ["ArrowDown", "Artists"], ["ArrowLeft", "Albums"],
    ["ArrowUp", "Songs"], ["ArrowUp", "ISRC"], ["ArrowDown", "Songs"],
    ["End", "ISRC"], ["Home", "Songs"], ["End", "ISRC"], ["ArrowLeft", "Song ID"],
  ]) {
    await page.keyboard.press(key);
    const selected = group.getByRole("radio", { name: label, exact: true });
    await expect(selected).toBeFocused();
    await expect(selected).toHaveAttribute("aria-checked", "true");
    await expect(selected).toHaveAttribute("tabindex", "0");
    await expect(group.locator('[role="radio"][tabindex="0"]')).toHaveCount(1);
    await expect(group.locator('[role="radio"][aria-checked="true"]')).toHaveCount(1);
  }
  await dialog.getByRole("searchbox", { name: "Lumen Audio song ID" }).fill("fixture-track");
  await dialog.getByRole("button", { name: "Look up", exact: true }).click();
  await expect.poll(() => requests.at(-1)).toMatchObject({ operation: "track", id: "fixture-track" });
});

for (const choice of ["track", "isrc", "albums"]) {
  test(`Source test panel selects its only Find operation: ${choice}`, async ({ page }) => {
    await mockApi(page);
    const requests: Record<string, unknown>[] = [];
    await mockTry(page, requests);
    await page.route("**/api/admin/providers/lumen-audio/try**", route => {
      if (route.request().method() !== "GET") return route.fallback();
      return route.fulfill({ json: { ...tryCatalog, capabilities: [{ capability: "metadata", accountRequirement: "required",
        operations: choice === "albums" ? [{ id: "search", kinds: ["albums"], inputs: ["query", "kind"] }] : [{ id: choice, inputs: [choice === "track" ? "id" : "isrc"] }],
      }] } });
    });
    await page.goto("#/integrations/accounts");
    await page.getByRole("link", { name: "Open Lumen Audio account" }).click();
    await page.locator(".service-account.focused .service-check").filter({ hasText: "Metadata" })
      .getByRole("button", { name: "Open Metadata tests" }).click();
    const dialog = page.getByRole("dialog", { name: "Test Lumen Audio" });
    await expect(dialog.getByRole("radiogroup", { name: "Find" })).toHaveCount(0);
    await dialog.getByRole("searchbox").fill("fixture");
    await dialog.getByRole("searchbox").press("Enter");
    await expect.poll(() => requests.at(-1)).toMatchObject(choice === "albums"
      ? { operation: "search", query: "fixture", kind: "albums" }
      : choice === "track" ? { operation: "track", id: "fixture" } : { operation: "isrc", isrc: "FIXTURE" });
  });
}

test("Source test panel searches one source and sends a result to lyrics, playback and download", async ({ page }) => {
  await mockApi(page);
  const requests: Record<string, unknown>[] = [];
  await mockTry(page, requests);
  await page.goto("#/integrations/accounts");
  await page.getByRole("link", { name: "Open Lumen Audio account" }).click();
  const row = page.locator(".service-account.focused .service-check").filter({ hasText: "Metadata" });
  await expect(row.getByRole("button", { name: "Test", exact: true })).toBeVisible();
  await row.getByRole("button", { name: "Open Metadata tests" }).click();

  const dialog = page.getByRole("dialog", { name: "Test Lumen Audio" });
  await expect(dialog).toContainText("Uses Lumen account.");
  await expect(dialog.getByRole("tab", { name: "Search" })).toHaveAttribute("aria-selected", "true");
  await dialog.getByRole("searchbox", { name: "Search" }).fill("get lucky");
  await dialog.getByRole("button", { name: "Search", exact: true }).click();
  await expect(dialog.getByRole("status")).toContainText("2 songs found in 840 ms");
  await expect(dialog.getByRole("list", { name: "Results" }).getByRole("listitem")).toHaveCount(2);
  expect(requests.at(-1)).toMatchObject({ capability: "metadata", operation: "search", query: "get lucky", kind: "tracks", accountId: "account" });

  await dialog.getByRole("button", { name: "Get lyrics for Get Lucky (feat. Pharrell Williams and Nile Rodgers)" }).click();
  await expect(dialog.getByRole("tab", { name: "Lyrics" })).toHaveAttribute("aria-selected", "true");
  await expect(dialog.getByRole("searchbox", { name: "Song", exact: true })).toHaveValue("Get Lucky (feat. Pharrell Williams and Nile Rodgers)");
  await expect(dialog.getByText("Word-timed")).toBeVisible();
  await expect(dialog.locator(".try-lyric-lines time").first()).toHaveText("0:31.40");
  expect(requests.at(-1)).toMatchObject({ capability: "lyrics", trackId: "lumen-71230", isrc: "USQX91300108", durationSeconds: 369, accountId: null });

  await dialog.getByRole("tab", { name: "Search" }).click();
  await dialog.getByRole("button", { name: "Play Get Lucky (Radio Edit)" }).click();
  await expect(dialog.getByRole("tab", { name: "Playback" })).toHaveAttribute("aria-selected", "true");
  await expect(dialog.locator("audio")).toHaveAttribute("src", /try\/media\/stream-ticket$/);
  await expect(dialog.locator(".try-media-facts")).toContainText("Byte range");
  expect(requests.at(-1)).toMatchObject({ capability: "streaming", operation: "stream", trackId: "lumen-71231", quality: "lossless" });

  await dialog.getByRole("tab", { name: "Download" }).click();
  await dialog.getByLabel("Song").fill("get lucky");
  await dialog.getByRole("button", { name: "Find" }).click();
  await dialog.getByRole("button", { name: "Download Get Lucky (feat. Pharrell Williams and Nile Rodgers)" }).click();
  await expect(dialog.getByRole("link", { name: "Save file" })).toHaveAttribute("href", /download-ticket\?download=1$/);
  await expect(dialog.locator(".try-media-facts")).toContainText("24-bit / 96 kHz");
  await expect(dialog.locator(".try-media-facts")).toContainText("44.0 MB");
});

test("Sources without an account can be tested in detail", async ({ page }) => {
  await mockApi(page);
  await page.route("**/api/admin/ui/schema", (route) => route.fulfill({ json: {
    ...schema, providers: [...schema.providers, { id: "lrclib", name: "LRCLIB", categories: ["lyrics"], runtimeCapabilities: [
      { id: "lyrics", supported: true, health: "healthy", ready: true, canAttempt: true, canTest: true, configuration: "not-required" },
    ] }],
  } }));
  let tested = 0;
  await page.route("**/api/admin/providers/test/lrclib/lyrics", (route) => { tested++; return route.fulfill({ json: { success: true, healthy: true, latencyMs: 120, bars: 4 } }); });
  await page.route("**/api/admin/providers/lrclib/try**", (route) => route.fulfill({ json: { provider: "lrclib", capabilities: [
    { capability: "lyrics", accountRequirement: "none", operations: [{ id: "lyrics", inputs: ["title", "artist"] }] },
  ] } }));
  await page.goto("#/integrations/services/lrclib");
  await openSourceSettings(page, "LRCLIB");
  const row = page.locator(".service-check").filter({ hasText: "Lyrics" });
  await row.getByRole("button", { name: "Test", exact: true }).click();
  await expect.poll(() => tested).toBe(1);
  await expect(page.getByRole("status").filter({ hasText: "LRCLIB Lyrics passed." })).toBeVisible();
  await row.getByRole("button", { name: "Open Lyrics tests" }).click({ trial: true, timeout: 5000 });
  await row.getByRole("button", { name: "Open Lyrics tests" }).click();
  const dialog = page.getByRole("dialog", { name: "Test LRCLIB" });
  await expect(dialog).toContainText("Runs on LRCLIB only");
  await expect(dialog.getByRole("tablist")).toHaveCount(0);
  await expect(dialog.getByRole("button", { name: "Use what I typed" })).toBeDisabled();
});

test("Lyrics-only source checks show lyrics outcomes and no playback controls", async ({ page }) => {
  await mockApi(page);
  await page.route("**/api/admin/ui/schema", (route) => route.fulfill({ json: {
    ...schema, providers: [...schema.providers, { id: "paxsenix", name: "Paxsenix", categories: ["lyrics"], runtimeCapabilities: [
      { id: "lyrics", supported: true, health: "unknown", ready: false, canAttempt: true, canTest: true, configuration: "not_required" },
    ] }],
  } }));
  let healthy = false;
  let unavailable = false;
  const requests: string[] = [];
  await page.route("**/api/admin/providers/test/paxsenix/**", route => {
    requests.push(new URL(route.request().url()).pathname);
    if (unavailable) return route.fulfill({ status: 503, json: { error: "unavailable" } });
    return route.fulfill({ json: { success: healthy, health: healthy ? "healthy" : "degraded", metric: "api-latency",
      capability: "lyrics", reasonCode: healthy ? null : "lyrics-upstream-busy-or-timed-out" } });
  });
  await page.route("**/api/admin/providers/paxsenix/try**", route => route.fulfill({ json: { provider: "paxsenix", capabilities: [
    { capability: "lyrics", accountRequirement: "none", operations: [{ id: "lyrics", inputs: ["title", "artist"] }] },
  ] } }));
  await page.goto("#/integrations/services/paxsenix");
  await openSourceSettings(page, "Paxsenix");
  const row = page.locator(".service-check").filter({ hasText: "Lyrics" });
  await row.getByRole("button", { name: "Test", exact: true }).click();
  const failure = page.getByRole("alert").filter({ hasText: "Paxsenix Lyrics needs attention." });
  await expect(failure).toContainText("The lyrics upstream is busy or timed out.");
  await expect(failure).not.toContainText("Playback");
  await failure.getByRole("button", { name: "Dismiss notification" }).click();
  healthy = true;
  await expect(page.locator(".source-more")).toHaveAttribute("open", "");
  await row.getByRole("button", { name: "Test", exact: true }).click();
  await expect(page.getByRole("status").filter({ hasText: "Paxsenix Lyrics passed." })).toBeVisible();
  await row.getByRole("button", { name: "Test", exact: true }).click({ trial: true, timeout: 5000 });
  unavailable = true;
  await row.getByRole("button", { name: "Test", exact: true }).click();
  const error = page.getByRole("alert").filter({ hasText: "The lyrics check failed." });
  await expect(error).toBeVisible();
  await expect(error).not.toContainText("Playback");
  await expect(page.locator(".toaster [role=alert]")).toHaveCount(1);
  expect(requests).toEqual(["/api/admin/providers/test/paxsenix/lyrics", "/api/admin/providers/test/paxsenix/lyrics", "/api/admin/providers/test/paxsenix/lyrics"]);
  await error.getByRole("button", { name: "Dismiss notification" }).click();
  await row.getByRole("button", { name: "Open Lyrics tests" }).click();
  const dialog = page.getByRole("dialog", { name: "Test Paxsenix" });
  await expect(dialog.getByRole("button", { name: "Use what I typed" })).toBeDisabled();
  await expect(dialog.getByRole("tab", { name: /stream|download/i })).toHaveCount(0);
  await expect(dialog.locator("audio")).toHaveCount(0);
  await expect(page.locator(".toaster [role=alert]")).toHaveCount(0);
});

for (const width of [390, 1280]) {
  for (const colorScheme of ["light", "dark"] as const) {
    test(`hires source download quality fact at ${width}px in ${colorScheme} mode`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
      await mockApi(page);
      await mockTry(page);
      let verdict = "upsampled";
      let likelySourceRate = 44100;
      await page.route("**/api/admin/providers/lumen-audio/try**", async (route) => {
        if (route.request().method() === "GET") return route.fallback();
        const body = route.request().postDataJSON();
        if (body.operation !== "download") return route.fallback();
        return route.fulfill({ json: { succeeded: true, capability: "download", operation: "download", elapsedMs: 200,
          media: { kind: "download", url: "/api/admin/providers/try/media/download-ticket", fileName: "Fixture.flac", codec: "flac",
            sampleRateHz: 96000, bitDepth: 24, sizeBytes: 46137344,
            qualityCheck: { claimedSampleRate: 96000, claimedBitDepth: 24, effectiveBandwidthHz: 20000, verdict, confidence: 0.8, likelySourceRate } } } });
      });
      await page.goto("#/integrations/accounts");
      await page.getByRole("link", { name: "Open Lumen Audio account" }).click();
      await page.locator(".service-account.focused .service-check").filter({ hasText: "Metadata" })
        .getByRole("button", { name: "Open Metadata tests" }).click();
      const dialog = page.getByRole("dialog", { name: "Test Lumen Audio" });
      await dialog.getByRole("tab", { name: "Download" }).click();
      await dialog.getByLabel("Song").fill("get lucky");
      await dialog.getByRole("button", { name: "Find" }).click();
      const download = dialog.getByRole("button", { name: "Download Get Lucky (feat. Pharrell Williams and Nile Rodgers)" });
      const fact = dialog.locator(".try-media-facts > div").filter({ has: page.locator("dt", { hasText: /^Quality check$/ }) });
      for (const [next, rate, label] of [
        ["upsampled", 44100, "Upsampled from 44.1 kHz"], ["upsampled", 48000, "Upsampled from 48 kHz"],
        ["true-hi-res", 0, "True hi-res"], ["padded-bit-depth", 0, "16-bit padded to 24-bit"],
        ["lossy-transcode-suspected", 0, "Lossy source suspected"], ["standard", 0, "CD quality"], ["unknown", 0, "Not checked"],
      ] as const) {
        verdict = next;
        likelySourceRate = rate;
        await download.click();
        await expect(fact.locator("dd")).toHaveText(label);
      }
      verdict = "upsampled";
      likelySourceRate = 44100;
      await download.click();
      await expect(fact.locator("dd")).toHaveText("Upsampled from 44.1 kHz");
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      await fact.scrollIntoViewIfNeeded();
      await page.screenshot({ path: `test-results/hires-source-${width}-${colorScheme}.png`, fullPage: false });
    });
  }
}
