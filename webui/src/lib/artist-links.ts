import { json } from "./api-client";
import { countLabel } from "./count-label";

export type ArtistLinkState = "songs" | "same" | "not-same" | "candidate";
export type ArtistLinkScope = "household" | "you";
export type ArtistLinkStatus = "needs-look" | "linked" | "decided" | "apart" | "library-only";
export type ArtistLinkFilter = "needs-look" | "linked" | "decided" | "apart" | "all";
export type ArtistLinkService = {
  providerId: string; providerName: string; artistId: string; name: string;
  state: ArtistLinkState; scope: ArtistLinkScope | null; sharedSongs: number | null;
  sharedSongTitles?: string[]; checkedAt?: string; reason?: string;
};
export type ArtistLinkRow = { id: string; name: string; songCount: number; imageUrl: string | null; status: ArtistLinkStatus; services: ArtistLinkService[] };
export type ArtistLinkPage = {
  counts: { needsLook: number; linked: number; decided: number; apart: number; all: number };
  artists: ArtistLinkRow[]; nextCursor: string | null;
};

const base = "/api/admin/artist-links";
const path = (artist: string, service: Pick<ArtistLinkService, "providerId" | "artistId">) =>
  `${base}/${[artist, service.providerId, service.artistId].map(encodeURIComponent).join("/")}`;

export const artistLinks = {
  list: (filter: ArtistLinkFilter, q = "", cursor?: string | null, limit = 50) =>
    json<ArtistLinkPage>(`${base}?${new URLSearchParams({ filter, limit: String(limit), ...(q ? { q } : {}), ...(cursor ? { cursor } : {}) })}`),
  get: (id: string, signal?: AbortSignal) => json<ArtistLinkRow>(`${base}/${encodeURIComponent(id)}`, { signal }),
  set: (artist: string, service: ArtistLinkService, same: boolean, scope: ArtistLinkScope) =>
    json<ArtistLinkRow>(path(artist, service), { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ same, scope }) }),
  undo: (artist: string, service: ArtistLinkService, scope: ArtistLinkScope) =>
    json<ArtistLinkRow>(`${path(artist, service)}?${new URLSearchParams({ scope })}`, { method: "DELETE" }),
  review: () => json<{ queued: boolean; running: boolean }>(`${base}/review`, { method: "POST" }),
};

export const artistScopes = [{ id: "household", label: "Household" }, { id: "you", label: "Just me" }] as const;

export function artistPill(status: ArtistLinkStatus) {
  return ({
    "needs-look": { tone: "warn", text: "Needs a look" },
    linked: { tone: "ok", text: "One entry" },
    decided: { tone: "neutral", text: "By you" },
    apart: { tone: "neutral", text: "Kept apart" },
    "library-only": { tone: "neutral", text: "Library only" },
  } as const)[status];
}

function list(items: string[]) {
  const unique = [...new Set(items)];
  return unique.length <= 2 ? unique.join(" and ") : `${unique.slice(0, -1).join(", ")} and ${unique.at(-1)}`;
}
const names = (services: ArtistLinkService[]) => list(services.map(service => service.providerName));

const scopeText = (scope: ArtistLinkScope | null) => scope === "you" ? "just for you" : "for the household";

// One line under each artist says why it has the status it has.
export function artistSummary(row: ArtistLinkRow) {
  const songs = countLabel(row.songCount, "song");
  const of = (state: ArtistLinkState) => row.services.filter(service => service.state === state);
  if (row.status === "needs-look") {
    const waiting = of("candidate");
    return `${songs} · ${names(waiting)}: same name, no shared songs yet`;
  }
  const [same] = of("same"), [apart] = of("not-same");
  if (row.status === "decided" && same) return `${songs} · ${same.providerName} marked as the same artist ${scopeText(same.scope)}`;
  if (row.status === "apart" && apart) return `${songs} · ${apart.providerName} kept apart ${scopeText(apart.scope)}`;
  if (row.status === "linked") return `${songs} · linked by shared songs`;
  return `${songs} · no service artist found`;
}

export type ArtistLinkSection = { id: "ask" | "songs" | "decisions"; label: string; services: ArtistLinkService[] };

export function artistSections(row: ArtistLinkRow): ArtistLinkSection[] {
  return ([
    { id: "ask", label: "Not linked yet", services: row.services.filter(service => service.state === "candidate") },
    { id: "songs", label: "Linked by shared songs", services: row.services.filter(service => service.state === "songs") },
    { id: "decisions", label: "Your decisions", services: row.services.filter(service => service.state === "same" || service.state === "not-same") },
  ] satisfies ArtistLinkSection[]).filter(section => section.services.length);
}

const sentence = (text: string) => text ? text[0].toUpperCase() + text.slice(1) : text;

export function serviceDetail(service: ArtistLinkService) {
  if (service.state === "same") return `You said same artist, ${scopeText(service.scope)}`;
  if (service.state === "not-same") return `You said not the same, ${scopeText(service.scope)}`;
  if (service.state === "songs") {
    const titles = service.sharedSongTitles ?? [];
    const count = service.sharedSongs ?? titles.length;
    if (!count) return "Linked by shared songs";
    return `${countLabel(count, "song")} ${count === 1 ? "matches" : "match"} yours${titles.length ? `: ${titles.join(", ")}${count > titles.length ? "…" : ""}` : ""}`;
  }
  return `${sentence(service.reason ?? "same name, no shared songs")}. Apps show it as a separate entry for now.`;
}

// What a listening app shows for this artist right now.
export function inAppsNow(row: ArtistLinkRow) {
  const joined = row.services.filter(service => service.state === "songs" || service.state === "same");
  const separate = row.services.filter(service => service.state === "candidate" || service.state === "not-same");
  const one = joined.length ? `One "${row.name}" with ${list(["library", ...joined.map(service => service.providerName)])} songs` : `"${row.name}" with your library's songs`;
  return separate.length ? `${one}; ${names(separate)} ${separate.length === 1 ? "shows as a separate artist" : "show as separate artists"}` : one;
}
