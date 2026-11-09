import { existsSync, readdirSync } from "node:fs";
import { afterEach, describe, expect, it, vi } from "vitest";
import { providerMarkSources } from "./provider-marks";

afterEach(() => vi.unstubAllGlobals());

describe("provider mark sources", () => {
  it("uses color masters in place of supplied legacy files", () => {
    expect(providerMarkSources("qobuz", { id: "qobuz", name: "Qobuz", logoUrl: "/images/providers/qobuz.webp" })).toEqual([
      "/images/providers/brands/qobuz/color.svg", "/images/providers/qobuz.webp", "/images/providers/qobuz.svg",
    ]);
    expect(providerMarkSources("spicylyrics", { id: "spicylyrics", name: "SpicyLyrics", logoUrl: "/images/providers/spicylyrics.ico" })).toEqual([
      "/images/providers/brands/spicylyrics/color.svg", "/images/providers/spicylyrics.ico",
    ]);
  });

  it("resolves service variants and hyphenated marks", () => {
    for (const [id, brand] of [["spotifyweb", "spotify"], ["spotiflac-tidal", "tidal"],
      ["applemusicdownload", "applemusic"], ["apple-musickit", "applemusic"], ["apple-download", "applemusic"],
      ["spotiflac-apple-music", "applemusic"], ["ytmusic", "youtubemusic"], ["youtube", "youtubemusic"],
      ["spotiflac-ytmusic-spotiflac", "youtubemusic"], ["spotiflac-tidal-web", "tidal"],
      ["amazon", "amazonmusic"], ["spotiflac-amazon", "amazonmusic"], ["spotiflac-qobuz-web", "qobuz"],
      ["jellyfin", "jellyfin"], ["navidrome", "navidrome"], ["subsonic", "subsonic"], ["opensubsonic", "subsonic"],
      ["local", "jellyfin"], ["native", "jellyfin"], ["musicbrainz", "musicbrainz"], ["musicbrainz-enrichment", "musicbrainz"]]) {
      expect(providerMarkSources(id)).toEqual([
        `/images/providers/brands/${brand}/color.svg`,
        `/images/providers/${brand}.svg`,
      ]);
    }
    expect(providerMarkSources("multi-scrobbler")).toEqual([
      "/images/providers/brands/multiscrobbler/color.svg", "/images/providers/multi-scrobbler.svg",
    ]);
    expect(providerMarkSources("audiomuse-ai")[0]).toBe("/images/providers/brands/audiomuse/color.png");
    for (const brand of ["melon", "genie", "bugs"]) {
      expect(providerMarkSources(`spotiflac-${brand}-music`)).toEqual([
        `/images/providers/brands/${brand}/color.png`,
        `/images/providers/${brand}.svg`,
      ]);
    }
  });

  it("prefers service branding before extension artwork", () => {
    expect(providerMarkSources("spotiflac-spotify-web", { id: "spotiflac-spotify-web", name: "Web playback", logoUrl: "/extensions/fixture/icon.png" })).toEqual([
      "/images/providers/brands/spotify/color.svg", "/extensions/fixture/icon.png", "/images/providers/spotify.svg",
    ]);
    expect(providerMarkSources("spotiflac-custom-service")).toEqual([
      "/images/providers/brands/custom-service/color.svg", "/images/providers/brands/custom-service/color.png", "/images/providers/customservice.svg",
    ]);
  });

  it("uses built-in identity with generic icons and account display labels", () => {
    expect(providerMarkSources("jellyfin", { id: "jellyfin", name: "Jellyfin", icon: "server" }, "Home library")).toEqual([
      "/images/providers/brands/jellyfin/color.svg", "/images/providers/jellyfin.svg",
    ]);
    expect(providerMarkSources("artistgrid", { id: "artistgrid", name: "ArtistGrid", icon: "extension" })).toEqual([
      "/images/providers/brands/artistgrid/color.svg", "/images/providers/artistgrid.svg",
    ]);
  });

  it("uses the available master when a color URL is supplied", () => {
    expect(providerMarkSources("spicylyrics", { id: "spicylyrics", name: "SpicyLyrics", logoUrl: "/images/providers/brands/spicylyrics/color.svg" })).toEqual([
      "/images/providers/brands/spicylyrics/color.svg", "/images/providers/spicylyrics.ico",
    ]);
  });

  it("uses the PNG master for Last.fm headers without an SVG master", () => {
    for (const logoUrl of [undefined, "/images/providers/lastfm.svg", "/images/providers/brands/lastfm/color.svg"]) {
      expect(providerMarkSources("lastfm", { id: "lastfm", name: "Last.fm", categories: ["scrobbling"], logoUrl })).toEqual([
        "/images/providers/brands/lastfm/color.png", "/images/providers/lastfm.svg",
      ]);
    }
  });

  it("preserves covers, external artwork and custom marks", () => {
    for (const logoUrl of ["/images/providers/spotify-cover.png", "/images/providers/spotify-cover-96.png",
      "https://example.test/spotify.svg", "/extensions/spotify.svg", "/images/providers/custom.svg"]) {
      expect(providerMarkSources("spotify", { id: "spotify", name: "Spotify", logoUrl })).toEqual([logoUrl]);
    }
    expect(providerMarkSources("custom", { id: "custom", name: "Custom", icon: "extension" })).toEqual(["/images/providers/custom.svg"]);
  });

  it("resolves every bundled brand to its available color master", () => {
    const root = new URL("../../../allstarr/wwwroot/", import.meta.url);
    const brands = readdirSync(new URL("./images/providers/brands", root), { withFileTypes: true })
      .filter(entry => entry.isDirectory()).map(entry => entry.name);
    expect(brands).toHaveLength(37);
    for (const brand of brands) {
      const id = ["melon", "genie", "bugs"].includes(brand) ? `spotiflac-${brand}-music`
        : brand === "jiosaavn" ? "spotiflac-jiosaavn" : brand;
      const sources = providerMarkSources(id);
      const format = existsSync(new URL(`./images/providers/brands/${brand}/color.svg`, root)) ? "svg" : "png";
      expect(sources[0]).toBe(`/images/providers/brands/${brand}/color.${format}`);
      const legacy = brand === "multiscrobbler" ? "multi-scrobbler.svg" : brand === "spicylyrics" ? "spicylyrics.ico" : `${brand}.svg`;
      if (existsSync(new URL(`./images/providers/${legacy}`, root))) expect(sources).toContain(`/images/providers/${legacy}`);
    }
    for (const [id, brand] of [["neteaselyrics", "netease"], ["qqlyrics", "qqmusic"], ["kugoulyrics", "kugou"]])
      expect(providerMarkSources(id, { id, name: id, icon: "lyrics" })[0]).toMatch(new RegExp(`^/images/providers/brands/${brand}/color\\.(svg|png)$`));
    expect(providerMarkSources("lastfm", { id: "lastfm", name: "Last.fm", logoUrl: "/images/providers/brands/lastfm/color.png" })).toEqual([
      "/images/providers/brands/lastfm/color.png", "/images/providers/lastfm.svg",
    ]);
  });

  it("keeps local lyrics sources as monograms", () => {
    expect(providerMarkSources("backend", undefined, "Media server lyrics")).toEqual([]);
    expect(providerMarkSources("kept", undefined, "Kept LRC")).toEqual([]);
  });

  it("prefixes color and fallback paths for mounted installations", () => {
    vi.stubGlobal("document", { querySelector: () => ({ getAttribute: () => "/music" }) });
    expect(providerMarkSources("spotify")).toEqual([
      "/music/images/providers/brands/spotify/color.svg", "/music/images/providers/spotify.svg",
    ]);
  });
});
