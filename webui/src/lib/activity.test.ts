import { describe, expect, it } from "vitest";
import {
  activityAction,
  activityTone,
  activityDuration,
  activityHeadline,
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

it("keeps different household owners separate even with colliding correlations", () => {
  const first = { ...item("one", "Song A"), actorUserId: "first", actorDisplayName: "Josh" };
  const second = { ...item("two", "Song A"), actorUserId: "second", actorDisplayName: "Alex" };
  expect(groupActivity([first, second])).toHaveLength(2);
  expect(filterActivity([first, second], { query: "Alex", category: "", provider: "", problemsOnly: false })).toEqual([second]);
});

it("keeps job stages together using the current outcome and exact owner", () => {
  const done = { ...item("done", "Song kept"), kind: "job", jobId: "job", actorUserId: "one", state: "succeeded", occurredAt: "2026-10-26T12:00:09Z" };
  const failed = { ...done, id: "failed", state: "failed", detail: "Earlier source failed", occurredAt: "2026-10-26T12:00:00Z" };
  const groups = groupActivity([done, failed, { ...done, id: "other", actorUserId: "two" }]);
  expect(groups).toHaveLength(2); expect(groups[0].entries).toHaveLength(2); expect(groupOutcome(groups[0].entries)).toBe("succeeded");
  expect(filterActivity([done, failed], { query: "", category: "", provider: "", problemsOnly: true })).toEqual([]);
});
it("links known work to its own history and rejects external actions", () => {
  const jobId = "11111111-1111-4111-8111-111111111111";
  expect(activityAction({ ...item("work", "Song kept"), kind: "job", jobId }, () => "Source")).toEqual({ href: `#/activity?view=jobs&job=${jobId}`, label: "Open related work" });
  expect(activityAction({ ...item("work", "Song kept"), kind: "job", actionHref: "javascript:alert(1)", actionLabel: "Unsafe" }, () => "Source")).toBeNull();
});
it("keeps invalid dates readable instead of crashing the feed", () => {
  expect(relativeTime("not-a-date", "Time unavailable")).toBe("Time unavailable");
});

it("uses meaningful outcome tones without marking cancelled or unknown work as failed", () => {
  for (const state of ["passed", "verified", "placed", "succeeded", "ok"]) expect(activityTone(state)).toBe("success");
  for (const state of ["running", "started", "pending", "scheduled"]) expect(activityTone(state)).toBe("accent");
  for (const state of ["retrying", "Deferred", "partial", "PartiallySucceeded", "Conflicted"]) expect(activityTone(state)).toBe("warning");
  for (const state of ["failed", "rejected", "unhealthy"]) expect(activityTone(state)).toBe("danger");
  for (const state of ["cancelled", "skipped", "partly_checked", "unrecognized"]) expect(activityTone(state)).toBe("neutral");
  expect(activityHeadline({ ...item("old", "Unknown"), kind: "job", label: "future.opaque" })).toBe("Background task");
  expect(activityHeadline({ ...item("song", "Song"), label: "Tate McRae song cached" })).toBe("Tate McRae song cached");
  expect(activityDuration(null)).toBe(""); expect(activityDuration(NaN)).toBe(""); expect(activityDuration(-1)).toBe("");
  expect(activityDuration(10)).toBe("less than a second");
});
