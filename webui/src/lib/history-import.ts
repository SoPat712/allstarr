import { ApiError, type HistoryImport } from "./api";
import { formatCount } from "./intelligence";

export function historyBytes(bytes: number) {
  if (bytes < 1024) return `${formatCount(bytes)} ${bytes === 1 ? "byte" : "bytes"}`;
  const unit = bytes < 1024 ** 2 ? 1 : 2;
  return `${new Intl.NumberFormat("en-US", { maximumFractionDigits: 1 }).format(bytes / 1024 ** unit)} ${unit === 1 ? "KB" : "MB"}`;
}

export function historyPlays(count: number) { return `${formatCount(count)} ${count === 1 ? "play" : "plays"}`; }

export function historyDateRange(first?: string | null, last?: string | null, years = false) {
  if (!first || !last || !Number.isFinite(Date.parse(first)) || !Number.isFinite(Date.parse(last))) return "Date range unavailable";
  const options: Intl.DateTimeFormatOptions = years ? { year: "numeric", timeZone: "UTC" } : { month: "short", day: "numeric", year: "numeric", timeZone: "UTC" };
  const start = new Date(first).toLocaleDateString("en-US", options), end = new Date(last).toLocaleDateString("en-US", options);
  return start === end ? start : `${start}${years ? "–" : " – "}${end}`;
}

export function historySource(format: string) {
  return ({ "spotify-extended-streaming-history": "Spotify", "lastfm-recent-tracks": "Last.fm", "listenbrainz-export": "ListenBrainz", "maloja-export": "Maloja", "koito-export-v1": "Koito" } as Record<string, string>)[format] ?? "Other service";
}

export function importPhase(item: HistoryImport) {
  return typeof item.state === "number" ? ["previewed", "pending", "running", "completed", "cancelled", "failed", "expired"][item.state] : item.state.toLowerCase();
}

export function importProgress(item: HistoryImport) {
  return importPhase(item) === "completed" ? 100 : Math.min(99, Math.max(0, Math.floor(100 * (item.importedRows + item.duplicateRows) / Math.max(1, item.preview.musicRows))));
}

function safeMessage(value: unknown) { return typeof value === "string" && /\s/.test(value) && !/[{}_]/.test(value) ? value : ""; }

export function importState(item: HistoryImport) {
  switch (importPhase(item)) {
    case "previewed": case "pending": return "Waiting";
    case "running": return `Importing ${importProgress(item)}%`;
    case "completed": return "Done";
    case "failed": return `Failed: ${safeMessage(item.lastErrorMessage) || "The file could not be imported."}`;
    case "cancelled": return "Cancelled";
    case "expired": return "Expired. Choose the file again.";
    default: return "Status unavailable";
  }
}

export function importSummary(item: HistoryImport) {
  const source = historySource(item.preview.format), range = historyDateRange(item.preview.earliest, item.preview.latest, true);
  const suffix = range === "Date range unavailable" ? "" : ` (${range})`;
  return item.importedRows === item.preview.musicRows
    ? `Imported ${historyPlays(item.preview.completed)} from ${source}${suffix}`
    : `Import from ${source}${suffix} is done.`;
}

export function historyError(error: unknown, fallback: string) {
  if (!(error instanceof ApiError)) return fallback;
  if (error.status === 413) return "Choose a history file up to 500 MB.";
  const details = error.details as { message?: string; error?: string } | null;
  return safeMessage(details?.message) || safeMessage(details?.error) || fallback;
}
