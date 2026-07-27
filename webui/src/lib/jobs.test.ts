import { describe, expect, it } from "vitest";
import { compactProgress, progressDetails } from "./jobs";
import type { JobProgress } from "./api";

const progress = (id: string, detailsJson: string): JobProgress => ({
  id,
  jobId: "job",
  action: "playlist.match",
  outcome: "running",
  detailsJson,
  createdAt: "2026-07-27T00:00:00Z",
});

describe("job progress", () => {
  it("parses safe details and collapses repeated progress noise", () => {
    const item = progress("1", '{"message":"Matching","completed":2,"total":5}');
    expect(progressDetails(item)).toMatchObject({ completed: 2, total: 5 });
    expect(progressDetails(progress("bad", "not-json"))).toEqual({});
    expect(compactProgress([item, progress("2", item.detailsJson)])).toHaveLength(1);
  });
});
