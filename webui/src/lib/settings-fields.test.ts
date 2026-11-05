import { describe, expect, it } from "vitest";
import { joinLibraryIds, splitLibraryIds } from "./settings-fields";

describe("music library selection", () => {
  it("uses an empty selection for all libraries", () => {
    expect(splitLibraryIds("")).toEqual([]);
    expect(splitLibraryIds(" , ")).toEqual([]);
    expect(joinLibraryIds([])).toBe("");
  });

  it("round trips trimmed, distinct IDs without changing their order", () => {
    expect(splitLibraryIds(" music, 2, music,,other ")).toEqual(["music", "2", "other"]);
    expect(joinLibraryIds(["music", " 2 ", "music", "", "other"])).toBe("music,2,other");
    expect(splitLibraryIds(joinLibraryIds(["music", "2"]))).toEqual(["music", "2"]);
  });
});
