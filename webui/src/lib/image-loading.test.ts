import { describe, expect, it } from "vitest";
import { imageHasLoaded, reconcileImageSources } from "./image-loading";
import { providerMarkSources } from "./provider-marks";

const image = (source: string, complete = true, naturalWidth = 32) => ({
  getAttribute: () => source, complete, naturalWidth,
});

describe("image loading", () => {
  it("preserves a loaded mark when refreshed definitions produce equal URLs", () => {
    const definition = { id: "apple-musickit", name: "Apple Music", logoUrl: "/images/providers/applemusic.svg" };
    const urls = providerMarkSources(definition.id, definition);
    const loaded = { ...reconcileImageSources(undefined, urls), loaded: true };
    expect(reconcileImageSources(loaded, providerMarkSources(definition.id, { ...definition }))).toBe(loaded);
  });

  it("preserves a loaded fallback and an exhausted list across equal refreshes", () => {
    for (const index of [1, 2]) {
      const state = { ...reconcileImageSources(undefined, ["/brand.svg", "/fallback.svg"]), index, loaded: index === 1 };
      expect(reconcileImageSources(state, ["/brand.svg", "/fallback.svg"])).toBe(state);
    }
  });

  it("resets a changed URL list and recognizes an image already loaded from cache", () => {
    const previous = { ...reconcileImageSources(undefined, ["/old.svg"]), index: 1, loaded: true };
    expect(reconcileImageSources(previous, ["/new.svg"])).toMatchObject({ index: 0, loaded: false, failed: false });
    expect(reconcileImageSources(previous, ["/new.svg"], image("/new.svg"))).toMatchObject({ index: 0, loaded: true });
  });

  it("recognizes completion before handlers attach without accepting broken or stale images", () => {
    expect(imageHasLoaded(image("/brand.svg"), "/brand.svg")).toBe(true);
    expect(imageHasLoaded(image("/brand.svg", false), "/brand.svg")).toBe(false);
    expect(imageHasLoaded(image("/brand.svg", true, 0), "/brand.svg")).toBe(false);
    expect(imageHasLoaded(image("/old.svg"), "/brand.svg")).toBe(false);
    expect(imageHasLoaded(undefined, "/brand.svg")).toBe(false);
  });
});
