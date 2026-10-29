import type { PlaylistDiscoveryItem } from "./api";

export const lastFmPeriods = [
  { value: "7day", label: "Last 7 days" },
  { value: "1month", label: "Last month" },
  { value: "3month", label: "Last 3 months" },
  { value: "12month", label: "Last year" },
  { value: "overall", label: "All time" },
];

export function formatCount(value: number) {
  return new Intl.NumberFormat("en-US").format(value);
}

export function playlistSourceKey(provider: string, id: string) {
  return provider === "lastfm" && /^top:[^:]+(?::50)?$/.test(id) ? id.replace(/:50$/, "") : id;
}

export function brainzDetails(description?: string | null) {
  const kind = description?.match(/^Algorithm: ([a-z0-9]+(?:-[a-z0-9]+)*)$/m)?.[1] ?? "";
  const date = description?.match(/^Created: (.+)$/m)?.[1];
  return { kind, created: date && Number.isFinite(Date.parse(date)) ? date : null };
}

export function brainzPlaylists(items: PlaylistDiscoveryItem[]) {
  const generated = new Map<string, PlaylistDiscoveryItem>();
  for (const item of items) {
    const { kind, created } = brainzDetails(item.description);
    const previous = generated.get(kind);
    const previousDate = brainzDetails(previous?.description).created;
    if (kind && (!previous || created && (!previousDate || Date.parse(created) > Date.parse(previousDate)))) generated.set(kind, item);
  }
  return [...generated.values(), ...items.filter(item => !brainzDetails(item.description).kind)];
}

export function listeningTime(milliseconds: number) {
  const minutes = Math.max(0, Math.round(milliseconds / 60000));
  const hours = Math.floor(minutes / 60);
  return hours ? `${formatCount(hours)} hr${minutes % 60 ? ` ${minutes % 60} min` : ""}` : `${minutes} min`;
}

function exactDate(value: string) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return null;
  const [year, month, day] = value.split("-").map(Number);
  const date = new Date(year, month - 1, day);
  return date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day ? date : null;
}

export function releaseDate(value?: string | null) {
  if (!value) return "Date unavailable";
  if (/^\d{4}$/.test(value)) return value;
  if (/^\d{4}-\d{2}$/.test(value) && Number(value.slice(5)) >= 1 && Number(value.slice(5)) <= 12)
    return new Date(`${value}-01T12:00:00`).toLocaleDateString("en-US", { month: "long", year: "numeric" });
  const date = exactDate(value);
  return date ? date.toLocaleDateString("en-US", { month: "short", day: "numeric", year: "numeric" }) : "Date unavailable";
}

export function groupByWeek<T>(items: T[], dateOf: (item: T) => string | null | undefined, now = new Date()) {
  const monday = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  monday.setDate(monday.getDate() - (monday.getDay() + 6) % 7);
  const lastMonday = new Date(monday);
  lastMonday.setDate(lastMonday.getDate() - 7);
  const groups = ["This week", "Last week", "Earlier"].map(label => ({ label, items: [] as T[] }));
  for (const item of items) {
    const value = dateOf(item);
    const date = value ? exactDate(value) : null;
    groups[date && date >= monday ? 0 : date && date >= lastMonday ? 1 : 2].items.push(item);
  }
  return groups.filter(group => group.items.length);
}
