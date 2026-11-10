import { describe, expect, it } from "vitest";
import { oidcRedirectUri } from "./oidc-settings";

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
