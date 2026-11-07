import { describe, expect, it } from "vitest";
import { ApiError } from "./api-client";
import { promotionPanelFailure, promotionSetupHint } from "./promotion-setup";

describe("promotion panel failures", () => {
  it("maps missing folder setup to a calm state", () => {
    expect(promotionPanelFailure(new ApiError(503, "Unavailable", { reason: "library-folder-not-set", error: "Setup needed" })))
      .toEqual({ setupRequired: true, error: "" });
    expect(promotionSetupHint).toBe("Adding to your library isn't set up");
  });
  it.each(["library-folder-unavailable", "source-changed", "destination-exists", "not-authorized", "unexpected-error"])("keeps actionable errors for %s", reason => {
    expect(promotionPanelFailure(new ApiError(409, "Conflict", { reason, error: "Review this problem" })))
      .toEqual({ setupRequired: false, error: "Review this problem" });
  });
});
