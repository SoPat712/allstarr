import { describe, expect, it } from "vitest";
import {
  differenceHash,
  hashSimilarity,
  isAttention,
  percent,
  playableProviders,
  providerResultCounts,
  scoreComponents,
} from "./mappings";

describe("mapping review presentation", () => {
  it("discovers arbitrary installed playback providers", () => {
    expect(
      playableProviders([
        { id: "future-extension", name: "Future", categories: ["metadata", "streaming"] },
        { id: "playlist-only", name: "Playlist", categories: ["playlist"] },
        {
          id: "offline",
          name: "Offline",
          categories: ["download"],
          runtimeCapabilities: [{ id: "download", ready: false, canAttempt: false }],
        },
      ]).map((provider) => provider.id),
    ).toEqual(["future-extension"]);
  });

  it("compares simple artwork fingerprints", () => {
    const pixels = new Uint8ClampedArray(9 * 8 * 4);
    for (let row = 0; row < 8; row += 1)
      for (let column = 0; column < 9; column += 1)
        pixels[(row * 9 + column) * 4] = 255 - column;
    const hash = differenceHash(pixels);
    expect(hashSimilarity(hash, hash)).toBe(1);
    expect(hashSimilarity(0n, (1n << 64n) - 1n)).toBe(0);
  });

  it("keeps scoring evidence ordered and state semantics stable", () => {
    expect(isAttention("Ambiguous")).toBe(true);
    expect(isAttention("accepted")).toBe(false);
    expect(percent(0.9346)).toBe("93.5%");
    expect(scoreComponents({ components: { title: 0.9, artist: 0.75 } })).toEqual([
      ["title", 0.9],
      ["artist", 0.75],
    ]);
  });

  it("summarizes every provider returned by an unfiltered search", () => {
    expect(providerResultCounts([
      { id: "1", title: "One", externalProvider: "apple-download" },
      { id: "2", title: "Two", externalProvider: "deezer" },
      { id: "3", title: "Three", externalProvider: "apple-download" },
    ])).toEqual([
      { providerId: "apple-download", count: 2 },
      { providerId: "deezer", count: 1 },
    ]);
  });
});
