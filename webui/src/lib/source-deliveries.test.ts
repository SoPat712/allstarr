import { describe, expect, it } from "vitest";
import type { ScrobbleDelivery } from "./api";
import { deliveryBreakdown, deliveryCounts, deliveryGroupReason, deliveryReasonLabel, groupDeliveries, listenCount } from "./source-deliveries";

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

describe("deliveryBreakdown", () => {
  const item = (providerCode: string, state: "retrying" | "permanentfailure" = "retrying") =>
    ({ id: providerCode + Math.random(), revision: 1, targetId: "listenbrainz", kind: "completed", state, providerCode, requiresReauthentication: false }) as ScrobbleDelivery;
  it("counts waiting listens by reason, largest first", () => {
    expect(deliveryBreakdown([item("song-unidentified", "permanentfailure"), item("metadata-unavailable"), item("metadata-unavailable"),
      item("delivery-not-attempted", "permanentfailure")])).toBe("2 waiting for song details · 1 couldn't be identified · 1 interrupted");
  });
  it("names a reauthentication wait by what fixes it", () => {
    expect(deliveryReasonLabel({ ...item("destination-changed"), requiresReauthentication: true })).toBe("need the account signed in again");
  });
});

describe("listens across services", () => {
  const listen = (id: string, targetId: string, providerCode: string, state: ScrobbleDelivery["state"]) => ({
    id, revision: 1, targetId, kind: "completed", state, providerCode, canResend: false, canDismiss: providerCode === "song-unidentified",
    requiresReauthentication: false, sendCount: 1, updatedAt: "2026-11-16T12:00:00Z",
    submission: { title: "Song " + id[0], artist: "Artist", occurredAt: "2026-11-1" + id[0] + "T12:00:00Z" },
  }) as ScrobbleDelivery;
  const items = [listen("1a", "lastfm", "song-unidentified", "permanentfailure"), listen("1b", "listenbrainz", "song-unidentified", "permanentfailure"),
    listen("2a", "lastfm", "song-unidentified", "permanentfailure"), listen("2b", "listenbrainz", "song-unidentified", "permanentfailure"),
    listen("3a", "lastfm", "metadata-unavailable", "retrying"), listen("3b", "listenbrainz", "metadata-unavailable", "retrying")];

  it("counts listens once and treats listens still being checked as waiting", () => {
    expect(deliveryCounts(items)).toEqual({ waiting: 1, unsendable: 2 });
  });

  it("pairs a listen's deliveries by its listen id when the song has no details", () => {
    const bare = items.map(item => ({ ...item, submission: null, listenId: "listen-" + item.id[0] }));
    expect(deliveryCounts(bare)).toEqual({ waiting: 1, unsendable: 2 });
  });

  it("puts one reason across services in one group and speaks about all of its songs", () => {
    const groups = groupDeliveries(items);
    expect(groups.map(group => listenCount(group.items))).toEqual([2, 1]);
    expect(deliveryGroupReason(groups[0].items[0], 2)).toBe("Allstarr couldn't identify these songs within seven days, so they can't be sent.");
    expect(deliveryGroupReason(groups[0].items[0], 1)).toBe("Allstarr couldn't identify this song within seven days, so it can't be sent.");
  });
});
