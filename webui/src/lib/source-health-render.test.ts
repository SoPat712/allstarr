import { describe, expect, it } from "vitest";
import { render } from "svelte/server";
import SourceHealthHistory from "./components/SourceHealthHistory.svelte";
import ProviderScoreDetails from "./components/ProviderScoreDetails.svelte";
import type { ProviderHealth, ProviderScore } from "./api";

const pause: ProviderHealth = {
  provider: "fixture", providerAccountId: null, providerAccountName: null, capability: "streaming",
  accountScope: "public", supported: true, enabled: true, configuration: "configured", health: "degraded",
  ready: false, canAttempt: false, canTest: true, failureCount: 3, automaticPauseReasonCode: "timeout",
  automaticPausedAt: "2030-04-05T12:00:00Z", automaticRetryAt: "2030-04-05T12:01:00Z",
};
const view = (statuses: ProviderHealth[], automatic = true) => render(SourceHealthHistory, { props: {
  providerId: "fixture", statuses, automatic, refreshKey: "", onRetryWindow: async () => {},
} }).body;

describe("source health state", () => {
  it("explains a low-quality score ceiling separately from successful real plays", () => {
    const score: ProviderScore = {
      providerId: "deezer", score: 60, uptimePercent: 95, checks: 20, days: [],
      quality: { typical: "MP3 128 kbps", best: "MP3 128 kbps", lossless: false, samples: 4 },
      capabilities: [{ capability: "streaming", score: 60, uptimePercent: 95, checks: 20, failures: 1,
        typicalLatencyMilliseconds: 100, lastFailureCode: "resolve",
        parts: { earned: 84, reliability: 50, reliabilityOutOf: 50, speed: 19, speedOutOf: 20,
          quality: 15, qualityOutOf: 30, cappedAt: 60, typicalMilliseconds: 750,
          realPlays: 4, realPlayFailures: 0, timedOnRealPlays: true } }],
    };
    const html = render(ProviderScoreDetails, { props: { score, capability: "streaming", qualityTarget: "CdLossless" } }).body;
    expect(html).toContain("held at 60 because this source delivers MP3 128 kbps");
    expect(html).toContain("asked for CD lossless");
    expect(html).toContain("Songs start in about 750 ms in real plays");
    expect(html).toContain("4 of 4 real plays");
  });

  it("renders a recoverable pause with its reason and retry time", () => {
    const html = view([pause]);
    expect(html).toContain("Paused from automatic use after repeated failures");
    expect(html).toContain("Resume now");
    expect(html).toContain("Started");
    expect(html).toContain("Retry after");
    expect(html).toContain("Timed out");
    expect(html).toContain("Recent diagnostic checks");
  });

  it("leaves manual-only, disabled, successful and unsupported sources unpaused", () => {
    for (const html of [view([pause], false), view([{ ...pause, enabled: false }]),
      view([{ ...pause, canAttempt: true }]), view([{ ...pause, supported: false }])]) {
      expect(html).not.toContain("Resume now");
      expect(html).not.toContain("Paused from automatic use");
    }
  });
});
