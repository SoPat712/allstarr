import { describe, expect, it } from "vitest";
import type { ManagedDownload } from "./api";
import { filterDownloads, groupDownloadSongs, qualityDetails } from "./downloads";

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
