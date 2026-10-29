import type { TasteNudge } from "./api";

export function greeting(hour: number, name = "") {
  const first = name.trim().split(/\s+/)[0];
  return `Good ${hour >= 5 && hour < 12 ? "morning" : hour >= 12 && hour < 17 ? "afternoon" : "evening"}${first ? `, ${first}` : ""}`;
}
export function mosaicArtwork(references: string[]) {
  const valid = references.filter(Boolean);
  return valid.length >= 4 ? valid.slice(0, 4) : valid.slice(0, 1);
}
export const artworkUrl = (id: string) => `/api/admin/downloads/artwork/${encodeURIComponent(id)}`;
export function reasonLabel(reason: string) {
  if (/^Played \d+ times/.test(reason)) return "You play this a lot";
  if (reason.startsWith("New release")) return "New release";
  if (reason === "A close new pick") return "New for you";
  if (reason.startsWith("An old favourite")) return "An old favourite";
  if (/^(Like |Tagged |From )/.test(reason) || ["Calm sound", "Energetic sound", "Party sound", "Similar sound", "A familiar favourite"].includes(reason)) return reason;
  return "Picked for you";
}
export function shortReadiness(message: string) {
  if (/play about|listening|history/i.test(message)) return "Needs more listening";
  if (/^Follow/i.test(message)) return "Follow more artists";
  if (/^Connect/i.test(message)) return "Connect a music service";
  return "Needs more music";
}
export function tasteSummary(lately: string[], usually: string[]) {
  const join = (values: string[]) => values.length > 1 ? `${values.slice(0, -1).join(", ")} and ${values.at(-1)}` : values[0];
  return [lately.length ? `Lately you've been into ${join(lately.slice(0, 2))}.` : "", usually.length ? `Usually it's ${join(usually.slice(0, 3))}.` : ""].filter(Boolean).join(" ") || "Play some music and your taste will show up here.";
}
export function nudgeMarker(mode?: string) { return ({ more: "↑", less: "↓", never: "⊘" } as Record<string, string>)[mode ?? ""] ?? ""; }
export function nudgeName(nudge: TasteNudge) { return nudge.kind === "song" ? "Hidden song" : nudge.name; }
export const mixOrder = ["daily-1", "daily-2", "daily-3", "daily-4", "discover-weekly", "release-radar", "on-repeat", "rewind", "chill", "focus", "workout", "party", "sleep"];
export function mixExplanation(kind: string) {
  if (kind.startsWith("daily-")) return "Songs you know and close new picks, grouped around your taste. Refreshes every morning.";
  return ({ "discover-weekly": "New songs picked around your taste. Refreshes every Monday.", "release-radar": "New music from artists you follow or play often. Refreshes every Friday.", "on-repeat": "The songs you've played most in the last 30 days. Refreshes every morning.", rewind: "Old favourites you haven't played in six months. Refreshes every Monday.", custom: "Songs chosen to fit your seed. This mix stays as you saved it.", external: "Kept up to date from your connected music service." } as Record<string, string>)[kind] ?? "Music for this mood, chosen from sound analysis and matching styles. Refreshes every Monday.";
}

export function mixName(name: string) {
  const periods: Record<string, string> = { "7day": "Last 7 days", "1month": "Last month", "3month": "Last 3 months", "12month": "Last year", overall: "All time" };
  return name.replace(/^Created for you · (weekly jams|weekly exploration)$/i, (_, value: string) => value.replace(/\b\w/g, letter => letter.toUpperCase()))
    .replace(/· (7day|1month|3month|12month|overall)$/, (_, value: string) => `· ${periods[value]}`);
}

export function mixNextStep(kind: string, message: string, administrator: boolean) {
  if (/^Connect/i.test(message) || kind === "external") return { href: "#/sources", label: "Connect" };
  if (/^Follow/i.test(message)) return { href: "#/for-you?section=releases", label: "Follow artists" };
  if (["chill", "focus", "workout", "party", "sleep"].includes(kind)) return administrator
    ? { href: "#/settings/sound-analysis", label: "Set up sound analysis" } : { href: "#/library/playlists", label: "Open library" };
  if (kind === "custom") return { href: "#/for-you", label: "Try another song" };
  return { href: "#/for-you?import=1", label: "Import history" };
}
