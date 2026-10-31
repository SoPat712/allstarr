import type { MixCard, PlaylistLink, PlaylistSourceAccount, PlaylistTrack } from "./api";

export function isServiceMix(link: Pick<PlaylistLink, "sourceProviderId" | "sourcePlaylistId" | "importMode">) {
  return link.importMode === "linked" && (link.sourceProviderId === "lastfm" && (["loved", "similar"].includes(link.sourcePlaylistId) || link.sourcePlaylistId.startsWith("top:"))
    || link.sourceProviderId === "listenbrainz" && link.sourcePlaylistId.startsWith("createdfor:"));
}

export type PlaylistMix = MixCard & { trackCount: number | null };

export type PlaylistSort = "name" | "tracks" | "coverage" | "updated";
export type TrackSort = "position" | "title" | "duration" | "route";
export type TrackRouteFilter = "all" | PlaylistTrack["routeKind"] | "review";
export type PlaylistImportMode = "oneTime" | "linked";
export type PlaylistTargetMode = "new" | "existing";

export function playlistBehaviorSummary(
  importMode: PlaylistImportMode,
  targetMode: PlaylistTargetMode,
  sourcePlaylistName: string,
  targetName: string,
  targetPlaylistName: string,
  cadence?: string,
  trackRetention: "onDemand" | "keepAll" = "onDemand",
) {
  const retention = trackRetention === "keepAll"
    ? " Allstarr will also keep a copy of every song it can download."
    : "";
  const destination = targetMode === "new"
    ? `a new playlist in ${targetName}`
    : `${targetPlaylistName} in ${targetName}`;
  const targetAction = targetMode === "new"
    ? `create ${destination} with its playable songs`
    : `update ${destination} with its playable songs`;
  if (importMode === "oneTime") {
    return `Allstarr will import ${sourcePlaylistName} once, show it through Allstarr, and ${targetAction}. It will not check the source for later changes.${retention}`;
  }
  const timing = cadence ? ` ${cadence}` : " when you run an update";
  return `Allstarr will keep ${sourcePlaylistName} in sync${timing} and ${targetAction}. It will also show every song from ${sourcePlaylistName} through Allstarr.${retention}`;
}

export function playlistOutcomeLabel(code?: string | null, targetName = "the media server playlist") {
  const labels: Record<string, string> = {
    included_native_backend_item: `Will be included in ${targetName}`,
    streamed_through_allstarr: "Streams through Allstarr",
    included_same_provider_identity: `Will be included in ${targetName}`,
    skipped_external_only_for_backend: `Not added to ${targetName}: only available from its original service`,
    skipped_cross_provider_identity: `Not added to ${targetName}: matched to a different service`,
    skipped_unresolved: `Not added to ${targetName}: no playable match yet`,
    skipped_rejected: `Not added to ${targetName}: match was rejected`,
    skipped_duplicate: `Duplicate: not added twice to ${targetName}`,
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

export function filterPlaylists<T extends PlaylistLink | PlaylistMix>(
  playlists: T[],
  query: string,
  state: "all" | "ready" | "attention" | "paused",
  sort: PlaylistSort,
) {
  const needle = query.trim().toLocaleLowerCase();
  return playlists
    .filter((playlist) => {
      const matchesQuery =
        !needle ||
        `${playlist.name} ${playlist.description ?? ""} ${"inApps" in playlist ? "" : `${playlist.sourceProviderId} ${playlist.targetProtocol}`}`
          .toLocaleLowerCase()
          .includes(needle);
      if ("inApps" in playlist) return matchesQuery && state === "all";
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
      if (sort === "tracks") return (right.trackCount ?? -1) - (left.trackCount ?? -1);
      if (sort === "coverage")
        return ("inApps" in right ? 0 : confirmationCoverage(right)) - ("inApps" in left ? 0 : confirmationCoverage(left));
      if (sort === "updated")
        return Date.parse(("inApps" in right ? right.updatedAt : right.lastRunAt) ?? "") - Date.parse(("inApps" in left ? left.updatedAt : left.lastRunAt) ?? "");
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
          `${track.title} ${track.artists.join(" ")} ${track.album ?? ""} ${track.routeProviderId ?? ""} ${track.providerRoutes.map((item) => item.providerId).join(" ")}`
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

const weekdayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

function cronNumber(value: string, max: number) {
  return /^\d+$/.test(value) && Number(value) <= max ? Number(value) : null;
}

function cronWeekdays(value: string) {
  const days = new Set<number>();
  for (const part of value.split(",")) {
    const range = /^(\d)(?:-(\d))?$/.exec(part);
    if (!range) return null;
    const start = Number(range[1]), end = Number(range[2] ?? range[1]);
    if (start > 7 || end > 7 || end < start) return null;
    for (let day = start; day <= end; day++) days.add(day % 7);
  }
  return [...days].sort((left, right) => left - right);
}

function timeOfDay(hour: number, minute: number) {
  return new Date(Date.UTC(2000, 0, 1, hour, minute)).toLocaleTimeString(undefined, {
    hour: "numeric", minute: "2-digit", timeZone: "UTC",
  });
}

export function scheduleCadence(cronExpression: string) {
  const custom = `Custom schedule (${cronExpression})`;
  const parts = cronExpression.trim().split(/\s+/);
  if (parts.length !== 5) return custom;
  const [minuteText, hourText, dayOfMonth, month, dayOfWeek] = parts;
  const minute = cronNumber(minuteText, 59);
  if (minute === null || dayOfMonth !== "*" || month !== "*") return custom;
  if (hourText === "*" && dayOfWeek === "*")
    return minute === 0 ? "Every hour" : `Every hour at ${String(minute).padStart(2, "0")} minutes past`;
  const interval = /^\*\/(\d+)$/.exec(hourText);
  if (interval && dayOfWeek === "*" && minute === 0) return `Every ${interval[1]} hours`;
  const hour = cronNumber(hourText, 23);
  if (hour === null) return custom;
  const at = timeOfDay(hour, minute);
  if (dayOfWeek === "*") return `Every day at ${at}`;
  const days = cronWeekdays(dayOfWeek);
  if (!days?.length) return custom;
  if (days.join() === "1,2,3,4,5") return `Weekdays at ${at}`;
  if (days.join() === "0,6") return `Weekends at ${at}`;
  const names = days.map((day) => weekdayNames[day]);
  const list = names.length === 1 ? names[0] : `${names.slice(0, -1).join(", ")} and ${names.at(-1)}`;
  return `Every ${list} at ${at}`;
}

// The zone only needs naming when it differs from the viewer's own.
export function scheduleZoneLabel(timeZone?: string | null) {
  if (!timeZone) return "";
  try {
    if (timeZone === Intl.DateTimeFormat().resolvedOptions().timeZone) return "";
    return new Intl.DateTimeFormat(undefined, { timeZone, timeZoneName: "short" })
      .formatToParts(new Date()).find((part) => part.type === "timeZoneName")?.value ?? timeZone;
  } catch {
    return timeZone;
  }
}

const mediaServerNames: Record<string, string> = {
  jellyfin: "Jellyfin",
  subsonic: "Subsonic",
  opensubsonic: "OpenSubsonic",
  navidrome: "Navidrome",
};

export function mediaServerName(protocol?: string | null) {
  return protocol ? mediaServerNames[protocol.toLowerCase()] ?? null : null;
}

export function listeningTime(milliseconds?: number | null) {
  if (milliseconds == null) return "length unknown";
  const minutes = Math.round(milliseconds / 60_000);
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60), rest = minutes % 60;
  return rest ? `${hours} hr ${rest} min` : `${hours} hr`;
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
    jellyfin: "var(--color-signal)",
    subsonic: "var(--color-signal)",
    opensubsonic: "var(--color-signal)",
    navidrome: "var(--color-signal)",
    spotify: "#1ed760",
    applemusic: "#fa243c",
    "apple-music": "#fa243c",
    "apple-download": "#fa243c",
    "apple-musickit": "#fa243c",
    deezer: "#ff0092",
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
