import { describe, expect, it } from "vitest";
import { nudgeLabel, weightDescription } from "./taste";

describe("taste presentation", () => {
  it("uses recent listening at the half-weight boundary", () => {
    expect(weightDescription(0.5)).toBe("Right now, what you've played today counts most.");
    expect(weightDescription(1)).toBe("Right now, what you've played today counts most.");
    expect(weightDescription(0.499)).toBe("Not much recent listening, so your usual taste counts most.");
    expect(weightDescription(0)).toBe("Not much recent listening, so your usual taste counts most.");
  });

  it("labels nudges and their duration", () => {
    expect(nudgeLabel({ kind: "tag", name: "R&B", mode: "more", expiresAt: "2026-10-29T04:00:00Z" })).toBe("More R&B · today only");
    expect(nudgeLabel({ kind: "tag", name: "EDM", mode: "less", expiresAt: null })).toBe("Less EDM");
    expect(nudgeLabel({ kind: "artist", name: "Artist X", mode: "never", expiresAt: null })).toBe("Never: Artist X");
  });
});
