import { describe, expect, it } from "vitest";
import {
  availablePackages, compareVersions, currentPackages, sessionReasonText, sessionSummary, valueChanges,
  verificationHost,
} from "./extensions";
import type { ExtensionPackage, ExtensionStoreItem } from "./api";

const pkg = (overrides: Partial<ExtensionPackage> = {}): ExtensionPackage => ({
  id: "1", extensionId: "demo", displayName: "Demo", version: "1.0.0", lifecycle: "active",
  state: "active", active: true, installed: true, permissionReviewRequired: false,
  hasPermissions: true,
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
      { id: "demo", version: "1.1.0" },
      { id: "new", version: "1.0.0" },
    ] as ExtensionStoreItem[];
    expect(availablePackages(store, [pkg()]).map((item) => `${item.id}:${item.version}`))
      .toEqual(["demo:1.1.0", "new:1.0.0"]);
  });

  it("labels added, unchanged, and removed update access", () => {
    expect(valueChanges(["network", "secret"], ["network", "cache"])).toEqual([
      { value: "network", change: "unchanged" },
      { value: "secret", change: "added" },
      { value: "cache", change: "removed" },
    ]);
  });
});

describe("extension sign-in", () => {
  it("names every session state in words, not only color", () => {
    expect(sessionSummary({ state: "signed_out" })).toMatchObject({ label: "Not signed in", badge: "suggested" });
    expect(sessionSummary({ state: "verification_pending" })).toMatchObject({ label: "Verification needed" });
    expect(sessionSummary({ state: "expired", expiresAt: "2026-01-01T00:00:00Z" }))
      .toMatchObject({ label: "Sign-in expired", badge: "degraded" });
    const signedIn = sessionSummary({ state: "signed_in", expiresAt: "2099-06-15T12:00:00Z" }, "en-US");
    expect(signedIn).toMatchObject({ label: "Signed in", badge: "healthy" });
    expect(signedIn.detail).toContain("2099");
  });

  it("explains failures with a recovery step and never echoes unknown codes", () => {
    expect(sessionReasonText(null)).toBe("");
    expect(sessionReasonText("origin_not_approved")).toContain("Review access");
    expect(sessionReasonText("provider_unavailable")).toContain("Try again");
    expect(sessionReasonText("internal_detail_code")).toBe("Sign-in didn't complete. Try again.");
  });

  it("shows only the host of safe verification links", () => {
    expect(verificationHost("https://verify.example.test/challenge?id=private")).toBe("verify.example.test");
    expect(verificationHost("javascript:alert(1)")).toBe("");
    expect(verificationHost("http://verify.example.test/")).toBe("");
    expect(verificationHost("not a url")).toBe("");
  });
});
