import { describe, expect, it } from "vitest";
import {
  activityLink,
  filterActivity,
  groupActivity,
  groupOutcome,
  humanize,
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
    const grouped = groupActivity([item("1", "Song A"), item("2", "Song A"), item("3", "Song B")]);
    expect(grouped).toMatchObject([{ title: "Matched 2 tracks", entries: [{ id: "1" }, { id: "3" }] }]);
    expect(groupActivity([item("0", "New Song"), item("1", "Song A")])[0].key)
      .toBe(grouped[0].key);
    expect(groupOutcome([item("1", "Song A"), { ...item("2", "Song B"), state: "suggested" }]))
      .toBe("mixed");
  });

  it("filters provider-neutral event fields and humanizes enums", () => {
    expect(filterActivity([item("1", "Song A")], {
      query: "future",
      kind: "",
      outcome: "accepted",
      provider: "",
      severity: "",
    })).toHaveLength(1);
    expect(humanize("provider_health.failed")).toBe("Provider Health Failed");
    expect(activityLink({ ...item("1", "Song A"), sourceTitle: "Song A" }))
      .toBe("#/library/mappings?search=Song%20A");
  });
});
