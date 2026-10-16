import { afterEach, expect, it, vi } from "vitest";
import { activityScopeKey, readActivityScope, rememberActivityScope } from "./activity-scope";

afterEach(() => vi.unstubAllGlobals());

it("defaults to Mine and remembers Everyone only for administrators", () => {
  const values = new Map<string, string>();
  vi.stubGlobal("localStorage", { getItem: (key: string) => values.get(key), setItem: (key: string, value: string) => values.set(key, value) });
  expect(readActivityScope(true)).toBe("mine");
  rememberActivityScope("everyone");
  expect(readActivityScope(true)).toBe("everyone");
  expect(readActivityScope(false)).toBe("mine");
  values.set(activityScopeKey, "invalid");
  expect(readActivityScope(true)).toBe("mine");
  rememberActivityScope("mine");
  expect(readActivityScope(true)).toBe("mine");
});

it("keeps Mine available when browser storage is blocked", () => {
  vi.stubGlobal("localStorage", { getItem() { throw new Error("blocked"); }, setItem() { throw new Error("blocked"); } });
  expect(readActivityScope(true)).toBe("mine");
  expect(() => rememberActivityScope("everyone")).not.toThrow();
});
