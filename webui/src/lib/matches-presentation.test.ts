import { describe, expect, it } from "vitest";
import { comparisonRows, currentSong, defaultMatchFilter, differenceChips, evidenceSentence, filterCounts, formatConfidence, inFilter, keyboardCommand, matchPill, matchesSearch, summarizeMatches, sortMatches, versionWords } from "./matches-presentation";
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
    expect(differenceChips(song, {})).toEqual([]);
    expect(differenceChips(song, { ...song, durationMilliseconds: 180999, isrc: null })).toEqual([]);
    expect(differenceChips(song, { ...song, durationMilliseconds: 178500 })).toEqual(["-2 s"]);
  });
  it("does not infer differences from missing recording details", () => {
    expect(differenceChips({ ...song, title: "Song (Live)" }, {})).toEqual([]);
    expect(differenceChips(song, { album: "Another album" })).toEqual(["Different album"]);
  });
  it("compares available fields while leaving missing fields uncomparable", () => {
    const rows = comparisonRows(song, { title: null, artist: song.artist, album: "Another album" });
    expect(rows.map(row => [row.label, row.verdict])).toEqual([
      ["Title", "Can't compare"], ["Artist", "Same"], ["Album", "Different"],
      ["Length", "Can't compare"], ["ISRC", "Can't compare"], ["Version", "Can't compare"],
    ]);
    expect(comparisonRows(song, { title: "  ", artist: "  " }).every(row => row.verdict === "Can't compare")).toBe(true);
  });
  it("calls title extras close and shows the actual length difference", () => {
    const rows = comparisonRows(song, { ...song, title: "Song (Live)", durationMilliseconds: 218000 });
    expect(rows.find(row => row.label === "Title")?.verdict).toBe("Close");
    expect(rows.find(row => row.label === "Length")?.verdict).toBe("+38 s");
  });
  it("keeps an identified target with missing metadata available for comparison", () => {
    const target = currentSong({ ...match("accepted"), target: { providerId: "qobuz", externalId: "saved", title: null } });
    expect(target).toMatchObject({ providerId: "qobuz", identity: "saved", title: null });
    expect(differenceChips(song, target)).toEqual([]);
    expect(comparisonRows(song, target).every(row => row.verdict === "Can't compare")).toBe(true);
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

describe("match confidence presentation", () => {
  it.each([[.967, "97%"], [.9545, "95%"], [.992, "99%"], [0, "0%"], [1, "100%"], [null, "—"], [undefined, "—"], [NaN, "—"], [Infinity, "—"]])("formats %s as %s", (value, label) => {
    expect(formatConfidence(value)).toBe(label);
  });
  it.each(["ambiguous", "suggested"])("keeps %s warning above the acceptance threshold", (state) => {
    expect(matchPill({ ...match(state), confidence: .967, threshold: .9 })).toEqual({ text: "97%", tone: "warn" });
  });
  it("uses success only for accepted automatic matches meeting the threshold", () => {
    expect(matchPill({ ...match("accepted"), confidence: .967, threshold: .9 })).toEqual({ text: "97%", tone: "ok" });
    expect(matchPill({ ...match("accepted"), confidence: .899, threshold: .9 })).toEqual({ text: "90%", tone: "warn" });
    expect(matchPill({ ...match("accepted"), confidence: .967, threshold: null }).tone).toBe("warn");
  });
  it("keeps hand-picked, rejected and unmatched decisions distinct from confidence", () => {
    expect(matchPill({ ...match("pinned"), confidence: 1 })).toEqual({ text: "By you", tone: "neutral" });
    const manual = { effective: true, kind: "provider_match" } as NonNullable<MatchReviewItem["manualAuthorities"]>[number];
    expect(matchPill({ ...match("accepted"), confidence: 1, threshold: .9, manualAuthorities: [manual] })).toEqual({ text: "By you", tone: "neutral" });
    expect(matchPill({ ...match("rejected"), confidence: 1 })).toEqual({ text: "Rejected", tone: "stop" });
    expect(matchPill({ ...match("unresolved"), confidence: .967, threshold: .9 })).toEqual({ text: "No match", tone: "stop" });
    expect(matchPill({ ...match("accepted"), confidence: null })).toEqual({ text: "No match", tone: "stop" });
  });
});

describe("plain language evidence", () => {
  it.each([["title_exact", "Title matches exactly"], ["duration_close", "Length within 1 second"], ["title", "Title"], ["artist", "Artist"], ["primaryArtist", "Main artist"], ["versionTags", "Version"], ["titleAnnotations", "Title extras"], ["album", "Album"], ["duration", "Length"], ["artwork", "Cover"]])("explains %s", (key, label) => {
    expect(evidenceSentence(key)).toBe(label);
  });
  it.each(["ArtistGrid best version +5", "MusicBrainz identity"])('preserves readable evidence "%s"', (reason) => {
    expect(evidenceSentence(reason)).toBe(reason);
  });
  it.each(["title_strong", "title_annotation_mismatch", "album_artist_partial", "below_accept_threshold_review", "unknown_reason", "providerTrackId", "artistGrid_bestVersion"])("never exposes a machine key: %s", key => {
    expect(evidenceSentence(key)).not.toBe(key);
    expect(evidenceSentence(key)).not.toContain("_");
  });
  it("uses a neutral explanation for unfamiliar evidence", () => {
    expect(evidenceSentence("new_reason")).toBe("Compare the recording details before choosing");
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
