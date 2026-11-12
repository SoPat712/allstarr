import { describe, expect, it } from "vitest";
import { artistLinks, artistPill, artistSections, artistSummary, inAppsNow, serviceDetail, type ArtistLinkRow, type ArtistLinkService } from "./artist-links";

const service = (providerName: string, state: ArtistLinkService["state"], extra: Partial<ArtistLinkService> = {}): ArtistLinkService =>
  ({ providerId: providerName.toLowerCase(), providerName, artistId: `${providerName}-1`, name: "Addison Rae", state, scope: null, sharedSongs: null, ...extra });
const row = (status: ArtistLinkRow["status"], services: ArtistLinkService[], songCount = 47): ArtistLinkRow =>
  ({ id: "a1", name: "Addison Rae", songCount, imageUrl: null, status, services });

describe("artist link rows", () => {
  it("says which services wait for an answer", () => {
    expect(artistSummary(row("needs-look", [service("Apple Music", "songs"), service("Spotify", "candidate")])))
      .toBe("47 songs · Spotify: same name, no shared songs yet");
    expect(artistSummary(row("needs-look", [service("Spotify", "candidate"), service("Deezer", "candidate")])))
      .toBe("47 songs · Spotify and Deezer: same name, no shared songs yet");
  });
  it("names the decision and who it applies to", () => {
    expect(artistSummary(row("decided", [service("Deezer", "same", { scope: "household" })], 88))).toBe("88 songs · Deezer marked as the same artist for the household");
    expect(artistSummary(row("apart", [service("SoundCloud", "not-same", { scope: "you" })], 1))).toBe("1 song · SoundCloud kept apart just for you");
    expect(artistSummary(row("linked", [service("Qobuz", "songs")]))).toBe("47 songs · linked by shared songs");
    expect(artistSummary(row("library-only", []))).toBe("47 songs · no service artist found");
  });
  it("labels each status", () => {
    expect(artistPill("needs-look")).toEqual({ tone: "warn", text: "Needs a look" });
    expect(artistPill("linked").text).toBe("One entry");
  });
});

describe("artist link panel", () => {
  const addison = row("needs-look", [
    service("Apple Music", "songs", { sharedSongs: 7, sharedSongTitles: ["Diet Pepsi", "Aquamarine"] }),
    service("Deezer", "songs", { sharedSongs: 5 }),
    service("Spotify", "candidate", { sharedSongs: 0, reason: "same name, no shared songs" }),
    service("SoundCloud", "not-same", { scope: "you" }),
  ]);
  it("puts questions first, then automatic links, then decisions", () => {
    expect(artistSections(addison).map(section => [section.label, section.services.map(item => item.providerName)])).toEqual([
      ["Not linked yet", ["Spotify"]], ["Linked by shared songs", ["Apple Music", "Deezer"]], ["Your decisions", ["SoundCloud"]],
    ]);
    expect(artistSections(row("library-only", []))).toEqual([]);
  });
  it("explains each service in a sentence", () => {
    expect(addison.services.map(serviceDetail)).toEqual([
      "7 songs match yours: Diet Pepsi, Aquamarine…",
      "5 songs match yours",
      "Same name, no shared songs. Apps show it as a separate entry for now.",
      "You said not the same, just for you",
    ]);
    expect(serviceDetail(service("Deezer", "songs", { sharedSongs: 1, sharedSongTitles: ["Headphones On"] }))).toBe("1 song matches yours: Headphones On");
    expect(serviceDetail(service("Deezer", "same", { scope: "household" }))).toBe("You said same artist, for the household");
  });
  it("describes what listening apps show now", () => {
    expect(inAppsNow(addison)).toBe('One "Addison Rae" with library, Apple Music and Deezer songs; Spotify and SoundCloud show as separate artists');
    expect(inAppsNow(row("needs-look", [service("Spotify", "candidate")]))).toBe(`"Addison Rae" with your library's songs; Spotify shows as a separate artist`);
  });
});

describe("artist link requests", () => {
  it("encodes every id in the address", async () => {
    const calls: [string, RequestInit | undefined][] = [];
    const original = globalThis.fetch;
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      calls.push([String(input), init]);
      return new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } });
    }) as typeof fetch;
    try {
      const spotify = service("Spotify", "candidate", { artistId: "a/b c" });
      await artistLinks.set("lib 1", spotify, true, "household");
      await artistLinks.undo("lib 1", spotify, "you");
      await artistLinks.list("needs-look", "say now", "next");
    } finally { globalThis.fetch = original; }
    expect(calls[0][0]).toContain("/api/admin/artist-links/lib%201/spotify/a%2Fb%20c");
    expect(calls[0][1]?.method).toBe("PUT");
    expect(JSON.parse(String(calls[0][1]?.body))).toEqual({ same: true, scope: "household" });
    expect(calls[1][0]).toContain("/api/admin/artist-links/lib%201/spotify/a%2Fb%20c?scope=you");
    expect(calls[1][1]?.method).toBe("DELETE");
    expect(calls[2][0]).toContain("/api/admin/artist-links?filter=needs-look&limit=50&q=say+now&cursor=next");
  });
});
