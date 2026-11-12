import { expect, test, type Locator } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";
import { openOtherPlaylistSources, viewports, clickWhenStable, waitForSettledOptions, schema, responses, routeRelease, mockApi, routes, stateRoutes } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

for (const viewport of viewports) {
  test.describe(`${viewport.width}x${viewport.height}`, () => {
    test.use({ viewport, colorScheme: process.env.ALLSTARR_SCREENSHOT_THEME === "dark" ? "dark" : "light" });

    for (const [route, heading] of routes) {
      test(`${route} has no document overflow`, async ({ page }) => {
        const errors: string[] = [];
        page.on("pageerror", (error) => errors.push(error.message));
        const screenshotTheme = process.env.ALLSTARR_SCREENSHOT_THEME;
        if (screenshotTheme === "light" || screenshotTheme === "dark") {
          await page.addInitScript((theme) => localStorage.setItem("allstarr.theme", theme), screenshotTheme);
        }
        await mockApi(page, route === "#/for-you" ? { intelligenceEnabled: false } : {});
        await page.goto(route);
        const pageHeading = page.getByRole("heading", { name: heading, level: 1 });
        await expect(pageHeading).toBeVisible();
        await page.evaluate(() => new Promise<void>((resolve) =>
          requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
        ));
        expect(await page.evaluate(() => window.scrollY)).toBe(0);
        expect((await pageHeading.boundingBox())?.y).toBeGreaterThanOrEqual(0);
        await expect.poll(() => page.evaluate(() =>
          document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
        if (viewport.width <= 760) await expectContainedMobileGeometry(page);
        if (route === "#/activity") await expect(page.getByLabel("Search")).toBeVisible();
        expect(errors).toEqual([]);
        if (process.env.ALLSTARR_SCREENSHOT_DIR) {
          const name = route.replace(/^#\/?/, "").replaceAll(/[^a-z0-9]+/gi, "-") || "home";
          await page.screenshot({
            path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/${screenshotTheme ?? "system"}-${viewport.width}-${name}.png`,
            fullPage: true,
          });
        }
      });
    }

    test("Playlist projection dialogs remain usable", async ({ page }, testInfo) => {
      await mockApi(page);
      await page.goto("#/library/playlists");
      await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
      const details = page.getByRole("dialog", { name: "Test playlist" });
      await expect(details).not.toContainText(/hybrid|materialized|write-back|projection mode/i);
      await expect(details.getByRole("tab", { name: /Original|Native/ })).toHaveCount(0);
      await expect(details.getByText("Test song", { exact: true })).toBeVisible();
      await expect(details).toHaveCSS("overflow", "clip");
      await expect(details.locator(".panel-body")).toHaveCSS("overflow-y", "auto");
      await expect(details.locator(".track-scroll")).toHaveCSS("overflow", "auto");
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      const screenshotDirectory = process.env.ALLSTARR_SCREENSHOT_DIR;
      const detailScreenshot = await page.screenshot({
        path: screenshotDirectory ? `${screenshotDirectory}/playlist-${viewport.width}-details.png` : undefined,
      });
      await testInfo.attach("playlist-details", {
        body: detailScreenshot,
        contentType: "image/png",
      });

      await details.getByRole("button", { name: "Actions" }).click();
      await page.getByRole("menuitem", { name: "Edit settings" }).click();
      const settings = page.getByRole("dialog", { name: "Edit playlist settings" });
      await expect(settings).not.toContainText(/native playlist|hybrid|materialized|write-back|projection mode|backend/i);
      await expect(settings.locator(".playlist-settings-form")).toHaveCSS("overflow-y", "auto");
      const importCards = settings.locator(".pl-option-static");
      await expect(importCards).toHaveCount(2);
      expect(await importCards.evaluateAll((cards) => cards.every((card) => {
        const text = card.querySelector(".pl-option-text")!;
        return text.getBoundingClientRect().width >= card.getBoundingClientRect().width - 40;
      }))).toBe(true);
      await expect(settings.getByRole("button", { name: "Save settings" })).toBeInViewport();
      const settingsScreenshot = await page.screenshot({
        path: screenshotDirectory ? `${screenshotDirectory}/playlist-${viewport.width}-settings.png` : undefined,
      });
      await testInfo.attach("playlist-settings", {
        body: settingsScreenshot,
        contentType: "image/png",
      });
    });

    for (const [route, heading, loadingLabel, releasePath, failures] of stateRoutes) {
      test(`${route} exposes loading and error recovery`, async ({ page, context }) => {
        const delayed = routeRelease();
        await mockApi(page, { releasePath, release: delayed.promise });
        await page.goto(route);
        await expect(page.getByLabel(loadingLabel)).toBeVisible();
        delayed.release();
        await expect(page.getByRole("heading", { name: heading, level: 1 })).toBeVisible();

        const errorPage = await context.newPage();
        const failedPaths = [...failures];
        await mockApi(errorPage, { fail: failedPaths });
        await errorPage.goto(route);
        const errorSurface = route === "#/activity" ? errorPage.getByRole("alert", { name: "Event log unavailable", exact: true }) : errorPage.getByRole("alert");
        await expect(errorSurface).toBeVisible();
        const retry = errorSurface.getByRole("button", { name: "Try again" });
        if (route === "#/activity") await retry.scrollIntoViewIfNeeded();
        await expect(retry).toBeInViewport();
        if (route === "#/") {
          failedPaths.length = 0;
          await retry.click();
          await expect(errorSurface).toHaveCount(0);
          await expect(errorPage.getByRole("region", { name: "Your setup", exact: true })).toBeVisible();
        }
      });
    }

    test("Settings dialogs remain usable", async ({ page }) => {
      await mockApi(page);
      let saved: Record<string, string> | null = null;
      await page.route("**/api/admin/config", route => {
        if (route.request().method() === "POST") {
          saved = route.request().postDataJSON().updates;
          return route.fulfill({ json: { message: "Runtime configuration updated.", updatedKeys: Object.keys(saved ?? {}) } });
        }
        return route.fulfill({ json: {
          ...(responses["/api/admin/config"] as object),
          providers: { streamingOrder: saved?.StreamingOrder ?? "lumen-audio" },
        } });
      });
      await page.goto("#/settings/advanced");
      const publicUrl = page.locator(".setting-field").filter({ has: page.getByText("Public URL", { exact: true }) });
      await expect(publicUrl.getByText("Set in your deployment", { exact: true })).toBeVisible();
      await expect(publicUrl.locator("output")).toBeVisible();
      await expect(publicUrl.getByRole("textbox")).toHaveCount(0);
      await page.goto("#/settings/appearance");
      await page.getByRole("radiogroup", { name: "Color theme", exact: true }).getByRole("radio", { name: "Dark", exact: true }).click();
      await expect(page.locator("html")).toHaveClass(/dark/);
      await page.keyboard.press("Escape");
      await page.goto("#/settings/accounts");
      await expect(page.getByRole("heading", { name: "Sources", level: 1 })).toBeVisible();
      await expect(page.getByRole("tab", { name: "Accounts" })).toHaveAttribute("aria-selected", "true");
      await page.goto("#/sources?source=lumen-audio&section=configuration");
      await expect(page.getByRole("heading", { name: "Sources", level: 1 })).toBeVisible({ timeout: 15_000 });
      await expect(page.getByRole("heading", { name: "Lumen Audio", level: 2 })).toBeVisible();
      await page.getByRole("button", { name: "Connect account", exact: true }).click();
      const sourceDialog = page.locator(".connect-source-dialog");
      await expect(sourceDialog.getByText("Lumen Audio", { exact: true })).toBeVisible();
      await expect(sourceDialog.getByLabel("Access token")).toHaveValue("");
      await sourceDialog.getByRole("button", { name: "Cancel" }).click();
      await page.goto("#/settings/routing");
      await expect(page.getByRole("heading", { name: "Music sources", level: 2 })).toBeVisible();
      await expect(page.getByText(/Which services Allstarr asks, in what order/)).toBeVisible();
      await expect(page.getByRole("button", { name: "Save playback and matching" })).toHaveCount(0);
      await expect(page.getByRole("button", { name: "Move Jellyfin up" })).toHaveCount(0);
      await expect(page.locator(".provider-art").first()).toHaveCSS("border-top-width", "0px");
      const savePlayback = page.getByRole("button", { name: "Save Playback", exact: true });
      await expect(savePlayback).toHaveCount(0);
      const routes = page.locator(".routing-group li[draggable=true]");
      // Centre the rows: on a landscape phone the bottom navigation bar covers the lower part of the screen.
      await routes.nth(0).evaluate((element) => element.scrollIntoView({ block: "center" }));
      await expect(routes.nth(0)).toBeInViewport({ ratio: 1 });
      await expect(routes.nth(1)).toBeInViewport({ ratio: 1 });
      // Dragging while the view is still easing in can land on the wrong row.
      await page.evaluate(() => Promise.all(document.getAnimations()
        .filter((animation) => animation.timeline === document.timeline && animation.effect?.getTiming().iterations !== Infinity)
        .map((animation) => animation.finished)));
      await routes.nth(0).locator(".routing-index").dragTo(routes.nth(1).locator(".routing-index"));
      await expect(routes.nth(0)).toContainText("Future audio");
      await expect.poll(() => saved).toEqual({ StreamingOrder: "future-audio,lumen-audio" });
      await expect(page.getByText("Order saved", { exact: true })).toBeVisible();
      await expect(page.getByRole("button", { name: "Undo", exact: true })).toBeVisible();
      await expect(savePlayback).toHaveCount(0);
      await expect.poll(() => page.evaluate(() =>
        document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      await page.goto("#/integrations/services/lumen-audio");
      await expect(page.getByRole("dialog", { name: "Lumen Audio", exact: true }).locator(".service-description")).toHaveText("Used for song details and playback.");
      await page.goto("#/settings/extensions");
      await page.getByRole("button", { name: "Add extension" }).click();
      const addSource = page.getByRole("dialog", { name: "Add an extension" });
      await expect(addSource.getByRole("searchbox", { name: "Search extensions" })).toBeVisible();
      await addSource.getByRole("tab", { name: /Manage catalogs/ }).click();
      await expect(addSource.getByText("Add a catalog", { exact: true })).toBeVisible();
      await addSource.getByRole("tab", { name: "From a link" }).click();
      await expect(addSource.getByRole("button", { name: "Download and review" })).toBeInViewport();
      await expect.poll(() => page.evaluate(() =>
        document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      await addSource.getByRole("button", { name: "Close add an extension" }).click();
      await page.goto("#/settings/maintenance");
      await expect(page.getByText("Choose a backup ZIP. Saved sign-ins are included; audio and caches need separate backups.", { exact: true })).toBeVisible();
      await expect(page.getByRole("button", { name: "Back up now" })).toBeVisible();
      await expect(page.getByText("No backups yet", { exact: true })).toBeVisible();
      await expect(page.locator(".transfer-card")).toHaveCount(0);
      await expect.poll(() => page.evaluate(() =>
        document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      await page.goto("#/settings/storage");
      const storage = page.locator(".settings-workspace");
      await expect(storage.getByRole("heading", { name: "Storage and speed", level: 2, exact: true })).toBeVisible();
      await expect(storage).toHaveAttribute("aria-busy", "false");
      const advanced = storage.locator("details").filter({ has: page.getByText(
        "Advanced: cache times, file checks and cleanup", { exact: true }) });
      await advanced.locator(":scope > summary").click();
      // The verification card loads above the cache actions and changes their position.
      await expect(storage.getByRole("article", { name: "Saved-file verification" })
        .getByRole("button", { name: "Verify saved files", exact: true })).toBeEnabled();
      await clickWhenStable(storage.locator(".cache-diagnostics-card").getByRole("button", { name: "Purge all cache", exact: true }));
      const dialog = page.getByRole("alertdialog", { name: "Remove cached files?" });
      await expect(dialog).toBeVisible();
      await expect(dialog.getByRole("button", { name: "Remove cache" })).toBeInViewport();
    });

    test("Intelligence results remain usable", async ({ page, context }) => {
      const delayed = routeRelease();
      await page.emulateMedia({ reducedMotion: "reduce" });
      await page.addInitScript(() => localStorage.setItem("allstarr.theme", "system"));
      await mockApi(page, { releasePath: "/api/admin/intelligence", release: delayed.promise });
      const initialHistoryOverview = page.waitForRequest((request) =>
        new URL(request.url()).pathname === "/api/admin/intelligence/history/overview");
      await page.goto("#/intelligence");
      const loadingStatus = page.getByRole("status", { name: "Loading Intelligence" });
      await expect(loadingStatus).toBeVisible();
      expect(await loadingStatus.locator(".skeleton-panel").first().evaluate((element) =>
        getComputedStyle(element, "::after").animationName)).toBe("none");
      delayed.release();
      const initialHistoryUrl = new URL((await initialHistoryOverview).url());
      expect(initialHistoryUrl.searchParams.has("from")).toBe(false);
      expect(initialHistoryUrl.searchParams.has("to")).toBe(false);
      await expect(page.getByRole("tab", { name: "Overview" })).toHaveAttribute("aria-selected", "true");
      await expect(page.getByRole("tab", { name: "All time" })).toHaveAttribute("aria-selected", "true");
      await expect(page.locator(".scope-value")).toContainText("Music");
      await expect(page.locator(".scope-value")).toContainText("Jellyfin · Jellyfin Music");
      if (viewport.width <= 760) {
        await page.getByRole("button", { name: "More destinations", exact: true }).click();
        const more = page.getByRole("dialog", { name: "More", exact: true });
        const themeButton = more.getByRole("button", { name: /Appearance/ });
        await themeButton.click();
        await expect(page.locator("html")).not.toHaveClass(/dark/);
        await themeButton.click();
        await expect(page.locator("html")).toHaveClass(/dark/);
        await themeButton.click();
        await expect(page.locator("html")).toHaveAttribute("data-theme", "system");
        await page.keyboard.press("Escape");
        await expect(more).toBeHidden();
      } else {
        await page.goto("#/settings/appearance");
        const theme = page.getByRole("radiogroup", { name: "Color theme", exact: true });
        await theme.getByRole("radio", { name: "Light", exact: true }).click();
        await expect(page.locator("html")).not.toHaveClass(/dark/);
        await theme.getByRole("radio", { name: "Dark", exact: true }).click();
        await expect(page.locator("html")).toHaveClass(/dark/);
        await theme.getByRole("radio", { name: "Device", exact: true }).click();
        await expect(page.locator("html")).toHaveAttribute("data-theme", "system");
        await page.goto("#/intelligence");
      }
      await expect(page.getByLabel("Server connection")).toHaveCount(0);
      await expect(page.getByRole("button", { name: "Open library" })).toHaveCount(0);
      if (viewport.width <= 650) {
        const sectionTabs = page.getByRole("navigation", { name: "Intelligence sections" }).locator(".segmented-tabs");
        await expect(sectionTabs).toHaveCSS("overflow-x", "auto");
        await expect(sectionTabs).toHaveCSS("scrollbar-width", "none");
        await expect(sectionTabs.getByRole("tab", { name: "Playlists", exact: true })).toHaveCount(1);
        expect(Math.min(...await sectionTabs.getByRole("tab").evaluateAll((tabs) => tabs.map((tab) => tab.clientWidth)))).toBeGreaterThanOrEqual(72);
      }
      const recap = page.locator(".recap-card");
      await expect(recap).toContainText("12 times across 9 songs and 5 artists");
      await expect(recap).toContainText("Your most-played song was Moon Song by The Comets");
      await expect(recap).toContainText("Your first recorded listen was");
      await expect(page.getByRole("heading", { name: "Recently played", level: 3 })).toBeVisible();
      const historyList = page.locator(".history-list");
      await expect(historyList.getByText("Moon Song", { exact: true })).toBeVisible();
      await expect(historyList).not.toContainText(/not\s?requested/i);
      await expect(historyList.locator(".track-art img")).toBeVisible();
      const historyRow = historyList.getByRole("button").first();
      expect((await historyRow.boundingBox())!.height).toBeLessThan(viewport.width <= 760 ? 112 : 72);
      if (viewport.width > 760) await expect(page.locator(".history-column-head")).toBeVisible();
      else await expect(page.locator(".history-column-head")).toBeHidden();
      expect(await page.locator(".history-list").evaluate((element) => element.tagName)).toBe("UL");
      const activityCard = page.locator(".activity-card");
      const dailyHeatmap = activityCard.getByRole("slider", { name: "Daily listening activity" });
      await expect(dailyHeatmap).toHaveAttribute("aria-valuemax", "30");
      await dailyHeatmap.focus();
      await page.keyboard.press("Home");
      await expect(dailyHeatmap).toHaveAttribute("aria-valuenow", "0");
      await expect(activityCard.getByRole("region", { name: "Selected listening activity" })).toContainText("December 1, 2025");
      await page.keyboard.press("End");
      await expect(dailyHeatmap).toHaveAttribute("aria-valuenow", "30");
      const activityDetail = activityCard.getByRole("region", { name: "Selected listening activity" });
      await expect(activityDetail).toContainText("December 31, 2025");
      await expect(activityDetail).toContainText("31");
      await expect(activityDetail).toContainText("1 hr 33 min");
      await expect(activityDetail).toContainText("3:00");

      const firstHeatmapCell = activityCard.locator('[data-heatmap-index="0"]');
      const dragHeatmapCell = activityCard.locator('[data-heatmap-index="20"]');
      const firstCellBox = await firstHeatmapCell.boundingBox();
      const dragCellBox = await dragHeatmapCell.boundingBox();
      expect(firstCellBox).not.toBeNull();
      expect(dragCellBox).not.toBeNull();
      const center = (box: NonNullable<typeof firstCellBox>) => ({ x: box.x + box.width / 2, y: box.y + box.height / 2 });
      await page.mouse.move(center(firstCellBox!).x, center(firstCellBox!).y);
      await page.mouse.down();
      await page.mouse.move(center(dragCellBox!).x, center(dragCellBox!).y, { steps: 2 });
      await expect(dailyHeatmap).toHaveAttribute("aria-valuenow", "20");
      await page.mouse.up();

      const dispatchTouchPointer = async (type: "pointerdown" | "pointermove" | "pointerup", point: { x: number; y: number }) =>
        page.evaluate(({ type, point }) => {
          const slider = document.querySelector<HTMLElement>('.activity-card [role="slider"]');
          if (!slider) throw new Error("Listening activity heatmap slider is missing.");
          const event = new PointerEvent(type, {
            bubbles: true, cancelable: true, pointerId: 23, pointerType: "touch",
            clientX: point.x, clientY: point.y, button: 0, buttons: type === "pointerup" ? 0 : 1,
          });
          if (type === "pointerdown") {
            const originalCapture = Element.prototype.setPointerCapture;
            let captured = false;
            Element.prototype.setPointerCapture = () => { captured = true; };
            try { slider.dispatchEvent(event); } finally { Element.prototype.setPointerCapture = originalCapture; }
            return captured;
          } else {
            slider.dispatchEvent(event);
            return false;
          }
        }, { type, point });
      expect(await dispatchTouchPointer("pointerdown", center(firstCellBox!))).toBe(true);
      await dispatchTouchPointer("pointermove", center(dragCellBox!));
      await expect(dailyHeatmap).toHaveAttribute("aria-valuenow", "20");
      await dispatchTouchPointer("pointerup", center(dragCellBox!));

      await activityCard.getByRole("tab", { name: "Monthly" }).click();
      const monthlyHeatmap = activityCard.getByRole("slider", { name: "Monthly listening activity" });
      await expect(monthlyHeatmap).toHaveAttribute("aria-valuemax", "0");
      await expect(activityCard.getByRole("region", { name: "Selected listening activity" })).toContainText("December 2025");
      await activityCard.getByRole("tab", { name: "Daily" }).click();
      await expect(page.locator(".breakdown-card").filter({ hasText: "Sources" })).toContainText("playback");
      await expect(page.locator(".breakdown-card").filter({ hasText: "Providers" })).toContainText("deezer");
      await expect(page.locator(".breakdown-card").filter({ hasText: "Listening apps" })).toContainText("Koito");
      const topTabs = page.getByRole("navigation", { name: "Top listening category" });
      await topTabs.getByRole("tab", { name: "Artists" }).focus();
      await page.keyboard.press("ArrowRight");
      await expect(topTabs.getByRole("tab", { name: "Albums" })).toHaveAttribute("aria-selected", "true");
      await page.keyboard.press("End");
      await expect(topTabs.getByRole("tab", { name: "Songs" })).toHaveAttribute("aria-selected", "true");
      if (viewport.width <= 650)
        expect(Math.min(...await topTabs.getByRole("tab").evaluateAll((tabs) => tabs.map((tab) => tab.clientHeight)))).toBeGreaterThanOrEqual(44);
      await page.getByRole("tab", { name: "Playlists", exact: true }).click();
      await expect(page).toHaveURL(/#\/intelligence\?section=playlists$/);
      const intelligencePlaylists = page.locator(".intelligence-playlists");
      await expect(intelligencePlaylists.getByRole("heading", { name: "Recommendation playlists", level: 3 })).toBeVisible();
      await expect(intelligencePlaylists.getByText("Morning discovery", { exact: true })).toBeVisible();
      await expect(intelligencePlaylists).toContainText("injects it into this user’s Jellyfin library");
      await expect(intelligencePlaylists.getByLabel("Playlist name")).toHaveValue("Your recommendations");
      await expect(intelligencePlaylists.getByRole("button", { name: "Create in Jellyfin" })).toBeVisible();
      await page.getByRole("tab", { name: "Discover" }).click();
      await expect(page).toHaveURL(/#\/intelligence\?section=discover$/);
      await expect(page.getByText("Future Song", { exact: true })).toBeVisible();
      await expect(page.getByText("Searching Lumen Audio.")).toBeVisible();
      await expect(page.getByRole("progressbar", { name: "Recommendation refresh progress" })).toHaveAttribute("aria-valuenow", "1");
      await page.getByRole("button", { name: "Cancel refresh" }).scrollIntoViewIfNeeded();
      await expect(page.getByRole("button", { name: "Cancel refresh" })).toBeInViewport();
      await page.getByRole("button", { name: "Refresh recommendations" }).scrollIntoViewIfNeeded();
      await expect(page.getByRole("button", { name: "Refresh recommendations" })).toBeInViewport();
      const soundDiscovery = page.locator(".sound-discovery");
      await expect(soundDiscovery).toContainText("Allstarr will not create or change a Jellyfin playlist");
      await expect(soundDiscovery).not.toContainText(/native playlist|hybrid|materialized|write-back/i);
      const firstScan = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/analysis"));
      await soundDiscovery.getByRole("button", { name: "Scan library sounds" }).click();
      expect((await firstScan).postDataJSON()).toMatchObject({ rebuild: false });
      await expect(soundDiscovery.getByRole("progressbar", { name: "Library sound scan progress" })).toHaveAttribute("aria-valuenow", "200");
      const rebuildScan = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/analysis"));
      await soundDiscovery.getByRole("button", { name: "Scan library again" }).click();
      expect((await rebuildScan).postDataJSON()).toMatchObject({ rebuild: true });
      const similarRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/similar"));
      await soundDiscovery.getByRole("button", { name: "Find songs" }).click();
      expect((await similarRequest).postDataJSON()).toMatchObject({
        protocol: "jellyfin", backendInstanceId: "main", seedTrackIds: ["track-1"],
      });
      await expect(soundDiscovery.getByText("Nearby Song", { exact: true })).toBeVisible();
      await soundDiscovery.getByLabel("How to explore").click();
      await page.getByRole("option", { name: "Connect two songs" }).click();
      const pathRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/path"));
      await soundDiscovery.getByRole("button", { name: "Find songs" }).click();
      expect((await pathRequest).postDataJSON()).toMatchObject({ startTrackId: "track-1", endTrackId: "track-2" });
      await expect(soundDiscovery.getByText("Bridge Song", { exact: true })).toBeVisible();
      await soundDiscovery.getByLabel("How to explore").click();
      await page.getByRole("option", { name: "Include one sound and avoid another" }).click();
      const blendRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/blend"));
      await soundDiscovery.getByRole("button", { name: "Find songs" }).click();
      expect((await blendRequest).postDataJSON()).toMatchObject({ includeTrackIds: ["track-1"], avoidTrackIds: ["track-2"] });
      await expect(soundDiscovery.getByText("Blend Song", { exact: true })).toBeVisible();
      await soundDiscovery.getByLabel("How to explore").click();
      await page.getByRole("option", { name: "Describe what you want" }).click();
      await soundDiscovery.getByLabel("Describe a sound").fill("warm and quiet");
      const textRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/search"));
      await soundDiscovery.getByRole("button", { name: "Find songs" }).click();
      expect((await textRequest).postDataJSON()).toMatchObject({ query: "warm and quiet", mode: "text" });
      await expect(soundDiscovery.getByText("Quiet Light", { exact: true })).toBeVisible();
      await soundDiscovery.getByLabel("What to match").click();
      await page.getByRole("option", { name: "Song lyrics" }).click();
      await soundDiscovery.getByLabel("Words to find").fill("city lights in the rain");
      const lyricsRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/search"));
      await soundDiscovery.getByRole("button", { name: "Find songs" }).click();
      expect((await lyricsRequest).postDataJSON()).toMatchObject({ query: "city lights in the rain", mode: "lyrics" });
      await expect(soundDiscovery.getByText("Rain Words", { exact: true })).toBeVisible();
      await soundDiscovery.getByLabel("How to explore").click();
      await page.getByRole("option", { name: "Use what I played most" }).click();
      const fingerprintRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/fingerprint"));
      await soundDiscovery.getByRole("button", { name: "Find songs" }).click();
      expect((await fingerprintRequest).postDataJSON()).toMatchObject({
        protocol: "jellyfin", backendInstanceId: "main", periodDays: 90,
      });
      await expect(soundDiscovery.getByText("Taste Song", { exact: true })).toBeVisible();
      await soundDiscovery.getByLabel("Playlist name").fill("Evening sounds");
      await soundDiscovery.getByRole("button", { name: "Create Jellyfin playlist" }).click();
      const createDialog = page.getByRole("alertdialog", { name: "Create Evening sounds in Jellyfin?" });
      await expect(createDialog).toContainText("Allstarr will create Evening sounds in Jellyfin with 1 song.");
      await createDialog.getByRole("button", { name: "Do not create playlist" }).click();
      await expect(createDialog).toBeHidden();
      await soundDiscovery.getByRole("button", { name: "Create Jellyfin playlist" }).click();
      const createRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/audiomuse/generated-sets"));
      await createDialog.getByRole("button", { name: "Create Jellyfin playlist" }).click();
      expect((await createRequest).postDataJSON()).toMatchObject({
        protocol: "jellyfin", backendInstanceId: "main",
        name: "Evening sounds", trackIds: ["track-5"],
      });
      await expect(soundDiscovery.getByText("Allstarr is creating Evening sounds in Jellyfin.", { exact: true })).toBeVisible();
      await soundDiscovery.getByLabel("How to explore").click();
      await page.getByRole("option", { name: "Browse the whole library by sound" }).click();
      await soundDiscovery.getByRole("button", { name: "Group similar songs" }).click();
      await expect(soundDiscovery.getByText("Soft and warm", { exact: true })).toBeVisible();
      await soundDiscovery.getByRole("button", { name: "Show more groups" }).click();
      await expect(soundDiscovery.getByText("Bright and quick", { exact: true })).toBeVisible();
      await soundDiscovery.getByRole("button", { name: "List the sound map" }).click();
      await expect(soundDiscovery.getByText("Quiet Light", { exact: true })).toBeVisible();
      const nextMap = page.waitForRequest((request) => request.url().includes("/api/admin/intelligence/audiomuse/map") && new URL(request.url()).searchParams.get("cursor") === "next");
      await soundDiscovery.getByRole("button", { name: "Show more songs" }).click();
      await nextMap;
      await expect(soundDiscovery.getByText("Far Song", { exact: true })).toBeVisible();
      if (process.env.ALLSTARR_SCREENSHOT_DIR)
        await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/intelligence-${viewport.width}-recommendations.png`, fullPage: true });
      await expect.poll(() => page.evaluate(() =>
        document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      await page.getByRole("tab", { name: "History", exact: true }).click();
      await expect(page.getByRole("heading", { name: "Listening history", level: 3, exact: true })).toBeVisible();
      await expect(page.locator(".history-list").getByText("Moon Song", { exact: true })).toBeVisible();
      await expect(page.getByRole("heading", { name: "Import listening history" })).toHaveCount(0);
      await expect.poll(() => page.evaluate(() =>
        document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      if (process.env.ALLSTARR_SCREENSHOT_DIR)
        await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/intelligence-${viewport.width}-history.png`, fullPage: true });
      await page.getByRole("tab", { name: "Import", exact: true }).click();
      await expect(page.getByRole("heading", { name: "Import listening history" })).toBeVisible();
      await expect(page.getByText(/upload the JSON files from your Extended Streaming History download/)).toBeVisible();
      const automationTab = page.getByRole("tab", { name: "Automation", exact: true });
      await expect(automationTab).toHaveCount(1);
      await automationTab.scrollIntoViewIfNeeded();
      await expect(automationTab).toBeInViewport();
      await expect(page.getByText("Imported listens older than 30 days are removed automatically.", { exact: true })).toBeVisible();
      await expect(page.locator(".history-list")).toHaveCount(0);
      const historyUpload = page.locator(".upload-zone");
      await historyUpload.scrollIntoViewIfNeeded();
      await expect(historyUpload).toBeInViewport();
      await expect(page.getByLabel("History export files")).toHaveAttribute("multiple", "");
      await expect.poll(() => page.evaluate(() =>
        document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      if (process.env.ALLSTARR_SCREENSHOT_DIR)
        await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/intelligence-${viewport.width}-imports.png`, fullPage: true });
      await page.getByRole("tab", { name: "Automation" }).click();
      await expect(page.getByText("Monday discoveries", { exact: true })).toBeVisible();
      await expect(page.getByText("Allstarr keeps private listening history and uses it for recommendations.", { exact: true })).toBeVisible();
      const automaticHistory = page.getByRole("checkbox", { name: /Save my listening automatically/ });
      await expect(automaticHistory).toBeChecked();
      await expect(automaticHistory.locator("..")).toHaveClass(/selected/);
      expect((await automaticHistory.locator("..").boundingBox())?.height ?? 999).toBeLessThan(140);
      await expect(automaticHistory).toHaveAttribute("data-slot", "checkbox");
      await expect(page.getByText("Keep listening history for", { exact: true })).toBeVisible();
      await expect(page.getByText("Recommendation actions", { exact: true })).toBeVisible();
      await expect(page.getByText("Recommendation sources", { exact: true })).toBeVisible();
      await expect(page.getByText("Connected services Allstarr may use to find candidates. This does not import history or change source accounts.", { exact: true })).toBeVisible();
      const audioMuseSetup = page.locator(".provider-setup").filter({ hasText: "AudioMuse-AI connection" });
      await expect(audioMuseSetup).toContainText("Connect your self-hosted AudioMuse-AI server here");
      await audioMuseSetup.getByRole("button", { name: "Connect AudioMuse-AI" }).click();
      const audioMuseDialog = page.locator(".connect-source-dialog");
      await expect(audioMuseDialog.getByLabel("AudioMuse-AI server URL")).toBeVisible();
      await expect(audioMuseDialog.getByLabel("AudioMuse-AI access token")).toBeVisible();
      await audioMuseDialog.getByRole("button", { name: "Close source connection dialog" }).click();
      await expect(page.locator(".settings-stack")).not.toContainText(/\bsignals?\b/i);
      await expect(page.getByText("Allstarr sent the latest completed listen to Last.fm.", { exact: true })).toBeVisible();
      await expect(page.getByText("Allstarr will not send listens to ListenBrainz.", { exact: true })).toBeVisible();
      await expect(page.getByText("Allstarr is checking 2 saved listens for more song details.", { exact: true })).toBeVisible();
      const intelligenceSource = page.locator(".provider-choices label").filter({ hasText: "Private similarity source." });
      await expect(intelligenceSource.locator('[data-slot="badge"]')).toHaveText("Ready");
      await expect(page.getByText("Where generated playlists are created", { exact: true })).toBeVisible();
      expect(await page.locator(".status-list").evaluate((element) => element.tagName)).toBe("UL");
      expect(await page.locator(".schedule-list").evaluate((element) => element.tagName)).toBe("UL");
      const listeningApps = page.locator(".listening-apps-card");
      await expect(listeningApps).toContainText("Allstarr will save listens from this key to your account on Jellyfin.");
      await expect(listeningApps).toContainText("Allstarr will not send these listens to another service.");
      expect(await listeningApps.locator(".key-list").evaluate((element) => element.tagName)).toBe("UL");
      await expect(listeningApps).not.toContainText(/native playlist|hybrid|materialized|write-back|projection|backend/i);
      const keyRequest = page.waitForRequest((request) => request.url().endsWith("/api/admin/intelligence/listening-apps") && request.method() === "POST");
      await listeningApps.getByRole("button", { name: "Create private key" }).click();
      const keyInput = (await keyRequest).postDataJSON();
      expect(keyInput).toMatchObject({
        protocol: "jellyfin", backendInstanceId: "main", sendToConnectedServices: false,
      });
      expect(keyInput).not.toHaveProperty("libraryScopeId");
      expect(keyInput).not.toHaveProperty("userId");
      expect(keyInput).not.toHaveProperty("ownerUserId");
      await expect(listeningApps.getByLabel("New listening app private key")).toHaveValue(/^als_/);
      await page.getByRole("button", { name: "Turn off and clear" }).click();
      const clearDialog = page.getByRole("alertdialog", { name: "Clear private listening data for this library?" });
      await expect(clearDialog).toContainText("it will not change Jellyfin playlists or connected Last.fm or ListenBrainz accounts.");
      await clearDialog.getByRole("button", { name: "Keep my data" }).click();
      if (process.env.ALLSTARR_SCREENSHOT_DIR)
        await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/intelligence-${viewport.width}-settings.png`, fullPage: true });

      const errorPage = await context.newPage();
      await mockApi(errorPage, { fail: ["/api/admin/intelligence"] });
      await errorPage.goto("#/intelligence");
      await expect(errorPage.getByRole("alert")).toContainText("Fixture unavailable");
      await expect(errorPage.getByRole("button", { name: "Try again" })).toBeInViewport();
    });

    test("Source dialogs remain usable", async ({ page }) => {
      await mockApi(page);
      await page.goto("#/sources/accounts");
      await page.getByRole("button", { name: "Connect account" }).click();
      const connect = page.locator(".connect-source-dialog");
      await expect(connect).toBeVisible();
      await connect.getByRole("radio", { name: /ListenBrainz/ }).click();
      await connect.getByRole("button", { name: "Next", exact: true }).click();
      await expect(connect.getByLabel("ListenBrainz token")).toBeVisible();
      await expect(connect.getByLabel("Listening address (optional)")).toBeVisible();
      await expect(connect.getByText("Leave blank for ListenBrainz. A compatible server can use HTTPS, or HTTP on your private network.")).toBeVisible();
      await expect(connect.getByRole("button", { name: /^(Connect|Save changes)$/ })).toBeInViewport();
      await page.getByRole("button", { name: "Close source connection dialog" }).click();
      await page.goto("#/integrations/services?source=audiomuse-ai&section=configuration");
      const soundSettings = page.getByRole("link", { name: "Configure", exact: true });
      await expect(soundSettings).toHaveAttribute("href", "#/settings/discovery");
      await expect(page.locator(".service-page")).toContainText("Sound analysis (AudioMuse)");
      await soundSettings.click();
      await page.getByRole("button", { name: "Edit connection", exact: true }).click();
      const soundConnection = page.getByRole("form", { name: "AudioMuse connection", exact: true });
      await expect(soundConnection.getByLabel("AudioMuse address", { exact: true })).toBeVisible();
      await soundConnection.getByRole("button", { name: "Save", exact: true }).scrollIntoViewIfNeeded();
      await expect(soundConnection.getByRole("button", { name: "Save", exact: true })).toBeInViewport();
      await soundConnection.getByRole("button", { name: "Cancel", exact: true }).click();
      await expect(soundConnection).toBeHidden();
      await page.goto("#/integrations/accounts");
      await page.getByRole("link", { name: "Open Lumen Audio account" }).click();
      await page.locator(".service-account").getByRole("button", { name: /^Actions for / }).click();
      await page.getByRole("menuitem", { name: "Edit access" }).click();
      const access = page.locator(".access-dialog");
      await expect(access).toBeVisible();
      await access.getByRole("radio", { name: "Personal" }).check();
      await access.getByRole("button", { name: "Allstarr user" }).click();
      const ownerOptions = page.getByRole("listbox");
      await waitForSettledOptions(ownerOptions, "Listener");
      await ownerOptions.getByRole("option", { name: "Listener", exact: true }).click();
      await expect(ownerOptions).toBeHidden();
      await expect(access.getByRole("button", { name: "Allstarr user" })).toContainText("Listener");
      await access.getByRole("button", { name: "Save access" }).click();
      await expect(access).toBeHidden();

      await page.locator(".service-account").getByRole("button", { name: /^Actions for / }).click();
      await page.getByRole("menuitem", { name: "Edit access" }).click();
      await expect(page.getByRole("radio", { name: "One library" })).toHaveCount(0);
      await page.getByRole("radio", { name: "Shared" }).check();
      await page.getByRole("button", { name: "Save access" }).click();
      await expect(page.getByRole("alertdialog", { name: "Share this connection with household users?" })).toBeVisible();
    });

    test("A listener connects only Personal accounts", async ({ page }) => {
      await mockApi(page);
      await page.route("**/api/admin/auth/me", (route) => route.fulfill({ json: {
        authenticated: true, backend: "Jellyfin", listenersCanConnectOwnAccounts: true,
        user: { id: "listener", name: "Listener", isAdministrator: false },
      } }));
      const writes: Record<string, unknown>[] = [];
      await page.route("**/api/admin/provider-accounts", async (route) => {
        if (route.request().method() !== "POST") return route.fallback();
        writes.push(route.request().postDataJSON());
        await route.fulfill({ status: 201, json: { id: "new-account", revision: 1, enabled: true } });
      });
      await page.goto("#/integrations/accounts?source=spotify&connect=1");
      const dialog = page.locator(".connect-source-dialog");
      await expect(dialog).toBeVisible();
      const audience = dialog.getByRole("button", { name: "Who can use it?" });
      await expect(audience).toContainText("Just me");
      await audience.click();
      await expect(page.getByRole("option", { name: "Shared", exact: true })).toHaveCount(0);
      await expect(page.getByRole("option", { name: "One library" })).toHaveCount(0);
      await page.getByRole("option", { name: "Just me", exact: true }).click();
      await dialog.getByLabel("Spotify session cookie").fill("fixture-only-cookie");
      await dialog.getByRole("button", { name: /^(Connect|Save changes)$/ }).click();
      await expect(dialog).toBeHidden();
      expect(writes).toHaveLength(1);
      expect(writes[0]).toMatchObject({ providerId: "spotify", displayName: "", scope: "Personal", secret: { sessionCookie: "fixture-only-cookie" } });
    });

    test("A listener keeps existing account controls when connections are off and Shared accounts are read-only", async ({ page }) => {
      await mockApi(page);
      await page.route("**/api/admin/auth/me", (route) => route.fulfill({ json: {
        authenticated: true, backend: "Jellyfin", listenersCanConnectOwnAccounts: false,
        user: { id: "listener", name: "Listener", isAdministrator: false },
      } }));
      const fixture = responses["/api/admin/provider-accounts"] as { accounts: Record<string, unknown>[] };
      let shared = false;
      await page.route("**/api/admin/provider-accounts", (route) => route.fulfill({ json: {
        listenersCanConnectOwnAccounts: false, audienceUsers: [], accounts: [{ ...fixture.accounts[0],
          scope: shared ? "Shared" : "Personal", ownerUserId: shared ? null : "listener",
          canManage: !shared, canChangeAudience: false,
          configuration: shared ? {} : { region: "ca" }, configuredFields: shared ? [] : ["region"],
        }],
      } }));
      await page.goto("#/integrations/accounts");
      await expect(page.locator(".connect-source-dialog")).toHaveCount(0);
      await expect(page.getByRole("button", { name: "Connect account" })).toHaveCount(0);
      await page.getByRole("link", { name: "Open Lumen Audio account" }).click();
      await expect(page.getByRole("button", { name: "Edit configuration", exact: true })).toBeVisible();
      await page.locator(".service-account").getByRole("button", { name: /^Actions for / }).click();
      await expect(page.getByRole("menuitem", { name: /^(Disable|Pause)$/ })).toBeVisible();
      await expect(page.getByRole("menuitem", { name: "Edit access" })).toHaveCount(0);
      await page.keyboard.press("Escape");
      shared = true;
      await page.reload();
      await expect(page.getByRole("heading", { name: "Lumen Audio", level: 2 })).toBeVisible();
      await expect(page.getByRole("button", { name: "Edit configuration", exact: true })).toHaveCount(0);
      await expect(page.locator(".service-account").getByRole("button", { name: /^Actions for / })).toHaveCount(0);
      if (viewport.width < 768) await expectContainedMobileGeometry(page);
      else expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    });

    test("AudioMuse-AI configuration stays in Intelligence before the connection is ready", async ({ page }) => {
      await mockApi(page, { audioMuseUnavailable: true });
      await page.goto("#/intelligence?section=automation");

      const setup = page.locator(".provider-setup").filter({ hasText: "AudioMuse-AI connection" });
      await expect(setup).toContainText("Connect your self-hosted AudioMuse-AI server here");
      await expect(setup.getByRole("link", { name: /extension/i })).toHaveCount(0);
      await setup.getByRole("button", { name: "Connect AudioMuse-AI" }).click();
      const dialog = page.locator(".connect-source-dialog");
      await expect(dialog.getByLabel("AudioMuse-AI server URL")).toBeVisible();
      await expect(dialog.getByLabel("AudioMuse-AI access token")).toBeVisible();
      await expect(dialog.getByLabel("AudioMuse-AI music server")).toBeVisible();
    });

    test("unfinished AudioMuse-AI setup is recoverable and does not look unhealthy", async ({ page }) => {
      await mockApi(page, { audioMuseUnavailable: true, audioMuseDisabledAccount: true });
      await page.goto("#/intelligence?section=automation");

      const source = page.locator(".provider-choices label").filter({ hasText: "AudioMuse-AI" });
      await expect(source.getByText("AudioMuse-AI", { exact: true })).toBeVisible();
      await expect(source.locator('[data-slot="badge"]')).toHaveText("Not connected");
      const setup = page.locator(".provider-setup").filter({ hasText: "AudioMuse-AI connection" });
      await expect(setup).toContainText("The previous setup did not finish.");
      await setup.getByRole("button", { name: "Finish AudioMuse-AI setup" }).click();

      const dialog = page.getByRole("dialog", { name: "Configure AudioMuse-AI" });
      await dialog.getByLabel("AudioMuse-AI server URL").fill("http://audiomuse.test");
      const secretRequest = page.waitForRequest((request) =>
        request.url().endsWith("/api/admin/provider-accounts/audio-account/secret") && request.method() === "PUT");
      const enableRequest = page.waitForRequest((request) =>
        request.url().endsWith("/api/admin/provider-accounts/audio-account") && request.method() === "PATCH");
      await dialog.getByRole("button", { name: /^(Connect|Save changes)$/ }).click();
      await secretRequest;
      expect((await enableRequest).postDataJSON()).toMatchObject({ enabled: true, expectedRevision: 3 });
      await expect(dialog).not.toBeVisible();
    });

    test("Match and removal dialogs remain usable", async ({ page }) => {
      await mockApi(page);
      await page.goto("#/library/mappings");
      await page.locator(".mapping-row .open-match:visible, .mapping-row .row-action:visible").first().click();
      await expect(page.getByRole("dialog", { name: "Test song" })).toBeVisible();
      await expect(page.getByRole("button", { name: "Nothing fits" })).toBeInViewport();
      await page.getByRole("button", { name: "Nothing fits" }).click();
      const reject = page.getByRole("alertdialog", { name: "Nothing fits?" });
      await expect(reject).toBeVisible();
      await expect(reject.getByRole("button", { name: "Reject match" })).toBeInViewport();
      await reject.getByRole("button", { name: "Cancel" }).click();
      await expect(reject).toBeHidden();
      await page.getByRole("button", { name: "Close", exact: true }).click();
      await expect(page.getByRole("dialog", { name: "Test song" })).toBeHidden();
      await expect.poll(() => page.evaluate(() => getComputedStyle(document.body).pointerEvents)).toBe("auto");

      await page.goto("#/library/cached");
      await page.getByRole("button", { name: "Actions for Test song", exact: true }).click();
      await page.getByRole("menuitem", { name: "Remove", exact: true }).click();
      const removal = page.getByRole("alertdialog", { name: "Remove this song?" });
      await expect(removal).toBeVisible();
      await expect(removal.locator(".dialog-actions")).toHaveCSS("border-top-width", "0px");
      if (process.env.ALLSTARR_SCREENSHOT_DIR)
        await removal.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/remove-track-${viewport.width}.png` });
      await expect(removal.getByRole("button", { name: "Remove song" })).toBeInViewport();
    });

    test("keyboard dialogs honor reduced motion", async ({ page }) => {
      await page.emulateMedia({ reducedMotion: "reduce" });
      await mockApi(page);
      await page.goto("#/settings/extensions");
      await page.getByRole("button", { name: "Add extension" }).click();
      const dialog = page.getByRole("dialog", { name: "Add an extension" });
      await expect(dialog).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(dialog).toBeHidden();
    });
  });
}

test("Shared visual tokens meet typography, geometry, and contrast floors", async ({ page }) => {
  await mockApi(page);
  await page.goto("#/");
  const contract = await page.evaluate(() => {
    const root = getComputedStyle(document.documentElement);
    const eyebrowSample = document.createElement("p");
    eyebrowSample.className = "eyebrow";
    document.body.append(eyebrowSample);
    const eyebrow = getComputedStyle(eyebrowSample);
    const resolveColor = (value: string) => {
      const sample = document.createElement("span");
      sample.style.color = value;
      document.body.append(sample);
      const color = getComputedStyle(sample).color;
      sample.remove();
      return color;
    };
    const channels = (value: string) =>
      (value.match(/[\d.]+/g) ?? []).slice(0, 3).map(Number);
    const luminance = (value: string) => {
      const [red, green, blue] = channels(value).map((channel) => {
        const normalized = channel / 255;
        return normalized <= 0.04045
          ? normalized / 12.92
          : ((normalized + 0.055) / 1.055) ** 2.4;
      });
      return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
    };
    const contrast = (foreground: string, background: string) => {
      const values = [luminance(foreground), luminance(background)].sort((a, b) => b - a);
      return (values[0] + 0.05) / (values[1] + 0.05);
    };
    const canvas = resolveColor("var(--color-canvas)");
    const result = {
      spacing: ["--space-1", "--space-2", "--space-3", "--space-4"].map((token) =>
        Number.parseFloat(root.getPropertyValue(token)) * 16),
      controls: ["--control-sm", "--control-md", "--control-lg"].map((token) =>
        Number.parseFloat(root.getPropertyValue(token)) * 16),
      icons: ["--icon-sm", "--icon-md", "--icon-lg", "--icon-xl"].map((token) =>
        Number.parseFloat(root.getPropertyValue(token)) * 16),
      radii: ["--radius-sm", "--radius-md", "--radius-lg"].map((token) =>
        Number.parseFloat(root.getPropertyValue(token)) * 16),
      dataGeometry: ["--data-row-height", "--data-artwork-size"].map((token) =>
        Number.parseFloat(root.getPropertyValue(token)) * 16),
      fonts: [Number.parseFloat(root.getPropertyValue("--text-sm")) * 16, Number.parseFloat(eyebrow.fontSize)],
      contrast: {
        body: contrast(getComputedStyle(document.body).color, canvas),
        metadata: contrast(eyebrow.color, canvas),
        primary: contrast(resolveColor("var(--color-on-signal)"), resolveColor("var(--color-signal)")),
        focus: contrast(resolveColor("var(--focus-ring)"), canvas),
      },
    };
    eyebrowSample.remove();
    return result;
  });
  expect(contract.spacing).toEqual([4, 8, 12, 16]);
  expect(contract.controls).toEqual([32, 36, 40]);
  expect(contract.icons).toEqual([16, 18, 20, 24]);
  expect(contract.radii).toEqual([8, 12, 16]);
  expect(contract.dataGeometry).toEqual([70.4, 44]);
  expect(contract.fonts[0]).toBeGreaterThanOrEqual(14);
  expect(contract.fonts[1]).toBeGreaterThanOrEqual(12);
  expect(contract.contrast.body).toBeGreaterThanOrEqual(4.5);
  expect(contract.contrast.metadata).toBeGreaterThanOrEqual(4.5);
  expect(contract.contrast.primary).toBeGreaterThanOrEqual(4.5);
  expect(contract.contrast.focus).toBeGreaterThanOrEqual(3);
});

test("Shared search fields reserve icon space", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);

  for (const [route, label] of [
    ["#/library/playlists", "Filter playlists"],
    ["#/library/mappings", "Search"],
    ["#/library/cached", "Filter cached songs"],
    ["#/activity", "Search"],
  ] as const) {
    await page.goto(route);
    const input = page.getByRole("searchbox", { name: label });
    await expect(input).toBeVisible();
    const spacing = await input.evaluate((element) => {
      const inputBounds = element.getBoundingClientRect();
      const iconBounds = element.previousElementSibling!.getBoundingClientRect();
      return {
        iconRight: iconBounds.right,
        textStart: inputBounds.left + Number.parseFloat(getComputedStyle(element).paddingLeft),
      };
    });
    expect(spacing.iconRight).toBeLessThanOrEqual(spacing.textStart);
  }

  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Import playlist" }).click();
  const dialog = page.getByRole("dialog", { name: "Import a playlist" });
  await openOtherPlaylistSources(dialog);
  await dialog.getByRole("radio", { name: /Spotify/ }).check();
  await expect(dialog.getByRole("searchbox", { name: "Find a source playlist" })).toBeVisible();
});

test("Dashboard primary tracks stay geometrically aligned", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await mockApi(page);

  const expectSameTrack = async (firstSelector: string, secondSelector: string) => {
    const [first, second] = await Promise.all([
      page.locator(firstSelector).first().boundingBox(),
      page.locator(secondSelector).first().boundingBox(),
    ]);
    expect(first).toBeTruthy();
    expect(second).toBeTruthy();
    expect(Math.abs(first!.x - second!.x)).toBeLessThanOrEqual(1);
    expect(Math.abs(first!.width - second!.width)).toBeLessThanOrEqual(1);
  };

  await page.goto("#/");
  const setupTiles = page.locator(".setup-grid > .setup-tile");
  await expect(setupTiles).toHaveCount(6);
  const homeBoxes = await setupTiles.evaluateAll(items => items.map(item => item.getBoundingClientRect().toJSON()));
  expect(Math.max(...homeBoxes.map(box => box.width)) - Math.min(...homeBoxes.map(box => box.width))).toBeLessThanOrEqual(1);
  expect(Math.abs(homeBoxes[0].y - homeBoxes[2].y)).toBeLessThanOrEqual(1);
  await expectSameTrack(".home-view", ".home-summary");
  await expectSameTrack(".home-summary", ".home-mixes");
  const week = (await page.locator(".week-panel").boundingBox())!;
  const recent = (await page.locator(".recent-panel").boundingBox())!;
  expect(Math.abs(week.y - recent.y)).toBeLessThanOrEqual(1);
  expect(Math.abs(week.width - recent.width)).toBeLessThanOrEqual(1);
  expect(recent.x).toBeGreaterThan(week.x + week.width);

  await page.goto("#/settings/general");
  await expect(page.locator(".settings-workspace")).toBeVisible();

  await page.goto("#/library/playlists");
  await expect(page.locator(".library-tabs")).toBeVisible();
  await expectSameTrack(".workspace-header", ".library-tabs");
  const playlistList = page.locator(".library-list");
  await expect(playlistList).toBeVisible();
  expect((await playlistList.boundingBox())?.height ?? Infinity).toBeLessThan(400);

  await page.goto("#/integrations/routing");
  const routingGroups = page.locator(".routing-groups");
  const routingCards = routingGroups.locator(":scope > .routing-group");
  await expect(routingCards).toHaveCount(1);
  await expectSameTrack(".routing-groups", ".routing-groups > .routing-group");

  await page.goto("#/for-you");
  await expect(page.locator(".intelligence-content")).toBeVisible();
  await expectSameTrack('[aria-label="For you sections"]', ".intelligence-content");

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("#/");
  await expect(setupTiles).toHaveCount(6);
  await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  const [lastPanel, mobileNav] = await Promise.all([
    page.locator(".home-view").boundingBox(),
    page.locator(".sidebar").boundingBox(),
  ]);
  expect(lastPanel).toBeTruthy();
  expect(mobileNav).toBeTruthy();
  expect(lastPanel!.y + lastPanel!.height).toBeLessThanOrEqual(mobileNav!.y + 1);
  const workspaceBottomPadding = await page.locator(".workspace").evaluate((element) =>
    Number.parseFloat(getComputedStyle(element).paddingBottom));
  expect(workspaceBottomPadding).toBeGreaterThanOrEqual(mobileNav!.height);
});

test("Compact panel headings keep actions aligned and contained", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockApi(page);

  for (const { route, heading, actions, stacked } of [
    { route: "#/library/playlists", heading: ".library-heading", actions: ".library-actions", stacked: false },
    { route: "#/library/cached", heading: ".library-downloads-heading", actions: ".library-downloads-heading-actions", stacked: false },
    { route: "#/integrations/services", heading: ".sources-heading", actions: ".sources-heading-actions", stacked: true },
  ]) {
    await page.goto(route);
    const panelHeading = page.locator(heading);
    await expect(panelHeading).toBeVisible();
    const [headingBox, copyBox, actionBox] = await Promise.all([
      panelHeading.boundingBox(),
      panelHeading.locator(stacked ? ".page-status" : ":scope > :first-child").boundingBox(),
      panelHeading.locator(actions).boundingBox(),
    ]);
    expect(headingBox).toBeTruthy();
    expect(copyBox).toBeTruthy();
    expect(actionBox).toBeTruthy();
    expect(actionBox!.x).toBeGreaterThanOrEqual(headingBox!.x - 1);
    expect(actionBox!.x + actionBox!.width).toBeLessThanOrEqual(headingBox!.x + headingBox!.width + 1);
    if (stacked) {
      expect(Math.abs(copyBox!.x - actionBox!.x)).toBeLessThanOrEqual(1);
      expect(Math.abs(headingBox!.width - actionBox!.width)).toBeLessThanOrEqual(1);
      expect(actionBox!.y).toBeGreaterThanOrEqual(copyBox!.y + copyBox!.height);
    } else {
      expect(copyBox!.x + copyBox!.width).toBeLessThanOrEqual(actionBox!.x);
      expect(Math.abs(copyBox!.y + copyBox!.height / 2 - actionBox!.y - actionBox!.height / 2)).toBeLessThanOrEqual(1);
    }
    expect(await panelHeading.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  }

  await page.goto("#/library/playlists");
  const playlistActions = page.locator(".library-actions");
  await expect(playlistActions.getByRole("button", { name: "Refresh playlists", exact: true })).toBeVisible();
  await expect(playlistActions.getByRole("button", { name: "Import playlist", exact: true })).toBeVisible();
  const actionButtons = await playlistActions.getByRole("button").evaluateAll(items =>
    items.map(item => item.getBoundingClientRect().toJSON()));
  expect(actionButtons).toHaveLength(2);
  expect(Math.abs(actionButtons[0].y - actionButtons[1].y)).toBeLessThanOrEqual(1);
  expect(actionButtons[0].x + actionButtons[0].width).toBeLessThanOrEqual(actionButtons[1].x);
});

test("Contextual dialog tabs scroll inside their panels at narrow widths", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  const screenshotTheme = process.env.ALLSTARR_SCREENSHOT_THEME;
  if (screenshotTheme === "light" || screenshotTheme === "dark")
    await page.addInitScript((theme) => localStorage.setItem("allstarr.theme", theme), screenshotTheme);
  await mockApi(page);

  const expectContainedActiveTab = async (tabs: Locator) => {
    await expect(tabs).toHaveCSS("overflow-x", "auto");
    const items = tabs.getByRole("tab");
    const heights = await items.evaluateAll((elements) =>
      elements.map((element) => element.getBoundingClientRect().height));
    expect(heights.every((height) => height >= 44)).toBe(true);
    await items.last().click();
    await expect.poll(() => tabs.evaluate((element) => {
      const active = element.querySelector<HTMLElement>('[aria-selected="true"]');
      if (!active) return false;
      const container = element.getBoundingClientRect();
      const item = active.getBoundingClientRect();
      return item.left >= container.left - 1 && item.right <= container.right + 1;
    })).toBe(true);
  };

  await page.goto("#/library/playlists");
  await page.getByRole("button", { name: "Open Test playlist playlist details" }).click();
  const playlist = page.getByRole("dialog", { name: "Test playlist" });
  await expect(playlist).not.toContainText("Where songs play from");
  await expect(playlist.locator(".playlist-facts")).toBeVisible();
  await expect(playlist.locator(".playlist-overview .contextual-tabs")).toHaveCount(0);
  expect(await playlist.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(true);
  if (process.env.ALLSTARR_SCREENSHOT_DIR)
    await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/${screenshotTheme ?? "system"}-mobile-320-playlist-tabs.png` });
  await playlist.getByRole("button", { name: "Close" }).click();

  await page.goto("#/integrations/services");
  const lumen = page.locator(".source-row").filter({ hasText: "Lumen Audio" });
  await lumen.getByRole("link").first().click();
  await expect(page.getByRole("heading", { name: "Lumen Audio", level: 2 })).toBeVisible();
  await expectContainedMobileGeometry(page);
  if (process.env.ALLSTARR_SCREENSHOT_DIR)
    await page.screenshot({ path: `${process.env.ALLSTARR_SCREENSHOT_DIR}/${screenshotTheme ?? "system"}-mobile-320-source-page.png` });
});

test("Compact dialogs share symmetric actions and readable progress", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  await mockApi(page);

  await page.goto("#/library/playlists");
  const toolbar = page.locator("header").filter({ has: page.getByRole("heading", { name: "Playlists", exact: true }) });
  await expect(toolbar.getByRole("button")).toHaveCount(2);
  await expect(toolbar.getByRole("button", { name: "Refresh playlists", exact: true })).toBeVisible();
  await expect(toolbar.getByRole("button", { name: "Import playlist", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Import playlist" }).click();
  const playlistDialog = page.getByRole("dialog", { name: "Import a playlist" });
  await expect(playlistDialog).toBeVisible();
  const steps = await playlistDialog.locator(".playlist-add-steps button").evaluateAll((buttons) =>
    buttons.map((button) => {
      const rect = button.getBoundingClientRect(), area = getComputedStyle(button, "::after");
      return { height: Math.max(rect.height, area.content === "none" ? 0 : parseFloat(area.minHeight) || 0), width: rect.width, y: Math.round(rect.y) };
    }),
  );
  expect(new Set(steps.map(({ y }) => y)).size).toBe(1);
  expect(steps.every(({ height }) => height >= 44)).toBe(true);
  expect(Math.max(...steps.map(({ width }) => width)) - Math.min(...steps.map(({ width }) => width))).toBeLessThanOrEqual(1);
  const playlistActions = await playlistDialog.locator(":scope > .dialog-actions > *").evaluateAll((actions) =>
    actions.map((action) => {
      const rect = action.getBoundingClientRect();
      return { width: rect.width, y: Math.round(rect.y) };
    }),
  );
  expect(Math.abs(playlistActions[0].width - playlistActions[1].width)).toBeLessThanOrEqual(1);
  expect(new Set(playlistActions.map(({ y }) => y)).size).toBe(2);
  await playlistDialog.getByRole("button", { name: "Cancel" }).click();

  await page.goto("#/integrations/services");
  await expect(page.getByRole("heading", { name: "Sources", level: 1 })).toBeVisible();
  const serviceButtons = page.locator(".sources-heading-actions button");
  await expect(serviceButtons).toHaveCount(2);
  await expect(serviceButtons.last()).toBeVisible();
  const entranceSamples = await page.locator(".integrations-workspace").evaluate(element => {
    const animation = getComputedStyle(element).animation;
    (element as HTMLElement).style.animation = "none";
    element.getBoundingClientRect();
    (element as HTMLElement).style.animation = animation;
    element.getBoundingClientRect();
    const entrance = element.getAnimations()[0];
    if (!entrance) return [];
    entrance.pause();
    const duration = Number(entrance.effect!.getComputedTiming().duration);
    const samples = Array.from({ length: 100 }, (_, index) => {
      entrance.currentTime = duration * index / 100;
      return {
        time: entrance.currentTime,
        transform: getComputedStyle(element).transform,
        heights: Array.from(element.querySelectorAll(".sources-heading-actions button"), button => button.getBoundingClientRect().height)
      };
    });
    entrance.finish();
    return samples.filter(sample => sample.heights.some(height => height < 44));
  });
  expect(entranceSamples, JSON.stringify(entranceSamples.slice(0, 5))).toEqual([]);
  for (const width of [320, 390, 650]) {
    await page.setViewportSize({ width, height: 844 });
    const serviceActions = await serviceButtons.evaluateAll((buttons) =>
      buttons.map((button) => {
        const rect = button.getBoundingClientRect();
        return { contained: button.scrollWidth <= button.clientWidth, right: rect.right, height: rect.height };
      }),
    );
    expect(serviceActions.every(({ contained, right }) => contained && right <= width), `${width}px ${JSON.stringify(serviceActions)}`).toBe(true);
    expect(serviceActions.every(({ height }) => height >= 44)).toBe(true);
  }
  await page.setViewportSize({ width: 320, height: 844 });
  await page.goto("#/sources/accounts");
  await page.getByRole("button", { name: "Connect account" }).click();
  const sourceDialog = page.getByRole("dialog", { name: "Connect an account" });
  await expect(sourceDialog).toBeVisible();
  await expect(sourceDialog.locator(":scope > .dialog-actions > *")).toHaveCount(2);
  const sourceActions = await sourceDialog.locator(":scope > .dialog-actions > *").evaluateAll((actions) =>
    actions.map((action) => {
      const rect = action.getBoundingClientRect(), area = getComputedStyle(action, "::after");
      return { height: Math.max(rect.height, area.content === "none" ? 0 : parseFloat(area.minHeight) || 0), width: rect.width, y: rect.y };
    }),
  );
  expect(sourceActions).toHaveLength(2);
  expect(sourceActions.every(({ height }) => height >= 44)).toBe(true);
  expect(Math.abs(sourceActions[0].width - sourceActions[1].width)).toBeLessThanOrEqual(1);
  expect(Math.abs(sourceActions[0].y - sourceActions[1].y)).toBeGreaterThanOrEqual(44);
  expect(await sourceDialog.evaluate((dialog) => dialog.scrollWidth <= dialog.clientWidth)).toBe(true);
});

test("Sparse data surfaces use the shared compact empty state", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  await mockApi(page);

  const expectCompactEmpty = async (selector: string, title: string) => {
    const empty = page.locator(selector);
    await expect(empty.getByRole("heading", { name: title, exact: true })).toBeVisible();
    await expect(empty).toHaveCSS("min-height", "0px");
    await expect(empty).toHaveCSS("border-top-style", "dashed");
    await expect(empty).toHaveCSS("border-radius", "14px");
    await expect(empty).toHaveCSS("align-items", "flex-start");
    await expect(empty).toHaveCSS("padding", "16px");
    const bounds = await empty.boundingBox();
    expect(bounds?.height ?? Infinity).toBeLessThanOrEqual(288);
    expect(await empty.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  };

  await page.goto("#/activity");
  await expectCompactEmpty(".event-log-panel .empty-state", "No activity yet");

  await page.route("**/api/admin/downloads**", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      storage: new URL(route.request().url()).searchParams.get("storage"),
      files: [], totalSize: 0, totalSizeFormatted: "0 B", count: 0,
      managedCount: 0, diagnosticCount: 0,
    }),
  }));
  await page.goto("#/library/cached");
  await expectCompactEmpty(".library-downloads .empty-state", "No cached songs");
});

test("Operational tables adapt before the workspace needs horizontal scrolling", async ({ page }) => {
  await mockApi(page);
  for (const width of [600, 768, 820, 1024, 1280]) {
    await page.setViewportSize({ width, height: 800 });
    for (const [route, selector, row] of [
      ["#/integrations/services", ".service-rows", ".source-row"],
      ["#/integrations/accounts", ".accounts-table-wrap", "tbody tr"],
    ]) {
      await page.goto(route);
      const surface = page.locator(selector);
      await expect(surface.locator(row).first()).toBeVisible();
      await expect.poll(() => surface.evaluate(element => element.scrollWidth - element.clientWidth),
        { message: `${route} fits at ${width}px` }).toBeLessThanOrEqual(1);
      if (selector === ".accounts-table-wrap") {
        await expect.poll(() => surface.locator("td:not(:first-child)").evaluateAll(cells => cells
          .filter(cell => cell.scrollWidth > cell.clientWidth + 1)
          .map(cell => ({ label: cell.getAttribute("data-label"), text: cell.textContent?.trim(), width: cell.clientWidth, scroll: cell.scrollWidth,
            children: [...cell.children].map(child => ({ class: child.className, width: child.getBoundingClientRect().width, scroll: child.scrollWidth })) }))),
        { message: `Account cells fit at ${width}px` }).toEqual([]);
      }
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    }
  }
});

for (const theme of ["light", "dark"]) {
  test(`Phone side panels cover navigation and restore it after Escape in ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await page.emulateMedia({ reducedMotion: "reduce" });
    await mockApi(page);
    await page.goto("#/integrations/services");
    const opener = page.getByRole("link", { name: "Open Lumen Audio", exact: true });
    await opener.click();
    const panel = page.getByRole("dialog", { name: "Lumen Audio", exact: true });
    await expect(panel).toBeVisible();
    await expect(panel).toHaveAttribute("aria-modal", "true");
    const navigation = page.locator(".mobile-navigation");
    const home = navigation.locator('a[href="#/"]');
    await expect(navigation).toHaveAttribute("inert", "");
    await home.focus();
    await expect(home).not.toBeFocused();
    expect(await navigation.locator("a, button").evaluateAll(elements => elements.every(element => {
      const box = element.getBoundingClientRect();
      return !element.contains(document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2));
    }))).toBe(true);
    const layers = await page.evaluate(() => ({
      navigation: Number.parseInt(getComputedStyle(document.querySelector(".sidebar")!).zIndex),
      overlay: Number.parseInt(getComputedStyle(document.querySelector(".side-panel-overlay")!).zIndex),
      panel: Number.parseInt(getComputedStyle(document.querySelector(".side-panel")!).zIndex),
    }));
    expect(layers.overlay).toBeGreaterThan(layers.navigation);
    expect(layers.panel).toBeGreaterThan(layers.overlay);
    for (const height of [844, 667]) {
      await page.setViewportSize({ width: 390, height });
      await expect(panel).toHaveCSS("height", `${height}px`);
      const sheet = await panel.boundingBox();
      const overlay = await page.locator(".side-panel-overlay").boundingBox();
      const footer = await panel.locator(".panel-footer").boundingBox();
      expect(sheet!.y).toBe(0); expect(sheet!.height).toBe(height);
      expect(overlay!.y).toBe(0); expect(overlay!.height).toBe(height);
      expect(footer!.y + footer!.height).toBeCloseTo(height, 0);
    }
    await page.setViewportSize({ width: 1280, height: 667 });
    await expect(navigation).not.toHaveAttribute("inert");
    const footer = await panel.locator(".panel-footer").boundingBox();
    expect(footer!.y + footer!.height).toBeCloseTo(667, 0);
    await page.setViewportSize({ width: 390, height: 667 });
    await expect(navigation).toHaveAttribute("inert", "");
    await panel.getByRole("button", { name: "Check now", exact: true }).focus();
    await page.keyboard.press("Tab");
    expect(await panel.evaluate(element => element.contains(document.activeElement))).toBe(true);
    await page.keyboard.press("Shift+Tab");
    await expect(panel.getByRole("button", { name: "Check now", exact: true })).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(panel).toHaveCount(0);
    await expect(opener).toBeFocused();
    await expect(navigation).not.toHaveAttribute("inert");
    await home.focus(); await expect(home).toBeFocused();
    await home.click(); await expect(page).toHaveURL(/#\/$/);
  });
}

for (const width of [390, 1280]) {
  test(`Source panels return keyboard focus after repeated closing at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.emulateMedia({ reducedMotion: "reduce" });
    await mockApi(page);
    await page.goto("#/integrations/services");
    const opener = page.getByRole("link", { name: "Open Lumen Audio", exact: true });
    const panel = page.getByRole("dialog", { name: "Lumen Audio", exact: true });
    for (const close of ["button", "Escape"]) {
      await opener.focus();
      await page.keyboard.press("Enter");
      await expect(panel).toBeVisible();
      if (close === "button") await panel.getByRole("button", { name: "Close", exact: true }).click();
      else await page.keyboard.press("Escape");
      await expect(panel).toHaveCount(0);
      await expect(page.locator(".mobile-navigation")).not.toHaveAttribute("inert");
      await expect(opener).toBeFocused();
    }
  });

  test(`Source panel scrolling keeps the header and footer anchored at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.emulateMedia({ reducedMotion: "reduce" });
    await mockApi(page);
    await page.goto("#/integrations/services/lumen-audio");
    const panel = page.getByRole("dialog", { name: "Lumen Audio", exact: true });
    await panel.locator(".source-more > summary").click();
    const action = panel.getByRole("button", { name: "Measure start time", exact: true });
    await action.evaluate(element => element.scrollIntoView({ block: "start" }));
    await expect(action).toBeInViewport();
    await expect(panel.getByRole("heading", { name: "Lumen Audio", exact: true })).toBeInViewport();
    expect(await panel.evaluate(element => element.scrollTop)).toBe(0);
    const footer = await panel.locator(".panel-footer").boundingBox();
    expect(footer!.y + footer!.height).toBeCloseTo(844, 0);
  });

  test(`Notification actions remain usable when source panels open and close at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.emulateMedia({ reducedMotion: "reduce" });
    await mockApi(page);
    const fixture = responses["/api/admin/provider-accounts"] as { accounts: { enabled: boolean; revision: number }[] };
    let account = { ...fixture.accounts[0] };
    const changes: boolean[] = [];
    await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: { ...fixture, accounts: [account] } }));
    await page.route("**/api/admin/provider-accounts/account", route => {
      const update = route.request().postDataJSON();
      changes.push(update.enabled);
      account = { ...account, enabled: update.enabled, revision: account.revision + 1 };
      return route.fulfill({ json: account });
    });
    await page.goto("#/integrations/services/lumen-audio");
    const panel = page.getByRole("dialog", { name: "Lumen Audio", exact: true });
    const region = page.getByRole("region", { name: "Notifications" });
    await page.locator(".source-more > summary").click();
    await panel.getByRole("button", { name: "Actions for Lumen account" }).click();
    await page.getByRole("menuitem", { name: "Disable", exact: true }).click();
    const undo = region.getByRole("button", { name: "Undo", exact: true });
    await undo.focus(); await expect(undo).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(region.locator(".toast")).toHaveCount(0);
    expect(changes).toEqual([false, true]);
    await expect(panel).toBeVisible();

    await panel.getByRole("button", { name: "Actions for Lumen account" }).click();
    await page.getByRole("menuitem", { name: "Disable", exact: true }).click();
    await expect(undo).toBeVisible();
    await panel.getByRole("button", { name: "Close", exact: true }).click();
    await expect(panel).toHaveCount(0);
    await undo.focus(); await expect(undo).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(region.locator(".toast")).toHaveCount(0);
    expect(changes).toEqual([false, true, false, true]);
  });
}

for (const target of [
  { name: "Home", route: "#/", endpoint: "/api/admin/ui/now-playing", fixture: "/api/admin/ui/now-playing", field: "items", property: "title" },
  { name: "Sources", route: "#/integrations/services", endpoint: "/api/admin/ui/schema", fixture: "/api/admin/ui/schema", field: "providers", property: "name" },
  { name: "Activity", route: "#/activity", endpoint: "/api/admin/ui/activity", fixture: "/api/admin/ui/activity?limit=8", field: "items", property: "label" },
  { name: "Playlists", route: "#/library/playlists", endpoint: "/api/admin/playlist-links", fixture: "/api/admin/playlist-links", field: "playlistLinks", property: "name" },
  { name: "Storage", route: "#/library/cached", endpoint: "/api/admin/downloads", fixture: "", field: "files", property: "title" },
]) for (const width of [390, 1280]) {
  test(`freshness reconciles ${target.name} silently at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await mockApi(page);
    await page.addInitScript(() => {
      class Stream {
        listeners = new Map<string, (event: MessageEvent<string>) => void>();
        onerror?: () => void;
        constructor() { (window as any).__freshnessStream = this; }
        addEventListener(name: string, listener: (event: MessageEvent<string>) => void) {
          this.listeners.set(name, listener);
          if (name === "stream-status") setTimeout(() => listener(new MessageEvent(name, { data: "{}" })), 0);
        }
        close() {}
      }
      Object.defineProperty(window, "EventSource", { value: Stream });
    });
    let version = 1;
    let hold = false;
    const gate: { release?: () => void } = {};
    const openGate = () => gate.release?.();
    await page.route(url => url.pathname === target.endpoint, async route => {
      if (hold) await new Promise<void>(resolve => { gate.release = resolve; });
      const body = target.fixture ? structuredClone(responses[target.fixture]) as Record<string, any> : {
        storage: "cache", files: [{ path: "/managed/song.flac", title: "Test song", artist: "Artist", fileName: "song.flac", size: 1024,
          storage: "cache", removable: true }], count: 1, totalSize: 1024, totalSizeFormatted: "1 KiB",
      };
      body[target.field][0][target.property] = `Freshness ${version}`;
      await route.fulfill({ json: body });
    });
    await page.goto(target.route);
    if (target.name === "Sources") await page.getByRole("radio", { name: /^All / }).click();
    await expect(page.getByText("Freshness 1", { exact: true }).first()).toBeVisible();
    // A healthy SSE connection can miss writes without an outbox event.
    version = 2;
    await expect(page.getByText("Freshness 2", { exact: true }).first()).toBeVisible({ timeout: 8000 });
    version = 3;
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await expect(page.getByText("Freshness 3", { exact: true }).first()).toBeVisible();
    version = 4;
    await page.evaluate(() => {
      const stream = (window as any).__freshnessStream;
      stream.onerror?.();
      stream.listeners.get("stream-status")?.(new MessageEvent("stream-status", { data: "{}" }));
    });
    await expect(page.getByText("Freshness 4", { exact: true }).first()).toBeVisible();
    const refresh = page.getByRole("button", { name: "Refresh", exact: true });
    if (await refresh.count()) {
      hold = true;
      await page.evaluate(() => window.dispatchEvent(new Event("focus")));
      await expect.poll(() => Boolean(gate.release)).toBe(true);
      await expect(refresh).toHaveAttribute("aria-busy", "false");
      await expect(refresh.locator("svg")).not.toHaveClass(/refresh-spinning/);
      await expect(page.getByText("Freshness 4", { exact: true }).first()).toBeVisible();
      hold = false; openGate();
      await expect.poll(() => refresh.isEnabled()).toBe(true);
      await page.waitForTimeout(100);
      hold = true; gate.release = undefined;
      await refresh.click();
      await expect(page.getByRole("button", { name: "Refreshing…", exact: true })).toHaveAttribute("aria-busy", "true");
      await expect.poll(() => Boolean(gate.release)).toBe(true);
      hold = false; openGate();
      await expect(refresh).toHaveAttribute("aria-busy", "false");
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
}

test("Desktop playback, task and review previews", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  await page.addInitScript(() => localStorage.setItem("allstarr.theme", "dark"));
  await mockApi(page);
  const playback = structuredClone(responses["/api/admin/ui/now-playing"]) as { items: Record<string, unknown>[] };
  Object.assign(playback.items[0], { itemId: "library-song", providerId: "library", catalogProviderId: null });
  await page.route("**/api/admin/ui/now-playing", route => route.fulfill({ json: playback }));
  await page.goto("#/");
  await expect(page.getByRole("region", { name: "Now playing" })).toContainText("Rocket");
  await expect(page.locator(".playback-meta")).toContainText("from your library");
  await testInfo.attach("desktop-library", { body: await page.screenshot({ fullPage: true }), contentType: "image/png" });

  await page.route("**/api/admin/activity/tasks*", route => route.fulfill({ json: { items: [{
    id: "lyrics-refresh", name: "Look for better lyrics", kind: "lyrics", state: "Scheduled", enabled: true,
    cadence: "Daily · UTC", detail: "Looks for better lyrics and keeps pinned choices.",
    lastRunAt: "2026-11-05T08:00:00Z", nextRunAt: "2026-11-06T08:00:00Z",
    runEndpoint: "/api/admin/activity/tasks/lyrics/run", actionHref: "#/activity?view=history", actionLabel: "View history",
  }] } }));
  await page.goto("#/activity?view=tasks");
  await page.getByRole("button", { name: /Look for better lyrics/ }).click();
  await expect(page.getByRole("dialog", { name: "Look for better lyrics", exact: true }).getByRole("button", { name: "Run now", exact: true })).toBeVisible();
  await testInfo.attach("desktop-task", { body: await page.screenshot({ fullPage: true }), contentType: "image/png" });
  await page.keyboard.press("Escape");

  await page.route("**/api/admin/extensions/packages", route => route.fulfill({ json: [{
    id: "review", extensionId: "lumen-audio", displayName: "Lumen Audio", version: "2.0.7", revision: 1,
    lifecycle: "reviewrequired", state: "reviewrequired", active: false, installed: false,
    permissionReviewRequired: true, hasPermissions: true, capabilities: ["metadata", "streaming"],
  }] }));
  await page.route("**/packages/review/permissions", route => route.fulfill({ json: [{
    id: "network", permissionKind: "network", permissionValue: "https://music.example.test/", required: true, decision: "pending",
  }] }));
  await page.goto("#/settings/extensions");
  await page.getByRole("list", { name: "Extensions", exact: true }).getByRole("button", { name: "Review", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Review permissions", exact: true })).toBeVisible();
  await testInfo.attach("desktop-review", { body: await page.screenshot({ fullPage: true }), contentType: "image/png" });
  await page.keyboard.press("Escape");
  await page.goto("#/library/mappings");
  await expect(page.getByRole("radiogroup", { name: "Match filters" })).toBeVisible();
  await testInfo.attach("desktop-mappings", { body: await page.screenshot({ fullPage: true }), contentType: "image/png" });
});
