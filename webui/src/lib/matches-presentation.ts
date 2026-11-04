import type { MatchCandidate, MatchReviewItem, MatchReviewResponse, MatchTarget } from "./api";
import { currentTarget } from "./mappings";
import { formatDuration } from "./playlists";

export type MatchFilter = "needs" | "automatic" | "manual" | "rejected" | "all";
export const filterStates: Record<MatchFilter, string[]> = {
  needs: ["attention", "unresolved"], automatic: ["automatic"], manual: ["manual_matches"], rejected: ["manual_rejections"], all: [""],
};
export type SongFacts = {
  title?: string | null; artist?: string | null; album?: string | null;
  durationMilliseconds?: number | null; isrc?: string | null; isExplicit?: boolean | null;
};
const normalized = (value?: string | null) => value?.trim().toLocaleLowerCase().replaceAll("’", "'") ?? "";
const versions: [string, RegExp][] = [
  ["Live", /\blive\b/i], ["Remix", /\bremix(?:ed)?\b/i], ["Acoustic", /\bacoustic\b/i],
  ["Instrumental", /\binstrumental\b/i], ["Sped up", /\bsped[ -]?up\b/i], ["Slowed", /\bslowed\b/i],
  ["Single Version", /\bsingle version\b/i], ["Radio Edit", /\bradio edit\b/i],
  ["Remastered", /\bremaster(?:ed)?\b/i], ["Taylor's Version", /\btaylor['’]s version\b/i],
];
export const versionWords = (title?: string | null) => versions.filter(([, pattern]) => pattern.test(title ?? "")).map(([label]) => label);

export function differenceChips(source: SongFacts, target: SongFacts | null): string[] {
  if (!target) return [];
  const from = versionWords(source.title), to = versionWords(target.title);
  const chips = [...to.filter(word => !from.includes(word)), ...from.filter(word => !to.includes(word)).map(word => `Not ${word}`)];
  if (source.album && target.album && normalized(source.album) !== normalized(target.album))
    chips.push(/deluxe/i.test(target.album) ? "Deluxe album" : "Different album");
  if (source.durationMilliseconds != null && target.durationMilliseconds != null) {
    const delta = target.durationMilliseconds - source.durationMilliseconds;
    if (Math.abs(delta) >= 1000) chips.push(`${delta > 0 ? "+" : "-"}${Math.round(Math.abs(delta) / 1000)} s`);
  }
  if (source.isExplicit != null && target.isExplicit != null && source.isExplicit !== target.isExplicit)
    chips.push(target.isExplicit ? "Explicit instead of clean" : "Clean instead of explicit");
  if (source.isrc && target.isrc && normalized(source.isrc) !== normalized(target.isrc)) chips.push("Different ISRC");
  if (source.title && target.title && normalized(source.title) !== normalized(target.title) && !chips.length) chips.push("Different title");
  if (source.artist && target.artist && normalized(source.artist) !== normalized(target.artist)) chips.push("Different artist");
  if (!chips.length && (!target.title || !target.artist)) chips.push("Details unavailable");
  return chips;
}

export function comparisonRows(source: SongFacts, target: SongFacts | null) {
  const fields = ["title", "artist", "album", "durationMilliseconds", "isrc", "version"] as const;
  const labels = ["Title", "Artist", "Album", "Length", "ISRC", "Version"];
  return fields.map((field, index) => {
    const value = (song: SongFacts | null) => !song ? null : field === "version"
      ? (song.title ? versionWords(song.title).join(", ") || "Original" : null) : song[field];
    const left = value(source), right = value(target);
    let verdict = "Can't compare";
    if (left != null && left !== "" && right != null && right !== "") {
      verdict = normalized(String(left)) === normalized(String(right)) ? "Same" : "Different";
      if (field === "durationMilliseconds" && left !== right && Math.abs(Number(left) - Number(right)) <= 1000) verdict = "Close";
    }
    const display = (input: typeof left) => input == null || input === "" ? "Not provided"
      : field === "durationMilliseconds" ? formatDuration(Number(input)) : String(input);
    return { label: labels[index], original: display(left), current: display(right), verdict };
  });
}

const evidence: Record<string, string> = {
  title: "Title", artist: "Artist", primaryArtist: "Main artist", versionTags: "Version", titleAnnotations: "Title extras", album: "Album", duration: "Length", artwork: "Cover",
  title_exact: "Title matches exactly", title_match: "Title matches", title_case_folded: "Title matches ignoring capitals",
  title_normalized: "Title matches after punctuation is removed", title_decorators_ignored: "Title matches without release labels", title_fuzzy: "Title is similar",
  artist_exact: "Artist matches exactly", artist_match: "Artist matches", album_exact: "Album matches exactly", album_match: "Album matches",
  duration_close: "Length within 1 second", duration_exact: "Length matches exactly", isrc_exact: "ISRC matches exactly", isrc_match: "ISRC matches",
  semantic_version_mismatch: "Different recording version", semantic_version_mismatch_review: "Different version needs a look",
  limited_recording_metadata: "Some recording details are missing", clean_edit_of_explicit: "Clean version of an explicit song",
};
export function evidenceSentence(key: string) {
  return Object.hasOwn(evidence, key) ? evidence[key] : key;
}
export const candidateReason = (candidate: Pick<MatchCandidate, "reasons" | "warnings">) =>
  [...candidate.warnings ?? [], ...candidate.reasons ?? []].slice(0, 2).map(evidenceSentence).join(" · ") || "Compare the recording details before choosing";

export function filterCounts(stats: MatchReviewResponse["stats"]) {
  return { needs: (stats.ambiguous ?? Math.max(0, stats.review - stats.suggested)) + stats.suggested + stats.unresolved,
    automatic: stats.automatic ?? Math.max(0, stats.accepted - (stats.manualMatches ?? 0)),
    manual: stats.manualMatches ?? 0, rejected: stats.manualRejections ?? stats.rejected, all: stats.total };
}
export function defaultMatchFilter(stats: MatchReviewResponse["stats"], saved = "", search = ""): MatchFilter {
  if (search) return "all";
  if (Object.hasOwn(filterStates, saved)) return saved as MatchFilter;
  return filterCounts(stats).needs > 0 ? "needs" : "all";
}
export function inFilter(match: MatchReviewItem, filter: MatchFilter) {
  const hand = match.manualAuthorities?.some(item => item.kind !== "rejection");
  if (filter === "needs") return ["ambiguous", "suggested", "unresolved"].includes(match.state);
  if (filter === "manual") return Boolean(hand);
  if (filter === "rejected") return match.state === "rejected" || Boolean(match.manualAuthorities?.some(item => item.kind === "rejection"));
  if (filter === "automatic") return match.state === "accepted" && !hand && !match.manualAuthorities?.some(item => item.kind === "rejection");
  return true;
}
export function matchesSearch(match: MatchReviewItem, search: string, playlists: string[] = []) {
  const term = normalized(search);
  return [match.title, match.artist, match.album, match.providerId, ...playlists].some(value => normalized(value).includes(term));
}
export function summarizeMatches(matches: MatchReviewItem[]): MatchReviewResponse["stats"] {
  const count = (test: (item: MatchReviewItem) => boolean) => matches.filter(test).length;
  return { total: matches.length, matched: count(item => ["accepted", "pinned"].includes(item.state)),
    accepted: count(item => ["accepted", "pinned"].includes(item.state)), unresolved: count(item => item.state === "unresolved"),
    suggested: count(item => item.state === "suggested"), review: count(item => ["ambiguous", "suggested"].includes(item.state)),
    ambiguous: count(item => item.state === "ambiguous"), rejected: count(item => item.state === "rejected"),
    attention: count(item => ["ambiguous", "suggested"].includes(item.state)),
    automatic: count(item => inFilter(item, "automatic")), manualMatches: count(item => inFilter(item, "manual")), manualRejections: count(item => inFilter(item, "rejected")) };
}
export function sortMatches(matches: MatchReviewItem[], sort: string, playlists: Record<string, string[]> = {}) {
  return matches.toSorted((a, b) => sort === "playlist" ? (playlists[a.externalSnapshotId]?.join(", ") || "\uffff").localeCompare(playlists[b.externalSnapshotId]?.join(", ") || "\uffff") || (a.title ?? "").localeCompare(b.title ?? "")
    : sort === "title" ? (a.title ?? "").localeCompare(b.title ?? "")
    : sort === "newest" ? (b.decidedAt ?? "").localeCompare(a.decidedAt ?? "")
    : sort === "confidence_desc" ? (b.confidence ?? -1) - (a.confidence ?? -1)
    : (a.confidence ?? -1) - (b.confidence ?? -1));
}
export function currentSong(match: MatchReviewItem): (SongFacts & { providerId: string; identity: string; artworkUrl?: string | null }) | null {
  if (match.target) return { ...match.target, identity: match.target.externalId };
  if (match.localTrack) return { ...match.localTrack, providerId: "local", identity: match.localTrack.id,
    isrc: match.localTrack.providerIds?.Isrc || match.localTrack.providerIds?.isrc };
  const target = currentTarget(match);
  return target ? { ...target, title: target.title === "Metadata unavailable" ? null : target.title,
    artist: target.artist === "Unknown artist" ? null : target.artist } : null;
}
export function targetSong(target: MatchTarget) { return { ...target, providerId: target.externalProvider || "local", identity: target.externalId || target.id }; }

type KeyEvent = { key: string; ctrlKey?: boolean; metaKey?: boolean; altKey?: boolean; defaultPrevented?: boolean; target?: { tagName?: string; isContentEditable?: boolean; closest?: (selector: string) => unknown } | null };
export function keyboardCommand(event: KeyEvent) {
  if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey || event.target?.isContentEditable ||
    /^(INPUT|TEXTAREA|SELECT)$/.test(event.target?.tagName ?? "") || event.target?.closest?.('[contenteditable]:not([contenteditable="false"]), [role="textbox"], [role="searchbox"], [role="combobox"], [role="listbox"], [role="menu"], [role="slider"]') ||
    event.key !== "/" && event.target?.closest?.('[role="radiogroup"]')) return null;
  return ({ ArrowUp: "previous", ArrowDown: "next", Enter: "open", a: "accept", r: "rematch", "/": "search", Escape: "close", "?": "help" } as Record<string, string>)[event.key.length === 1 ? event.key.toLowerCase() : event.key] ?? null;
}
