import { describe, expect, it } from "vitest";
import { isAttention, percent, playableProviders, scoreComponents } from "./mappings";

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

  it("keeps scoring evidence ordered and state semantics stable", () => {
    expect(isAttention("Ambiguous")).toBe(true);
    expect(isAttention("accepted")).toBe(false);
    expect(percent(0.9346)).toBe("93.5%");
    expect(scoreComponents({ components: { title: 0.9, artist: 0.75 } })).toEqual([
      ["title", 0.9],
      ["artist", 0.75],
    ]);
  });
});
