import type {
  ActivityItem,
  Job,
  PlaylistLink,
  PlaylistResponse,
  ProviderDefinition,
  ProviderSummary,
  RuntimeStatus,
} from "./api";

export type HomeSnapshot = {
  providerCatalog?: ProviderDefinition[];
  status?: RuntimeStatus;
  playlists?: PlaylistResponse;
  playlistLinks?: PlaylistLink[];
  jobs?: Job[];
  activity?: ActivityItem[];
  providers?: ProviderSummary[];
  failures: string[];
};

export function summarizeHome(snapshot: HomeSnapshot) {
  const playlists = snapshot.playlists?.playlists ?? [];
  const links = snapshot.playlistLinks;
  const activeJobs = (snapshot.jobs ?? []).filter(
    (job) => !["Succeeded", "Failed", "Cancelled"].includes(job.state),
  ).length;

  return {
    activeJobs,
    managed: links?.length ?? snapshot.playlists?.inventory.managed ?? playlists.length,
    unmanaged: snapshot.playlists?.inventory.unmanaged ?? 0,
    playable: links
      ? links.reduce((total, playlist) => total + playlist.playableCount, 0)
      : playlists.reduce(
          (total, playlist) => total + playlist.localTracks + playlist.externalTracks,
          0,
        ),
    unresolved: links
      ? links.reduce((total, playlist) => total + playlist.unmatchedCount, 0)
      : playlists.reduce((total, playlist) => total + playlist.unmatchedTracks, 0),
  };
}
