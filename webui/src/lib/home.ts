import type {
  NowPlayingItem,
  ProviderDefinition,
  ProviderSummary,
  RuntimeStatus,
  HomeOverview,
  ActivityTask,
  ActivityItem,
  SignInExpiredAccount,
} from "./api";
import { formatPlaybackSource } from "./sources";
import { isProblem, isRoutineActivity } from "./activity";

export type HomeSnapshot = {
  providerCatalog?: ProviderDefinition[];
  status?: RuntimeStatus;
  activity?: HomeOverview["activity"]["items"];
  providers?: ProviderSummary[];
  nowPlaying?: NowPlayingItem[];
  stats?: HomeOverview["stats"];
  signInExpired?: SignInExpiredAccount[];
  failures: string[];
};

// Home shows what changed: routine and waiting work stays in Activity, and a repeated result shows once.
export function recentHomeActivity(items: ActivityItem[]) {
  const seen = new Set<string>();
  return items.toSorted((a, b) => b.occurredAt.localeCompare(a.occurredAt)).filter(item => {
    if (isRoutineActivity(item)) return false;
    if (!isProblem(item) && ["pending", "waiting", "deferred", "retryscheduled"].includes(item.state.toLowerCase().replaceAll(/[-_ ]/g, ""))) return false;
    const key = JSON.stringify([item.actorUserId, item.providerId, item.source, item.label, item.displayName, item.state, item.detail]);
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  }).slice(0, 4);
}

export function listenTrend(current: number, previous: number) {
  const change = current - previous;
  return change ? `${change > 0 ? "+" : "−"}${Math.abs(change).toLocaleString()} on last week` : "same as last week";
}

// Apps that kept their old client name when they were renamed, so pins and settings saved under it survive.
const renamedApps: Record<string, string> = { Aloud: "Songbird" };
export function musicAppName(client: string): string;
export function musicAppName(client: string | null | undefined): string | null | undefined;
export function musicAppName(client: string | null | undefined) {
  return client ? renamedApps[client] ?? client : client;
}

export function playbackMeta(
  item: Pick<NowPlayingItem, "userName" | "client" | "device" | "sourceConfirmed" | "providerId" | "catalogProviderId" | "cached">,
  providers: Pick<ProviderDefinition, "id" | "name">[],
) {
  const source = formatPlaybackSource(providers, item.providerId,
    { confirmed: item.sourceConfirmed === true }).text;
  return [`${item.userName} on ${musicAppName(item.client)}${item.device ? `, ${item.device}` : ""}`, source].filter(Boolean).join(" · ");
}

export function nextScheduledWork(tasks: ActivityTask[]) {
  const next = tasks.filter(task => task.enabled && task.nextRunAt && Number.isFinite(Date.parse(task.nextRunAt)))
    .toSorted((a, b) => Date.parse(a.nextRunAt!) - Date.parse(b.nextRunAt!))[0];
  if (!next) return null;
  const syncs = tasks.filter(task => task.enabled && task.runPlaylistLinkId && task.nextRunAt === next.nextRunAt);
  return { name: next.runPlaylistLinkId ? `${syncs.length} playlist sync${syncs.length === 1 ? "" : "s"}` : next.name, at: next.nextRunAt! };
}

export function homeSourceHealth(snapshot: HomeSnapshot) {
  const checked = (snapshot.providers ?? []).filter(source => source.capabilityTotal > 0);
  const failing = checked.filter(source => source.failedCapabilityCount > 0);
  const slow = failing.filter(source => /(?:^|[_-])slow(?:$|[_-])/.test(source.lastFailureCode ?? ""));
  const down = failing.filter(source => !slow.includes(source));
  return { checked, failing, slow, down };
}

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
