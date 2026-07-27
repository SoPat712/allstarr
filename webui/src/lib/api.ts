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
  kind: string;
  source: string;
  label: string;
  state: string;
  detail: string;
  occurredAt: string;
  correlationId?: string | null;
  severity?: string;
  providerId?: string | null;
  playlistLinkId?: string | null;
  playlistName?: string | null;
  artworkUrl?: string | null;
  sourceTitle?: string | null;
  sourceArtist?: string | null;
  sourceAlbum?: string | null;
  targetProviderId?: string | null;
  targetTitle?: string | null;
  targetArtist?: string | null;
  confidenceLabel?: string | null;
  isrc?: string | null;
  sourceProviderTrackId?: string | null;
  targetProviderTrackId?: string | null;
  backendItemId?: string | null;
  routeDecisionId?: string | null;
  actorUserId?: string | null;
  action?: string | null;
  durationMilliseconds?: number | null;
  technicalDetails?: Record<string, string> | null;
};

export type ActivityResponse = {
  items: ActivityItem[];
  hasMore: boolean;
  nextCursor?: string | null;
  nextCursorId?: string | null;
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
  categories?: string[];
  status?: string;
  runtimeCapabilities?: Array<{
    id: string;
    ready: boolean;
    canAttempt: boolean;
  }>;
  capabilityRoutes?: Array<{ capabilities: string[] }>;
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

export type MatchCandidate = {
  libraryTrackId?: string | null;
  backendItemId?: string | null;
  confidence?: number | null;
  title?: string | null;
  artist?: string | null;
  album?: string | null;
  durationMilliseconds?: number | null;
  sourceIsrc?: string | null;
  candidateIsrc?: string | null;
  artistOverlap?: number | null;
  albumEvidence?: number | null;
  durationDeltaMilliseconds?: number | null;
  providerTrackIds?: Record<string, string> | null;
  components?: Record<string, number> | null;
  reasons?: string[] | null;
  warnings?: string[] | null;
};

export type MatchReviewItem = {
  externalSnapshotId: string;
  providerId: string;
  providerAccountId?: string | null;
  libraryScopeId: string;
  state: string;
  decisionSource: string;
  confidence?: number | null;
  threshold?: number | null;
  decisionVersion?: number | null;
  algorithmVersion?: string | null;
  policyVersion?: string | null;
  sourceSnapshotVersion?: number | null;
  libraryIndexRevision?: number | null;
  canonicalRecordingId?: string | null;
  libraryTrackId?: string | null;
  overrideId?: string | null;
  overrideRevision?: number | null;
  title?: string | null;
  artist?: string | null;
  album?: string | null;
  artworkUrl?: string | null;
  sourceArtworkUrl?: string | null;
  candidateArtworkUrl?: string | null;
  isrc?: string | null;
  durationMilliseconds?: number | null;
  localTrack?: {
    id: string;
    backendItemId: string;
    title: string;
    artist?: string | null;
    album?: string | null;
    durationMilliseconds?: number | null;
    artworkUrl?: string | null;
    providerIds?: Record<string, string>;
  } | null;
  providerIdentities: Array<{
    providerId: string;
    externalId: string;
    scope: string;
    verification: string;
  }>;
  candidates: MatchCandidate[];
  reasons: string[];
  warnings: string[];
  decidedAt?: string | null;
  reviewedAt?: string | null;
};

export type MatchReviewResponse = {
  matches: MatchReviewItem[];
  stats: {
    total: number;
    matched: number;
    accepted: number;
    unresolved: number;
    review: number;
    rejected: number;
    attention: number;
  };
  pagination: { page: number; pageSize: number; total: number; totalPages: number };
};

export type MatchTarget = {
  id: string;
  backendItemId?: string | null;
  externalId?: string | null;
  externalProvider?: string | null;
  title: string;
  artist?: string | null;
  album?: string | null;
  artworkUrl?: string | null;
  durationMilliseconds?: number | null;
  isrc?: string | null;
};

export type ManagedDownload = {
  path: string;
  storage: string;
  artist: string;
  album: string;
  title: string;
  fileName: string;
  size: number;
  sizeFormatted: string;
  lastModified: string;
  codec: string;
  bitrateKbps?: number | null;
  sampleRateHz?: number | null;
  bitDepth?: number | null;
  channels?: number | null;
  durationMilliseconds?: number | null;
  quality: string;
  provider?: string | null;
  externalId?: string | null;
  artworkUrl?: string | null;
};

export type DownloadsResponse = {
  storage: string;
  files: ManagedDownload[];
  totalSize: number;
  totalSizeFormatted: string;
  count: number;
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

  if (response.status === 204) return undefined as T;
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
  schema: () =>
    json<{ activeBackend: string; providers: ProviderDefinition[] }>("/api/admin/ui/schema"),
  status: () => json<RuntimeStatus>("/api/admin/status"),
  playlists: () => json<PlaylistResponse>("/api/admin/playlists"),
  jobs: () => json<{ jobs: Job[] }>("/api/admin/jobs?limit=100"),
  activity: () => json<ActivityResponse>("/api/admin/ui/activity?limit=8"),
  providers: () => json<{ providers: ProviderSummary[] }>("/api/admin/ui/provider-summaries"),
};

export const eventLog = {
  list: (params: { limit?: number; before?: string; beforeId?: string } = {}) => {
    const query = new URLSearchParams({ limit: String(params.limit ?? 50) });
    if (params.before) query.set("before", params.before);
    if (params.beforeId) query.set("beforeId", params.beforeId);
    return json<ActivityResponse>(`/api/admin/ui/activity?${query}`);
  },
};

export const downloads = {
  list: (storage: "cache" | "kept") =>
    json<DownloadsResponse>(`/api/admin/downloads?storage=${storage}`),
  keep: (path: string) =>
    json<{ success: boolean }>(
      `/api/admin/downloads/promote?path=${encodeURIComponent(path)}`,
      { method: "POST" },
    ),
  remove: (path: string, storage: "cache" | "kept") =>
    json<{ success: boolean }>(
      `/api/admin/downloads?path=${encodeURIComponent(path)}&storage=${storage}`,
      { method: "DELETE" },
    ),
  removeAll: (storage: "cache" | "kept") =>
    json<{ success: boolean; deletedCount: number }>(
      `/api/admin/downloads/all?storage=${storage}`,
      { method: "DELETE" },
    ),
  fileUrl: (path: string, storage: "cache" | "kept") =>
    `/api/admin/downloads/file?path=${encodeURIComponent(path)}&storage=${storage}`,
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

export const matchReview = {
  list: (params: {
    page?: number;
    pageSize?: number;
    search?: string;
    state?: string;
    libraryScopeId?: string;
  }) => {
    const query = new URLSearchParams();
    for (const [key, value] of Object.entries(params)) {
      if (value !== undefined && value !== "") query.set(key, String(value));
    }
    return json<MatchReviewResponse>(`/api/admin/track-matches?${query}`);
  },
  searchLocal: (query: string, libraryScopeId: string) =>
    json<{ tracks: MatchTarget[] }>(
      `/api/admin/track-matches/targets/local?query=${encodeURIComponent(query)}&libraryScopeId=${encodeURIComponent(libraryScopeId)}`,
    ),
  searchProviders: (query: string, libraryScopeId: string, provider = "") => {
    const params = new URLSearchParams({ query, libraryScopeId, limit: "50" });
    if (provider) params.set("provider", provider);
    return json<{ tracks: MatchTarget[]; providers: string[] }>(
      `/api/admin/track-matches/targets/provider?${params}`,
    );
  },
  resolve: (
    externalSnapshotId: string,
    target:
      | { targetType: "local"; libraryTrackId: string; reason: string }
      | { targetType: "provider"; externalProvider: string; externalId: string; reason: string }
      | { targetType: "reject"; reason: string },
  ) =>
    json<{ success: boolean }>(
      `/api/admin/track-matches/${encodeURIComponent(externalSnapshotId)}/resolve`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(target),
      },
    ),
  rematch: (externalSnapshotId: string) =>
    json<{ rematched: boolean; state: string }>(
      `/api/admin/track-matches/${encodeURIComponent(externalSnapshotId)}/rematch`,
      { method: "POST" },
    ),
  clear: (overrideId: string, expectedRevision: number) =>
    json<void>(
      `/api/admin/playlist-links/matches/overrides/${encodeURIComponent(overrideId)}?expectedRevision=${expectedRevision}`,
      { method: "DELETE" },
    ),
};
