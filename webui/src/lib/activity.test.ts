import { describe, expect, it } from "vitest";
import {
  activityLink,
  filterActivity,
  groupActivity,
  groupOutcome,
  humanize,
  mergeActivity,
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
      title: "Matched 2 tracks across 2 playlists",
      entries: [{ id: "1" }, { id: "3" }],
    }]);
    expect(groupActivity([item("0", "New Song"), item("1", "Song A")])[0].key)
      .toBe(grouped[0].key);
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
      kind: "",
      outcome: "accepted",
      provider: "",
      severity: "",
    })).toHaveLength(1);
    expect(filterActivity([item("1", "Song A")], {
      query: "",
      kind: "",
      outcome: "",
      provider: "future-extension",
      severity: "",
    })).toHaveLength(0);
    expect(humanize("provider_health.failed")).toBe("Provider Health Failed");
    expect(activityLink({ ...item("1", "Song A"), sourceTitle: "Song A" }))
      .toBe("#/library/mappings?search=Song%20A");
    expect(activityLink({ ...item("2", "Cached"), kind: "caching" }))
      .toBe("#/library/cached");
    expect(groupActivity([{ ...item("3", "Failed"), label: "scrobbling check" }])[0].title)
      .toBe("Scrobbling Check");
  });
});
