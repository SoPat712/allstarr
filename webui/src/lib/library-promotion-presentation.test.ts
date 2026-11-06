import { describe, expect, it } from "vitest";
import { canCancelPromotion, promotionRequestStatus, requestSummary, requestLibraryFact, keptQuality, promotionGroup, promotionProgress, promotionTimeline, qualityWording, songStatus } from "./library-promotion-presentation";
import type { PromotionRun, PromotionTrack, QualityCheck } from "./library-promotion-api";

const track = (values: Partial<PromotionTrack> = {}): PromotionTrack => ({ recordingId: "song", song: { title: "Song", artist: "Artist", album: "Album" }, step: "Acquire", outcome: "Queued", reason: "Waiting", steps: [], ...values });
const run = (values: Partial<PromotionRun> = {}): PromotionRun => ({ id: "run", title: "Album", artist: "Artist", unit: "album", counts: { total: 3, added: 1, needsLook: 0 }, state: "active", step: "Lyrics", outcome: "Running", tracks: [], profileSteps: [{ key: "tags-cover", title: "MusicBrainz tags and cover art", description: "", enabled: true }], ...values });
const check = (values: Partial<QualityCheck> = {}): QualityCheck => ({ claimedSampleRate: 44100, claimedBitDepth: 16, effectiveBandwidthHz: 16000, verdict: "lossy-transcode-suspected", confidence: .65, ...values });
describe("promotion presentation", () => {
  it("shows actual lossless depth and rate or lossy bitrate in kept chips", () => {
    expect(keptQuality({ codec: "flac", bitDepth: 16, sampleRateHz: 44100 })).toBe("FLAC 16/44");
    expect(keptQuality({ codec: "aac", sampleRateHz: 44100, bitrateKbps: 256 })).toBe("AAC 256");
  });
  it("places review runs before completion and never calls placement confirmed", () => {
    expect(promotionGroup(run({ state: "needs-look" }))).toEqual({ id: "needs-look", label: "Needs a look", chip: "Check", tone: "warn" });
    expect(promotionGroup(run({ outcome: "Placed" })).id).toBe("active");
    expect(promotionGroup(run({ state: "added" })).chip).toBe("In library");
    expect(promotionGroup(run({ state: "cancelled", outcome: "Cancelled" })).label).toBe("Cancelled");
    expect(canCancelPromotion(run())).toBe(true);
    expect(canCancelPromotion(run({ outcome: "Placed" }))).toBe(false);
    expect(canCancelPromotion(run({ state: "cancelled", outcome: "Cancelled" }))).toBe(false);
  });
  it("uses confirmed counts and bounds the progress bar", () => {
    expect(promotionProgress(run())).toMatchObject({ added: 1, total: 3, label: "1 of 3 songs" });
    expect(promotionProgress(run()).percent).toBeCloseTo(100 / 3);
    expect(promotionProgress(run({ counts: { total: 0, added: 0, needsLook: 0 } })).percent).toBe(0);
    expect(promotionProgress(run({ counts: { total: 2, added: 7, needsLook: 0 } })).percent).toBe(100);
  });
  it("excludes songs already in the library from the adding progress", () => {
    const value = run({ counts: { total: 16, added: 11, needsLook: 0 }, tracks: [track({ recordingId: "one", steps: [{ name: "Dedupe", outcome: "Skipped", reason: "Already in library" }] }), track({ recordingId: "two", steps: [{ name: "Dedupe", outcome: "Skipped", reason: "Already in library" }] })] });
    expect(promotionProgress(value)).toMatchObject({ added: 9, total: 14, label: "9 of 14 songs" });
  });
  it("finishes the progress for added songs when another song was skipped", () => {
    expect(promotionProgress(run({ counts: { total: 3, added: 2, needsLook: 0 }, tracks: [track({ outcome: "Skipped" })] }))).toMatchObject({ added: 2, total: 2, percent: 100 });
  });
  it("maps song chips from actual steps without treating skipped songs as done", () => {
    expect(songStatus(track()).text).toBe("Queued");
    expect(songStatus(track({ outcome: "Needs review" })).text).toBe("Needs a look");
    expect(songStatus(track({ outcome: "Skipped" })).text).toBe("Skipped");
    expect(songStatus(track({ outcome: "Placed" })).text).toBe("Now");
    expect(songStatus(track({ outcome: "Placed", steps: [{ name: "Handover", outcome: "Complete", reason: "Confirmed" }] })).text).toBe("Done");
    expect(songStatus(track({ outcome: "Needs review" }), true).text).toBe("Cancelled");
  });
  it("describes recorded progress and names the real source", () => {
    const result = promotionTimeline(run({ tracks: [track({ provider: "qobuz", steps: [{ name: "Acquire", outcome: "Complete", reason: "Acquired" }, { name: "Quality", outcome: "Complete", reason: "Verified" }, { name: "Lyrics", outcome: "Running", reason: "Looking for lyrics" }] })] }));
    expect(result.filter(step => step.state !== "upcoming").map(step => step.title)).toEqual(["Downloaded 1 song from Qobuz", "1 song passed the lossless check", "Synced lyrics and ReplayGain"]);
    expect(result[3].tone).toBe("info");
    expect(result.find(step => step.title.includes("MusicBrainz"))?.state).toBe("upcoming");
  });
  it("does not call a disabled or skipped check a pass", () => {
    const result = promotionTimeline(run({ tracks: [track({ steps: [{ name: "Quality", outcome: "Skipped", reason: "Off in your profile" }] })] })).slice(1);
    expect(result[0].title).toBe("Lossless check is off");
    expect(result[0].tone).toBe("neutral");
  });
  it("uses the saved profile when a completed check was disabled", () => {
    const result = promotionTimeline(run({ profileSteps: [{ key: "quality", title: "Check", description: "Off", enabled: false }], tracks: [track({ steps: [{ name: "Quality", outcome: "Complete", reason: "Not measured" }, { name: "Tags", outcome: "Complete", reason: "Cover written" }] })] })).slice(1);
    expect(result[0].title).toBe("Lossless check is off");
    expect(result[1].title).toBe("Tags and cover art written");
  });
  it("describes an accepted suspicious copy as reviewed rather than passed", () => {
    const result = promotionTimeline(run({ tracks: [track({ qualityCheck: check(), steps: [{ name: "Quality", outcome: "Complete", reason: "Accepted with a reason" }] })] })).slice(1);
    expect(result[0].title).toBe("1 song accepted after review");
    expect(result[0].tone).toBe("warn");
  });
  it("keeps an accepted quality override queued until processing resumes", () => {
    const approved = track({ outcome: "Queued", approvedByUserId: "listener", approvalReason: "Only available mastering", qualityCheck: check(), steps: [{ name: "Quality", outcome: "Needs review", reason: "Screen result" }] });
    const result = promotionTimeline(run({ outcome: "Queued", tracks: [approved] })).slice(1);
    expect(result[0].title).toBe("1 song accepted after review");
    expect(result[0].detail).toBe("Accepted: Only available mastering");
    expect(songStatus(approved, false, true).text).toBe("Queued");
  });
  it("treats missing lyrics as a finished result and does not claim they synced", () => {
    const result = promotionTimeline(run({ tracks: [track({ steps: [{ name: "Lyrics", outcome: "None fits", reason: "No timing match" }, { name: "ReplayGain", outcome: "Complete", reason: "Gain measured" }] })] })).slice(3);
    expect(result[0].tone).toBe("ok");
    expect(result[0].title).toBe("ReplayGain measured; no synced lyrics fit");
  });
  it("keeps a timeline stage active while another song has not reached it", () => {
    const result = promotionTimeline(run({ tracks: [track({ recordingId: "first", steps: [{ name: "Acquire", outcome: "Complete", reason: "Acquired" }] }), track({ recordingId: "second" })] }));
    expect(result[0].title).toBe("Downloaded 1 song");
    expect(result[0].tone).toBe("info");
  });
});
describe("quality wording", () => {
  it("explains measured MP3-like cutoff in plain words", () => {
    expect(qualityWording(check())).toEqual({ title: "This copy looks upscaled from MP3.", detail: "Its sound stops at about 16 kHz, where a real CD-quality file keeps going to about 20 kHz." });
  });
  it("does not invent a cutoff for unknown measurements", () => {
    expect(qualityWording(check({ effectiveBandwidthHz: null, verdict: "unknown" })).detail).not.toMatch(/16|20|kHz/);
    expect(qualityWording(null, "No authorized source meets your floor.").detail).toBe("No authorized source meets your floor.");
  });
  it("distinguishes upsampling and padded depth from MP3", () => {
    expect(qualityWording(check({ verdict: "upsampled", likelySourceRate: 44100, effectiveBandwidthHz: 22050 })).detail).toContain("44.1 kHz");
    expect(qualityWording(check({ verdict: "padded-bit-depth", claimedBitDepth: 24 })).detail).toContain("24-bit");
    expect(qualityWording(check({ verdict: "padded-bit-depth" })).title).not.toContain("MP3");
  });
});


describe("library requests", () => {
  it("maps each request state without claiming that approval means added", () => {
    expect(promotionRequestStatus("pending")).toEqual({ text: "Requested · waiting for an admin", tone: "neutral" });
    expect(promotionRequestStatus("approved").text).toBe("Approved · adding to the library");
    expect(promotionRequestStatus("added")).toEqual({ text: "In library", tone: "ok" });
    expect(promotionRequestStatus("declined").text).toBe("Request declined");
    expect(promotionRequestStatus("cancelled").text).toBe("Request cancelled");
    expect(promotionRequestStatus("failed").text).toBe("Adding needs a look");
    expect(promotionRequestStatus("unknown").text).toBe("Request status unavailable");
  });
  it("uses the returned requester name and real song count", () => {
    expect(requestSummary({ artist: "SZA", requester: { id: "sam", displayName: "Sam" }, songCount: 23 })).toBe("SZA · requested by Sam · 23 songs");
    expect(requestSummary({ artist: "SZA", songCount: 1 })).toBe("SZA · requested by a listener · 1 song");
  });
  it("words empty, partial and complete library membership honestly", () => {
    expect(requestLibraryFact(0, 23)).toBe("None of its 23 songs");
    expect(requestLibraryFact(2, 23)).toBe("2 of its 23 songs");
    expect(requestLibraryFact(1, 1)).toBe("Its song is already there");
    expect(requestLibraryFact(23, 23)).toBe("All 23 songs");
  });
});

describe("timeline stage states", () => {
  it("marks an upcoming disabled check as off rather than next", () => {
    const steps = promotionTimeline(run({ profileSteps: [{ key: "quality", title: "Check", description: "Off", enabled: false }], tracks: [track()] }));
    expect(steps[1]).toMatchObject({ title: "Lossless check is off", detail: "Off in your profile", state: "skipped", tone: "neutral" });
  });
  it("shows completed, current and upcoming steps with truthful subtext", () => {
    const value = run({ tracks: [track({ outcome: "Running", step: "Lyrics", provider: "qobuz", steps: [
      { name: "Acquire", outcome: "Complete", reason: "Downloaded the verified copy" },
      { name: "Quality", outcome: "Complete", reason: "No upscaled files found" },
      { name: "Tags", outcome: "Complete", reason: "Release tags written" },
      { name: "Lyrics", outcome: "Running", reason: "Looking for matching lyrics" }
    ] })] });
    const steps = promotionTimeline(value);
    expect(steps.map(step => step.state)).toEqual(["complete", "complete", "complete", "current", "upcoming", "upcoming"]);
    expect(steps[3].detail).toBe("Looking for matching lyrics");
    expect(steps.slice(4).map(step => step.detail)).toEqual(["Next", "After the library finds the files"]);
    expect(steps.slice(4).every(step => step.tone === "neutral" && !/complete|written|rescanned/i.test(step.detail))).toBe(true);
  });
  it("does not repeat a completed sibling's subtext while another song is waiting", () => {
    const steps = promotionTimeline(run({ tracks: [track({ recordingId: "done", steps: [{ name: "Acquire", outcome: "Complete", reason: "Complete" }] }), track({ recordingId: "waiting" })] }));
    expect(steps[0].state).toBe("current");
    expect(steps[0].detail).toBe("Waiting for the remaining songs");
  });
});
