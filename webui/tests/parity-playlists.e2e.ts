import { expect, test } from "@playwright/test";
import { responses, routeRelease, mockApi, routes } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

for (const width of [390, 1280]) {
  for (const theme of ["light", "dark"] as const) {
    test(`Playlist detail distinguishes streamed, local, duplicate and unresolved rows at ${width} in ${theme}`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width, height: 844 });
      await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
      await page.addInitScript((value) => localStorage.setItem("allstarr.theme", value), theme);
      await mockApi(page);
      const rows = [
        { title: "Streamed song", routeKind: "external", routeProviderId: "apple-musickit",
          matchState: "accepted", targetStatus: "streamed", outcomeCode: "streamed_through_allstarr",
          targetEligible: false, providerRoutes: [{ providerId: "apple-musickit", externalId: "12345", pinned: false },
            { providerId: "apple-download", externalId: "12345", pinned: false }] },
        { title: "Review song", routeKind: "external", routeProviderId: "apple-musickit",
          matchState: "ambiguous", targetStatus: "streamed", outcomeCode: "streamed_through_allstarr",
          targetEligible: false, providerRoutes: [{ providerId: "apple-musickit", externalId: "suggested", pinned: false }] },
        { title: "Local song", routeKind: "local", matchState: "accepted", targetStatus: "included",
          outcomeCode: "included_native_backend_item", targetEligible: true, providerRoutes: [] },
        { title: "Duplicate song", routeKind: "local", matchState: "accepted", targetStatus: "duplicate",
          outcomeCode: "skipped_duplicate", targetEligible: false, providerRoutes: [] },
        { title: "Unresolved song", routeKind: "unresolved", matchState: "unresolved", targetStatus: "unresolved",
          outcomeCode: "skipped_unresolved", targetEligible: false, providerRoutes: [] },
      ].map((row, index) => ({ ...row, sourcePosition: index, position: index + 1,
        externalSnapshotId: `snapshot-${index}`, artists: ["Artist"], durationMs: 180_000 }));
      await page.route((url) => url.pathname === "/api/admin/playlist-links/playlist-link", (route) => route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({
          id: "playlist-link", snapshotId: "playlist-snapshot", snapshotVersion: 1,
          name: "Test playlist", sourceProviderId: "spotify", targetProtocol: "jellyfin",
          targetPlaylistId: "jellyfin-playlist", projectionMode: "resolved", retrievedAt: "2026-10-16",
          trackCount: 5, localCount: 2, externalCount: 2, unresolvedCount: 1,
          matchedCount: 3, reviewCount: 1, rejectedCount: 0, playableCount: 4,
          durationMs: 720_000, unknownDurationCount: 0, routeCoverage: [], tracks: rows,
          clientProjection: { protocolId: "allstarr-vpl-playlist-link", projectionMode: "resolved",
            trackCount: 5, tracks: rows.map((row) => ({ ...row, itemId: `item-${row.sourcePosition}` })) },
        }),
      }));
      await page.goto("#/library/playlists");
      await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
      const details = page.getByRole("dialog", { name: "Test playlist" });
      await expect(details.locator("tbody tr")).toHaveCount(5);
      const streamed = details.getByRole("row").filter({ hasText: "Streamed song" });
      await expect(streamed).toContainText("Streams through Allstarr");
      await expect(streamed).not.toContainText(/unresolved|failed|no playable match/i);
      await expect(details.getByRole("row").filter({ hasText: "Local song" })).toContainText("In your library");
      await expect(details.getByRole("row").filter({ hasText: "Duplicate song" })).toContainText("Duplicate · not added twice");
      await expect(details.getByRole("row").filter({ hasText: "Unresolved song" })).toContainText("Not playable");
      await streamed.getByRole("button", { name: "Routes for Streamed song" }).click();
      const mapped = page.getByRole("list", { name: "Mapped providers for Streamed song" });
      await expect(mapped.getByRole("listitem")).toHaveCount(1);
      await expect(mapped).toContainText("Apple Music");
      await expect(mapped).toContainText("Automatically matched · 2 sources");
      const tile = await mapped.locator(".route-tile").first().boundingBox();
      expect(Math.round(tile!.width)).toBe(32); expect(Math.round(tile!.height)).toBe(32);
      if (process.env.ALLSTARR_SCREENSHOT_DIR)
        await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/track-routes-${width}-${theme}.png` });
      await page.keyboard.press("Escape");
      await details.getByRole("row").filter({ hasText: "Review song" }).getByRole("button", { name: "Routes for Review song" }).click();
      const suggested = page.getByRole("list", { name: "Mapped providers for Review song" });
      await expect(suggested).toContainText("Suggested source — check recording");
      await expect(suggested).not.toContainText(/Verified match|Automatically matched/);
      await page.keyboard.press("Escape");
      const labels = ["Streams through Allstarr", "Streams through Allstarr", "Will be included in Jellyfin playlist",
        "Duplicate: not added twice to Jellyfin playlist", "Not added to Jellyfin playlist: no playable match yet"];
      for (const [index, row] of rows.entries()) {
        await details.getByRole("button", { name: `Technical details for ${row.title}`, exact: true }).click();
        await expect(page.locator(".track-technical")).toContainText(labels[index]);
        await page.keyboard.press("Escape");
      }
      if (width === 1280 && theme === "dark") {
        await page.evaluate(async () => { await document.fonts.ready; });
        await page.screenshot({ path: testInfo.outputPath("playlist-panel-default-1280-dark.png") });
        await details.getByRole("button", { name: "Expand", exact: true }).click();
        await expect(details).toHaveAttribute("data-expanded", "true");
        await details.locator(".track-data-table").scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath("playlist-panel-expanded-1280-dark.png") });
        await details.getByRole("button", { name: "Restore", exact: true }).click();
      }
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      await testInfo.attach("playlist-row-outcomes", { body: await page.screenshot(), contentType: "image/png" });
    });
  }
}

test("Served playlist settings keep the playlist in apps", async ({ page }) => {
  await mockApi(page);
  await page.route("**/api/admin/playlist-links", route => {
    if (route.request().method() !== "GET") return route.fallback();
    const response = responses["/api/admin/playlist-links"] as { playlistLinks: Record<string, unknown>[] };
    return route.fulfill({ json: { playlistLinks: response.playlistLinks.map(item => ({
      ...item, mode: "virtual", targetPlaylistId: null,
    })) } });
  });
  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
  const details = page.getByRole("dialog", { name: "Test playlist" });
  await details.getByRole("button", { name: "Actions" }).click();
  await page.getByRole("menuitem", { name: "Edit settings" }).click();
  const settings = page.getByRole("dialog", { name: "Edit playlist settings" });
  const update = page.waitForRequest(request => request.method() === "PUT" && request.url().endsWith("/api/admin/playlist-links/playlist-link"));
  await settings.getByRole("button", { name: "Save settings" }).click();
  expect((await update).postDataJSON()).toMatchObject({ mode: "virtual", targetPlaylistId: null });
  await expect(settings).toBeHidden();
});

test("Playlist views and revisioned settings stay keyboard-safe", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await mockApi(page);
  let listRevision = 1;
  await page.route("**/api/admin/playlist-links", (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    const response = responses["/api/admin/playlist-links"] as { playlistLinks: Record<string, unknown>[] };
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ playlistLinks: response.playlistLinks.map((item) => ({ ...item, revision: listRevision })) }),
    });
  });
  await page.route("**/api/admin/media-targets", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({ targets: [
      {
        id: "wrong-credential", protocol: "jellyfin", backendInstanceId: "main",
        credentialReferenceId: "wrong", displayName: "Wrong credential",
      },
      ...(responses["/api/admin/media-targets"] as { targets: Record<string, unknown>[] }).targets,
    ] }),
  }));
  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
  const details = page.getByRole("dialog", { name: "Test playlist" });
  await expect(details).toBeVisible();
  await expect(details.getByRole("tab", { name: /Mapped|Original|Native/ })).toHaveCount(0);
  await expect(details.getByText("Test song", { exact: true })).toBeVisible();
  await expect(details).toHaveCSS("overflow", "clip");
  await expect(details.locator(".track-scroll")).toHaveCSS("overflow", "auto");
  await expect(details.locator(".sync-settings .locked")).toHaveText("Off");

  const exactTarget = page.waitForRequest((request) =>
    request.url().includes("/api/admin/media-targets/22222222-2222-2222-2222-222222222222/playlists"));
  await details.getByRole("button", { name: "Actions" }).click();
  await page.getByRole("menuitem", { name: "Edit settings" }).click();
  let settings = page.getByRole("dialog", { name: "Edit playlist settings" });
  await exactTarget;
  const newTarget = settings.getByRole("radio", { name: /New playlist/ });
  await newTarget.focus();
  await page.keyboard.press("Space");
  await expect(newTarget).toBeChecked();
  const update = page.waitForRequest((request) =>
    request.method() === "PUT" && request.url().endsWith("/api/admin/playlist-links/playlist-link"));
  await settings.getByRole("button", { name: "Save settings" }).click();
  expect((await update).postDataJSON()).toMatchObject({
    expectedRevision: 1,
    mode: "hybrid",
    projectionMode: "resolved",
    materializationMode: "reconcile",
    mirrorStaleEntries: false,
    preserveManualEntries: true,
    syncName: true,
    syncDescription: true,
    syncArtwork: true,
    targetPlaylistId: null,
  });
  await expect(settings).toBeHidden();

  await page.route("**/api/admin/playlist-links/playlist-link", async (route) => {
    if (route.request().method() === "PUT") {
      await route.fulfill({
        status: 409,
        contentType: "application/json",
        body: JSON.stringify({ error: "The resource changed before this update" }),
      });
      return;
    }
    await route.fallback();
  });
  await details.getByRole("button", { name: "Actions" }).click();
  await page.getByRole("menuitem", { name: "Edit settings" }).click();
  settings = page.getByRole("dialog", { name: "Edit playlist settings" });
  await settings.getByRole("button", { name: "Save settings" }).click();
  await expect(settings.getByRole("alert")).toContainText("resource changed");
  listRevision = 2;
  await page.locator(".library-actions").getByRole("button", { name: "Refresh playlists" })
    .evaluate((button) => (button as HTMLButtonElement).click());
  await expect(settings.getByRole("alert")).toContainText("changed while you were editing");
  await expect(settings.getByRole("button", { name: "Save settings" })).toBeDisabled();
  await page.keyboard.press("Escape");
  await expect(settings).toBeHidden();
  await expect(details).toBeVisible();

  await details.getByRole("button", { name: "Close" }).click();
  let projectionUnavailable = true;
  await page.route("**/api/admin/playlist-links/playlist-link?projectionMode=resolved", (route) => projectionUnavailable
    ? route.fulfill({ status: 503, contentType: "application/json", body: JSON.stringify({ error: "Projection fixture unavailable" }) })
    : route.fallback());
  await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
  const projectionError = details.getByRole("alert");
  await expect(projectionError).toContainText("Projection fixture unavailable");
  projectionUnavailable = false;
  await projectionError.getByRole("button", { name: "Try again" }).click();
  await expect(details.getByText("Test song", { exact: true })).toBeVisible();
});

for (const viewport of [
  { width: 390, height: 844 },
  { width: 820, height: 900 },
  { width: 1280, height: 800 },
]) {
test(`Provider playlist update names both playlists and requires the checked confirmation at ${viewport.width}px`, async ({ page }) => {
  await page.setViewportSize(viewport);
  await mockApi(page);
  const response = responses["/api/admin/playlist-links"] as { playlistLinks: Record<string, unknown>[] };
  await page.route("**/api/admin/playlist-links", (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        playlistLinks: response.playlistLinks.map((item) => ({
          ...item,
          sourceProviderId: "spotify",
          sourceUpdateAvailable: true,
        })),
      }),
    });
  });

  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
  const details = page.getByRole("dialog", { name: "Test playlist" });
  await details.getByRole("button", { name: "Actions" }).click();
  await page.getByRole("menuitem", { name: "Preview changes to Spotify" }).click();

  const preview = page.getByRole("dialog", { name: "Update Spotify?" });
  await expect(preview).toBeVisible();
  await expect(preview).toContainText("Allstarr will update “Road trip source” in Spotify to match “Road trip” in Jellyfin.");
  await expect(preview).toContainText("Allstarr will not change “Road trip” in Jellyfin.");
  await expect(preview).toContainText("If either changes before this runs, nothing will be updated.");
  await expect(preview.getByRole("definition")).toHaveCount(4);
  await expect(preview.getByText("Local only")).toBeVisible();
  await expect(preview).not.toContainText(/write-back|hybrid|native playlist/i);
  await expect(preview.locator(".playlist-source-update-body")).toHaveCSS("overflow", "auto");
  await expect(preview.getByRole("button", { name: "Update Spotify" })).toBeInViewport();
  if (process.env.ALLSTARR_SCREENSHOT_DIR)
    await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/playlist-${viewport.width}-update-spotify.png` });

  const request = page.waitForRequest((item) =>
    item.method() === "POST" && item.url().endsWith("/api/admin/playlist-links/playlist-link/source-update/apply"));
  await preview.getByRole("button", { name: "Update Spotify" }).click();
  expect((await request).postDataJSON()).toEqual({
    expectedRevision: 1,
    confirmationId: "a".repeat(64),
  });
  await expect(preview).toBeHidden();
  await expect(page.getByRole("status").filter({ hasText: "Spotify update queued." })).toBeVisible();
});
}

test("Playlist track artwork keeps a placeholder while loading or unavailable", async ({ page }) => {
  const options = { trackArtworkUrl: null as string | null };
  await mockApi(page, options);
  await page.goto("#/library/playlists");
  const openPlaylist = page.getByRole("button", { name: "Open Test playlist playlist details" });
  await openPlaylist.click();
  const artwork = page.getByRole("dialog", { name: "Test playlist" }).locator(".track-art");
  await expect(artwork.locator(":scope > span")).toBeVisible();
  await expect(artwork.locator("img")).toHaveCount(0);

  options.trackArtworkUrl = "/playlist-track-artwork";
  const image = routeRelease();
  await page.route("**/playlist-track-artwork", async route => {
    await image.promise;
    await route.fulfill({ contentType: "image/svg+xml", body: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><path fill="red" d="M0 0h32v32H0z"/></svg>' });
  });
  await page.reload();
  await openPlaylist.click();
  await artwork.scrollIntoViewIfNeeded();
  try {
    await expect(artwork.locator(":scope > span")).toBeVisible();
  } finally { image.release(); }
  await expect(artwork.locator("img")).toBeVisible();
  await expect(artwork.locator(":scope > span")).toHaveCount(0);

  options.trackArtworkUrl = "/unavailable-track-artwork";
  await page.route("**/unavailable-track-artwork", route => route.fulfill({ status: 404 }));
  await page.reload();
  await openPlaylist.click();
  await artwork.scrollIntoViewIfNeeded();
  await expect(artwork.locator(":scope > span")).toBeVisible();
  await expect(artwork.locator("img")).toHaveCount(0);
});

test("Playlist details use a responsive dialog and track rows open mapping review", async ({ page }) => {
  await page.setViewportSize({ width: 835, height: 762 });
  await mockApi(page);
  await page.goto("#/library/playlists");
  await expect.poll(async () => (await page.locator(".library-list").boundingBox())?.height ?? Infinity).toBeLessThan(450);
  await expect(page.locator(".playlist-row")).toHaveCount(1);
  await expect(page.locator(".desktop-navigation").getByRole("link", { name: "Library" }).locator("svg"))
    .toBeVisible();
  await expect(page.locator(".desktop-navigation").getByRole("link", { name: "Library" }).locator("use"))
    .toHaveCount(0);
  const refresh = page.waitForRequest((item) =>
    item.method() === "POST" && item.url().endsWith("/api/admin/playlist-links/playlist-link/refresh"));
  await page.getByRole("button", { name: "Refresh playlists" }).click();
  await refresh;
  await expect(page.getByText(/1 playlist refreshed in/)).toBeVisible();
  await expect(page.locator(".playlist-row").getByText("Needs a look", { exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Playlist status" })).toContainText("1 playlist");
  const rematchPreview = page.waitForRequest((item) =>
    item.method() === "GET" && item.url().endsWith("/api/admin/playlist-links/rematch/preview"));
  await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
  await page.getByRole("button", { name: "Actions", exact: true }).click();
  await page.getByRole("menuitem", { name: "Review account rematch", exact: true }).click();
  await rematchPreview;
  const rematchDialog = page.getByRole("dialog", { name: "Review full rematch" });
  await expect(rematchDialog.getByText("1 track needs review")).toBeVisible();
  await expect(rematchDialog.getByText("Generic external").locator("..")).toContainText("0");
  await rematchDialog.getByRole("checkbox", { name: /I reviewed these counts/ }).check();
  const rematchApply = page.waitForRequest((item) =>
    item.method() === "POST" && item.url().endsWith("/api/admin/playlist-links/rematch/apply"));
  await rematchDialog.getByRole("button", { name: "Rematch 1 track" }).click();
  await rematchApply;
  await expect(page.getByText("Controlled rematch queued.")).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  const openPlaylist = page.getByRole("button", { name: "Open Test playlist playlist details", exact: true });
  await expect(openPlaylist).toBeVisible();
  await expect(page.locator(".playlist-row").getByText("Needs a look", { exact: true })).toBeVisible();
  await openPlaylist.click();
  const dialog = page.getByRole("dialog", { name: "Test playlist" });
  await expect(dialog).toBeVisible();
  await expect.poll(async () => (await dialog.boundingBox())?.width ?? 0).toBe(390);
  await expect(dialog.locator(".hero-art > span")).toBeVisible();
  await expect(dialog.locator(".track-art > span")).toBeVisible();
  await expect(dialog.getByRole("table", { name: "Test playlist tracks" })).toBeVisible();
  await expect(dialog.locator(".coverage-bar")).toHaveCount(0);
  await expect(dialog).not.toContainText("Where songs play from");
  await expect(dialog).not.toContainText("Songs keep their original order.");
  await expect(dialog.getByText("Needs review", { exact: true })).toBeVisible();
  await expect(dialog.getByText("No automatic updates", { exact: true })).toBeVisible();
  await dialog.getByRole("button", { name: "Show 1 track needing review" }).click();
  await expect(dialog.getByRole("button", { name: "Track route" })).toContainText("To review (1)");
  const mobileReview = dialog.getByRole("button", { name: "Open mapping details for Test song" });
  await expect.poll(async () => (await mobileReview.boundingBox())?.width ?? 0).toBeGreaterThanOrEqual(44);
  await mobileReview.click();
  const mobileMatch = page.getByRole("dialog", { name: "Test song" });
  await expect(mobileMatch).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(mobileMatch).toBeHidden();
  await expect(dialog).toBeVisible();
  await expect(mobileReview).toBeFocused();
  await dialog.getByRole("button", { name: "Close" }).click();
  await openPlaylist.click();
  await expect(dialog.getByRole("button", { name: "Track route" })).toContainText("All routes");
  await expect(dialog.getByRole("button", { name: "Actions" })).toBeInViewport();
  await dialog.getByRole("button", { name: "Actions" }).click();
  await expect(page.getByRole("menuitem", { name: "Update playlist now" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Review account rematch" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Refresh source" })).toBeVisible();
  await page.keyboard.press("Escape");
  await page.setViewportSize({ width: 1280, height: 800 });
  await expect.poll(async () => (await dialog.boundingBox())?.width ?? 0).toBe(420);
  await expect(dialog.getByText(/Snapshot v/)).toHaveCount(0);
  await expect(dialog.getByText("Last target sync")).toHaveCount(0);
  await expect(dialog.getByText("Current published snapshot")).toHaveCount(0);
  await expect(dialog.getByRole("button", { name: /Operation details:/ })).toBeVisible();
  await expect(dialog.getByRole("columnheader", { name: "Artist" })).toBeVisible();
  await expect(dialog.getByRole("columnheader", { name: "Album" })).toBeVisible();
  await dialog.getByRole("button", { name: /Choose track columns/ }).click();
  await page.locator(".track-column-picker").getByLabel("Album", { exact: true }).uncheck();
  await expect(dialog.getByRole("columnheader", { name: "Album" })).toHaveCount(0);
  await page.keyboard.press("Escape");
  const density = await dialog.evaluate((element) => {
    const bounds = element.getBoundingClientRect();
    const overview = element.querySelector<HTMLElement>(".playlist-overview")!;
    const overviewBounds = overview.getBoundingClientRect();
    const trackScroll = element.querySelector(".track-scroll")!.getBoundingClientRect();
    const table = element.querySelector(".track-data-table")!.getBoundingClientRect();
    const facts = [...overview.querySelectorAll(".playlist-facts > div")]
      .map((child) => child.getBoundingClientRect());
    return {
      dialogHeight: bounds.height,
      blankTrackTail: trackScroll.height - table.height,
      overviewFits: overview.scrollWidth <= overview.clientWidth &&
        facts.every((fact) => fact.left >= overviewBounds.left - 1 && fact.right <= overviewBounds.right + 1),
    };
  });
  expect(density.dialogHeight).toBeLessThanOrEqual(800);
  expect(density.blankTrackTail).toBeLessThan(48);
  expect(density.overviewFits).toBe(true);
  await dialog.getByRole("button", { name: "Technical details for Test song" }).click();
  await expect(page).toHaveURL(/#\/library\/playlists$/);
  const trackDetails = page.locator(".track-details-menu");
  const reportSong = trackDetails.getByRole("link", { name: "Report a problem", exact: true });
  await expect(reportSong).toHaveAttribute("href", /#\/report\?snapshot=snapshot&from=%2Flibrary%2Fplaylists/);
  const reviewMatch = trackDetails.getByRole("button", { name: "Review match" });
  await expect(reviewMatch).toBeVisible();
  await expect.poll(() => trackDetails.evaluate((panel) => {
    const bounds = panel.getBoundingClientRect();
    return panel.contains(document.elementFromPoint(bounds.left + bounds.width / 2, bounds.top + 8));
  })).toBe(true);
  await reviewMatch.click();
  await expect(trackDetails).toBeHidden();
  const reviewedMatch = page.getByRole("dialog", { name: "Test song" });
  await expect(reviewedMatch).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(reviewedMatch).toBeHidden();
  await expect(dialog.getByRole("button", { name: "Technical details for Test song" })).toBeFocused();
  await dialog.getByRole("button", { name: "Open mapping details for Test song" }).focus();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/#\/library\/playlists$/);
  await expect(page.getByRole("dialog", { name: "Test song" })).toBeVisible();
});

test("Playlist dialog keeps Actions and close controls independent at desktop and mobile widths", async ({ page }) => {
  await mockApi(page);
  for (const viewport of [{ width: 320, height: 844 }, { width: 390, height: 844 }, { width: 1280, height: 800 }]) {
    await page.setViewportSize(viewport);
    await page.goto("#/library/playlists");
    const opener = page.getByRole("button", { name: "Open Test playlist playlist details", exact: true });
    await opener.click();
    const dialog = page.getByRole("dialog", { name: "Test playlist" });
    const actions = dialog.getByRole("button", { name: "Actions" });
    const close = dialog.getByRole("button", { name: "Close" });
    await expect(actions).toBeVisible();
    await expect(close).toBeVisible();
    await expect(close).toHaveAttribute("aria-label", "Close");

    const { actionBox, closeBox } = await dialog.evaluate(element => ({
      actionBox: element.querySelector('[aria-label="Actions"]')!.getBoundingClientRect().toJSON(),
      closeBox: element.querySelector('[aria-label="Close"]')!.getBoundingClientRect().toJSON(),
    }));
    expect(actionBox!.y + actionBox!.height <= closeBox!.y || closeBox!.y + closeBox!.height <= actionBox!.y || actionBox!.x + actionBox!.width <= closeBox!.x || closeBox!.x + closeBox!.width <= actionBox!.x).toBe(true);

    await close.focus();
    await expect(close).toBeFocused();
    await actions.click();
    await expect(page.getByRole("menuitem", { name: "Update playlist now" })).toBeVisible();
    await expect(close).toBeVisible();
    await page.keyboard.press("Escape");
    await close.click();
    await expect(dialog).toBeHidden();
    await expect(opener).toBeFocused();
  }
});

test("Passive playlist refresh keeps mutation controls enabled while detail reads finish", async ({ page }) => {
  await page.setViewportSize({ width: 835, height: 762 });
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
        data: JSON.stringify({ resource: "playlist-links", resourceId: "playlist-link", revision: 2 }),
        lastEventId: "playlist-links-2",
      };
      for (const listener of TestEventSource.instance?.listeners.get("update") ?? [])
        listener(event as unknown as Event);
    };
  });
  await mockApi(page);
  const detailRefresh = routeRelease();
  let detailRequests = 0;
  await page.route("**/api/admin/playlist-links/playlist-link?*", async (route) => {
    detailRequests++;
    if (detailRequests === 2) await detailRefresh.promise;
    return route.fallback();
  });
  let postRequests = 0;
  page.on("request", (request) => {
    if (request.method() === "POST") postRequests++;
  });

  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
  const dialog = page.getByRole("dialog", { name: "Test playlist" });
  await expect(dialog).toBeVisible();
  await expect.poll(() => detailRequests).toBe(1);

  await page.evaluate(() => window.__emitAllstarrUpdate?.());
  await expect.poll(() => detailRequests).toBe(2);
  await expect(page.getByRole("button", { name: "Refresh playlists" })).toBeEnabled();
  await expect(page.getByRole("dialog", { name: "Test playlist" }).getByRole("button", { name: "Actions", exact: true })).toBeEnabled();
  expect(postRequests).toBe(0);

  detailRefresh.release();
  await expect(dialog.getByRole("button", { name: "Close" })).toBeVisible();
});

for (const viewport of [{ width: 390, height: 844 }, { width: 1280, height: 800 }]) {
  test(`Playlist sync preserves reading position at ${viewport.width}px`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await mockApi(page);
    const first = (responses["/api/admin/playlist-links"] as {
      playlistLinks: Record<string, unknown>[];
    }).playlistLinks[0];
    await page.route("**/api/admin/playlist-links", async (route) => {
      if (route.request().method() !== "GET") return route.fallback();
      return route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({
          playlistLinks: [
            { ...first, trackCount: 60, playableCount: 60, unmatchedCount: 0 },
            { ...first, id: "playlist-link-two", name: "Second playlist", trackCount: 60, playableCount: 60, unmatchedCount: 0 },
          ],
        }),
      });
    });

    const detail = (id: string, name: string, projectionMode: "resolved" | "source" | "target") => ({
      id,
      snapshotId: `${id}-snapshot`,
      snapshotVersion: 1,
      latestSourceSnapshotVersion: 1,
      hasNewerSourceGeneration: false,
      name,
      sourceProviderId: "lumen-audio",
      projectionMode,
      targetProtocol: "jellyfin",
      retrievedAt: "2026-01-01",
      completedAt: "2026-01-01",
      trackCount: 60,
      localCount: 60,
      externalCount: 0,
      unresolvedCount: 0,
      durationMs: 10_800_000,
      matchedCount: 60,
      reviewCount: 0,
      rejectedCount: 0,
      playableCount: 60,
      routeCoverage: [{ providerId: "jellyfin", count: 60 }],
      unknownDurationCount: 0,
      clientProjection: {
        protocolId: `allstarr-vpl-${id}`,
        projectionMode,
        trackCount: 60,
        tracks: Array.from({ length: 60 }, (_, index) => ({
          position: index + 1,
          sourcePosition: index,
          itemId: `${id}-${projectionMode}-${index}`,
          title: `Track ${index + 1}`,
          artists: ["Artist"],
          album: "Album",
          durationMs: 180_000,
          routeKind: "local",
        })),
      },
      tracks: Array.from({ length: 60 }, (_, index) => ({
        sourcePosition: index,
        position: index + 1,
        externalSnapshotId: `${id}-track-${index}`,
        title: `Track ${index + 1}`,
        artists: ["Artist"],
        album: "Album",
        durationMs: 180_000,
        routeKind: "local",
        routeProviderId: "jellyfin",
        matchState: "accepted",
        providerRoutes: [],
      })),
    });
    let detailRequests = 0;
    let releaseRefresh!: () => void;
    const refreshHeld = new Promise<void>((resolve) => { releaseRefresh = resolve; });
    await page.route("**/api/admin/playlist-links/playlist-link?*", async (route) => {
      detailRequests++;
      if (detailRequests === 2) await refreshHeld;
      const mode = new URL(route.request().url()).searchParams.get("projectionMode") as "resolved" | "source" | "target";
      return route.fulfill({ contentType: "application/json", body: JSON.stringify(detail("playlist-link", "Test playlist", mode)) });
    });
    await page.route("**/api/admin/playlist-links/playlist-link-two?*", (route) => {
      const mode = new URL(route.request().url()).searchParams.get("projectionMode") as "resolved" | "source" | "target";
      return route.fulfill({
        contentType: "application/json",
        body: JSON.stringify(detail("playlist-link-two", "Second playlist", mode)),
      });
    });

    await page.goto("#/library/playlists");
    await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
    const dialog = page.getByRole("dialog", { name: "Test playlist" });
    const scroll = dialog.locator(".track-scroll");
    await scroll.evaluate((element) => {
      element.setAttribute("data-instance", "reading-position");
      element.scrollTop = 600;
    });
    const filter = dialog.getByLabel("Filter tracks");
    await filter.fill("Track");
    await filter.focus();
    const before = await scroll.evaluate((element) => element.scrollTop);
    await dialog.getByRole("button", { name: "Actions" }).click();
    await page.getByRole("menuitem", { name: "Update playlist now" }).click();
    await filter.focus();
    await expect.poll(() => detailRequests).toBe(2);

    await expect(dialog.getByText("Loading playlist tracks…")).toHaveCount(0);
    await expect(scroll).toHaveAttribute("data-instance", "reading-position");
    await expect(filter).toHaveValue("Track");
    await expect(filter).toBeFocused();
    expect(Math.abs(await scroll.evaluate((element) => element.scrollTop) - before)).toBeLessThanOrEqual(1);
    releaseRefresh();
    await expect(page.getByRole("status").filter({ hasText: "Playlist update queued." })).toBeVisible();
    await expect(scroll).toHaveAttribute("data-instance", "reading-position");
    expect(Math.abs(await scroll.evaluate((element) => element.scrollTop) - before)).toBeLessThanOrEqual(1);

    await dialog.getByRole("button", { name: "Close" }).click();
    await page.getByRole("button", { name: "Open Second playlist playlist details" }).click();
    await expect(page.getByRole("dialog", { name: "Second playlist" }).locator(".track-scroll"))
      .toHaveJSProperty("scrollTop", 0);
  });
}

test("Playlist operations show durable progress and confirm cancellation", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);
  let cancelled = false;
  let detailRequests = 0;
  await page.route("**/api/admin/jobs?limit=100", (route) => {
    const fixture = responses["/api/admin/jobs?limit=100"] as { jobs: Record<string, unknown>[]; progress: unknown[] };
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        ...fixture,
        jobs: fixture.jobs.map((job) => ({
          ...job,
          state: cancelled ? "Succeeded" : "Deferred",
          deferralCount: 1,
          availableAt: "2099-01-01T12:00:00Z",
        })),
      }),
    });
  });
  await page.route("**/api/admin/jobs/*/cancel", (route) => {
    cancelled = true;
    return route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        jobId: "11111111-1111-1111-1111-111111111111",
        state: "CancellationRequested",
      }),
    });
  });
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/admin/playlist-links/playlist-link")
      detailRequests++;
  });
  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Open Test playlist playlist details", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Test playlist" });
  const update = page.waitForRequest((item) =>
    item.method() === "POST" && item.url().endsWith("/api/admin/playlist-links/playlist-link/run") &&
    item.postDataJSON().snapshotId === "playlist-snapshot");
  await dialog.getByRole("button", { name: "Actions" }).click();
  await page.getByRole("menuitem", { name: "Update playlist now" }).click();
  await update;
  await expect(page.getByRole("status").filter({ hasText: "Playlist update queued." })).toBeVisible();
  await dialog.getByRole("button", { name: /Operation details:/ }).click();
  const operation = page.locator(".operation-popover");
  await expect(operation.getByText("Matching Test song", { exact: true }).first()).toBeVisible();
  await expect(operation.getByText("1/2")).toBeVisible();
  await expect(operation.getByText("Wait until")).toBeVisible();
  await expect(operation.getByText("Deferrals")).toBeVisible();
  await operation.getByRole("button", { name: "Cancel operation" }).click();
  const confirmation = page.getByRole("alertdialog", { name: "Cancel this operation?" });
  await expect(confirmation).toBeVisible();
  const request = page.waitForRequest((item) =>
    item.method() === "POST" && item.url().endsWith("/api/admin/jobs/11111111-1111-1111-1111-111111111111/cancel"));
  await confirmation.getByRole("button", { name: "Cancel operation" }).click();
  await request;
  await expect.poll(() => detailRequests).toBeGreaterThanOrEqual(2);
});

test("Playlist columns and nested dialogs retain interaction ownership", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await mockApi(page);
  await page.goto("#/library/playlists");
  const opener = page.getByRole("button", { name: "Open Test playlist playlist details" });
  await opener.click();
  const playlist = page.getByRole("dialog", { name: "Test playlist" });
  await expect(playlist).toHaveAttribute("data-side-panel");
  await playlist.getByRole("button", { name: /Choose track columns/ }).click();
  const columns = page.locator(".track-column-picker");
  await columns.getByLabel("Artist", { exact: true }).uncheck();
  await expect(playlist.getByRole("columnheader", { name: "Artist" })).toHaveCount(0);
  await page.keyboard.press("Escape");

  const review = playlist.getByRole("button", { name: "Open mapping details for Test song" });
  await review.click();
  const match = page.getByRole("dialog", { name: "Test song" });
  await expect(match).toBeVisible();
  const layers = await page.evaluate(() => ({
    parent: Number.parseInt(getComputedStyle(document.querySelector(".side-panel")!).zIndex),
    nestedOverlay: Number.parseInt(getComputedStyle(document.querySelector(".match-dialog-overlay")!).zIndex),
    nested: Number.parseInt(getComputedStyle(document.querySelector(".match-dialog")!).zIndex),
  }));
  expect(layers.parent).toBeLessThan(layers.nestedOverlay);
  expect(layers.nestedOverlay).toBeLessThan(layers.nested);
  await page.keyboard.press("Escape");
  await expect(match).toBeHidden();
  await expect(playlist).toBeVisible();
  await expect(review).toBeFocused();
  await playlist.getByRole("button", { name: "Close" }).click();
  await expect(opener).toBeFocused();
});

test("playlist projections, operations, and cancellation keep the selected playlist authoritative", async ({ page }) => {
  await mockApi(page);
  const list = structuredClone(responses["/api/admin/playlist-links"]) as {
    playlistLinks: Array<Record<string, unknown>>;
    inventory: Record<string, unknown>;
  };
  list.playlistLinks.push({ ...list.playlistLinks[0], id: "playlist-2", name: "Second playlist" });
  await page.route("**/api/admin/playlist-links", (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    return route.fulfill({ contentType: "application/json", body: JSON.stringify(list) });
  });
  await page.route("**/api/admin/jobs/11111111-1111-1111-1111-111111111111/cancel", (route) => route.fulfill({
    status: 503,
    contentType: "application/json",
    body: JSON.stringify({ error: "The worker could not be reached." }),
  }));
  await page.goto("#/library/playlists");
  const detailsResponse = page.waitForResponse((response) =>
    new URL(response.url()).pathname === "/api/admin/playlist-links/playlist-link" && response.request().method() === "GET");
  await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
  const fixture = await (await detailsResponse).json() as Record<string, unknown>;
  const details = page.getByRole("dialog", { name: "Test playlist" });

  await details.getByRole("button", { name: /Operation details:/ }).click();
  await page.getByRole("button", { name: "Cancel operation" }).click();
  const cancel = page.getByRole("alertdialog", { name: "Cancel this operation?" });
  await cancel.getByRole("button", { name: "Cancel operation" }).click();
  await expect(cancel.getByRole("alert")).toContainText("The worker could not be reached.");
  await expect(cancel.getByRole("button", { name: "Cancel operation" })).toBeEnabled();
  await cancel.getByRole("button", { name: "Keep running" }).click();

  await details.getByRole("button", { name: "Actions" }).click();
  await page.getByRole("menuitem", { name: "Update playlist now" }).click();
  await expect(details.getByRole("button", { name: /Operation details:/ })).toBeVisible();
  await details.getByRole("button", { name: "Close" }).click();

  const projection = routeRelease();
  await page.route("**/api/admin/playlist-links/playlist-link?projectionMode=resolved", async (route) => {
    await projection.promise;
    return route.fulfill({ contentType: "application/json", body: JSON.stringify({ ...fixture, name: "Stale playlist" }) });
  });
  const staleRequest = page.waitForRequest((request) => request.url().endsWith("/playlist-link?projectionMode=resolved"));
  await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
  await staleRequest;
  await page.keyboard.press("Escape");
  await expect(details).toBeHidden();

  await page.route("**/api/admin/playlist-links/playlist-2?*", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({ ...fixture, id: "playlist-2", name: "Second playlist" }),
  }));
  await page.getByRole("button", { name: "Open Second playlist playlist details" }).click();
  const second = page.getByRole("dialog", { name: "Second playlist" });
  await expect(second).toBeVisible();
  await expect(second.getByRole("button", { name: /Operation details:/ })).toHaveCount(0);
  const staleResponse = page.waitForResponse((response) => response.url().endsWith("/playlist-link?projectionMode=resolved"));
  projection.release();
  await staleResponse;
  await expect(second).toBeVisible();
  await expect(second).not.toContainText("Stale playlist");
});

for (const width of [390, 1280]) {
  for (const colorScheme of ["light", "dark"] as const) {
    test(`Missing-song export at ${width}px ${colorScheme}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 844 });
      await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
      await mockApi(page);
      const format = colorScheme === "light" ? "csv" : "json";
      let requestedFormat = "";
      await page.route("**/api/admin/playlist-links/playlist-link/missing-songs?*", (route) => {
        requestedFormat = new URL(route.request().url()).searchParams.get("format") ?? "";
        return route.fulfill({ contentType: format === "csv" ? "text/csv" : "application/json",
          body: format === "csv" ? 'position,title\n2,"Missing song"\n' : JSON.stringify([{ position: 2, title: "Missing song" }]),
        });
      });
      await page.goto("#/library/playlists");
      await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
      const dialog = page.getByRole("dialog", { name: "Test playlist" });
      await dialog.getByRole("button", { name: "Actions" }).click();
      const item = page.getByRole("menuitem", { name: `Export missing songs as ${format.toUpperCase()}` });
      await expect(item).toBeVisible();
      await item.focus();
      const downloaded = page.waitForEvent("download");
      await item.press("Enter");
      const file = await downloaded;
      expect(file.suggestedFilename()).toBe(`missing-songs.${format}`);
      expect(requestedFormat).toBe(format);
      const stream = await file.createReadStream();
      let text = "";
      for await (const part of stream) text += part.toString();
      expect(text).toContain("Missing song");
      await expect(dialog).toBeVisible();
      expect(await dialog.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(true);
    });
  }
}

test("Missing-song export failures keep playlist details open for retry", async ({ page }) => {
  await mockApi(page);
  let requests = 0;
  await page.route("**/api/admin/playlist-links/playlist-link/missing-songs?*", (route) => {
    requests++;
    return requests === 1
      ? route.fulfill({ status: 403, contentType: "application/json", body: JSON.stringify({ error: "This playlist is outside the authenticated scope" }) })
      : route.fulfill({ contentType: "text/csv", body: "position,title\n2,Missing song\n" });
  });
  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
  const dialog = page.getByRole("dialog", { name: "Test playlist" });
  await dialog.getByRole("button", { name: "Actions" }).click();
  await page.getByRole("menuitem", { name: "Export missing songs as CSV" }).click();
  await expect(dialog.getByRole("alert")).toHaveText("This playlist is outside the authenticated scope");
  await expect(dialog).toBeVisible();
  await dialog.getByRole("button", { name: "Actions" }).click();
  const downloaded = page.waitForEvent("download");
  await page.getByRole("menuitem", { name: "Export missing songs as CSV" }).click();
  expect((await downloaded).suggestedFilename()).toBe("missing-songs.csv");
  await expect(dialog.getByRole("alert")).toHaveCount(0);
  expect(requests).toBe(2);
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Playlist sync links missing media-server access to its fix — ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.addInitScript((value) => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    const list = structuredClone(responses["/api/admin/playlist-links"]) as { playlistLinks: Array<Record<string, unknown>> };
    list.playlistLinks[0].lastRunCode = "backend_access_missing";
    await page.route("**/api/admin/playlist-links", (route) => route.fulfill({ json: list }));
    await page.route("**/api/admin/playlist-links/playlist-link?*", (route) => route.fulfill({ json: {
      id: "playlist-link", snapshotId: "snapshot", snapshotVersion: 1, latestSourceSnapshotVersion: 1,
      name: "Test playlist", sourceProviderId: "lumen-audio", projectionMode: "resolved", targetProtocol: "jellyfin",
      trackCount: 0, matchedCount: 0, unresolvedCount: 0, localCount: 0, externalCount: 0, reviewCount: 0,
      rejectedCount: 0, playableCount: 0, unknownDurationCount: 0, routeCoverage: [], tracks: [],
      syncState: "failed", lastRunCode: "backend_access_missing", retrievedAt: "2026-01-01",
    } }));
    await page.goto("#/library/playlists");
    await expect(page.locator(".playlist-row").getByText("Access needed", { exact: true })).toBeVisible();
    await page.getByRole("button", { name: "Open Test playlist playlist details", exact: true }).click();
    const warning = page.getByRole("status").filter({ hasText: "Restore media-server access to resume playlist sync and library scans." });
    await expect(warning).toHaveCount(1);
    const link = warning.getByRole("link", { name: "Open media-server access" });
    await expect(link).toHaveAttribute("href", "#/settings/people");
    await link.focus();
    await expect(link).toBeFocused();
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
}

test("Empty playlists keep a readable song count", async ({ page }) => {
  await mockApi(page);
  const fixture = responses["/api/admin/playlist-links"] as { playlistLinks: Record<string, unknown>[] };
  await page.route((url) => url.pathname === "/api/admin/playlist-links", route => route.fulfill({ json: {
    playlistLinks: [{ ...fixture.playlistLinks[0], trackCount: 0, matchedCount: 0, unmatchedCount: 0, playableCount: 0,
      metrics: { total: 0, matched: 0, review: 0, unresolved: 0, rejected: 0, playable: 0, materialized: 0 } }],
  } }));
  await page.goto("#/library/playlists");
  await expect(page.locator(".playlist-row")).toContainText("0 songs");
  await expect(page.locator(".playlist-row .coverage-bar")).toHaveCount(0);
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Playlist written into the media server shows its songs and readable status — ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.addInitScript((value) => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    const listFixture = responses["/api/admin/playlist-links"] as { playlistLinks: Record<string, unknown>[] };
    await page.route((url) => url.pathname === "/api/admin/playlist-links", route => route.fulfill({ json: {
      playlistLinks: [{ ...listFixture.playlistLinks[0], trackCount: 30, matchedCount: 28, unmatchedCount: 1, playableCount: 29,
        metrics: { total: 30, matched: 28, review: 1, unresolved: 1, rejected: 1, playable: 29, materialized: 0 } }],
    } }));
    const track = (position: number, title: string, routeKind: "local" | "external", routeProviderId: string) => ({
      sourcePosition: position - 1, position, externalSnapshotId: `snapshot-${position}`, title,
      artists: ["Artist"], album: "Album", durationMs: 200_000, routeKind, routeProviderId,
      backendItemId: routeKind === "local" ? `item-${position}` : null, matchState: "accepted",
      targetEligible: routeKind === "local",
      outcomeCode: routeKind === "local" ? "included_native_backend_item" : "streamed_through_allstarr",
      targetStatus: routeKind === "local" ? "included" : "streamed",
      providerRoutes: routeKind === "local" ? [] : [{ providerId: routeProviderId, externalId: `ext-${position}`, pinned: false }],
    });
    await page.route((url) => url.pathname === "/api/admin/playlist-links/playlist-link", (route) => route.fulfill({ json: {
      id: "playlist-link", snapshotId: "snapshot", snapshotVersion: 2, latestSourceSnapshotVersion: 2,
      hasNewerSourceGeneration: false, name: "Test playlist", sourceProviderId: "lumen-audio",
      projectionMode: "resolved", targetProtocol: "jellyfin", targetPlaylistId: "jellyfin-playlist",
      artworkUrl: "/missing-playlist-art", retrievedAt: new Date(Date.now() - 15 * 3_600_000).toISOString(),
      lastRematchedAt: new Date(Date.now() - 18 * 60_000).toISOString(), completedAt: "2026-01-01",
      trackCount: 3, localCount: 2, externalCount: 1, unresolvedCount: 0, durationMs: 8_092_000,
      matchedCount: 3, reviewCount: 0, rejectedCount: 0, playableCount: 3, unknownDurationCount: 0,
      routeCoverage: [{ providerId: "jellyfin", count: 2 }, { providerId: "lumen-audio", count: 1 }],
      schedule: { cronExpression: "0 8 * * 1", timeZoneId: "America/New_York", enabled: true,
        nextRunAt: new Date(Date.now() + 3 * 86_400_000).toISOString() },
      reconciliation: { addedPositions: [0, 1, 2], removedPositions: [], movedPositions: [], duplicatedPositions: [], changedPositions: [] },
      clientProjection: null,
      tracks: [track(1, "First local song", "local", "jellyfin"), track(2, "Second local song", "local", "jellyfin"),
        track(3, "Streamed song", "external", "lumen-audio")],
    } }));
    await page.goto("#/library/playlists");
    await expect(page.locator(".playlist-row").getByText("Needs a look", { exact: true })).toBeVisible();
    await expect(page.getByRole("radio", { name: /^Needs a look/ })).toContainText("1");
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `test-results/playlist-list-${width}-${theme}.png`, fullPage: true });
    await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
    const dialog = page.getByRole("dialog", { name: "Test playlist" });
    const table = dialog.getByRole("table", { name: "Test playlist tracks" });
    await expect(table.getByText("First local song", { exact: true })).toBeVisible();
    await expect(table.getByText("Streamed song", { exact: true })).toBeVisible();
    await expect(dialog.getByText(/Mapped playlist details are unavailable|No songs yet/)).toHaveCount(0);
    await expect(dialog).not.toContainText("Unresolved");
    await expect(dialog.locator(".hero-route")).toContainText("Jellyfin");
    await expect(dialog.locator(".hero-route")).toContainText("2 hr 15 min");
    const overview = dialog.locator(".playlist-overview");
    await expect(dialog).not.toContainText("Where songs play from");
    await expect(dialog).not.toContainText("3 of 3 play");
    await expect(dialog).not.toContainText("Songs keep their original order.");
    await expect(overview.locator(".review-filter-button")).toHaveCount(0);
    await expect(overview.locator(".coverage-bar, .composition-legend")).toHaveCount(0);
    await expect(overview).toContainText(/Every Monday at 8:00\sAM/);
    await expect(overview).not.toContainText("Cron");
    await expect(overview).toContainText("3 added");
    await expect(overview.getByRole("button", { name: "Edit schedule" })).toBeVisible();
    expect(await dialog.evaluate((element) => [element, ...element.querySelectorAll(".playlist-overview, .playlist-hero")]
      .every((part) => part.scrollWidth <= part.clientWidth + 1))).toBe(true);
    await expect(dialog.locator(".panel-body")).toHaveCSS("overflow-y", "auto");
    await page.screenshot({ path: `test-results/playlist-detail-${width}-${theme}.png`, fullPage: true });
  });
}
