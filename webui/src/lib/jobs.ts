import type { JobProgress } from "./api";

export type JobProgressDetails = {
  stage?: string;
  message?: string;
  completed?: number | null;
  total?: number | null;
  provider?: string | null;
  playlist?: string | null;
  track?: string | null;
  deferralReason?: string | null;
  throughputPerSecond?: number | null;
};

export function progressDetails(item: JobProgress): JobProgressDetails {
  try {
    const value = JSON.parse(item.detailsJson);
    return value && typeof value === "object" ? value : {};
  } catch {
    return {};
  }
}

export function compactProgress(items: JobProgress[]) {
  let previous = "";
  return items.filter((item) => {
    const details = progressDetails(item);
    const key = `${item.action}|${item.outcome}|${details.message ?? ""}`;
    if (key === previous) return false;
    previous = key;
    return true;
  }).slice(0, 200);
}

export type HistoryOutcome = { title: string; artist: string; plays: number; found: string; state: "resolved" | "notfound" | "retry"; label: string };
export type HistoryMatching = { found: number; notFound: number; retry: number; recent: HistoryOutcome[] };

const retryOutcomes = ["provider-failure:", "song-read-failed", "identity-missing", "identity-conflict", "exception:"];

// Plain words for one history match outcome code.
export function historyOutcome(reason: string): Pick<HistoryOutcome, "state" | "label"> {
  if (reason.startsWith("matched-")) return { state: "resolved", label: "Found" };
  if (retryOutcomes.some(code => reason.startsWith(code))) return { state: "retry", label: "Trying again later" };
  if (reason === "missing-metadata") return { state: "notfound", label: "No title or artist" };
  if (reason === "edition-mismatch") return { state: "notfound", label: "Other version" };
  if (reason === "not-playback-eligible") return { state: "notfound", label: "Can't be played" };
  if (reason === "no-candidates") return { state: "notfound", label: "Not found" };
  return { state: "notfound", label: "No close match" };
}

// The imported-history matcher reports its outcome counts and newest songs as JSON; older reports hold only the counts.
export function historyMatching(details: JobProgressDetails): HistoryMatching | null {
  if (details.stage !== "history-resolution" || !details.message) return null;
  let value: unknown;
  try { value = JSON.parse(details.message); } catch { return null; }
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const report = value as { reasons?: unknown; recent?: unknown };
  const reasons = (report.reasons && typeof report.reasons === "object" ? report.reasons : "recent" in report ? {} : report) as Record<string, unknown>;
  const result: HistoryMatching = { found: 0, notFound: 0, retry: 0, recent: [] };
  for (const [reason, count] of Object.entries(reasons)) {
    if (typeof count !== "number") continue;
    const state = historyOutcome(reason).state;
    if (state === "resolved") result.found += count; else if (state === "retry") result.retry += count; else result.notFound += count;
  }
  for (const item of Array.isArray(report.recent) ? report.recent : []) {
    if (!item || typeof item !== "object") continue;
    const song = item as Record<string, unknown>;
    const text = (key: string) => typeof song[key] === "string" ? song[key] as string : "";
    if (!text("title")) continue;
    const outcome = historyOutcome(text("reason"));
    const same = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: "accent" }) === 0;
    const found = outcome.state === "resolved" && text("found") && !(same(text("found"), text("title")) && same(text("foundArtist"), text("artist")))
      ? [text("found"), text("foundArtist")].filter(Boolean).join(" · ") : "";
    result.recent.push({ title: text("title"), artist: text("artist"), plays: typeof song.plays === "number" ? song.plays : 1, found, ...outcome });
  }
  return result;
}
