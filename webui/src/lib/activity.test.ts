import { describe, expect, it } from "vitest";
import {
  activityAction,
  activityLink,
  dayLabel,
  filterActivity,
  groupActivity,
  groupOutcome,
  humanize,
  mergeActivity,
  relativeTime,
} from "./activity";
import type { ActivityItem } from "./api";

const item = (id: string, detail: string): ActivityItem => ({
  id,
  kind: "matching",
  source: "future-extension",
  label: "Track matched",
  state: "accepted",
  detail,
  occurredAt: "2026-07-27T00:00:00Z",
  correlationId: "job-1",
  action: "track-match.evaluate",
});

describe("event log presentation", () => {
  it("groups consecutive operation events and removes retry noise", () => {
    const grouped = groupActivity([
      { ...item("1", "Song A"), playlistName: "First" },
      { ...item("2", "Song A"), playlistName: "First" },
      { ...item("3", "Song B"), playlistName: "Second" },
    ]);
    expect(grouped).toMatchObject([{
      title: "Matched 2 songs in 2 playlists",
      entries: [{ id: "1" }, { id: "3" }],
    }]);
    expect(groupActivity([item("0", "New Song"), ...[
      { ...item("1", "Song A"), playlistName: "First" },
      { ...item("2", "Song A"), playlistName: "First" },
      { ...item("3", "Song B"), playlistName: "Second" },
    ]])[0].key)
      .toBe(grouped[0].key);
    expect(groupActivity([
      { ...item("1", "Song A"), playlistName: "First" },
      { ...item("2", "Song A"), playlistName: "First" },
      { ...item("3", "Song B"), playlistName: "Second" },
      item("4", "Older Song"),
    ])[0].key).toBe(grouped[0].key);
    expect(groupOutcome([item("1", "Song A"), { ...item("2", "Song B"), state: "suggested" }]))
      .toBe("mixed");
  });

  it("retains loaded history while deduplicating live refreshes", () => {
    const current = Array.from({ length: 250 }, (_, index) =>
      ({ ...item(String(index), `Song ${index}`), occurredAt: `2026-07-26T${String(index % 24).padStart(2, "0")}:00:00Z` }));
    const merged = mergeActivity(current, [
      { ...item("249", "Updated"), occurredAt: "2026-07-27T00:00:00Z" },
    ]);
    expect(merged).toHaveLength(250);
    expect(merged[0]).toMatchObject({ id: "249", detail: "Updated" });
  });

  it("filters provider-neutral event fields and humanizes enums", () => {
    expect(filterActivity([item("1", "Song A")], {
      query: "future",
      category: "",
      provider: "",
      problemsOnly: false,
    })).toHaveLength(1);
    expect(filterActivity([item("1", "Song A")], {
      query: "",
      category: "",
      provider: "future-extension",
      problemsOnly: false,
    })).toHaveLength(0);
    expect(filterActivity([item("1", "Song A")], {
      query: "",
      category: "",
      provider: "",
      problemsOnly: true,
    })).toHaveLength(0);
    expect(filterActivity([{ ...item("2", "Song B"), state: "failed" }], {
      query: "",
      category: "matching",
      provider: "",
      problemsOnly: true,
    })).toHaveLength(1);
    expect(dayLabel("2026-10-12T10:00:00", new Date("2026-10-12T20:00:00"))).toBe("Today");
    expect(dayLabel("2026-10-11T10:00:00", new Date("2026-10-12T20:00:00"))).toBe("Yesterday");
    expect(activityAction({ ...item("5", "Song A"), sourceTitle: "Song A" }, () => "Lumen")?.label)
      .toBe("Review match");
    expect(humanize("provider_health.failed")).toBe("Provider Health Failed");
    expect(humanize("reviewrequired")).toBe("Review Required");
    expect(relativeTime(null, "Never")).toBe("Never");
    expect(activityLink({ ...item("1", "Song A"), sourceTitle: "Song A" }))
      .toBe("#/library/mappings?search=Song%20A");
    expect(activityLink({ ...item("2", "Cached"), kind: "caching" }))
      .toBe("#/library/cached");
    expect(activityLink({
      ...item("3", "Provider unhealthy"),
      kind: "provider_health",
      providerId: "lumen-audio",
    })).toBe("#/integrations/services/lumen-audio");
    expect(activityLink({
      ...item("4", "Local library"),
      kind: "library",
      providerId: "library",
    })).toBeNull();
    expect(groupActivity([{ ...item("3", "Failed"), label: "scrobbling check" }])[0].title)
      .toBe("Scrobbling Check");
  });
});

describe("background work presentation", () => {
  it("uses ordinary job names and distinguishes deferred work", async () => {
    const { jobName, jobState, progressPercent } = await import("./activity");
    expect(jobName("playlist.materialize")).toBe("Update playlist");
    expect(jobState("RetryScheduled")).toBe("Retrying");
    expect(jobState("Deferred")).toBe("Deferred");
    expect(progressPercent(5, 10)).toBe(50);
    expect(progressPercent(20, 10)).toBe(100);
    expect(progressPercent(-1, 10)).toBe(0);
    expect(progressPercent(5, 0)).toBeNull();
    expect(progressPercent(NaN, 10)).toBeNull();
  });
});
