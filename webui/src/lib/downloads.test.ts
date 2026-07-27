import { describe, expect, it } from "vitest";
import type { ManagedDownload } from "./api";
import { filterDownloads, qualityDetails } from "./downloads";

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
});
