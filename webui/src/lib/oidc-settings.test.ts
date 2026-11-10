import { describe, expect, it } from "vitest";
import type { OidcAccess, OidcAccessDraft } from "./api";
import { oidcDraftDirty, oidcRedirectUri, validateOidcDraft } from "./oidc-settings";

const saved: OidcAccess = {
  enabled: true, authority: "https://identity.example.com", clientId: "allstarr",
  publicUrl: "https://music.example.com", displayName: "Home SSO",
  hasClientSecret: true, needsReview: false, revision: "1",
};
const draft: OidcAccessDraft = { ...saved, clientSecret: "", removeClientSecret: false };

describe("Single sign-on draft", () => {
  it("starts clean, changes locally, and becomes clean when restored", () => {
    const edited = { ...draft };
    expect(oidcDraftDirty(edited, saved)).toBe(false);
    edited.publicUrl = "https://new.example.com";
    expect(oidcDraftDirty(edited, saved)).toBe(true);
    expect(saved.publicUrl).toBe("https://music.example.com");
    edited.publicUrl = saved.publicUrl;
    expect(oidcDraftDirty(edited, saved)).toBe(false);
  });

  it("tracks every editable field and secret removal", () => {
    for (const change of [
      { enabled: false }, { authority: "https://new.example.com" }, { clientId: "new-client" },
      { displayName: "Sign in at home" }, { clientSecret: "replacement" }, { removeClientSecret: true },
    ]) expect(oidcDraftDirty({ ...draft, ...change }, saved)).toBe(true);
  });
});

describe("Single sign-on validation", () => {
  it("keeps a saved secret only for the same issuer and client", () => {
    expect(validateOidcDraft(draft, saved)).toEqual({});
    expect(validateOidcDraft({ ...draft, authority: `${saved.authority}///` }, saved)).toEqual({});
    for (const change of [{ authority: "https://new.example.com" }, { clientId: "new-client" }, { removeClientSecret: true }]) {
      expect(validateOidcDraft({ ...draft, ...change }, saved).clientSecret).toBeTruthy();
      expect(validateOidcDraft({ ...draft, ...change, clientSecret: "replacement", removeClientSecret: false }, saved)).toEqual({});
    }
    expect(validateOidcDraft(draft, { ...saved, hasClientSecret: false }).clientSecret).toBeTruthy();
  });

  it("reports all required fields together and permits turning off incomplete settings", () => {
    const empty = { ...draft, authority: " ", clientId: " ", publicUrl: " ", displayName: " " };
    expect(Object.keys(validateOidcDraft(empty, saved)).sort()).toEqual(["authority", "clientId", "clientSecret", "displayName", "publicUrl"]);
    expect(validateOidcDraft({ ...empty, enabled: false }, saved)).toEqual({});
  });

  it.each([
    "localhost", "speaker.local", "music.lan", "identity.home.arpa", "server.internal",
    "127.0.0.1", "10.2.3.4", "172.16.0.1", "192.168.1.50", "169.254.2.3", "100.64.0.1",
    "[::1]", "[fd12::1]", "[fe80::1]",
  ])("allows private HTTP addresses at %s", host => {
    expect(validateOidcDraft({ ...draft, authority: `http://${host}`, publicUrl: `http://${host}` }, saved).publicUrl).toBeUndefined();
    expect(validateOidcDraft({ ...draft, authority: `http://${host}`, clientSecret: "replacement" }, saved).authority).toBeUndefined();
  });

  it.each(["example.com", "172.32.0.1", "100.128.0.1", "[2001:db8::1]", "localhost.example.com"])("requires HTTPS for public host %s", host => {
    const errors = validateOidcDraft({ ...draft, authority: `http://${host}`, publicUrl: `http://${host}` }, saved);
    expect(errors.authority).toContain("https");
    expect(errors.publicUrl).toContain("https");
  });

  it.each(["invalid", "ftp://music.example.com", "https://user:password@music.example.com", "https://music.example.com?x=1", "https://music.example.com#section"])("rejects invalid address %s", publicUrl => {
    expect(validateOidcDraft({ ...draft, publicUrl }, saved).publicUrl).toBeTruthy();
  });

  it("requires the configured public path and a sign-in button name", () => {
    expect(validateOidcDraft({ ...draft, publicUrl: "https://music.example.com/allstarr///" }, saved, "/allstarr")).toEqual({});
    expect(validateOidcDraft(draft, saved, "/allstarr").publicUrl).toContain("/allstarr");
    expect(validateOidcDraft({ ...draft, publicUrl: "https://music.example.com/other" }, saved).publicUrl).toBeTruthy();
    expect(validateOidcDraft({ ...draft, displayName: " " }, saved).displayName).toBeTruthy();
  });

  it("checks secret size and conflicting replacement/removal even when disabled", () => {
    expect(validateOidcDraft({ ...draft, enabled: false, clientSecret: "x".repeat(4097) }, saved).clientSecret).toBeTruthy();
    expect(validateOidcDraft({ ...draft, enabled: false, clientSecret: "replacement", removeClientSecret: true }, saved).clientSecret).toBeTruthy();
    expect(validateOidcDraft({ ...draft, clientSecret: "x".repeat(4096) }, saved)).toEqual({});
  });

  it("checks stored field limits, control characters and nonempty addresses even when disabled", () => {
    const errors = validateOidcDraft({ ...draft, enabled: false, authority: `https://example.com/${"a".repeat(2048)}`, publicUrl: "http://music.example.com", clientId: "x".repeat(501), displayName: "Home\u0000SSO" }, saved);
    expect(Object.keys(errors).sort()).toEqual(["authority", "clientId", "displayName", "publicUrl"]);
    expect(validateOidcDraft({ ...draft, displayName: "x".repeat(501) }, saved).displayName).toBeTruthy();
    expect(validateOidcDraft({ ...draft, enabled: false, publicUrl: "https://music.example.com/other" }, saved)).toEqual({});
  });
});

describe("OIDC redirect URI", () => {
  it.each([
    ["http://192.168.1.50:5275", "http://192.168.1.50:5275/api/admin/auth/oidc/callback"],
    ["  http://identity.home.arpa///  ", "http://identity.home.arpa/api/admin/auth/oidc/callback"],
    ["https://music.example.com/allstarr/", "https://music.example.com/allstarr/api/admin/auth/oidc/callback"],
    ["", ""],
    ["   ", ""],
  ])("formats the address %j", (address, expected) => {
    expect(oidcRedirectUri(address)).toBe(expected);
  });
});
