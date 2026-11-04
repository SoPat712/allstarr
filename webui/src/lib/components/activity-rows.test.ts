import { describe, expect, it } from "vitest";
import type { PlayTrace } from "$lib/api";
import { groupPlays, plainTaskName, scheduleParts } from "./activity-rows";

const play = (id: string, title: string, listenerId = "listener", provider = "library") => ({
  id, listenerId, startedAt: "2026-11-04T12:00:00Z",
  outcome: "completed", timing: { elapsedMs: 1000, firstByteMs: 400, headersMs: 300, phases: [], omittedPhases: 0 },
  request: { presented: { title, artist: "River" } },
  resolution: { provider, externalSnapshotId: title, cached: false },
} as PlayTrace);

describe("consecutive plays", () => {
  it("groups repeats without folding intervening songs or other listeners", () => {
    const rows = [play("1", "Evening"), play("2", "Evening"), play("3", "Morning"), play("4", "Evening"), play("5", "Evening", "other")];
    expect(groupPlays(rows).map(group => group.map(item => item.id))).toEqual([["1", "2"], ["3"], ["4"], ["5"]]);
  });
  it("groups source changes but keeps local days separate", () => {
    const first = play("1", "Evening");
    expect(groupPlays([first, play("2", "Evening", "listener", "external"), { ...first, id: "3", startedAt: "2026-11-03T12:00:00Z" }])).toHaveLength(2);
  });
});

describe("task presentation", () => {
  it("removes generated sync wrappers while retaining a plain playlist title", () => {
    expect(plainTaskName("Sync Weekly Exploration for treeman183, week of November 2", "playlist")).toBe("Weekly Exploration");
    expect(plainTaskName("Weekly Jams, week of November 2", "playlist")).toBe("Weekly Jams");
    expect(plainTaskName("Health checks", "health")).toBe("Health checks");
  });
  it("reads common schedules without rewriting custom expressions", () => {
    expect(scheduleParts("0 4 * * *", true)).toEqual({ repeat: "daily", time: "04:00", day: "1" });
    expect(scheduleParts("30 9 * * 5", true)).toEqual({ repeat: "weekly", time: "09:30", day: "5" });
    expect(scheduleParts("0 */2 * * *", true).repeat).toBe("custom");
    expect(scheduleParts("0 4 * * *", false).repeat).toBe("manual");
  });
});
