import type { PlaylistLink, PlaylistSourceAccount, PlaylistTrack } from "./api";

export type PlaylistSort = "name" | "tracks" | "coverage" | "updated";
export type TrackSort = "position" | "title" | "duration" | "route";

export function coverage(playlist: Pick<PlaylistLink, "trackCount" | "playableCount">) {
  return playlist.trackCount ? playlist.playableCount / playlist.trackCount : 0;
}

export function filterPlaylists(
  playlists: PlaylistLink[],
  query: string,
  state: "all" | "ready" | "attention" | "paused",
  sort: PlaylistSort,
) {
  const needle = query.trim().toLocaleLowerCase();
  return playlists
    .filter((playlist) => {
      const matchesQuery =
        !needle ||
        `${playlist.name} ${playlist.description ?? ""} ${playlist.sourceProviderId} ${playlist.targetProtocol}`
          .toLocaleLowerCase()
          .includes(needle);
      const matchesState =
        state === "all" ||
        (state === "paused" && !playlist.enabled) ||
        (state === "ready" && playlist.enabled && playlist.unmatchedCount === 0) ||
        (state === "attention" && playlist.enabled && playlist.unmatchedCount > 0);
      return matchesQuery && matchesState;
    })
    .toSorted((left, right) => {
      if (sort === "tracks") return right.trackCount - left.trackCount;
      if (sort === "coverage") return coverage(right) - coverage(left);
      if (sort === "updated")
        return Date.parse(right.lastRunAt ?? "") - Date.parse(left.lastRunAt ?? "");
      return left.name.localeCompare(right.name, undefined, { sensitivity: "base" });
    });
}

export function filterTracks(
  tracks: PlaylistTrack[],
  query: string,
  route: "all" | PlaylistTrack["routeKind"],
  sort: TrackSort,
) {
  const needle = query.trim().toLocaleLowerCase();
  return tracks
    .filter(
      (track) =>
        (route === "all" || track.routeKind === route) &&
        (!needle ||
          `${track.title} ${track.artists.join(" ")} ${track.album ?? ""} ${track.routeProviderId ?? ""}`
            .toLocaleLowerCase()
            .includes(needle)),
    )
    .toSorted((left, right) => {
      if (sort === "title") return left.title.localeCompare(right.title, undefined, { sensitivity: "base" });
      if (sort === "duration") return (right.durationMs ?? -1) - (left.durationMs ?? -1);
      if (sort === "route")
        return (left.routeProviderId ?? left.routeKind).localeCompare(
          right.routeProviderId ?? right.routeKind,
        );
      return left.position - right.position;
    });
}

export function formatDuration(milliseconds?: number | null) {
  if (milliseconds == null) return "—";
  const totalSeconds = Math.max(0, Math.round(milliseconds / 1_000));
  const hours = Math.floor(totalSeconds / 3_600);
  const minutes = Math.floor((totalSeconds % 3_600) / 60);
  const seconds = totalSeconds % 60;
  return hours
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`
    : `${minutes}:${String(seconds).padStart(2, "0")}`;
}

export function providerColor(providerId: string) {
  const colors: Record<string, string> = {
    unresolved: "var(--color-ink-muted)",
    unmatched: "var(--color-ink-muted)",
    jellyfin: "#aa5cc3",
    spotify: "#1ed760",
    applemusic: "#fa243c",
    "apple-music": "#fa243c",
    "apple-download": "#fa243c",
    deezer: "#a238ff",
    qobuz: "#0070ef",
    soundcloud: "#ff5500",
    "youtube-music": "#ff0033",
  };
  const normalized = providerId.toLowerCase();
  if (colors[normalized]) return colors[normalized];
  let hash = 0;
  for (const character of normalized) hash = (hash * 31 + character.codePointAt(0)!) >>> 0;
  return `hsl(${hash % 360} 72% 58%)`;
}

export function orderPlaylistSources(
  accounts: PlaylistSourceAccount[],
  providerOrder: string[],
) {
  const providers = new Map(providerOrder.map((id, index) => [id.toLowerCase(), index]));
  const rank = (id: string) =>
    ["jellyfin", "subsonic"].includes(id.toLowerCase()) ? 0 :
      id.toLowerCase() === "spotify" ? 1 : 2;
  return accounts.toSorted((left, right) =>
    rank(left.providerId) - rank(right.providerId) ||
    (providers.get(left.providerId.toLowerCase()) ?? Number.MAX_SAFE_INTEGER) -
      (providers.get(right.providerId.toLowerCase()) ?? Number.MAX_SAFE_INTEGER) ||
    left.displayName.localeCompare(right.displayName));
}
