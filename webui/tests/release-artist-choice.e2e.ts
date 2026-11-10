import { expect, test, type Page, type TestInfo } from "@playwright/test";
import type { FollowedReleaseArtist, ReleaseArtistCandidate, ReleaseInbox, ReleaseInboxEntry } from "../src/lib/api";
import { appearance, mockForYou } from "./for-you-fixtures";
import { expectContainedMobileGeometry } from "./mobile-geometry";

type Choice = { libraryArtistId: string; providerId: string | null; artistId: string | null };
const reason = "Several artists share this name. Choose the one you listen to.";
const north: FollowedReleaseArtist = { providerId: "qobuz", artistId: "north", displayName: "North" };
const candidates: ReleaseArtistCandidate[] = [
  { providerId: "qobuz", artistId: "willow-other", displayName: "Willow", serviceName: "Qobuz", imageUrl: "/api/admin/releases/artwork/qobuz/willow-other", hints: ["First light", "Night drive"] },
  { providerId: "deezer", artistId: "willow-right", displayName: "Willow", serviceName: "Deezer", imageUrl: "/api/admin/releases/artwork/deezer/willow-right", hints: ["Soft edges", "Quiet water", "Morning song"] },
];
function release(id: string, title: string, artist: string): ReleaseInboxEntry {
  return { id, title, artist, releaseDate: "2026-11-06", datePrecision: "day", type: "album", isExplicit: false, edition: "", exclusionReason: null, sources: [], artworkUrl: `/api/admin/releases/artwork/qobuz/${id}` };
}
async function mockArtistChoices(page: Page) {
  await mockForYou(page);
  const state = {
    artist: { providerId: "jellyfin", artistId: "native-willow", libraryArtistId: "native-willow", displayName: "Willow", listenerConfirmed: false, resolutionReason: reason } as FollowedReleaseArtist,
    currentChoice: null as ReleaseArtistCandidate | null,
    choices: [] as Choice[], reads: 0,
  };
  await page.route("**/api/admin/stats/art?**", route => route.fulfill({ contentType: "image/svg+xml", body: '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64"><rect width="64" height="64" fill="#496d66"/></svg>' }));
  await page.route("**/api/admin/releases**", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    if (path === "/api/admin/releases/artists/candidates") {
      expect(url.searchParams.get("libraryArtistId")).toBe("native-willow");
      return route.fulfill({ json: { artist: state.artist, candidates, currentChoice: state.currentChoice ?? candidates.find(item => item.providerId === state.artist.providerId && item.artistId === state.artist.artistId) ?? null } });
    }
    if (path === "/api/admin/releases/artists/choice" && request.method() === "PUT") {
      const choice = request.postDataJSON() as Choice;
      state.choices.push(choice);
      const selected = candidates.find(item => item.providerId === choice.providerId && item.artistId === choice.artistId) ?? (state.currentChoice?.providerId === choice.providerId && state.currentChoice?.artistId === choice.artistId ? state.currentChoice : null);
      state.currentChoice = selected;
      state.artist = { ...state.artist, providerId: selected?.providerId ?? "jellyfin", artistId: selected?.artistId ?? "native-willow", listenerConfirmed: true, resolutionReason: null };
      return route.fulfill({ json: state.artist });
    }
    if (path === "/api/admin/releases/artists") {
      state.reads++;
      return route.fulfill({ json: [state.artist, north] });
    }
    if (path === "/api/admin/releases") {
      const inbox: ReleaseInbox = {
        entries: [release("north-album", "Open skies", "North"), ...(state.artist.providerId === "jellyfin" ? [] : [release("willow-album", "Soft edges", "Willow")])],
        excluded: [], coverage: [{ artist: state.artist, status: state.artist.providerId === "jellyfin" ? "library-only" : "known", checkedAt: null, reason: state.artist.resolutionReason }],
      };
      return route.fulfill({ json: inbox });
    }
    return route.fallback();
  });
  return state;
}
async function openFollowing(page: Page) {
  await page.getByRole("button", { name: "Following (2)", exact: true }).click();
  return page.getByRole("dialog", { name: "Followed artists (2)", exact: true });
}
async function screenshot(page: Page, testInfo: TestInfo, name: string, width: number) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  if (width === 390) await expectContainedMobileGeometry(page);
  await page.screenshot({ path: testInfo.outputPath(`${name}.png`) });
}

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });
for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Release artist choice at ${width}px ${theme}`, async ({ page }, testInfo) => {
    await appearance(page, width, theme);
    const state = await mockArtistChoices(page);
    await page.goto("/#/for-you?section=releases");
    const following = await openFollowing(page);
    await expect(following.getByText(reason, { exact: true })).toBeVisible();
    await screenshot(page, testInfo, "following-unresolved", width);
    await following.getByRole("button", { name: "Choose the right artist for Willow", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Choose the right artist", exact: true });
    await expect(dialog.getByText("Soft edges · Quiet water · Morning song", { exact: true })).toBeVisible();
    await expect(dialog.getByRole("radio", { name: "Willow on Qobuz", exact: true })).toBeVisible();
    await expect(dialog.getByRole("radio", { name: "Willow on Deezer", exact: true })).toBeVisible();
    await expect(dialog.getByText("Current choice: None of these", { exact: true })).toBeVisible();
    const targets = await dialog.locator(".artist-option").evaluateAll(elements => elements.map(element => element.getBoundingClientRect().height));
    expect(targets.every(height => height >= 44)).toBe(true);
    await screenshot(page, testInfo, "artist-choices", width);
    if (!((width === 1280 && theme === "dark") || (width === 390 && theme === "light"))) return;

    await dialog.getByRole("radio", { name: "Willow on Deezer", exact: true }).check();
    const reads = state.reads;
    await dialog.getByRole("button", { name: "Save choice", exact: true }).click();
    await expect(dialog).toBeHidden();
    await expect(following).toBeVisible();
    await expect(following.getByText(reason, { exact: true })).toHaveCount(0);
    await expect(page.getByText("Artist choice saved for Willow", { exact: true })).toBeVisible();
    expect(state.reads).toBeGreaterThan(reads);
    expect(state.choices).toEqual([{ libraryArtistId: "native-willow", providerId: "deezer", artistId: "willow-right" }]);
    await following.getByRole("button", { name: "Done", exact: true }).click();
    await expect(page.getByRole("article", { name: "Soft edges by Willow", exact: true })).toBeVisible();
    await expect(page.getByRole("article", { name: "Open skies by North", exact: true })).toBeVisible();

    await page.reload();
    const reopened = await openFollowing(page);
    await reopened.getByRole("button", { name: "Change artist for Willow", exact: true }).click();
    await expect(dialog.getByText("Current choice: Willow · Deezer", { exact: true })).toBeVisible();
    await expect(dialog.getByRole("radio", { name: "Willow on Deezer", exact: true })).toBeChecked();
    await expect(dialog.locator(".artist-option").filter({ has: page.getByRole("radio", { name: "Willow on Deezer", exact: true }) })).toContainText("Current choice");
    await screenshot(page, testInfo, "artist-choice-confirmed", width);
    await dialog.getByRole("radio", { name: /^None of these/ }).check();
    await dialog.getByRole("button", { name: "Save choice", exact: true }).click();
    await expect(dialog).toBeHidden();
    await expect(reopened.getByText("No service artist selected.", { exact: true })).toBeVisible();
    expect(state.choices).toEqual([
      { libraryArtistId: "native-willow", providerId: "deezer", artistId: "willow-right" },
      { libraryArtistId: "native-willow", providerId: null, artistId: null },
    ]);
    await reopened.getByRole("button", { name: "Done", exact: true }).click();
    await expect(page.getByRole("article", { name: "Soft edges by Willow", exact: true })).toHaveCount(0);
    await expect(page.getByRole("article", { name: "Open skies by North", exact: true })).toBeVisible();

    await page.reload();
    const persisted = await openFollowing(page);
    await persisted.getByRole("button", { name: "Change artist for Willow", exact: true }).click();
    await expect(dialog.getByRole("radio", { name: /^None of these/ })).toBeChecked();
    await expect(dialog.locator(".none-option")).toContainText("Current choice");
    await expect(dialog.getByText("Current choice: None of these", { exact: true })).toBeVisible();
    await screenshot(page, testInfo, "artist-choice-none", width);
  });
}


test("A current release artist outside search results stays selectable", async ({ page }, testInfo) => {
  await appearance(page, 1280, "dark");
  const state = await mockArtistChoices(page);
  state.artist = { ...state.artist, providerId: "qobuz", artistId: "willow-existing" };
  state.currentChoice = { providerId: "qobuz", artistId: "willow-existing", displayName: "Willow Ensemble", serviceName: "Qobuz", imageUrl: "/api/admin/releases/artwork/qobuz/willow-existing", hints: ["A quiet season", "River song"] };
  await page.goto("/#/for-you?section=releases");
  const following = await openFollowing(page);
  await following.getByRole("button", { name: "Choose the right artist for Willow", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Choose the right artist", exact: true });
  const current = dialog.getByRole("radio", { name: "Willow Ensemble on Qobuz", exact: true });
  await expect(current).toBeChecked();
  await expect(dialog.getByRole("radio")).toHaveCount(candidates.length + 2);
  await expect(dialog.getByText("Current choice: Willow Ensemble · Qobuz", { exact: true })).toBeVisible();
  await expect(dialog.locator(".current-option")).toContainText("Current choice");
  await expect(dialog.locator(".current-option img")).toHaveAttribute("src", state.currentChoice.imageUrl!);
  await expect(dialog.getByRole("button", { name: "Save choice", exact: true })).toBeEnabled();
  await screenshot(page, testInfo, "artist-choice-outside-results", 1280);
  await dialog.getByRole("button", { name: "Save choice", exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(state.choices).toEqual([{ libraryArtistId: "native-willow", providerId: "qobuz", artistId: "willow-existing" }]);
  await following.getByRole("button", { name: "Change artist for Willow", exact: true }).click();
  await expect(current).toBeChecked();
  await expect(dialog.getByText("Current choice: Willow Ensemble · Qobuz", { exact: true })).toBeVisible();
});
