import { expect, it } from "vitest";
import { diagnosticFailureMessage, diagnosticIsNeutral } from "../src/lib/diagnostics";

it("explains an unavailable test sample without blaming the service", () => {
  for (const metric of ["api-latency", "cts"])
    expect(diagnosticFailureMessage({ metric, stage: "resolve", reasonCode: "sample-unavailable" }))
      .toBe("Sample unavailable, retrying another recording.");
  expect(diagnosticIsNeutral({ failureCode: "sample-unavailable" })).toBe(true);
  expect(diagnosticIsNeutral({ reasonCode: "probe_not_available" })).toBe(true);
  expect(diagnosticIsNeutral({ reasonCode: "download-not-verified" })).toBe(true);
  expect(diagnosticIsNeutral({ reasonCode: "service_unavailable" })).toBe(false);
});

it.each(["probe_not_available", "download-not-verified"])("explains neutral %s without calling it a failure", (reasonCode) => {
  const message = diagnosticFailureMessage({ metric: "api-latency", capability: "download", reasonCode });
  expect(message).not.toContain("failed");
  expect(message).toContain(reasonCode === "probe_not_available" ? "no automatic check" : "not been verified");
});

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

it("explains a download service failure without suggesting setup changes", () => {
  expect(diagnosticFailureMessage({ stage: "resolve", reasonCode: "service_unavailable" }))
    .toBe("The source service is unavailable. Try again later.");
});
it("explains a diagnostic timeout", () => {
  expect(diagnosticFailureMessage({ stage: "resolve", reasonCode: "timeout" })).toContain("too long");
});


it.each(["lyrics", "metadata", "playlist", "intelligence"])("keeps %s probe failures separate from playback", (capability) => {
  const message = diagnosticFailureMessage({ metric: "api-latency", capability, reasonCode: "probe_failed" });
  expect(message.toLowerCase()).toContain(capability);
  expect(message.toLowerCase()).not.toContain("playback");
});

it.each(["timeout", "service_unavailable", "lyrics-upstream-busy-or-timed-out"])("explains a busy lyrics probe with %s", (reasonCode) => {
  const message = diagnosticFailureMessage({ metric: "api-latency", capability: "lyrics", reasonCode });
  expect(message).toContain("lyrics upstream is busy or timed out");
  expect(message).not.toContain("playback");
});

it("explains a connection check's failed lyrics capability", () => {
  expect(diagnosticFailureMessage({ metric: "api-latency", failedCapabilities: [
    { capability: "lyrics", reasonCode: "lyrics-upstream-busy-or-timed-out" },
  ] })).toContain("lyrics upstream is busy or timed out");
});

for (const capability of ["metadata", "download", "streaming"]) {
  it(`explains an unavailable ${capability} endpoint without suggesting account changes`, () => {
    expect(diagnosticFailureMessage({ metric: "api-latency", capability, reasonCode: "service_unavailable" }))
      .toBe("The source service is unavailable. Try again later.");
  });
}

for (const reasonCode of ["TransientFailure", "transient-failure", "http-501"]) {
  it(`preserves the upstream meaning of ${reasonCode} during playback checks`, () => {
    expect(diagnosticFailureMessage({ stage: "resolve", reasonCode }))
      .toBe("The source service is unavailable. Try again later.");
  });
}
