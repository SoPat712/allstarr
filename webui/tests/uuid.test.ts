import { afterEach, expect, test, vi } from "vitest";
import { newUuid } from "../src/lib/uuid";

afterEach(() => vi.unstubAllGlobals());

test("UUID generation works when randomUUID is unavailable", () => {
  const getRandomValues = vi.fn((bytes: Uint8Array) => { bytes.fill(255); return bytes; });
  vi.stubGlobal("crypto", { getRandomValues, randomUUID: undefined });
  expect(newUuid()).toBe("ffffffff-ffff-4fff-bfff-ffffffffffff");
  expect(getRandomValues).toHaveBeenCalledOnce();
});

test("UUID generation uses the native API when available", () => {
  const randomUUID = vi.fn(() => "11111111-1111-4111-8111-111111111111");
  vi.stubGlobal("crypto", { randomUUID });
  expect(newUuid()).toBe("11111111-1111-4111-8111-111111111111");
  expect(randomUUID).toHaveBeenCalledOnce();
});
