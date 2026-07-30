import { describe, expect, it } from "vitest";
import {
  candidateResolution,
  currentTarget,
  differenceHash,
  hashSimilarity,
  isAttention,
  percent,
  playableProviderIds,
  providerResultCounts,
  rankedTargets,
  reviewStateLabel,
  scoreComponents,
} from "./mappings";

describe("mapping review presentation", () => {
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
    expect(reviewStateLabel("suggested")).toBe("Tentative");
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
      { id: "4", title: "Four" },
    ])).toEqual([
      { providerId: "local", count: 1 },
      { providerId: "apple-download", count: 2 },
      { providerId: "deezer", count: 1 },
    ]);
    expect(providerResultCounts([])).toEqual([{ providerId: "local", count: 0 }]);
  });

  it("orders unified results by match confidence", () => {
    expect(rankedTargets([
      { id: "1", title: "Weak", confidence: 0.45 },
      { id: "2", title: "Best", externalProvider: "deezer", confidence: 0.98 },
      {
        id: "4",
        title: "Preferred local",
        confidence: 0.93,
        components: { localPreference: 0.07, preferenceScore: 1 },
      },
      { id: "3", title: "Unknown" },
    ]).map((target) => target.title)).toEqual([
      "Preferred local",
      "Best",
      "Weak",
      "Unknown",
    ]);
  });

  it("only accepts a concrete local or non-source provider candidate", () => {
    const playable = new Set(["deezer"]);
    expect(candidateResolution({ libraryTrackId: "local-1" }, "spotify", playable)).toEqual({
      targetType: "local",
      libraryTrackId: "local-1",
    });
    expect(candidateResolution({
      isLocal: false,
      libraryTrackId: "synthetic-external-id",
      providerTrackIds: { spotify: "source", deezer: "candidate" },
    }, "spotify", playable)).toEqual({
      targetType: "provider",
      externalProvider: "deezer",
      externalId: "candidate",
    });
    expect(candidateResolution({
      isLocal: false,
      libraryTrackId: "metadata-only",
      providerTrackIds: { musicbrainzalbum: "release" },
    }, "spotify", playable)).toBeNull();
    expect(candidateResolution(
      { providerTrackIds: { spotify: "source" } },
      "spotify",
      playable,
    )).toBeNull();
  });

  it("derives selectable providers only from playback capabilities", () => {
    expect([...playableProviderIds([
      { id: "musicbrainz", name: "MusicBrainz", categories: ["metadata"] },
      { id: "jellyfin", name: "Jellyfin", categories: ["streaming"] },
      {
        id: "extension",
        name: "Extension",
        capabilityRoutes: [{ capabilities: ["metadata", "download"] }],
      },
    ])]).toEqual(["jellyfin", "extension"]);
  });

  it("projects external target metadata instead of reducing the match to a provider id", () => {
    const target = currentTarget({
      externalSnapshotId: "snapshot",
      providerId: "spotify",
      libraryScopeId: "music",
      state: "accepted",
      decisionSource: "track_match_decision",
      providerIdentities: [
        { providerId: "spotify", externalId: "source", scope: "catalog", verification: "verified" },
        { providerId: "qobuz", externalId: "target", scope: "catalog", verification: "verified" },
      ],
      candidates: [{
        title: "Target title",
        artist: "Target artist",
        album: "Target album",
        durationMilliseconds: 180_000,
        providerTrackIds: { qobuz: "target" },
      }],
      reasons: [],
      warnings: [],
    });

    expect(target).toMatchObject({
      providerId: "qobuz",
      identity: "target",
      title: "Target title",
      artist: "Target artist",
      album: "Target album",
    });
  });
});
