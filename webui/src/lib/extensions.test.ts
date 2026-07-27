import { describe, expect, it } from "vitest";
import { availablePackages, compareVersions, currentPackages } from "./extensions";
import type { ExtensionPackage, ExtensionStoreItem } from "./api";

const pkg = (overrides: Partial<ExtensionPackage> = {}): ExtensionPackage => ({
  id: "1", extensionId: "demo", displayName: "Demo", version: "1.0.0", lifecycle: "active",
  state: "active", active: true, installed: true, permissionReviewRequired: false,
  stagedAt: "2026-01-01T00:00:00Z", revision: 1, ...overrides,
});

it("compares dotted extension versions", () => {
  expect(compareVersions("1.10.0", "1.9.9")).toBeGreaterThan(0);
  expect(compareVersions("2.0", "2.0.0")).toBe(0);
  expect(compareVersions("2.0.0", "2.0.0-beta")).toBeGreaterThan(0);
});

describe("extension catalog", () => {
  it("keeps the newest live package per extension", () => {
    expect(currentPackages([
      pkg(), pkg({ id: "2", version: "2.0.0", stagedAt: "2026-02-01T00:00:00Z" }),
      pkg({ id: "3", extensionId: "gone", state: "uninstalled" }),
    ])).toEqual([expect.objectContaining({ id: "2" })]);
  });

  it("offers only new or newer packages", () => {
    const store = [
      { id: "demo", version: "1.0.0" },
      { id: "new", version: "1.0.0" },
    ] as ExtensionStoreItem[];
    expect(availablePackages(store, [pkg()]).map((item) => item.id)).toEqual(["new"]);
  });
});
