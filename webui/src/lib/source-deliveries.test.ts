import { describe, expect, it } from "vitest";
import type { ScrobbleDelivery } from "./api";
import { deliveryCounts } from "./source-deliveries";

const delivery: ScrobbleDelivery = { id: "listen", revision: 1, targetId: "listenbrainz", kind: "completed", state: "retrying", canResend: true, requiresReauthentication: false, sendCount: 0, updatedAt: "2026-11-06T12:00:00Z", submission: { title: "Song", artist: "Artist", occurredAt: "2026-11-06T12:00:00Z" } };

describe("delivery counts", () => {
  it("separates saved listens that can be resent from unavailable submissions", () => {
    expect(deliveryCounts([delivery, { ...delivery, id: "blocked", state: "permanentfailure", canResend: false, submission: null }])).toEqual({ waiting: 1, unsendable: 1 });
  });

  it("keeps reconnectable listens waiting and excludes notices and completed deliveries", () => {
    expect(deliveryCounts([{ ...delivery, requiresReauthentication: true }, { ...delivery, kind: "nowplaying" }, { ...delivery, state: "delivered" }, { ...delivery, state: "ignored" }])).toEqual({ waiting: 1, unsendable: 0 });
    expect(deliveryCounts([])).toEqual({ waiting: 0, unsendable: 0 });
  });
});
