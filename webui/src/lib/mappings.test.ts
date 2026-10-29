import { describe, expect, it } from "vitest";
import type { MatchReviewItem } from "./api";
import {
  candidateResolution,
  currentTarget,
  differenceHash,
  hashSimilarity,
  isAttention,
  isReviewableMatch,
  matchScore,
  percent,
  playableProviderIds,
  providerResultCounts,
  rankedTargets,
  reportedAudio,
  reviewStateLabel,
  routingPreference,
  scoreComponents,
  targetRoutingPreference,
} from "./mappings";

describe("mapping review presentation", () => {
  it("shows only labeled source songs with usable review identities", () => {
    const match: MatchReviewItem = {
      externalSnapshotId: "source-snapshot", providerId: "spotify", title: "Source song", artist: "Source performer",
      state: "unresolved", decisionSource: "automatic", providerIdentities: [],
      candidates: [], reasons: [], warnings: [],
    };
    expect(isReviewableMatch(match)).toBe(true);
    for (const title of [undefined, null, "", " \t\n "])
      expect(isReviewableMatch({ ...match, title })).toBe(false);
    for (const artist of [undefined, null, "", " "])
      expect(isReviewableMatch({ ...match, artist })).toBe(false);
    for (const providerId of ["", " ", "allstarr", " AllStarr "])
      expect(isReviewableMatch({ ...match, providerId })).toBe(false);
    expect(isReviewableMatch({ ...match, externalSnapshotId: " " })).toBe(false);
    expect(isReviewableMatch({ ...match, state: "accepted", decisionSource: "manual",
      target: { providerId: "qobuz", externalId: "saved-target", title: null },
    })).toBe(true);
    expect(isReviewableMatch({ ...match, state: "accepted", decisionSource: "manual",
      localTrack: { id: "saved-local", backendItemId: "saved-local", title: "Saved local song" },
    })).toBe(true);
  });

  it("ignores internal recording identities when finding a saved playback target", () => {
    const match: MatchReviewItem = {
      externalSnapshotId: "source-snapshot", providerId: "spotify", title: "Source song", artist: "Source performer",
      state: "unresolved", decisionSource: "automatic", providerIdentities: [
        { providerId: "allstarr", externalId: "recording", scope: "catalog", verification: "verified" },
      ], candidates: [], reasons: [], warnings: [],
    };
    expect(currentTarget(match)).toBeNull();
    expect(currentTarget({ ...match, providerIdentities: [
      ...match.providerIdentities,
      { providerId: "qobuz", externalId: "saved-target", scope: "catalog", verification: "verified" },
    ] })).toMatchObject({ providerId: "qobuz", identity: "saved-target", title: "Metadata unavailable" });
  });

  it("separates routing priority from confidence evidence", () => {
    const components = { title: 1, priorityWindow: 0.07, routingPriority: 0, acceptanceQualified: 1 };
    expect(scoreComponents({ components })).toEqual([["title", 1]]);
    expect(routingPreference(components)).toBe("Local first");
    expect(routingPreference({ ...components, routingPriority: 2 })).toBe("Streaming priority 2");
    expect(routingPreference({ ...components, acceptanceQualified: 0 })).toBe("7% tentative window");
    expect(routingPreference(undefined)).toBe("");
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
    expect(reviewStateLabel("suggested")).toBe("Unsure");
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

  it("orders manual-search results by raw match confidence", () => {
    expect(rankedTargets([
      { id: "1", title: "Weak", confidence: 0.45 },
      { id: "2", title: "Best", externalProvider: "deezer", confidence: 0.98 },
      {
        id: "4",
        title: "Preferred local",
        confidence: 0.93,
        components: { priorityWindow: 0.07 },
      },
      { id: "3", title: "Unknown" },
    ]).map((target) => target.title)).toEqual([
      "Best",
      "Preferred local",
      "Weak",
      "Unknown",
    ]);
  });

  it("breaks real Power Trip score ties by streaming order and full album edition", () => {
    const components = { title: 1, artist: .875, album: 1, duration: .95, acceptanceQualified: 1 };
    const targets = [
      { id: "deezer", title: "Power Trip", externalProvider: "deezer", confidence: .9545,
        routingPriority: 2, matchesSourceAlbum: true, components },
      { id: "apple-standard", title: "Power Trip (feat. Miguel)", externalProvider: "apple-download", confidence: .9545,
        routingPriority: 1, matchesSourceAlbum: false, components },
      { id: "apple-deluxe", title: "Power Trip (feat. Miguel)", externalProvider: "apple-download", confidence: .9545,
        routingPriority: 1, matchesSourceAlbum: true, components },
    ];
    expect(rankedTargets(targets).map(target => target.id)).toEqual(["apple-deluxe", "apple-standard", "deezer"]);
    expect(targets.map(target => target.id)).toEqual(["deezer", "apple-standard", "apple-deluxe"]);
    expect(matchScore(.9545)).toBe("95.45%");
    expect(matchScore(.9544)).toBe("95.44%");
    expect(matchScore(1)).toBe("100%");
    expect(matchScore(null)).toBe("—");
    expect(matchScore(Number.NaN)).toBe("—");
  });

  it("uses the shared automatic order even when the preferred version has a lower score", () => {
    expect(rankedTargets([
      { id: "stronger", title: "Track", confidence: .99, automaticOrder: 1 },
      { id: "preferred", title: "Track", confidence: .93, automaticOrder: 0 },
      { id: "variant", title: "Track (Live)", confidence: .98, automaticOrder: 2 },
    ]).map(target => target.id)).toEqual(["preferred", "stronger", "variant"]);
  });

  it("keeps stronger recording evidence above preferred sources and reported resolution", () => {
    expect(rankedTargets([
      { id: "preferred", title: "Preferred", confidence: .9545, routingPriority: 1,
        media: { codec: "alac", bitDepth: 24, sampleRate: 192000 } },
      { id: "stronger", title: "Stronger", confidence: .9546, routingPriority: 2,
        media: { codec: "mp3", bitrate: 320000 } },
      { id: "unknown", title: "Unknown", confidence: Number.NaN, routingPriority: 0 },
    ]).map(target => target.id)).toEqual(["stronger", "preferred", "unknown"]);
  });

  it("uses local preference, legacy rank evidence and stable item identities for exact ties", () => {
    expect(rankedTargets([
      { id: "b", title: "Alpha", externalProvider: "deezer", confidence: .95, components: { routingPriority: 2 } },
      { id: "a", title: "Zulu", externalProvider: "deezer", confidence: .95, routingPriority: 2 },
      { id: "local", title: "Local", confidence: .95, routingPriority: 0 },
      { id: "invalid", title: "Invalid rank", confidence: .95, routingPriority: -1 },
      { id: "missing", title: "Missing score" },
    ]).map(target => target.id)).toEqual(["local", "a", "b", "invalid", "missing"]);
    expect(targetRoutingPreference({ id: "1", title: "Track", routingPriority: 1 })).toBe("Streaming priority 1");
    expect(targetRoutingPreference({ id: "1", title: "Track", routingPriority: 0 })).toBe("Local first");
    expect(targetRoutingPreference({ id: "1", title: "Track", routingPriority: 2147483647 })).toBe("");
  });

  it("shows only reported positive audio facts without inventing ranges or lossy bit depth", () => {
    expect(reportedAudio({ codec: "flac", container: "flac", bitDepth: 24, sampleRate: 96000,
      bitrate: 1792000, channels: 2 })).toBe("FLAC · 24-bit · 96 kHz · 1792 kbps · 2 channels");
    expect(reportedAudio({ codec: "aac", container: "mp4", bitDepth: 24, sampleRate: 44100,
      bitrate: 256000, channels: 1 })).toBe("AAC · MP4 · 44.1 kHz · 256 kbps · 1 channel");
    expect(reportedAudio({ codec: "unknown", container: "unknown", bitrate: 0, sampleRate: -1,
      bitDepth: Number.NaN, channels: 0 })).toBe("Audio format not reported");
    expect(reportedAudio(null)).toBe("Audio format not reported");
    expect(reportedAudio({ bitrate: 1 })).toBe("<1 kbps");
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
      libraryTrackId: "legacy-synthetic-id",
      providerTrackIds: { spotify: "source", deezer: "legacy-candidate" },
    }, "spotify", playable)).toEqual({
      targetType: "provider",
      externalProvider: "deezer",
      externalId: "legacy-candidate",
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

  it("recognizes legacy Apple playback identities without rewriting them or matching the same source", () => {
    const playable = playableProviderIds([
      { id: "apple-musickit", name: "Apple Music", categories: ["streaming"] },
    ]);
    const candidate = {
      isLocal: false,
      libraryTrackId: "legacy-synthetic-id",
      providerTrackIds: { "apple-download": "apple-track" },
    };
    expect(candidateResolution(candidate, "spotify", playable)).toEqual({
      targetType: "provider",
      externalProvider: "apple-download",
      externalId: "apple-track",
    });
    expect(candidateResolution(candidate, "apple-musickit", playable)).toBeNull();
    expect(candidateResolution(candidate, "applemusic", playable)).toBeNull();
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

  it("uses persisted provider target metadata when candidates are empty", () => {
    expect(currentTarget({
      externalSnapshotId: "snapshot",
      providerId: "spotify",
      state: "accepted",
      decisionSource: "manual",
      target: {
        providerId: "qobuz",
        externalId: "saved-target",
        title: "Saved title",
        artist: "Saved artist",
        album: "Saved album",
        durationMilliseconds: 210_000,
        artworkUrl: "/artwork/saved-target",
        isrc: "US1234567890",
      },
      localTrack: { id: "previous-local", backendItemId: "previous-local", title: "Previous title" },
      providerIdentities: [
        { providerId: "deezer", externalId: "other-target", scope: "catalog", verification: "verified" },
      ],
      candidates: [],
      reasons: [],
      warnings: [],
    })).toEqual({
      providerId: "qobuz",
      identity: "saved-target",
      title: "Saved title",
      artist: "Saved artist",
      album: "Saved album",
      durationMilliseconds: 210_000,
      artworkUrl: "/artwork/saved-target",
    });
  });

  it("keeps provider targets visible when saved metadata is unavailable", () => {
    expect(currentTarget({
      externalSnapshotId: "snapshot",
      providerId: "spotify",
      state: "accepted",
      decisionSource: "manual",
      target: {
        providerId: "qobuz",
        externalId: "saved-target",
        title: null,
        artist: null,
        album: null,
        durationMilliseconds: null,
        artworkUrl: null,
        isrc: null,
      },
      providerIdentities: [],
      candidates: [],
      reasons: [],
      warnings: [],
    })).toEqual({
      providerId: "qobuz",
      identity: "saved-target",
      title: "Metadata unavailable",
      artist: "Unknown artist",
      album: null,
      durationMilliseconds: null,
      artworkUrl: null,
    });
  });

  it("falls back to the provider identity when saved targets and candidates are absent", () => {
    expect(currentTarget({
      externalSnapshotId: "snapshot",
      providerId: "spotify",
      state: "accepted",
      decisionSource: "manual",
      target: null,
      providerIdentities: [
        { providerId: "qobuz", externalId: "saved-target", scope: "catalog", verification: "verified" },
      ],
      candidates: [],
      reasons: [],
      warnings: [],
    })).toMatchObject({
      providerId: "qobuz",
      identity: "saved-target",
      title: "Metadata unavailable",
      artist: "Unknown artist",
    });
  });
});
