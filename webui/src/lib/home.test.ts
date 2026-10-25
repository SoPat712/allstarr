import { describe, expect, it } from "vitest";
import { playbackProgress, playbackSourceIssues, summarizeHome, type HomeSnapshot } from "./home";

describe("reported playback progress", () => {
  const item = { positionSeconds: 30.25, durationSeconds: 120, isPaused: false, positionUpdatedAt: "2026-01-01T00:00:00Z" };
  const clock = { capturedAt: "2026-01-01T00:00:02Z", elapsedSeconds: .5 };

  it("uses the server reference and monotonic elapsed time, retaining fractional seconds", () => {
    expect(playbackProgress(item, clock)).toEqual({ seconds: 32.75, progress: 32.75 / 120, state: "estimated", advancing: true });
    expect(playbackProgress({ ...item, positionSeconds: 10.25 }, clock).seconds).toBe(12.75);
  });

  it("freezes explicit pauses and leaves missing pause or anchors reported-only", () => {
    expect(playbackProgress({ ...item, isPaused: true }, clock)).toEqual({ seconds: 30.25, progress: 30.25 / 120, state: "paused", advancing: false });
    for (const fields of [{ isPaused: null }, { positionUpdatedAt: null }, { positionUpdatedAt: "invalid" }])
      expect(playbackProgress({ ...item, ...fields }, clock)).toEqual({ seconds: 30.25, progress: 30.25 / 120, state: "reported", advancing: false });
    expect(playbackProgress(item, null).seconds).toBe(30.25);
  });

  it("bounds estimates by freshness and known duration without reducing an actual report", () => {
    expect(playbackProgress(item, { ...clock, elapsedSeconds: 100 })).toEqual({ seconds: 45.25, progress: 45.25 / 120, state: "stale", advancing: false });
    expect(playbackProgress({ ...item, positionSeconds: 119 }, clock)).toEqual({ seconds: 120, progress: 1, state: "estimated", advancing: false });
    expect(playbackProgress({ ...item, positionSeconds: 125 }, clock).seconds).toBe(125);
  });

  it("does not invent a position, duration, or a clock for invalid or future references", () => {
    expect(playbackProgress({ ...item, positionSeconds: null, durationSeconds: null }, clock)).toEqual({ seconds: null, progress: null, state: "reported", advancing: false });
    expect(playbackProgress({ ...item, positionSeconds: 0, durationSeconds: 0 }, null)).toEqual({ seconds: 0, progress: null, state: "reported", advancing: false });
    expect(playbackProgress({ ...item, positionSeconds: Number.NaN }, clock).seconds).toBeNull();
    expect(playbackProgress({ ...item, durationSeconds: Number.POSITIVE_INFINITY }, clock).progress).toBeNull();
    for (const reference of [{ ...clock, capturedAt: "invalid" }, { ...clock, capturedAt: "2025-12-31T23:00:00Z" }, { ...clock, elapsedSeconds: Number.NaN }])
      expect(playbackProgress(item, reference).seconds).toBe(30.25);
  });
});

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

it("uses managed check summaries instead of public account requirements", () => {
  const providers = [{ id: "source", name: "Source", runtimeCapabilities: [
    { id: "download", ready: false, canAttempt: false, reasonCode: "provider_account_required" },
  ] }];
  expect(playbackSourceIssues(providers, [{ providerId: "source", enabledAccountCount: 1,
    capabilityTotal: 1, healthyCapabilityCount: 1, failedCapabilityCount: 0 }])).toEqual([]);
});

describe("summarizeHome", () => {
  it("projects the aggregate dashboard counts", () => {
    const snapshot: HomeSnapshot = {
      failures: [],
      stats: {
        activeJobs: 1, linkedPlaylists: 2, playableTracks: 6, unresolvedTracks: 2,
        completedListens: 12, currentWeekListens: 24, previousWeekListens: 20,
        scrobbleDeliveries: 2, cacheTracks: 3, keptTracks: 4,
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
