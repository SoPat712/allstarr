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
  if (!Number.isFinite(seconds)) return fallback;
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
  const jobs = new Map<string, ActivityItem[]>();
  for (const item of items) {
    const key = activityJobKey(item);
    if (key) jobs.set(key, [...(jobs.get(key) ?? []), item]);
  }
  const matches = (item: ActivityItem) => {
    if (filters.category && activityCategory(item.kind) !== filters.category) return false;
    if (filters.provider && item.providerId !== filters.provider) return false;
    return !query || [item.actorDisplayName, item.label, item.detail, item.source, item.providerId,
      item.playlistName, item.correlationId, item.sourceTitle, item.targetTitle, item.jobId, item.recovery]
      .some(value => value?.toLowerCase().includes(query));
  };
  return items.filter(item => {
    const family = jobs.get(activityJobKey(item));
    const latest = family?.toSorted((a, b) => b.occurredAt.localeCompare(a.occurredAt))[0] ?? item;
    if (filters.problemsOnly && !isProblem(latest)) return false;
    return family ? family.some(matches) : matches(item);
  });
}

function activityJobKey(item: ActivityItem) {
  return item.jobId ? `${item.actorUserId ?? "household"}|job:${item.jobId.toLowerCase().replaceAll("-", "")}` : "";
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
    const jobKey = activityJobKey(item);
    const correlated = Boolean(jobKey || item.correlationId && item.action);
    const operationKey = jobKey || (correlated
      ? `${item.actorUserId ?? "household"}|${item.correlationId}|${item.action}`
      : item.id);
    const group = correlated ? operations.get(operationKey) : undefined;
    if (group) {
      const duplicate = group.entries.some((entry) =>
        entry.label === item.label &&
        entry.state === item.state &&
        entry.detail === item.detail &&
        entry.action === item.action);
      if (!duplicate) group.entries.push(item);
      group.entries.sort((a, b) => b.occurredAt.localeCompare(a.occurredAt));
      group.title = groupTitle(group.entries);
    } else {
      const created = {
        key: operationKey,
        operationKey,
        entries: [item],
        title: item.kind === "job" || activityJobKey(item) ? activityHeadline(item) : humanize(item.label),
      };
      groups.push(created);
      if (correlated) operations.set(operationKey, created);
    }
  }
  return groups;
}

function groupTitle(entries: ActivityItem[]) {
  if (entries.length === 1 || activityJobKey(entries[0])) return activityHeadline(entries[0]);
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
  if (activityJobKey(entries[0])) return entries[0].state.toLowerCase();
  const states = uniqueStates(entries.map((item) => item.state));
  return states.length === 1 ? states[0] : "mixed";
}

export function groupSeverity(entries: ActivityItem[]) {
  if (activityJobKey(entries[0])) return entries[0].severity || "info";
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
  if (internalActivityLink(item.actionHref)) return item.actionHref!;
  if (validJobId(item.jobId)) return `#/activity?view=jobs&job=${encodeURIComponent(item.jobId!)}`;
  if (item.playlistLinkId)
    return `#/library/playlists?playlist=${encodeURIComponent(item.playlistLinkId)}`;
  if (item.kind === "caching") return "#/library/cached";
  if (item.kind === "matching") {
    const search = item.sourceTitle || item.sourceProviderTrackId || item.detail;
    return `#/library/mappings?search=${encodeURIComponent(search)}`;
  }
  if (item.providerId && item.providerId !== "library")
    return `#/integrations/services/${encodeURIComponent(item.providerId)}`;
  return null;
}

export function activityAction(item: ActivityItem, providerName: (id: string) => string) {
  const href = activityLink(item);
  if (!href) return null;
  if (internalActivityLink(item.actionHref) && item.actionLabel) return { href, label: item.actionLabel };
  if (validJobId(item.jobId)) return { href, label: "Open related work" };
  if (item.playlistLinkId) return { href, label: `Open ${item.playlistName || "playlist"}` };
  if (item.kind === "caching") return { href, label: "Open cached songs" };
  if (item.kind === "matching") return { href, label: "Review match" };
  return { href, label: `Open ${providerName(item.providerId ?? "")}` };
}

export function outcomeClass(state: string) {
  const tone = activityTone(state);
  return tone === "success" ? "accepted" : tone === "warning" ? "suggested" : tone === "danger" ? "rejected" : "";
}


export const jobNames: Record<string, string> = {
  "health.smoke": "Check music server", "health.cleanup": "Clean up earlier checks", "health.alert": "Send health alert", "storage.backup": "Back up database and keyring",
  "playlist.materialize": "Update playlist",
  "playback.signal": "Record a listen", "favorite.process": "Update favorites", "favorite.display-backfill": "Recover favorite details",
  "recording.kept-cleanup": "Remove unused kept copy", "recording.keep": "Keep a song", "playlist.retain-track": "Keep playlist song",
  "track-match.rematch": "Refresh matches", "library.index": "Refresh library index", "recording.musicbrainz.enrich": "Identify recording", "recording.odesli.enrich": "Find recording links",
};
export function jobName(type: string) { return jobNames[type] ?? "Background task"; }
export function jobState(state: string) {
  return ({ Pending: "Waiting", Running: "Running", RetryScheduled: "Retrying", Deferred: "Deferred", Succeeded: "Completed", Failed: "Failed", Cancelled: "Cancelled" } as Record<string, string>)[state] ?? humanize(state);
}
export function progressPercent(completed?: number | null, total?: number | null) {
  return typeof total === "number" && Number.isFinite(total) && total > 0 && typeof completed === "number" && Number.isFinite(completed)
    ? Math.min(100, Math.max(0, Math.round(completed / total * 100))) : null;
}

export type ActivityTone = "neutral" | "accent" | "success" | "warning" | "danger";
export function activityTone(state: string, severity = "info"): ActivityTone {
  const normalized = state.toLowerCase().replaceAll(/[-_ ]/g, "");
  if (["cancelled", "canceled", "superseded", "notchecked", "paused", "automatic"].includes(normalized)) return "neutral";
  if (["failed", "rejected", "unhealthy", "unresolved", "error", "blocked", "unauthorized"].includes(normalized)) return "danger";
  if (normalized.startsWith("skipped") || normalized.startsWith("retry") || ["ambiguous", "partial", "partlychecked", "partiallysucceeded", "partialfailure", "conflict", "conflicted", "deferred", "interrupted", "suggested", "warning", "warn", "degraded", "needsattention", "needsreview", "sampleunavailable"].includes(normalized)) return "warning";
  if (["accepted", "delivered", "healthy", "pinned", "succeeded", "success", "passed", "completed", "verified", "placed", "saved", "resolved", "ok", "updated", "released", "created", "enriched", "merged", "alreadylinked", "concurrentexisting", "rematched", "republished"].includes(normalized)) return "success";
  if (["running", "inprogress", "started", "pending", "waiting", "queued", "scheduled", "checking", "active"].includes(normalized)) return "accent";
  return severity.toLowerCase() === "error" ? "danger" : ["warning", "warn"].includes(severity.toLowerCase()) ? "warning" : "neutral";
}
export function validJobId(value?: string | null) {
  return Boolean(value && /^(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i.test(value));
}
export function internalActivityLink(value?: string | null) {
  return Boolean(value && /^#\/(?:activity(?:\?|$)|library\/(?:playlists|mappings|cached|kept)(?:[/?]|$)|integrations\/(?:services|accounts|routing)(?:[/?]|$)|settings\/(?:health|backups|setup)(?:[/?]|$))/.test(value));
}
const opaqueReference = /\b(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{24,})\b/gi;
export function activityText(value?: string | null) {
  if (!value) return "";
  if (/^(?:[0-9a-f]{24,}|[0-9a-f-]{36})$/i.test(value.trim())) return "Details were not retained for this older event.";
  return value.replace(opaqueReference, "the recorded item");
}
export function activityHeadline(item: ActivityItem) {
  if (item.kind === "job" && /^[a-z0-9-]+\.[a-z0-9.-]+$/.test(item.label)) return jobName(item.label);
  return activityText(item.label) || "Activity update";
}
export function activityDuration(value?: number | null) {
  if (value == null || !Number.isFinite(value) || value < 0) return "";
  return value < 1000 ? "less than a second" : `${Number((value / 1000).toFixed(1))} seconds`;
}
