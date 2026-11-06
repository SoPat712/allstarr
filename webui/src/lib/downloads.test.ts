import { describe, expect, it } from "vitest";
import type { ManagedDownload } from "./api";
import { filterDownloads, groupDownloadSongs, qualityDetails, qualityWarning } from "./downloads";

const file = (title: string, provider: string, size: number): ManagedDownload => ({
  path: `${title}.flac`,
  storage: "cache",
  artist: "Artist",
  album: "Album",
  title,
  fileName: `${title}.flac`,
  size,
  sizeFormatted: `${size} B`,
  lastModified: "2026-07-27T00:00:00Z",
  codec: "FLAC",
  bitrateKbps: 900,
  sampleRateHz: 44_100,
  bitDepth: 24,
  channels: 2,
  quality: "24-bit / 44.1 kHz",
  provider,
  lastAccessedAt: "2026-07-27T00:00:00Z",
  publicationState: "Indexed",
  explicitlyKept: false,
  removable: true,
});

describe("managed download presentation", () => {
  it("filters arbitrary providers and sorts numeric size", () => {
    expect(filterDownloads([
      file("Small", "future-extension", 1),
      file("Large", "future-extension", 10),
      file("Elsewhere", "other-extension", 20),
    ], "", "future-extension", "size").map((item) => item.title))
      .toEqual(["Large", "Small"]);
  });

  it("formats complete audio quality facts", () => {
    expect(qualityDetails(file("Track", "future-extension", 1)))
      .toEqual(["FLAC", "900 kbps", "24-bit", "44.1 kHz", "2 ch"]);
  });

  it("keeps measured quality warnings distinct from the claimed format", () => {
    const copy = file("Track", "future-extension", 1);
    const check = { claimedSampleRate: 96000, claimedBitDepth: 24, effectiveBandwidthHz: 20000, confidence: 0.8 };
    expect(qualityWarning({ ...copy, qualityCheck: { ...check, verdict: "upsampled", likelySourceRate: 48000 } }))
      .toEqual({ label: "Likely upscaled", detail: "The measured bandwidth suggests a 48 kHz source in this higher-rate file." });
    expect(qualityWarning({ ...copy, qualityCheck: { ...check, verdict: "padded-bit-depth" } })?.label).toBe("Padded");
    expect(qualityWarning({ ...copy, qualityCheck: { ...check, verdict: "lossy-transcode-suspected" } })?.label).toBe("Lossy source?");
    expect(qualityWarning({ ...copy, qualityCheck: { ...check, verdict: "true-hi-res" } })).toBeNull();
    expect(qualityWarning(copy)).toBeNull();
  });
});


describe("cached song copies", () => {
  it("groups matching recordings across services and album releases", () => {
    const original = { ...file("Same song", "deezer", 1), durationMilliseconds: 180000, externalId: "one" };
    const other = { ...original, path: "other.flac", provider: "apple", externalId: "two", album: "Single", durationMilliseconds: 181000 };
    expect(groupDownloadSongs([original, other])[0].files).toHaveLength(2);
  });
  it("keeps different versions and uncertain recordings separate", () => {
    const original = { ...file("Same song", "deezer", 1), durationMilliseconds: 180000 };
    const live = { ...original, path: "live.flac", title: "Same song (Live)" };
    const longer = { ...original, path: "long.flac", durationMilliseconds: 210000 };
    expect(groupDownloadSongs([original, live, longer])).toHaveLength(3);
    expect(groupDownloadSongs([original, { ...original, path: "unknown.flac", album: "Single", durationMilliseconds: null }])).toHaveLength(2);
  });
});
