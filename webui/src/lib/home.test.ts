import { describe, expect, it } from "vitest";
import { playbackSourceIssues, summarizeHome, type HomeSnapshot } from "./home";

it("surfaces blocked playback sources once per provider", () => {
  expect(playbackSourceIssues([{
    id: "apple-download",
    name: "Apple Music – GAMDL",
    runtimeCapabilities: [
      { id: "streaming", ready: false, canAttempt: false, reasonCode: "login_required" },
      { id: "download", ready: false, canAttempt: false, reasonCode: "login_required" },
    ],
  }])).toEqual([{
    providerId: "apple-download",
    providerName: "Apple Music – GAMDL",
    reason: "login_required",
  }]);
});

describe("summarizeHome", () => {
  it("projects the aggregate dashboard counts", () => {
    const snapshot: HomeSnapshot = {
      failures: [],
      stats: {
        activeJobs: 1, linkedPlaylists: 2, playableTracks: 6, unresolvedTracks: 2,
        completedListens: 12, scrobbleDeliveries: 2,
      },
    };

    expect(summarizeHome(snapshot)).toEqual({
      activeJobs: 1,
      managed: 2,
      playable: 6,
      unresolved: 2,
    });
  });
});
