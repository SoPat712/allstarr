import { expect, it, vi } from "vitest";
import { nextFilterIndex, readFilter, segmentShares, writeFilter } from "./helpers";

it("normalizes segment shares and ignores invalid totals", () => {
  const shares = segmentShares([
    { label: "Ready", value: 3, tone: "good" },
    { label: "Waiting", value: 1, tone: "warn" },
    { label: "Invalid", value: Number.NaN, tone: "stop" },
    { label: "Negative", value: -2, tone: "neutral" },
  ]);
  expect(shares.map(segment => segment.percentage)).toEqual([75, 25, 0, 0]);
  expect(segmentShares([{ label: "Empty", value: 0, tone: "neutral" }])[0].percentage).toBe(0);
});

it("restores only filters that still exist", () => {
  const storage = { getItem: vi.fn(() => "old"), setItem: vi.fn() };
  expect(readFilter("view", ["all", "ready"], storage)).toBeUndefined();
  storage.getItem.mockReturnValue("ready");
  expect(readFilter("view", ["all", "ready"], storage)).toBe("ready");
  expect(readFilter(undefined, ["ready"], storage)).toBeUndefined();
  expect(storage.getItem).toHaveBeenCalledTimes(2);
});

it("keeps filtering available when storage is blocked", () => {
  const storage = { getItem: () => { throw new Error("blocked"); }, setItem: () => { throw new Error("blocked"); } };
  expect(readFilter("view", ["all"], storage)).toBeUndefined();
  expect(() => writeFilter("view", "all", storage)).not.toThrow();
  const saved = { getItem: () => null, setItem: vi.fn() };
  writeFilter("view", "ready", saved);
  expect(saved.setItem).toHaveBeenCalledExactlyOnceWith("view", "ready");
});

it("wraps roving focus and supports the first and last options", () => {
  expect(nextFilterIndex("ArrowRight", 2, 3)).toBe(0);
  expect(nextFilterIndex("ArrowLeft", 0, 3)).toBe(2);
  expect(nextFilterIndex("ArrowDown", 0, 3)).toBe(1);
  expect(nextFilterIndex("Home", 2, 3)).toBe(0);
  expect(nextFilterIndex("End", 0, 3)).toBe(2);
  expect(nextFilterIndex("ArrowRight", -1, 3)).toBe(0);
  expect(nextFilterIndex("ArrowLeft", -1, 3)).toBe(2);
  expect(nextFilterIndex("Enter", 0, 3)).toBeUndefined();
  expect(nextFilterIndex("ArrowRight", 0, 0)).toBeUndefined();
});
