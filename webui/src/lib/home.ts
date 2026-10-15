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

export function playbackSourceIssues(providers: ProviderDefinition[]) {
  return providers.flatMap((provider) => {
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
