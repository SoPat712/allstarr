import { describe, expect, it } from "vitest";
import { fieldValue, move, routingOrder } from "./settings";

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

  it("moves routes without crossing list bounds", () => {
    expect(move(["a", "b", "c"], 1, -1)).toEqual(["b", "a", "c"]);
    expect(move(["a"], 0, -1)).toEqual(["a"]);
  });
});
