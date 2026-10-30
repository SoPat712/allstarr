import { describe, expect, it } from "vitest";
import { ApiError } from "./api";
import { playDuration, playPhase, playOutcome, playStart, playSource, playComparison, playActionError } from "./play-traces";

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

describe("play comparison and action errors", () => {
  const play = {
    request: { presented: { title: "Song", artist: "Singer", album: "Album", durationMilliseconds: 185000 } },
    resolution: { provider: "applemusic", cached: false },
    received: { tags: { title: "Song live", artist: "Singer" }, durationSeconds: 201 },
    differences: ["title-mismatch", "duration-mismatch"],
  };
  it("builds short comparison lines and highlights supplied differences only on what played", () => {
    const rows = playComparison(play, []);
    expect(rows.map(row => row.label)).toEqual(["Asked for", "Played"]);
    expect(rows[0].fields.map(field => field.text)).toEqual(["Song", "Singer", "Album", "3:05"]);
    expect(rows[1].fields.map(field => field.text)).toEqual(["Song live", "Singer", "3:21", "Apple Music"]);
    expect(rows.map(row => row.fields.filter(field => field.different).map(field => field.text)))
      .toEqual([[], ["Song live", "3:21"]]);
    expect(playComparison({ ...play, differences: [] }, [])[1].fields.every(field => !field.different)).toBe(true);
  });
  it("shows the request and source when received facts were not recorded", () => {
    const rows = playComparison({ ...play, received: null }, []);
    expect(rows).toHaveLength(1);
    expect(rows[0].fields.at(-1)?.text).toBe("Apple Music");
    expect(rows[0].fields.every(field => !field.different)).toBe(true);
    expect(playComparison({ ...play, received: { tags: null } }, [])[1].fields.map(field => field.text))
      .toEqual(["Unknown song", "Unknown artist", "—", "Apple Music"]);
  });
  it("uses server sentences but hides codes, JSON and transport failures", () => {
    expect(playActionError(new ApiError(503, "Unavailable", { error: "The library is unavailable. Try again later." })))
      .toBe("The library is unavailable. Try again later.");
    expect(playActionError(new ApiError(503, "Unavailable", { error: "Try again in 30 seconds" }))).toBe("Try again in 30 seconds");
    for (const error of [new Error("Failed to fetch"), new ApiError(500, "Error", { error: "match_unavailable" }),
      new ApiError(500, "Error", { error: '{"error":"failed"}' }), new ApiError(500, "Error", { error: "Failed at /private/library." })])
      expect(playActionError(error)).toBe("That didn't work. Try again.");
  });
});
