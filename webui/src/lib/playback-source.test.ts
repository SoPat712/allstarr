import { describe, expect, it } from "vitest";
import { formatPlaybackSource } from "./sources";

describe("playback source wording", () => {
  it("describes local plays as coming from the listener's library", () => {
    for (const id of ["library", "local", "jellyfin", "subsonic", "jellyfin-local", "subsonic-local", "JELLYFIN"])
      expect(formatPlaybackSource([], id)).toEqual({ label: "Your library", text: "from your library" });
    expect(formatPlaybackSource([{ id: "jellyfin", name: "Jellyfin" }], "jellyfin").text)
      .toBe("from your library");
  });

  it("uses a supplied library name and catalog service names", () => {
    expect(formatPlaybackSource([{ id: "local", name: "Family music" }], "local"))
      .toEqual({ label: "Family music", text: "from Family music" });
    expect(formatPlaybackSource([{ id: "jellyfin-local", name: "Family music" }], "jellyfin-local").text)
      .toBe("from Family music");
    expect(formatPlaybackSource([], "jellyfin", { libraryName: "Record room" }).text)
      .toBe("from Record room");
    expect(formatPlaybackSource([{ id: "custom-source", name: "My music" }], "custom-source"))
      .toEqual({ label: "My music", text: "from My music" });
    expect(formatPlaybackSource([{ id: "apple-musickit", name: "Apple Music" }], "applemusic").text)
      .toBe("from Apple Music");
  });

  it("omits the source clause when the source is not known", () => {
    for (const id of [null, undefined, "", "unrecognized-source"])
      expect(formatPlaybackSource([], id)).toEqual({ label: "", text: "" });
    for (const name of ["", "  ", "Unknown source"])
      expect(formatPlaybackSource([{ id: "source", name }], "source").text).toBe("");
    expect(formatPlaybackSource([], "unrecognized-source", { confirmed: false }).text).toBe("");
  });

  it("keeps unconfirmed catalogs distinct from observed playback", () => {
    expect(formatPlaybackSource([], "spotify", { confirmed: false }))
      .toEqual({ label: "Spotify", text: "catalog: Spotify (playback unconfirmed)" });
    expect(formatPlaybackSource([], "library", { confirmed: false }).text)
      .toBe("catalog: your library (playback unconfirmed)");
    expect(formatPlaybackSource([], "spotify", { cached: true }))
      .toEqual({ label: "Saved copy", text: "from a saved copy" });
    expect(formatPlaybackSource([], "spotify", { cached: true, confirmed: false }).text)
      .toBe("catalog: Spotify (playback unconfirmed)");
  });
});
