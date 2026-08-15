import type { ActivityItem } from "./api";

export type ActivityFilters = {
  query: string;
  kind: string;
  outcome: string;
  provider: string;
  severity: string;
};

export type ActivityGroup = {
  key: string;
  operationKey: string;
  entries: ActivityItem[];
  title: string;
};

export function humanize(value?: string | null) {
  if (!value) return "Unknown";
  return value
    .replace(/reviewrequired/gi, "review required")
    .replaceAll(/[-_.]+/g, " ")
    .replaceAll(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/\b\w/g, (letter) => letter.toUpperCase());
}

export function relativeTime(value?: string | null, fallback = "Not checked") {
  if (!value) return fallback;
  const seconds = Math.round((new Date(value).getTime() - Date.now()) / 1_000);
  const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
  if (Math.abs(seconds) < 60) return formatter.format(seconds, "second");
  const minutes = Math.round(seconds / 60);
  if (Math.abs(minutes) < 60) return formatter.format(minutes, "minute");
  const hours = Math.round(minutes / 60);
  if (Math.abs(hours) < 24) return formatter.format(hours, "hour");
  return formatter.format(Math.round(hours / 24), "day");
}

export function filterActivity(items: ActivityItem[], filters: ActivityFilters) {
  const query = filters.query.trim().toLowerCase();
  return items.filter((item) => {
    if (filters.kind && item.kind !== filters.kind) return false;
    if (filters.outcome && item.state !== filters.outcome) return false;
    if (filters.provider && item.providerId !== filters.provider) return false;
    if (filters.severity && (item.severity || "info") !== filters.severity) return false;
    return !query || [
      item.label,
      item.detail,
      item.source,
      item.providerId,
      item.playlistName,
      item.correlationId,
      item.sourceTitle,
      item.targetTitle,
    ].some((value) => value?.toLowerCase().includes(query));
  });
}

export function mergeActivity(current: ActivityItem[], incoming: ActivityItem[]) {
  return [...new Map([...current, ...incoming].map((item) => [item.id, item])).values()]
    .toSorted((left, right) =>
      right.occurredAt.localeCompare(left.occurredAt) || right.id.localeCompare(left.id));
}

export function groupActivity(items: ActivityItem[]) {
  const groups: ActivityGroup[] = [];
  const occurrences = new Map<string, number>();
  for (const item of items) {
    const key = item.correlationId && item.action
      ? `${item.correlationId}|${item.action}`
      : [item.kind, item.source, item.label, item.state].join("|");
    const previous = groups.at(-1);
    if (previous?.operationKey === key) {
      const duplicate = previous.entries.some((entry) =>
        entry.label === item.label &&
        entry.state === item.state &&
        entry.detail === item.detail &&
        entry.action === item.action);
      if (!duplicate) previous.entries.push(item);
      previous.title = groupTitle(previous.entries);
    } else {
      const occurrence = (occurrences.get(key) ?? 0) + 1;
      occurrences.set(key, occurrence);
      groups.push({
        key: `${key}|${occurrence}`,
        operationKey: key,
        entries: [item],
        title: humanize(item.label),
      });
    }
  }
  return groups;
}

function groupTitle(entries: ActivityItem[]) {
  if (entries.length === 1) return humanize(entries[0].label);
  const first = entries[0];
  if (first.kind === "matching") {
    const accepted = entries.every((item) =>
      ["accepted", "pinned"].includes(item.state.toLowerCase()));
    const playlists = new Set(entries.map((item) => item.playlistName).filter(Boolean));
    return `${accepted ? "Matched" : "Evaluated"} ${entries.length} tracks` +
      (playlists.size ? ` across ${playlists.size} playlist${playlists.size === 1 ? "" : "s"}` : "");
  }
  if (first.kind === "playlist") return `${humanize(first.label)} · ${entries.length} playlists`;
  return `${humanize(first.label)} · ${entries.length} events`;
}

export function groupOutcome(entries: ActivityItem[]) {
  const states = uniqueStates(entries.map((item) => item.state));
  return states.length === 1 ? states[0] : "mixed";
}

export function groupSeverity(entries: ActivityItem[]) {
  const severities = uniqueStates(entries.map((item) => item.severity || "info"));
  return severities.includes("error")
    ? "error"
    : severities.includes("warning")
      ? "warning"
      : "info";
}

function uniqueStates(values: string[]) {
  return [...new Set(values.map((value) => value.toLowerCase()))];
}

export function activityIcon(kind: string) {
  return ({
    administration: "⚙",
    caching: "↓",
    extension: "◇",
    job: "↻",
    library: "♫",
    matching: "↔",
    playlist: "≡",
    provider_health: "♥",
    scrobble: "♪",
    streaming: "▶",
  } as Record<string, string>)[kind] ?? "•";
}

export function activityLink(item: ActivityItem) {
  if (item.playlistLinkId)
    return `#/library/playlists?playlist=${encodeURIComponent(item.playlistLinkId)}`;
  if (item.kind === "caching") return "#/library/cached";
  if (item.kind === "matching") {
    const search = item.sourceTitle || item.sourceProviderTrackId || item.detail;
    return `#/library/mappings?search=${encodeURIComponent(search)}`;
  }
  if (item.providerId) return "#/sources";
  return null;
}

export function outcomeClass(state: string) {
  const normalized = state.toLowerCase();
  if (["accepted", "delivered", "healthy", "pinned", "succeeded", "success"].includes(normalized))
    return "accepted";
  if (["ambiguous", "partial", "retrying", "suggested", "warning"].includes(normalized))
    return "suggested";
  if (["failed", "rejected", "unhealthy", "unresolved"].includes(normalized))
    return "rejected";
  return "";
}
