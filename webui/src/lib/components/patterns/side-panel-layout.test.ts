import { describe, expect, it } from "vitest";
import { sidePanelLayout } from "./side-panel-layout";

describe("side sheet layout", () => {
  it("overlays desktops when the expanded drawer leaves too little list space", () => {
    for (const width of [1024, 1071, 1100, 1180]) {
      expect(sidePanelLayout(width, width - 248 - 48).docked).toBe(false);
    }
  });

  it("docks at 1280 with an expanded drawer and retains at least 520 pixels", () => {
    expect(sidePanelLayout(1280, 1280 - 248 - 48)).toEqual({ width: 420, docked: true });
    expect(sidePanelLayout(1280, 939).docked).toBe(false);
    expect(sidePanelLayout(1280, 940).docked).toBe(true);
    expect(sidePanelLayout(1180, 1180 - 80 - 48)).toEqual({ width: 420, docked: true });
    expect(sidePanelLayout(1440, 1440 - 248 - 48)).toEqual({ width: 480, docked: true });
  });

  it("uses a narrower panel below 1440 and a full width phone sheet", () => {
    expect(sidePanelLayout(1000, 1000).width).toBe(360);
    expect(sidePanelLayout(899, 1500)).toEqual({ width: 899, docked: false });
    expect(sidePanelLayout(390, 1500)).toEqual({ width: 390, docked: false });
  });
  it("docks a compact sheet when the list retains its requested width", () => {
    expect(sidePanelLayout(1280, 984, { width: 380, minimumContentWidth: 560 })).toEqual({ width: 380, docked: true });
    expect(sidePanelLayout(1024, 728, { width: 380, minimumContentWidth: 560 }).docked).toBe(false);
    expect(sidePanelLayout(390, 366, { width: 380, minimumContentWidth: 560 })).toEqual({ width: 390, docked: false });
  });
});
