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
    };

    expect(summarizeHome(snapshot)).toEqual({
      activeJobs: 1,
      managed: 2,
      unmanaged: 3,
      playable: 13,
      unresolved: 2,
    });
  });
});
