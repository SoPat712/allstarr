import { describe, expect, it } from "vitest";
import { historyBytes, historyDateRange, historyPlays, historySource, importProgress, importState, importSummary } from "./history-import";
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
