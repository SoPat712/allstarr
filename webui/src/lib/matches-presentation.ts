import type { MatchCandidate, MatchReviewItem, MatchReviewResponse, MatchTarget } from "./api";
import { currentTarget, isAttention } from "./mappings";
import { formatDuration } from "./playlists";

export type MatchFilter = "needs" | "automatic" | "manual" | "rejected" | "all";
export const filterStates: Record<MatchFilter, string[]> = {
  needs: ["needs"], automatic: ["automatic"], manual: ["manual_matches"], rejected: ["manual_rejections"], all: [""],
};
export function formatConfidence(value?: number | null) {
  return value == null || !Number.isFinite(value) ? "—" : `${Math.round(value * 100)}%`;
}
// The playlist song is the very song that plays, so nothing needed scoring.
const isExact = (match: MatchReviewItem) => match.state === "accepted" && match.confidence == null && match.decisionSource === "canonical_provider_identity";
const providerCover = (url?: string | null) => !!url && /^\/api\/admin\/providers\/[^/]+\/cover$/.test(url);
export const hasOwnArtwork = (match: MatchReviewItem) => !!match.sourceArtworkUrl && !providerCover(match.sourceArtworkUrl);
// A playlist song without its own cover shows the song it plays as rather than the service's logo.
export function sourceArtwork(match: MatchReviewItem) {
  const own = match.sourceArtworkUrl || match.artworkUrl;
  return providerCover(own) ? match.localTrack?.artworkUrl || match.target?.artworkUrl || own : own;
}
export function matchPill(match: MatchReviewItem): { text: string; tone: "ok" | "warn" | "stop" | "neutral" } {
  if (match.state === "rejected") return { text: "Rejected", tone: "stop" };
  if (match.manualAuthorities?.some(item => item.effective && item.kind !== "rejection") || match.state === "pinned") return { text: "By you", tone: "neutral" };
  if (isExact(match)) return { text: "Exact", tone: "ok" };
  if (match.state === "unresolved" || match.confidence == null) return { text: "No match", tone: "stop" };
  return { text: formatConfidence(match.confidence), tone: match.state === "accepted" && match.threshold != null && match.confidence >= match.threshold ? "ok" : "warn" };
}
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
  const chips = source.title?.trim() && target.title?.trim()
    ? [...to.filter(word => !from.includes(word)), ...from.filter(word => !to.includes(word)).map(word => `Not ${word}`)] : [];
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
  return chips;
}

type MatchEvidence = Pick<MatchReviewItem, "state" | "reasons" | "warnings" | "candidates">;
const concernText: Record<string, string> = {
  clean_edit_of_explicit: "Clean instead of explicit",
  explicit_edit_of_clean: "Explicit instead of clean",
  semantic_version_mismatch: "Different version",
  semantic_version_mismatch_review: "Different version",
  isrc_conflict: "Different ISRC",
  duration_exceeds_tolerance: "Different length",
  duration_mismatch: "Different length",
  ambiguous_top_candidates: "Several close matches",
  limited_recording_metadata: "Playlist gives few details",
};

// Why Allstarr isn't sure, in the matcher's own terms. Settled matches have nothing to explain.
export function matchConcerns(match: MatchEvidence): string[] {
  if (!isAttention(match.state)) return [];
  const concerns = [...match.reasons ?? [], ...match.warnings ?? []].map(code => concernText[code]).filter((text): text is string => Boolean(text));
  const closest = match.candidates?.[0];
  if (match.state.toLowerCase() === "unresolved" && closest?.artist && (closest.artistOverlap ?? 1) < 0.5)
    concerns.unshift(`Closest is by ${closest.artist}`);
  return [...new Set(concerns)];
}

// The chips a row shows: what differs from the playlist's copy, then why the match is unsure. "Same song" only
// when the match is settled and nothing differs; an unsure match with nothing to compare isn't the same song.
export function rowChips(match: MatchEvidence & SongFacts, target: SongFacts | null) {
  const chips = [...new Set([...differenceChips(match, target), ...matchConcerns(match)])];
  return { chips, sameSong: Boolean(target?.title?.trim() && target.artist?.trim()) && !chips.length && !isAttention(match.state) };
}

export function comparisonRows(source: SongFacts, target: SongFacts | null) {
  const fields = ["title", "artist", "album", "durationMilliseconds", "isrc", "version"] as const;
  const labels = ["Title", "Artist", "Album", "Length", "ISRC", "Version"];
  return fields.map((field, index) => {
    const value = (song: SongFacts | null) => !song ? null : field === "version"
      ? (song.title?.trim() ? versionWords(song.title).join(", ") || "Original" : null) : typeof song[field] === "string" ? song[field]?.trim() : song[field];
    const left = value(source), right = value(target);
    let verdict = "Can't compare";
    if (left != null && left !== "" && right != null && right !== "") {
      verdict = normalized(String(left)) === normalized(String(right)) ? "Same" : "Different";
      if (field === "title" && verdict === "Different" &&
        normalized(String(left).replace(/\([^)]*\)|\[[^\]]*\]/g, "")) === normalized(String(right).replace(/\([^)]*\)|\[[^\]]*\]/g, ""))) verdict = "Close";
      if (field === "durationMilliseconds" && left !== right) {
        const delta = Number(right) - Number(left);
        verdict = Math.abs(delta) <= 1000 ? "Close" : `${delta > 0 ? "+" : "-"}${Math.round(Math.abs(delta) / 1000)} s`;
      }
    }
    const display = (input: typeof left) => input == null || input === "" ? "Not provided"
      : field === "durationMilliseconds" ? formatDuration(Number(input)) : String(input);
    return { label: labels[index], original: display(left), current: display(right), verdict };
  });
}

const evidence: Record<string, string> = {
  albumArtist: "Album artist", weakDurationAlbumAgreement: "Album confirms the recording", title: "Title", artist: "Artist", primaryArtist: "Main artist", versionTags: "Version", titleAnnotations: "Title extras", album: "Album", duration: "Length", artwork: "Cover",
  title_strong: "Title is a strong match", title_partial: "Title partly matches",
  artist_strong: "Artist is a strong match", artist_partial: "Artist partly matches",
  album_strong: "Album is a strong match", album_partial: "Album partly matches",
  album_artist_exact: "Album artist matches exactly", album_artist_strong: "Album artist is a strong match", album_artist_partial: "Album artist partly matches",
  duration_partial: "Length partly matches", duration_exceeds_tolerance: "Length differs more than expected", duration_mismatch: "Different length",
  title_annotation_mismatch: "Different title extras", title_annotation_mismatch_review: "Title extras need a look",
  below_accept_threshold_review: "Confidence needs a look", below_suggestion_threshold: "No confident suggestion", weak_artist_evidence_review: "Artist needs a look",
  explicitness_mismatch_review: "Clean or explicit version needs a look", explicit_edit_of_clean: "Explicit version of a clean song", clean_version_available: "A clean version is available",
  weak_source_duration: "Playlist length may be inaccurate", single_album_recording_corroborated: "Album confirms the recording", source_album_marker_confirmation: "Album version needs confirmation", source_album_vocal_evidence: "Album confirms the vocal version",
  provider_track_id_exact: "Same service recording", canonical_recording_id_exact: "Same known recording", isrc_conflict: "Different ISRC",
  musicbrainz_recording_id_exact: "Same MusicBrainz recording", musicbrainz_recording_isrc_exact: "MusicBrainz recording and ISRC match", musicbrainz_recording_isrc_conflict: "MusicBrainz recording has a different ISRC", odesli_alias_exact: "Linked recording identity matches",
  backing_recording_mismatch: "Different backing recording", continuous_mix_excerpt: "Excerpt from a continuous mix", cover_artist_mismatch: "Cover by a different artist",
  field_score: "Compared recording details", ambiguous_top_candidates: "Several versions are close", no_indexed_candidate: "No recording found",
  accepted_route_priority_selected: "Uses your preferred source", local_priority_window_selected: "Your library is preferred", provider_priority_window_selected: "Uses your preferred service",
  manual_override_pinned: "Chosen by you", manual_override_rejected_all: "You rejected these versions", manual_override_target_not_visible: "Your chosen version is unavailable",
  title_exact: "Title matches exactly", title_match: "Title matches", title_case_folded: "Title matches ignoring capitals",
  title_normalized: "Title matches after punctuation is removed", title_decorators_ignored: "Title matches without release labels", title_fuzzy: "Title is similar",
  artist_exact: "Artist matches exactly", artist_match: "Artist matches", album_exact: "Album matches exactly", album_match: "Album matches",
  duration_close: "Length within 1 second", duration_exact: "Length matches exactly", isrc_exact: "ISRC matches exactly", isrc_match: "ISRC matches",
  semantic_version_mismatch: "Different recording version", semantic_version_mismatch_review: "Different version needs a look",
  limited_recording_metadata: "Some recording details are missing", clean_edit_of_explicit: "Clean version of an explicit song",
};
export function evidenceSentence(key: string) {
  return Object.hasOwn(evidence, key) ? evidence[key] : /[ _]/.test(key) && !key.includes("_") ? key : "Compare the recording details before choosing";
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
