import { describe, expect, it } from "vitest";
import { sidePanelLayout } from "./side-panel-layout";

describe("side sheet layout", () => {
  it("overlays medium desktops with an expanded navigation drawer", () => {
    for (const width of [1024, 1071, 1100, 1180, 1280]) {
      expect(sidePanelLayout(width, width - 248 - 48).docked).toBe(false);
    }
  });

  it("docks only when the measured content retains 640 pixels", () => {
    expect(sidePanelLayout(1280, 1159)).toEqual({ width: 480, docked: true });
    expect(sidePanelLayout(1280, 1119).docked).toBe(false);
    expect(sidePanelLayout(1280, 1120).docked).toBe(true);
    expect(sidePanelLayout(1180, 1180 - 80 - 48).docked).toBe(false);
    expect(sidePanelLayout(1440, 1440 - 248 - 48).docked).toBe(true);
  });

  it("keeps phone sheets full width even in a large containing element", () => {
    expect(sidePanelLayout(899, 1500)).toEqual({ width: 899, docked: false });
  });
});
