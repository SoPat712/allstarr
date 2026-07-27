import type { PlaylistLink, PlaylistTrack } from "./api";

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
  let hash = 0;
  for (const character of providerId) hash = (hash * 31 + character.codePointAt(0)!) >>> 0;
  return `hsl(${hash % 360} 72% 58%)`;
}

export function summarizeRoutes(tracks: PlaylistTrack[], targetProtocol: string) {
  const counts = new Map<string, number>();
  for (const track of tracks) {
    const providerId =
      track.routeProviderId ??
      (track.routeKind === "local"
        ? targetProtocol
        : track.routeKind === "external"
          ? "external"
          : "unresolved");
    counts.set(providerId, (counts.get(providerId) ?? 0) + 1);
  }
  return [...counts].map(([providerId, count]) => ({ providerId, count }));
}
