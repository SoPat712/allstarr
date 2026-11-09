import { countLabel } from "./count-label";
import type { MixCard, TasteNudge } from "./api";

export function greeting(hour: number, name = "") {
  const first = name.trim().split(/\s+/)[0];
  return `Good ${hour >= 5 && hour < 12 ? "morning" : hour >= 12 && hour < 17 ? "afternoon" : "evening"}${first ? `, ${first}` : ""}`;
}
export function mosaicArtwork(references: string[]) {
  const valid = references.filter(Boolean);
  return valid.length >= 4 ? valid.slice(0, 4) : valid.slice(0, 1);
}
// Release covers arrive as ready-made Allstarr paths; every other reference is a library or provider item id.
export const artworkUrl = (id: string) => id.startsWith("/api/") ? id : `/api/admin/downloads/artwork/${encodeURIComponent(id)}`;
export function reasonLabel(reason: string) {
  if (/^Played \d+ times/.test(reason)) return "You play this a lot";
  if (reason.startsWith("New release")) return "New release";
  if (reason === "A close new pick") return "New for you";
  if (reason.startsWith("An old favourite")) return "An old favourite";
  if (/^(Like |Tagged |From )/.test(reason) || ["Calm sound", "Energetic sound", "Party sound", "Similar sound", "A familiar favourite"].includes(reason)) return reason;
  return "Picked for you";
}
export function shortReadiness(message: string) {
  if (/^Refresh this mix/i.test(message)) return "Refresh to look for songs";
  const plays = message.match(/^Play about (\d+) more songs/i);
  if (plays) return `Play about ${countLabel(Number(plays[1]), "more song")}`;
  const songs = message.match(/^Add about (\d+) more qualifying analyzed or tagged songs/i);
  if (songs) return `Needs about ${countLabel(Number(songs[1]), "more song")}`;
  if (/^No playable songs are available/i.test(message)) return "No songs yet";
  if (/^Fewer than \d+ songs remain/i.test(message)) return "Not enough matching songs yet";
  if (message === "Connect a service with recommendations, or add unheard tagged songs to your library.") return "No new songs to suggest yet";
  if (/play about|listening|history/i.test(message)) return "Needs more listening";
  if (/^Follow/i.test(message)) return "Follow more artists";
  if (/^Connect/i.test(message)) return "Connect a music service";
  return "Needs more music";
}
export function tasteSummary(lately: string[], usually: string[]) {
  const join = (values: string[]) => values.length > 1 ? `${values.slice(0, -1).join(", ")} and ${values.at(-1)}` : values[0];
  return [lately.length ? `Lately you've been into ${join(lately.slice(0, 2))}.` : "", usually.length ? `Usually it's ${join(usually.slice(0, 3))}.` : ""].filter(Boolean).join(" ") || "Play some music and your taste will show up here.";
}
// Long-term favourites and what's playing lately take turns, so neither crowds the other out.
export function steeringNames(lately: string[], usually: string[], limit: number) {
  const names = new Set<string>();
  for (let index = 0; names.size < limit && index < Math.max(lately.length, usually.length); index++) {
    if (usually[index]) names.add(usually[index]);
    if (lately[index] && names.size < limit) names.add(lately[index]);
  }
  return [...names];
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
  return name.replace(/^(Chill|Focus|Workout|Party|Sleep) Mix$/i, "$1").replace(/^Created for you · (weekly jams|weekly exploration)$/i, (_, value: string) => value.replace(/\b\w/g, letter => letter.toUpperCase()))
    .replace(/· (7day|1month|3month|12month|overall)$/, (_, value: string) => `· ${periods[value]}`);
}

export function mixNextStep(kind: string, message: string, administrator: boolean) {
  if (/^Connect/i.test(message) || kind === "external") return { href: "#/sources", label: "Connect" };
  if (/^Follow/i.test(message)) return { href: "#/for-you?section=releases", label: "Follow artists" };
  if (["chill", "focus", "workout", "party", "sleep"].includes(kind)) return administrator
    ? { href: "#/settings/discovery", label: "Set up sound analysis" } : { href: "#/library/playlists", label: "Open library" };
  if (kind === "custom") return { href: "#/for-you", label: "Try another song" };
  return { href: "#/for-you?import=1", label: "Import history" };
}

export function mixDescription(description: string, kind: string) {
  const descriptions: Record<string, string> = {
    "discover-weekly": "New to you, based on your taste",
    rewind: "Songs you loved and haven't played lately",
    "on-repeat": "What you're playing most right now",
    "release-radar": "New from artists you play",
  };
  if (descriptions[kind]) return descriptions[kind];
  if (kind.startsWith("daily-")) return description.replace(/^Made for you\s*·\s*[^·]*·?\s*/, "");
  if (["chill", "focus", "workout", "party", "sleep"].includes(kind)) return "Updated Monday";
  return description;
}

export function mixExclusions(excluded: Record<string, number>) {
  const labels: Record<string, string> = {
    "three early skips": "skipped early", never: "you marked Never", "duplicate recordings": "repeats",
    "listening policy": "left out by your listening settings", "no playable route": "you can't play",
    "missing song details": "missing song details", "candidate limit": "over the song limit",
    "changed before saving": "left out before saving", "choices changed during generation": "left out after your choices changed", "outside this recipe": "didn't fit this mix",
    "artist, release, tier and size limits": "over this mix's limits",
  };
  // The API client lowercases the first letter of every response key.
  return Object.entries(excluded).filter(([, count]) => count > 0).map(([label, count]) => `${count} ${labels[label.toLowerCase()] ?? "left out"}`).join(" · ");
}

// One sentence a listener can recognise: what sets their favourites apart from the rest of their library, the pace,
// and what they skip. Differences under a quarter of the library's spread aren't worth saying.
const notable = 25;
export function soundSummary(profile: { traits: { name: string; difference: number; lean: number | null }[]; tempo: number | null; libraryTempo: number | null }) {
  const names = (rows: { name: string }[]) => rows.map(row => row.name.toLowerCase()).join(" and ");
  const more = profile.traits.filter(trait => trait.difference >= notable).sort((a, b) => b.difference - a.difference).slice(0, 2);
  const less = profile.traits.filter(trait => trait.difference <= -notable).sort((a, b) => a.difference - b.difference).slice(0, 1);
  const sets = [more.length ? `more ${names(more)}` : "", less.length ? `less ${names(less)}` : ""].filter(Boolean).join(" and ");
  const faster = profile.tempo && profile.libraryTempo && Math.abs(profile.tempo - profile.libraryTempo) >= 5
    ? profile.tempo > profile.libraryTempo ? "a little faster" : "a little slower" : "";
  const pace = profile.tempo ? `${faster ? `${faster}, ` : ""}around ${profile.tempo} BPM` : "";
  const lean = profile.traits.filter(trait => trait.lean !== null && Math.abs(trait.lean) >= notable).sort((a, b) => Math.abs(b.lean!) - Math.abs(a.lean!))[0];
  return [
    `${sets ? `Your favourites are ${sets} than the rest of your library` : "Your favourites sound much like the rest of your library"}${pace ? `, ${pace}` : ""}.`,
    lean ? lean.lean! < 0 ? `You skip ${lean.name.toLowerCase()} songs more than you finish them.` : `You finish ${lean.name.toLowerCase()} songs more than you skip them.` : "",
  ].filter(Boolean).join(" ");
}

export function soundDifference(value: number) {
  return value > 0 ? `+${value}` : value < 0 ? `−${Math.abs(value)}` : "0";
}

export function serviceCards(cards: MixCard[], suggestions: MixCard[] = []): MixCard[] {
  const feeds = cards.filter(card => card.section === "yourServices");
  return [...feeds.map(card => ({ ...card, description: card.sourceLabel ? `From ${card.sourceLabel}${card.inApps ? " · kept in sync" : ""}` : "From your connected service" })),
    ...suggestions.filter(suggestion => !feeds.some(card => card.source && card.source === suggestion.source && card.sourcePlaylistId === suggestion.sourcePlaylistId))];
}
