import { adminUrl } from "./admin-url";

export type Session = {
  authenticated: boolean;
  backendConfigured?: boolean;
  restartPending?: boolean;
  backend: string;
  features?: {
    intelligence: boolean;
  };
  user?: {
    id: string;
    allstarrUserId?: string | null;
    name: string;
    isAdministrator: boolean;
    avatarUrl?: string | null;
  };
};

export type PlaylistConsentStatus = {
  supported: boolean;
  granted: boolean;
  updatedAt: string | null;
};

export type OnboardingState = {
  completed: boolean;
  setupOpen: boolean;
  shouldRedirectToSetup: boolean;
  schemaVersion: string;
  completedSteps: string[];
  skippedSteps?: string[];
  steps?: Array<{ id: string; title: string; explanation: string; href: string | null; required: boolean; state: string }>;
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

export type ExplicitFilter = "All" | "ExplicitOnly" | "CleanOnly";
export type PlaybackStart = "FastStart" | "Balanced" | "BestQuality";

export type ListeningPreferences = {
  explicitFilter: string;
  showExternalLabel: boolean;
  showExplicitLabel: boolean;
  playbackStart?: PlaybackStart | null;
  lyricsOrder?: string | null;
};

export type PersonalListeningPreferences = {
  values: ListeningPreferences;
  householdDefaults: ListeningPreferences;
  usesHouseholdDefaults: boolean;
  revision: string;
  cleanOnlyLocked?: boolean;
};

export type ListenerPlaylistCopy = {
  id: string;
  name: string;
  originalPlaylistId: string;
  originalTargetPlaylistId: string | null;
  backendInstanceId: string;
  createdAt: string;
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
  displayName?: string;
  displayState?: string;
  technicalDetails?: Record<string, string> | null;
  summary?: string;
  targetName?: string | null;
  recovery?: string | null;
  actionHref?: string | null;
  actionLabel?: string | null;
  correlationId?: string | null;
  source?: string;
  id: string;
  type: string;
  state: string;
  ownerName?: string | null;
  canCancel?: boolean;
  createdAt?: string;
  completedAt?: string | null;
  startedAt?: string | null;
  lastAttemptStartedAt?: string | null;
  lastAttemptOutcome?: string | null;
  lastAttemptCompletedAt?: string | null;
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

export type JobResponse = { jobs: Job[]; progress: JobProgress[]; hasMore?: boolean; nextCursor?: string | null; nextCursorId?: string | null };
export type JobAttempt = { attemptNumber: number; startedAt: string; completedAt?: string | null; outcome?: string | null; errorCode?: string | null; errorMessage?: string | null };
export type JobDetail = { job: Job; attempts: JobAttempt[]; hasMore: boolean; nextCursor?: string | null; nextCursorId?: string | null };
export type ActivityTask = { id: string; name: string; kind: string; state: string; enabled: boolean; ownerName?: string | null; cadence: string; detail?: string; lastRunAt?: string | null; nextRunAt?: string | null; runPlaylistLinkId?: string | null; actionHref?: string | null; actionLabel?: string | null };

export type PlayTrace = {
  id: string;
  listenerId?: string | null;
  startedAt: string;
  outcome: string;
  request: { presented: { title?: string | null; artist?: string | null; artwork?: string | null } };
  resolution: { provider?: string | null; cached?: boolean | null };
  timing: { elapsedMs: number; firstByteMs?: number | null; headersMs?: number | null; omittedPhases: number;
    phases: { phase: string; provider?: string | null; startedMs: number; elapsedMs: number }[] };
};

export type ActivityItem = {
  jobId?: string | null;
  recovery?: string | null;
  actionHref?: string | null;
  actionLabel?: string | null;
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
  actorDisplayName?: string | null;
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
  connectedAccounts?: { scope: "Personal" | "Shared"; customLabel?: string | null }[];
  enabledAccountCount: number;
  capabilityTotal: number;
  healthyCapabilityCount: number;
  failedCapabilityCount: number;
  lastCheckedAt?: string | null;
  successRate?: number | null;
  p95LatencyMilliseconds?: number | null;
  lastFailureCode?: string | null;
};

export type NowPlayingItem = {
  deviceId: string;
  userId?: string | null;
  userName: string;
  avatarUrl?: string | null;
  client: string;
  device?: string | null;
  itemId: string;
  title: string;
  artist: string;
  album?: string | null;
  providerId: string;
  providerAccountName?: string | null;
  catalogProviderId?: string;
  sourceConfirmed?: boolean;
  cached?: boolean;
  routeReason?: string | null;
  seekRung?: string | null;
  artworkUrl?: string | null;
  positionSeconds: number | null;
  isPaused?: boolean | null;
  positionUpdatedAt?: string | null;
  durationSeconds?: number | null;
  progress?: number | null;
  lastActivity: string;
  scrobbleThresholdSeconds?: number | null;
  scrobbleEligible?: boolean;
  scrobbleDeliveries?: Array<{
    targetId: string;
    kind: string;
    state: string;
    requiresReauthentication: boolean;
    message?: string | null;
    updatedAt: string;
  }>;
  scrobbled: boolean;
};

export type HomeOverview = {
  schema: UiSchema;
  status: RuntimeStatus;
  stats: {
    linkedPlaylists: number;
    playableTracks: number;
    unresolvedTracks: number;
    activeJobs: number;
    completedListens: number;
    currentWeekListens: number;
    previousWeekListens: number;
    scrobbleDeliveries: number;
    cacheTracks?: number | null;
    keptTracks?: number | null;
    topArtist?: { name?: string | null; listens: number } | null;
  };
  providerHealth: { providers: ProviderSummary[] };
  activity: ActivityResponse;
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
  verificationMeasurementId?: string | null;
  verificationMethod?: string | null;
  id: string;
  configuration?: string;
  supported?: boolean;
  health?: string;
  ready: boolean;
  canAttempt: boolean;
  canTest?: boolean;
  testedAt?: string | null;
  latencyMilliseconds?: number | null;
  reasonCode?: string | null;
};

export type ProviderDefinition = {
  useMode?: ProviderUseMode;
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
  usesSignedSession?: boolean;
  originName?: string | null;
  audience?: string | null;
  supportedAudiences?: ("Personal" | "Shared")[];
  sharingDescription?: string | null;
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
  account?: { state?: string; logged_in?: boolean; maskedIdentifier?: string };
};

export type UiSchema = {
  activeBackend: string;
  listenersCanConnectOwnAccounts?: boolean;
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
  advanced?: boolean;
  defaultValueJson?: string | null;
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
  media?: { files: number; identified: number; needsIdentification: number; notices: string[] };
  backend?: { backend: string; endpoint: string; savesConnection: boolean; serviceAccessAction?: string; explanation: string };
  providerAccounts?: Array<{ providerId: string; reason: string; enabledAfterImport: boolean }>;
  conflicts: string[];
  warnings: string[];
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
  memory: CacheTierUsage;
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
  customLabel?: string | null;
  sourceDisplayName?: string | null;
  scope: "Personal" | "Shared";
  ownerUserId?: string | null;
  ownerDisplayName?: string | null;
  createdByUserId?: string | null;
  creatorDisplayName?: string | null;
  canManage: boolean;
  enabled: boolean;
  revision: number;
  canChangeAudience?: boolean;
  supportedAudiences?: ("Personal" | "Shared")[];
  sharingDescription?: string | null;
  configuration?: Record<string, unknown>;
  configuredFields?: string[];
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
  providerAccountId: string | null;
  providerAccountName: string | null;
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
  verificationMeasurementId?: string | null;
  verificationMethod?: string | null;
  automaticRetryAt?: string | null;
  automaticPausedAt?: string | null;
  failureCount?: number | null;
  automaticPauseReasonCode?: string | null;
};

export type ProviderCheckHistory = {
  provider: string;
  capturedAt: string;
  counts: { passed: number; failed: number; neutral: number };
  checks: {
    capability: string;
    accountLabel: string;
    accountScope: "public" | "personal" | "shared";
    testedAt: string;
    outcome: "passed" | "failed" | "neutral";
    reasonCode: string | null;
    latencyMs: number | null;
  }[];
};

export type ConnectivityResult = {
  succeeded?: boolean;
  stage?: string;
  failureCode?: string | null;
  error?: string | null;
  success?: boolean;
  healthy?: boolean;
  health?: string;
  latencyMs?: number;
  bars?: number;
  metric?: string;
  testedAt?: string;
  reasonCode?: string | null;
  seekRung?: "byte-range" | "provider-seek" | "sequential" | "unavailable";
  sampleBytes?: number;
};

export type ProviderTryCapability = "metadata" | "lyrics" | "playlist" | "streaming" | "download" | "scrobbling";
export type ProviderTryInput = "query" | "kind" | "id" | "isrc" | "url" | "title" | "artist" | "album" | "durationSeconds" | "trackId" | "quality";
export type ProviderTrySearchKind = "tracks" | "albums" | "artists";

export type ProviderTryCatalog = {
  provider: string;
  capabilities: {
    capability: ProviderTryCapability;
    accountRequirement: "none" | "optional" | "required";
    operations: { id: string; inputs: ProviderTryInput[]; kinds?: ProviderTrySearchKind[] }[];
  }[];
};

export type ProviderTryRequest = {
  capability: ProviderTryCapability;
  operation: string;
  accountId?: string | null;
  query?: string;
  kind?: ProviderTrySearchKind;
  id?: string;
  isrc?: string;
  url?: string;
  title?: string;
  artist?: string;
  album?: string;
  durationSeconds?: number;
  trackId?: string;
  quality?: "any" | "lossy" | "lossless" | "high-resolution";
};

export type ProviderTryTrack = {
  id: string;
  isBestVersion?: boolean;
  title: string;
  artists: string[];
  album: string | null;
  durationSeconds: number | null;
  isrc: string | null;
  explicit: boolean | null;
  artworkUrl: string | null;
  year: number | null;
};

export type ProviderTryPlaylist = {
  id: string;
  title: string;
  owner: string | null;
  trackCount: number | null;
  artworkUrl: string | null;
};

export type LyricsAttribution = {
  service: string;
  catalog: string;
  uploader?: { username: string; url?: string | null } | null;
  maker?: { username: string; url?: string | null } | null;
};

export type ProviderTryResult = {
  provider: string;
  capability: ProviderTryCapability;
  operation: string;
  accountId: string | null;
  succeeded: boolean;
  elapsedMs: number;
  reasonCode: string | null;
  message: string | null;
  tracks?: ProviderTryTrack[];
  albums?: { id: string; title: string; artists: string[]; year: number | null; trackCount: number | null; artworkUrl: string | null }[];
  artists?: { id: string; name: string; artworkUrl: string | null }[];
  playlist?: ProviderTryPlaylist;
  playlists?: ProviderTryPlaylist[];
  lyrics?: {
    format: "word" | "line" | "plain";
    source: string | null;
    language: string | null;
    lineCount: number;
    lines: { startMs: number | null; text: string }[];
    attribution?: LyricsAttribution | null;
  };
  media?: {
    kind: "stream" | "download";
    url: string;
    expiresAt: string;
    fileName: string | null;
    sizeBytes: number | null;
    contentType: string;
    codec: string | null;
    sampleRateHz: number | null;
    bitDepth: number | null;
    bitrateKbps: number | null;
    durationSeconds: number | null;
    quality: string | null;
    seekMode: string | null;
    startMs: number | null;
  };
};

export type CtsMeasurement = {
  providerAccountId?: string | null;
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

export type PlaylistCopy = { id: string; name: string; originalPlaylistId: string; originalTargetPlaylistId?: string | null; backendInstanceId: string; createdAt: string };

export type PlaylistLink = {
  id: string;
  virtualPlaylistId?: string;
  enabled: boolean;
  name: string;
  description?: string | null;
  artworkUrl?: string | null;
  sourceProviderId: string;
  sourcePlaylistId: string;
  followNewest?: boolean;
  sourceUpdateAvailable?: boolean;
  providerAccountId: string | null;
  targetProtocol: string;
  targetBackendInstanceId: string;
  targetPlaylistId?: string | null;
  targetCredentialReferenceId?: string | null;
  mode: "virtual" | "materialized" | "hybrid";
  projectionMode: "resolved" | "source" | "target";
  materializationMode: "reconcile" | "recreate";
  importMode: "oneTime" | "linked";
  trackRetention: "onDemand" | "keepAll";
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
  lastRunCode?: string | null;
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

export type PlaylistRematchPreview = {
  confirmationId: string;
  playlistCount: number;
  libraryCount: number;
  totalRows: number;
  localRows: number;
  exactProviderRows: number;
  genericExternalRows: number;
  unresolvedRows: number;
  confirmedManualRows: number;
  staleRevisionRows: number;
  conflictingRows: number;
  rowsToRematch: number;
  uniqueTracksToRematch: number;
  canApply: boolean;
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
  lastRunCode?: string | null;
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
  accessLabel: string;
  scope?: string;
  available?: boolean;
  reasonCode?: string | null;
};

export type PlaylistDiscoveryItem = {
  id: string;
  providerId: string;
  name: string;
  owner?: string | null;
  trackCount?: number | null;
  artworkUrl?: string | null;
  description?: string | null;
  sourceRevision?: string;
};

export type MediaTarget = {
  id: string;
  protocol: "jellyfin" | "subsonic";
  backendInstanceId: string;
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
  isCurrent?: boolean;
  isBestVersion?: boolean;
  libraryTrackId?: string | null;
  backendItemId?: string | null;
  confidence?: number | null;
  title?: string | null;
  artist?: string | null;
  album?: string | null;
  durationMilliseconds?: number | null;
  sourceIsrc?: string | null;
  candidateIsrc?: string | null;
  normalizedSourceTitle?: string | null;
  normalizedCandidateTitle?: string | null;
  artistOverlap?: number | null;
  albumEvidence?: number | null;
  durationDeltaMilliseconds?: number | null;
  isLocal?: boolean | null;
  providerTrackIds?: Record<string, string> | null;
  components?: Record<string, number> | null;
  reasons?: string[] | null;
  warnings?: string[] | null;
};

export type MatchAuthorityScope = "personal" | "household";

export type ExpectedMatchAuthority = { id: string; revision: number } | null;

export type ManualMatchAuthority = {
  id: string;
  kind: "local_match" | "provider_match" | "rejection";
  revision: number;
  authoritySnapshotId: string;
  reason: string;
  createdAt: string;
  effective: boolean;
  targetProviderId?: string | null;
  targetExternalId?: string | null;
  scope?: MatchAuthorityScope;
  canEdit?: boolean;
};

export type MatchReviewItem = {
  externalSnapshotId: string;
  providerId: string;
  providerAccountId?: string | null;
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
  manualAuthorities?: ManualMatchAuthority[];
  allowedAuthorityScopes?: MatchAuthorityScope[];
  effectiveAuthorityScope?: MatchAuthorityScope | "automatic";
  title?: string | null;
  searchQuery?: string | null;
  artist?: string | null;
  album?: string | null;
  artworkUrl?: string | null;
  sourceArtworkUrl?: string | null;
  candidateArtworkUrl?: string | null;
  isExplicit?: boolean | null;
  isrc?: string | null;
  durationMilliseconds?: number | null;
  target?: {
    providerId: string;
    externalId: string;
    title?: string | null;
    artist?: string | null;
    album?: string | null;
    durationMilliseconds?: number | null;
    artworkUrl?: string | null;
    isrc?: string | null;
  } | null;
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
    id?: string;
    providerId: string;
    externalId: string;
    scope: string;
    verification: string;
    verificationMethod?: string;
    revision?: number;
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
    ambiguous?: number;
    rejected: number;
    manualMatches?: number;
    manualRejections?: number;
    automatic?: number;
    attention: number;
  };
  pagination: { page: number; pageSize: number; total: number; totalPages: number };
};

export type TrackRematchAllPreview = {
  confirmationId: string;
  algorithmVersion: string;
  totalTracks: number;
  resolvedTracks: number;
  reviewTracks: number;
  unresolvedTracks: number;
  protectedManualTracks: number;
  automaticDecisionsToReplace: number;
  tracksToRematch: number;
  canApply: boolean;
};

export type MatchTarget = {
  id: string;
  isBestVersion?: boolean;
  backendItemId?: string | null;
  externalId?: string | null;
  externalProvider?: string | null;
  title: string;
  artist?: string | null;
  album?: string | null;
  artworkUrl?: string | null;
  durationMilliseconds?: number | null;
  confidence?: number | null;
  routingPriority?: number | null;
  matchesSourceAlbum?: boolean | null;
  isExplicit?: boolean | null;
  media?: {
    codec?: string | null;
    container?: string | null;
    bitrate?: number | null;
    sampleRate?: number | null;
    bitDepth?: number | null;
    channels?: number | null;
  } | null;
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
  lastAccessedAt: string;
  expiresAt?: string | null;
  publicationState: string;
  referenceCount?: number | null;
  explicitlyKept: boolean;
  removable: boolean;
  canDeleteKeptCopy?: boolean;
};

export type KeptCopyDeletionPreview = {
  managedFileId: string;
  revision: number;
  referenceFingerprint: string;
  title: string;
  artist: string;
  album: string | null;
  referenceCount: number;
  favoriteReferences: number;
  playlistReferences: number;
  keepReferences: number;
  otherReferences: number;
};

export type DownloadsResponse = {
  storage: string;
  files: ManagedDownload[];
  totalSize: number;
  totalSizeFormatted: string;
  count: number;
  managedCount: number;
  diagnosticCount: number;
};

export type IntelligenceScope = {
  protocol: string;
  backendInstanceId: string;
};

export type ListeningApp = {
  id: string;
  relayExternally: boolean;
  createdAt: string;
};

export type ListeningAppCreated = ListeningApp & { token: string };

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
  listeningServices?: Array<{
    id: string;
    label: string;
    configured: boolean;
    latestState?: string | null;
    requiresReauthentication?: boolean | null;
    updatedAt?: string | null;
  }>;
  songDetails?: {
    pending: number;
    resolved: number;
    unresolved: number;
    failed: number;
  };
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

export type AudioMuseConnection = { baseUrl: string; keyConfigured: boolean; revision: number | null };
export type AudioMuseStatus = { reason: string | null; accessibleLibrarySongs: number; analyzedSongs: number; coverageComplete: boolean };
export type AudioMuseSong = { id: string; title: string; artist: string; album: string | null };
export type AudioMusePreview = { reason: string | null; tracks: AudioMuseSong[]; eligibleCount: number; previewId: string | null; recipeVersion: string | null };
export type TasteStrength = { name: string; strength: number; value: number };
export type TasteLayer = { tags: TasteStrength[]; artists: TasteStrength[] };
export type TasteNudge = { kind: "tag" | "artist"; name: string; mode: "more" | "less" | "never"; expiresAt: string | null };
export type TasteProfile = { lately: TasteLayer; usually: TasteLayer; latelyWeight: number; nudges: TasteNudge[]; paused: boolean; useImportedHistory?: boolean };

export type ListeningStats = {
  period: { from: string | null; to: string; timeZoneId: string };
  playCount: number;
  activeDays: number;
  importedCount: number;
  observedSince: string | null;
  estimatedTimeFromSongLengthsMilliseconds: number;
  coverage: { unknownSongIdentityCount: number; unknownDurationCount: number };
  topSongs: Array<{ title: string; artist: string; album: string | null; playCount: number }>;
  topArtists: Array<{ artist: string; playCount: number }>;
  topAlbums: Array<{ album: string; artist: string; playCount: number }>;
};

export type HistoryImport = {
  importId: string;
  revision: string;
  displayFileName: string;
  sizeBytes: number;
  expiresAt: string;
  state: number | string;
  jobId: string | null;
  jobState: string | null;
  lastErrorCode: string | null;
  lastErrorMessage: string | null;
  importedRows: number;
  duplicateRows: number;
  resolvedRows: number;
  unresolvedRows: number;
  preview: ListeningHistoryImportPreview & { songs: number };
};

export type FollowedReleaseArtist = { providerId: string; artistId: string; displayName: string; musicBrainzArtistId?: string | null };
export type ReleaseSource = { providerId: string; albumId: string; title: string; artist: string; releaseDate: string | null; datePrecision: string; type: string; isExplicit: boolean | null; edition: string; upc: string | null; isrcs: string[]; aliases: string[]; exclusionReason: string | null };
export type ReleaseInboxEntry = { id: string; title: string; artist: string; releaseDate: string | null; datePrecision: string; type: string; isExplicit: boolean | null; edition: string; exclusionReason: string | null; sources: ReleaseSource[] };
export type ReleaseInbox = { entries: ReleaseInboxEntry[]; excluded: ReleaseInboxEntry[]; coverage: Array<{ artist: FollowedReleaseArtist; status: string; checkedAt: string | null }> };

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
  breakdowns: {
    sources: ListeningHistoryBreakdown[];
    providers: ListeningHistoryBreakdown[];
    clients: ListeningHistoryBreakdown[];
  };
  nowPlaying?: ListeningHistoryItem | null;
  recent: ListeningHistoryItem[];
};

export type ListeningHistoryBreakdown = {
  dimension: string;
  value: string;
  listenCount: number;
  durationMilliseconds: number;
};

export type ListeningHistoryActivity = {
  period: ListeningHistoryPeriod;
  currentStreakDays: number;
  longestStreakDays: number;
  buckets: Array<{
    date: string;
    count: number;
    importedCount: number;
    playbackCount: number;
    durationMilliseconds: number;
  }>;
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
  outsideRetentionRows?: number;
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
  const requestInput = typeof input === "string" ? adminUrl(input) : input;
  const response = await fetch(requestInput, {
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
    // Short failure codes need their stage for context; full sentences stand on their own.
    super(body?.stage && !/\s/.test(message) ? `${body.stage}: ${message}` : message);
  }
}

export async function json<T>(input: RequestInfo | URL, init?: RequestInit): Promise<T> {
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

export const auth = {
  session: () => json<Session>("/api/admin/auth/me"),
  login: (username: string, password: string, rememberMe: boolean, managePlaylists = false) =>
    json<Session>("/api/admin/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password, rememberMe, managePlaylists }),
    }),
  playlistConsent: () =>
    json<PlaylistConsentStatus>("/api/admin/auth/playlist-consent"),
  grantPlaylistConsent: (password: string) =>
    json<PlaylistConsentStatus>("/api/admin/auth/playlist-consent", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ password }),
    }),
  revokePlaylistConsent: () =>
    json<PlaylistConsentStatus>("/api/admin/auth/playlist-consent", { method: "DELETE" }),
  logout: () => json<{ success: boolean }>("/api/admin/auth/logout", { method: "POST" }),
};

export type ProviderFavoriteMember = {
  userId: string; displayName: string; protocol: "jellyfin" | "subsonic"; instanceLabel: string;
  profileCode: string; identityFingerprint: string; lastSeenAt: string | null;
  participation: "available" | "active" | "disabled" | "synthetic" | "identity_changed";
  linkId: string | null;
};
export type ProviderFavoriteLink = {
  id: string; jellyfin: ProviderFavoriteMember; subsonic: ProviderFavoriteMember;
  state: "merging" | "shared" | "needs_attention"; createdAt: string; revision: number;
  union: { jobId: string; state: "pending" | "running" | "retry_scheduled" | "succeeded" | "failed" | "cancelled";
    processedRows: number; seededItems: number; completedAt: string | null; errorCode: string | null; canRetry: boolean };
};
export type FavoriteLinkPair = { jellyfinUserId: string; jellyfinFingerprint: string;
  subsonicUserId: string; subsonicFingerprint: string };
export type FavoriteLinkPreview = { jellyfin: ProviderFavoriteMember; subsonic: ProviderFavoriteMember;
  confirmationVersion: number; existingLinkId: string | null };
export type FavoriteLinkReceipt = { linkId: string; jobId: string; created: boolean; replayed: boolean };
export const providerFavoriteLinks = {
  members: (protocol: "jellyfin" | "subsonic", cursor?: string, signal?: AbortSignal) => {
    const query = new URLSearchParams({ protocol, limit: "200" });
    if (cursor) query.set("cursor", cursor);
    return json<{ activeProtocol: string; items: ProviderFavoriteMember[]; nextCursor: string | null }>(
      `/api/admin/provider-favorite-links/members?${query}`, { signal });
  },
  list: (cursor?: string, signal?: AbortSignal) => {
    const query = new URLSearchParams({ limit: "50" });
    if (cursor) query.set("cursor", cursor);
    return json<{ activeProtocol: string; items: ProviderFavoriteLink[]; nextCursor: string | null }>(
      `/api/admin/provider-favorite-links?${query}`, { signal });
  },
  detail: (id: string, signal?: AbortSignal) => json<ProviderFavoriteLink>(
    `/api/admin/provider-favorite-links/${encodeURIComponent(id)}`, { signal }),
  preview: (pair: FavoriteLinkPair) => json<FavoriteLinkPreview>("/api/admin/provider-favorite-links/preview", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(pair) }),
  create: (input: FavoriteLinkPair & { requestId: string; confirmedSamePerson: boolean; confirmationVersion: number }) =>
    json<FavoriteLinkReceipt>("/api/admin/provider-favorite-links", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(input) }),
  retry: (id: string, input: { requestId: string; expectedRevision: number }) =>
    json<FavoriteLinkReceipt>(`/api/admin/provider-favorite-links/${encodeURIComponent(id)}/retry`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(input) }),
};

export const onboarding = {
  status: () => json<OnboardingState>("/api/admin/onboarding/status"),
  complete: () => json<OnboardingState>("/api/admin/onboarding/complete", { method: "POST" }),
  step: (stepId: string, action: string, expectedRevision: number) => json<OnboardingState>("/api/admin/onboarding/step", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ stepId, action, expectedRevision }),
  }),
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
  listeningApps: (scope: IntelligenceScope) =>
    json<{ items: ListeningApp[] }>(`/api/admin/intelligence/listening-apps?${intelligenceQuery(scope)}`),
  createListeningApp: (scope: IntelligenceScope, sendToConnectedServices: boolean) =>
    json<ListeningAppCreated>("/api/admin/intelligence/listening-apps",
      intelligenceBody({ ...scope, sendToConnectedServices })),
  revokeListeningApp: (scope: IntelligenceScope, id: string) =>
    json<void>(`/api/admin/intelligence/listening-apps/${encodeURIComponent(id)}`,
      intelligenceBody(scope, "DELETE")),
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
  createAudioMusePlaylist: (scope: IntelligenceScope, name: string, trackIds: string[], idempotencyKey: string) =>
    json<{ id: string; state: string }>("/api/admin/intelligence/audiomuse/generated-sets",
      intelligenceBody({ ...scope, name, trackIds, idempotencyKey })),
  audioMuseSearch: (scope: IntelligenceScope, query: string, mode: "text" | "lyrics", limit = 25) =>
    json<{ tracks: AudioMuseTrack[]; mode: string }>("/api/admin/intelligence/audiomuse/search",
      intelligenceBody({ ...scope, query, mode, limit })),
  audioMuseClusters: (scope: IntelligenceScope, limit = 10, cursor?: string) =>
    json<{ clusters: AudioMuseCluster[]; nextCursor?: string | null }>(
      `/api/admin/intelligence/audiomuse/clusters?${intelligenceQuery(scope, { limit, cursor })}`),
  audioMuseMap: (scope: IntelligenceScope, limit = 50, cursor?: string) =>
    json<AudioMuseMapPage>(`/api/admin/intelligence/audiomuse/map?${intelligenceQuery(scope, { limit, cursor })}`),
  historyOverview: (scope: IntelligenceScope, from: string | undefined, to: string | undefined, timeZoneId: string) =>
    json<ListeningHistoryOverview>(`/api/admin/intelligence/history/overview?${intelligenceQuery(scope, { from, to, timeZoneId })}`),
  history: (scope: IntelligenceScope, input: {
    from?: string;
    to?: string;
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
  historyActivity: (scope: IntelligenceScope, from: string | undefined, to: string | undefined, timeZoneId: string) =>
    json<ListeningHistoryActivity>(`/api/admin/intelligence/history/activity?${intelligenceQuery(scope, { from, to, timeZoneId })}`),
  historyTop: (scope: IntelligenceScope, kind: "artist" | "album" | "track", from: string | undefined, to: string | undefined, timeZoneId: string) =>
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
    adminUrl(`/api/admin/intelligence/history/export?${intelligenceQuery(scope)}`),
  previewHistoryImport: (scope: IntelligenceScope, file: File) => {
    const body = new FormData();
    body.append("file", file);
    for (const [key, value] of Object.entries(scope)) body.append(key, value);
    return json<ListeningHistoryImport>("/api/admin/intelligence/history/imports/preview", { method: "POST", body });
  },
  historyImport: (scope: IntelligenceScope, id: string) =>
    json<ListeningHistoryImport>(`/api/admin/intelligence/history/imports/${encodeURIComponent(id)}?${intelligenceQuery(scope)}`),
  historyImports: (scope: IntelligenceScope) =>
    json<{ items: ListeningHistoryImport[] }>(`/api/admin/intelligence/history/imports?${intelligenceQuery(scope)}`),
  changeHistoryImport: (scope: IntelligenceScope, item: ListeningHistoryImport, operation: "apply" | "resume" | "cancel") =>
    json<ListeningHistoryImport>(`/api/admin/intelligence/history/imports/${encodeURIComponent(item.importId)}/${operation}`,
      intelligenceBody({ ...scope, revision: item.revision })),
  removeHistoryImport: (scope: IntelligenceScope, item: ListeningHistoryImport) =>
    json<{ removedImport: true; removedListens: number }>(
      `/api/admin/intelligence/history/imports/${encodeURIComponent(item.importId)}`,
      intelligenceBody({ ...scope, revision: item.revision, confirmed: true }, "DELETE"),
    ),
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
  overview: () => json<HomeOverview>("/api/admin/ui/home"),
  schema: () =>
    json<UiSchema>("/api/admin/ui/schema"),
  jobs: () => json<JobResponse>("/api/admin/jobs?limit=100"),
  cancelJob: (id: string) =>
    json<{ jobId: string; state: string }>(`/api/admin/jobs/${encodeURIComponent(id)}/cancel`, {
      method: "POST",
    }),
  providers: () => json<{ providers: ProviderSummary[] }>("/api/admin/ui/provider-summaries"),
  nowPlaying: () => json<{ items: NowPlayingItem[]; capturedAt?: string | null }>("/api/admin/ui/now-playing"),
};

export type ScrobbleDelivery = {
  id: string;
  revision: number;
  targetId: string;
  state: "delivered" | "ignored" | "retrying" | "permanentfailure";
  providerCode?: string | null;
  safeMessage?: string | null;
  requiresReauthentication: boolean;
  destinationAccountId?: string | null;
  destinationAccountRevision?: number | null;
  suggestedDestinationAccountId?: string | null;
  availableDestinationAccountIds?: string[];
  sendCount: number;
  retryAfter?: string | null;
  attemptStartedAt?: string | null;
  updatedAt: string;
  submission?: { title: string; artist: string; occurredAt: string } | null;
};

export const scrobbling = {
  deliveries: (before?: string, targetId?: string) => json<{ items: ScrobbleDelivery[] }>(
    `/api/admin/scrobbling/deliveries?attentionOnly=true${targetId ? `&targetId=${encodeURIComponent(targetId)}` : ""}${before ? `&before=${encodeURIComponent(before)}` : ""}`),
  resolve: (delivery: ScrobbleDelivery, action: "retry" | "confirm-delivered", accountRevision?: number, destinationAccountId?: string) =>
    json(`/api/admin/scrobbling/deliveries/${delivery.id}/resolve`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ revision: delivery.revision, action, accountRevision, destinationAccountId }),
    }),
};

export type SpotifyLikesImportState = "Pending" | "Running" | "RetryScheduled" | "Deferred" | "Succeeded" | "Failed" | "Cancelled";
export type SpotifyLikesImportStatus = {
  jobId: string;
  state: SpotifyLikesImportState;
  processed: number;
  total: number | null;
  imported: number;
  alreadyHearted: number;
  needsReview: number;
  notFound: number;
  errorCode: string | null;
};
export const spotifyLikes = {
  start: (providerAccountId: string, requestId: string) =>
    json<{ jobId: string }>("/api/admin/spotify-likes/import", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ providerAccountId, requestId }),
    }),
  status: (jobId: string) => json<SpotifyLikesImportStatus>(`/api/admin/spotify-likes/${encodeURIComponent(jobId)}`),
};

export const sources = {
  accounts: () =>
    json<{
      listenersCanConnectOwnAccounts?: boolean;
      audienceUsers: { id: string; displayName: string }[];
      accounts: ProviderAccount[];
    }>("/api/admin/provider-accounts"),
  health: () => json<ProviderHealth[]>("/api/admin/providers/status"),
  history: (provider: string, signal?: AbortSignal) =>
    json<ProviderCheckHistory>(`/api/admin/providers/${encodeURIComponent(provider)}/health-history?limit=24`, { signal }),
  resume: (provider: string, capability: string, accountId?: string | null) =>
    json(`/api/admin/providers/${encodeURIComponent(provider)}/resume/${encodeURIComponent(capability)}${accountId ? `?accountId=${encodeURIComponent(accountId)}` : ""}`, { method: "POST" }),
  cts: () =>
    json<{ measurements: CtsMeasurement[] }>("/api/admin/provider-diagnostics/deep-stream/latest"),
  create: (input: {
    providerId: string;
    displayName: string;
    scope: "Personal" | "Shared";
    ownerUserId?: string | null;
    enabled: boolean;
    secret?: Record<string, unknown>;
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
    json<{ accountId: string; revision: number }>(`/api/admin/provider-accounts/${account.id}/secret`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ secret }),
    }),
  setAudience: (
    account: ProviderAccount,
    scope: "Personal" | "Shared",
    ownerUserId?: string | null,
  ) =>
    json<ProviderAccount>(`/api/admin/provider-accounts/${account.id}/audience`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ scope, ownerUserId, expectedRevision: account.revision }),
    }),
  remove: (id: string) =>
    json<void>(`/api/admin/provider-accounts/${id}`, { method: "DELETE" }),
  test: (account: ProviderAccount, capability?: string) =>
    json<ConnectivityResult>(
      `/api/admin/providers/test/${encodeURIComponent(account.providerId)}${capability ? `/${encodeURIComponent(capability)}` : ""}?accountId=${encodeURIComponent(account.id)}`,
      { method: "POST" },
    ),
  testWithoutAccount: (providerId: string, capability: string) =>
    json<ConnectivityResult>(
      `/api/admin/providers/test/${encodeURIComponent(providerId)}/${encodeURIComponent(capability)}`,
      { method: "POST" },
    ),
  tryOperations: (providerId: string, accountId?: string | null, signal?: AbortSignal) =>
    json<ProviderTryCatalog>(
      `/api/admin/providers/${encodeURIComponent(providerId)}/try${accountId ? `?accountId=${encodeURIComponent(accountId)}` : ""}`,
      { signal },
    ),
  try: (providerId: string, body: ProviderTryRequest, signal?: AbortSignal) =>
    json<ProviderTryResult>(`/api/admin/providers/${encodeURIComponent(providerId)}/try`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
      signal,
    }),
  deepStream: (providerId: string, providerAccountId?: string | null, quality = 0) =>
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
        providerId,
        providerAccountId,
        quality,
      }),
    }),
};

export type StorageBackup = {
  id: string;
  status: "verified" | "unverified" | "changed" | "invalid";
  createdAt: string;
  verifiedAt: string | null;
  fileName: string;
  bytes: number;
  sha256: string | null;
  schemaVersion: string;
  applicationVersion: string;
  includesKeyRing: true;
  origin?: "manual" | "scheduled" | "legacy";
  retentionBuckets?: Array<"daily" | "weekly" | "manual">;
  verification?: "verified" | "unverified" | "changed" | "invalid";
};

export type BackupPolicy = {
  enabled: boolean;
  timeUtc: string;
  timeZone: "UTC";
  directory: string;
  dailyRetentionCount: number;
  weeklyRetentionCount: number;
  manualRetentionCount: number;
  revision: string;
};
export type BackupPolicyInput = Omit<BackupPolicy, "timeZone" | "revision">;
export type BackupStatus = {
  state: "idle" | "queued" | "running" | "unavailable" | "restore-pending";
  directoryState: "unchecked" | "ready" | "unavailable" | "changed";
  activeJobId: string | null;
  lastSuccessAt: string | null;
  nextRunAt: string | null;
  lastBytes: number | null;
  lastFailure: { code: string; message: string; at: string } | null;
};
export type StorageOverview = {
  storage: { provider?: string; readiness?: string; checkedAt?: string };
  backups: StorageBackup[];
  restorePending: boolean;
  backupPolicy?: BackupPolicy;
  backupStatus?: BackupStatus;
};

export const settings = {
  config: () => json<Record<string, unknown>>("/api/admin/config"),
  testChallengeSolver: (url: string) =>
    json<{ ok: boolean; message: string; version?: string }>("/api/admin/challenge-solver/test", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ url, timeoutSeconds: 60 }),
    }),
  save: (updates: Record<string, string>) =>
    json<{ message: string; updatedKeys: string[]; requiresRestart?: boolean }>("/api/admin/config", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ updates }),
    }),
  storage: () => json<StorageOverview>("/api/admin/storage"),
  backup: () => json<{ jobId: string; created: boolean; message?: string }>("/api/admin/storage/backups", { method: "POST" }),
  saveBackupPolicy: (input: BackupPolicyInput & { expectedRevision: string }) =>
    json<{ backupPolicy: BackupPolicy; backupStatus: BackupStatus }>("/api/admin/storage/backups/configuration", {
      method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(input),
    }),
  restore: (file: File) => {
    const body = new FormData();
    body.append("backup", file);
    return json<{ restartRequired: true; message: string }>("/api/admin/storage/restore", { method: "POST", body });
  },
  backupDownloadUrl: (id: string) =>
    adminUrl(`/api/admin/storage/backups/${encodeURIComponent(id)}/download`),
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
  migrationStatus: () => json<{
    available: boolean;
    completed: boolean;
    sourcePresent: boolean;
    serviceAccessAvailable?: boolean;
    firstRun: boolean;
    lastAppliedAt?: string | null;
  }>("/api/admin/config/migration/status"),
  previewServiceAccess: () => json<EnvMigrationPreview>("/api/admin/config/migration/preview-service-access", { method: "POST" }),
  previewMountedMigration: () => json<EnvMigrationPreview>("/api/admin/config/migration/preview-mounted", { method: "POST" }),
  previewMigration: (file: File) => {
    const body = new FormData();
    body.append("file", file);
    return json<EnvMigrationPreview>("/api/admin/config/migration/preview", {
      method: "POST",
      body,
    });
  },
  applyMigration: (preview: EnvMigrationPreview) =>
    json<{ success: boolean; alreadyApplied: boolean; backendServiceAccess?: string; requiresRestart?: boolean; mediaFilesQueued?: number; mediaJobId?: string; accountChecks?: Array<{ providerId: string; reason: string; enabled: boolean }> }>("/api/admin/config/migration/apply", {
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

export type LyricsChoiceStamp = { id: string; revision: number };
export type LyricsChoiceRevision = { personal: LyricsChoiceStamp | null; household: LyricsChoiceStamp | null };
export type RecordingLyricsChoice = { id: string; sourceId: string; revision: number };
export type RecordingLyricsChoiceLayers = {
  personal: RecordingLyricsChoice | null;
  household: RecordingLyricsChoice | null;
  effective: RecordingLyricsChoice | null;
  revision: LyricsChoiceRevision;
};
export type RecordingLyricsSource = {
  sourceId: string;
  format: "synced" | "plain" | null;
  preview: string | null;
  previewRevision: string | null;
  available: boolean;
  truncated: boolean;
  attribution?: LyricsAttribution | null;
};
export type RecordingLyricsReview = {
  recordingId: string;
  scope: MatchAuthorityScope;
  song: { title: string; artist: string; album: string; durationSeconds: number | null };
  choices: RecordingLyricsChoiceLayers;
  sources: RecordingLyricsSource[];
};
const recordingLyricsPath = (id: string) => `/api/admin/recordings/${encodeURIComponent(id)}/lyrics`;
export const recordingLyrics = {
  get: (id: string, scope: MatchAuthorityScope, signal?: AbortSignal) =>
    json<RecordingLyricsReview>(`${recordingLyricsPath(id)}?scope=${scope}`, { signal }),
  choose: (id: string, sourceId: string, scope: MatchAuthorityScope, expectedRevision: LyricsChoiceRevision,
    previewRevision: string, signal?: AbortSignal) => json<RecordingLyricsChoiceLayers>(recordingLyricsPath(id), {
      method: "PUT", headers: { "Content-Type": "application/json" }, signal,
      body: JSON.stringify({ sourceId, scope, expectedRevision, previewRevision }),
    }),
  clear: (id: string, scope: MatchAuthorityScope, expectedRevision: LyricsChoiceRevision, signal?: AbortSignal) =>
    json<RecordingLyricsChoiceLayers>(recordingLyricsPath(id), {
      method: "DELETE", headers: { "Content-Type": "application/json" }, signal,
      body: JSON.stringify({ scope, expectedRevision }),
    }),
};

export type ProviderUseMode = "automatic" | "manualOnly" | "off";
export type ProviderUseSettings = {
  personalRevision: string;
  householdRevision: string;
  providers: { providerId: string; householdMode: ProviderUseMode; personalMode: ProviderUseMode | null; effectiveMode: ProviderUseMode; canChoosePersonal: boolean }[];
};
export const providerUse = {
  get: () => json<ProviderUseSettings>("/api/admin/providers/use"),
  save: (providerId: string, mode: ProviderUseMode | null, scope: "personal" | "household", expectedRevision: string) =>
    json<ProviderUseSettings>(`/api/admin/providers/use/${encodeURIComponent(providerId)}`, {
      method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ mode, scope, expectedRevision }),
    }),
};

export const listeningPreferences = {
  get: () => json<PersonalListeningPreferences>("/api/admin/preferences"),
  lock: (userId: string) => json<PersonalListeningPreferences>(`/api/admin/preferences/users/${encodeURIComponent(userId)}/clean-only-lock`),
  saveLock: (userId: string, locked: boolean, expectedRevision: string) =>
    json<PersonalListeningPreferences>(`/api/admin/preferences/users/${encodeURIComponent(userId)}/clean-only-lock`, {
      method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ locked, expectedRevision }),
    }),
  copies: (userId: string) => json<ListenerPlaylistCopy[]>(`/api/admin/listeners/${encodeURIComponent(userId)}/playlist-forks`),
  save: (values: {
    explicitFilter: ExplicitFilter;
    showExternalLabel: boolean;
    showExplicitLabel: boolean;
    playbackStart?: PlaybackStart;
    lyricsOrder?: string;
    expectedRevision: string;
  }) => json<PersonalListeningPreferences>("/api/admin/preferences", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(values),
  }),
  reset: (expectedRevision: string) =>
    json<PersonalListeningPreferences>("/api/admin/preferences", {
      method: "DELETE",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ expectedRevision }),
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
  hasPermissions: boolean;
  description?: string | null;
  author?: string | null;
  iconUrl?: string | null;
  capabilities?: string[];
  compatibility?: string | null;
  usesSignedSession?: boolean;
  failureCode?: string | null;
  stagedAt?: string | null;
  revision: number;
};

export type ExtensionSessionState = "signed_out" | "verification_pending" | "signed_in" | "expired";

export type ExtensionSession = {
  accountId?: string | null;
  state: ExtensionSessionState;
  expiresAt?: string | null;
  verificationUrl?: string | null;
  reasonCode?: string | null;
  renewsAutomatically?: boolean;
  lastRenewalFailure?: string | null;
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
  retry: (item: ExtensionPackage) =>
    json<ExtensionPackage>(`/api/admin/extensions/packages/${item.id}/retry`, revisionBody(item.revision)),
  disable: (item: ExtensionPackage) =>
    json<void>(`/api/admin/extensions/packages/${item.id}/disable`, revisionBody(item.revision)),
  revokePermissions: (item: ExtensionPackage) =>
    json<ExtensionPackage>(`/api/admin/extensions/packages/${item.id}/permissions/revoke`, revisionBody(item.revision)),
  uninstall: (item: ExtensionPackage) =>
    json<ExtensionPackage>(`/api/admin/extensions/packages/${item.id}`, {
      ...revisionBody(item.revision),
      method: "DELETE",
    }),
  session: (item: ExtensionPackage, accountId?: string) =>
    json<ExtensionSession>(`/api/admin/extensions/packages/${item.id}/session${accountId ? `?accountId=${encodeURIComponent(accountId)}` : ""}`),
  startSession: (item: ExtensionPackage, accountId?: string) =>
    json<ExtensionSession>(`/api/admin/extensions/packages/${item.id}/session/start`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ expectedRevision: item.revision, accountId }),
    }),
  completeSession: (item: ExtensionPackage, grant: string, accountId?: string) =>
    json<ExtensionSession>(`/api/admin/extensions/packages/${item.id}/session/grant`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ grant, expectedRevision: item.revision, accountId }),
    }),
  clearSession: (item: ExtensionPackage, accountId?: string) =>
    json<ExtensionSession>(`/api/admin/extensions/packages/${item.id}/session`, {
      method: "DELETE", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ expectedRevision: item.revision, accountId }),
    }),
};

export const eventLog = {
  list: (params: { limit?: number; before?: string; beforeId?: string; scope?: "mine" | "everyone" } = {}) => {
    const query = new URLSearchParams({ limit: String(params.limit ?? 50), scope: params.scope ?? "mine" });
    if (params.before) query.set("before", params.before);
    if (params.beforeId) query.set("beforeId", params.beforeId);
    return json<ActivityResponse>(`/api/admin/ui/activity?${query}`);
  },
};

export const downloads = {
  previewKeptDeletion: (path: string) => json<KeptCopyDeletionPreview>("/api/admin/downloads/kept/delete-preview", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ path }),
  }),
  deleteKeptCopy: (preview: KeptCopyDeletionPreview, requestId: string) =>
    json<{ jobId: string; queued: boolean }>("/api/admin/downloads/kept/delete", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ managedFileId: preview.managedFileId, revision: preview.revision,
        referenceFingerprint: preview.referenceFingerprint, confirmation: "Delete kept copy", requestId }),
    }),
  list: (storage: "cache" | "kept") =>
    json<DownloadsResponse>(`/api/admin/downloads?storage=${storage}`),
  keep: (path: string) =>
    json<{ success: boolean; queued: boolean; jobId: string; message: string }>(
      `/api/admin/downloads/promote?path=${encodeURIComponent(path)}`,
      { method: "POST", headers: { "Idempotency-Key": crypto.randomUUID() } },
    ),
  remove: (path: string, storage: "cache" | "kept") =>
    json<{ success: boolean; released?: boolean; message?: string }>(
      `/api/admin/downloads?path=${encodeURIComponent(path)}&storage=${storage}`,
      { method: "DELETE" },
    ),
  removeAll: (storage: "cache" | "kept") =>
    json<{
      success: boolean;
      deletedCount: number;
      releasedCount?: number;
      skippedUnknown: number;
      skippedReferenced: number;
      message?: string;
    }>(
      `/api/admin/downloads/all?storage=${storage}`,
      { method: "DELETE" },
    ),
  fileUrl: (path: string, storage: "cache" | "kept") =>
    adminUrl(`/api/admin/downloads/file?path=${encodeURIComponent(path)}&storage=${storage}`),
};

export const audioMuse = {
  status: () => json<AudioMuseStatus>("/api/admin/audiomuse/status"),
  connection: () => json<AudioMuseConnection>("/api/admin/audiomuse/connection"),
  configure: (baseUrl: string, apiKey: string, revision: number | null) => json<AudioMuseConnection>("/api/admin/audiomuse/connection", {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ baseUrl, ...(apiKey ? { apiKey } : {}), revision }),
  }),
  seeds: (query: string) => json<AudioMusePreview>(`/api/admin/audiomuse/seeds?${new URLSearchParams({ query })}`),
  similar: (seedId: string) => json<AudioMusePreview>("/api/admin/audiomuse/similar", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ seedId, n: 25 }),
  }),
  mood: (mood: "calm" | "workout") => json<AudioMusePreview>("/api/admin/audiomuse/mood", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ mood, n: 25 }),
  }),
  save: (previewId: string, name: string) => json<{ reason: string | null; playlistId: string | null }>("/api/admin/audiomuse/save", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ previewId, name }),
  }),
};

export const taste = {
  get: () => json<TasteProfile>("/api/admin/taste"),
  nudge: (kind: TasteNudge["kind"], name: string, mode: TasteNudge["mode"], todayOnly: boolean, timeZoneId: string) => json<TasteProfile>("/api/admin/taste/nudge", {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ kind, name, mode, todayOnly, timeZoneId }),
  }),
  remove: (kind: TasteNudge["kind"], name: string) => json<TasteProfile>(`/api/admin/taste/nudge?${new URLSearchParams({ kind, name })}`, { method: "DELETE" }),
  pause: (paused: boolean) => json<TasteProfile>(`/api/admin/taste/${paused ? "pause" : "resume"}`, { method: "POST" }),
  importedHistory: (enabled: boolean) => json<TasteProfile>("/api/admin/taste/imported-history", {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled }),
  }),
  reset: (clearNudges: boolean) => json<TasteProfile>("/api/admin/taste/reset", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ clearNudges }),
  }),
};

export const listeningStats = {
  get: (period = "30d", year = "") => json<ListeningStats>(`/api/admin/stats?${new URLSearchParams(year ? { year } : { period })}`),
};

export const historyImports = {
  list: () => json<HistoryImport[]>("/api/admin/history/imports"),
  get: (id: string) => json<HistoryImport>(`/api/admin/history/imports/${encodeURIComponent(id)}`),
  confirm: (item: HistoryImport) => json<HistoryImport>(`/api/admin/history/imports/${encodeURIComponent(item.importId)}/confirm`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ revision: item.revision }),
  }),
  remove: (item: HistoryImport) => json<{ removedListens: number }>(`/api/admin/history/imports/${encodeURIComponent(item.importId)}?${new URLSearchParams({ revision: item.revision })}`, { method: "DELETE" }),
  upload: (file: File, progress: (bytes: number) => void, signal: AbortSignal) => new Promise<HistoryImport>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    const abort = () => xhr.abort();
    xhr.open("POST", adminUrl(`/api/admin/history/imports?${new URLSearchParams({ fileName: file.name })}`));
    xhr.setRequestHeader("Content-Type", "application/octet-stream");
    xhr.responseType = "json";
    xhr.upload.onprogress = event => progress(event.loaded);
    xhr.onload = () => {
      const body = normalizeResponse(xhr.response);
      if (xhr.status >= 200 && xhr.status < 300 && body) resolve(body as HistoryImport);
      else reject(new ApiError(xhr.status, xhr.statusText, body));
    };
    xhr.onerror = () => reject(new Error("Upload failed"));
    xhr.onabort = () => reject(new Error("Upload cancelled"));
    xhr.onloadend = () => signal.removeEventListener("abort", abort);
    signal.addEventListener("abort", abort, { once: true });
    if (signal.aborted) { signal.removeEventListener("abort", abort); reject(new Error("Upload cancelled")); }
    else xhr.send(file);
  }),
};

export const releaseInbox = {
  get: () => json<ReleaseInbox>("/api/admin/releases"),
  artists: () => json<FollowedReleaseArtist[]>("/api/admin/releases/artists"),
  followFromHearts: () => json<{ followed: number }>("/api/admin/releases/artists/from-hearts", { method: "POST" }),
  unfollow: (providerId: string, artistId: string) => json<void>(`/api/admin/releases/artists/${encodeURIComponent(providerId)}/${encodeURIComponent(artistId)}`, { method: "DELETE" }),
  dismiss: (id: string) => json<void>(`/api/admin/releases/${encodeURIComponent(id)}`, { method: "DELETE" }),
};

export const playlistLinks = {
  list: () => json<{ playlistLinks: PlaylistLink[] }>("/api/admin/playlist-links"),
  copies: (listenerId: string) => json<PlaylistCopy[]>(`/api/admin/listeners/${encodeURIComponent(listenerId)}/playlist-forks`),
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
  previewRematch: () =>
    json<PlaylistRematchPreview>("/api/admin/playlist-links/rematch/preview"),
  applyRematch: (confirmationId: string) =>
    json<{ jobId: string; created: boolean }>("/api/admin/playlist-links/rematch/apply", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ confirmationId }),
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
    providers: Array<{ id: string; displayName: string; supportsPublicRead?: boolean }>;
  }>("/api/admin/playlist-sources"),
  missingSongs: async (id: string, format: "csv" | "json") => {
    const response = await request(`/api/admin/playlist-links/${encodeURIComponent(id)}/missing-songs?format=${format}`);
    return response.blob();
  },
  publicUserPlaylists: (providerId: string, sourceUserId: string, cursor = "") => {
    const params = new URLSearchParams({ limit: "100", sourceUserId });
    if (cursor) params.set("cursor", cursor);
    return json<{ items: PlaylistDiscoveryItem[]; nextCursor?: string; isPartial: boolean }>(
      `/api/admin/playlist-sources/public/${encodeURIComponent(providerId)}/playlists?${params}`,
    );
  },
  sourcePlaylists: (accountId: string, query = "", cursor = "") => {
    const params = new URLSearchParams({ limit: "100" });
    if (query) params.set("query", query);
    if (cursor) params.set("cursor", cursor);
    return json<{ items: PlaylistDiscoveryItem[]; nextCursor?: string | null; isPartial?: boolean }>(
      `/api/admin/playlist-sources/${encodeURIComponent(accountId)}/playlists?${params}`,
    );
  },
  targets: () => json<{ targets: MediaTarget[] }>("/api/admin/media-targets"),
  libraryIndexCounts: () => json<{ trackCount: number }>("/api/admin/library-index/counts"),
  enqueueLibraryIndex: (credentialReferenceId: string) =>
    json<{ jobId: string; created: boolean; generation: number }>("/api/admin/library-index/enqueue", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        credentialReferenceId,
        pageSize: 200,
      }),
    }),
  targetPlaylists: (targetId: string, query = "", cursor = "") => {
    const params = new URLSearchParams({ limit: "100" });
    if (query) params.set("query", query);
    if (cursor) params.set("cursor", cursor);
    return json<{ items: TargetPlaylist[]; nextCursor?: string | null }>(
      `/api/admin/media-targets/${encodeURIComponent(targetId)}/playlists?${params}`,
    );
  },
  create: (input: {
    providerAccountId: string | null;
    sourceProviderId: string;
    sourcePlaylistId: string;
    followNewest?: boolean;
    targetProtocol: string;
    targetBackendInstanceId: string;
    targetCredentialReferenceId?: string | null;
    targetPlaylistId?: string | null;
    mode: "virtual" | "materialized" | "hybrid";
    projectionMode?: "resolved" | "source" | "target";
    materializationMode: "reconcile" | "recreate";
    importMode: "oneTime" | "linked";
    trackRetention: "onDemand" | "keepAll";
    mirrorStaleEntries: boolean;
    preserveManualEntries: boolean;
    syncName: boolean;
    syncDescription: boolean;
    syncArtwork: boolean;
  }) => json<{ id: string; initialJobId?: string | null }>("/api/admin/playlist-links", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(input),
  }),
  update: (id: string, input: {
    expectedRevision: number;
    mode: "virtual" | "materialized" | "hybrid";
    projectionMode: "resolved" | "source" | "target";
    materializationMode: "reconcile" | "recreate";
    importMode?: "oneTime" | "linked";
    trackRetention?: "onDemand" | "keepAll";
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
  }) => json<PlaylistLink & { retentionQueued?: number | null }>(`/api/admin/playlist-links/${encodeURIComponent(id)}`, {
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
    backendLibraryId?: string;
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
  searchLocal: (query: string, externalSnapshotId: string, backendLibraryId = "") =>
    json<{ tracks: MatchTarget[] }>(
      `/api/admin/track-matches/targets/local?query=${encodeURIComponent(query)}&backendLibraryId=${encodeURIComponent(backendLibraryId)}&externalSnapshotId=${encodeURIComponent(externalSnapshotId)}`,
    ),
  searchProviders: (query: string, externalSnapshotId: string) => {
    const params = new URLSearchParams({ query, externalSnapshotId, limit: "50" });
    return json<{ tracks: MatchTarget[]; providers: string[] }>(
      `/api/admin/track-matches/targets/provider?${params}`,
    );
  },
  resolve: (
    externalSnapshotId: string,
    target: (
      | { targetType: "local"; libraryTrackId: string; reason: string }
      | { targetType: "provider"; externalProvider: string; externalId: string; reason: string }
      | { targetType: "reject"; reason: string }
    ) & {
      authorityScope: MatchAuthorityScope;
      expectedAuthority: ExpectedMatchAuthority;
    },
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


export const playTraces = {
  list: (before = "") => json<PlayTrace[]>(`/api/admin/play-traces?limit=50${before ? `&before=${encodeURIComponent(before)}` : ""}`),
  detail: (id: string) => json<PlayTrace>(`/api/admin/play-traces/${encodeURIComponent(id)}`),
  diagnostics: async (id: string) => (await request(`/api/admin/play-traces/${encodeURIComponent(id)}/diagnostics`)).text(),
};

export const activityWork = {
  jobs: (view: "queue" | "history", state = "", type = "", before = "", beforeId = "", scope: "mine" | "everyone" = "mine") => {
    const query = new URLSearchParams({ view, limit: "50", scope });
    if (state) query.set("state", state);
    if (type) query.set("type", type);
    if (before && beforeId) { query.set("before", before); query.set("beforeId", beforeId); }
    return json<JobResponse>(`/api/admin/jobs?${query}`);
  },
  detail: (id: string, before = "", beforeId = "") => {
    const query = new URLSearchParams({ limit: "25" });
    if (before && beforeId) { query.set("before", before); query.set("beforeId", beforeId); }
    return json<JobDetail>(`/api/admin/jobs/${encodeURIComponent(id)}?${query}`);
  },
  tasks: (scope: "mine" | "everyone" = "mine") => json<{ items: ActivityTask[]; hasMore?: boolean }>(scope === "mine" ? "/api/admin/activity/tasks" : `/api/admin/activity/tasks?scope=${scope}`),
};

export type OidcAccess = { enabled: boolean; authority: string; clientId: string; publicUrl: string; displayName: string; hasClientSecret: boolean; needsReview: boolean; revision: string };
export type BackendAccess = { credentialKind?: string; supported: boolean; connectionSaved: boolean; configured: boolean; needsReview: boolean; revision: string };
export type FileVerification = { available: boolean; pending: boolean; needsReview: boolean; jobId?: string; jobState?: string; verifiedAt?: string; files: number };
export const setupAccess = {
  initial: () => json<{ configured: boolean; storageReady: boolean; csrfToken: string }>("/api/admin/auth/setup"),
  connect: (value: object, csrf: string) => json<{ requiresRestart: boolean }>("/api/admin/auth/setup", accessBody(value, csrf, "POST")),
  oidc: () => json<{ settings: OidcAccess; csrfToken: string }>("/api/admin/access/oidc"),
  backend: () => json<{ settings: BackendAccess; csrfToken: string }>("/api/admin/access/backend"),
  save: (kind: "oidc" | "backend", value: object, csrf: string) => json<{ requiresRestart: boolean }>(`/api/admin/access/${kind}`, accessBody(value, csrf, "PUT")),
  files: () => json<{ settings: FileVerification; csrfToken: string }>("/api/admin/storage/files"),
  verify: (csrf: string) => json<{ message: string }>("/api/admin/storage/files/verify", accessBody({}, csrf, "POST")),
};
function accessBody(value: object, csrf: string, method: string) {
  return { method, headers: { "Content-Type": "application/json", "X-Allstarr-CSRF": csrf }, body: JSON.stringify(value) };
}

export type AlertChannelKind = "ntfy" | "discord" | "webhook";
export type AlertChannel = { id: string; displayName: string; kind: AlertChannelKind; enabled: boolean; household: boolean; hasDestination: boolean; revision: number };
export type AlertChannelInput = { displayName: string; kind: AlertChannelKind; enabled: boolean; household: boolean; destination?: string; expectedRevision: number };
export type AlertDelivery = { id: string; channelId: string; incidentId?: string | null; event?: string; kind?: string; severity?: string;
  status: string; attempt?: number; nextAttemptAt?: string | null; safeError?: string | null; createdAt: string };
export type AlertIncident = { id: string; kind: string; severity: string; state: string; openedAt?: string | null;
  resolvedAt?: string | null; lastObservedAt?: string; revision?: number; resource?: string; message: string; action: string };
export type ProblemAlertThresholds = { warningBytes: number; warningPercent: number; criticalBytes: number; criticalPercent: number;
  hysteresisBytes: number; hysteresisPercent: number; cooldownMinutes: number };
export type AlertSettings = { channels: AlertChannel[]; deliveries: AlertDelivery[]; thresholds: ProblemAlertThresholds | null;
  permittedDestinations: string[] | null; revision: number };
export const problemAlerts = {
  read: () => json<AlertSettings>("/api/admin/alerts"),
  incidents: () => json<AlertIncident[]>("/api/admin/alerts/incidents"),
  saveChannel: (id: string | null, input: AlertChannelInput) => json<AlertChannel>(
    id ? `/api/admin/alerts/channels/${encodeURIComponent(id)}` : "/api/admin/alerts/channels",
    { method: id ? "PUT" : "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(input) }),
  test: (id: string) => json<{ deliveryId: string; preview: { message: string; action: string } }>(
    `/api/admin/alerts/channels/${encodeURIComponent(id)}/test`, { method: "POST" }),
  thresholds: (thresholds: ProblemAlertThresholds, expectedRevision: number) => json<AlertSettings>("/api/admin/alerts/thresholds",
    { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ thresholds, expectedRevision }) }),
  destinations: (permittedDestinations: string[], expectedRevision: number) => json<AlertSettings>("/api/admin/alerts/destinations",
    { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ permittedDestinations, expectedRevision }) }),
};
