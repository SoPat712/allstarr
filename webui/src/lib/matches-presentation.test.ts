import { describe, expect, it } from "vitest";
import { comparisonRows, defaultMatchFilter, differenceChips, evidenceSentence, filterCounts, inFilter, keyboardCommand, matchesSearch, summarizeMatches, sortMatches, versionWords } from "./matches-presentation";
import type { MatchReviewItem } from "./api";

const song = { title: "Song", artist: "Singer", album: "Album", durationMilliseconds: 180000, isrc: "AB123", isExplicit: true };
const match = (state: string) => ({ ...song, state, externalSnapshotId: state, candidates: [], manualAuthorities: [] }) as unknown as MatchReviewItem;

describe("match differences", () => {
  it("leaves differences empty when there is no recording to compare", () => {
    expect(differenceChips(song, null)).toEqual([]);
  });
  it("shows only changed versions, album edition, length, content and identity", () => {
    expect(differenceChips(song, { ...song, title: "Song (Live Remix)", album: "Album Deluxe", durationMilliseconds: 182000, isExplicit: false, isrc: "CD456" }))
      .toEqual(["Live", "Remix", "Deluxe album", "+2 s", "Clean instead of explicit", "Different ISRC"]);
    expect(differenceChips(song, song)).toEqual([]);
    expect(differenceChips(song, {})).toEqual(["Details unavailable"]);
    expect(differenceChips(song, { ...song, durationMilliseconds: 180999, isrc: null })).toEqual([]);
    expect(differenceChips(song, { ...song, durationMilliseconds: 178500 })).toEqual(["-2 s"]);
  });
  it("recognizes every supported version and notices versions removed from a title", () => {
    expect(versionWords("Live Remix Acoustic Instrumental Sped up Slowed Single Version Radio Edit Remastered Taylor’s Version"))
      .toHaveLength(10);
    expect(differenceChips({ ...song, title: "Song (Acoustic)" }, song)).toContain("Not Acoustic");
  });
  it("never calls missing metadata the same and uses a close length verdict", () => {
    expect(comparisonRows(song, null).every(row => row.verdict === "Can't compare")).toBe(true);
    expect(comparisonRows(song, { ...song, durationMilliseconds: 180500 }).find(row => row.label === "Length")?.verdict).toBe("Close");
  });
});

describe("plain language evidence", () => {
  it.each([["title_exact", "Title matches exactly"], ["duration_close", "Length within 1 second"], ["title", "Title"], ["artist", "Artist"], ["primaryArtist", "Main artist"], ["versionTags", "Version"], ["titleAnnotations", "Title extras"], ["album", "Album"], ["duration", "Length"], ["artwork", "Cover"]])("explains %s", (key, label) => {
    expect(evidenceSentence(key)).toBe(label);
  });
  it.each(["ArtistGrid best version +5", "MusicBrainz identity", "providerTrackId", "unknown_reason", "artistGrid_bestVersion"])('preserves "%s" without changing names or unknown tokens', (reason) => {
    expect(evidenceSentence(reason)).toBe(reason);
  });
});

describe("match filters and ordering", () => {
  it("combines review, unsure and no match without rejections", () => {
    const stats = { total: 12, review: 3, suggested: 2, ambiguous: 1, unresolved: 4, accepted: 4, manualMatches: 1, manualRejections: 1 } as Parameters<typeof filterCounts>[0];
    expect(filterCounts(stats)).toMatchObject({ needs: 7, automatic: 3, manual: 1, rejected: 1, all: 12 });
    for (const state of ["ambiguous", "suggested", "unresolved"]) expect(inFilter(match(state), "needs")).toBe(true);
    expect(inFilter(match("rejected"), "needs")).toBe(false);
  });
  it("defaults to work needing attention, otherwise All, and honors saved choices", () => {
    const stats = { total: 2, review: 0, suggested: 0, unresolved: 0 } as Parameters<typeof filterCounts>[0];
    expect(defaultMatchFilter(stats)).toBe("all");
    expect(defaultMatchFilter({ ...stats, unresolved: 1 })).toBe("needs");
    expect(defaultMatchFilter({ ...stats, unresolved: 1 }, "automatic")).toBe("automatic");
    expect(defaultMatchFilter(stats, "stale")).toBe("all");
    expect(defaultMatchFilter(stats, "manual", "Song")).toBe("all");
  });
  it("searches playlist names as well as song metadata and counts the results", () => {
    expect(matchesSearch(match("accepted"), "road", ["Road trip"])).toBe(true);
    expect(matchesSearch(match("accepted"), "singer")).toBe(true);
    expect(matchesSearch(match("accepted"), "missing")).toBe(false);
    expect(summarizeMatches([match("accepted"), match("ambiguous"), match("unresolved")])).toMatchObject({ total: 3, automatic: 1, review: 1, unresolved: 1 });
  });
  it("sorts all loaded rows before pagination", () => {
    const rows = [{ ...match("accepted"), title: "Z", confidence: .99 }, { ...match("unresolved"), title: "A", confidence: .1 }];
    expect(sortMatches(rows, "confidence_asc")[0].title).toBe("A");
    expect(sortMatches(rows, "title")[0].title).toBe("A");
    expect(sortMatches(rows, "playlist", { accepted: ["First"], unresolved: ["Second"] })[0].title).toBe("Z");
  });
});

describe("Matches keyboard commands", () => {
  it("ignores typing, modifiers and already handled events", () => {
    expect(keyboardCommand({ key: "a", target: { tagName: "INPUT" } })).toBeNull();
    expect(keyboardCommand({ key: "r", target: { isContentEditable: true } })).toBeNull();
    expect(keyboardCommand({ key: "r", ctrlKey: true })).toBeNull();
    expect(keyboardCommand({ key: "ArrowDown", defaultPrevented: true })).toBeNull();
  });
  it.each(['[role="textbox"]', '[role="searchbox"]', '[role="listbox"]', '[role="slider"]', '[contenteditable]:not([contenteditable="false"])'])("leaves shortcuts inside %s alone", (editable) => {
    const target = { closest: (selector: string) => selector.includes(editable) };
    expect(keyboardCommand({ key: "/", target })).toBeNull();
    expect(keyboardCommand({ key: "ArrowDown", target })).toBeNull();
  });
  it("allows search from filter radios while preserving their arrow navigation", () => {
    const target = { closest: (selector: string) => selector.includes('[role="radiogroup"]') };
    expect(keyboardCommand({ key: "ArrowDown", target })).toBeNull();
    expect(keyboardCommand({ key: "/", target })).toBe("search");
  });
  it.each([["a", "accept"], ["R", "rematch"], ["/", "search"], ["?", "help"], ["ArrowUp", "previous"], ["ArrowDown", "next"], ["Enter", "open"], ["Escape", "close"]])("maps %s", (key, action) => {
    expect(keyboardCommand({ key })).toBe(action);
  });
});
