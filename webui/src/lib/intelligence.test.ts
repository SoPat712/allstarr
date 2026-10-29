import { describe, expect, it } from "vitest";
import { formatCount, listeningTime, releaseDate, groupByWeek, playlistSourceKey, brainzDetails, brainzPlaylists } from "./intelligence";

describe("listening presentation", () => {
  it("reads generated playlist metadata and keeps the newest edition of each kind", () => {
    expect(brainzDetails("Creator: Listener\nCreated: 2026-10-26T12:00:00Z\nAlgorithm: weekly-jams\nSome notes")).toEqual({ kind: "weekly-jams", created: "2026-10-26T12:00:00Z" });
    expect(brainzDetails("Algorithm: invalid kind\nCreated: nonsense")).toEqual({ kind: "", created: null });
    const item = (id: string, description: string) => ({ id, name: id, providerId: "listenbrainz", description });
    expect(brainzPlaylists([item("own", ""), item("old", "Algorithm: weekly-jams\nCreated: 2026-10-19T12:00:00Z"), item("new", "Algorithm: weekly-jams\nCreated: 2026-10-26T12:00:00Z")]).map(value => value.id)).toEqual(["new", "own"]);
    expect(brainzPlaylists([item("earlier", "Algorithm: weekly-jams\nCreated: 2026-10-26T06:00:00Z"), item("later", "Algorithm: weekly-jams\nCreated: 2026-10-26T00:00:00-07:00")])[0].id).toBe("later");
  });
  it("recognizes the default Last.fm count while keeping other selections distinct", () => {
    expect(playlistSourceKey("lastfm", "top:1month")).toBe(playlistSourceKey("lastfm", "top:1month:50"));
    expect(playlistSourceKey("lastfm", "top:1month:25")).not.toBe(playlistSourceKey("lastfm", "top:1month:50"));
    expect(playlistSourceKey("listenbrainz", "top:1month")).toBe("top:1month");
  });
  it("formats counts and estimates from full song lengths", () => {
    expect(formatCount(12345)).toBe("12,345");
    expect(formatCount(0)).toBe("0");
    expect(listeningTime(0)).toBe("0 min");
    expect(listeningTime(30000)).toBe("1 min");
    expect(listeningTime(3600000 + 120000)).toBe("1 hr 2 min");
    expect(listeningTime(25 * 3600000)).toBe("25 hr");
  });

  it("groups by Monday boundaries without treating partial dates as exact days", () => {
    const entries = [
      { date: "2026-10-26", name: "Monday" },
      { date: "2026-10-25", name: "Sunday" },
      { date: "2026-10-19", name: "Last Monday" },
      { date: "2026-10-18", name: "Older" },
      { date: "2026-10", name: "Month only" },
      { date: null, name: "Unknown" },
    ];
    const groups = groupByWeek(entries, item => item.date, new Date(2026, 9, 28, 12));
    expect(groups.map(group => [group.label, group.items.map(item => item.name)])).toEqual([
      ["This week", ["Monday"]],
      ["Last week", ["Sunday", "Last Monday"]],
      ["Earlier", ["Older", "Month only", "Unknown"]],
    ]);
    expect(groupByWeek([], item => String(item))).toEqual([]);
  });

  it("keeps the precision of release dates", () => {
    expect(releaseDate("2026")).toBe("2026");
    expect(releaseDate("2026-10")).toBe("October 2026");
    expect(releaseDate("2026-10-28")).toBe("Oct 28, 2026");
    expect(releaseDate(null)).toBe("Date unavailable");
    expect(releaseDate("2026-02-31")).toBe("Date unavailable");
  });
});
