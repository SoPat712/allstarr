import { describe, expect, it } from "vitest";
import { originalCopyName } from "./listeners";

describe("personal copy names", () => {
  const link = { id: "12345678-1234-1234-1234-123456789abc", name: "Household favorites",
    targetBackendInstanceId: "main", targetPlaylistId: "target" };
  it("resolves a virtual original from the existing playlist list", () => {
    expect(originalCopyName({ originalPlaylistId: "allstarr-vpl-12345678123412341234123456789abc",
      originalTargetPlaylistId: null, backendInstanceId: "main" }, [link])).toBe("Household favorites");
  });
  it("resolves target originals only within the same server", () => {
    const copy = { originalPlaylistId: "other", originalTargetPlaylistId: "target", backendInstanceId: "main" };
    expect(originalCopyName(copy, [link])).toBe("Household favorites");
    expect(originalCopyName({ ...copy, backendInstanceId: "other" }, [link])).toBe("Original name unavailable");
  });
  it("never displays an unknown original's identifier", () => {
    expect(originalCopyName({ originalPlaylistId: "private-id", originalTargetPlaylistId: null,
      backendInstanceId: "main" }, [])).toBe("Original name unavailable");
  });
});
