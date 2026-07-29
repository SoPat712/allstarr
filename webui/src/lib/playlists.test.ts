import { describe, expect, it } from "vitest";
import type { PlaylistLink, PlaylistSourceAccount, PlaylistTrack } from "./api";
import {
  filterPlaylists,
  filterTracks,
  formatDuration,
  orderPlaylistSources,
  providerColor,
  resizedColumnWidth,
  runBounded,
} from "./playlists";

const playlist = (values: Partial<PlaylistLink>): PlaylistLink => ({
  id: "one",
  enabled: true,
  name: "One",
  sourceProviderId: "source",
  targetProtocol: "target",
  materializationMode: "reconcile",
  revision: 1,
  trackCount: 10,
  matchedCount: 9,
  unmatchedCount: 1,
  playableCount: 9,
  materializedCount: 8,
  routeCoverage: [{ providerId: "target", count: 9 }, { providerId: "unresolved", count: 1 }],
  metrics: { total: 10, matched: 9, unresolved: 1, review: 0, rejected: 0, playable: 9, materialized: 8 },
  ...values,
});

describe("playlist presentation", () => {
  it("uses provider colors and a neutral unresolved segment", () => {
    expect(providerColor("unresolved")).toBe("var(--color-ink-muted)");
    expect(providerColor("unmatched")).toBe("var(--color-ink-muted)");
    expect(providerColor("jellyfin")).toBe("#8b65fb");
    expect(providerColor("apple-download")).toBe("#fa243c");
  });

  it("keeps resized columns within their usable bounds", () => {
    expect(resizedColumnWidth(300, -200, 220, 520)).toBe(220);
    expect(resizedColumnWidth(300, 400, 220, 520)).toBe(520);
    expect(resizedColumnWidth(300, 40, 220, 520)).toBe(340);
  });

  it("filters and orders summaries by canonical coverage", () => {
    const result = filterPlaylists(
      [
        playlist({ id: "partial", name: "Partial", playableCount: 8, unmatchedCount: 2 }),
        playlist({ id: "ready", name: "Ready", playableCount: 10, unmatchedCount: 0 }),
        playlist({ id: "paused", name: "Paused", enabled: false, playableCount: 2 }),
      ],
      "",
      "all",
      "coverage",
    );
    expect(result.map((item) => item.id)).toEqual(["ready", "partial", "paused"]);
    expect(filterPlaylists(result, "source", "attention", "name").map((item) => item.id)).toEqual([
      "partial",
    ]);
  });

  it("keeps source order by default and formats durations", () => {
    const tracks = [
      { sourcePosition: 1, position: 2, externalSnapshotId: "b", title: "B", artists: [], routeKind: "external", providerRoutes: [] },
      { sourcePosition: 0, position: 1, externalSnapshotId: "a", title: "A", artists: [], routeKind: "local", providerRoutes: [] },
    ] satisfies PlaylistTrack[];
    expect(filterTracks(tracks, "", "all", "position").map((item) => item.position)).toEqual([1, 2]);
    expect(formatDuration(3_723_000)).toBe("1:02:03");
    expect(formatDuration(null)).toBe("—");
    expect(providerColor("any-extension")).toMatch(/^hsl\(\d+ 72% 58%\)$/);
  });

  it("orders local targets, Spotify, then configured playlist Sources", () => {
    const source = (providerId: string): PlaylistSourceAccount => ({
      id: providerId, providerId, displayName: providerId, accessLabel: "Personal account",
    });
    expect(orderPlaylistSources(
      ["qobuz", "spotify", "extension", "subsonic", "jellyfin"].map(source),
      ["extension", "qobuz", "spotify"],
    ).map((item) => item.providerId)).toEqual([
      "jellyfin", "subsonic", "spotify", "extension", "qobuz",
    ]);
  });

  it("bounds bulk work and isolates failures", async () => {
    let active = 0;
    let peak = 0;
    const progress: number[] = [];
    const results = await runBounded(
      [1, 2, 3, 4, 5],
      2,
      async (value) => {
        active++;
        peak = Math.max(peak, active);
        await Promise.resolve();
        active--;
        if (value === 3) throw new Error("fixture failure");
      },
      (completed) => progress.push(completed),
    );

    expect(peak).toBe(2);
    expect(results.map((result) => result.status)).toEqual([
      "fulfilled", "fulfilled", "rejected", "fulfilled", "fulfilled",
    ]);
    expect(progress).toEqual([1, 2, 3, 4, 5]);
  });
});
