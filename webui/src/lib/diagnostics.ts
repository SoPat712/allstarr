import { checkGuide } from "./sources";

export type DiagnosticFailure = { stage?: string; failureCode?: string | null; reasonCode?: string | null; error?: string | null };
export function diagnosticFailureMessage(result: DiagnosticFailure) {
  const code = (result.failureCode ?? result.reasonCode ?? result.error ?? "").toLowerCase();
  if (code === "service_unavailable" || code === "timeout") return checkGuide(code)!.step;
  if (code.includes("account_mismatch") || code.includes("account-mismatch"))
    return "Use the same Apple account for the Media User Token and playback sign-in. Check steps 1 and 2.";
  if (code.includes("sign-in-required") || code.includes("needs_login"))
    return "Apple Music needs a playback sign-in. Open step 2 and sign in, then test again.";
  if (code.includes("unauthorized") || code.includes("needsconfiguration") || code.includes("accountneedsconfiguration"))
    return "The connection needs attention. Check its saved account details and sign-in, then test again.";
  const messages: Record<string, string> = {
    capability: "This source does not offer playback testing. Choose a source with streaming support.",
    "account-resolution": "Check this account's account access and whether it is enabled, then test again.",
    concurrency: "Wait for another test to finish, then try again.",
    "track-selection": "No song is available to test. Add a playlist from this source or choose a song, then try again.",
    resolve: "The playback connection could not be prepared. Check this source's setup and sign-in, then test again.",
    "lease-validation": "The playback link is not usable. Run a fresh test; if it still fails, check the source's setup.",
    "response-range": "The source returned the wrong part of the audio. Run a fresh test; if it still fails, check the source. Full playback may still work.",
    "first-byte": "The source did not return usable audio. Check its connection and sign-in, then test again.",
  };
  return messages[result.stage ?? ""] ?? "Playback could not be tested. Check the source's connection and try again.";
}
