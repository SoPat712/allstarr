import { describe, expect, it } from "vitest";
import { combineHistoryPreviews, groupHistoryImports, historyBytes, historyDateRange, historyPlays, historySource, importBatchProgress, importProgress, importState, importSummary } from "./history-import";
import type { HistoryImport } from "./api";

describe("history presentation", () => {
  it("formats upload sizes at byte boundaries", () => {
    expect(historyBytes(0)).toBe("0 bytes");
    expect(historyBytes(1)).toBe("1 byte");
    expect(historyBytes(1023)).toBe("1,023 bytes");
    expect(historyBytes(1024)).toBe("1 KB");
    expect(historyBytes(1536)).toBe("1.5 KB");
    expect(historyBytes(500 * 1024 ** 2)).toBe("500 MB");
  });
  it("uses words and singular counts for plays", () => {
    expect(historyPlays(0)).toBe("0 plays");
    expect(historyPlays(1)).toBe("1 play");
    expect(historyPlays(48210)).toBe("48,210 plays");
  });
  it("keeps day ranges and year summaries stable across time zones", () => {
    expect(historyDateRange(null, null)).toBe("Date range unavailable");
    expect(historyDateRange("invalid", "2026-10-28T00:00:00Z")).toBe("Date range unavailable");
    expect(historyDateRange("2016-01-01T00:00:00Z", "2026-10-28T23:00:00Z")).toBe("Jan 1, 2016 – Oct 28, 2026");
    expect(historyDateRange("2026-10-28T00:00:00Z", "2026-10-28T23:00:00Z")).toBe("Oct 28, 2026");
    expect(historyDateRange("2016-01-01T00:00:00Z", "2026-10-28T00:00:00Z", true)).toBe("2016–2026");
    expect(historyDateRange("2026-01-01T00:00:00Z", "2026-10-28T00:00:00Z", true)).toBe("2026");
  });
  it("presents numeric response states and source names in words", () => {
    expect(historySource("koito-export-v1")).toBe("Koito");
    expect(historySource("unknown-format")).toBe("Other service");
    const item = { state: 2, importedRows: 420, duplicateRows: 0, preview: { musicRows: 1000 } } as HistoryImport;
    expect(importProgress(item)).toBe(42);
    expect(importState(item)).toBe("Importing 42%");
    expect(importState({ ...item, state: 3 })).toBe("Done");
    expect(importState({ ...item, state: 5, lastErrorMessage: "history_import_failed" })).toBe("Failed: The file could not be imported.");
  });
  it("does not claim filtered rows were completed plays", () => {
    const item = { state: 3, importedRows: 50000, preview: { format: "spotify-extended-streaming-history", musicRows: 50000, completed: 48210, skipped: 1790, earliest: "2016-01-01T00:00:00Z", latest: "2026-10-28T00:00:00Z" } } as HistoryImport;
    expect(importSummary(item)).toBe("Imported 48,210 plays from Spotify (2016–2026)");
    expect(importSummary({ ...item, importedRows: 49000 })).toBe("Import from Spotify (2016–2026) is done.");
  });
});

describe("history batches", () => {
  const item = (id: string, first: string | null, last: string | null) => ({
    importId: id, sizeBytes: 100, state: 0, importedRows: 0, duplicateRows: 0,
    preview: { completed: 10, skipped: 2, songs: 4, malformed: 1, musicRows: 12,
      unresolvedNewRows: 2, duplicateInFile: 1, duplicateExisting: 3, outsideRetentionRows: 0, earliest: first, latest: last },
  }) as HistoryImport;
  const first = item("first", "2020-01-01T00:00:00Z", "2021-01-01T00:00:00Z");
  const second = item("second", "2016-01-01T00:00:00Z", "2026-01-01T00:00:00Z");

  it("adds preview counts and takes the full date range regardless of file order", () => {
    expect(combineHistoryPreviews([first, second])).toEqual({
      completed: 20, skipped: 4, songs: 8, malformed: 2,
      unresolvedNewRows: 4, duplicateInFile: 2, duplicateExisting: 6, outsideRetentionRows: 0,
      earliest: "2016-01-01T00:00:00Z", latest: "2026-01-01T00:00:00Z",
    });
    expect(combineHistoryPreviews([second, first])).toEqual(combineHistoryPreviews([first, second]));
  });

  it("keeps empty or missing dates out of the combined range", () => {
    expect(combineHistoryPreviews([])).toMatchObject({ completed: 0, songs: 0, earliest: null, latest: null });
    expect(combineHistoryPreviews([item("missing", null, "invalid"), first])).toMatchObject({ earliest: first.preview.earliest, latest: first.preview.latest });
    expect(combineHistoryPreviews([first])).toMatchObject({ completed: 10, songs: 4 });
  });

  it("groups only imports with the same saved batch, preserving list order", () => {
    const single = item("single", null, null);
    expect(groupHistoryImports([first, single, second], { first: "batch", second: "batch" })).toEqual([
      { id: "batch", items: [first, second] }, { id: "single", items: [single] },
    ]);
    expect(groupHistoryImports([first, second], {})).toHaveLength(2);
  });

  it("combines file progress and advances the file number after a completed file", () => {
    expect(importBatchProgress([first, second])).toEqual({ percent: 0, file: 1 });
    expect(importBatchProgress([{ ...first, state: 3 }, { ...second, state: 2, importedRows: 6 }])).toEqual({ percent: 75, file: 2 });
    expect(importBatchProgress([{ ...first, state: 3 }, { ...second, state: 3 }])).toEqual({ percent: 100, file: 2 });
  });
});
