import { describe, expect, it } from "vitest";
import type { ProviderScore } from "./api";
import { capabilityScore, isRanked, scoreTier, sortByScore } from "./provider-score";

const score = (overall: number | null, lyrics?: number): ProviderScore => ({
  providerId: "a", score: overall, uptimePercent: 100, checks: 10, days: [],
  capabilities: lyrics == null ? [] : [{ capability: "lyrics", score: lyrics, uptimePercent: 100, checks: 10, failures: 0, typicalLatencyMilliseconds: null, lastFailureCode: null }],
});

describe("provider scores", () => {
  it("uses the overall score except for lyrics", () => {
    expect(capabilityScore(score(60, 95), "metadata")).toBe(60);
    expect(capabilityScore(score(60, 95), "lyrics")).toBe(95);
    expect(capabilityScore(undefined, "metadata")).toBeNull();
  });

  it("ranks only sources Allstarr can pick by itself", () => {
    expect(isRanked("automatic")).toBe(true);
    expect(isRanked(undefined)).toBe(true);
    expect(isRanked("manualOnly")).toBe(false);
    expect(isRanked("off")).toBe(false);
  });

  it("sorts manual sources below everything, however high their score", () => {
    const values: Record<string, number | null> = { manual: 99, low: 20, high: 80, unscored: null };
    expect(sortByScore(["manual", "low", "unscored", "high"], (id) => values[id], (id) => id !== "manual"))
      .toEqual(["high", "low", "unscored", "manual"]);
  });

  it("keeps the current order for equal scores", () => {
    expect(sortByScore(["b", "a"], () => 50, () => true)).toEqual(["b", "a"]);
  });

  it("names score tiers", () => {
    expect([95, 75, 60, 20, null].map(scoreTier)).toEqual(["great", "good", "fair", "poor", "none"]);
  });
});
