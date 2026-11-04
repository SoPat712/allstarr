import { describe, expect, it } from "vitest";
import { groupDeliveries } from "../src/lib/source-deliveries";
import type { ScrobbleDelivery } from "../src/lib/api";

const listen = (id: string, extra: Partial<ScrobbleDelivery> = {}): ScrobbleDelivery => ({
  id, revision: 1, targetId: "listenbrainz", state: "permanentfailure", providerCode: "destination-unbound",
  safeMessage: "Confirm the destination.", requiresReauthentication: false, sendCount: 0,
  suggestedDestinationAccountId: "first", availableDestinationAccountIds: ["first"], updatedAt: "2026-11-03T12:00:00Z", ...extra,
});

describe("listen repair groups", () => {
  it("groups a repeated cause only for the same destination and retry state", () => {
    const groups = groupDeliveries([listen("one"), listen("two"), listen("other", { suggestedDestinationAccountId: "second" }),
      listen("retry", { state: "retrying" }), listen("auth", { requiresReauthentication: true }), listen("unknown", { attemptStartedAt: "2026-11-03T12:00:00Z" })]);
    expect(groups.map(group => group.items.map(item => item.id))).toEqual([["one", "two"], ["other"], ["retry"], ["auth"], ["unknown"]]);
  });
  it("keeps different services and reasons separate even when their messages match", () => {
    expect(groupDeliveries([listen("one"), listen("two", { targetId: "lastfm" }), listen("three", { providerCode: "ambiguous" })])).toHaveLength(3);
  });
});
