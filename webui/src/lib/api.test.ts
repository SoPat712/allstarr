import { describe, expect, it } from "vitest";
import { normalizeResponse } from "./api";

describe("API response normalization", () => {
  it("normalizes nested ASP.NET property names without changing values", () => {
    expect(normalizeResponse({
      Accounts: [{ Id: "account", Secret: { Configured: true } }],
      TechnicalDetails: { sourceId: "track" },
    })).toEqual({
      accounts: [{ id: "account", secret: { configured: true } }],
      technicalDetails: { sourceId: "track" },
    });
  });
});
