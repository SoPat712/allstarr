import { describe, expect, it } from "vitest";
import { storageSegments } from "./storage-usage";

describe("Storage segments", () => {
  it("includes the measured database without estimating missing bytes", () => {
    const mib = 1024 ** 2;
    expect(storageSegments(mib, 2 * mib, 8 * mib, 3 * mib).map(item => [item.label, item.value])).toEqual([
      ["Cached songs (MB)", 1], ["Kept songs (MB)", 2], ["Database (MB)", 3], ["Cache room left (MB)", 7],
    ]);
    expect(storageSegments(mib, 0, 8 * mib, null).some(item => item.label.startsWith("Database"))).toBe(false);
    expect(storageSegments(null, 0, 8 * mib, 3 * mib)).toEqual([]);
  });
  it("keeps fractional measured usage and does not invent free space above the limit", () => {
    expect(storageSegments(2 * 1024 ** 2, 0, 1024 ** 2, 512 * 1024).map(item => item.value)).toEqual([2, 0, 0.5, 0]);
  });
});
