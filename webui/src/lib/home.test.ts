import { describe, expect, it } from "vitest";
import { homeSourceHealth, listenTrend, nextScheduledWork, playbackMeta, playbackProgress, playbackSourceIssues, recentHomeActivity, summarizeHome, type HomeSnapshot } from "./home";
import type { ActivityItem } from "./api";

describe("Home recent activity", () => {
  const check = (id: string, detail = "4 passed, 3 not checked", actorUserId = "one"): ActivityItem => ({
    id, kind: "job", label: "Health check", action: "health.smoke", state: "succeeded", detail,
    actorUserId, occurredAt: `2026-11-06T12:0${id}:00Z`,
  } as ActivityItem);

  it("keeps the latest identical health result and makes room for other work", () => {
    const saved = { ...check("1"), id: "saved", kind: "caching", label: "Saved a song", action: "cache.save" };
    expect(recentHomeActivity([check("2"), saved, check("4"), check("3")]).map(item => item.id)).toEqual(["4", "saved"]);
  });

  it("preserves different health results, listeners and unrelated work", () => {
    expect(recentHomeActivity([check("4"), check("3", "1 failed"), check("2", undefined, "two"),
      { ...check("1"), label: "Update playlist", action: "playlist.sync" }])).toHaveLength(4);
  });

  it("filters automatic rematches while keeping their progress summary and failures", () => {
    const rematch = { ...check("1"), label: "Track matched", action: "automatic.review", state: "accepted" };
    const progress = { ...check("4"), id: "progress", label: "Checking matches", action: "checking-matches", state: "running",
      detail: "20 of 100 · Now plays from your library: 12 songs", isRoutine: false };
    expect(recentHomeActivity([rematch, { ...rematch, id: "waiting", label: "Refresh matches", action: "track-match.rematch", state: "retry_scheduled" },
      progress, { ...rematch, id: "failed", state: "failed", severity: "error" }]).map(item => item.id)).toEqual(["progress", "failed"]);
  });

  it("leaves waiting work to Activity and shows a repeated result once", () => {
    const scrobble = (id: string) => ({ ...check(id), label: "Scrobble recorded", action: "scrobble.deliver", detail: "Sent to Last.fm" });
    const waiting = { ...check("4"), id: "waiting", label: "Refresh library index", action: "library.index", state: "retry_scheduled", isRoutine: false };
    expect(recentHomeActivity([scrobble("1"), scrobble("2"), scrobble("3"), waiting]).map(item => item.id)).toEqual(["3"]);
  });

  it("filters routine work and keeps a failed background task", () => {
    const task = { ...check("1"), label: "Background task", action: "", state: "succeeded" };
    expect(recentHomeActivity([task, { ...task, id: "failed", state: "failed", severity: "error" }]).map(item => item.id)).toEqual(["failed"]);
  });
});

describe("Home wording", () => {
  it("shows the signed change from last week", () => {
    expect(listenTrend(36, 0)).toBe("+36 on last week");
    expect(listenTrend(10, 14)).toBe("−4 on last week");
    expect(listenTrend(10, 10)).toBe("same as last week");
  });

  it("keeps the playback route honest when its source is unconfirmed", () => {
    const item = { userName: "Listener", client: "Aloud", device: "iPhone", providerId: "applemusic", sourceConfirmed: true };
    const providers = [{ id: "apple-musickit", name: "Apple Music" }];
    expect(playbackMeta(item, providers)).toBe("Listener on Songbird, iPhone · from Apple Music");
    expect(playbackMeta({ ...item, cached: true }, providers)).toBe("Listener on Songbird, iPhone · from Apple Music");
    expect(playbackMeta({ ...item, sourceConfirmed: false }, providers)).toBe("Listener on Songbird, iPhone · catalog: Apple Music (playback unconfirmed)");
    expect(playbackMeta({ ...item, device: null }, providers)).toBe("Listener on Songbird · from Apple Music");
    expect(playbackMeta({ ...item, sourceConfirmed: false, catalogProviderId: "deezer" }, providers))
      .toBe("Listener on Songbird, iPhone · catalog: Apple Music (playback unconfirmed)");
    expect(playbackMeta({ ...item, sourceConfirmed: false, providerId: "unrecognized-source", catalogProviderId: "applemusic" }, providers))
      .toBe("Listener on Songbird, iPhone");
  });

  it("names local playback and omits unavailable sources without a trailing separator", () => {
    const item = { userName: "Listener", client: "Music app", providerId: "library", sourceConfirmed: true };
    expect(playbackMeta(item, [])).toBe("Listener on Music app · from your library");
    for (const providerId of ["jellyfin-local", "subsonic-local"])
      expect(playbackMeta({ ...item, providerId }, [])).toBe("Listener on Music app · from your library");
    expect(playbackMeta({ ...item, providerId: "jellyfin" }, [{ id: "jellyfin", name: "Family music" }]))
      .toBe("Listener on Music app · from Family music");
    expect(playbackMeta({ ...item, providerId: "unrecognized-source" }, [])).toBe("Listener on Music app");
    expect(playbackMeta({ ...item, providerId: "unrecognized-source", sourceConfirmed: false }, []))
      .toBe("Listener on Music app");
    expect(playbackMeta({ ...item, cached: true }, [])).toBe("Listener on Music app · from your library");
  });

  it("groups the next scheduled playlist syncs and ignores paused tasks", () => {
    const task = { id: "one", name: "Refresh mixes", kind: "upkeep", state: "Scheduled", enabled: true, cadence: "Daily", nextRunAt: "2026-11-07T04:00:00Z" };
    expect(nextScheduledWork([{ ...task, enabled: false }])).toBeNull();
    expect(nextScheduledWork([task])?.name).toBe("Refresh mixes");
    expect(nextScheduledWork([{ ...task, runPlaylistLinkId: "one" }, { ...task, id: "two", runPlaylistLinkId: "two" }])?.name).toBe("2 playlist syncs");
    expect(nextScheduledWork([{ ...task, nextRunAt: "invalid" }])).toBeNull();
  });

  it("uses explicit check failures for down and slow source labels", () => {
    const source = { providerId: "one", capabilityTotal: 1, enabledAccountCount: 0, healthyCapabilityCount: 0, failedCapabilityCount: 1 };
    const health = homeSourceHealth({ failures: [], providers: [source, { ...source, providerId: "slow", lastFailureCode: "stream_slow" }, { ...source, providerId: "working", failedCapabilityCount: 0, p95LatencyMilliseconds: 10000 }, { ...source, providerId: "unchecked", capabilityTotal: 0 }] });
    expect(health.down.map(item => item.providerId)).toEqual(["one"]);
    expect(health.slow.map(item => item.providerId)).toEqual(["slow"]);
    expect(health.checked).toHaveLength(3);
  });
});

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
