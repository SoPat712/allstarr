export type Session = {
  authenticated: boolean;
  backend: string;
  user?: {
    id: string;
    name: string;
    isAdministrator: boolean;
    avatarUrl?: string | null;
  };
};

export type RuntimeStatus = {
  version: string;
  backendType: string;
  durableStorage?: {
    provider: string;
    readiness: string;
    errorCode?: string | null;
    checkedAt?: string;
  };
};

export type PlaylistSummary = {
  id: string;
  name: string;
  trackCount: number;
  localTracks: number;
  externalTracks: number;
  unmatchedTracks: number;
  artworkUrl?: string | null;
  sourceProvider?: string;
};

export type PlaylistResponse = {
  playlists: PlaylistSummary[];
  inventory: {
    managed: number;
    unmanaged: number;
  };
};

export type Job = {
  id: string;
  type: string;
  state: string;
  updatedAt: string;
};

export type ActivityItem = {
  id: string;
  source: string;
  label: string;
  state: string;
  detail: string;
  occurredAt: string;
};

export type ProviderSummary = {
  providerId: string;
  connectedAccountName?: string | null;
  enabledAccountCount: number;
  capabilityTotal: number;
  healthyCapabilityCount: number;
  failedCapabilityCount: number;
  lastCheckedAt?: string | null;
};

export type ProviderDefinition = {
  id: string;
  name: string;
  logoUrl?: string | null;
};

export type PlaylistLinkMetrics = {
  total: number;
  matched: number;
  unresolved: number;
  review: number;
  rejected: number;
  playable: number;
  materialized: number;
  snapshotId?: string | null;
  snapshotVersion?: number | null;
};

export type PlaylistLink = {
  id: string;
  enabled: boolean;
  name: string;
  description?: string | null;
  artworkUrl?: string | null;
  sourceProviderId: string;
  targetProtocol: string;
  materializationMode: string;
  revision: number;
  lastRunAt?: string | null;
  lastRunState?: string | null;
  trackCount: number;
  matchedCount: number;
  unmatchedCount: number;
  playableCount: number;
  materializedCount: number;
  metrics: PlaylistLinkMetrics;
};

export type PlaylistTrack = {
  position: number;
  externalSnapshotId: string;
  title: string;
  artists: string[];
  album?: string | null;
  isrc?: string | null;
  durationMs?: number | null;
  durationProvenance?: string | null;
  artworkUrl?: string | null;
  backendItemId?: string | null;
  routeKind: "local" | "external" | "unmatched";
  routeProviderId?: string | null;
  matchState?: string | null;
  providerRoutes: Array<{ providerId: string; externalId: string; pinned: boolean }>;
};

export type PlaylistDetails = {
  id: string;
  snapshotId: string;
  snapshotVersion: number;
  name: string;
  sourceProviderId: string;
  targetProtocol: string;
  targetPlaylistId?: string | null;
  artworkUrl?: string | null;
  retrievedAt: string;
  completedAt?: string | null;
  syncState?: string | null;
  trackCount: number;
  localCount: number;
  externalCount: number;
  unresolvedCount: number;
  durationMs?: number | null;
  unknownDurationCount: number;
  tracks: PlaylistTrack[];
};

async function json<T>(input: RequestInfo | URL, init?: RequestInit): Promise<T> {
  const response = await fetch(input, {
    cache: "no-store",
    credentials: "same-origin",
    ...init,
  });

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { error?: string } | null;
    throw new Error(body?.error || `${response.status} ${response.statusText}`);
  }

  return response.json() as Promise<T>;
}

export const auth = {
  session: () => json<Session>("/api/admin/auth/me"),
  login: (username: string, password: string, rememberMe: boolean) =>
    json<Session>("/api/admin/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password, rememberMe }),
    }),
  logout: () => json<{ success: boolean }>("/api/admin/auth/logout", { method: "POST" }),
};

export const home = {
  schema: () => json<{ providers: ProviderDefinition[] }>("/api/admin/ui/schema"),
  status: () => json<RuntimeStatus>("/api/admin/status"),
  playlists: () => json<PlaylistResponse>("/api/admin/playlists"),
  jobs: () => json<{ jobs: Job[] }>("/api/admin/jobs?limit=100"),
  activity: () => json<{ items: ActivityItem[] }>("/api/admin/ui/activity?limit=8"),
  providers: () => json<{ providers: ProviderSummary[] }>("/api/admin/ui/provider-summaries"),
};

export const playlistLinks = {
  list: () => json<{ playlistLinks: PlaylistLink[] }>("/api/admin/playlist-links"),
  details: (id: string) => json<PlaylistDetails>(`/api/admin/playlist-links/${encodeURIComponent(id)}`),
  sync: (id: string, snapshotId: string) =>
    json<{ jobId: string; created: boolean }>(`/api/admin/playlist-links/${encodeURIComponent(id)}/run`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ snapshotId }),
    }),
  rematch: (id: string) =>
    json<{ preview: unknown }>(`/api/admin/playlist-links/${encodeURIComponent(id)}/refresh`, {
      method: "POST",
    }),
  setEnabled: (id: string, expectedRevision: number, enabled: boolean) =>
    json<{ id: string; enabled: boolean; revision: number }>(
      `/api/admin/playlist-links/${encodeURIComponent(id)}/state`,
      {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ expectedRevision, enabled }),
      },
    ),
};
