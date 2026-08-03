import type { PlaylistLink, PlaylistSourceAccount, PlaylistTrack } from "./api";

export type PlaylistSort = "name" | "tracks" | "coverage" | "updated";
export type TrackSort = "position" | "title" | "duration" | "route";
export type TrackRouteFilter = "all" | PlaylistTrack["routeKind"] | "review";

export function playlistDestinationOptions(
  targetName = "your media server",
  playlistName = `the selected ${targetName} playlist`,
  sourcePlaylistName = "this playlist",
) {
  return [
    { id: "virtual", label: "Show only through Allstarr", description: `Allstarr will show ${sourcePlaylistName} and will not create or change a playlist in ${targetName}.` },
    { id: "materialized", label: `Add songs to ${playlistName} in ${targetName}`, description: `Allstarr will add playable ${sourcePlaylistName} songs to ${playlistName}, without showing another copy.` },
    { id: "hybrid", label: `Show through Allstarr and add songs to ${playlistName}`, description: `Allstarr will show every ${sourcePlaylistName} song and add the playable ones to ${playlistName} in ${targetName}.` },
  ] as const;
}

export function playlistProjectionOptions(
  sourceName = "the source service",
  targetName = "your media server",
  playlistName = `the selected ${targetName} playlist`,
) {
  const targetLabel = playlistName.startsWith("the selected ")
    ? `${targetName} playlist`
    : `${playlistName} in ${targetName}`;
  return [
    { id: "resolved", label: `${targetName} when available`, description: `Listeners get songs from ${targetName} when available and the original ${sourceName} version for anything else.` },
    { id: "source", label: `Every song from ${sourceName}`, description: `Keep the songs and order from ${sourceName}, even when a song is not in ${targetName}.` },
    { id: "target", label: targetLabel, description: `Show exactly the songs currently in ${playlistName}.` },
  ] as const;
}

export function playlistBehaviorSummary(
  mode: "virtual" | "materialized" | "hybrid",
  materializationMode: "reconcile" | "recreate",
  sourcePlaylistName: string,
  targetName: string,
  targetPlaylistName: string,
  cadence?: string,
) {
  if (mode === "virtual")
    return cadence
      ? `Allstarr will refresh ${sourcePlaylistName} through Allstarr ${cadence} and will not create or change a playlist in ${targetName}.`
      : `Allstarr will refresh ${sourcePlaylistName} only when you run an update and will not create or change a playlist in ${targetName}.`;

  const visibility = mode === "hybrid"
    ? ` Allstarr will also show every song from ${sourcePlaylistName} to listeners.`
    : " It will not show a second playlist through Allstarr.";
  if (materializationMode === "recreate")
    return `Allstarr will create a new playlist in ${targetName} ${cadence ?? "when you run an update"} instead of changing ${targetPlaylistName}.${visibility}`;
  return cadence
    ? `Allstarr will keep ${targetPlaylistName} in ${targetName} updated ${cadence} with songs from ${sourcePlaylistName} that ${targetName} can play.${visibility}`
    : `Allstarr will add songs from ${sourcePlaylistName} to ${targetPlaylistName} in ${targetName} only when you run an update. It will not keep that playlist updated automatically.${visibility}`;
}

export function playlistOutcomeLabel(code?: string | null, targetName = "the media server playlist") {
  const labels: Record<string, string> = {
    included_native_backend_item: `Will be included in ${targetName}`,
    included_same_provider_identity: `Will be included in ${targetName}`,
    skipped_external_only_for_backend: `Not added to ${targetName}: only available from its original service`,
    skipped_cross_provider_identity: `Not added to ${targetName}: matched to a different service`,
    skipped_unresolved: `Not added to ${targetName}: no playable match yet`,
    skipped_rejected: `Not added to ${targetName}: match was rejected`,
    skipped_duplicate: `Not added twice to ${targetName}`,
    skipped_wrong_backend_or_library: `Not added to ${targetName}: belongs to a different library`,
    skipped_stale_revision: `Not added to ${targetName}: the source changed`,
  };
  return code ? labels[code] ?? `Eligibility for ${targetName} is unavailable` : "Eligibility unavailable";
}

export function isReviewTrack(track: Pick<PlaylistTrack, "matchState">) {
  return track.matchState === "suggested" || track.matchState === "ambiguous";
}

export function confirmationCoverage(
  playlist: Pick<PlaylistLink, "trackCount" | "matchedCount">,
) {
  return playlist.trackCount ? playlist.matchedCount / playlist.trackCount : 0;
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
        (state === "ready" &&
          playlist.enabled &&
          playlist.unmatchedCount === 0 &&
          playlist.metrics.review === 0 &&
          playlist.metrics.rejected === 0) ||
        (state === "attention" &&
          playlist.enabled &&
          (playlist.unmatchedCount > 0 ||
            playlist.metrics.review > 0 ||
            playlist.metrics.rejected > 0));
      return matchesQuery && matchesState;
    })
    .toSorted((left, right) => {
      if (sort === "tracks") return right.trackCount - left.trackCount;
      if (sort === "coverage")
        return confirmationCoverage(right) - confirmationCoverage(left);
      if (sort === "updated")
        return Date.parse(right.lastRunAt ?? "") - Date.parse(left.lastRunAt ?? "");
      return left.name.localeCompare(right.name, undefined, { sensitivity: "base" });
    });
}

export function filterTracks(
  tracks: PlaylistTrack[],
  query: string,
  route: TrackRouteFilter,
  sort: TrackSort,
) {
  const needle = query.trim().toLocaleLowerCase();
  return tracks
    .filter(
      (track) =>
        (route === "all" || (route === "review" ? isReviewTrack(track) : track.routeKind === route)) &&
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
      return left.sourcePosition - right.sourcePosition;
    });
}

export function scheduleCadence(cronExpression: string) {
  const known: Record<string, string> = {
    "0 * * * *": "Every hour",
    "0 3 * * *": "Every day at 03:00",
    "0 3 * * 1": "Every Monday at 03:00",
  };
  return known[cronExpression] ?? `Cron ${cronExpression}`;
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
    jellyfin: "#8b65fb",
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

export async function runBounded<T>(
  items: T[],
  concurrency: number,
  task: (item: T) => Promise<void>,
  progress?: (completed: number, total: number) => void,
) {
  const results = Array<PromiseSettledResult<void>>(items.length);
  let next = 0;
  let completed = 0;
  await Promise.all(
    Array.from({ length: Math.min(Math.max(1, concurrency), items.length) }, async () => {
      while (next < items.length) {
        const index = next++;
        try {
          await task(items[index]);
          results[index] = { status: "fulfilled", value: undefined };
        } catch (reason) {
          results[index] = { status: "rejected", reason };
        }
        progress?.(++completed, items.length);
      }
    }),
  );
  return results;
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
