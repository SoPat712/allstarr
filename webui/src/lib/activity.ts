import type { ActivityItem } from "./api";

export type ActivityFilters = {
  query: string;
  category: string;
  provider: string;
  problemsOnly: boolean;
};

export const activityCategories = [
  { id: "listening", label: "Listening", kinds: ["streaming", "scrobble"] },
  { id: "playlists", label: "Playlists", kinds: ["playlist"] },
  { id: "matching", label: "Matching", kinds: ["matching", "library"] },
  { id: "sources", label: "Sources", kinds: ["provider_health", "extension"] },
  { id: "system", label: "System", kinds: ["job", "caching", "administration"] },
] as const;

export function activityCategory(kind: string) {
  return activityCategories.find((category) => (category.kinds as readonly string[]).includes(kind))?.id ?? "system";
}

export function isProblem(item: ActivityItem) {
  const severity = (item.severity || "info").toLowerCase();
  return severity === "error" || severity === "warning" || outcomeClass(item.state) === "rejected";
}

export function dayLabel(value: string, now = new Date()) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "Earlier";
  const startOfDay = (input: Date) => new Date(input.getFullYear(), input.getMonth(), input.getDate()).getTime();
  const days = Math.round((startOfDay(now) - startOfDay(date)) / 86_400_000);
  if (days === 0) return "Today";
  if (days === 1) return "Yesterday";
  return new Intl.DateTimeFormat(undefined, {
    weekday: days < 7 ? "long" : undefined,
    month: "long",
    day: "numeric",
    year: date.getFullYear() === now.getFullYear() ? undefined : "numeric",
  }).format(date);
}

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
    if (filters.category && activityCategory(item.kind) !== filters.category) return false;
    if (filters.provider && item.providerId !== filters.provider) return false;
    if (filters.problemsOnly && !isProblem(item)) return false;
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
  const operations = new Map<string, ActivityGroup>();
  for (const item of items) {
    const correlated = Boolean(item.correlationId && item.action);
    const operationKey = correlated
      ? `${item.correlationId}|${item.action}`
      : item.id;
    const group = correlated ? operations.get(operationKey) : undefined;
    if (group) {
      const duplicate = group.entries.some((entry) =>
        entry.label === item.label &&
        entry.state === item.state &&
        entry.detail === item.detail &&
        entry.action === item.action);
      if (!duplicate) group.entries.push(item);
      group.title = groupTitle(group.entries);
    } else {
      const created = {
        key: operationKey,
        operationKey,
        entries: [item],
        title: humanize(item.label),
      };
      groups.push(created);
      if (correlated) operations.set(operationKey, created);
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
    return `${accepted ? "Matched" : "Checked"} ${entries.length} songs` +
      (playlists.size ? ` in ${playlists.size} playlist${playlists.size === 1 ? "" : "s"}` : "");
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
  if (item.providerId && item.providerId !== "library")
    return `#/integrations/services?source=${encodeURIComponent(item.providerId)}`;
  return null;
}

export function activityAction(item: ActivityItem, providerName: (id: string) => string) {
  const href = activityLink(item);
  if (!href) return null;
  if (item.playlistLinkId) return { href, label: `Open ${item.playlistName || "playlist"}` };
  if (item.kind === "caching") return { href, label: "Open cached songs" };
  if (item.kind === "matching") return { href, label: "Review match" };
  return { href, label: `Open ${providerName(item.providerId ?? "")}` };
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
