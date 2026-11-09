import { describe, expect, it } from "vitest";
import { compactProgress, historyMatching, progressDetails } from "./jobs";
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
    expect(compactProgress(Array.from({ length: 250 }, (_, index) =>
      progress(String(index), `{"message":"Track ${index}"}`)))).toHaveLength(200);
  });
});

describe("historyMatching", () => {
  it("turns the matcher's counts and newest songs into plain outcomes", () => {
    const message = JSON.stringify({
      reasons: { "matched-stored-metadata": 792, "matched-search": 81, "edition-mismatch": 1, "candidates-rejected:weak_artist_evidence_review": 1, "provider-failure:TransientFailure": 2 },
      recent: [
        { title: "Halo", artist: "Beyoncé", plays: 3, found: "HALO", foundArtist: "Beyoncé", reason: "matched-search" },
        { title: "Halo (Live)", artist: "Beyoncé", plays: 1, found: "Halo - Live", foundArtist: "Beyoncé", reason: "matched-stored-metadata" },
        { title: "Interlude", artist: "Someone", plays: 1, found: null, foundArtist: null, reason: "no-candidates" },
        { title: "", artist: "Nobody", reason: "matched-search" },
      ],
    });
    const result = historyMatching({ stage: "history-resolution", message });
    expect(result).toMatchObject({ found: 873, notFound: 2, retry: 2 });
    expect(result?.recent).toEqual([
      { title: "Halo", artist: "Beyoncé", plays: 3, found: "", state: "resolved", label: "Found" },
      { title: "Halo (Live)", artist: "Beyoncé", plays: 1, found: "Halo - Live · Beyoncé", state: "resolved", label: "Found" },
      { title: "Interlude", artist: "Someone", plays: 1, found: "", state: "notfound", label: "Not found" },
    ]);
  });

  it("reads older count-only reports and ignores other jobs", () => {
    expect(historyMatching({ stage: "history-resolution", message: '{"matched-search":4,"no-candidates":1}' }))
      .toEqual({ found: 4, notFound: 1, retry: 0, recent: [] });
    expect(historyMatching({ stage: "playlist-sync", message: '{"matched-search":4}' })).toBeNull();
    expect(historyMatching({ stage: "history-resolution", message: "Working." })).toBeNull();
  });
});
