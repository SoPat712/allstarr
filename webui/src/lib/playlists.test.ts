import { describe, expect, it } from "vitest";
import type { MixCard, PlaylistLink, PlaylistSourceAccount, PlaylistTrack } from "./api";
import {
  filterPlaylists,
  filterTracks,
  formatDuration,
  isReviewTrack,
  isServiceMix,
  orderPlaylistSources,
  playlistBehaviorSummary,
  playlistOutcomeLabel,
  providerColor,
  runBounded,
  mediaServerName,
  scheduleCadence,
  scheduleZoneLabel,
} from "./playlists";

const playlist = (values: Partial<PlaylistLink>): PlaylistLink => ({
  id: "one",
  enabled: true,
  name: "One",
  sourceProviderId: "source",
  sourcePlaylistId: "source-playlist",
  providerAccountId: "account",
  targetProtocol: "target",
  targetBackendInstanceId: "backend",
  targetPlaylistId: "target-playlist",
  mode: "hybrid",
  projectionMode: "resolved",
  materializationMode: "reconcile",
  importMode: "linked",
  trackRetention: "onDemand",
  mirrorStaleEntries: true,
  preserveManualEntries: true,
  syncName: true,
  syncDescription: true,
  syncArtwork: true,
  ruleVersion: "rules-v1",
  policyVersion: "policy-v1",
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
  it("distinguishes streamed, local, duplicate, and unresolved outcomes in plain words", () => {
    expect(playlistOutcomeLabel("streamed_through_allstarr")).toBe("Streams through Allstarr");
    expect(playlistOutcomeLabel("included_native_backend_item", "Jellyfin playlist"))
      .toBe("Will be included in Jellyfin playlist");
    expect(playlistOutcomeLabel("skipped_duplicate", "Jellyfin playlist"))
      .toBe("Duplicate: not added twice to Jellyfin playlist");
    expect(playlistOutcomeLabel("skipped_unresolved", "Jellyfin playlist"))
      .toBe("Not added to Jellyfin playlist: no playable match yet");
  });

  it("uses provider colors and a neutral unresolved segment", () => {
    expect(providerColor("unresolved")).toBe("var(--color-ink-muted)");
    expect(providerColor("unmatched")).toBe("var(--color-ink-muted)");
    expect(providerColor("jellyfin")).toBe("var(--color-signal)");
    expect(providerColor("apple-download")).toBe("#fa243c");
    expect(providerColor("deezer")).toBe("#ff0092");
  });

  it("filters and orders summaries by confirmed coverage", () => {
    const result = filterPlaylists(
      [
        playlist({ id: "partial", name: "Partial", matchedCount: 8, unmatchedCount: 2 }),
        playlist({ id: "ready", name: "Ready", matchedCount: 10, unmatchedCount: 0 }),
        playlist({ id: "paused", name: "Paused", enabled: false, matchedCount: 2 }),
        playlist({
          id: "review",
          name: "Review",
          matchedCount: 9,
          unmatchedCount: 0,
          metrics: {
            total: 10,
            matched: 9,
            unresolved: 0,
            review: 1,
            rejected: 0,
            playable: 10,
            materialized: 0,
          },
        }),
      ],
      "",
      "all",
      "coverage",
    );
    expect(result.map((item) => item.id)).toEqual(["ready", "review", "partial", "paused"]);
    expect(filterPlaylists(result, "source", "attention", "name").map((item) => item.id)).toEqual([
      "partial", "review",
    ]);
  });

  it("includes app mixes in name filtering and sorting only for all states", () => {
    const mix: MixCard & { trackCount: number | null } = { id: "mix", kind: "daily-1", name: "Blend 1", description: "Songs picked for you", section: "madeForYou", sortOrder: 0, sourceLabel: null, recipeVersion: "1", updatedAt: "2026-10-29T04:00:00Z", readiness: { ready: true, message: "Ready" }, inApps: true, appsAllowed: true, coverArtwork: [], trackCount: 20 };
    const items = [playlist({ name: "Evening" }), mix];
    expect(filterPlaylists(items, "", "all", "name").map(item => item.id)).toEqual(["mix", "one"]);
    expect(filterPlaylists(items, "BLEND", "all", "name").map(item => item.id)).toEqual(["mix"]);
    for (const state of ["ready", "attention", "paused"] as const)
      expect(filterPlaylists(items, "blend", state, "name")).toEqual([]);
    expect(filterPlaylists(items, "", "all", "tracks").map(item => item.id)).toEqual(["mix", "one"]);
    expect(filterPlaylists(items, "", "all", "coverage").map(item => item.id)).toEqual(["one", "mix"]);
    expect(filterPlaylists([playlist({ name: "Collection 24" }), playlist({ name: "Collection 9" })], "", "all", "name").map(item => item.name)).toEqual(["Collection 24", "Collection 9"]);
  });

  it("keeps source order by default and formats durations", () => {
    const tracks = [
      { sourcePosition: 1, position: 2, externalSnapshotId: "b", title: "B", artists: [], routeKind: "external", matchState: "suggested", providerRoutes: [] },
      { sourcePosition: 0, position: 1, externalSnapshotId: "a", title: "A", artists: [], routeKind: "local", providerRoutes: [] },
    ] satisfies PlaylistTrack[];
    expect(filterTracks(tracks, "", "all", "position").map((item) => item.position)).toEqual([1, 2]);
    expect(filterTracks(tracks, "", "review", "position").map((item) => item.position)).toEqual([2]);
    expect(isReviewTrack(tracks[0])).toBe(true);
    expect(formatDuration(3_723_000)).toBe("1:02:03");
    expect(formatDuration(null)).toBe("—");
    expect(providerColor("any-extension")).toMatch(/^hsl\(\d+ 72% 58%\)$/);
  });

  it("describes common sync schedules and preserves custom cron expressions", () => {
    expect(scheduleCadence("0 * * * *")).toBe("Every hour");
    expect(scheduleCadence("0 3 * * *")).toBe("Every day at 3:00 AM");
    expect(scheduleCadence("0 8 * * 1")).toBe("Every Monday at 8:00 AM");
    expect(scheduleCadence("15 4 * * 2")).toBe("Every Tuesday at 4:15 AM");
    expect(scheduleCadence("30 18 * * 1,3,5")).toBe("Every Monday, Wednesday and Friday at 6:30 PM");
    expect(scheduleCadence("0 7 * * 1-5")).toBe("Weekdays at 7:00 AM");
    expect(scheduleCadence("0 9 * * 0,6")).toBe("Weekends at 9:00 AM");
    expect(scheduleCadence("0 9 * * 7")).toBe("Every Sunday at 9:00 AM");
    expect(scheduleCadence("0 */6 * * *")).toBe("Every 6 hours");
    expect(scheduleCadence("0 4 1 * *")).toBe("Custom schedule (0 4 1 * *)");
    expect(scheduleCadence("not a cron")).toBe("Custom schedule (not a cron)");
  });

  it("names media servers and only labels schedule zones that differ from the viewer's", () => {
    expect(mediaServerName("jellyfin")).toBe("Jellyfin");
    expect(mediaServerName("Navidrome")).toBe("Navidrome");
    expect(mediaServerName("spotify")).toBeNull();
    expect(scheduleZoneLabel(Intl.DateTimeFormat().resolvedOptions().timeZone)).toBe("");
    expect(scheduleZoneLabel(null)).toBe("");
    expect(scheduleZoneLabel("Not/AZone")).toBe("Not/AZone");
  });

  it("keeps fixed hybrid import consequences and eligibility reasons explicit", () => {
    expect(playlistBehaviorSummary(
      "oneTime", "new", "Morning mix", "Jellyfin", "", undefined, "keepAll",
    )).toContain("import Morning mix once");
    expect(playlistBehaviorSummary(
      "oneTime", "new", "Morning mix", "Jellyfin", "", undefined, "keepAll",
    )).toContain("create a new playlist in Jellyfin");
    expect(playlistBehaviorSummary(
      "oneTime", "new", "Morning mix", "Jellyfin", "", undefined, "keepAll",
    )).toContain("keep a copy of every song it can download");
    expect(playlistBehaviorSummary(
      "linked", "existing", "Morning mix", "Jellyfin", "Road trip", "every day at 3:00 AM",
    )).toContain("keep Morning mix in sync every day at 3:00 AM");
    expect(playlistBehaviorSummary(
      "linked", "existing", "Morning mix", "Jellyfin", "Road trip", "every day at 3:00 AM",
    )).toContain("update Road trip in Jellyfin");
    expect(playlistOutcomeLabel("skipped_wrong_backend_or_library", "Jellyfin playlist")).toBe(
      "Not added to Jellyfin playlist: belongs to a different library",
    );
    expect(playlistOutcomeLabel("future_internal_code", "Jellyfin playlist")).not.toContain("future_internal_code");
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

describe("service mix links", () => {
  it("recognizes only supported recipe links", () => {
    expect(isServiceMix(playlist({ sourceProviderId: "lastfm", sourcePlaylistId: "loved" }))).toBe(true);
    expect(isServiceMix(playlist({ sourceProviderId: "lastfm", sourcePlaylistId: "top:1month:50" }))).toBe(true);
    expect(isServiceMix(playlist({ sourceProviderId: "lastfm", sourcePlaylistId: "top:7day:20" }))).toBe(true);
    expect(isServiceMix(playlist({ sourceProviderId: "lastfm", sourcePlaylistId: "loved", importMode: "oneTime" }))).toBe(false);
    expect(isServiceMix(playlist({ sourceProviderId: "listenbrainz", sourcePlaylistId: "createdfor:weekly-jams" }))).toBe(true);
    expect(isServiceMix(playlist({ sourceProviderId: "lastfm", sourcePlaylistId: "other" }))).toBe(false);
    expect(isServiceMix(playlist({ sourceProviderId: "spotify", sourcePlaylistId: "loved" }))).toBe(false);
  });
});
