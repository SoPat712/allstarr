import { describe, expect, it } from "vitest";
import { summarizeHome, type HomeSnapshot } from "./home";

describe("summarizeHome", () => {
  it("keeps playlist counts and routes separate", () => {
    const snapshot: HomeSnapshot = {
      failures: [],
      jobs: [
        { id: "1", type: "match", state: "Running", updatedAt: "" },
        { id: "2", type: "sync", state: "Succeeded", updatedAt: "" },
      ],
      playlists: {
        inventory: { managed: 2, unmanaged: 3 },
        playlists: [
          {
            id: "a",
            name: "A",
            trackCount: 10,
            localTracks: 6,
            externalTracks: 3,
            unmatchedTracks: 1,
          },
          {
            id: "b",
            name: "B",
            trackCount: 5,
            localTracks: 2,
            externalTracks: 2,
            unmatchedTracks: 1,
          },
        ],
      },
      playlistLinks: [{
        id: "canonical",
        enabled: true,
        name: "Canonical",
        sourceProviderId: "source",
        targetProtocol: "jellyfin",
        materializationMode: "reconcile",
        revision: 1,
        trackCount: 8,
        matchedCount: 6,
        unmatchedCount: 2,
        playableCount: 6,
        materializedCount: 6,
        routeCoverage: [{ providerId: "jellyfin", count: 6 }, { providerId: "unresolved", count: 2 }],
        metrics: { total: 8, matched: 6, unresolved: 2, review: 0, rejected: 2, playable: 6, materialized: 6 },
      }],
    };

    expect(summarizeHome(snapshot)).toEqual({
      activeJobs: 1,
      managed: 1,
      unmanaged: 3,
      playable: 6,
      unresolved: 2,
    });
  });
});
