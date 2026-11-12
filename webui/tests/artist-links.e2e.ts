import { expect, test, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { expectContainedMobileGeometry } from "./mobile-geometry";

type Service = { providerId: string; providerName: string; artistId: string; name: string; state: string; scope: string | null; sharedSongs: number | null; sharedSongTitles?: string[]; checkedAt?: string; reason?: string };
type Artist = { id: string; name: string; songCount: number; imageUrl: string | null; services: Service[] };

const checkedAt = "2026-11-12T02:12:00Z";
const service = (providerId: string, providerName: string, name: string, state: string, extra: Partial<Service> = {}): Service =>
  ({ providerId, providerName, artistId: `${providerId}-${name.toLowerCase().replaceAll(" ", "-")}`, name, state, scope: null, sharedSongs: state === "candidate" ? 0 : null, ...extra });

// Shaped like the server's artist review: song links, saved answers and same-named candidates per library artist.
function artists(): Artist[] {
  return [
    { id: "lib-addison", name: "Addison Rae", songCount: 47, imageUrl: null, services: [
      service("applemusickit", "Apple Music", "Addison Rae", "songs", { sharedSongs: 7, sharedSongTitles: ["Diet Pepsi", "Aquamarine", "Headphones On"] }),
      service("deezer", "Deezer", "Addison Rae", "songs", { sharedSongs: 5, sharedSongTitles: ["Diet Pepsi"] }),
      service("spotify", "Spotify", "Addison Rae", "candidate", { reason: "same name, no shared songs" }),
    ] },
    { id: "lib-doja", name: "Doja Cat", songCount: 212, imageUrl: null, services: [
      service("applemusickit", "Apple Music", "Doja Cat", "songs", { sharedSongs: 40 }), service("deezer", "Deezer", "Doja Cat", "songs", { sharedSongs: 38 }),
      service("qobuz", "Qobuz", "Doja Cat", "songs", { sharedSongs: 12 }),
    ] },
    { id: "lib-charli", name: "Charli xcx", songCount: 88, imageUrl: null, services: [
      service("applemusickit", "Apple Music", "Charli xcx", "songs", { sharedSongs: 21 }),
      service("deezer", "Deezer", "Charli XCX", "same", { scope: "household" }),
    ] },
    { id: "lib-mother", name: "Mother Mother", songCount: 31, imageUrl: null, services: [
      service("applemusickit", "Apple Music", "Mother Mother", "songs", { sharedSongs: 9 }),
      service("soundcloud", "SoundCloud", "Mother Mother", "not-same", { scope: "you" }),
    ] },
    { id: "lib-saynow", name: "Say Now", songCount: 12, imageUrl: null, services: [] },
  ];
}

const statusFor = (services: Service[]) => {
  const states = new Set(services.map(item => item.state));
  return states.has("candidate") ? "needs-look" : states.has("same") ? "decided" : states.has("not-same") ? "apart" : states.has("songs") ? "linked" : "library-only";
};

async function fixture(page: Page, administrator = true) {
  const library = artists();
  const writes: { method: string; path: string; body: unknown }[] = [];
  const shape = (artist: Artist, detail: boolean) => ({ ...artist, status: statusFor(artist.services),
    services: artist.services.map(item => detail ? { ...item, checkedAt } : { ...item, sharedSongTitles: undefined, reason: undefined }) });
  await page.route("**/api/**", async route => {
    const url = new URL(route.request().url()), method = route.request().method();
    let body: unknown = {};
    if (url.pathname.endsWith("/auth/me")) body = { authenticated: true, backend: "Jellyfin", user: { id: "josh", name: "Josh", isAdministrator: administrator } };
    if (url.pathname.endsWith("/onboarding/status")) body = { completed: true, shouldRedirectToSetup: false, setupOpen: false, completedSteps: [], recoveryNotices: [], migration: {} };
    if (url.pathname.endsWith("/ui/schema")) body = { activeBackend: "Jellyfin", providers: [
      { id: "applemusickit", name: "Apple Music", categories: ["streaming"] }, { id: "deezer", name: "Deezer", categories: ["streaming"] },
      { id: "spotify", name: "Spotify", categories: ["metadata"] }, { id: "qobuz", name: "Qobuz", categories: ["streaming"] }, { id: "soundcloud", name: "SoundCloud", categories: ["streaming"] },
    ] };
    if (url.pathname === "/api/admin/track-matches") body = { matches: [], stats: { total: 0, matched: 0, accepted: 0, automatic: 0, manualMatches: 0, manualRejections: 0, suggested: 0, ambiguous: 0, review: 0, unresolved: 0, rejected: 0, attention: 0 }, pagination: { page: 1, pageSize: 50, total: 0, totalPages: 1 } };
    if (url.pathname === "/api/admin/playlist-links") body = { playlistLinks: [] };
    if (url.pathname.startsWith("/api/admin/artist-links")) {
      if (!administrator) return route.fulfill({ status: 403, json: {} });
      const parts = url.pathname.split("/").slice(4).map(decodeURIComponent);
      if (url.pathname === "/api/admin/artist-links/review") { writes.push({ method, path: "review", body: null }); body = { queued: true, running: true }; }
      else if (!parts.length) {
        const filter = url.searchParams.get("filter") ?? "needs-look", q = (url.searchParams.get("q") ?? "").toLowerCase();
        const rows = library.filter(artist => artist.name.toLowerCase().includes(q)).map(artist => shape(artist, false));
        const count = (status: string) => rows.filter(row => row.status === status).length;
        body = { counts: { needsLook: count("needs-look"), linked: count("linked"), decided: count("decided"), apart: count("apart"), all: rows.length },
          artists: rows.filter(row => filter === "all" || row.status === filter).toSorted((a, b) => b.songCount - a.songCount), nextCursor: null };
      } else {
        const artist = library.find(item => item.id === parts[0]);
        if (!artist) return route.fulfill({ status: 404, json: {} });
        if (parts.length === 3) {
          const target = artist.services.find(item => item.providerId === parts[1] && item.artistId === parts[2])!;
          const request = method === "PUT" ? route.request().postDataJSON() : { scope: url.searchParams.get("scope") };
          writes.push({ method, path: parts.join("/"), body: request });
          if (method === "PUT") Object.assign(target, { state: request.same ? "same" : "not-same", scope: request.scope });
          else Object.assign(target, { state: (target.sharedSongs ?? 0) > 0 ? "songs" : "candidate", scope: null });
        }
        body = shape(artist, true);
      }
    }
    if (url.pathname.endsWith("/updates/stream")) return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    await route.fulfill({ json: body });
  });
  return writes;
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`Artists in Matches list, explain and answer at ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    const writes = await fixture(page);
    await page.goto("#/library/matches");
    await page.getByRole("tab", { name: "Artists", exact: true }).click();
    await expect(page).toHaveURL(/#\/library\/matches\?view=artists$/);
    const status = page.getByRole("region", { name: "Artist links", exact: true });
    await expect(status).toContainText("5 library artists");
    await expect(status).toContainText("1 needs a look");
    await expect(page.getByRole("radio", { name: "Needs a look 1", exact: true })).toHaveAttribute("aria-checked", "true");
    const rows = page.locator(".artist-list .item-row");
    await expect(rows).toHaveCount(1);
    await expect(rows.first()).toContainText("47 songs · Spotify: same name, no shared songs yet");
    if (width <= 760) await expectContainedMobileGeometry(page);
    await mkdir("test-results/artists", { recursive: true });
    await page.screenshot({ path: `test-results/artists/list-${width}-${theme}.png` });

    await page.getByRole("button", { name: "Open Addison Rae", exact: true }).click();
    const panel = page.getByRole("dialog", { name: "Addison Rae", exact: true });
    await expect(panel).toContainText("47 songs in your library");
    await expect(panel.getByRole("region", { name: "Not linked yet" })).toContainText("Same name, no shared songs. Apps show it as a separate entry for now.");
    await expect(panel.getByRole("region", { name: "Linked by shared songs" })).toContainText("7 songs match yours: Diet Pepsi, Aquamarine, Headphones On");
    await expect(panel).toContainText('One "Addison Rae" with library, Apple Music and Deezer songs; Spotify shows as a separate artist');
    await page.screenshot({ path: `test-results/artists/panel-${width}-${theme}.png` });

    await panel.getByRole("button", { name: "Same artist", exact: true }).click();
    await expect(page.getByText("Spotify's Addison Rae now joins your Addison Rae.")).toBeVisible();
    expect(writes.at(-1)).toEqual({ method: "PUT", path: "lib-addison/spotify/spotify-addison-rae", body: { same: true, scope: "household" } });
    const decisions = panel.getByRole("region", { name: "Your decisions" });
    await expect(decisions).toContainText("You said same artist, for the household");
    await expect(page.getByRole("radio", { name: "Needs a look 0", exact: true })).toBeVisible();
    await decisions.getByRole("button", { name: "Undo", exact: true }).click();
    expect(writes.at(-1)).toEqual({ method: "DELETE", path: "lib-addison/spotify/spotify-addison-rae", body: { scope: "household" } });
    await expect(panel.getByRole("region", { name: "Not linked yet" })).toBeVisible();
  });
}

test("Not the same can be saved just for you, and a decided artist explains itself", async ({ page }) => {
  const writes = await fixture(page);
  await page.goto("#/library/matches?view=artists");
  await page.getByRole("button", { name: "Open Addison Rae", exact: true }).click();
  const panel = page.getByRole("dialog", { name: "Addison Rae", exact: true });
  await panel.getByRole("button", { name: /^Same artist: Household; choose scope$/ }).click();
  await page.getByRole("menuitemradio", { name: "Just me", exact: true }).click();
  await panel.getByRole("region", { name: "Not linked yet" }).getByRole("button", { name: "Not the same", exact: true }).click();
  expect(writes.at(-1)).toEqual({ method: "PUT", path: "lib-addison/spotify/spotify-addison-rae", body: { same: false, scope: "you" } });
  await expect(panel.getByRole("region", { name: "Your decisions" })).toContainText("You said not the same, just for you");
  await page.keyboard.press("Escape");

  await page.getByRole("radio", { name: "By you 1", exact: true }).click();
  await expect(page.locator(".artist-list .item-row")).toContainText(["88 songs · Deezer marked as the same artist for the household"]);
  await page.getByRole("radio", { name: /^All 5$/ }).click();
  await page.getByRole("searchbox", { name: "Search artists" }).fill("say");
  await expect(page.locator(".artist-list .item-row")).toHaveCount(1);
  await page.getByRole("button", { name: "Open Say Now", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Say Now", exact: true })).toContainText("No service has an artist by this name yet.");
  await page.keyboard.press("Escape");

  await page.getByRole("button", { name: "Check all artists again", exact: true }).click();
  await expect(page.getByText("Checking every library artist on your services. This runs in the background.")).toBeVisible();
  expect(writes.at(-1)).toEqual({ method: "POST", path: "review", body: null });
});

test("An artist link opens from its address", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await fixture(page);
  await page.goto("#/library/matches?view=artists&artist=lib-charli");
  const panel = page.getByRole("dialog", { name: "Charli xcx", exact: true });
  await expect(panel.getByRole("region", { name: "Your decisions" })).toContainText("Charli XCX · Deezer");
  await expect(panel.getByRole("region", { name: "Not linked yet" })).toHaveCount(0);
  await mkdir("test-results/artists", { recursive: true });
  await page.screenshot({ path: "test-results/artists/decided.png" });
});

test("Listeners keep the song matches without the Artists switch", async ({ page }) => {
  await fixture(page, false);
  await page.goto("#/library/matches?view=artists");
  await expect(page.getByRole("heading", { name: "Matches", exact: true })).toBeVisible();
  await expect(page.getByRole("tab", { name: "Artists", exact: true })).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Artist links", exact: true })).toHaveCount(0);
});
