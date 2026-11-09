import { describe, expect, it } from "vitest";
import type { MixCard } from "../api";
import { bold, coverFor, hsv } from "./cover-for";

function mix(kind: string, overrides: Partial<MixCard> = {}): MixCard {
  return { id: kind, kind, name: kind, description: "", section: "madeForYou", sourceLabel: null, recipeVersion: null, updatedAt: null, readiness: { ready: true, message: "" }, inApps: true, appsAllowed: true, coverArtwork: [], sortOrder: 0, ...overrides };
}

describe("mix gradients", () => {
  it.each(["daily-1", "discover-weekly", "release-radar", "on-repeat", "rewind", "chill", "focus", "workout", "party", "sleep", "custom", "release"])("gives %s two stops at its own angle", kind => {
    expect(coverFor(mix(kind))).toMatch(/^linear-gradient\(\d{1,3}deg, #[a-f0-9]{6}, #[a-f0-9]{6}\)$/);
  });
  it("gives two mixes of one kind different colours", () => {
    expect(coverFor(mix("daily-1", { id: "first" }))).not.toBe(coverFor(mix("daily-1", { id: "second" })));
    expect(coverFor(mix("daily-1", { id: "first", name: "Other" }))).toBe(coverFor(mix("daily-1", { id: "first" })));
  });
  it("matches the server's colours", () => {
    expect([hsv(0, 100, 100), hsv(120, 100, 100), hsv(240, 100, 50), hsv(390, 0, 100), hsv(200, 78, 96)])
      .toEqual(["#ff0000", "#00ff00", "#000080", "#ffffff", "#36b5f5"]);
    // The server draws the same (MixCoverRendererTests.Gradients_match_the_web_ui).
    expect(bold("daily-mix-one")).toEqual({ stops: ["#9a91fa", "#3db1db"], angle: 19 });
    expect(bold("mix-2")).toEqual({ stops: ["#9e2392", "#572a1a"], angle: 176 });
  });
  it.each(["Last.fm", "LAST FM", "last-fm"])("keeps %s colours", sourceLabel => {
    expect(coverFor(mix("external", { sourceLabel }))).toBe("linear-gradient(135deg, #d51007, #f6efe6)");
  });
  it("keeps ListenBrainz colours", () => {
    expect(coverFor(mix("external", { sourceLabel: "ListenBrainz" }))).toBe("linear-gradient(135deg, #353070, #eb743b, #9a93e6)");
  });
});
