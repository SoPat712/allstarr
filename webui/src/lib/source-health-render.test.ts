import { describe, expect, it } from "vitest";
import { render } from "svelte/server";
import SourceHealthHistory from "./components/SourceHealthHistory.svelte";
import type { ProviderHealth } from "./api";

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
