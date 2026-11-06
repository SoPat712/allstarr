import { afterEach, describe, expect, it, vi } from "vitest";
import type { ProviderCheckHistory } from "./api";
import { sourceCheckCard } from "./source-check-card";

const check: ProviderCheckHistory["checks"][number] = {
  capability: "streaming", accountLabel: "Household", accountScope: "shared",
  testedAt: "2026-11-03T12:00:00Z", outcome: "failed", reasonCode: "timeout", latencyMs: 1234.5,
};

afterEach(() => vi.useRealTimers());

describe("source check card content", () => {
  it("maps a failure to readable details and the shared reason wording", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-11-03T12:05:00Z"));
    expect(sourceCheckCard(check)).toEqual({
      capability: "Streaming", account: "Household", outcome: "Failed", tone: "danger",
      relative: "5 minutes ago",
      absolute: new Date(check.testedAt).toLocaleString(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }),
      reason: "Timed out", latency: "1235 ms",
    });
  });

  it("maps passed and neutral checks without exposing raw reason codes", () => {
    expect(sourceCheckCard({ ...check, capability: "lyrics", accountLabel: "", accountScope: "public",
      outcome: "passed", latencyMs: 0 })).toMatchObject({
      capability: "Lyrics", account: "Public", outcome: "Passed", tone: "success", reason: null, latency: "Under 1 ms",
    });
    expect(sourceCheckCard({ ...check, outcome: "neutral", reasonCode: "probe_not_available", latencyMs: null }))
      .toMatchObject({ outcome: "Neutral", tone: "neutral", reason: "Not checked yet", latency: "Latency unavailable" });
    expect(sourceCheckCard({ ...check, reasonCode: "unrecognized-reason" })?.reason).toBe("Check failed");
    expect(sourceCheckCard({ ...check, reasonCode: null })?.reason).toBe("Check failed");
  });

  it("handles absent checks and unavailable measurements", () => {
    expect(sourceCheckCard(null)).toBeNull();
    for (const latencyMs of [null, -1, Number.NaN, Number.POSITIVE_INFINITY]) {
      expect(sourceCheckCard({ ...check, testedAt: "invalid", latencyMs })).toMatchObject({
        relative: "Time unavailable", absolute: "Time unavailable", latency: "Latency unavailable",
      });
    }
  });
});
