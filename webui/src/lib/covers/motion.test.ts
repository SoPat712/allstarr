import { describe, expect, it } from "vitest";
import { shouldAnimate } from "./motion";

describe("cover motion", () => {
  it("keeps every cover still with reduced motion", () => {
    for (const liquid of [false, true]) for (const engaged of [false, true]) {
      expect(shouldAnimate({ reduced: true, onScreen: true, pageVisible: true, liquid, engaged })).toBe(false);
    }
  });
  it("drifts visible liquid and boils printed covers only while engaged", () => {
    expect(shouldAnimate({ reduced: false, onScreen: true, pageVisible: true, liquid: true, engaged: false })).toBe(true);
    expect(shouldAnimate({ reduced: false, onScreen: true, pageVisible: true, liquid: false, engaged: true })).toBe(true);
    expect(shouldAnimate({ reduced: false, onScreen: true, pageVisible: true, liquid: false, engaged: false })).toBe(false);
  });
  it("stops offscreen and hidden page covers", () => {
    for (const liquid of [false, true]) {
      expect(shouldAnimate({ reduced: false, onScreen: false, pageVisible: true, liquid, engaged: true })).toBe(false);
      expect(shouldAnimate({ reduced: false, onScreen: true, pageVisible: false, liquid, engaged: true })).toBe(false);
    }
  });
});
