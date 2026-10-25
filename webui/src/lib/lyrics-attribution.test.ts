import { describe, expect, it } from "vitest";
import { lyricsAttribution } from "./lyrics-attribution";

describe("lyrics credits", () => {
  const community = {
    service: "spicylyrics", catalog: "spicy_lyrics",
    uploader: { username: "Uploader", url: "https://spicylyrics.org/uid/123" },
    maker: { username: "Maker", url: "https://spicylyrics.org/uid/456" },
  };

  it("binds credits to the source that supplied this result", () => {
    expect(lyricsAttribution("spicylyrics", community)).toMatchObject({ catalog: "Spicy Lyrics", contributors: [
      { role: "Uploaded by", name: "Uploader", url: community.uploader.url },
      { role: "Synced by", name: "Maker", url: community.maker.url },
    ] });
    expect(lyricsAttribution("lrclib", community)).toBeNull();
    expect(lyricsAttribution("spicylyrics", { ...community, service: "lrclib" })).toBeNull();
  });

  it.each(["javascript:alert(1)", "https://spicylyrics.org.evil.test/uid/123", "https://user@spicylyrics.org/uid/123",
    "http://spicylyrics.org/uid/123", "https://spicylyrics.org:444/uid/123", "https://spicylyrics.org/uid/123?token=x",
    "https://spicylyrics.org/uid/123#x", "https://spicylyrics.org/other", "https://spicylyrics.org/uid/a/../123"])(
    "keeps a contributor's name without an unsafe profile link: %s", url => {
      expect(lyricsAttribution("spicylyrics", { ...community, maker: null, uploader: { username: "Uploader", url } })?.contributors)
        .toEqual([{ role: "Uploaded by", name: "Uploader", url: null }]);
    });

  it("reports the answering catalogue without inventing community contributors", () => {
    expect(lyricsAttribution("spicylyrics", { ...community, catalog: "apple_music" }))
      .toMatchObject({ catalog: "Apple Music", contributors: [] });
    expect(lyricsAttribution("spicylyrics", { ...community, catalog: "spotify" })?.catalog).toBe("Spotify");
    expect(lyricsAttribution("spicylyrics", { service: "spicylyrics", catalog: "unexpected" }))
      .toMatchObject({ catalog: "Unknown source", contributors: [] });
  });

  it("handles absent, malformed and control-filled credits safely", () => {
    for (const value of [undefined, null, [], true, "spicylyrics", {}]) expect(lyricsAttribution("spicylyrics", value)).toBeNull();
    expect(lyricsAttribution("spicylyrics", { ...community, uploader: { username: "\u0000bad" }, maker: { username: "x".repeat(201) } })?.contributors).toEqual([]);
    expect(lyricsAttribution("spicylyrics", { ...community, uploader: { username: "x".repeat(200) }, maker: null })?.contributors)
      .toEqual([{ role: "Uploaded by", name: "x".repeat(200), url: null }]);
    expect(lyricsAttribution("spicylyrics", { ...community, uploader: { username: " <script>inert</script> " }, maker: null })?.contributors)
      .toEqual([{ role: "Uploaded by", name: "<script>inert</script>", url: null }]);
  });
});
