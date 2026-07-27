import { describe, expect, it } from "vitest";
import { acceptUpdate } from "./live-updates.svelte";

describe("acceptUpdate", () => {
  it("deduplicates event IDs without comparing unrelated resource revisions", () => {
    const revisions = new Map<string, number>();
    const eventIds = new Set<string>();

    expect(
      acceptUpdate(
        { resource: "job", resourceId: "job-1", revision: 5 },
        "event-1",
        revisions,
        eventIds,
      ),
    ).toBe(true);
    expect(
      acceptUpdate(
        { resource: "track-match", resourceId: "match-1", revision: 1 },
        "event-2",
        revisions,
        eventIds,
      ),
    ).toBe(true);
    expect(
      acceptUpdate(
        { resource: "job", resourceId: "job-1", revision: 4 },
        "event-3",
        revisions,
        eventIds,
      ),
    ).toBe(false);
    expect(
      acceptUpdate(
        { resource: "job", resourceId: "job-1", revision: 5 },
        "event-1",
        revisions,
        eventIds,
      ),
    ).toBe(false);
  });
});
