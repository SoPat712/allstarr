import { expect, test } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";
import { clickWhenStable, openSourceSettings, schema, responses, mockApi, expectRecordingLyricsGeometry, lyricsRecordingId, mockRecordingLyrics, tryCatalog, tryTracks, mockTry, openLyricsTry, spicyDefinition, spicyCredits, spicyTrack, spicyUnlinkedMaker, mockSpicySource } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Lyrics services preserve multiline Musixmatch credentials at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
      ...schema, providers: [...schema.providers, {
        id: "lyricsplus", name: "LyricsPlus", categories: ["lyrics"], connectionKind: "operator_managed",
        description: "Public instances with fallback or your self-hosted service.",
        configSchema: [{ key: "LYRICSPLUS_URL", label: "Optional self-hosted URL", type: "url", valuePath: "lyricsPlus.baseUrl",
          helpText: "Leave blank to use public instances with fallback. A configured service is used exclusively." }],
      }, {
        id: "musixmatch", name: "Musixmatch", categories: ["lyrics"],
        accountSettings: [{ key: "userToken", label: "Debug Info or user token", type: "password", sensitive: true, multiline: true,
          helpText: "Paste the Debug Info from the Musixmatch app, or just the user token. Optional: Musixmatch works without it." }],
      }],
    } }));
    let endpoint = "";
    let saved: Record<string, string> | undefined;
    await page.route("**/api/admin/config", route => {
      if (route.request().method() === "POST") {
        saved = route.request().postDataJSON().updates;
        endpoint = saved?.LYRICSPLUS_URL ?? "";
        return route.fulfill({ json: { message: "Runtime configuration updated." } });
      }
      return route.fulfill({ json: { ...(responses["/api/admin/config"] as object), lyricsPlus: { baseUrl: endpoint } } });
    });
    await page.goto("#/integrations/services/lyricsplus");
    await expect(page.getByRole("heading", { name: "LyricsPlus", exact: true })).toBeVisible();
    const url = page.locator('input[name="LYRICSPLUS_URL"]');
    await expect(url).toHaveValue("");
    await expect(page.getByText("Leave blank to use public instances with fallback. A configured service is used exclusively.")).toBeVisible();
    await url.fill("http://lyrics:3000/prefix");
    await url.press("Tab");
    await expect.poll(() => saved).toEqual({ LYRICSPLUS_URL: "http://lyrics:3000/prefix" });
    await page.getByRole("dialog", { name: "LyricsPlus", exact: true }).evaluate(async panel => {
      await Promise.all(panel.getAnimations().map(animation => animation.finished));
    });
    await expectContainedMobileGeometry(page);
    await page.goto("#/integrations/services/musixmatch");
    await openSourceSettings(page, "Musixmatch");
    await page.getByRole("button", { name: "Connect account", exact: true }).click();
    const dialog = page.locator(".connect-source-dialog");
    const token = dialog.locator('textarea[name="userToken"]');
    await expect(token).toHaveAttribute("rows", "6");
    await expect(token).toHaveAttribute("autocomplete", "off");
    await expect(token).toHaveAttribute("autocapitalize", "off");
    await expect(token).toHaveAttribute("spellcheck", "false");
    await expect(token).toHaveAttribute("wrap", "off");
    await expect(token).toHaveAttribute("placeholder", "Paste the Debug Info here");
    await expect(token).not.toHaveAttribute("required");
    await expect(dialog.getByText("Paste the Debug Info from the Musixmatch app, or just the user token. Optional: Musixmatch works without it.")).toBeVisible();
    const debugInfo = "[DeviceId]: fixture-device\n[UserToken]: fixture-user-token\n[AppBuild]: 2025120901";
    await token.focus();
    await page.keyboard.insertText(debugInfo);
    await expect(token).toHaveValue(debugInfo);
    await expectContainedMobileGeometry(page);
    const style = await token.evaluate(element => {
      const css = getComputedStyle(element);
      return { resize: css.resize, minHeight: css.minHeight, maxHeight: css.maxHeight, fontSize: css.fontSize, lineHeight: css.lineHeight,
        width: element.getBoundingClientRect().width, columnWidth: element.parentElement!.getBoundingClientRect().width };
    });
    expect(style).toMatchObject({ resize: "vertical", minHeight: "144px", maxHeight: "288px", fontSize: "14px", lineHeight: "19.6px" });
    expect(Math.abs(style.width - style.columnWidth)).toBeLessThanOrEqual(1);
    await page.screenshot({ path: `test-results/mxm-field-${width}-${theme}.png`, fullPage: true });
    const fixture = responses["/api/admin/provider-accounts"] as { accounts: Record<string, unknown>[] };
    const account = { ...fixture.accounts[0], id: "musixmatch-account", providerId: "musixmatch", displayName: "Musixmatch",
      configuration: {}, configuredFields: ["userToken"], secret: { configured: true, revoked: false } };
    let created: Record<string, unknown> | undefined;
    let replacement: Record<string, unknown> | undefined;
    await page.route("**/api/admin/provider-accounts", route => {
      if (route.request().method() === "POST") {
        created = route.request().postDataJSON();
        return route.fulfill({ json: account });
      }
      return route.fulfill({ json: { ...fixture, accounts: [account] } });
    });
    await page.route("**/api/admin/provider-accounts/musixmatch-account/test", route => route.fulfill({ json: {} }));
    await page.route("**/api/admin/provider-accounts/musixmatch-account/secret", route => {
      replacement = route.request().postDataJSON();
      return route.fulfill({ json: { accountId: account.id, revision: 2 } });
    });
    await dialog.getByRole("button", { name: /^(Connect|Save changes)$/ }).click();
    await expect.poll(() => created?.secret).toEqual({ userToken: debugInfo });
    await expect(dialog).toHaveCount(0);
    await page.locator(".service-account").getByRole("button", { name: "Edit configuration", exact: true }).click();
    const configure = page.getByRole("dialog", { name: "Configure Musixmatch" });
    await expect(configure.locator('textarea[name="userToken"]')).toHaveValue("");
    await expect(configure.getByText("Configured · replace", { exact: true })).toBeVisible();
    await configure.getByRole("button", { name: /^(Connect|Save changes)$/ }).click();
    await expect.poll(() => replacement?.secret).toEqual({});
    await expect(configure).toHaveCount(0);
  });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Recording lyrics sources stay scoped and usable at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.emulateMedia({ colorScheme: theme as "light" | "dark", reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const fixture = await mockRecordingLyrics(page);
    await page.goto("#/library/mappings?review=snapshot");
    const dialog = page.getByRole("dialog", { name: "Fixture song" });
    const open = dialog.getByRole("button", { name: "More", exact: true });
    await open.click();
    await page.getByRole("menuitem", { name: "Wrong lyrics?", exact: true }).click();
    const picker = dialog.getByRole("region", { name: "Lyrics sources" });
    await expect(picker).toBeVisible();
    await expect(dialog.getByRole("button", { name: "Nothing fits", exact: true })).toHaveCount(0);
    await expect(dialog.getByRole("button", { name: "Back to match", exact: true })).toBeVisible();
    await expect(picker.locator(".lyrics-source > span")).toHaveText(["LyricsPlus", "LRCLIB", "Spotify"]);
    const best = picker.getByRole("button", { name: /LyricsPlus.*Synced/ });
    await expect(best.getByText("Best match", { exact: true })).toBeVisible();
    await expect(picker.getByText("Best match", { exact: true })).toHaveCount(1);
    await expect(best).toHaveAttribute("aria-pressed", "true");
    await expect(picker.getByRole("button", { name: /Spotify.*Unavailable/ })).toBeDisabled();
    await picker.getByRole("button", { name: /LRCLIB.*Plain/ }).click();
    await expect(picker.getByRole("textbox", { name: "Lyrics preview" })).toHaveValue("Personal plain words <script>inert</script>");
    await expect(picker.locator("script")).toHaveCount(0);
    await expectRecordingLyricsGeometry(page, picker, width);
    await page.screenshot({ path: `/tmp/allstarr-lyrics-picker-${width}-${theme}.png` });
    const save = picker.getByRole("button", { name: "Use this source for this recording", exact: true });
    await save.focus();
    await page.keyboard.press("Enter");
    await expect.poll(() => fixture.writes.length).toBe(1);
    expect(fixture.writes[0]).toMatchObject({ method: "PUT", body: { sourceId: "lrclib", scope: "personal",
      expectedRevision: { personal: { id: "personal-choice", revision: 1 }, household: { id: "household-choice", revision: 2 } },
      previewRevision: "sha256:personal-plain-1" } });
    await expect(page.getByRole("status").filter({ hasText: "Lyrics source saved for you." })).toBeVisible();
    const useDefault = picker.getByRole("button", { name: "Use household default", exact: true });
    if (width === 1280) {
      await useDefault.scrollIntoViewIfNeeded();
      const target = await useDefault.boundingBox();
      await page.mouse.click(target!.x + target!.width / 2, target!.y - 2);
    } else await useDefault.click();
    await expect.poll(() => fixture.writes.length).toBe(2);
    expect(fixture.writes[1]).toMatchObject({ method: "DELETE", body: { scope: "personal",
      expectedRevision: { personal: { id: "personal-choice", revision: 2 }, household: { id: "household-choice", revision: 2 } } } });
    await picker.getByRole("radio", { name: "Household", exact: true }).check();
    await expect(picker.getByRole("textbox", { name: "Lyrics preview" })).toHaveValue("Shared plain words");
    await expect(picker.getByRole("button", { name: /LRCLIB.*Plain/ })).toHaveAttribute("aria-pressed", "true");
    await expect(best).toHaveAttribute("aria-pressed", "false");
    await expect(best.getByText("Best match", { exact: true })).toBeVisible();
    await expect(picker).toContainText("Saved source: LRCLIB.");
    await expect(picker).not.toContainText("Personal plain words");
    await picker.getByRole("button", { name: /LyricsPlus.*Synced/ }).click();
    await expect(picker).toContainText("Preview shortened");
    await save.click();
    await expect.poll(() => fixture.writes.length).toBe(3);
    expect(fixture.writes[2]).toMatchObject({ method: "PUT", body: { scope: "household", sourceId: "lyricsplus",
      expectedRevision: { personal: null, household: { id: "household-choice", revision: 2 } } } });
    await expectRecordingLyricsGeometry(page, picker, width);
    await dialog.getByRole("button", { name: "Back to match", exact: true }).focus();
    await page.keyboard.press("Enter");
    await expect(picker).toHaveCount(0);
    await expect(dialog.getByRole("button", { name: "Nothing fits", exact: true })).toBeVisible();
    await expect(dialog.getByRole("button", { name: "More", exact: true })).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
  });
}

test("Recording lyrics conflict reloads the preview without repeating a write", async ({ page }) => {
  const fixture = await mockRecordingLyrics(page, { administrator: false, conflict: true });
  await page.goto("#/library/mappings?review=snapshot");
  await page.getByRole("button", { name: "More", exact: true }).click();
  await page.getByRole("menuitem", { name: "Wrong lyrics?", exact: true }).click();
  const picker = page.getByRole("region", { name: "Lyrics sources" });
  await expect(picker.getByRole("radio", { name: "Household", exact: true })).toHaveCount(0);
  await picker.getByRole("button", { name: /LRCLIB.*Plain/ }).click();
  await picker.getByRole("button", { name: "Use this source for this recording", exact: true }).click();
  await expect.poll(fixture.reads).toBe(2);
  expect(fixture.writes).toHaveLength(1);
  await expect(picker.getByRole("alert")).toContainText("The lyrics or saved choice changed.");
  await expect(page.getByRole("region", { name: "Notifications" }).getByRole("alert")).toHaveCount(0);
  await expect(picker.getByRole("textbox", { name: "Lyrics preview" })).toHaveValue("[00:01.234]Timed words");
  await picker.getByRole("button", { name: /LRCLIB.*Plain/ }).click();
  await picker.getByRole("button", { name: "Use this source for this recording", exact: true }).click();
  await expect.poll(() => fixture.writes.length).toBe(2);
  expect(fixture.writes[1].body).toMatchObject({ expectedRevision: { personal: { id: "personal-choice", revision: 8 } }, previewRevision: "sha256:personal-plain-2" });
});

test("Recording lyrics unavailable sources can be reloaded", async ({ page }) => {
  const fixture = await mockRecordingLyrics(page, { failFirst: true });
  await page.goto("#/library/mappings?review=snapshot");
  await page.getByRole("button", { name: "More", exact: true }).click();
  await page.getByRole("menuitem", { name: "Wrong lyrics?", exact: true }).click();
  const picker = page.getByRole("region", { name: "Lyrics sources" });
  await expect(picker).toContainText("Lyrics sources are temporarily unavailable.");
  await picker.getByRole("button", { name: "Reload sources", exact: true }).click();
  await expect(picker.getByRole("textbox", { name: "Lyrics preview" })).toHaveValue("[00:01.234]Timed words");
  expect(fixture.reads()).toBe(2);
  expect(fixture.writes).toHaveLength(0);
});

test("Recording lyrics review has an accessible song name", async ({ page }) => {
  await mockRecordingLyrics(page);
  await page.goto("#/library/mappings?review=snapshot");
  await expect(page.getByRole("dialog")).toHaveAccessibleName("Fixture song");
});

for (const [text, expected] of [
  ["Espresso", { title: "Espresso" }],
  ["Daft Punk - Get Lucky", { title: "Get Lucky", artist: "Daft Punk" }],
  ["Get Lucky — Daft Punk", { title: "Get Lucky", artist: "Daft Punk" }],
] as const) {
  test(`Lyrics direct text lookup accepts ${text}`, async ({ page }) => {
    const requests: Record<string, unknown>[] = [];
    const dialog = await openLyricsTry(page, requests);
    await dialog.getByRole("searchbox", { name: "Song", exact: true }).fill(text);
    await dialog.getByRole("button", { name: "Use what I typed" }).click();
    await expect(dialog.getByRole("region", { name: "Lyrics", exact: true })).toBeVisible();
    expect(requests.at(-1)).toMatchObject({ capability: "lyrics", operation: "lyrics", ...expected });
    if (!("artist" in expected)) expect(requests.at(-1)).not.toHaveProperty("artist");
  });
}

test("Lyrics details allow ISRC alone, refine text and remember disclosure for the session", async ({ page }) => {
  const requests: Record<string, unknown>[] = [];
  let dialog = await openLyricsTry(page, requests);
  const more = dialog.getByRole("button", { name: "More details" });
  await expect(more).toHaveAttribute("aria-expanded", "false");
  await expect(dialog.getByLabel("Artist", { exact: true })).toHaveCount(0);
  await more.click();
  await dialog.getByLabel("ISRC", { exact: true }).fill("USQX91300108");
  await dialog.getByRole("button", { name: "Use what I typed" }).click();
  await expect.poll(() => requests.at(-1)).toMatchObject({ capability: "lyrics", isrc: "USQX91300108" });
  expect(requests.at(-1)).not.toHaveProperty("title");
  await expect(dialog.getByRole("region", { name: "Lyrics", exact: true })).toBeVisible();
  await dialog.getByRole("searchbox", { name: "Song", exact: true }).fill("Get Lucky");
  await dialog.getByLabel("Artist", { exact: true }).fill("Daft Punk");
  await dialog.getByLabel("Album", { exact: true }).fill("Random Access Memories");
  await dialog.getByLabel("Length", { exact: true }).fill("6:09");
  await dialog.getByRole("button", { name: "Use what I typed" }).click();
  await expect.poll(() => requests.at(-1)).toMatchObject({ title: "Get Lucky", artist: "Daft Punk", album: "Random Access Memories", durationSeconds: 369 });
  await dialog.getByRole("button", { name: "Done", exact: true }).click();
  await page.reload();
  await page.locator(".service-account.focused .service-check").filter({ hasText: "Metadata" })
    .getByRole("button", { name: "Open Metadata tests" }).click();
  dialog = page.getByRole("dialog", { name: "Test Lumen Audio" });
  await dialog.getByRole("tab", { name: "Lyrics" }).click();
  await expect(dialog.getByRole("button", { name: "More details" })).toHaveAttribute("aria-expanded", "true");
});

test("Lyrics search picks full song metadata and suggestions run immediately", async ({ page }) => {
  const requests: Record<string, unknown>[] = [];
  const dialog = await openLyricsTry(page, requests);
  await expect(dialog).toContainText("Try a song you know has lyrics");
  await dialog.getByRole("button", { name: "Espresso · Sabrina Carpenter", exact: true }).click();
  await expect.poll(() => requests.at(-1)).toMatchObject({ capability: "lyrics", title: "Espresso", artist: "Sabrina Carpenter" });
  await expect(dialog.getByRole("region", { name: "Lyrics", exact: true })).toBeVisible();
  await dialog.getByRole("searchbox", { name: "Song", exact: true }).fill("get lucky");
  await dialog.getByRole("searchbox", { name: "Song", exact: true }).press("Enter");
  await expect(dialog.getByRole("list", { name: "Songs to test" }).getByRole("listitem")).toHaveCount(2);
  await dialog.getByRole("button", { name: `Get lyrics for ${tryTracks[0].title}` }).click();
  await expect.poll(() => requests.at(-1)).toMatchObject({ capability: "lyrics", title: tryTracks[0].title,
    artist: tryTracks[0].artists.join(", "), album: tryTracks[0].album, durationSeconds: 369, isrc: "USQX91300108", trackId: "lumen-71230" });
  await expect(dialog.locator(".try-lyrics header")).toContainText("3 lines");
  await expect(dialog.locator(".try-lyrics header")).toContainText("1.3 s");
});

test("Lyrics search limits matches to five and leaves direct lookup available after no matches", async ({ page }) => {
  const requests: Record<string, unknown>[] = [];
  const dialog = await openLyricsTry(page, requests);
  await page.route("**/api/admin/providers/lumen-audio/try", route => {
    const body = route.request().postDataJSON();
    if (body.operation !== "search") return route.fallback();
    return route.fulfill({ json: { provider: "lumen-audio", capability: "metadata", operation: "search", succeeded: true, elapsedMs: 100,
      tracks: body.query === "nothing" ? [] : Array.from({ length: 7 }, (_, index) => ({ ...tryTracks[0], id: `result-${index}` })),
    } });
  });
  const song = dialog.getByRole("searchbox", { name: "Song", exact: true });
  await song.fill("get lucky");
  await song.press("Enter");
  await expect(dialog.getByRole("list", { name: "Songs to test" }).getByRole("listitem")).toHaveCount(5);
  await song.fill("nothing");
  await song.press("Enter");
  await expect(dialog.getByRole("list", { name: "Songs to test" })).toHaveCount(0);
  await expect(dialog).toContainText("No matching songs found.");
  await dialog.getByRole("button", { name: "Use what I typed" }).click();
  await expect.poll(() => requests.at(-1)).toMatchObject({ capability: "lyrics", title: "nothing" });
});

for (const width of [390, 1280]) for (const scheme of ["light", "dark"] as const) {
  test(`Lyrics source uses household search, a monogram and one inline failure at ${width}px ${scheme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), scheme);
    await page.route("**/*", route => new URL(route.request().url()).hostname === "127.0.0.1" ? route.continue() : route.abort());
    await mockApi(page);
    const searches: Record<string, unknown>[] = [];
    const lyrics: Record<string, unknown>[] = [];
    let needsArtist = true;
    await mockTry(page, searches);
    await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
      ...schema, priorityGroups: [{ id: "metadata", label: "Metadata search order", providers: ["lumen-audio"] }],
      providers: [...schema.providers, { id: "paxsenix", name: "Paxsenix", categories: ["lyrics"], runtimeCapabilities: [
        { id: "lyrics", supported: true, ready: true, canAttempt: true, canTest: true, configuration: "not_required" },
      ] }],
    } }));
    await page.route(/\/images\/providers\/(?:brands\/paxsenix\/color\.(?:svg|png)|paxsenix\.svg)$/, route => route.abort());
    await page.route("**/api/admin/providers/paxsenix/try**", route => {
      if (route.request().method() === "GET") return route.fulfill({ json: { provider: "paxsenix", capabilities: [tryCatalog.capabilities[1]] } });
      const body = route.request().postDataJSON();
      lyrics.push(body);
      return route.fulfill({ json: { provider: "paxsenix", capability: "lyrics", operation: "lyrics", succeeded: false,
        elapsedMs: 240, reasonCode: needsArtist ? "needs_artist" : null, message: needsArtist ? "This source needs the artist too" : null,
        ...(needsArtist ? {} : { succeeded: true, lyrics: { format: "line", source: "Paxsenix", language: "en", lineCount: 80,
          lines: Array.from({ length: 80 }, (_, index) => ({ startMs: index * 2000, text: `Fixture lyric line ${index + 1}` })) } }),
      } });
    });
    await page.goto("#/integrations/services/paxsenix");
    await openSourceSettings(page, "Paxsenix");
    await page.getByRole("button", { name: "Open Lyrics tests" }).click();
    const dialog = page.getByRole("dialog", { name: "Test Paxsenix" });
    await expect(dialog.locator(".try-mark .provider-monogram")).toHaveText("P");
    await page.screenshot({ path: `test-results/try-lyrics-empty-${width}-${scheme}.png` });
    await dialog.getByRole("searchbox", { name: "Song", exact: true }).fill("get lucky");
    await dialog.getByRole("button", { name: "Find", exact: true }).click();
    await expect.poll(() => searches.at(-1)).toMatchObject({ capability: "metadata", query: "get lucky", accountId: "account" });
    await expect(dialog.getByRole("list", { name: "Songs to test" }).getByRole("listitem")).toHaveCount(2);
    await page.screenshot({ path: `test-results/try-lyrics-matches-${width}-${scheme}.png` });
    expect(lyrics).toHaveLength(0);
    await dialog.getByRole("button", { name: "Use what I typed" }).click();
    await expect(dialog.getByRole("alert")).toHaveText("This source needs the artist too");
    await expect(page.getByRole("alert")).toHaveCount(1);
    await expect(page.locator(".toaster .toast")).toHaveCount(0);
    await dialog.getByRole("button", { name: "More details" }).click();
    await expect(dialog.getByLabel("Artist", { exact: true })).toBeVisible();
    await dialog.locator(".try-body").evaluate(element => element.scrollTo(0, element.scrollHeight));
    await expect(dialog.getByRole("searchbox", { name: "Song", exact: true })).toBeInViewport({ ratio: 1 });
    await expect(dialog.getByRole("button", { name: "Done", exact: true })).toBeInViewport({ ratio: 1 });
    expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: `test-results/try-lyrics-failure-details-${width}-${scheme}.png` });
    needsArtist = false;
    await dialog.getByLabel("Artist", { exact: true }).fill("Daft Punk");
    await dialog.getByRole("button", { name: "More details" }).click();
    await dialog.getByRole("button", { name: `Get lyrics for ${tryTracks[0].title}` }).click();
    await expect.poll(() => lyrics.at(-1)).toMatchObject({ title: tryTracks[0].title, artist: tryTracks[0].artists.join(", "),
      album: tryTracks[0].album, durationSeconds: 369, isrc: "USQX91300108" });
    expect(lyrics.at(-1)).not.toHaveProperty("trackId");
    const card = dialog.getByRole("region", { name: "Lyrics", exact: true });
    await expect(card).toBeVisible();
    await expect(card.locator("header")).toContainText("80 lines found in 240 ms");
    const scroller = card.getByRole("region", { name: "Lyric lines", exact: true });
    expect(await scroller.evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true);
    expect(await card.evaluate(element => element.getBoundingClientRect().height)).toBeLessThanOrEqual(width < 600 ? 450 : 360);
    await expect(card).toBeInViewport();
    await expect(dialog.getByRole("searchbox", { name: "Song", exact: true })).toBeInViewport({ ratio: 1 });
    await expect(dialog.getByRole("button", { name: "Done", exact: true })).toBeInViewport({ ratio: 1 });
    await page.screenshot({ path: `test-results/try-lyrics-results-${width}-${scheme}.png` });
  });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Lyrics test names the actual source at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const requests: Record<string, unknown>[] = [];
    await mockApi(page);
    await mockTry(page, requests);
    await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
      ...schema, providers: [...schema.providers, { id: "lrclib", name: "LRCLib", categories: ["lyrics"] }],
    } }));
    await page.route("**/api/admin/providers/lumen-audio/try**", route => {
      if (route.request().method() === "GET") return route.fallback();
      const request = route.request().postDataJSON();
      if (request.operation !== "lyrics") return route.fallback();
      requests.push(request);
      return route.fulfill({ json: { provider: "lumen-audio", capability: "lyrics", operation: "lyrics", succeeded: true, elapsedMs: 20,
        lyrics: { source: "lrclib", format: "line", lineCount: 2,
          lines: [{ startMs: 1000, text: "First fixture line" }, { startMs: 2000, text: "Second fixture line" }] } } });
    });
    await page.goto("#/integrations/accounts");
    await page.getByRole("link", { name: "Open Lumen Audio account" }).click();
    await page.locator(".service-account.focused .service-check").filter({ hasText: "Metadata" })
      .getByRole("button", { name: "Open Metadata tests" }).click();
    const dialog = page.getByRole("dialog", { name: "Test Lumen Audio" });
    await dialog.getByRole("tab", { name: "Lyrics" }).click();
    await dialog.getByRole("searchbox", { name: "Song", exact: true }).fill("Fixture song");
    await dialog.getByRole("button", { name: "Use what I typed" }).click();
    await expect(dialog.getByText("From LRCLib", { exact: true })).toBeVisible();
    await expect(dialog.getByText("From lrclib", { exact: true })).toHaveCount(0);
    expect(requests.at(-1)).toMatchObject({ capability: "lyrics", operation: "lyrics", title: "Fixture song" });
    await expect(dialog.getByRole("region", { name: "Lyric lines", exact: true })).toContainText("First fixture line");
    if (width < 600) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: `test-results/lyrics-source-name-${width}-${theme}.png` });
  });

  test(`Lyrics routing preserves movable local sources at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    let order = "backend,lrclib,kept,paxsenix";
    let saved: Record<string, string> | undefined;
    await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
      ...schema,
      providers: [...schema.providers, { id: "lrclib", name: "LRCLib", categories: ["lyrics"] },
        { id: "paxsenix", name: "Paxsenix", categories: ["lyrics"] }],
      priorityGroups: [{ id: "lyrics", label: "Lyrics", envKey: "MULTI_PROVIDER_LYRICS_ORDER", providers: ["backend", "lrclib", "kept", "paxsenix"] }],
    } }));
    await page.route("**/api/admin/config", route => {
      if (route.request().method() === "POST") {
        saved = route.request().postDataJSON().updates;
        order = saved!.MULTI_PROVIDER_LYRICS_ORDER;
        return route.fulfill({ json: { message: "Runtime configuration updated." } });
      }
      return route.fulfill({ json: { ...(responses["/api/admin/config"] as object), providers: { lyricsOrder: order } } });
    });
    const lyricsScore = (providerId: string, score: number) => ({ providerId, score, uptimePercent: 100, checks: 12, days: [], quality: null,
      capabilities: [{ capability: "lyrics", score, uptimePercent: 100, checks: 12, failures: 0, typicalLatencyMilliseconds: 300, lastFailureCode: null }] });
    await page.route("**/api/admin/providers/scores", route => route.fulfill({ json: { providers: [lyricsScore("lrclib", 41), lyricsScore("paxsenix", 88)] } }));
    await page.goto("#/integrations/routing");
    const group = page.locator(".routing-group").filter({ has: page.getByRole("heading", { name: "Lyrics source order", exact: true }) });
    await expect(group.locator("li strong")).toHaveText(["Media server lyrics", "LRCLib", "Kept LRC", "Paxsenix"]);
    await group.getByLabel("Reorder Kept LRC", { exact: true }).click();
    await expect(group.getByRole("button", { name: "Move Kept LRC up", exact: true })).toBeEnabled();
    await expect(group.getByRole("button", { name: "Move Kept LRC down", exact: true })).toBeEnabled();
    await expect(group.locator("li").nth(2)).toHaveAttribute("draggable", "true");
    await group.locator("li").nth(2).dispatchEvent("dragstart");
    await group.locator("li").first().dispatchEvent("drop");
    await group.locator("li").first().dispatchEvent("dragend");
    await expect(group.locator("li strong")).toHaveText(["Kept LRC", "Media server lyrics", "LRCLib", "Paxsenix"]);
    await group.getByRole("button", { name: "Move Kept LRC down", exact: true }).click();
    await group.getByLabel("Reorder LRCLib", { exact: true }).click();
    await group.getByRole("button", { name: "Move LRCLib up", exact: true }).focus();
    await page.keyboard.press("Enter");
    await expect(group.locator("li strong")).toHaveText(["Media server lyrics", "LRCLib", "Kept LRC", "Paxsenix"]);
    await group.getByRole("button", { name: "Move LRCLib up", exact: true }).click();
    await expect.poll(() => saved).toEqual({ MULTI_PROVIDER_LYRICS_ORDER: "lrclib,backend,kept,paxsenix" });
    await expect(group.locator("li strong")).toHaveText(["LRCLib", "Media server lyrics", "Kept LRC", "Paxsenix"]);
    await expect(group.locator("header")).toContainText("Drag to reorder. Saves as you drop.");
    await expect(group).not.toContainText("gets 5 confidence points");
    // Lyrics sources are scored too; audio quality means nothing for them, and local lyrics stay first when sorting.
    await expect(group.locator(".routing-quality-label")).toHaveCount(0);
    await expect(group.locator("li").filter({ hasText: "Kept LRC" })).toContainText("Local");
    await group.getByRole("button", { name: "Sort by score", exact: true }).click();
    await expect(group.locator("li strong")).toHaveText(["Media server lyrics", "Kept LRC", "Paxsenix", "LRCLib"]);
    await expect.poll(() => saved).toEqual({ MULTI_PROVIDER_LYRICS_ORDER: "backend,kept,paxsenix,lrclib" });
    while (await page.getByRole("button", { name: "Dismiss notification", exact: true }).count())
      await page.getByRole("button", { name: "Dismiss notification", exact: true }).first().click();
    await group.getByRole("heading", { name: "Lyrics source order", exact: true }).click();
    if (width < 600) await expectContainedMobileGeometry(page);
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({ path: `test-results/lyrics-routing-${width}-${theme}.png`, fullPage: true });
  });
}

for (const width of [320, 390, 718, 1280]) for (const theme of ["light", "dark"]) {
  test(`SpicyLyrics free key setup stays private at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: width === 718 ? 681 : 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockSpicySource(page, false);
    await page.goto("#/integrations/services/spicylyrics");
    await openSourceSettings(page, "SpicyLyrics");
    const setup = page.getByRole("region", { name: "SpicyLyrics setup" });
    await expect(setup).toContainText("free server key");
    await expect(setup.getByRole("link", { name: "Get a free API key" })).toHaveAttribute("href", "https://developers.spicylyrics.org/docs/keys");
    await expect(page.getByRole("dialog", { name: "SpicyLyrics", exact: true }).locator('.panel-artwork img[src$="/brands/spicylyrics/color.svg"]')).toBeVisible();
    if (width < 600) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: `test-results/spicy-setup-${width}-${theme}.png`, fullPage: true });
    await page.getByRole("button", { name: "Connect account", exact: true }).click();
    const dialog = page.locator(".connect-source-dialog");
    const key = dialog.getByLabel("Server API key", { exact: true });
    await expect(key).toHaveAttribute("type", "password");
    await expect(key).toHaveValue("");
    await key.fill("fixture-server-key");
    await expect(key).toHaveAttribute("type", "password");
    if (width < 600) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: `test-results/spicy-key-${width}-${theme}.png` });
  });

  test(`SpicyLyrics tests Spotify links and clears response credits at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: width === 718 ? 681 : 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockSpicySource(page);
    const requests: Record<string, unknown>[] = [];
    const testCredits = width === 718 ? { ...spicyCredits,
      uploader: { ...spicyCredits.uploader, username: "Uploader " + "X".repeat(190) },
      maker: { ...spicyCredits.maker, username: "Maker <script>inert</script> " + "Y".repeat(170) },
    } : spicyCredits;
    let count = 0;
    await page.route("**/api/admin/providers/spicylyrics/try**", async route => {
      if (route.request().method() === "GET") return route.fulfill({ json: {
        provider: "spicylyrics", capabilities: [{ capability: "lyrics", accountRequirement: "required", operations: [{ id: "lyrics", inputs: ["trackId"] }] }],
      } });
      const body = route.request().postDataJSON(); requests.push(body); count++;
      const result = { provider: "spicylyrics", capability: "lyrics", operation: "lyrics", accountId: "spicy-account", elapsedMs: 25 };
      if (count === 3) return route.fulfill({ json: { ...result, succeeded: false, reasonCode: "not_found" } });
      return route.fulfill({ json: { ...result, succeeded: true, lyrics: { source: "spicylyrics", format: "word", lineCount: 65,
        lines: Array.from({ length: 65 }, (_, index) => ({ startMs: 1200 + index * 1200,
          text: index === 0 ? "First fixture words" : index === 1 ? "Second fixture words" : `Fixture lyric line ${index + 1}` })),
        attribution: count === 1 ? testCredits : { service: "spicylyrics", catalog: "apple_music" },
      } } });
    });
    await page.goto("#/integrations/services/spicylyrics");
    const sourcePanel = page.getByRole("dialog", { name: "SpicyLyrics", exact: true });
    // Both sections load above the test opener; wait for their final layout before clicking.
    await expect(sourcePanel.getByText("No checks yet", { exact: true })).toBeVisible();
    await expect(sourcePanel.getByRole("radiogroup", { name: "Use this source for you", exact: true })).toBeVisible();
    await openSourceSettings(page, "SpicyLyrics");
    await clickWhenStable(sourcePanel.getByRole("button", { name: "Open Lyrics tests", exact: true }));
    const dialog = page.getByRole("dialog", { name: "Test SpicyLyrics" });
    await expect(dialog).toBeVisible();
    const input = dialog.getByRole("textbox", { name: "Spotify track link or ID", exact: true });
    const selector = `https://open.spotify.com/track/${spicyTrack}?si=fixture-share`;
    await input.fill(selector);
    await dialog.getByRole("button", { name: "Get lyrics", exact: true }).click();
    const credits = dialog.getByRole("region", { name: "Lyrics credits" });
    await expect(credits).toContainText("Lyrics source: Spicy Lyrics");
    await expect(credits.getByRole("link", { name: `Uploaded by ${testCredits.uploader.username}` })).toHaveAttribute("href", spicyCredits.uploader.url);
    await expect(credits.getByRole("link", { name: `Synced by ${testCredits.maker.username}` })).toHaveAttribute("rel", "noopener noreferrer");
    await expect(credits.locator("script")).toHaveCount(0);
    expect(requests[0]).toEqual({ capability: "lyrics", operation: "lyrics", accountId: "spicy-account", trackId: selector });
    const lyricLines = dialog.getByRole("region", { name: "Lyric lines" });
    await expect(lyricLines).toContainText("First fixture words");
    const geometry = await dialog.getByRole("region", { name: "Lyrics", exact: true }).evaluate(root => {
      const credit = root.querySelector<HTMLElement>('[aria-label="Lyrics credits"]')!;
      const lyric = root.querySelector<HTMLElement>(".try-lyric-scroll")!;
      return { creditBottom: Math.max(credit.getBoundingClientRect().bottom,
        ...Array.from(credit.querySelectorAll("p, a, span")).map(element => element.getBoundingClientRect().bottom)),
        lyricTop: lyric.getBoundingClientRect().top, lyricHeight: lyric.getBoundingClientRect().height,
        lyricBottom: lyric.getBoundingClientRect().bottom, cardBottom: root.getBoundingClientRect().bottom };
    });
    expect(geometry.creditBottom, "Visible credits stay above the scrolling lyric rows").toBeLessThanOrEqual(geometry.lyricTop + 1);
    expect(geometry.lyricHeight, "Long results keep a usable lyric viewport").toBeGreaterThanOrEqual(44);
    expect(geometry.lyricBottom, "The card contains the complete lyric viewport").toBeLessThanOrEqual(geometry.cardBottom + 1);
    await lyricLines.evaluate(element => element.scrollTop = element.scrollHeight);
    await lyricLines.getByText("Fixture lyric line 65", { exact: true }).scrollIntoViewIfNeeded();
    await expect(lyricLines.getByText("Fixture lyric line 65", { exact: true })).toBeInViewport({ ratio: 1 });
    await expect(credits).toBeInViewport();
    await lyricLines.evaluate(element => element.scrollTop = 0);
    if (width < 600) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: `test-results/spicy-test-${width}-${theme}.png` });
    await input.fill(spicyTrack);
    await dialog.getByRole("button", { name: "Get lyrics", exact: true }).click();
    await expect(credits).toContainText("Lyrics source: Apple Music");
    await expect(credits.getByText(testCredits.uploader.username)).toHaveCount(0);
    await expect(credits.getByRole("link", { name: /Uploaded by|Synced by/ })).toHaveCount(0);
    await dialog.getByRole("button", { name: "Get lyrics", exact: true }).click();
    await expect(credits).toHaveCount(0);
    await expect(dialog.getByRole("region", { name: "Lyrics", exact: true })).toHaveCount(0);
    await expect(dialog).toContainText("SpicyLyrics found nothing for this.");
    expect(requests).toHaveLength(3);
    expect(requests.every(request => request.operation === "lyrics")).toBe(true);
  });

  test(`SpicyLyrics recording preview keeps credits with its source at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: width === 718 ? 681 : 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockRecordingLyrics(page);
    await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: { ...schema, providers: [...schema.providers, spicyDefinition,
      { id: "lrclib", name: "LRCLib", categories: ["lyrics"] }] } }));
    await page.route(`**/api/admin/recordings/${lyricsRecordingId}/lyrics*`, route => route.fulfill({ json: {
      recordingId: lyricsRecordingId, scope: "personal", song: { title: "Fixture song", artist: "Artist", album: "Album" },
      choices: { personal: null, household: null, effective: null, revision: { personal: null, household: null } }, sources: [
        { sourceId: "spicylyrics", available: true, preview: "[00:01.200]Community fixture", previewRevision: "sha256:spicy", format: "synced", truncated: false,
          attribution: { ...spicyCredits, maker: { username: spicyUnlinkedMaker, url: "https://spicylyrics.org.evil.test/uid/456" } } },
        { sourceId: "lrclib", available: true, preview: "Public fixture", previewRevision: "sha256:public", format: "plain", truncated: false },
      ],
    } }));
    await page.goto("#/library/mappings?review=snapshot");
    await page.getByRole("dialog", { name: "Fixture song" }).getByRole("button", { name: "More", exact: true }).click();
    await page.getByRole("menuitem", { name: "Wrong lyrics?", exact: true }).click();
    const picker = page.getByRole("region", { name: "Lyrics sources" });
    const credits = picker.getByRole("region", { name: "Lyrics credits" });
    await expect(credits).toContainText(`Synced by ${spicyUnlinkedMaker}`);
    await expect(credits.getByRole("link", { name: /Unlinked maker/ })).toHaveCount(0);
    await expect(picker.getByRole("textbox", { name: "Lyrics preview" })).toHaveValue("[00:01.200]Community fixture");
    await picker.getByRole("textbox", { name: "Lyrics preview" }).scrollIntoViewIfNeeded();
    await expect(credits).toBeInViewport();
    if (width < 600) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: `test-results/spicy-picker-${width}-${theme}.png` });
    await picker.getByRole("button", { name: /LRCLib.*Plain/ }).click();
    await expect(credits).toHaveCount(0);
    await expect(picker.getByRole("textbox", { name: "Lyrics preview" })).toHaveValue("Public fixture");
  });
}
