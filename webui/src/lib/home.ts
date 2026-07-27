import type {
  ActivityItem,
  Job,
  PlaylistResponse,
  ProviderDefinition,
  ProviderSummary,
  RuntimeStatus,
} from "./api";

export type HomeSnapshot = {
  providerCatalog?: ProviderDefinition[];
  status?: RuntimeStatus;
  playlists?: PlaylistResponse;
  jobs?: Job[];
  activity?: ActivityItem[];
  providers?: ProviderSummary[];
  failures: string[];
};

export function summarizeHome(snapshot: HomeSnapshot) {
  const playlists = snapshot.playlists?.playlists ?? [];
  const activeJobs = (snapshot.jobs ?? []).filter(
    (job) => !["Succeeded", "Failed", "Cancelled"].includes(job.state),
  ).length;

  return {
    activeJobs,
    managed: snapshot.playlists?.inventory.managed ?? playlists.length,
    unmanaged: snapshot.playlists?.inventory.unmanaged ?? 0,
    playable: playlists.reduce(
      (total, playlist) => total + playlist.localTracks + playlist.externalTracks,
      0,
    ),
    unresolved: playlists.reduce((total, playlist) => total + playlist.unmatchedTracks, 0),
  };
}
