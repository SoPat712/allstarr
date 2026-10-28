import { describe, expect, it } from "vitest";
import { playDuration, playPhase, playOutcome, playStart, playSource } from "./play-traces";

describe("play history wording", () => {
  it("describes short, long and unavailable durations in words", () => {
    expect(playDuration(0)).toBe("Less than a second");
    expect(playDuration(40)).toBe("Less than a second");
    expect(playDuration(1200)).toBe("1.2 seconds");
    expect(playDuration(1000)).toBe("1 second");
    expect(playDuration(60000)).toBe("1 minute");
    expect(playDuration(61500)).toBe("1 minute 1.5 seconds");
    for (const value of [null, undefined, -1, NaN, Infinity]) expect(playDuration(value)).toBe("Time not recorded");
  });
  it("only calls playback started when the first audio arrived", () => {
    expect(playStart(1200)).toBe("Started in 1.2 s");
    expect(playStart(0)).toBe("Started in less than 0.1 s");
    expect(playStart(null)).toBe("Start time not recorded");
  });
  it("names every start step without exposing phase keys", () => {
    const phases = ["Route", "Account", "Lease", "Source", "Verify", "Retry", "Segments", "Process", "Headers", "FirstByte", "Preparation"];
    expect(phases.map(playPhase)).toEqual(["Choosing where to play", "Choosing an account", "Waiting for a turn", "Opening the source", "Checking the audio", "Trying again", "Loading audio pieces", "Preparing the audio", "Ready to send audio", "Sending the first audio", "Getting the song ready"]);
    expect(playPhase("future_step")).toBe("Preparing playback");
  });
  it("uses familiar source and outcome names", () => {
    expect(playSource("spotify", true, [])).toBe("Saved copy");
    expect(playSource("custom-source", false, [{ id: "custom-source", name: "My music" }])).toBe("My music");
    expect(playSource("applemusic", false, [])).toBe("Apple Music");
    expect(playSource("private-key", false, [])).toBe("Music source");
    expect(playSource("library", false, [])).toBe("Your library");
    expect(playOutcome("started")).toEqual({ state: "success", label: "Started" });
    expect(playOutcome("failed")).toEqual({ state: "failed", label: "Could not play" });
    expect(playOutcome("future_result")).toEqual({ state: "unknown", label: "Not recorded" });
  });
});
