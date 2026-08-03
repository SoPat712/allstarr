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

export type OnboardingState = {
  completed: boolean;
  setupOpen: boolean;
  shouldRedirectToSetup: boolean;
  schemaVersion: string;
  completedSteps: string[];
  completionSource: string;
  completedAt?: string | null;
  reopenedAt?: string | null;
  revision: number;
  recoveryNotices: string[];
  alreadyCompleted?: boolean;
  migration: {
    available: boolean;
    completed: boolean;
    firstRun: boolean;
    lastAppliedAt?: string | null;
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
  availableAt?: string;
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
  icon?: string | null;
  description?: string | null;
  logoUrl?: string | null;
  categories?: string[];
  status?: string;
  notes?: string[];
  configSchema?: ConfigField[];
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

export type AppleDownloadStatus = {
  state?: string;
  ready?: boolean;
  staged?: boolean;
  daemon_running?: boolean;
  wrapper_healthy?: boolean;
  logged_in?: boolean;
  login_state?: string;
  api_version?: string | null;
  account?: { state?: string; logged_in?: boolean };
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

export type EnvMigrationPreview = {
  previewToken: string;
  revision: string;
  expiresAt: string;
  canApply: boolean;
  importedSettingCount: number;
  providerAccountCount: number;
  manualCount: number;
  backendIdentityCount: number;
  playlistLinkCount: number;
  scheduleCount: number;
  items: Array<{
    key: string;
    sourceLine: number;
    action: string;
    reason: string;
    sensitive: boolean;
    valuePreview?: string | null;
    warning?: string | null;
  }>;
  conflicts: string[];
  warnings: string[];
};

export type SelectiveTransferOptions = {
  settings: boolean;
  accounts: boolean;
  playlists: boolean;
  intelligence: boolean;
  extensions: boolean;
};

export type SelectiveTransferReport = {
  includedCategories: string[];
  excludedCategories: string[];
  totalRows: number;
  rowsByEntry: Record<string, number>;
};

export type SelectiveTransferPreview = {
  canImport: boolean;
  dependencies: string[];
  conflicts: string[];
  report: SelectiveTransferReport;
};

export type CacheTierUsage = {
  tier: string;
  entryCount: number;
  payloadBytes: number;
  maximumBytes?: number | null;
  maximumEntryBytes?: number | null;
  enabled: boolean;
  hits: number;
  misses: number;
  writes: number;
  evictions: number;
  hitRatio: number;
};

export type CacheCategoryDiagnostics = {
  category: string;
  owner: string;
  storageTier: string;
  enabled: boolean;
  entryCount: number;
  payloadBytes: number;
  freshSeconds: number;
  staleSeconds: number;
  maximumBytes: number;
  maximumEntries: number;
  warmingRule: string;
  invalidationTrigger: string;
};

export type CacheDiagnostics = {
  database: CacheTierUsage;
  hot: CacheTierUsage;
  media: CacheTierUsage;
  categories: CacheCategoryDiagnostics[];
  activity: {
    coalescedRequests: number;
    staleServes: number;
    upstreamBytesAvoided: number;
  };
  artworkLimits: {
    maximumEntryBytes: number;
    maximumDecodedPixels: number;
  };
  extensionStorage: {
    activeExtensions: number;
    entryCount: number;
    payloadBytes: number;
    maximumBytes: number;
  };
  capturedAt: string;
};

export type CacheMaintenancePreview = {
  metadata: {
    scannedEntries: number;
    scanLimitReached: boolean;
    expiredEntries: number;
    unknownOwnerEntries: number;
    disabledCategoryEntries: number;
    noExpiryEntries: number;
    staleAuthorizationScopeEntries: number;
    supersededEntries: number;
    overQuotaEntries: number;
    reclaimableBytes: number;
  };
  media: {
    scannedFiles: number;
    scanLimitReached: boolean;
    temporaryFiles: number;
    malformedMetadataFiles: number;
    orphanedMetadataFiles: number;
    orphanedPayloadFiles: number;
    expiredEntries: number;
    noExpiryEntries: number;
    overQuotaEntries: number;
    reclaimableBytes: number;
    cleanupIntervalSeconds: number;
    lastCleanupAt?: string | null;
    lastCleanupDeletedEntries: number;
  };
  unreferencedArtworkPayloads: number;
  unreferencedArtworkBytes: number;
  artworkReferenceScanLimitReached: boolean;
  capturedAt: string;
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
  sourcePlaylistId: string;
  sourceUpdateAvailable?: boolean;
  providerAccountId: string;
  libraryScopeId: string;
  targetProtocol: string;
  targetBackendInstanceId: string;
  targetPlaylistId?: string | null;
  targetCredentialReferenceId?: string | null;
  mode: "virtual" | "materialized" | "hybrid";
  projectionMode: "resolved" | "source" | "target";
  materializationMode: "reconcile" | "recreate";
  scheduleId?: string | null;
  mirrorStaleEntries: boolean;
  preserveManualEntries: boolean;
  syncName: boolean;
  syncDescription: boolean;
  syncArtwork: boolean;
  ruleVersion: string;
  policyVersion: string;
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

export type PlaylistSourceUpdatePreview = {
  providerId: string;
  providerName: string;
  sourcePlaylistName: string;
  backendPlaylistName: string;
  backendProtocol: string;
  sourceVersion: string;
  expectedRevision: number;
  confirmationId: string;
  currentCount: number;
  includedCount: number;
  skippedCount: number;
  addedCount: number;
  removedCount: number;
  movedCount: number;
  duplicateCount: number;
  canApply: boolean;
  message: string;
  changes: Array<{
    kind: "add" | "remove" | "move";
    fromPosition?: number | null;
    toPosition?: number | null;
    title: string;
    artist: string;
  }>;
  skipped: Array<{
    position: number;
    title: string;
    artist: string;
    reason: string;
  }>;
  unshownChangeCount: number;
  unshownSkippedCount: number;
};

export type PlaylistTrack = {
  sourcePosition: number;
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
  routeKind: "local" | "external" | "unmatched" | "unresolved";
  routeProviderId?: string | null;
  matchState?: string | null;
  targetEligible?: boolean;
  outcomeCode?: string | null;
  targetStatus?: string | null;
  providerRoutes: Array<{ providerId: string; externalId: string; pinned: boolean }>;
};

export type PlaylistClientTrack = {
  position: number;
  sourcePosition: number;
  itemId: string;
  playlistEntryId?: string | null;
  title: string;
  artists: string[];
  album?: string | null;
  durationMs?: number | null;
  routeKind: "local" | "external" | "unresolved";
  routeProviderId?: string | null;
};

export type PlaylistClientProjection = {
  protocolId: string;
  projectionMode: "resolved" | "source" | "target";
  trackCount: number;
  tracks: PlaylistClientTrack[];
};

export type PlaylistSchedule = {
  id: string;
  cronExpression: string;
  timeZoneId: string;
  overlapPolicy: "skip" | "queue";
  misfirePolicy: "skip" | "runOnce";
  enabled: boolean;
  nextRunAt?: string | null;
  revision: number;
};

export type PlaylistDetails = {
  id: string;
  snapshotId: string;
  snapshotVersion: number;
  latestSourceSnapshotVersion: number;
  hasNewerSourceGeneration: boolean;
  name: string;
  sourceProviderId: string;
  projectionMode: "resolved" | "source" | "target";
  targetProtocol: string;
  targetPlaylistId?: string | null;
  artworkUrl?: string | null;
  retrievedAt: string;
  lastRematchedAt?: string | null;
  completedAt?: string | null;
  syncState?: string | null;
  trackCount: number;
  localCount: number;
  externalCount: number;
  unresolvedCount: number;
  matchedCount: number;
  reviewCount: number;
  rejectedCount: number;
  playableCount: number;
  routeCoverage: Array<{ providerId: string; count: number }>;
  durationMs?: number | null;
  unknownDurationCount: number;
  reconciliation?: {
    providerAdvertisedRows: number;
    rawRows: number;
    mappedRows: number;
    persistedSourceRows: number;
    publishedRows: number;
    accepted: number;
    tentative: number;
    rejected: number;
    unresolved: number;
    playableRoutes: number;
    materializedTargetRows: number;
    protocolVisibleRows: number;
    addedPositions: number[];
    removedPositions: number[];
    movedPositions: number[];
    duplicatedPositions: number[];
    changedPositions: number[];
  } | null;
  schedule?: PlaylistSchedule | null;
  clientProjection?: PlaylistClientProjection | null;
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
  libraryScopeId?: string | null;
  displayName: string;
  credentialReferenceId?: string | null;
};

export type TargetPlaylist = {
  id: string;
  name: string;
  description?: string | null;
  trackCount?: number | null;
  artworkUrl?: string | null;
  writable: boolean;
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
  isLocal?: boolean | null;
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
  searchQuery?: string | null;
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
  confidence?: number | null;
  isrc?: string | null;
  components?: Record<string, number> | null;
  reasons?: string[];
  warnings?: string[];
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

export type IntelligenceScope = {
  protocol: string;
  backendInstanceId: string;
  libraryScopeId: string;
};

export type AudioMuseTrack = {
  trackId: string;
  title?: string | null;
  artist?: string | null;
  album?: string | null;
  libraryTrackId?: string | null;
  score: number;
  explanation?: string | null;
};

export type AudioMuseAnalysis = {
  jobId: string;
  state: "queued" | "running" | "completed" | "failed" | "canceled";
  completed: number;
  total: number;
  safeCode?: string | null;
};

export type AudioMuseCluster = {
  id: string;
  name: string;
  tracks: AudioMuseTrack[];
};

export type AudioMuseMapPage = {
  items: Array<AudioMuseTrack & { x: number; y: number; clusterId?: string | null }>;
  projection: string;
  nextCursor?: string | null;
  isPartial: boolean;
  snapshotVersion?: string | null;
};

export type IntelligenceState = {
  state: string;
  message?: string | null;
  scope: IntelligenceScope;
  policy?: {
    enabled: boolean;
    retentionDays: number;
    revision: number;
    targetCredentialReferenceId?: string | null;
    targetCredentialConfigured?: boolean;
  } | null;
  availableSignalTypes: Array<{ id: string; label: string; enabled: boolean }>;
  providers: Array<{
    id: string;
    label: string;
    description: string;
    enabled: boolean;
    available: boolean;
    state: string;
    reasonCode?: string | null;
  }>;
  actions: {
    canRun: boolean;
    canGenerate: boolean;
    latestRunId?: string | null;
    latestRunState?: string | null;
    latestJobId?: string | null;
    attemptCount?: number | null;
    failureCount?: number | null;
    maxAttempts?: number | null;
    canCancel?: boolean;
    progress?: {
      stage: string;
      message: string;
      completed?: number | null;
      total?: number | null;
      provider?: string | null;
      playlist?: string | null;
      track?: string | null;
    } | null;
  };
  candidates: Array<{
    id: string;
    trackKey: string;
    title?: string | null;
    artist?: string | null;
    album?: string | null;
    artworkUrl?: string | null;
    score: number;
    source: string;
    providerId: string;
    sourceRevision: string;
    revision: number;
    explanations: Array<{ code: string; weight: number; explanation: string }>;
    exclusions: string[];
    feedback?: { kind: string; reasonCode?: string | null; revision: number } | null;
  }>;
  generatedSets: Array<{
    id: string;
    name: string;
    trackCount: number;
    state: string;
    materialized: boolean;
    backendPlaylistId?: string | null;
    errorCode?: string | null;
  }>;
  schedules: IntelligenceSchedule[];
  visualization: Array<{ key: string; label: string; value: number }>;
};

export type IntelligenceSchedule = {
  id: string;
  cronExpression: string;
  timeZoneId: string;
  overlapPolicy: "skip" | "queue";
  misfirePolicy: "skip" | "runOnce";
  enabled: boolean;
  nextRunAt?: string | null;
  revision: number;
  name: string;
  limit: number;
};

export type ListeningHistoryStats = {
  completedListens: number;
  distinctTracks: number;
  distinctArtists: number;
  listeningTimeMilliseconds: number;
  firstListen?: string | null;
};

export type ListeningHistoryTargetStatus = {
  target: string;
  state: string;
  code?: string | null;
  message?: string | null;
  retryAfter?: string | null;
  requiresReauthentication: boolean;
  updatedAt: string;
};

export type ListeningHistoryItem = {
  id: string;
  title?: string | null;
  artist?: string | null;
  album?: string | null;
  listenedAt?: string | null;
  durationMilliseconds?: number | null;
  client?: string | null;
  source: string;
  provider?: string | null;
  state: string;
  enrichmentState: string;
  artworkUrl?: string | null;
  targetStatuses: ListeningHistoryTargetStatus[];
  revision: number;
};

export type ListeningHistoryPeriod = { from: string; to: string; timeZoneId: string };

export type ListeningHistoryOverview = {
  period: ListeningHistoryPeriod;
  allTime: ListeningHistoryStats;
  selected: ListeningHistoryStats;
  currentStreakDays: number;
  longestStreakDays: number;
  nowPlaying?: ListeningHistoryItem | null;
  recent: ListeningHistoryItem[];
};

export type ListeningHistoryActivity = {
  period: ListeningHistoryPeriod;
  currentStreakDays: number;
  longestStreakDays: number;
  buckets: Array<{ date: string; count: number; durationMilliseconds: number }>;
};

export type ListeningHistoryTopItem = {
  title?: string | null;
  artist?: string | null;
  album?: string | null;
  listenCount: number;
  listeningTimeMilliseconds: number;
  lastListenedAt?: string | null;
};

export type ListeningHistoryDetail = {
  item: ListeningHistoryItem;
  identity: {
    recordingMusicBrainzId?: string | null;
    isrc?: string | null;
    albumArtist?: string | null;
    trackNumber?: number | null;
    musicBrainzEnrichmentConfidence?: number | null;
    musicBrainzSourceRevision?: string | null;
    musicBrainzEnrichedAt?: string | null;
    musicBrainzFacts?: Record<string, unknown> | null;
  };
  provenance: {
    source: string;
    client?: string | null;
    device?: string | null;
    provider?: string | null;
    imported: boolean;
  };
};

export type ListeningHistoryImportPreview = {
  format: string;
  fileRows: number;
  musicRows: number;
  completed: number;
  partial: number;
  skipped: number;
  episodes: number;
  nonTrack: number;
  malformed: number;
  duplicateInFile: number;
  duplicateExisting: number;
  newRows: number;
  resolvedNewRows: number;
  unresolvedNewRows: number;
  rowsWithoutProviderIdentity: number;
  sourceUserCount: number;
  estimatedMusicBrainzLookups: number;
  earliest?: string | null;
  latest?: string | null;
  reasonCounts: Record<string, number>;
};

export type ListeningHistoryImport = {
  importId: string;
  revision: string;
  displayFileName?: string;
  sizeBytes?: number;
  expiresAt?: string;
  state: string;
  jobId?: string | null;
  jobState?: string | null;
  lastErrorCode?: string | null;
  lastErrorMessage?: string | null;
  importedRows?: number;
  duplicateRows?: number;
  resolvedRows?: number;
  unresolvedRows?: number;
  outboundReplay: false;
  preview?: ListeningHistoryImportPreview;
};

async function request(input: RequestInfo | URL, init?: RequestInit) {
  const response = await fetch(input, {
    cache: "no-store",
    credentials: "same-origin",
    ...init,
  });

  if (!response.ok) {
    const body = normalizeResponse(await response.json().catch(() => null));
    throw new ApiError(response.status, response.statusText, body);
  }
  return response;
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    statusText: string,
    readonly details: unknown,
  ) {
    const body = details as { error?: string; message?: string; stage?: string } | null;
    const message = body?.error || body?.message || `${status} ${statusText}`;
    super(body?.stage ? `${body.stage}: ${message}` : message);
  }
}

async function json<T>(input: RequestInfo | URL, init?: RequestInit): Promise<T> {
  const response = await request(input, init);
  if (response.status === 204) return undefined as T;
  return normalizeResponse(await response.json()) as T;
}

export function normalizeResponse(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(normalizeResponse);
  if (!value || typeof value !== "object") return value;
  return Object.fromEntries(Object.entries(value).map(([key, item]) => [
    key[0].toLowerCase() + key.slice(1),
    normalizeResponse(item),
  ]));
}

function selectiveTransferForm(
  file: File,
  mode: "Conflict" | "Merge" | "Replace",
  options: SelectiveTransferOptions,
) {
  const body = new FormData();
  body.append("File", file);
  body.append("Mode", mode);
  for (const [category, included] of Object.entries(options))
    body.append(`Import${category[0].toUpperCase()}${category.slice(1)}`, String(included));
  return body;
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

export const onboarding = {
  status: () => json<OnboardingState>("/api/admin/onboarding/status"),
  complete: () => json<OnboardingState>("/api/admin/onboarding/complete", { method: "POST" }),
  reopen: () => json<OnboardingState>("/api/admin/onboarding/reopen", { method: "POST" }),
};

const intelligenceBody = (value: object, method = "POST") => ({
  method,
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify(value),
});

const intelligenceQuery = (scope: IntelligenceScope, input: Record<string, string | number | undefined> = {}) => {
  const query = new URLSearchParams(scope);
  for (const [key, value] of Object.entries(input))
    if (value !== undefined && value !== "") query.set(key, String(value));
  return query;
};

export const intelligence = {
  get: (scope: IntelligenceScope) =>
    json<IntelligenceState>(`/api/admin/intelligence?${new URLSearchParams(scope)}`),
  savePolicy: (scope: IntelligenceScope, input: object) =>
    json<{ revision: number }>("/api/admin/intelligence/policy",
      intelligenceBody({ ...scope, ...input }, "PUT")),
  run: (scope: IntelligenceScope, seedTrackKeys: string[] = []) =>
    json<{ runId: string; jobId: string }>("/api/admin/intelligence/runs",
      intelligenceBody({ ...scope, seedTrackKeys, limit: 25, idempotencyKey: crypto.randomUUID() })),
  generate: (scope: IntelligenceScope, runId: string, name: string) =>
    json<{ id: string }>("/api/admin/intelligence/generated-sets",
      intelligenceBody({ ...scope, runId, name })),
  feedback: (scope: IntelligenceScope, candidateId: string, kind: string, expectedRevision: number) =>
    json<{ revision: number }>(`/api/admin/intelligence/candidates/${encodeURIComponent(candidateId)}/feedback`,
      intelligenceBody({ ...scope, kind, expectedRevision }, "PUT")),
  purge: (scope: IntelligenceScope) =>
    json<void>("/api/admin/intelligence/data", intelligenceBody(scope, "DELETE")),
  startAudioMuseAnalysis: (scope: IntelligenceScope, rebuild = false) =>
    json<AudioMuseAnalysis>("/api/admin/intelligence/audiomuse/analysis",
      intelligenceBody({ ...scope, rebuild, idempotencyKey: crypto.randomUUID() })),
  audioMuseAnalysis: (scope: IntelligenceScope, jobId: string) =>
    json<AudioMuseAnalysis>(`/api/admin/intelligence/audiomuse/analysis/${encodeURIComponent(jobId)}?${intelligenceQuery(scope)}`),
  audioMuseSimilar: (scope: IntelligenceScope, seedTrackIds: string[], limit = 25) =>
    json<{ tracks: AudioMuseTrack[] }>("/api/admin/intelligence/audiomuse/similar",
      intelligenceBody({ ...scope, seedTrackIds, limit })),
  audioMusePath: (scope: IntelligenceScope, startTrackId: string, endTrackId: string, limit = 25) =>
    json<{ tracks: AudioMuseTrack[]; totalDistance: number }>("/api/admin/intelligence/audiomuse/path",
      intelligenceBody({ ...scope, startTrackId, endTrackId, limit })),
  audioMuseBlend: (scope: IntelligenceScope, includeTrackIds: string[], avoidTrackIds: string[], limit = 25) =>
    json<{ tracks: AudioMuseTrack[] }>("/api/admin/intelligence/audiomuse/blend",
      intelligenceBody({ ...scope, includeTrackIds, avoidTrackIds, limit })),
  audioMuseFingerprint: (scope: IntelligenceScope, periodDays: 30 | 90 | 365, limit = 25) =>
    json<{ tracks: AudioMuseTrack[]; periodDays: number; completedListens: number; seedCount: number }>(
      "/api/admin/intelligence/audiomuse/fingerprint",
      intelligenceBody({ ...scope, periodDays, limit })),
  audioMuseSearch: (scope: IntelligenceScope, query: string, mode: "text" | "lyrics", limit = 25) =>
    json<{ tracks: AudioMuseTrack[]; mode: string }>("/api/admin/intelligence/audiomuse/search",
      intelligenceBody({ ...scope, query, mode, limit })),
  audioMuseClusters: (scope: IntelligenceScope, limit = 50) =>
    json<{ clusters: AudioMuseCluster[] }>(`/api/admin/intelligence/audiomuse/clusters?${intelligenceQuery(scope, { limit })}`),
  audioMuseMap: (scope: IntelligenceScope, limit = 50, cursor?: string) =>
    json<AudioMuseMapPage>(`/api/admin/intelligence/audiomuse/map?${intelligenceQuery(scope, { limit, cursor })}`),
  historyOverview: (scope: IntelligenceScope, from: string, to: string, timeZoneId: string) =>
    json<ListeningHistoryOverview>(`/api/admin/intelligence/history/overview?${intelligenceQuery(scope, { from, to, timeZoneId })}`),
  history: (scope: IntelligenceScope, input: {
    from: string;
    to: string;
    timeZoneId: string;
    limit?: number;
    cursor?: string;
    source?: string;
    client?: string;
    artist?: string;
    album?: string;
    track?: string;
    search?: string;
  }) => json<{ period: ListeningHistoryPeriod; items: ListeningHistoryItem[]; nextCursor?: string | null }>(
    `/api/admin/intelligence/history?${intelligenceQuery(scope, input)}`,
  ),
  historyActivity: (scope: IntelligenceScope, from: string, to: string, timeZoneId: string) =>
    json<ListeningHistoryActivity>(`/api/admin/intelligence/history/activity?${intelligenceQuery(scope, { from, to, timeZoneId })}`),
  historyTop: (scope: IntelligenceScope, kind: "artist" | "album" | "track", from: string, to: string, timeZoneId: string) =>
    json<{ period: ListeningHistoryPeriod; kind: string; items: ListeningHistoryTopItem[] }>(
      `/api/admin/intelligence/history/top/${kind}?${intelligenceQuery(scope, { from, to, timeZoneId, limit: 10 })}`,
    ),
  historyDetail: (scope: IntelligenceScope, id: string) =>
    json<ListeningHistoryDetail>(`/api/admin/intelligence/history/${encodeURIComponent(id)}?${intelligenceQuery(scope)}`),
  correctHistory: (scope: IntelligenceScope, id: string, input: {
    title: string;
    artist: string;
    album?: string | null;
    albumArtist?: string | null;
    expectedRevision: number;
  }) => json<{ id: string; revision: number }>(
    `/api/admin/intelligence/history/${encodeURIComponent(id)}`,
    intelligenceBody({ ...scope, ...input }, "PUT"),
  ),
  deleteHistory: (scope: IntelligenceScope, id: string, expectedRevision: number) =>
    json<void>(`/api/admin/intelligence/history/${encodeURIComponent(id)}`,
      intelligenceBody({ ...scope, expectedRevision, confirmed: true }, "DELETE")),
  historyExportUrl: (scope: IntelligenceScope) =>
    `/api/admin/intelligence/history/export?${intelligenceQuery(scope)}`,
  previewHistoryImport: (scope: IntelligenceScope, file: File) => {
    const body = new FormData();
    body.append("file", file);
    for (const [key, value] of Object.entries(scope)) body.append(key, value);
    return json<ListeningHistoryImport>("/api/admin/intelligence/history/imports/preview", { method: "POST", body });
  },
  historyImport: (scope: IntelligenceScope, id: string) =>
    json<ListeningHistoryImport>(`/api/admin/intelligence/history/imports/${encodeURIComponent(id)}?${intelligenceQuery(scope)}`),
  changeHistoryImport: (scope: IntelligenceScope, item: ListeningHistoryImport, operation: "apply" | "resume" | "cancel") =>
    json<ListeningHistoryImport>(`/api/admin/intelligence/history/imports/${encodeURIComponent(item.importId)}/${operation}`,
      intelligenceBody({ ...scope, revision: item.revision })),
  createSchedule: (scope: IntelligenceScope, input: Omit<IntelligenceSchedule, "id" | "revision" | "nextRunAt">) =>
    json<IntelligenceSchedule>("/api/admin/intelligence/schedules", intelligenceBody({ ...scope, ...input })),
  updateSchedule: (scope: IntelligenceScope, schedule: IntelligenceSchedule, input: Omit<IntelligenceSchedule, "id" | "revision" | "nextRunAt">) =>
    json<IntelligenceSchedule>(`/api/admin/intelligence/schedules/${encodeURIComponent(schedule.id)}`,
      intelligenceBody({ ...scope, ...input, expectedRevision: schedule.revision }, "PUT")),
  deleteSchedule: (scope: IntelligenceScope, schedule: IntelligenceSchedule) =>
    json<void>(`/api/admin/intelligence/schedules/${encodeURIComponent(schedule.id)}`,
      intelligenceBody({ ...scope, expectedRevision: schedule.revision }, "DELETE")),
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
  cache: () => json<CacheDiagnostics>("/api/admin/cache"),
  cachePreview: () => json<CacheMaintenancePreview>("/api/admin/cache/maintenance/preview"),
  cleanCache: () => json<{ deleted: number }>("/api/admin/cache/maintenance", { method: "POST" }),
  purgeCache: (scope: "metadata" | "media" | "all") =>
    json<{ deleted: number }>(`/api/admin/cache/${scope}`, { method: "DELETE" }),
  purgeCacheCategory: (category: string) =>
    json<{ category: string; deleted: number }>(
      `/api/admin/cache/categories/${encodeURIComponent(category)}`,
      { method: "DELETE" },
    ),
  mediaProbe: () => json<{ success: boolean; code: string; message: string }>("/api/admin/media-probe"),
  playlistProbe: () => json<{ success: boolean; code: string; message: string }>("/api/admin/playlist-readiness"),
  exportState: async (options: SelectiveTransferOptions, signal?: AbortSignal) => {
    const response = await request("/api/admin/export-selective-state", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(Object.fromEntries(Object.entries(options)
        .map(([category, included]) => [`include${category[0].toUpperCase()}${category.slice(1)}`, included]))),
      signal,
    });
    const filename = /filename="?([^";]+)"?/i.exec(response.headers.get("Content-Disposition") ?? "")?.[1] ??
      "allstarr-selective-export.zip";
    return { blob: await response.blob(), filename };
  },
  previewState: (
    file: File,
    mode: "Conflict" | "Merge" | "Replace",
    options: SelectiveTransferOptions,
    signal?: AbortSignal,
  ) => json<SelectiveTransferPreview>("/api/admin/preview-selective-state", {
    method: "POST",
    body: selectiveTransferForm(file, mode, options),
    signal,
  }),
  importState: (
    file: File,
    mode: "Conflict" | "Merge" | "Replace",
    options: SelectiveTransferOptions,
    signal?: AbortSignal,
  ) => json<{ success: boolean; message: string; report: SelectiveTransferReport }>(
    "/api/admin/import-selective-state", {
      method: "POST",
      body: selectiveTransferForm(file, mode, options),
      signal,
    }),
  migrationStatus: () => json<{
    available: boolean;
    completed: boolean;
    sourcePresent: boolean;
    firstRun: boolean;
    lastAppliedAt?: string | null;
  }>("/api/admin/config/migration/status"),
  previewMigration: (file: File) => {
    const body = new FormData();
    body.append("file", file);
    return json<EnvMigrationPreview>("/api/admin/config/migration/preview", {
      method: "POST",
      body,
    });
  },
  applyMigration: (preview: EnvMigrationPreview) =>
    json<{ success: boolean; alreadyApplied: boolean }>("/api/admin/config/migration/apply", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        previewToken: preview.previewToken,
        revision: preview.revision,
        confirmed: true,
      }),
    }),
  resetMigration: (previewToken: string) =>
    json<void>("/api/admin/config/migration/reset", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ previewToken }),
    }),
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
  details: (id: string, projectionMode?: "resolved" | "source" | "target") =>
    json<PlaylistDetails>(`/api/admin/playlist-links/${encodeURIComponent(id)}${projectionMode ? `?projectionMode=${projectionMode}` : ""}`),
  refresh: (id: string) =>
    json(`/api/admin/playlist-links/${encodeURIComponent(id)}/refresh`, { method: "POST" }),
  run: (id: string, snapshotId?: string) =>
    json<{ jobId: string; created: boolean }>(`/api/admin/playlist-links/${encodeURIComponent(id)}/run`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(snapshotId ? { snapshotId } : {}),
    }),
  previewSourceUpdate: (id: string) =>
    json<PlaylistSourceUpdatePreview>(
      `/api/admin/playlist-links/${encodeURIComponent(id)}/source-update/preview`,
    ),
  applySourceUpdate: (id: string, expectedRevision: number, confirmationId: string) =>
    json<{ jobId: string; created: boolean }>(
      `/api/admin/playlist-links/${encodeURIComponent(id)}/source-update/apply`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ expectedRevision, confirmationId }),
      },
    ),
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
  targetPlaylists: (targetId: string, query = "", cursor = "") => {
    const params = new URLSearchParams({ limit: "100" });
    if (query) params.set("query", query);
    if (cursor) params.set("cursor", cursor);
    return json<{ items: TargetPlaylist[]; nextCursor?: string | null }>(
      `/api/admin/media-targets/${encodeURIComponent(targetId)}/playlists?${params}`,
    );
  },
  create: (input: {
    providerAccountId: string;
    sourceProviderId: string;
    sourcePlaylistId: string;
    libraryScopeId: string;
    targetProtocol: string;
    targetBackendInstanceId: string;
    targetCredentialReferenceId?: string | null;
    targetPlaylistId?: string | null;
    mode: "virtual" | "materialized" | "hybrid";
    projectionMode?: "resolved" | "source" | "target";
    materializationMode: "reconcile" | "recreate";
    mirrorStaleEntries: boolean;
    preserveManualEntries: boolean;
    syncName: boolean;
    syncDescription: boolean;
    syncArtwork: boolean;
  }) => json<{ id: string }>("/api/admin/playlist-links", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(input),
  }),
  update: (id: string, input: {
    expectedRevision: number;
    mode: "virtual" | "materialized" | "hybrid";
    projectionMode: "resolved" | "source" | "target";
    materializationMode: "reconcile" | "recreate";
    scheduleId?: string | null;
    targetPlaylistId?: string | null;
    targetCredentialReferenceId?: string | null;
    mirrorStaleEntries: boolean;
    preserveManualEntries: boolean;
    syncName: boolean;
    syncDescription: boolean;
    syncArtwork: boolean;
    ruleVersion?: string;
    policyVersion?: string;
  }) => json<PlaylistLink>(`/api/admin/playlist-links/${encodeURIComponent(id)}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(input),
  }),
  createSchedule: (id: string, input: {
    cronExpression: string;
    timeZoneId: string;
    overlapPolicy: "skip" | "queue";
    misfirePolicy: "skip" | "runOnce";
    enabled?: boolean;
  }) => json<PlaylistSchedule>(`/api/admin/playlist-links/${encodeURIComponent(id)}/schedules`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(input),
  }),
  updateSchedule: (schedule: PlaylistSchedule, input: {
    cronExpression: string;
    timeZoneId: string;
    enabled: boolean;
  }) => json<PlaylistSchedule>(
    `/api/admin/playlist-links/schedules/${encodeURIComponent(schedule.id)}`,
    {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        ...input,
        overlapPolicy: schedule.overlapPolicy,
        misfirePolicy: schedule.misfirePolicy,
        expectedRevision: schedule.revision,
      }),
    },
  ),
};

export const matchReview = {
  list: (params: {
    page?: number;
    pageSize?: number;
    search?: string;
    state?: string;
    sort?: string;
    libraryScopeId?: string;
    externalSnapshotId?: string;
  }) => {
    const query = new URLSearchParams();
    for (const [key, value] of Object.entries(params)) {
      if (value !== undefined && value !== "") query.set(key, String(value));
    }
    return json<MatchReviewResponse>(`/api/admin/track-matches?${query}`);
  },
  get: async (externalSnapshotId: string) =>
    (await matchReview.list({ externalSnapshotId, pageSize: 1 })).matches[0] ?? null,
  searchLocal: (query: string, libraryScopeId: string, externalSnapshotId: string) =>
    json<{ tracks: MatchTarget[] }>(
      `/api/admin/track-matches/targets/local?query=${encodeURIComponent(query)}&libraryScopeId=${encodeURIComponent(libraryScopeId)}&externalSnapshotId=${encodeURIComponent(externalSnapshotId)}`,
    ),
  searchProviders: (query: string, libraryScopeId: string, externalSnapshotId: string) => {
    const params = new URLSearchParams({ query, libraryScopeId, externalSnapshotId, limit: "50" });
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

export const appleDownload = {
  status: () => json<AppleDownloadStatus>("/api/admin/apple-download/status"),
  setup: (file: File) => {
    const body = new FormData();
    body.append("file", file, file.name);
    return json<{ message?: string; fileName?: string; sizeBytes?: number }>(
      "/api/admin/apple-download/setup",
      { method: "POST", body },
    );
  },
  login: (username: string, password: string) =>
    json<AppleDownloadStatus>("/api/admin/apple-download/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password }),
    }),
  submit2fa: (code: string) =>
    json<AppleDownloadStatus>("/api/admin/apple-download/login/2fa", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ code }),
    }),
};
