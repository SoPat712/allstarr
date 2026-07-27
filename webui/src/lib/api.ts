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
  attemptCount?: number;
  failureCount?: number;
  deferralCount?: number;
  cancellationRequestedAt?: string | null;
  lastErrorCode?: string | null;
  lastErrorMessage?: string | null;
  updatedAt: string;
};

export type JobProgress = {
  id: string;
  jobId?: string | null;
  action: string;
  outcome: string;
  detailsJson: string;
  createdAt: string;
};

export type JobResponse = { jobs: Job[]; progress: JobProgress[] };

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
  successRate?: number | null;
  p95LatencyMilliseconds?: number | null;
  lastFailureCode?: string | null;
};

export type ProviderSetting = {
  key: string;
  label: string;
  type: string;
  sensitive?: boolean;
  required?: boolean;
  options?: string[];
  helpText?: string | null;
  defaultValueJson?: string | null;
};

export type ProviderRuntimeCapability = {
  id: string;
  configuration?: string;
  supported?: boolean;
  health?: string;
  ready: boolean;
  canAttempt: boolean;
  canTest?: boolean;
  testedAt?: string | null;
  reasonCode?: string | null;
};

export type ProviderDefinition = {
  id: string;
  name: string;
  description?: string | null;
  logoUrl?: string | null;
  categories?: string[];
  status?: string;
  notes?: string[];
  accountSettings?: ProviderSetting[];
  runtimeCapabilities?: ProviderRuntimeCapability[];
  connectionKind?: string | null;
  audience?: string | null;
  implementationOrigin?: string | null;
  routeId?: string | null;
  capabilityRoutes?: Array<{
    routeId?: string;
    name?: string;
    origin?: string;
    capabilities: string[];
  }>;
};

export type UiSchema = {
  activeBackend: string;
  providerAccountManagementMode?: string;
  providers: ProviderDefinition[];
  configSections?: ConfigSection[];
  priorityGroups?: PriorityGroup[];
};

export type ConfigField = {
  key: string;
  label: string;
  type: string;
  valuePath?: string | null;
  options?: string[];
  placeholder?: string | null;
  sensitive?: boolean;
  required?: boolean;
  ownership?: string;
  readOnly?: boolean;
  helpText?: string | null;
  min?: number | null;
  max?: number | null;
};

export type ConfigSection = {
  id: string;
  label: string;
  fields: ConfigField[];
};

export type PriorityGroup = {
  id: string;
  label: string;
  description?: string | null;
  envKey: string;
  enabledEnvKey?: string | null;
  providers: string[];
  pinnedProvider?: {
    id: string;
    name: string;
    icon: string;
    reason: string;
  } | null;
};

export type ProviderAccount = {
  id: string;
  providerId: string;
  displayName: string;
  sourceDisplayName?: string | null;
  scope: "Global" | "User" | "Library";
  ownerUserId?: string | null;
  ownerDisplayName?: string | null;
  createdByUserId?: string | null;
  creatorDisplayName?: string | null;
  libraryScopeId?: string | null;
  enabled: boolean;
  revision: number;
  secret: {
    configured: boolean;
    version?: number | null;
    updatedAt?: string | null;
    revoked: boolean;
  };
  createdAt: string;
  updatedAt: string;
};

export type ProviderHealth = {
  provider: string;
  providerAccountId: string;
  providerAccountName: string;
  capability: string;
  accountScope: string;
  supported: boolean;
  enabled: boolean;
  configuration: string;
  health: string;
  ready: boolean;
  canAttempt: boolean;
  testedAt?: string | null;
  reasonCode?: string | null;
  canTest: boolean;
};

export type ConnectivityResult = {
  success?: boolean;
  healthy?: boolean;
  health?: string;
  latencyMs?: number;
  bars?: number;
  metric?: string;
  testedAt?: string;
  reasonCode?: string | null;
};

export type CtsMeasurement = {
  providerAccountId: string;
  providerId: string;
  health: string;
  latencyMs: number;
  bars: number;
  testedAt: string;
  failureCode?: string | null;
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
  routeCoverage: Array<{ providerId: string; count: number }>;
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
  routeCoverage: Array<{ providerId: string; count: number }>;
  durationMs?: number | null;
  unknownDurationCount: number;
  tracks: PlaylistTrack[];
};

export type PlaylistSourceAccount = {
  id: string;
  providerId: string;
  displayName: string;
  ownerDisplayName?: string | null;
  libraryScopeId?: string | null;
  accessLabel: string;
};

export type PlaylistDiscoveryItem = {
  id: string;
  providerId: string;
  name: string;
  owner?: string | null;
  trackCount?: number | null;
  artworkUrl?: string | null;
};

export type MediaTarget = {
  id: string;
  protocol: "jellyfin" | "subsonic";
  backendInstanceId: string;
  displayName: string;
  credentialReferenceId?: string | null;
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
    suggested: number;
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
    const body = (await response.json().catch(() => null)) as { error?: string; message?: string } | null;
    throw new Error(body?.error || body?.message || `${response.status} ${response.statusText}`);
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
    json<UiSchema>("/api/admin/ui/schema"),
  status: () => json<RuntimeStatus>("/api/admin/status"),
  playlists: () => json<PlaylistResponse>("/api/admin/playlists"),
  jobs: () => json<JobResponse>("/api/admin/jobs?limit=100"),
  cancelJob: (id: string) =>
    json<{ jobId: string; state: string }>(`/api/admin/jobs/${encodeURIComponent(id)}/cancel`, {
      method: "POST",
    }),
  activity: () => json<ActivityResponse>("/api/admin/ui/activity?limit=8"),
  providers: () => json<{ providers: ProviderSummary[] }>("/api/admin/ui/provider-summaries"),
};

export const sources = {
  accounts: () =>
    json<{
      managementMode: string;
      audienceUsers: { id: string; displayName: string }[];
      accounts: ProviderAccount[];
    }>("/api/admin/provider-accounts"),
  health: () => json<ProviderHealth[]>("/api/admin/providers/status"),
  cts: () =>
    json<{ measurements: CtsMeasurement[] }>("/api/admin/provider-diagnostics/deep-stream/latest"),
  create: (input: {
    providerId: string;
    displayName: string;
    scope: string;
    libraryScopeId?: string | null;
    enabled: boolean;
    secret: Record<string, unknown>;
  }) => json<ProviderAccount>("/api/admin/provider-accounts", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(input),
  }),
  authenticateLastFm: (accountId: string, username: string, password: string) =>
    json<{ success: boolean }>("/api/admin/scrobbling/lastfm/authenticate", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ accountId, username, password }),
    }),
  setEnabled: (account: ProviderAccount, enabled: boolean) =>
    json<ProviderAccount>(`/api/admin/provider-accounts/${account.id}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled, expectedRevision: account.revision }),
    }),
  replaceSecret: (account: ProviderAccount, secret: Record<string, unknown>) =>
    json<{ accountId: string }>(`/api/admin/provider-accounts/${account.id}/secret`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ secret }),
    }),
  setAudience: (
    account: ProviderAccount,
    scope: string,
    ownerUserId?: string | null,
    libraryScopeId?: string | null,
  ) =>
    json<ProviderAccount>(`/api/admin/provider-accounts/${account.id}/audience`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ scope, ownerUserId, libraryScopeId, expectedRevision: account.revision }),
    }),
  remove: (id: string) =>
    json<void>(`/api/admin/provider-accounts/${id}`, { method: "DELETE" }),
  test: (account: ProviderAccount, capability?: string) =>
    json<ConnectivityResult>(
      `/api/admin/providers/test/${encodeURIComponent(account.providerId)}${capability ? `/${encodeURIComponent(capability)}` : ""}?accountId=${encodeURIComponent(account.id)}`,
      { method: "POST" },
    ),
  deepStream: (account: ProviderAccount, quality = 0) =>
    json<ConnectivityResult & {
      clickToStreamMilliseconds?: number;
      firstByteMilliseconds?: number;
      sampleBytes?: number;
      throughputKbps?: number;
      cacheState?: string;
      trackLabel?: string;
    }>("/api/admin/provider-diagnostics/deep-stream", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        providerId: account.providerId,
        providerAccountId: account.id,
        quality,
      }),
    }),
};

export const settings = {
  config: () => json<Record<string, unknown>>("/api/admin/config"),
  save: (updates: Record<string, string>) =>
    json<{ message: string; updatedKeys: string[] }>("/api/admin/config", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ updates }),
    }),
  storage: () => json<{
    storage: { provider?: string; readiness?: string; checkedAt?: string };
    backups: Array<{ id: string; status: string; createdAt: string; verifiedAt?: string | null }>;
  }>("/api/admin/storage"),
  backup: () => json<{ id: string; status: string }>("/api/admin/storage/backups", { method: "POST" }),
  cache: () => json<{
    database: { entryCount: number; payloadBytes: number; hitRatio: number };
    hot: { entryCount: number; payloadBytes: number; hitRatio: number };
    media: { entryCount: number; payloadBytes: number; maximumBytes?: number | null; hitRatio: number };
    activity: { coalescedRequests: number; staleServes: number; upstreamBytesAvoided: number };
    extensionStorage: { activeExtensions: number; entryCount: number; payloadBytes: number; maximumBytes: number };
    capturedAt: string;
  }>("/api/admin/cache"),
  cachePreview: () => json<{
    metadata: { expiredEntries: number; overQuotaEntries: number; reclaimableBytes: number };
    media: { expiredEntries?: number; overQuotaEntries?: number; reclaimableBytes?: number };
    unreferencedArtworkPayloads: number;
    unreferencedArtworkBytes: number;
  }>("/api/admin/cache/maintenance/preview"),
  cleanCache: () => json<{ deleted: number }>("/api/admin/cache/maintenance", { method: "POST" }),
  purgeCache: (scope: "metadata" | "media" | "all") =>
    json<{ deleted: number }>(`/api/admin/cache/${scope}`, { method: "DELETE" }),
  mediaProbe: () => json<{ success: boolean; code: string; message: string }>("/api/admin/media-probe"),
  playlistProbe: () => json<{ success: boolean; code: string; message: string }>("/api/admin/playlist-readiness"),
};

export type ExtensionRegistry = {
  id: string;
  name: string;
  registryUrl: string;
  enabled: boolean;
  revision: number;
};

export type ExtensionPackage = {
  id: string;
  registryId?: string | null;
  previousPackageId?: string | null;
  extensionId: string;
  displayName: string;
  version: string;
  lifecycle: string;
  state: string;
  active: boolean;
  installed: boolean;
  permissionReviewRequired: boolean;
  description?: string | null;
  author?: string | null;
  iconUrl?: string | null;
  capabilities?: string[];
  compatibility?: string | null;
  failureCode?: string | null;
  stagedAt?: string | null;
  revision: number;
};

export type ExtensionStoreItem = {
  id: string;
  displayName: string;
  version: string;
  description?: string | null;
  author?: string | null;
  downloadUrl: string;
  sha256: string;
  registryId?: string | null;
  iconUrl?: string | null;
  types?: string[];
};

export type ExtensionPermission = {
  id: string;
  permissionKind: string;
  permissionValue: string;
  required: boolean;
  decision: string;
};

export type ExtensionLog = {
  id: string;
  extensionPackageId?: string | null;
  extensionId?: string | null;
  level: string;
  eventCode?: string | null;
  message?: string | null;
  summary: string;
  createdAt: string;
};

const revisionBody = (expectedRevision: number) => ({
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ expectedRevision }),
});

export const extensions = {
  registries: () => json<ExtensionRegistry[]>("/api/admin/extensions/registries"),
  addRegistry: (name: string, registryUrl: string) =>
    json<ExtensionRegistry>("/api/admin/extensions/registries", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name, registryUrl, enabled: true }),
    }),
  setRegistryEnabled: (item: ExtensionRegistry, enabled: boolean) =>
    json<ExtensionRegistry>(`/api/admin/extensions/registries/${item.id}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled, expectedRevision: item.revision }),
    }),
  removeRegistry: (item: ExtensionRegistry) =>
    json<void>(`/api/admin/extensions/registries/${item.id}?expectedRevision=${item.revision}`, {
      method: "DELETE",
    }),
  packages: () => json<ExtensionPackage[]>("/api/admin/extensions/packages"),
  store: () => json<{ items: ExtensionStoreItem[]; errors: Array<{ repository: string; message: string }> }>("/api/admin/extensions/store"),
  logs: () => json<ExtensionLog[]>("/api/admin/extensions/logs?limit=100"),
  install: (item: Pick<ExtensionStoreItem, "id" | "downloadUrl" | "sha256" | "registryId">) =>
    json<{ packageId: string; message: string }>("/api/admin/extensions/install", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(item),
    }),
  permissions: (id: string) =>
    json<ExtensionPermission[]>(`/api/admin/extensions/packages/${id}/permissions`),
  review: (item: ExtensionPackage, decisions: Array<{ kind: string; value: string; approved: boolean }>) =>
    json<ExtensionPackage>(`/api/admin/extensions/packages/${item.id}/review`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ expectedRevision: item.revision, decisions }),
    }),
  activate: (item: ExtensionPackage) =>
    json<ExtensionPackage>(`/api/admin/extensions/packages/${item.id}/activate`, revisionBody(item.revision)),
  disable: (item: ExtensionPackage) =>
    json<void>(`/api/admin/extensions/packages/${item.id}/disable`, revisionBody(item.revision)),
  rollback: (item: ExtensionPackage) =>
    json<ExtensionPackage>(`/api/admin/extensions/packages/${item.id}/rollback`, revisionBody(item.revision)),
  revokePermissions: (item: ExtensionPackage) =>
    json<ExtensionPackage>(`/api/admin/extensions/packages/${item.id}/permissions/revoke`, revisionBody(item.revision)),
  cancelStaging: (item: ExtensionPackage) =>
    json<ExtensionPackage>(`/api/admin/extensions/packages/${item.id}/staging/cancel`, revisionBody(item.revision)),
  uninstall: (item: ExtensionPackage) =>
    json<ExtensionPackage>(`/api/admin/extensions/packages/${item.id}`, {
      ...revisionBody(item.revision),
      method: "DELETE",
    }),
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
  run: (id: string, snapshotId?: string) =>
    json<{ jobId: string; created: boolean }>(`/api/admin/playlist-links/${encodeURIComponent(id)}/run`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(snapshotId ? { snapshotId } : {}),
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
  sources: () => json<{
    accounts: PlaylistSourceAccount[];
    blockedAccounts: PlaylistSourceAccount[];
    providers: Array<{ id: string; displayName: string }>;
  }>("/api/admin/playlist-sources"),
  sourcePlaylists: (accountId: string, query = "", cursor = "") => {
    const params = new URLSearchParams({ limit: "100" });
    if (query) params.set("query", query);
    if (cursor) params.set("cursor", cursor);
    return json<{ items: PlaylistDiscoveryItem[]; nextCursor?: string | null }>(
      `/api/admin/playlist-sources/${encodeURIComponent(accountId)}/playlists?${params}`,
    );
  },
  targets: () => json<{ targets: MediaTarget[] }>("/api/admin/media-targets"),
  create: (input: {
    providerAccountId: string;
    sourceProviderId: string;
    sourcePlaylistId: string;
    libraryScopeId: string;
    targetProtocol: string;
    targetBackendInstanceId: string;
    targetCredentialReferenceId?: string | null;
  }) => json<{ id: string }>("/api/admin/playlist-links", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      ...input,
      mode: "virtual",
      materializationMode: "reconcile",
      targetPlaylistId: null,
      mirrorStaleEntries: false,
      preserveManualEntries: true,
      syncName: true,
      syncDescription: true,
      syncArtwork: true,
    }),
  }),
};

export const matchReview = {
  list: (params: {
    page?: number;
    pageSize?: number;
    search?: string;
    state?: string;
    sort?: string;
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
