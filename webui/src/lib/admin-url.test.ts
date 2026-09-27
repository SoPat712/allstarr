import { afterEach, describe, expect, it, vi } from "vitest";
import { adminUrl } from "./admin-url";

afterEach(() => vi.unstubAllGlobals());

function setBasePath(content: string | null) {
  vi.stubGlobal("document", {
    querySelector: vi.fn().mockReturnValue(content === null ? null : {
      getAttribute: vi.fn().mockReturnValue(content),
    }),
  });
}

describe("adminUrl", () => {
  it("defaults to root-relative URLs during SSR or development", () => {
    expect(adminUrl("/api/admin/ui/home")).toBe("/api/admin/ui/home");
  });

  it("prefixes same-origin root paths and is idempotent", () => {
    setBasePath("/control-room/");

    expect(adminUrl("/api/admin/ui/home")).toBe("/control-room/api/admin/ui/home");
    expect(adminUrl("/")).toBe("/control-room/");
    expect(adminUrl("/control-room/api/admin/ui/home")).toBe("/control-room/api/admin/ui/home");
    expect(adminUrl("/control-room?return=1")).toBe("/control-room?return=1");
  });

  it("leaves non-root and external URL forms untouched", () => {
    setBasePath("/admin");

    for (const value of [
      "#/library",
      "api/admin/ui/home",
      "./favicon.svg",
      "https://example.test/image.svg",
      "//cdn.example.test/image.svg",
      "mailto:admin@example.test",
    ]) {
      expect(adminUrl(value)).toBe(value);
    }
  });

  it("ignores malformed metadata instead of constructing an unsafe URL", () => {
    for (const value of ["admin", "/admin//", "/admin/%2e%2e", "/admin?x=1"]) {
      setBasePath(value);
      expect(adminUrl("/api/admin/ui/home")).toBe("/api/admin/ui/home");
    }
  });
});
