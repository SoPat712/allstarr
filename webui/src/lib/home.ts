import type {
  NowPlayingItem,
  ProviderDefinition,
  ProviderSummary,
  RuntimeStatus,
  HomeOverview,
} from "./api";

export type HomeSnapshot = {
  providerCatalog?: ProviderDefinition[];
  status?: RuntimeStatus;
  activity?: HomeOverview["activity"]["items"];
  providers?: ProviderSummary[];
  nowPlaying?: NowPlayingItem[];
  stats?: HomeOverview["stats"];
  failures: string[];
};

export function playbackProgress(
  item: Pick<NowPlayingItem, "positionSeconds" | "durationSeconds" | "isPaused" | "positionUpdatedAt">,
  clock: { capturedAt: string; elapsedSeconds: number } | null,
) {
  const position = typeof item.positionSeconds === "number" && Number.isFinite(item.positionSeconds) && item.positionSeconds >= 0
    ? item.positionSeconds : null;
  const duration = typeof item.durationSeconds === "number" && Number.isFinite(item.durationSeconds) && item.durationSeconds > 0
    ? item.durationSeconds : null;
  let seconds = position;
  let state: "reported" | "paused" | "estimated" | "stale" = item.isPaused === true ? "paused" : "reported";
  let advancing = false;
  if (position !== null && item.isPaused === false && item.positionUpdatedAt && clock) {
    const age = (Date.parse(clock.capturedAt) - Date.parse(item.positionUpdatedAt)) / 1000 + clock.elapsedSeconds;
    if (Number.isFinite(age) && Number.isFinite(clock.elapsedSeconds) && clock.elapsedSeconds >= 0 && age >= 0) {
      seconds = position + Math.min(age, 15, duration === null ? Infinity : Math.max(0, duration - position));
      state = age >= 15 ? "stale" : "estimated";
      advancing = age < 15 && (duration === null || seconds < duration);
    }
  }
  return { seconds, progress: seconds !== null && duration !== null ? Math.min(1, Math.max(0, seconds / duration)) : null, state, advancing };
}

export function playbackSourceIssues(providers: ProviderDefinition[], summaries: ProviderSummary[] = []) {
  return providers.flatMap((provider) => {
    if (summaries.some((item) => item.providerId.toLowerCase() === provider.id.toLowerCase() && item.enabledAccountCount > 0))
      return [];
    const capability = provider.runtimeCapabilities?.find((item) =>
      item.supported !== false &&
      ["streaming", "download"].includes(item.id.toLowerCase()) &&
      !item.canAttempt);
    return capability ? [{
      providerId: provider.id,
      providerName: provider.name,
      reason: capability.reasonCode || capability.configuration || capability.health || "unavailable",
    }] : [];
  });
}

export function summarizeHome(snapshot: HomeSnapshot) {
  return {
    activeJobs: snapshot.stats?.activeJobs ?? 0,
    managed: snapshot.stats?.linkedPlaylists ?? 0,
    playable: snapshot.stats?.playableTracks ?? 0,
    unresolved: snapshot.stats?.unresolvedTracks ?? 0,
  };
}

export function playbackSeekLabel(item: Pick<NowPlayingItem, "sourceConfirmed" | "seekRung">) {
  if (!item.sourceConfirmed) return "Seeking unconfirmed";
  switch (item.seekRung) {
    case "byte-range": return "Byte seeking available";
    case "progressive-cache": return "Seeking available as audio downloads";
    case "flac-duration":
    case "mp3-duration":
    case "mp4-duration": return "Duration supplied · client buffering";
    case "sequential-cache":
    case "sequential": return "Full stream · client buffering";
    default: return "Seeking managed by your music server";
  }
}
