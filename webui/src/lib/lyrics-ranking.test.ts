import { describe, expect, it } from "vitest";
import type { RecordingLyricsSource } from "./api";
import { defaultLyricsSourceId, rankedLyricsSources } from "./lyrics-ranking";

const source = (sourceId: string, values: Partial<RecordingLyricsSource> = {}): RecordingLyricsSource => ({
  sourceId, available: true, format: "plain", preview: "Words", previewRevision: "preview",
  truncated: false, ...values,
});

describe("lyrics source ranking", () => {
  it("preserves legacy source order without changing the input", () => {
    const sources = [source("first"), source("second"), source("third")];
    const ordered = rankedLyricsSources(sources);
    expect(ordered.map(value => value.sourceId)).toEqual(["first", "second", "third"]);
    expect(ordered).not.toBe(sources);
    expect(sources.map(value => value.sourceId)).toEqual(["first", "second", "third"]);
    expect(defaultLyricsSourceId(sources)).toBe("first");
  });

  it("orders ranked sources before null and missing ranks", () => {
    const sources = [source("missing"), source("third", { rank: 3 }), source("null", { rank: null }),
      source("best", { rank: 1 }), source("second", { rank: 2 })];
    expect(rankedLyricsSources(sources).map(value => value.sourceId))
      .toEqual(["best", "second", "third", "missing", "null"]);
    expect(defaultLyricsSourceId(sources)).toBe("best");
  });

  it("keeps equal ranks in their original order", () => {
    const sources = [source("first", { rank: 2 }), source("best", { rank: 1 }), source("second", { rank: 2 })];
    expect(rankedLyricsSources(sources).map(value => value.sourceId)).toEqual(["best", "first", "second"]);
  });

  it("prefers an available saved source over the highest rank", () => {
    const sources = [source("best", { rank: 1 }), source("saved", { rank: 3 })];
    expect(defaultLyricsSourceId(sources, "saved")).toBe("saved");
  });

  it("skips unavailable choices and ranked sources when selecting a preview", () => {
    const sources = [source("legacy", { rank: null }), source("offline", { rank: 1, available: false }),
      source("ready", { rank: 2 }), source("saved", { rank: 3, available: false })];
    expect(rankedLyricsSources(sources).map(value => value.sourceId)).toEqual(["offline", "ready", "saved", "legacy"]);
    expect(defaultLyricsSourceId(sources, "saved")).toBe("ready");
    expect(defaultLyricsSourceId(sources, null)).toBe("ready");
    expect(defaultLyricsSourceId(sources, "missing")).toBe("ready");
  });

  it("uses the first available legacy source and handles an empty selection", () => {
    const sources = [source("offline", { rank: null, available: false }), source("ready")];
    expect(defaultLyricsSourceId(sources)).toBe("ready");
    expect(defaultLyricsSourceId([sources[0]], "offline")).toBe("");
    expect(defaultLyricsSourceId([])).toBe("");
  });
});
