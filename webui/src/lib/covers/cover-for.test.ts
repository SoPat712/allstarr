import { describe, expect, it } from "vitest";
import type { MixCard, MixSong } from "../api";
import { coverFor } from "./cover-for";
import { LIQUID, paletteFor } from "./palettes";

function mix(kind: string, overrides: Partial<MixCard> = {}): MixCard {
  return { id: kind, kind, name: kind, description: "", section: "madeForYou", sourceLabel: null, recipeVersion: null, updatedAt: null, readiness: { ready: true, message: "" }, inApps: true, appsAllowed: true, coverArtwork: [], sortOrder: 0, ...overrides };
}

describe("mix gradients", () => {
  it.each(["daily-1", "discover-weekly", "release-radar", "on-repeat", "rewind", "chill", "focus", "workout", "party", "sleep", "custom", "external"])("provides a plain gradient for %s", kind => {
    expect(coverFor(mix(kind))).toMatch(/^linear-gradient\(135deg, #[a-f0-9]{6}(, #[a-f0-9]{6})+\)$/);
  });
  it("retains the Blend genre colours from its description", () => {
    expect(coverFor(mix("daily-2", { description: "Made for you · hip-hop · Artists" }))).toBe(`linear-gradient(135deg, ${LIQUID["Hip-hop"].join(", ")})`);
  });
  it("uses a clean Blend name when description metadata is absent", () => {
    expect(coverFor(mix("daily-1", { name: "Jazz Blend" }))).toContain(LIQUID.Jazz.join(", "));
  });
  it.each(["Last.fm", "LAST FM", "last-fm"])("keeps %s colours", sourceLabel => {
    expect(coverFor(mix("external", { sourceLabel }))).toBe("linear-gradient(135deg, #d51007, #f6efe6)");
  });
  it("keeps ListenBrainz colours", () => {
    expect(coverFor(mix("external", { sourceLabel: "ListenBrainz" }))).toBe("linear-gradient(135deg, #353070, #eb743b, #9a93e6)");
  });
  it("keeps preset colours independent of the title", () => {
    expect(coverFor(mix("on-repeat", { name: "Other" }))).toBe("linear-gradient(135deg, #f6f2ea, #2e3b55, #e8443a)");
  });
  it("uses the most common song genre for saved mixes with stable ties", () => {
    const songs = ["Jazz", "Pop", "Jazz"].map(genre => ({ song: { genre } }) as MixSong);
    const palette = paletteFor("Jazz");
    expect(coverFor(mix("custom"), songs)).toBe(`linear-gradient(135deg, ${[palette.paper, ...palette.inks].join(", ")})`);
    expect(coverFor(mix("custom"), songs.slice(0, 2))).toBe(coverFor(mix("custom"), songs.slice(0, 2).reverse()));
  });
});
