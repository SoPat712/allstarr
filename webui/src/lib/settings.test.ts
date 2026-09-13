import { describe, expect, it } from "vitest";
import { fieldValue, mergeRoutingOrders, move, routingOrder } from "./settings";

describe("settings presentation", () => {
  it("reads nested schema values without provider-specific branches", () => {
    expect(fieldValue({ library: { storageMode: "Cache" } }, {
      key: "STORAGE_MODE",
      label: "Storage mode",
      type: "select",
      valuePath: "library.storageMode",
    })).toBe("Cache");
  });

  it("preserves configured routing order and appends new providers", () => {
    expect(routingOrder({ providers: { streamingOrder: "future-extension,deezer" } }, {
      id: "streaming",
      label: "Streaming",
      envKey: "MULTI_PROVIDER_STREAMING_ORDER",
      providers: ["deezer", "future-extension", "new-extension"],
    })).toEqual(["future-extension", "deezer", "new-extension"]);
  });

  it("refreshes saved routing groups without overwriting another unsaved order", () => {
    const groups = [
      { id: "streaming", label: "Streaming", envKey: "STREAMING", providers: ["a", "b"] },
      { id: "lyrics", label: "Lyrics", envKey: "LYRICS", providers: ["a", "b"] },
    ];
    expect(mergeRoutingOrders(
      { providers: { streamingOrder: "a,b", lyricsOrder: "a,b" } },
      groups,
      { streaming: ["a", "b"], lyrics: ["b", "a"] },
      ["lyrics"],
    )).toEqual({ streaming: ["a", "b"], lyrics: ["b", "a"] });
  });

  it("moves routes without crossing list bounds", () => {
    expect(move(["a", "b", "c"], 1, -1)).toEqual(["b", "a", "c"]);
    expect(move(["a"], 0, -1)).toEqual(["a"]);
  });
});
