import { expect, test } from "@playwright/test";
import { openOtherPlaylistSources, nextPlaylistOptions, responses, routeRelease, mockApi } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

test("Import playlist separates source, import mode, destination, updates, and storage on mobile", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const sources = routeRelease();
  await mockApi(page, { releasePath: "/api/admin/playlist-sources", release: sources.promise });
  await page.goto("#/library/playlists");
  const add = page.getByRole("button", { name: "Import playlist" });
  await add.click();
  const dialog = page.getByRole("dialog", { name: "Import a playlist" });
  await expect(dialog).toBeVisible();
  const dialogBox = await dialog.boundingBox();
  expect(dialogBox).toBeTruthy();
  expect(dialogBox!.x).toBeLessThanOrEqual(1);
  expect(Math.abs(dialogBox!.width - 390)).toBeLessThanOrEqual(1);
  expect(dialogBox!.height).toBeLessThanOrEqual(844 * 0.92 + 1);
  await expect(dialog.getByRole("status", { name: "Loading items" })).toBeVisible();
  sources.release();
  await openOtherPlaylistSources(dialog);
  await expect(dialog.getByRole("radiogroup", { name: "Import from" }).getByRole("radio")).toHaveCount(5);
  await expect(dialog.getByRole("button", { name: "Choose", exact: true })).toBeDisabled();
  await expect(dialog.getByRole("button", { name: "Options and review", exact: true })).toBeDisabled();
  const stepTops = await dialog.locator(".playlist-add-steps button").evaluateAll((buttons) =>
    buttons.map((button) => Math.round(button.getBoundingClientRect().top)));
  expect(new Set(stepTops).size).toBe(1);
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  await expect(add).toBeFocused();
  await add.click();
  await openOtherPlaylistSources(dialog);
  const sourceGroups = dialog.getByRole("radiogroup", { name: "Import from" }).locator(".pl-option-text strong");
  await expect(sourceGroups).toHaveCount(5);
  expect(await sourceGroups.allTextContents()).toEqual([
    "Jellyfin", "Subsonic", "Spotify", "Lumen Audio", "Qobuz",
  ]);
  await openOtherPlaylistSources(dialog);
  await dialog.getByRole("radio", { name: /Spotify/ }).check();
  await expect(dialog.locator(".other-sources .playlist-art > span")).toBeVisible();
  await dialog.getByRole("button", { name: "Load more" }).click();
  await expect(dialog.getByRole("radio", { name: /Second Mix/ })).toBeVisible();
  await dialog.getByRole("radio", { name: /Source Mix/ }).check();
  await nextPlaylistOptions(dialog);
  await expect(dialog.getByRole("button", { name: "Back to Choose" })).toBeEnabled();
  await expect(dialog.getByRole("button", { name: "Options and review", exact: true })).toHaveAttribute("aria-current", "step");
  if (process.env.ALLSTARR_SCREENSHOT_DIR)
    await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/playlist-390-listener-choice.png` });
  await expect(dialog.getByRole("radio", { name: /Import once/ })).toBeChecked();
  await dialog.getByRole("radio", { name: /Keep in sync/ }).check();
  await nextPlaylistOptions(dialog);
  await expect.poll(() => dialog.locator(".playlist-add-body").evaluate((body) => body.scrollTop)).toBe(0);
  await expect(dialog.getByRole("radio", { name: /New playlist/ })).toBeChecked();
  await dialog.getByRole("radio", { name: /Existing playlist/ }).check();
  await expect(dialog.getByRole("radio", { name: /Road trip/ })).toBeVisible();
  await dialog.getByRole("radio", { name: /Road trip/ }).check();
  if (process.env.ALLSTARR_SCREENSHOT_DIR) {
    await dialog.locator(".playlist-add-body").evaluate((body) => body.scrollTo({ top: 0 }));
    await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/playlist-390-appearance-choice.png` });
  }
  await nextPlaylistOptions(dialog);
  await dialog.getByRole("button", { name: "Update schedule" }).click();
  await expect(page.getByRole("option", { name: "Daily at 3:00 AM" })).toHaveAttribute("aria-selected", "true");
  await page.getByRole("option", { name: "Daily at 3:00 AM" }).click();
  await expect(dialog.getByRole("button", { name: /^Import (?:\d+ songs|playlist)$/ })).toBeVisible();
  await expect(dialog).toContainText("show its playable songs as a playlist in your apps");
  await expect(dialog).toContainText("Shows in your apps using the source name.");
  await expect(dialog.getByRole("checkbox", { name: /Keep downloaded copies/ })).not.toBeChecked();
  await dialog.getByRole("checkbox", { name: /Keep downloaded copies/ }).check();
  await expect(dialog.getByRole("button", { name: /^Import (?:\d+ songs|playlist)$/ })).toBeInViewport();
  await expect(dialog.locator(".playlist-add-body")).toHaveCSS("overflow-y", "auto");
  const create = page.waitForRequest((request) =>
    request.method() === "POST" && request.url().endsWith("/api/admin/playlist-links"));
  const scheduled = page.waitForRequest((request) =>
    request.method() === "POST" && request.url().endsWith("/schedules"));
  await dialog.getByRole("button", { name: /^Import (?:\d+ songs|playlist)$/ }).click();
  const input = (await create).postDataJSON();
  expect(input).toMatchObject({
    targetPlaylistId: "jellyfin-playlist",
    sourcePlaylistId: "playlist",
    mode: "hybrid",
    projectionMode: "resolved",
    materializationMode: "reconcile",
    mirrorStaleEntries: true,
    preserveManualEntries: true,
    importMode: "linked",
    trackRetention: "keepAll",
  });
  expect(input).not.toHaveProperty("libraryScopeId");
  expect((await scheduled).postDataJSON().cronExpression).toBe("0 3 * * *");
  await expect(dialog).toBeHidden();
});

for (const width of [390, 1280]) {
  for (const colorScheme of ["light", "dark"] as const) {
    for (const importMode of ["oneTime", "linked"] as const) {
      for (const destination of ["new", "existing"] as const) {
        test(`Playlist import ${importMode} into ${destination} at ${width}px ${colorScheme}`, async ({ page }) => {
          await page.setViewportSize({ width, height: 844 });
          await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
          await mockApi(page);
          const creates: Record<string, unknown>[] = [];
          const schedules: Record<string, unknown>[] = [];
          await page.route("**/api/admin/playlist-links", (route) => {
            if (route.request().method() !== "POST") return route.fallback();
            creates.push(route.request().postDataJSON());
            return route.fulfill({ contentType: "application/json", body: JSON.stringify({ id: "new-playlist" }) });
          });
          await page.route("**/api/admin/playlist-links/new-playlist/schedules", (route) => {
            schedules.push(route.request().postDataJSON());
            return route.fulfill({ contentType: "application/json", body: JSON.stringify({ id: "schedule" }) });
          });
          await page.goto("#/library/playlists");
          await page.getByRole("button", { name: "Import playlist" }).click();
          const dialog = page.getByRole("dialog", { name: "Import a playlist" });
          await openOtherPlaylistSources(dialog);
          await dialog.getByRole("radio", { name: /Spotify/ }).check();
          await dialog.getByRole("radio", { name: /Source Mix/ }).check();
          await nextPlaylistOptions(dialog);
          await dialog.getByRole("radio", { name: importMode === "oneTime" ? /Import once/ : /Keep in sync/ }).check();
          await nextPlaylistOptions(dialog);
          if (destination === "existing") {
            await dialog.getByRole("radio", { name: /Existing playlist/ }).check();
            await expect(dialog.getByRole("button", { name: /^Import (?:\d+ songs|playlist)$/ })).toBeDisabled();
            await dialog.getByRole("radio", { name: /Road trip/ }).check();
          } else {
            await expect(dialog.getByRole("radio", { name: /New playlist/ })).toBeChecked();
          }
          await nextPlaylistOptions(dialog);
          const keep = dialog.getByRole("checkbox", { name: /Keep downloaded copies/ });
          await expect(keep).not.toBeChecked();
          await keep.focus();
          await page.keyboard.press("Space");
          await expect(keep).toBeChecked();
          if (importMode === "oneTime") {
            await expect(dialog.getByRole("button", { name: "Update schedule" })).toHaveCount(0);
          } else {
            await expect(dialog.getByRole("button", { name: "Update schedule" })).toContainText("Daily at 3:00 AM");
          }
          const save = dialog.getByRole("button", { name: /^Import (?:\d+ songs|playlist)$/, exact: true });
          await expect(save).toBeInViewport();
          expect(await dialog.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(true);
          expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
          await save.click();
          await expect(dialog).toBeHidden();
          expect(creates).toHaveLength(1);
          expect(creates[0]).toMatchObject({
            importMode, targetPlaylistId: destination === "new" ? null : "jellyfin-playlist",
            mode: "hybrid", projectionMode: "resolved", materializationMode: "reconcile",
            mirrorStaleEntries: true, preserveManualEntries: true, trackRetention: "keepAll",
          });
          expect(schedules).toHaveLength(importMode === "linked" ? 1 : 0);
          if (importMode === "linked") expect(schedules[0].cronExpression).toBe("0 3 * * *");
        });
      }
    }
  }
}

for (const width of [390, 1280]) {
  for (const colorScheme of ["light", "dark"] as const) {
    test(`Public Spotify playlist without an account at ${width}px ${colorScheme}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 844 });
      await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
      await mockApi(page);
      await page.route("**/api/admin/playlist-sources", (route) => route.fulfill({
        contentType: "application/json", body: JSON.stringify({ accounts: [], blockedAccounts: [],
          providers: [{ id: "spotify", displayName: "Spotify", supportsPublicRead: true }] }),
      }));
      let created: Record<string, unknown> | undefined;
      await page.route("**/api/admin/playlist-links", (route) => {
        if (route.request().method() !== "POST") return route.fallback();
        created = route.request().postDataJSON();
        return route.fulfill({ contentType: "application/json", body: JSON.stringify({ id: "public-link" }) });
      });
      await page.goto("#/library/playlists");
      await page.getByRole("button", { name: "Import playlist" }).click();
      const dialog = page.getByRole("dialog", { name: "Import a playlist" });
      await openOtherPlaylistSources(dialog);
      await dialog.getByRole("radio", { name: /Public Spotify playlist/ }).check();
      await expect(dialog.getByRole("button", { name: "Next" })).toBeDisabled();
      await dialog.getByRole("searchbox", { name: "Playlist link, URI or ID" }).fill("https://open.spotify.com/playlist/public-mix?si=discard");
      await dialog.getByRole("button", { name: "Use playlist" }).click();
      await expect(dialog.getByRole("button", { name: "Next" })).toBeEnabled();
      expect(await dialog.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(true);
      if (process.env.ALLSTARR_SCREENSHOT_DIR)
        await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/public-source-${width}-${colorScheme}.png`, fullPage: true });
      await nextPlaylistOptions(dialog);
      await dialog.getByRole("button", { name: /^Import (?:\d+ songs|playlist)$/, exact: true }).click();
      await expect(dialog).toBeHidden();
      expect(created).toMatchObject({ providerAccountId: null, sourceProviderId: "spotify",
        sourcePlaylistId: "https://open.spotify.com/playlist/public-mix?si=discard", importMode: "oneTime" });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    });
  }
}

test("Liked Songs uses the selected Spotify account", async ({ page }) => {
  await mockApi(page);
  let created: Record<string, unknown> | undefined;
  await page.route("**/api/admin/playlist-links", (route) => {
    if (route.request().method() !== "POST") return route.fallback();
    created = route.request().postDataJSON();
    return route.fulfill({ contentType: "application/json", body: JSON.stringify({ id: "liked-link" }) });
  });
  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Import playlist" }).click();
  const dialog = page.getByRole("dialog", { name: "Import a playlist" });
  await openOtherPlaylistSources(dialog);
  await dialog.getByRole("radio", { name: /Spotify/ }).check();
  await dialog.getByRole("radio", { name: /Liked Songs/ }).check();
  await nextPlaylistOptions(dialog);
  await dialog.getByRole("button", { name: /^Import (?:\d+ songs|playlist)$/, exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(created).toMatchObject({ providerAccountId: "spotify", sourceProviderId: "spotify", sourcePlaylistId: "liked-songs" });
});

for (const width of [390, 1280]) {
  for (const colorScheme of ["light", "dark"] as const) {
    test(`Public Spotify user playlists at ${width}px ${colorScheme}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 844 });
      await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
      await mockApi(page);
      await page.route("**/api/admin/playlist-sources", (route) => route.fulfill({
        contentType: "application/json", body: JSON.stringify({ accounts: [], blockedAccounts: [],
          providers: [{ id: "spotify", displayName: "Spotify", supportsPublicRead: true }] }),
      }));
      const requests: URL[] = [];
      await page.route("**/api/admin/playlist-sources/public/spotify/playlists?*", (route) => {
        const url = new URL(route.request().url());
        requests.push(url);
        const more = url.searchParams.has("cursor");
        return route.fulfill({ contentType: "application/json", body: JSON.stringify({
          items: [{ id: more ? "second" : "first", name: more ? "Followed public mix" : "First public mix", owner: "Profile owner" }],
          nextCursor: more ? null : "1", isPartial: !more,
        }) });
      });
      let created: Record<string, unknown> | undefined;
      await page.route("**/api/admin/playlist-links", (route) => {
        if (route.request().method() !== "POST") return route.fallback();
        created = route.request().postDataJSON();
        return route.fulfill({ contentType: "application/json", body: JSON.stringify({ id: "profile-link" }) });
      });
      await page.goto("#/library/playlists");
      await page.getByRole("button", { name: "Import playlist" }).click();
      const dialog = page.getByRole("dialog", { name: "Import a playlist" });
      await openOtherPlaylistSources(dialog);
      await dialog.getByRole("radio", { name: /Public Spotify playlist/ }).check();
      await dialog.getByRole("searchbox", { name: "Spotify user link, URI or ID" }).fill("spotify:user:profile-owner");
      await dialog.getByRole("button", { name: "Browse playlists" }).click();
      await expect(dialog.getByRole("radio", { name: /First public mix/ })).toBeVisible();
      await dialog.getByRole("button", { name: "Load more" }).click();
      await dialog.getByRole("radio", { name: /Followed public mix/ }).check();
      expect(requests).toHaveLength(2);
      expect(requests[0].searchParams.get("sourceUserId")).toBe("spotify:user:profile-owner");
      expect(requests[1].searchParams.get("cursor")).toBe("1");
      expect(await dialog.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(true);
      if (process.env.ALLSTARR_SCREENSHOT_DIR)
        await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/public-profile-${width}-${colorScheme}.png`, fullPage: true });
      await nextPlaylistOptions(dialog);
      await dialog.getByRole("button", { name: /^Import (?:\d+ songs|playlist)$/, exact: true }).click();
      await expect(dialog).toBeHidden();
      expect(created).toMatchObject({ providerAccountId: null, sourceProviderId: "spotify", sourcePlaylistId: "second" });
    });
  }
}

test("Public Spotify profile edits invalidate stale discovery and selection", async ({ page }) => {
  await mockApi(page);
  await page.route("**/api/admin/playlist-sources", (route) => route.fulfill({
    contentType: "application/json", body: JSON.stringify({ accounts: [], blockedAccounts: [],
      providers: [{ id: "spotify", displayName: "Spotify", supportsPublicRead: true }] }),
  }));
  let releaseOld!: () => void;
  const oldReady = new Promise<void>((resolve) => { releaseOld = resolve; });
  let sawOld!: () => void;
  const oldStarted = new Promise<void>((resolve) => { sawOld = resolve; });
  await page.route("**/api/admin/playlist-sources/public/spotify/playlists?*", async (route) => {
    const old = new URL(route.request().url()).searchParams.get("sourceUserId") === "old-user";
    if (old) { sawOld(); await oldReady; }
    return route.fulfill({ contentType: "application/json", body: JSON.stringify({
      items: [{ id: old ? "old-mix" : "new-mix", name: old ? "Stale public mix" : "Current public mix", owner: "Owner" }], isPartial: false,
    }) });
  });
  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Import playlist" }).click();
  const dialog = page.getByRole("dialog", { name: "Import a playlist" });
  await openOtherPlaylistSources(dialog);
  await dialog.getByRole("radio", { name: /Public Spotify playlist/ }).check();
  const user = dialog.getByRole("searchbox", { name: "Spotify user link, URI or ID" });
  await user.fill("old-user");
  await dialog.getByRole("button", { name: "Browse playlists" }).click();
  await oldStarted;
  await user.fill("new-user");
  await dialog.getByRole("button", { name: "Browse playlists" }).click();
  await dialog.getByRole("radio", { name: /Current public mix/ }).check();
  await expect(dialog.getByRole("button", { name: "Next" })).toBeEnabled();
  const oldResponse = page.waitForResponse((response) => new URL(response.url()).searchParams.get("sourceUserId") === "old-user");
  releaseOld();
  await (await oldResponse).finished();
  await expect(dialog.getByRole("radio", { name: /Stale public mix/ })).toHaveCount(0);
  await expect(dialog.getByRole("radio", { name: /Current public mix/ })).toBeChecked();
  await user.fill("third-user");
  await expect(dialog.getByRole("radio", { name: /Current public mix/ })).toHaveCount(0);
  await expect(dialog.getByRole("button", { name: "Next" })).toBeDisabled();
});

for (const width of [390, 1280]) {
  for (const colorScheme of ["light", "dark"] as const) {
    test(`URL import preview at ${width}px ${colorScheme}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 844 });
      await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
      await mockApi(page);
      if (width === 390) await page.route("**/api/admin/playlist-sources", (route) => route.fulfill({
        contentType: "application/json", body: JSON.stringify({ accounts: [], blockedAccounts: [], providers: [] }),
      }));
      await page.route("**/api/admin/playlist-links/resolve-url", (route) => {
        expect(route.request().postDataJSON()).toEqual({ url: "https://tidal.com/browse/playlist/morning" });
        return route.fulfill({ contentType: "application/json", body: JSON.stringify({
          source: { id: "tidal-fixture", displayName: "Tidal" }, kind: "playlist", id: "morning",
          providerAccountId: null, title: "Morning listening", owner: "A curator", trackCount: 42,
          artworkUrl: null,
        }) });
      });
      const importMode = colorScheme === "dark" ? "linked" : "oneTime";
      const existing = width === 1280;
      await page.route("**/api/admin/playlist-links/imported/schedules", (route) => route.fulfill({
        contentType: "application/json", body: JSON.stringify({ id: "schedule" }),
      }));
      let created: Record<string, unknown> | undefined;
      await page.route("**/api/admin/playlist-links", (route) => {
        if (route.request().method() !== "POST") return route.fallback();
        created = route.request().postDataJSON();
        return route.fulfill({ contentType: "application/json", body: JSON.stringify({ id: "imported" }) });
      });
      await page.goto("#/library/playlists");
      await page.getByRole("button", { name: "Import playlist" }).click();
      const dialog = page.getByRole("dialog", { name: "Import a playlist" });
      await dialog.getByRole("tab", { name: "Paste a link", exact: true }).click();
      const input = dialog.getByRole("textbox", { name: "Paste a link" });
      await expect(input).toHaveAttribute("placeholder", "Paste a playlist or album link");
      if (colorScheme === "dark") {
        await input.evaluate((element) => {
          const data = new DataTransfer();
          data.setData("text", "https://tidal.com/browse/playlist/morning");
          element.dispatchEvent(new ClipboardEvent("paste", { clipboardData: data, bubbles: true, cancelable: true }));
        });
      } else {
        await input.fill("https://tidal.com/browse/playlist/morning");
        await input.press("Enter");
      }
      await expect(dialog.getByText("Morning listening")).toBeVisible();
      await expect(dialog.getByText("Playlist · 42 songs · from Tidal")).toBeVisible();
      await expect(dialog.getByText("A curator", { exact: true })).toBeVisible();
      await expect(dialog.getByText("Connect your music account first")).toHaveCount(0);
      expect(await input.evaluate((element) => getComputedStyle(element).fontSize)).toBe("16px");
      expect(await dialog.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(true);
      await page.screenshot({ path: `test-results/urlimport-${width}-${colorScheme}.png`, fullPage: true });
      await nextPlaylistOptions(dialog);
      await dialog.getByRole("radio", { name: importMode === "linked" ? /Keep in sync/ : /Import once/ }).check();
      await nextPlaylistOptions(dialog);
      if (existing) {
        await dialog.getByRole("radio", { name: /Existing playlist/ }).check();
        await dialog.getByRole("radio", { name: /Road trip/ }).check();
      }
      await nextPlaylistOptions(dialog);
      await dialog.getByRole("checkbox", { name: /Keep downloaded copies/ }).check();
      await dialog.getByRole("button", { name: /^Import (?:\d+ songs|playlist)$/, exact: true }).click();
      await expect(dialog).toBeHidden();
      expect(created).toMatchObject({ providerAccountId: null, sourceProviderId: "tidal-fixture",
        sourcePlaylistId: "morning", importMode, targetPlaylistId: existing ? "jellyfin-playlist" : null, trackRetention: "keepAll" });
    });
  }
}

for (const message of ["No source can read this link.", "Turn on Tidal to import from it.", "Albums are not supported yet."]) {
  test(`URL import inline error: ${message}`, async ({ page }) => {
    await mockApi(page);
    await page.route("**/api/admin/playlist-links/resolve-url", (route) => route.fulfill({
      status: 400, contentType: "application/json", body: JSON.stringify({ error: message }),
    }));
    await page.goto("#/library/playlists");
    await page.getByRole("button", { name: "Import playlist" }).click();
    const dialog = page.getByRole("dialog", { name: "Import a playlist" });
    await dialog.getByRole("tab", { name: "Paste a link", exact: true }).click();
    const input = dialog.getByRole("textbox", { name: "Paste a link" });
    await input.fill("https://tidal.com/album/42");
    await input.press("Enter");
    await expect(dialog.getByRole("alert")).toContainText(message);
    await expect(dialog.getByRole("button", { name: "Next" })).toBeDisabled();
  });
}

test("URL import ignores a preview after the pasted link is edited", async ({ page }) => {
  await mockApi(page);
  const response = routeRelease();
  await page.route("**/api/admin/playlist-links/resolve-url", async (route) => {
    await response.promise;
    return route.fulfill({ contentType: "application/json", body: JSON.stringify({
      source: { id: "spotify", displayName: "Spotify" }, kind: "playlist", id: "old",
      providerAccountId: null, title: "Old preview", owner: "Curator", trackCount: 1, artworkUrl: null,
    }) });
  });
  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Import playlist" }).click();
  const dialog = page.getByRole("dialog", { name: "Import a playlist" });
  await dialog.getByRole("tab", { name: "Paste a link", exact: true }).click();
  const input = dialog.getByRole("textbox", { name: "Paste a link" });
  await input.fill("https://open.spotify.com/playlist/old");
  const requested = page.waitForRequest("**/api/admin/playlist-links/resolve-url");
  await input.press("Enter");
  await requested;
  await input.fill("https://open.spotify.com/playlist/new");
  response.release();
  await expect(dialog.getByText("Old preview")).toHaveCount(0);
  await expect(dialog.getByRole("button", { name: "Next" })).toBeDisabled();
});

test("URL import keeps destinations when a link is pasted while sources load", async ({ page }) => {
  await mockApi(page);
  const sources = routeRelease();
  await page.route("**/api/admin/playlist-sources", async (route) => {
    await sources.promise;
    return route.fulfill({ contentType: "application/json", body: JSON.stringify(responses["/api/admin/playlist-sources"]) });
  });
  await page.route("**/api/admin/playlist-links/resolve-url", (route) => route.fulfill({
    contentType: "application/json", body: JSON.stringify({
      source: { id: "spotify", displayName: "Spotify" }, kind: "playlist", id: "mix",
      providerAccountId: null, title: "Quick preview", owner: "Curator", trackCount: 1, artworkUrl: null,
    }),
  }));
  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Import playlist" }).click();
  const dialog = page.getByRole("dialog", { name: "Import a playlist" });
  await dialog.getByRole("tab", { name: "Paste a link", exact: true }).click();
  const input = dialog.getByRole("textbox", { name: "Paste a link" });
  await input.fill("https://open.spotify.com/playlist/mix");
  await input.press("Enter");
  await expect(dialog.getByText("Quick preview")).toBeVisible();
  sources.release();
  await expect(dialog.getByRole("button", { name: "Next" })).toBeEnabled();
  await nextPlaylistOptions(dialog);
  await nextPlaylistOptions(dialog);
  await expect(dialog.getByRole("radio", { name: /New playlist/ })).toBeChecked();
  await expect(dialog.getByRole("button", { name: "Import 1 song", exact: true })).toBeEnabled();
});

test("URL import keeps a later browsing choice when its preview arrives", async ({ page }) => {
  await mockApi(page);
  const response = routeRelease();
  await page.route("**/api/admin/playlist-links/resolve-url", async (route) => {
    await response.promise;
    return route.fulfill({ contentType: "application/json", body: JSON.stringify({
      source: { id: "spotify", displayName: "Spotify" }, kind: "playlist", id: "old",
      providerAccountId: null, title: "Old preview", owner: "Curator", trackCount: 1, artworkUrl: null,
    }) });
  });
  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Import playlist" }).click();
  const dialog = page.getByRole("dialog", { name: "Import a playlist" });
  await dialog.getByRole("tab", { name: "Paste a link", exact: true }).click();
  const input = dialog.getByRole("textbox", { name: "Paste a link" });
  await input.fill("https://open.spotify.com/playlist/old");
  const requested = page.waitForRequest("**/api/admin/playlist-links/resolve-url");
  await input.press("Enter");
  await requested;
  await expect(dialog.getByRole("searchbox", { name: "Playlist link, URI or ID" })).toHaveCount(0);
  await dialog.getByRole("tab", { name: "From my accounts", exact: true }).click();
  await openOtherPlaylistSources(dialog);
  await dialog.getByRole("radio", { name: /Spotify/ }).check();
  await dialog.getByRole("radio", { name: /Source Mix/ }).check();
  response.release();
  await expect(dialog.getByText("Old preview")).toHaveCount(0);
  await expect(dialog.getByRole("radio", { name: /Source Mix/ })).toBeChecked();
  await expect(dialog.getByRole("button", { name: "Next" })).toBeEnabled();
});
