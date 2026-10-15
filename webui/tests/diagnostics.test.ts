import { expect, it } from "vitest";
import { diagnosticFailureMessage } from "../src/lib/diagnostics";

it.each([
  ["capability", "does not offer"], ["account-resolution", "account access"],
  ["concurrency", "another test"], ["track-selection", "song"],
  ["resolve", "connection"], ["lease-validation", "fresh"], ["first-byte", "audio"], ["response-range", "wrong part"],
])("explains %s safely with a next step", (stage, phrase) => {
  const message = diagnosticFailureMessage({ stage, error: "raw-private-provider-payload" });
  expect(message).toContain(phrase);
  expect(message).not.toContain("raw-private");
  expect(message).not.toContain(stage + ":");
});
it("points an Apple sign-in failure to the setup checklist", () => {
  expect(diagnosticFailureMessage({ stage: "resolve", failureCode: "apple-download-sign-in-required" })).toContain("step 2");
});
it("explains account mismatch without exposing an identifier", () => {
  expect(diagnosticFailureMessage({ stage: "first-byte", failureCode: "account_mismatch" })).toContain("same Apple account");
});
