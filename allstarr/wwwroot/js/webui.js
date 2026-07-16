import { LitElement, html, nothing } from "/js/lit-3.3.3.js";

const THEME_KEY = "allstarr-theme";
const DEFAULT_ROUTE = "/home";
const SETUP_GUIDE_DISMISSED_KEY = "allstarr-setup-guide-dismissed";
const SETUP_GUIDE_STEP_KEY = "allstarr-setup-guide-step";
const SETUP_GUIDE_LAST_STEP = 4;

function normalizeRoute(hash = window.location.hash) {
  const route = hash.replace(/^#/, "") || DEFAULT_ROUTE;
  return route.startsWith("/") ? route : `/${route}`;
}

function routeParts(route) {
  return route.split("/").filter(Boolean);
}

function getPathValue(source, path, fallback = "") {
  if (!source || !path) {
    return fallback;
  }

  return path.split(".").reduce((current, key) => {
    if (current && Object.prototype.hasOwnProperty.call(current, key)) {
      return current[key];
    }

    return undefined;
  }, source) ?? fallback;
}

function setPathValue(source, path, value) {
  if (!source || !path) {
    return;
  }

  const parts = path.split(".");
  let current = source;
  for (const part of parts.slice(0, -1)) {
    if (!current[part] || typeof current[part] !== "object") {
      current[part] = {};
    }
    current = current[part];
  }
  current[parts.at(-1)] = value;
}

function display(value, fallback = "-") {
  if (value === null || value === undefined || value === "") {
    return fallback;
  }
  return String(value);
}

function titleCase(value) {
  return display(value)
    .replace(/[_-]+/g, " ")
    .replace(/\b\w/g, (char) => char.toUpperCase());
}

function formatDate(value) {
  if (!value) {
    return "-";
  }

  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? "-" : parsed.toLocaleString();
}

function percent(value) {
  const numeric = Number(value);
  if (!Number.isFinite(numeric)) {
    return 0;
  }
  return Math.max(0, Math.min(100, numeric * (numeric <= 1 ? 100 : 1)));
}

function asArray(value) {
  return Array.isArray(value) ? value : [];
}

function parseBoolValue(value) {
  if (typeof value === "boolean") {
    return value;
  }
  if (typeof value === "number") {
    return value !== 0;
  }
  if (typeof value === "string") {
    return ["true", "1", "yes", "on", "enabled"].includes(value.trim().toLowerCase());
  }
  return false;
}

function splitCsv(value) {
  return String(value || "")
    .split(",")
    .map((item) => item.trim().toLowerCase())
    .filter(Boolean);
}

function joinCsv(values) {
  return [...new Set(asArray(values).map((item) => String(item).trim().toLowerCase()).filter(Boolean))].join(",");
}

function normalizedFieldValue(field, value) {
  if (field.type === "toggle") {
    return parseBoolValue(value) ? "true" : "false";
  }
  return String(value ?? "");
}

function providerMark(provider) {
  const id = String(provider?.id || provider?.Id || provider?.name || provider?.Name || "").toLowerCase();
  const marks = {
    spotify: "Spotify",
    applemusic: "Apple Music",
    "apple-download": "Apple download",
    deezer: "Deezer",
    qobuz: "Qobuz",
    squidwtf: "SquidWTF",
    musicbrainz: "MusicBrainz",
    lyricsplus: "Lyrics+",
    lrclib: "LRCLib",
    extensions: "Extensions",
  };
  return marks[id] || titleCase(provider?.name || provider?.Name || id);
}

function providerLogoUrl(provider) {
  const id = String(provider?.id || provider?.Id || provider?.name || provider?.Name || "").toLowerCase();
  const logoId = id === "apple-download" ? "applemusic" : id;
  const logos = new Set(["spotify", "applemusic", "deezer", "qobuz", "musicbrainz"]);
  return logos.has(logoId) ? `/images/providers/${logoId}.svg` : "";
}

const providersWithoutCardMark = new Set(["lyricsplus", "squidwtf", "lrclib"]);

function providerDisplayName(providerId, providers = []) {
  const provider = asArray(providers).find((item) =>
    String(item?.id || item?.Id || "").toLowerCase() === String(providerId).toLowerCase());
  return provider?.name || provider?.Name || providerMark({ id: providerId });
}

function appleLoginState(value) {
  const rawState = value?.login_state || value?.account?.state || value?.auth?.state || value?.state || "";
  const state = String(rawState).trim().toLowerCase().replaceAll("-", "_").replaceAll(" ", "_");
  if (["authenticated", "logged_in", "ready"].includes(state)) {
    return "authenticated";
  }
  if (["awaiting_2fa", "awaiting2fa", "needs_2fa", "2fa_required", "two_factor_required"].includes(state)) {
    return "awaiting_2fa";
  }
  if (["logged_out", "unauthenticated"].includes(state)) {
    return "logged_out";
  }
  if (!state && (value?.logged_in || value?.account?.logged_in || value?.auth?.logged_in)) {
    return "authenticated";
  }
  return state || "unknown";
}

function isAwaitingApple2fa(value) {
  return appleLoginState(value) === "awaiting_2fa";
}

function appleAuthFeedback(value, operation) {
  const state = appleLoginState(value);
  if (state === "authenticated") {
    return {
      state: "success",
      message: operation === "2fa" ? "Apple Music 2FA accepted and account login confirmed." : "Apple Music login confirmed.",
    };
  }
  if (state === "awaiting_2fa") {
    return {
      state: "warning",
      message: operation === "2fa" ? "Apple Music still needs a valid 2FA code." : "Apple Music needs a 2FA code.",
    };
  }
  return {
    state: operation === "2fa" ? "error" : "warning",
    message: operation === "2fa"
      ? "Apple Music 2FA did not authenticate the account. Check the code and try again."
      : "Apple Music login has not authenticated the account yet.",
  };
}

async function readErrorMessage(response, fallback) {
  try {
    const data = await response.clone().json();
    const protocolError = data?.["subsonic-response"]?.error?.message;
    const directError = typeof data.error === "string" ? data.error : data.error?.message;
    return data.detail || directError || data.message || protocolError || `${fallback} (HTTP ${response.status})`;
  } catch {
    try {
      const text = await response.text();
      return text || `${fallback} (HTTP ${response.status})`;
    } catch {
      return `${fallback} (HTTP ${response.status})`;
    }
  }
}

async function requestJson(url, options = {}, fallback = "Request failed") {
  const response = await fetch(url, {
    credentials: "same-origin",
    ...options,
  });

  if (!response.ok) {
    const error = new Error(await readErrorMessage(response, fallback));
    error.status = response.status;
    throw error;
  }

  return response.json();
}

async function requestBlob(url, options = {}, fallback = "Request failed") {
  const response = await fetch(url, {
    credentials: "same-origin",
    ...options,
  });

  if (!response.ok) {
    const error = new Error(await readErrorMessage(response, fallback));
    error.status = response.status;
    throw error;
  }

  return response.blob();
}

function jsonBody(payload, method = "POST") {
  return {
    method,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  };
}

const ENV_MIGRATION_ENDPOINTS = Object.freeze({
  status: "/api/admin/config/migration/status",
  preview: "/api/admin/config/migration/preview",
  apply: "/api/admin/config/migration/apply",
});

const ONBOARDING_ENDPOINTS = Object.freeze({
  status: "/api/admin/onboarding/status",
  complete: "/api/admin/onboarding/complete",
});

const API = {
  me: () => requestJson("/api/admin/auth/me", { cache: "no-store" }, "Authentication required"),
  login: (username, password, rememberMe) =>
    requestJson("/api/admin/auth/login", jsonBody({ username, password, rememberMe }), "Authentication failed"),
  logout: () => requestJson("/api/admin/auth/logout", { method: "POST" }, "Logout failed"),
  schema: () => requestJson("/api/admin/ui/schema", { cache: "no-store" }, "Failed to load UI schema"),
  status: () => requestJson("/api/admin/status", { cache: "no-store" }, "Failed to load status"),
  config: () => requestJson("/api/admin/config", { cache: "no-store" }, "Failed to load config"),
  updateConfig: (key, value) =>
    requestJson("/api/admin/config", jsonBody({ updates: { [key]: String(value) } }), "Failed to save setting"),
  clearCache: () => requestJson("/api/admin/cache/clear", { method: "POST" }, "Failed to clear cache"),
  restart: () => requestJson("/api/admin/restart", { method: "POST" }, "Failed to restart"),
  exportEnv: () => requestBlob("/api/admin/export-env", {}, "Failed to export .env"),
  envMigrationStatus: () =>
    requestJson(ENV_MIGRATION_ENDPOINTS.status, { cache: "no-store" }, "Failed to check legacy migration status"),
  previewEnvMigration: (source, sourceName) => {
    const data = new FormData();
    const file = source instanceof Blob ? source : new Blob([String(source ?? "")], { type: "text/plain" });
    data.append("file", file, sourceName || "legacy.env");
    return requestJson(
      ENV_MIGRATION_ENDPOINTS.preview,
      { method: "POST", body: data },
      "Failed to preview the legacy .env migration",
    );
  },
  applyEnvMigration: (previewToken, revision) =>
    requestJson(
      ENV_MIGRATION_ENDPOINTS.apply,
      jsonBody({ previewToken, revision, confirmed: true }),
      "Failed to apply the legacy .env migration",
    ),
  onboardingStatus: () =>
    requestJson(ONBOARDING_ENDPOINTS.status, { cache: "no-store" }, "Failed to load setup status"),
  completeOnboarding: () =>
    requestJson(ONBOARDING_ENDPOINTS.complete, { method: "POST" }, "Failed to save setup completion"),
  playlists: (refresh = false) =>
    requestJson(`/api/admin/playlists${refresh ? "?refresh=true" : ""}`, {}, "Failed to load playlists"),
  playlistTracks: (name) =>
    requestJson(`/api/admin/playlists/${encodeURIComponent(name)}/tracks`, {}, "Failed to load playlist tracks"),
  refreshPlaylists: () => requestJson("/api/admin/playlists/refresh", { method: "POST" }, "Failed to refresh playlists"),
  refreshPlaylist: (name) =>
    requestJson(`/api/admin/playlists/${encodeURIComponent(name)}/refresh`, { method: "POST" }, "Failed to refresh playlist"),
  matchPlaylist: (name) =>
    requestJson(`/api/admin/playlists/${encodeURIComponent(name)}/match`, { method: "POST" }, "Failed to match playlist"),
  clearPlaylistCache: (name) =>
    requestJson(`/api/admin/playlists/${encodeURIComponent(name)}/clear-cache`, { method: "POST" }, "Failed to clear playlist cache"),
  addPlaylist: (name, spotifyId, localTracksPosition = "first") =>
    requestJson("/api/admin/playlists", jsonBody({ name, spotifyId, localTracksPosition }), "Failed to add playlist"),
  removePlaylist: (name) =>
    requestJson(`/api/admin/playlists/${encodeURIComponent(name)}`, { method: "DELETE" }, "Failed to remove playlist"),
  playlistLinks: (libraryScopeId = "") => {
    const suffix = libraryScopeId ? `?libraryScopeId=${encodeURIComponent(libraryScopeId)}` : "";
    return requestJson(`/api/admin/playlist-links${suffix}`, { cache: "no-store" }, "Failed to load playlist links");
  },
  createPlaylistLink: (payload) =>
    requestJson("/api/admin/playlist-links", jsonBody(payload), "Failed to create playlist link"),
  updatePlaylistLink: (id, payload) =>
    requestJson(`/api/admin/playlist-links/${encodeURIComponent(id)}`, jsonBody(payload, "PUT"), "Failed to update playlist link"),
  refreshPlaylistLink: (id) =>
    requestJson(`/api/admin/playlist-links/${encodeURIComponent(id)}/refresh`, { method: "POST" }, "Failed to refresh source playlist"),
  playlistLinkPreview: (id, snapshotId = "") => {
    const suffix = snapshotId ? `?snapshotId=${encodeURIComponent(snapshotId)}` : "";
    return requestJson(`/api/admin/playlist-links/${encodeURIComponent(id)}/preview${suffix}`, { cache: "no-store" }, "Failed to preview playlist link");
  },
  runPlaylistLink: (id, payload = {}) =>
    requestJson(`/api/admin/playlist-links/${encodeURIComponent(id)}/run`, jsonBody(payload), "Failed to run playlist link"),
  overridePlaylistMatch: (externalSnapshotId, payload) =>
    requestJson(`/api/admin/playlist-links/matches/${encodeURIComponent(externalSnapshotId)}/override`, jsonBody(payload), "Failed to save match review"),
  deletePlaylistMatchOverride: (overrideId) =>
    requestJson(`/api/admin/playlist-links/matches/overrides/${encodeURIComponent(overrideId)}`, { method: "DELETE" }, "Failed to clear match review"),
  createPlaylistSchedule: (id, payload) =>
    requestJson(`/api/admin/playlist-links/${encodeURIComponent(id)}/schedules`, jsonBody(payload), "Failed to schedule playlist link"),
  updatePlaylistSchedule: (scheduleId, payload) =>
    requestJson(`/api/admin/playlist-links/schedules/${encodeURIComponent(scheduleId)}`, jsonBody(payload, "PUT"), "Failed to update playlist schedule"),
  createPlaylistBackendCredential: (payload) =>
    requestJson("/api/admin/playlist-links/backend-credentials", jsonBody(payload), "Failed to store backend credentials"),
  rotatePlaylistBackendCredential: (referenceId, payload) =>
    requestJson(`/api/admin/playlist-links/backend-credentials/${encodeURIComponent(referenceId)}`, jsonBody(payload, "PUT"), "Failed to rotate backend credentials"),
  downloads: () => requestJson("/api/admin/downloads", {}, "Failed to load downloads"),
  deleteDownload: (path) =>
    requestJson(`/api/admin/downloads?path=${encodeURIComponent(path)}`, { method: "DELETE" }, "Failed to delete download"),
  deleteAllDownloads: () => requestJson("/api/admin/downloads/all", { method: "DELETE" }, "Failed to delete downloads"),
  endpointUsage: (top = 50) =>
    requestJson(`/api/admin/debug/endpoint-usage?top=${top}`, {}, "Failed to load endpoint usage"),
  clearEndpointUsage: () => requestJson("/api/admin/debug/endpoint-usage", { method: "DELETE" }, "Failed to clear endpoint usage"),
  queue: () => requestJson("/api/admin/downloads/queue", {}, "Failed to load queue"),
  jobs: () => requestJson("/api/admin/jobs?limit=100", {}, "Failed to load durable jobs"),
  cancelJob: (id) =>
    requestJson(`/api/admin/jobs/${encodeURIComponent(id)}/cancel`, { method: "POST" }, "Failed to cancel job"),
  providerAccounts: () =>
    requestJson("/api/admin/provider-accounts", {}, "Failed to load provider accounts"),
  favoriteActionPolicy: (scope) =>
    requestJson(`/api/admin/favorite-action-policies?${new URLSearchParams(scope)}`, { cache: "no-store" }, "Failed to load favorite policy"),
  saveFavoriteActionPolicy: (scope, administrator) =>
    requestJson(`/api/admin/favorite-action-policies/${administrator ? "global" : "me"}`, jsonBody(scope, "PUT"), "Failed to save favorite policy"),
  intelligence: (scope) =>
    requestJson(`/api/admin/intelligence?${new URLSearchParams(scope)}`, { cache: "no-store" }, "Failed to load intelligence"),
  saveIntelligencePolicy: (payload) =>
    requestJson("/api/admin/intelligence/policy", jsonBody(payload, "PUT"), "Failed to save intelligence settings"),
  runIntelligence: (payload) =>
    requestJson("/api/admin/intelligence/runs", jsonBody(payload), "Failed to start recommendation run"),
  generateIntelligencePlaylist: (payload) =>
    requestJson("/api/admin/intelligence/generated-sets", jsonBody(payload), "Failed to create generated playlist preview"),
  createIntelligenceSchedule: (payload) =>
    requestJson("/api/admin/intelligence/schedules", jsonBody(payload), "Failed to schedule recommendations"),
  updateIntelligenceSchedule: (scheduleId, payload) =>
    requestJson(`/api/admin/intelligence/schedules/${encodeURIComponent(scheduleId)}`, jsonBody(payload, "PUT"), "Failed to update recommendation schedule"),
  disableIntelligenceSchedule: (scheduleId, payload) =>
    requestJson(`/api/admin/intelligence/schedules/${encodeURIComponent(scheduleId)}`, jsonBody(payload, "DELETE"), "Failed to disable recommendation schedule"),
  purgeIntelligence: (payload) =>
    requestJson("/api/admin/intelligence/data", jsonBody(payload, "DELETE"), "Failed to clear intelligence data"),
  providerHealth: () =>
    requestJson("/api/admin/providers/status", { cache: "no-store" }, "Failed to load provider health"),
  testProviderAccountCapability: (accountId, provider, capability) =>
    requestJson(
      `/api/admin/providers/test/${encodeURIComponent(provider)}/${encodeURIComponent(capability)}?accountId=${encodeURIComponent(accountId)}`,
      { method: "POST" },
      "Failed to test provider capability",
    ),
  createProviderAccount: (payload) =>
    requestJson("/api/admin/provider-accounts", jsonBody(payload), "Failed to create provider account"),
  revokeProviderAccount: (id) =>
    requestJson(`/api/admin/provider-accounts/${encodeURIComponent(id)}`, { method: "DELETE" }, "Failed to revoke provider account"),
  createDatabaseBackup: () =>
    requestJson("/api/admin/storage/backups", { method: "POST" }, "Failed to create database backup"),
  mappings: (params = {}) => {
    const query = new URLSearchParams();
    for (const key of ["page", "pageSize", "search", "state", "libraryScopeId"]) {
      if (params[key] !== undefined && params[key] !== null && params[key] !== "") query.set(key, params[key]);
    }
    return requestJson(`/api/admin/track-matches?${query}`, {}, "Failed to load track matches");
  },
  saveMapping: (externalSnapshotId, payload) =>
    requestJson(`/api/admin/playlist-links/matches/${encodeURIComponent(externalSnapshotId)}/override`, jsonBody(payload), "Failed to save match review"),
  deleteMapping: (overrideId, expectedRevision = 0) =>
    requestJson(`/api/admin/playlist-links/matches/overrides/${encodeURIComponent(overrideId)}?expectedRevision=${encodeURIComponent(expectedRevision)}`, { method: "DELETE" }, "Failed to clear match review"),
  externalPlaylistSearch: (query, provider, limit = 20) => {
    const params = new URLSearchParams({ query, provider, limit: String(limit) });
    return requestJson(`/api/admin/external/playlists/search?${params}`, {}, "Failed to search playlists");
  },
  externalPlaylistTracks: (provider, externalId, limit = 50) =>
    requestJson(`/api/admin/external/playlists/${encodeURIComponent(provider)}/${encodeURIComponent(externalId)}/tracks?limit=${limit}`, {}, "Failed to load external playlist tracks"),
  extensionStore: () => requestJson("/api/admin/extensions/store", {}, "Failed to load extension store"),
  extensionRegistries: () => requestJson("/api/admin/extensions/registries", { cache: "no-store" }, "Failed to load extension registries"),
  createExtensionRegistry: (payload) =>
    requestJson("/api/admin/extensions/registries", jsonBody(payload), "Failed to add extension registry"),
  setExtensionRegistryEnabled: (registryId, enabled, expectedRevision) =>
    requestJson(`/api/admin/extensions/registries/${encodeURIComponent(registryId)}`, jsonBody({ enabled, expectedRevision }, "PATCH"), "Failed to update extension registry"),
  extensionPackages: () => requestJson("/api/admin/extensions/packages", { cache: "no-store" }, "Failed to load extension packages"),
  extensionPermissions: (packageId) =>
    requestJson(`/api/admin/extensions/packages/${encodeURIComponent(packageId)}/permissions`, { cache: "no-store" }, "Failed to load extension permissions"),
  reviewExtensionPermissions: (packageId, payload) =>
    requestJson(`/api/admin/extensions/packages/${encodeURIComponent(packageId)}/review`, jsonBody(payload), "Failed to review extension permissions"),
  activateExtensionPackage: (packageId, expectedRevision) =>
    requestJson(`/api/admin/extensions/packages/${encodeURIComponent(packageId)}/activate`, jsonBody({ expectedRevision }), "Failed to activate extension"),
  disableExtensionPackage: (packageId, expectedRevision) =>
    requestJson(`/api/admin/extensions/packages/${encodeURIComponent(packageId)}/disable`, jsonBody({ expectedRevision }), "Failed to disable extension"),
  rollbackExtensionPackage: (packageId, expectedRevision) =>
    requestJson(`/api/admin/extensions/packages/${encodeURIComponent(packageId)}/rollback`, jsonBody({ expectedRevision }), "Failed to roll back extension"),
  uninstallExtensionPackage: (packageId, expectedRevision) =>
    requestJson(`/api/admin/extensions/packages/${encodeURIComponent(packageId)}`, jsonBody({ expectedRevision, retainProviderAccounts: true }, "DELETE"), "Failed to uninstall extension package"),
  extensionLogs: (packageId = "", limit = 100) => {
    const query = new URLSearchParams({ limit: String(limit) });
    if (packageId) query.set("packageId", packageId);
    return requestJson(`/api/admin/extensions/logs?${query}`, { cache: "no-store" }, "Failed to load extension logs");
  },
  installExtension: (item) =>
    requestJson("/api/admin/extensions/install", jsonBody({ id: item.id || item.Id, downloadUrl: item.downloadUrl || item.DownloadUrl || "", sha256: item.sha256 || item.Sha256 || "", registryId: item.registryId || item.RegistryId || null }), "Failed to stage extension"),
  scrobblingStatus: () => requestJson("/api/admin/scrobbling/status", {}, "Failed to load scrobbling"),
  updateLocalTracksScrobbling: (enabled) =>
    requestJson("/api/admin/scrobbling/local-tracks/update", jsonBody({ enabled }), "Failed to update local scrobbling"),
  testLastFm: () => requestJson("/api/admin/scrobbling/lastfm/test", { method: "POST" }, "Failed to test Last.fm"),
  validateListenBrainz: (userToken) =>
    requestJson("/api/admin/scrobbling/listenbrainz/validate", jsonBody({ userToken }), "Failed to validate ListenBrainz"),
  testListenBrainz: () => requestJson("/api/admin/scrobbling/listenbrainz/test", { method: "POST" }, "Failed to test ListenBrainz"),
  appleMusicStatus: () => requestJson("/api/admin/apple-download/status", { cache: "no-store" }, "Failed to load Apple download status"),
  appleMusicLogin: (username, password) =>
    requestJson("/api/admin/apple-download/login", jsonBody({ username, password }), "Failed to start Apple Music login"),
  appleMusic2fa: (code) =>
    requestJson("/api/admin/apple-download/login/2fa", jsonBody({ code }), "Failed to submit Apple Music 2FA"),
};

class ThemeManager {
  static apply(theme) {
    if (theme === "system") {
      document.documentElement.removeAttribute("data-theme");
    } else {
      document.documentElement.dataset.theme = theme;
    }
    localStorage.setItem(THEME_KEY, theme);
  }

  static current() {
    return localStorage.getItem(THEME_KEY) || "system";
  }
}

class AllstarrApp extends LitElement {
  static properties = {
    authenticated: { type: Boolean },
    loading: { type: Boolean },
    route: { type: String },
    navOpen: { type: Boolean },
    session: { state: true },
    authBackend: { state: true },
    schema: { state: true },
    config: { state: true },
    status: { state: true },
    theme: { state: true },
    loginError: { state: true },
    restartKeys: { state: true },
    toasts: { state: true },
    activity: { state: true },
    playlists: { state: true },
    playlistLinks: { state: true },
    playlistLinkPreview: { state: true },
    selectedPlaylistLinkId: { state: true },
    downloads: { state: true },
    jobs: { state: true },
    providerAccounts: { state: true },
    providerHealth: { state: true },
    providerTests: { state: true },
    endpointUsage: { state: true },
    mappings: { state: true },
    externalPlaylists: { state: true },
    externalPlaylistTracks: { state: true },
    extensionStore: { state: true },
    extensionRegistries: { state: true },
    extensionPackages: { state: true },
    extensionPermissions: { state: true },
    extensionLogs: { state: true },
    selectedExtensionPackageId: { state: true },
    scrobbling: { state: true },
    appleMusicStatus: { state: true },
    serviceResults: { state: true },
    extensionActions: { state: true },
    extensionRegistryError: { state: true },
    providerConfigOpen: { state: true },
    favoritePolicy: { state: true },
    intelligence: { state: true },
    intelligenceLoading: { state: true },
    priorityDrag: { state: true },
    envMigration: { state: true },
    envMigrationStatus: { state: true },
    onboardingStatus: { state: true },
    onboardingSaving: { state: true },
    setupGuideOpen: { state: true },
    setupStep: { state: true },
    loadFailures: { state: true },
  };

  constructor() {
    super();
    this.authenticated = false;
    this.loading = true;
    this.route = normalizeRoute();
    this.navOpen = false;
    this.session = null;
    this.authBackend = "media server";
    this.schema = null;
    this.config = null;
    this.status = null;
    this.theme = ThemeManager.current();
    this.loginError = "";
    this.restartKeys = new Set();
    this.toasts = [];
    this.activity = [];
    this.playlists = null;
    this.playlistLinks = [];
    this.playlistLinkPreview = null;
    this.selectedPlaylistLinkId = "";
    this.downloads = null;
    this.jobs = [];
    this.providerAccounts = [];
    this.providerHealth = [];
    this.providerTests = new Set();
    this.endpointUsage = null;
    this.mappings = null;
    this.externalPlaylists = null;
    this.externalPlaylistTracks = new Map();
    this.extensionStore = null;
    this.extensionRegistries = [];
    this.extensionPackages = [];
    this.extensionPermissions = new Map();
    this.extensionLogs = [];
    this.selectedExtensionPackageId = "";
    this.scrobbling = null;
    this.appleMusicStatus = null;
    this.serviceResults = {};
    this.extensionActions = {};
    this.extensionRegistryError = "";
    this.providerConfigOpen = new Set();
    this.favoritePolicy = null;
    this.intelligence = null;
    this.intelligenceLoading = false;
    this.priorityDrag = null;
    this.envMigration = { state: "idle", sourceName: "", preview: null, result: null, error: "" };
    this.envMigrationStatus = null;
    this.onboardingStatus = null;
    this.onboardingSaving = false;
    this.setupGuideOpen = false;
    this.setupStep = Math.max(0, Math.min(SETUP_GUIDE_LAST_STEP, Number(localStorage.getItem(SETUP_GUIDE_STEP_KEY)) || 0));
    this.loadFailures = {};
    this.playlistLinkFilters = { libraryScopeId: "" };
    this.mappingFilters = { page: 1, pageSize: 50, state: "", libraryScopeId: "", search: "" };
    this.externalPlaylistProvider = "deezer";
    this.externalPlaylistQuery = "";
    this.activitySource = null;
    this.routeLoadKey = "";
  }

  createRenderRoot() {
    return this;
  }

  connectedCallback() {
    super.connectedCallback();
    ThemeManager.apply(this.theme);
    this.onHashChange = () => {
      const requestedRoute = normalizeRoute();
      this.route = this.routeForSession(requestedRoute);
      if (this.route !== requestedRoute) {
        window.history.replaceState(null, "", `#${this.route}`);
      }
      this.navOpen = false;
      this.loadForRoute();
    };
    window.addEventListener("hashchange", this.onHashChange);
    this.bootstrap();
  }

  disconnectedCallback() {
    window.removeEventListener("hashchange", this.onHashChange);
    this.stopActivityStream();
    super.disconnectedCallback();
  }

  updated() {
    if (!this.shouldShowSetupGuide()) return;
    const dialog = this.querySelector(".setup-guide");
    if (dialog && !dialog.contains(document.activeElement)) {
      dialog.querySelector("[autofocus]")?.focus();
    }
  }

  async bootstrap() {
    this.loading = true;
    try {
      const authState = await API.me();
      this.authBackend = authState.backend || authState.Backend || "media server";
      if (!(authState.authenticated || authState.Authenticated)) {
        this.authenticated = false;
        this.session = null;
        return;
      }

      this.session = authState.user || authState.User;
      this.authenticated = true;
      await this.loadSchema();
      if (this.isAdministrator()) {
        const [configResult, statusResult] = await Promise.allSettled([
          this.loadConfig(),
          this.loadStatus(),
          this.loadEnvMigrationStatus(),
          this.loadOnboardingStatus(),
        ]);
        if (configResult.status === "rejected") {
          this.config = {};
          this.toast(configResult.reason?.message || "Failed to load config", "error");
        }
        if (statusResult.status === "rejected") {
          this.status = {};
          this.toast(statusResult.reason?.message || "Failed to load status", "error");
        }
        this.startActivityStream();
      } else {
        this.config = {};
        this.status = {};
        const restrictedRoute = this.routeForSession(this.route);
        if (restrictedRoute !== this.route) {
          this.route = restrictedRoute;
          window.history.replaceState(null, "", `#${this.route}`);
        }
      }
      await this.loadForRoute();
    } catch (error) {
      // A confirmed session must not be discarded because a later UI bootstrap
      // request failed. Only the auth check itself can leave us unauthenticated.
      if (!this.authenticated) {
        this.session = null;
      }
      if (!String(error.message).includes("Authentication")) {
        this.toast(error.message, "error");
      }
    } finally {
      this.loading = false;
    }
  }

  async loadSchema() {
    this.schema = await API.schema();
  }

  async loadConfig() {
    try {
      this.config = await API.config();
      this.clearLoadFailure("config");
    } catch (error) {
      this.recordLoadFailure("config", "Configuration", error);
      throw error;
    }
  }

  async loadStatus() {
    try {
      this.status = await API.status();
      this.clearLoadFailure("status");
    } catch (error) {
      this.recordLoadFailure("status", "Runtime status", error);
      throw error;
    }
  }

  recordLoadFailure(key, label, error) {
    this.loadFailures = { ...this.loadFailures, [key]: { label, message: error?.message || "This information could not be loaded." } };
  }

  clearLoadFailure(key) {
    if (!this.loadFailures[key]) return;
    const next = { ...this.loadFailures };
    delete next[key];
    this.loadFailures = next;
  }

  async retryLoadFailure(key) {
    try {
      if (key === "config") await this.loadConfig();
      else if (key === "status") await this.loadStatus();
      else if (key === "playlistLinks") await this.loadPlaylistLinks();
      else if (key === "extensionRegistries") await this.loadExtensionControlPlane();
      else await this.loadForRoute(true);
      this.toast("Information loaded");
    } catch (error) {
      this.toast(error.message, "error");
    }
  }

  async loadEnvMigrationStatus() {
    try {
      this.envMigrationStatus = await API.envMigrationStatus();
    } catch (error) {
      this.envMigrationStatus = { eligible: false, completed: false, unavailable: true, message: error.message };
    }
  }

  async loadOnboardingStatus() {
    try {
      this.onboardingStatus = await API.onboardingStatus();
      const migration = this.onboardingStatus?.migration || this.onboardingStatus?.Migration;
      if (migration) this.envMigrationStatus = migration;
      const completed = Boolean(this.onboardingStatus?.completed ?? this.onboardingStatus?.Completed);
      this.setupGuideOpen = !completed && localStorage.getItem(SETUP_GUIDE_DISMISSED_KEY) !== "1";
    } catch (error) {
      this.onboardingStatus = { completed: false, unavailable: true, message: error.message };
      // A transient status failure should not make the guide impossible to use.
      this.setupGuideOpen = localStorage.getItem(SETUP_GUIDE_DISMISSED_KEY) !== "1";
    }
  }

  isAdministrator() {
    return Boolean(this.session?.isAdministrator || this.session?.IsAdministrator);
  }

  routeForSession(route) {
    return this.authenticated && !this.isAdministrator() && route !== "/intelligence" ? "/sources" : route;
  }

  async loadForRoute(force = false) {
    if (!this.authenticated) {
      return;
    }

    if (!this.isAdministrator() && this.route !== "/sources" && this.route !== "/intelligence") {
      this.route = "/sources";
      window.history.replaceState(null, "", "#/sources");
    }

    const routeKey = `${this.route}`;
    if (!force && routeKey === this.routeLoadKey) {
      return;
    }
    this.routeLoadKey = routeKey;
    this.clearLoadFailure(`route:${routeKey}`);

    const [zone, sub] = routeParts(this.route);
    try {
      if (zone === "library") {
        if (!sub || sub === "link") {
          await this.loadPlaylistLinks();
        } else if (!sub || sub === "link" || sub === "injected") {
          await this.loadPlaylists();
        } else if (sub === "mappings") {
          await this.loadMappings();
        } else if (sub === "missing" || sub === "migration") {
          await this.loadMigrationData();
        } else if (sub === "kept") {
          await this.loadDownloads();
        }
      } else if (zone === "sources") {
        if (this.isAdministrator()) {
          await Promise.all([
            this.loadProviderAccounts(),
            this.loadAppleMusicStatus().catch((error) => {
              this.appleMusicStatus = { error: error.message, logged_in: false };
            }),
          ]);
        } else {
          await this.loadProviderAccounts();
        }
      } else if (zone === "activity") {
        await Promise.all([this.loadEndpointUsage(), this.loadScrobbling(), this.loadQueue(), this.loadJobs()]);
      }
    } catch (error) {
      if (error?.status === 401) {
        const sessionState = await this.confirmDashboardSession();
        if (sessionState === false) {
          this.handleExpiredSession();
          return;
        }
      }
      this.recordLoadFailure(`route:${routeKey}`, `${titleCase(routeParts(routeKey)[0] || "page")} data`, error);
    }
  }

  async confirmDashboardSession() {
    try {
      const authState = await API.me();
      if (!(authState.authenticated || authState.Authenticated)) return false;
      this.session = authState.user || authState.User || this.session;
      return true;
    } catch {
      // A failed confirmation request is not proof that the cookie expired.
      return null;
    }
  }

  handleExpiredSession() {
    this.authenticated = false;
    this.session = null;
    this.routeLoadKey = "";
    this.stopActivityStream();
    this.loginError = "Your dashboard session expired. Sign in again to continue.";
  }

  startActivityStream() {
    this.stopActivityStream();
    this.activitySource = new EventSource("/api/admin/downloads/activity");
    this.activitySource.onmessage = (event) => {
      try {
        this.activity = JSON.parse(event.data);
      } catch {
        this.activity = [];
      }
    };
    this.activitySource.onerror = () => {
      this.stopActivityStream();
    };
  }

  stopActivityStream() {
    if (this.activitySource) {
      this.activitySource.close();
      this.activitySource = null;
    }
  }

  async login(event) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    this.loginError = "";
    try {
      await API.login(data.get("username"), data.get("password"), data.get("remember") === "on");
      await this.bootstrap();
    } catch (error) {
      this.loginError = error.message;
    }
  }

  async logout() {
    try {
      await API.logout();
    } finally {
      this.authenticated = false;
      this.session = null;
      this.stopActivityStream();
    }
  }

  setTheme(theme) {
    this.theme = theme;
    ThemeManager.apply(theme);
  }

  toast(message, type = "success") {
    const id = crypto.randomUUID?.() || String(Date.now());
    this.toasts = [...this.toasts, { id, message, type }];
    window.setTimeout(() => {
      this.toasts = this.toasts.filter((toast) => toast.id !== id);
    }, 4200);
  }

  navigate(path) {
    window.location.hash = path;
  }

  async saveField(field, value) {
    if (field.readOnly || field.ownership === "deployment") {
      return;
    }
    if (field.sensitive && !value) {
      return;
    }

    const currentValue = getPathValue(this.config, field.valuePath, "");
    if (!field.sensitive && normalizedFieldValue(field, currentValue) === normalizedFieldValue(field, value)) {
      return;
    }

    await API.updateConfig(field.key, value);
    if (field.valuePath) {
      const nextConfig = structuredClone(this.config || {});
      setPathValue(nextConfig, field.valuePath, field.type === "toggle" ? value === "true" : value);
      this.config = nextConfig;
    }
    if (field.requiresRestart) {
      this.restartKeys = new Set([...this.restartKeys, field.key]);
    }
    this.toast(`${field.label} saved`);
  }

  async savePriority(group, providers) {
    await API.updateConfig(group.envKey, providers.join(","));
    this.schema = {
      ...this.schema,
      priorityGroups: this.schema.priorityGroups.map((item) =>
        item.id === group.id ? { ...item, providers } : item,
      ),
    };
    this.restartKeys = new Set([...this.restartKeys, group.envKey]);
    this.toast(`${group.label} saved`);
  }

  async loadPlaylists(refresh = false) {
    this.playlists = await API.playlists(refresh);
  }

  async loadPlaylistLinks() {
    try {
      const response = await API.playlistLinks(this.playlistLinkFilters.libraryScopeId);
      this.playlistLinks = asArray(response?.playlistLinks || response?.PlaylistLinks || response?.links || response?.Links || response);
      this.clearLoadFailure("playlistLinks");
      if (this.selectedPlaylistLinkId && !this.playlistLinks.some((link) =>
        String(link.id || link.Id) === String(this.selectedPlaylistLinkId))) {
        this.selectedPlaylistLinkId = "";
        this.playlistLinkPreview = null;
      }
      if (!this.providerAccounts.length) {
        await this.loadProviderAccounts();
      }
    } catch (error) {
      this.recordLoadFailure("playlistLinks", "Playlist links", error);
      throw error;
    }
  }

  async loadPlaylistLinkPreview(linkId, refresh = false) {
    if (refresh) {
      await API.refreshPlaylistLink(linkId);
    }
    this.selectedPlaylistLinkId = String(linkId);
    this.playlistLinkPreview = await API.playlistLinkPreview(linkId);
  }

  async loadDownloads() {
    this.downloads = await API.downloads();
  }

  async loadQueue() {
    this.activity = await API.queue();
  }

  async loadJobs() {
    const response = await API.jobs();
    this.jobs = asArray(response?.jobs || response?.Jobs);
  }

  async loadProviderAccounts() {
    const managementMode = String(this.schema?.providerAccountManagementMode || "Hybrid");
    const administrator = Boolean(this.session?.isAdministrator || this.session?.IsAdministrator);
    if (managementMode === "AdminManaged" && !administrator) {
      this.providerAccounts = [];
      this.providerHealth = [];
      return;
    }

    const [response, health] = await Promise.all([
      API.providerAccounts(),
      administrator ? API.providerHealth() : Promise.resolve([]),
    ]);
    this.providerAccounts = asArray(response?.accounts || response?.Accounts);
    this.providerHealth = asArray(health);
  }

  saveFavoritePolicy = async (event) => {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    const payload = {
      protocol: data.get("protocol"), backendInstanceId: data.get("backendInstanceId"),
      libraryScopeId: data.get("libraryScopeId") || null,
      addToVirtualLiked: data.has("addToVirtualLiked"), matchLocalLibrary: data.has("matchLocalLibrary"),
      autoDownload: data.has("autoDownload"), enrichMetadata: data.has("enrichMetadata"),
      placeManagedFile: data.has("placeManagedFile"), refreshBackendLibrary: data.has("refreshBackendLibrary"),
    };
    const global = this.isAdministrator() && data.get("policyOwner") === "global";
    await API.saveFavoriteActionPolicy(payload, global);
    this.favoritePolicy = await API.favoriteActionPolicy({ protocol: payload.protocol,
      backendInstanceId: payload.backendInstanceId, libraryScopeId: payload.libraryScopeId || "" });
    this.toast(global ? "Global favorite policy saved" : "Your favorite policy saved");
  };

  loadIntelligence = async (event) => {
    event?.preventDefault();
    const data = new FormData(event.currentTarget);
    const scope = { protocol: data.get("protocol"), backendInstanceId: data.get("backendInstanceId"),
      libraryScopeId: data.get("libraryScopeId") };
    this.intelligenceLoading = true;
    this.intelligence = { state: "loading", scope };
    try { this.intelligence = await API.intelligence(scope); }
    catch (error) { this.intelligence = { state: "error", message: error.message, scope }; }
    finally { this.intelligenceLoading = false; }
  };

  saveIntelligencePolicy = async (event) => {
    event.preventDefault(); const data = new FormData(event.currentTarget);
    const payload = { ...(this.intelligence?.scope || {}), enabled: data.has("enabled"),
      retentionDays: Number(data.get("retentionDays")), allowedSignalTypes: data.getAll("signalTypes"),
      enabledProviders: data.getAll("providers"), expectedRevision: Number(data.get("expectedRevision") || 0) };
    payload.targetCredentialReferenceId = this.intelligence?.policy?.targetCredentialReferenceId || null;
    if (payload.protocol === "subsonic" && payload.enabled) {
      const username = String(data.get("intelligenceTargetUsername") || "").trim();
      const password = String(data.get("intelligenceTargetPassword") || "");
      if (password) {
        if (!username) throw new Error("Navidrome / Subsonic username is required when changing the password");
        const body = { targetProtocol: "subsonic", username, password };
        if (payload.targetCredentialReferenceId) {
          await API.rotatePlaylistBackendCredential(payload.targetCredentialReferenceId, body);
        } else {
          const credential = await API.createPlaylistBackendCredential(body);
          payload.targetCredentialReferenceId = credential.referenceId || credential.ReferenceId;
        }
      }
      if (!payload.targetCredentialReferenceId) throw new Error("Navidrome / Subsonic credentials are required for generated playlists");
    } else if (payload.protocol === "jellyfin") {
      payload.targetCredentialReferenceId = null;
    }
    await API.saveIntelligencePolicy(payload);
    this.intelligence = await API.intelligence(payload); this.toast("Intelligence settings saved");
  };

  runIntelligence = async () => {
    const payload = { ...(this.intelligence?.scope || {}), limit: 25,
      idempotencyKey: crypto.randomUUID?.() || `recommendation-${Date.now()}`, seedTrackKeys: [] };
    await API.runIntelligence(payload); this.intelligence = await API.intelligence(payload);
    this.toast("Recommendation run queued");
  };

  generateIntelligencePlaylist = async (event) => {
    event.preventDefault(); const data = new FormData(event.currentTarget);
    const payload = { ...(this.intelligence?.scope || {}), runId: this.intelligence?.actions?.latestRunId,
      name: data.get("name") };
    await API.generateIntelligencePlaylist(payload); this.intelligence = await API.intelligence(payload);
    this.toast("Generated playlist preview created");
  };

  createIntelligenceSchedule = async (event) => {
    event.preventDefault(); const data = new FormData(event.currentTarget);
    const payload = { ...(this.intelligence?.scope || {}), name: data.get("name"),
      limit: Number(data.get("limit")), cronExpression: data.get("cronExpression"),
      timeZoneId: data.get("timeZoneId"), overlapPolicy: data.get("overlapPolicy"),
      misfirePolicy: data.get("misfirePolicy"), enabled: true };
    await API.createIntelligenceSchedule(payload);
    this.intelligence = await API.intelligence(payload); this.toast("Recommendation schedule created");
  };

  toggleIntelligenceSchedule = async (schedule) => {
    const payload = { ...(this.intelligence?.scope || {}), name: schedule.name, limit: schedule.limit,
      cronExpression: schedule.cronExpression, timeZoneId: schedule.timeZoneId,
      overlapPolicy: schedule.overlapPolicy, misfirePolicy: schedule.misfirePolicy,
      enabled: !schedule.enabled, expectedRevision: schedule.revision };
    await API.updateIntelligenceSchedule(schedule.id, payload);
    this.intelligence = await API.intelligence(payload);
    this.toast(schedule.enabled ? "Recommendation schedule paused" : "Recommendation schedule resumed");
  };

  disableIntelligenceSchedule = async (schedule) => {
    if (!window.confirm(`Disable the recommendation schedule “${schedule.name}”?`)) return;
    const payload = { ...(this.intelligence?.scope || {}), expectedRevision: schedule.revision };
    await API.disableIntelligenceSchedule(schedule.id, payload);
    this.intelligence = await API.intelligence(payload); this.toast("Recommendation schedule disabled");
  };

  purgeIntelligence = async () => {
    if (!window.confirm("Turn off intelligence and remove the retained signals, profiles, recommendations, and generated sets for this scope?")) return;
    await API.purgeIntelligence(this.intelligence?.scope || {});
    this.intelligence = { state: "disabled", scope: this.intelligence?.scope,
      message: "Intelligence is off and retained data for this scope was removed." };
    this.toast("Intelligence data removed");
  };

  async loadEndpointUsage() {
    this.endpointUsage = await API.endpointUsage(50);
  }

  async loadMappings() {
    this.mappings = await API.mappings(this.mappingFilters);
  }

  async loadMigrationData() {
    await this.loadPlaylists();
  }

  async loadExtensionControlPlane() {
    const [registries, packages, logs] = await Promise.allSettled([
      API.extensionRegistries(),
      API.extensionPackages(),
      API.extensionLogs(),
    ]);
    if (registries.status === "fulfilled") {
      this.extensionRegistries = asArray(registries.value?.items || registries.value?.Items || registries.value);
      this.clearLoadFailure("extensionRegistries");
    } else {
      this.recordLoadFailure("extensionRegistries", "Extension registries", registries.reason);
    }
    if (packages.status === "fulfilled") {
      this.extensionPackages = asArray(packages.value?.items || packages.value?.Items || packages.value);
    } else {
      this.recordLoadFailure("extensionPackages", "Extension packages", packages.reason);
    }
    if (logs.status === "fulfilled") {
      this.extensionLogs = asArray(logs.value?.items || logs.value?.Items || logs.value);
    } else {
      this.recordLoadFailure("extensionLogs", "Extension logs", logs.reason);
    }
  }

  async loadExtensionStore() {
    this.extensionStore = await API.extensionStore();
  }

  async loadScrobbling() {
    this.scrobbling = await API.scrobblingStatus();
  }

  async loadAppleMusicStatus() {
    this.appleMusicStatus = await API.appleMusicStatus();
  }

  async installExtension(item) {
    const key = item.id || item.Id || item.displayName || item.DisplayName;
    this.extensionActions = { ...this.extensionActions, [key]: "Installing" };
    try {
      await API.installExtension(item);
      await Promise.all([this.loadExtensionControlPlane(), this.loadExtensionStore()]);
      this.toast("Extension staged for review");
    } finally {
      const nextActions = { ...this.extensionActions };
      delete nextActions[key];
      this.extensionActions = nextActions;
    }
  }

  async stageExtensionPackage(event) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const item = {
      downloadUrl: form.get("downloadUrl"),
      sha256: form.get("sha256"),
      registryId: form.get("registryId") || null,
      id: form.get("downloadUrl"),
    };
    await this.installExtension(item);
    event.currentTarget.reset();
  }

  async createExtensionRegistry(event) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    this.extensionRegistryError = "";
    this.extensionActions = { ...this.extensionActions, registry: "Adding" };
    try {
      await API.createExtensionRegistry({ name: form.get("name"), registryUrl: form.get("registryUrl"), enabled: true });
      event.currentTarget.reset();
      await this.loadExtensionControlPlane();
      this.toast("Extension registry validated and added");
    } catch (error) {
      this.extensionRegistryError = error.message;
      this.toast(error.message, "error");
    } finally {
      const nextActions = { ...this.extensionActions };
      delete nextActions.registry;
      this.extensionActions = nextActions;
    }
  }

  async setExtensionRegistryEnabled(item, enabled) {
    const id = item.id || item.Id;
    const key = `registry:${id}`;
    this.extensionActions = { ...this.extensionActions, [key]: enabled ? "Enabling" : "Disabling" };
    try {
      await API.setExtensionRegistryEnabled(id, enabled, item.revision ?? item.Revision ?? 0);
      await this.loadExtensionControlPlane();
      this.toast(`Extension registry ${enabled ? "enabled" : "disabled"}`);
    } finally {
      const nextActions = { ...this.extensionActions };
      delete nextActions[key];
      this.extensionActions = nextActions;
    }
  }

  async loadExtensionPermissions(item) {
    const id = item.id || item.Id;
    this.selectedExtensionPackageId = id;
    const response = await API.extensionPermissions(id);
    const next = new Map(this.extensionPermissions);
    next.set(id, asArray(response?.items || response?.Items || response));
    this.extensionPermissions = next;
  }

  async reviewExtensionPermissions(item) {
    const id = item.id || item.Id;
    const reviews = this.extensionPermissions.get(id) || [];
    const decisions = reviews.map((review) => ({
      kind: review.permissionKind || review.PermissionKind,
      value: review.permissionValue || review.PermissionValue,
      approved: (review.uiDecision || review.UiDecision || review.decision || review.Decision || "pending").toString().toLowerCase() === "approved",
    }));
    await this.runExtensionAction(item, "Reviewing", () => API.reviewExtensionPermissions(id, {
      expectedRevision: item.revision ?? item.Revision ?? 0,
      decisions,
    }), "Permissions reviewed");
  }

  setExtensionPermissionDecision(packageId, permissionId, approved) {
    const next = new Map(this.extensionPermissions);
    next.set(packageId, asArray(next.get(packageId)).map((review) =>
      String(review.id || review.Id) === String(permissionId)
        ? { ...review, uiDecision: approved ? "approved" : "denied" }
        : review));
    this.extensionPermissions = next;
  }

  async runExtensionAction(item, label, action, message) {
    const id = item.id || item.Id;
    this.extensionActions = { ...this.extensionActions, [id]: label };
    try {
      await action();
      await Promise.all([this.loadExtensionControlPlane(), this.loadSchema()]);
      this.toast(message);
    } finally {
      const nextActions = { ...this.extensionActions };
      delete nextActions[id];
      this.extensionActions = nextActions;
    }
  }

  async runServiceAction(key, action) {
    this.serviceResults = { ...this.serviceResults, [key]: { state: "running", message: "Testing..." } };
    try {
      const result = await action();
      this.serviceResults = {
        ...this.serviceResults,
        [key]: { state: "success", message: result.message || result.Message || "Connection test completed." },
      };
    } catch (error) {
      this.serviceResults = {
        ...this.serviceResults,
        [key]: { state: "error", message: error.message },
      };
    }
  }

  providerGroup(category) {
    return asArray(this.schema?.priorityGroups).find((group) => group.id === category);
  }

  capabilityConfig(category) {
    if (category === "metadata") {
      return { envKey: "MULTI_PROVIDER_ENABLED_SEARCH", valuePath: "providers.enabledSearch" };
    }
    if (category === "playlist") {
      return { envKey: "MULTI_PROVIDER_ENABLED_PLAYLIST", valuePath: "providers.enabledPlaylist" };
    }
    const group = this.providerGroup(category);
    return group ? { envKey: group.envKey, valuePath: null, group } : null;
  }

  capabilityProviders(category) {
    const config = this.capabilityConfig(category);
    if (!config) {
      return [];
    }
    if (config.valuePath) {
      const configured = splitCsv(getPathValue(this.config, config.valuePath, ""));
      return configured.length ? configured : asArray(this.providerGroup(category)?.providers);
    }
    return asArray(config.group?.providers);
  }

  providerCapabilityEnabled(provider, category) {
    const providerId = String(provider.id || provider.Id || "").toLowerCase();
    return this.capabilityProviders(category).includes(providerId);
  }

  async toggleProviderCapability(provider, category, enabled) {
    const providerId = String(provider.id || provider.Id || "").toLowerCase();
    const config = this.capabilityConfig(category);
    if (!providerId || !config) {
      return;
    }

    const providers = this.capabilityProviders(category);
    const nextProviders = enabled
      ? joinCsv([...providers, providerId])
      : joinCsv(providers.filter((item) => item !== providerId));

    await API.updateConfig(config.envKey, nextProviders);
    if (config.valuePath) {
      const nextConfig = structuredClone(this.config || {});
      setPathValue(nextConfig, config.valuePath, nextProviders);
      this.config = nextConfig;
    }

    if (config.group) {
      this.schema = {
        ...this.schema,
        priorityGroups: this.schema.priorityGroups.map((group) =>
          group.id === config.group.id ? { ...group, providers: splitCsv(nextProviders) } : group,
        ),
      };
    }

    this.restartKeys = new Set([...this.restartKeys, config.envKey]);
    this.toast(`${provider.name || provider.Name} ${category} ${enabled ? "enabled" : "disabled"}`);
  }

  async setProviderDisabled(provider, disabled) {
    const providerId = String(provider.id || provider.Id || "").toLowerCase();
    if (!providerId) {
      return;
    }

    const current = splitCsv(this.config?.providers?.disabledProviders || "");
    const next = disabled
      ? joinCsv([...current, providerId])
      : joinCsv(current.filter((item) => item !== providerId));

    await API.updateConfig("MULTI_PROVIDER_DISABLED_PROVIDERS", next);
    const nextConfig = structuredClone(this.config || {});
    setPathValue(nextConfig, "providers.disabledProviders", next);
    this.config = nextConfig;
    await this.loadSchema();
    this.restartKeys = new Set([...this.restartKeys, "MULTI_PROVIDER_DISABLED_PROVIDERS"]);
    this.toast(`${provider.name || provider.Name} ${disabled ? "disabled" : "enabled"}`);
  }

  async submitAppleLogin(event) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    this.serviceResults = { ...this.serviceResults, applemusic: { state: "running", message: "Starting Apple Music login..." } };
    try {
      const result = await API.appleMusicLogin(data.get("username"), data.get("password"));
      const account = result.auth || result.account || result;
      const immediateState = appleLoginState(result);
      this.appleMusicStatus = {
        ...(this.appleMusicStatus || {}),
        account,
        login_state: immediateState,
        logged_in: immediateState === "authenticated",
      };
      form.reset();
      await this.loadAppleMusicStatus();
      const feedback = appleAuthFeedback(this.appleMusicStatus, "login");
      this.serviceResults = {
        ...this.serviceResults,
        applemusic: feedback,
      };
    } catch (error) {
      this.serviceResults = { ...this.serviceResults, applemusic: { state: "error", message: error.message } };
    }
  }

  async submitApple2fa(event) {
    event.preventDefault();
    const form = event.currentTarget;
    const code = new FormData(form).get("code");
    this.serviceResults = { ...this.serviceResults, applemusic: { state: "running", message: "Submitting Apple Music 2FA..." } };
    try {
      const result = await API.appleMusic2fa(code);
      const account = result.auth || result.account || result;
      const immediateState = appleLoginState(result);
      this.appleMusicStatus = {
        ...(this.appleMusicStatus || {}),
        account,
        login_state: immediateState,
        logged_in: immediateState === "authenticated",
      };
      form.reset();
      await this.loadAppleMusicStatus();
      const feedback = appleAuthFeedback(this.appleMusicStatus, "2fa");
      this.serviceResults = { ...this.serviceResults, applemusic: feedback };
    } catch (error) {
      this.serviceResults = { ...this.serviceResults, applemusic: { state: "error", message: error.message } };
    }
  }

  render() {
    if (this.loading) {
      return html`<div class="app-loading"><div class="chip">Loading Allstarr</div></div>`;
    }

    if (!this.authenticated) {
      return this.renderAuth();
    }

    const administrator = this.isAdministrator();
    return html`
      <div class="app-shell">
        ${this.renderSidebar()}
        <div class="main-shell">
          ${this.renderTopbar()}
          <main class="content">
            ${this.renderLoadFailures()}
            ${this.renderRoute()}
          </main>
        </div>
      </div>
      ${administrator ? this.renderRestartBar() : nothing}
      ${administrator ? this.renderNowPlaying() : nothing}
      ${administrator ? this.renderSetupGuide() : nothing}
      ${this.renderToasts()}
    `;
  }

  renderAuth() {
    return html`
      <section class="auth-screen">
        <div class="auth-card">
          <h1>Allstarr</h1>
          <p>Sign in with your ${display(this.authBackend, "media server")} account to manage this server.</p>
          <form class="form-stack" @submit=${this.login}>
            <div class="form-row">
              <label for="username">Username</label>
              <input id="username" name="username" autocomplete="username" required>
            </div>
            <div class="form-row">
              <label for="password">Password</label>
              <input id="password" name="password" type="password" autocomplete="current-password" required>
            </div>
            <label class="inline-check">
              <input name="remember" type="checkbox">
              <span>Keep me signed in</span>
            </label>
            <button class="primary" type="submit">Sign in</button>
            <div class="auth-error" role="alert">${this.loginError}</div>
          </form>
        </div>
      </section>
    `;
  }

  renderLoadFailures() {
    const failures = Object.entries(this.loadFailures);
    if (!failures.length) return nothing;
    return html`<div class="load-failure-stack" aria-live="polite">
      ${failures.map(([key, failure]) => html`<div class="load-failure" role="alert">
        <div><strong>${display(failure.label)} could not load</strong><p>${display(failure.message)}</p></div>
        <button @click=${() => this.retryLoadFailure(key)}>Retry</button>
      </div>`)}
    </div>`;
  }

  renderSidebar() {
    const routes = asArray(this.schema?.routes);
    const administrator = this.isAdministrator();
    const routeById = new Map(routes.map((route) => [String(route.id || route.zone || "").toLowerCase(), route]));
    const primaryRoutes = ["home", "library", "sources", "intelligence"]
      .map((id) => routeById.get(id))
      .filter(Boolean);
    const systemRoutes = ["activity", "settings", "architecture"]
      .map((id) => routeById.get(id))
      .filter(Boolean);
    const groupedRoutes = new Set([...primaryRoutes, ...systemRoutes]);
    const otherRoutes = routes.filter((route) => !groupedRoutes.has(route));
    const systemActive = systemRoutes.some((route) => this.isRouteActive(route.path));
    const renderNavLink = (route) => html`
      <a class="nav-link ${this.isRouteActive(route.path) ? "active" : ""}" href=${route.path}>
        <span>${route.label}</span>
      </a>`;
    return html`
      <aside id="primary-sidebar" class="sidebar ${this.navOpen ? "open" : ""}">
        <div class="brand">
          <button
            type="button"
            class="mobile-menu ghost"
            aria-label="Close menu"
            @click=${() => { this.navOpen = false; }}
            @keydown=${(event) => {
              if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                this.navOpen = false;
              }
            }}
          >Close</button>
          <a class="brand-title" href=${administrator ? "#/home" : "#/sources"}>Allstarr</a>
          <div class="brand-subtitle">${display(this.status?.version || this.status?.Version, "Media manager")}</div>
          <span class="status-chip configured">${display(this.schema?.activeBackend || this.config?.backendType)}</span>
        </div>
        <nav class="nav-list" aria-label="Primary">
          <div class="nav-section">
            <span class="nav-heading">Music</span>
            ${primaryRoutes.map(renderNavLink)}
            ${otherRoutes.map(renderNavLink)}
          </div>
          ${systemRoutes.length ? html`
            <details class="nav-group" ?open=${systemActive}>
              <summary>System</summary>
              <div class="nav-section">${systemRoutes.map(renderNavLink)}</div>
            </details>` : nothing}
        </nav>
        <div class="sidebar-footer">
          <div>Signed in as <strong>${display(this.session?.name || this.session?.Name)}</strong></div>
          <select aria-label="Theme" .value=${this.theme} @change=${(event) => this.setTheme(event.target.value)}>
            <option value="system">System</option>
            <option value="dark">Dark</option>
            <option value="light">Light</option>
          </select>
          ${administrator ? html`<button class="ghost" @click=${async () => { await Promise.all([this.loadStatus(), this.loadConfig(), this.loadEnvMigrationStatus()]); this.toast("Status refreshed"); }}>Refresh</button>` : nothing}
          <button class="ghost" @click=${this.logout}>Logout</button>
        </div>
      </aside>
    `;
  }

  isRouteActive(path) {
    const routeZone = routeParts(this.route)[0] || "home";
    const pathZone = routeParts(path.replace("#", ""))[0] || "home";
    return routeZone === pathZone;
  }

  renderTopbar() {
    const [zone, sub] = routeParts(this.route);
    const administrator = this.isAdministrator();
    return html`
      <header class="topbar">
        <div>
          <button
            type="button"
            class="mobile-menu ghost"
            aria-controls="primary-sidebar"
            aria-expanded=${this.navOpen ? "true" : "false"}
            @click=${() => { this.navOpen = true; }}
            @keydown=${(event) => {
              if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                this.navOpen = true;
              }
            }}
          >Menu</button>
          <h1>${titleCase(zone || "home")}${sub ? html` <span class="muted">/ ${titleCase(sub)}</span>` : nothing}</h1>
          <div class="topbar-meta">${display(this.schema?.activeBackend || this.config?.backendType, "Backend unknown")}</div>
        </div>
        <div class="actions">
          <select aria-label="Theme" .value=${this.theme} @change=${(event) => this.setTheme(event.target.value)}>
            <option value="system">System</option>
            <option value="dark">Dark</option>
            <option value="light">Light</option>
          </select>
          ${administrator ? html`<button class="ghost" @click=${async () => { await Promise.all([this.loadStatus(), this.loadConfig(), this.loadEnvMigrationStatus()]); this.toast("Status refreshed"); }}>Refresh</button>` : nothing}
        </div>
      </header>
    `;
  }

  renderRoute() {
    if (!this.isAdministrator()) {
      return routeParts(this.route)[0] === "intelligence" ? this.renderIntelligence() : this.renderSources();
    }

    const [zone] = routeParts(this.route);
    if (zone === "library") {
      return this.renderLibrary();
    }
    if (zone === "sources") {
      return this.renderSources();
    }
    if (zone === "activity") {
      return this.renderActivity();
    }
    if (zone === "settings") {
      return this.renderSettings();
    }
    if (zone === "intelligence") return this.renderIntelligence();
    if (zone === "architecture") return this.renderArchitecture();
    return this.renderHome();
  }

  renderArchitecture() {
    const backend = display(this.schema?.activeBackend, "Media server");
    const lanes = ["metadata", "streaming", "download", "playlist", "lyrics"];
    const providers = asArray(this.schema?.providers);
    const laneState = (lane) => providers.filter((provider) =>
      asArray(provider.categories).map((category) => String(category).toLowerCase()).includes(lane) ||
      asArray(provider.runtimeCapabilities).some((capability) =>
        String(capability.id).toLowerCase() === lane && capability.supported !== false));
    return html`
      <section class="view-stack architecture-view">
        <div class="view-header">
          <div><h2>Architecture</h2><p>Follow a request from your music app to the local library or an enabled provider.</p></div>
          <button @click=${() => this.navigate("/sources")}>Manage sources</button>
        </div>
        <div class="architecture-flow" aria-label="Allstarr request and media flow">
          <button class="architecture-node" @click=${() => this.navigate("/home")}>
            <span class="architecture-kicker">Client plane</span><strong>Jellyfin or Subsonic client</strong><small>Uses the one protocol selected for this deployment.</small>
          </button>
          <div class="architecture-arrow" aria-hidden="true">→</div>
          <button class="architecture-node active" @click=${() => this.navigate("/activity")}>
            <span class="architecture-kicker">Allstarr core</span><strong>${backend} adapter and provider router</strong><small>Authenticates, scopes, matches, routes, and records durable work.</small>
          </button>
          <div class="architecture-arrow" aria-hidden="true">→</div>
          <button class="architecture-node" @click=${() => this.navigate("/library")}>
            <span class="architecture-kicker">Library plane</span><strong>${backend} and accessible media folders</strong><small>Audio stays in mounted folders. Postgres stores control-plane records, not song bytes.</small>
          </button>
        </div>
        <div class="architecture-lanes">
          ${lanes.map((lane) => {
            const matches = laneState(lane);
            const ready = matches.filter((provider) => ["healthy", "configured"].includes(String(provider.status).toLowerCase())).length;
            return html`<button class="architecture-lane" @click=${() => this.navigate("/sources")}>
              <span><strong>${titleCase(lane)}</strong><small>${matches.length ? `${matches.length} available source${matches.length === 1 ? "" : "s"}` : "No source advertised"}</small></span>
              <span class="status-chip ${ready ? "healthy" : matches.length ? "needs_config" : "disabled"}">${ready ? `${ready} ready` : matches.length ? "Review" : "Unavailable"}</span>
            </button>`;
          })}
        </div>
        <div class="grid architecture-details">
          <div class="panel"><h3>Control plane</h3><p>Postgres owns settings, encrypted secret references, identities, matches, playlists, jobs, audit history, and provider health. Valkey is an optional accelerator, never the only copy of durable work.</p></div>
          <div class="panel"><h3>Media plane</h3><p>Streams move between clients, the selected backend, providers, and accessible media roots. Managed downloads are normal files, and Allstarr never stores encoded audio in Postgres.</p></div>
          <div class="panel"><h3>Failure boundaries</h3><p>An unavailable optional provider loses only its own capabilities. The backend, database, and other providers continue independently, with retryable failures visible in Activity.</p></div>
        </div>
      </section>
    `;
  }

  renderHome() {
    const spotify = this.status?.spotify || this.status?.Spotify || {};
    const spotifyImport = this.status?.spotifyImport || this.status?.SpotifyImport || {};
    const providerCards = asArray(this.schema?.providers).filter((provider) =>
      ["squidwtf", "apple-download", "deezer", "qobuz"].includes(provider.id),
    );
    const downloadCanAttempt = asArray(this.schema?.providers).some((provider) =>
      asArray(provider.runtimeCapabilities).some((capability) =>
        capability.id === "download" && capability.canAttempt),
    );

    return html`
      <section class="view-stack">
        <div class="view-header">
          <div>
            <h2>Home</h2>
            <p>Runtime state, provider readiness, and current activity.</p>
          </div>
          <div class="actions">
            <button @click=${async () => { await API.refreshPlaylists(); this.toast("Playlist refresh requested"); }}>Refresh playlists</button>
            <button @click=${async () => { await API.clearCache(); this.toast("Cache clear requested"); }}>Clear cache</button>
            <button class="primary" @click=${async () => { await API.restart(); this.toast("Restart requested"); }}>Restart</button>
          </div>
        </div>

        <div class="grid">
          <div class="card metric">
            <span class="metric-label">Backend</span>
            <span class="metric-value">${display(this.status?.backendType || this.config?.backendType)}</span>
          </div>
          <div class="card metric">
            <span class="metric-label">Spotify</span>
            <span class="metric-value">${titleCase(spotify.authStatus || "unknown")}</span>
          </div>
          <div class="card metric">
            <span class="metric-label">Injected playlists</span>
            <span class="metric-value">${display(spotifyImport.playlistCount ?? this.config?.spotifyImport?.playlists?.length ?? 0)}</span>
          </div>
          <div class="card metric">
            <span class="metric-label">Active tasks</span>
            <span class="metric-value">${this.activity.length}</span>
          </div>
        </div>

        <div class="setup-launcher">
          <div>
            <h3>Need a hand getting everything connected?</h3>
            <p>Open the setup guide again at any time. It will walk through your media server, sources, and the optional Allstarr 2.x import.</p>
          </div>
          <button @click=${() => this.openSetupGuide()}>Open setup guide</button>
        </div>

        <div class="wide-grid">
          <div class="panel">
            <h3>Setup</h3>
            <div class="stat-list">
              ${this.renderSetupStep("Backend URL configured", Boolean(this.config?.jellyfin?.url || this.config?.subsonic?.url))}
              ${this.renderSetupStep("Spotify cookie present", Boolean(spotify.hasCookie || spotify.HasCookie))}
              ${this.renderSetupStep("Download capability configured", downloadCanAttempt)}
              ${this.renderSetupStep("Playlist sync enabled", Boolean(this.config?.spotifyImport?.enabled))}
            </div>
          </div>
          <div class="panel">
            <h3>Provider health</h3>
            <div class="stat-list">
              ${providerCards.map((provider) => html`
                <div class="stat-row">
                  <span>${provider.name}</span>
                  <span class="status-chip ${provider.status}">${titleCase(provider.status)}</span>
                </div>
              `)}
            </div>
          </div>
        </div>

        <div class="panel">
          <h3>Activity feed</h3>
          ${this.renderActivityList(this.activity.slice(0, 8))}
        </div>
      </section>
    `;
  }

  renderIntelligence() {
    const data = this.intelligence;
    const state = String(data?.state || "empty").toLowerCase();
    const policy = data?.policy || {};
    const actions = data?.actions || {};
    const candidates = asArray(data?.candidates);
    const generated = asArray(data?.generatedSets);
    const schedules = asArray(data?.schedules);
    const visualization = asArray(data?.visualization);
    const stateMessage = {
      empty: "Choose your backend and library to see intelligence data.",
      loading: "Loading your intelligence data...",
      disabled: "Intelligence is off for this backend and library.",
      configured: candidates.length || generated.length ? "Current results from your enabled sources." : "Intelligence is configured, but there are no results yet.",
      degraded: data?.message || "Some enabled intelligence sources are unavailable. Existing results are still shown.",
      unauthorized: "This backend or library is not linked to your user.",
      error: data?.message || "Intelligence data could not be loaded.",
    }[state] || "No intelligence data is available.";
    return html`<section class="view-stack intelligence-view">
      <div class="view-header"><div><h2>Intelligence</h2>
        <p>Private listening signals, explained recommendations, and generated playlist previews.</p></div></div>
      <form class="panel config-grid" @submit=${this.loadIntelligence} aria-label="Intelligence scope">
        <div class="form-row"><label>Protocol</label><select name="protocol"><option value="jellyfin">Jellyfin</option><option value="subsonic">Subsonic / Navidrome</option></select></div>
        <div class="form-row"><label>Backend instance ID</label><input name="backendInstanceId" maxlength="200" required></div>
        <div class="form-row"><label>Library scope</label><input name="libraryScopeId" maxlength="300" required></div>
        <div class="actions"><button class="primary" ?disabled=${this.intelligenceLoading}>${this.intelligenceLoading ? "Loading..." : "Load intelligence"}</button></div>
      </form>
      <div class="panel intelligence-state ${state}" role="status" aria-live="polite">
        <span class="status-chip ${state === "configured" ? "configured" : state}">${titleCase(state)}</span>
        <p>${stateMessage}</p>
      </div>
      ${data?.scope && !["loading", "unauthorized", "error"].includes(state) ? html`
        <form class="panel config-grid" @submit=${this.saveIntelligencePolicy}>
          <div class="section-heading full-span"><div><h3>Privacy and retention</h3><p>Nothing is collected until you opt in. Turning this off stops new signals. Expired signals are removed by the retention job.</p></div></div>
          <label class="toggle-row"><input type="checkbox" name="enabled" .checked=${Boolean(policy.enabled)}><span>Enable intelligence for this scope</span></label>
          <div class="form-row"><label>Keep signals for</label><select name="retentionDays" .value=${String(policy.retentionDays || 30)}><option value="7">7 days</option><option value="30">30 days</option><option value="90">90 days</option><option value="365">1 year</option></select></div>
          ${data.scope.protocol === "subsonic" ? html`
            <div class="form-row"><label>Navidrome / Subsonic username</label><input name="intelligenceTargetUsername" autocomplete="username"><small>${policy.targetCredentialReferenceId ? "A target credential is configured. Leave the password blank to keep it." : "Required to create generated playlists."}</small></div>
            <div class="form-row"><label>Navidrome / Subsonic password</label><input name="intelligenceTargetPassword" type="password" autocomplete="new-password"><small>Stored as an encrypted tenant-scoped secret. It is never returned to this page.</small></div>
          ` : nothing}
          <fieldset class="form-row"><legend>Signals</legend>${asArray(data.availableSignalTypes).map((item) => html`<label><input type="checkbox" name="signalTypes" value=${item.id} .checked=${Boolean(item.enabled)}>${item.label}</label>`)}</fieldset>
          <fieldset class="form-row"><legend>Sources</legend>${asArray(data.providers).map((item) => html`<label><input type="checkbox" name="providers" value=${item.id} .checked=${Boolean(item.enabled)} ?disabled=${!item.available}>${item.label} <span class="muted">${titleCase(item.state)}</span><small>${display(item.description)}</small></label>`)}</fieldset>
          <input type="hidden" name="expectedRevision" value=${policy.revision || 0}>
          <div class="actions"><button class="primary">Save privacy settings</button>${actions.canRun ? html`<button type="button" @click=${this.runIntelligence}>Run recommendations</button>` : nothing}<button class="danger" type="button" @click=${this.purgeIntelligence}>Turn off and clear my data</button></div>
        </form>
        ${this.renderIntelligenceResults(candidates, generated, schedules, visualization, actions, policy)}
      ` : nothing}
    </section>`;
  }

  renderIntelligenceResults(candidates, generated, schedules, visualization, actions, policy) {
    return html`<div class="wide-grid intelligence-results">
      <div class="panel"><h3>Recommendations</h3>${candidates.length ? html`<ol class="activity-list">
        ${candidates.map((item) => html`<li class="activity-item" tabindex="0"><div><strong>${display(item.title || item.trackKey)}</strong><div class="muted">${display(item.artist, item.source)}</div>
          <details><summary>Why this track</summary><ul>${asArray(item.explanations || item.signals).map((reason) => html`<li>${display(reason.explanation || reason.code || reason)}</li>`)}</ul></details></div><span class="status-chip configured">${Math.round(Number(item.score || 0) * 100)}%</span></li>`)}
      </ol>` : html`<div class="empty">No explained recommendations yet.</div>`}</div>
      <div class="panel"><h3>Generated playlists</h3><p class="muted">Creating a generated set queues a durable playlist action for a compatible Jellyfin or Navidrome target. Follow its saved state here or in Jobs.</p>
        ${actions?.canGenerate ? html`<form class="actions" @submit=${this.generateIntelligencePlaylist}><label>Preview name <input name="name" maxlength="200" required value="Your recommendations"></label><button class="primary">Create preview</button></form>` : nothing}
        ${generated.length ? html`<div class="activity-list">${generated.map((item) => html`<div class="activity-item" tabindex="0"><div><strong>${display(item.name)}</strong><div class="muted">${display(item.trackCount, 0)} tracks · ${formatDate(item.createdAt)}</div>${item.errorCode ? html`<div class="error-text">${display(item.errorCode)}</div>` : nothing}</div><span class="status-chip ${item.materialized ? "configured" : "unknown"}">${titleCase(item.state || "pending")}</span></div>`)}</div>` : html`<div class="empty">No generated playlists yet.</div>`}</div>
      <div class="panel"><h3>Listening profile</h3>${visualization.length ? html`<div class="intelligence-bars" role="img" aria-label="Listening profile values">${visualization.map((item) => html`<div class="stat-row"><span>${display(item.label || item.key)}</span><meter min="0" max="1" value=${Number(item.value || 0)}>${Number(item.value || 0)}</meter></div>`)}</div>` : html`<div class="empty">No retained profile data.</div>`}</div>
      <div class="panel full-span"><div class="section-heading"><div><h3>Recommendation automation</h3><p>Build a fresh playlist from your current listening habits on a durable schedule. Scheduled runs use the sources and privacy settings saved above.</p></div></div>
        ${policy?.enabled ? html`<form class="config-grid" @submit=${this.createIntelligenceSchedule} aria-label="Create recommendation schedule">
          <div class="form-row"><label>Playlist name</label><input name="name" maxlength="200" required value="Your recommendations"></div>
          <div class="form-row"><label>Tracks</label><input name="limit" type="number" min="1" max="500" required value="25"></div>
          <div class="form-row"><label>Schedule (cron)</label><input name="cronExpression" required value="0 8 * * *" inputmode="text"><small>Five-field cron expression.</small></div>
          <div class="form-row"><label>Time zone</label><input name="timeZoneId" required value=${Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC"}></div>
          <div class="form-row"><label>When a run is still active</label><select name="overlapPolicy"><option value="skip">Skip this occurrence</option><option value="queue">Queue another run</option></select></div>
          <div class="form-row"><label>After downtime</label><select name="misfirePolicy"><option value="runOnce">Run once</option><option value="skip">Wait for the next occurrence</option></select></div>
          <div class="actions full-span"><button class="primary">Add schedule</button></div>
        </form>` : html`<div class="empty">Enable and save intelligence before adding an automation.</div>`}
        ${schedules.length ? html`<div class="activity-list" aria-label="Recommendation schedules">${schedules.map((schedule) => html`
          <div class="activity-item" tabindex="0"><div><strong>${display(schedule.name)}</strong><div class="muted">${display(schedule.limit)} tracks · ${display(schedule.cronExpression)} · ${display(schedule.timeZoneId)}</div><small>${schedule.enabled ? `Next run ${formatDate(schedule.nextRunAt)}` : "Paused"}</small></div>
            <div class="actions"><span class="status-chip ${schedule.enabled ? "configured" : "unknown"}">${schedule.enabled ? "Active" : "Paused"}</span><button type="button" @click=${() => this.toggleIntelligenceSchedule(schedule)}>${schedule.enabled ? "Pause" : "Resume"}</button><button class="danger" type="button" ?disabled=${!schedule.enabled} @click=${() => this.disableIntelligenceSchedule(schedule)}>Disable</button></div>
          </div>`)} </div>` : html`<div class="empty">No recommendation schedules yet.</div>`}
      </div>
    </div>`;
  }

  renderSetupStep(label, complete) {
    return html`
      <div class="stat-row">
        <span>${label}</span>
        <span class="chip ${complete ? "success" : "warning"}">${complete ? "Ready" : "Needs setup"}</span>
      </div>
    `;
  }

  renderLibrary() {
    const [, requestedSub] = routeParts(this.route);
    const sub = requestedSub || "link";
    return html`
      <section class="view-stack">
        <div class="view-header">
          <div>
            <h2>Library</h2>
            <p>Match provider playlists to your local library, keep their order, and choose where they show up.</p>
          </div>
        </div>
        ${this.renderLibraryNav(sub)}
        ${sub === "link" ? this.renderLinkPlaylists() :
          sub === "injected" ? this.renderInjectedPlaylists() :
          sub === "mappings" ? this.renderMappings() :
          sub === "missing" ? this.renderMissingTracks() :
          sub === "migration" ? this.renderSongMigration() :
          sub === "kept" ? this.renderKeptDownloads() :
          sub === "external" ? this.renderExternalPlaylistExplorer() :
          this.renderLinkPlaylists()}
      </section>
    `;
  }

  renderLibraryNav(active) {
    const items = [
      ["link", "Playlist links"],
      ["injected", "Injected"],
      ["mappings", "Mappings"],
      ["external", "External playlists"],
      ["kept", "Kept"],
    ];
    return html`
      <nav class="subnav">
        ${items.map(([id, label]) => html`<a class=${active === id ? "active" : ""} href="#/library/${id}">${label}</a>`)}
      </nav>
    `;
  }

  renderLibraryOverview() {
    return html`
      <div class="grid">
        <button class="card" @click=${() => this.navigate("/library/link")}>
          <h3>Link playlists</h3>
          <p class="muted">Connect provider playlists to Jellyfin or Navidrome using songs already in your local library.</p>
        </button>
        <button class="card" @click=${() => this.navigate("/library/mappings")}>
          <h3>Track mappings</h3>
          <p class="muted">Manage manual Spotify to local or external mappings.</p>
        </button>
        <button class="card" @click=${() => this.navigate("/library/external")}>
          <h3>External playlists</h3>
          <p class="muted">Browse provider playlists from Deezer, Qobuz, SquidWTF, and Apple Music.</p>
        </button>
        <button class="card" @click=${() => this.navigate("/library/kept")}>
          <h3>Kept downloads</h3>
          <p class="muted">Review permanent downloads and archives.</p>
        </button>
      </div>
    `;
  }

  renderLinkPlaylists() {
    const links = asArray(this.playlistLinks);
    return html`
      <div class="playlist-link-layout">
        <div class="view-stack">
          <form class="panel form-stack" aria-label="Create playlist link" @submit=${this.createPlaylistLink}>
            <div><h3>New playlist link</h3><p class="muted">Only songs already found in the selected local library are added. Allstarr never moves the music files.</p></div>
            <div class="playlist-link-form-grid">
              <div class="form-row"><label for="playlist-provider-account">Provider account</label><select id="playlist-provider-account" name="providerAccountId" required><option value="">Choose an account</option>${this.providerAccounts.map((account) => html`<option value=${account.id || account.Id}>${providerDisplayName(account.providerId || account.ProviderId, this.schema?.providers)} · ${account.displayName || account.DisplayName || "Account"}</option>`)}</select></div>
              <div class="form-row"><label for="playlist-source-id">Source playlist ID</label><input id="playlist-source-id" name="sourcePlaylistId" required autocomplete="off" placeholder="Playlist ID or stable provider reference"></div>
              <div class="form-row"><label for="playlist-library-scope">Library scope ID</label><input id="playlist-library-scope" name="libraryScopeId" required autocomplete="off" placeholder="Local music library ID"></div>
              <div class="form-row"><label for="playlist-target-backend">Target</label><select id="playlist-target-backend" name="targetProtocol" required @change=${(event) => { event.currentTarget.form.querySelector("[data-backend-credentials]").hidden = event.target.value !== "subsonic"; }}><option value="jellyfin">Jellyfin</option><option value="subsonic">Navidrome / Subsonic</option></select></div>
              <div class="form-row"><label for="playlist-target-instance">Backend instance ID</label><input id="playlist-target-instance" name="targetBackendInstanceId" required autocomplete="off"></div>
              <div class="form-row"><label for="playlist-target-id">Existing target playlist ID <span class="muted">(optional)</span></label><input id="playlist-target-id" name="targetPlaylistId" autocomplete="off"></div>
              <div class="form-row" data-backend-credentials hidden><label for="playlist-target-username">Navidrome / Subsonic username</label><input id="playlist-target-username" name="targetUsername" autocomplete="username"><label for="playlist-target-password">Password</label><input id="playlist-target-password" name="targetPassword" type="password" autocomplete="new-password"><p class="muted">Stored encrypted when you submit. The password is never shown again.</p></div>
              <div class="form-row"><label for="playlist-mode">How it should appear</label><select id="playlist-mode" name="mode"><option value="virtual">Virtual</option><option value="materialized">Filled in locally</option><option value="hybrid">Both</option></select></div>
              <div class="form-row"><label for="playlist-write-mode">Update behavior</label><select id="playlist-write-mode" name="materializationMode"><option value="reconcile">Keep in sync</option><option value="recreate">Recreate each run</option></select></div>
              <div class="form-row"><label for="playlist-trigger">Run</label><select id="playlist-trigger" name="trigger" @change=${(event) => { event.currentTarget.form.querySelector("[data-schedule]").hidden = event.target.value !== "scheduled"; }}><option value="manual">Manually</option><option value="scheduled">On a schedule</option></select></div>
              <div class="form-row" data-schedule hidden><label for="playlist-cron">Schedule (cron)</label><input id="playlist-cron" name="cronExpression" value="0 8 * * *" inputmode="text"><label for="playlist-timezone">Time zone</label><input id="playlist-timezone" name="timeZoneId" value=${Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC"}></div>
            </div>
            <div class="playlist-link-options" role="group" aria-label="Playlist update rules"><label class="inline-check"><input type="checkbox" name="syncName" checked> Copy name</label><label class="inline-check"><input type="checkbox" name="syncDescription" checked> Copy description</label><label class="inline-check"><input type="checkbox" name="syncArtwork" checked> Copy artwork</label><label class="inline-check"><input type="checkbox" name="preserveManualEntries" checked> Keep manually added songs</label><label class="inline-check"><input type="checkbox" name="mirrorStaleEntries"> Remove stale synced songs</label></div>
            <div class="actions"><button class="primary" type="submit">Create link</button><button type="button" @click=${() => this.loadPlaylistLinks()}>Refresh</button></div>
          </form>

          <div class="table-wrap"><table><thead><tr><th>Playlist</th><th>Source</th><th>Target</th><th>Mode</th><th>Last run</th><th></th></tr></thead><tbody>
            ${links.length ? links.map((link) => this.renderPlaylistLinkRow(link)) : html`<tr><td colspan="6"><div class="empty">No playlist links yet.</div></td></tr>`}
          </tbody></table></div>
        </div>
        ${this.renderPlaylistLinkPreview()}
      </div>`;
  }

  renderPlaylistLinkRow(link) {
    const id = link.id || link.Id;
    const provider = link.provider || link.Provider || link.providerId || link.ProviderId || "provider";
    const target = link.targetProtocol || link.TargetProtocol || link.targetBackendType || link.TargetBackendType || link.backendType || link.BackendType;
    const state = link.lastRunState || link.LastRunState || link.state || link.State || "ready";
    return html`<tr>
      <td><strong>${link.name || link.Name || "Untitled playlist"}</strong><div class="muted mono">${display(id)}</div></td>
      <td>${providerDisplayName(provider, this.schema?.providers)}<div class="muted mono">${display(link.sourcePlaylistId || link.SourcePlaylistId)}</div></td>
      <td>${String(target).toLowerCase() === "subsonic" ? "Navidrome / Subsonic" : display(target)}</td>
      <td>${titleCase(link.mode || link.Mode)} · ${titleCase(link.materializationMode || link.MaterializationMode)}</td>
      <td><span class="status-chip ${String(state).toLowerCase()}">${titleCase(state)}</span><div class="muted">${formatDate(link.lastRunAt || link.LastRunAt)}</div></td>
      <td class="row-actions"><button @click=${() => this.loadPlaylistLinkPreview(id)}>Preview</button><button @click=${() => this.loadPlaylistLinkPreview(id, true)}>Refresh source</button><button class="primary" @click=${() => this.runPlaylistLink(id)}>Run now</button>${String(target).toLowerCase() === "subsonic" ? html`<details><summary>Rotate credentials</summary><form class="form-stack" @submit=${(event) => this.savePlaylistBackendCredential(link, event)}><input name="username" aria-label="Subsonic username" autocomplete="username" required><input name="password" aria-label="Subsonic password" type="password" autocomplete="new-password" required><button type="submit">Save encrypted credentials</button></form></details>` : nothing}</td>
    </tr>`;
  }

  renderPlaylistLinkPreview() {
    if (!this.selectedPlaylistLinkId) {
      return html`<aside class="panel playlist-preview empty" aria-live="polite"><h3>Preview</h3><p>Choose a playlist link to review included songs, skipped songs, and conflicts before a run.</p></aside>`;
    }
    if (!this.playlistLinkPreview) {
      return html`<aside class="panel playlist-preview" aria-live="polite"><p>Loading preview…</p></aside>`;
    }
    const preview = this.playlistLinkPreview;
    const entries = asArray(preview.entries || preview.Entries);
    const included = entries.filter((entry) => String(entry.status || entry.Status).toLowerCase() === "included").length;
    const conflicts = entries.filter((entry) => ["ambiguous", "conflict"].includes(String(entry.status || entry.Status).toLowerCase())).length;
    return html`<aside class="panel playlist-preview" aria-live="polite">
      <div class="view-header"><div><h3>${preview.name || preview.Name || "Playlist preview"}</h3><p>${included} included · ${entries.length - included} skipped · ${conflicts} conflicts</p></div><button class="ghost" aria-label="Close preview" @click=${() => { this.selectedPlaylistLinkId = ""; this.playlistLinkPreview = null; }}>Close</button></div>
      <div class="actions"><button class="primary" @click=${() => this.runPlaylistLink(this.selectedPlaylistLinkId)}>Run now</button></div>
      <ol class="playlist-preview-list">${entries.length ? entries.map((entry) => this.renderPlaylistPreviewEntry(entry)) : html`<li class="empty">The source playlist has no tracks.</li>`}</ol>
    </aside>`;
  }

  renderPlaylistPreviewEntry(entry) {
    const status = String(entry.status || entry.Status || "unresolved").toLowerCase();
    const externalSnapshotId = entry.externalSnapshotId || entry.ExternalSnapshotId || entry.externalMetadataSnapshotId || entry.ExternalMetadataSnapshotId;
    const localTrackId = entry.libraryTrackId || entry.LibraryTrackId || entry.localTrackId || entry.LocalTrackId;
    const overrideId = entry.overrideId || entry.OverrideId;
    return html`<li class="playlist-preview-entry">
      <div><strong>${entry.title || entry.Title || "Unknown track"}</strong><div class="muted">${entry.artist || entry.Artist || entry.reason || entry.Reason || "No local match yet"}</div></div>
      <span class="chip ${status === "included" ? "success" : status === "ambiguous" ? "warning" : "support-unavailable"}">${titleCase(status)}</span>
      <div class="row-actions">
        ${externalSnapshotId && localTrackId ? html`<button @click=${() => this.reviewPlaylistMatch(externalSnapshotId, "Pin", localTrackId)}>Pin match</button>` : nothing}
        ${externalSnapshotId ? html`<button class="danger" @click=${() => this.reviewPlaylistMatch(externalSnapshotId, "Reject")}>Reject</button>` : nothing}
        ${overrideId ? html`<button class="ghost" @click=${() => this.clearPlaylistMatchReview(overrideId)}>Clear review</button>` : nothing}
      </div>
    </li>`;
  }

  createPlaylistLink = async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const account = this.providerAccounts.find((item) => String(item.id || item.Id) === String(data.get("providerAccountId")));
    const payload = {
      providerAccountId: data.get("providerAccountId"), sourceProviderId: String(account?.providerId || account?.ProviderId || "").toLowerCase(),
      sourcePlaylistId: data.get("sourcePlaylistId").trim(), libraryScopeId: data.get("libraryScopeId").trim(),
      targetProtocol: data.get("targetProtocol"), targetBackendInstanceId: data.get("targetBackendInstanceId").trim(),
      mode: data.get("mode"), materializationMode: data.get("materializationMode"),
      targetPlaylistId: data.get("targetPlaylistId").trim() || null,
      targetCredentialReferenceId: null,
      mirrorStaleEntries: data.get("mirrorStaleEntries") === "on", preserveManualEntries: data.get("preserveManualEntries") === "on",
      syncName: data.get("syncName") === "on", syncDescription: data.get("syncDescription") === "on", syncArtwork: data.get("syncArtwork") === "on",
    };
    if (payload.targetProtocol === "subsonic") {
      const username = String(data.get("targetUsername") || "").trim();
      const password = String(data.get("targetPassword") || "");
      if (!username || !password) throw new Error("Navidrome / Subsonic username and password are required");
      const credential = await API.createPlaylistBackendCredential({ targetProtocol: "subsonic", username, password });
      payload.targetCredentialReferenceId = credential.referenceId || credential.ReferenceId;
    }
    const created = await API.createPlaylistLink(payload);
    const linkId = created.id || created.Id || created.playlistLink?.id || created.PlaylistLink?.Id;
    if (data.get("trigger") === "scheduled" && linkId) {
      await API.createPlaylistSchedule(linkId, { cronExpression: data.get("cronExpression").trim(), timeZoneId: data.get("timeZoneId").trim(), overlapPolicy: "skip", misfirePolicy: "runOnce", enabled: true });
    }
    form.reset();
    await this.loadPlaylistLinks();
    this.toast("Playlist link created");
  };

  async savePlaylistBackendCredential(link, event) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const body = { targetProtocol: "subsonic", username: String(data.get("username") || "").trim(), password: String(data.get("password") || "") };
    const referenceId = link.targetCredentialReferenceId || link.TargetCredentialReferenceId;
    if (referenceId) {
      await API.rotatePlaylistBackendCredential(referenceId, body);
    } else {
      const credential = await API.createPlaylistBackendCredential(body);
      await API.updatePlaylistLink(link.id || link.Id, {
        expectedRevision: link.revision ?? link.Revision, mode: String(link.mode || link.Mode).toLowerCase(),
        materializationMode: String(link.materializationMode || link.MaterializationMode).toLowerCase(),
        scheduleId: link.scheduleId || link.ScheduleId || null, targetPlaylistId: link.targetPlaylistId || link.TargetPlaylistId || null,
        targetCredentialReferenceId: credential.referenceId || credential.ReferenceId,
        mirrorStaleEntries: Boolean(link.mirrorStaleEntries ?? link.MirrorStaleEntries), preserveManualEntries: Boolean(link.preserveManualEntries ?? link.PreserveManualEntries),
        syncName: Boolean(link.syncName ?? link.SyncName), syncDescription: Boolean(link.syncDescription ?? link.SyncDescription), syncArtwork: Boolean(link.syncArtwork ?? link.SyncArtwork)
      });
    }
    form.reset();
    await this.loadPlaylistLinks();
    this.toast("Backend credentials stored encrypted");
  }

  async runPlaylistLink(id) {
    const response = await API.runPlaylistLink(id);
    await this.loadPlaylistLinks();
    this.toast(response?.message || response?.Message || "Playlist run queued");
  }

  async reviewPlaylistMatch(externalSnapshotId, decision, libraryTrackId = null) {
    await API.overridePlaylistMatch(externalSnapshotId, { decision, libraryTrackId });
    await this.loadPlaylistLinkPreview(this.selectedPlaylistLinkId);
    this.toast(decision === "Pin" ? "Match pinned" : "Match rejected");
  }

  async clearPlaylistMatchReview(overrideId) {
    await API.deletePlaylistMatchOverride(overrideId);
    await this.loadPlaylistLinkPreview(this.selectedPlaylistLinkId);
    this.toast("Match review cleared");
  }

  renderInjectedPlaylists() {
    const playlists = asArray(this.playlists?.playlists || this.playlists?.Playlists);
    return html`
      <div class="panel">
        <div class="toolbar">
          <button class="primary" @click=${async () => { await this.loadPlaylists(true); this.toast("Playlists refreshed"); }}>Refresh</button>
          <button @click=${async () => { await API.refreshPlaylists(); this.toast("Refresh requested"); }}>Refresh all</button>
          <form class="toolbar" @submit=${this.addInjectedPlaylist}>
            <div class="form-row">
              <label>Name</label>
              <input name="name" required>
            </div>
            <div class="form-row">
              <label>Spotify ID</label>
              <input name="spotifyId" required>
            </div>
            <button>Add</button>
          </form>
        </div>
      </div>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Name</th><th>Tracks</th><th>Local</th><th>External</th><th>Schedule</th><th></th></tr></thead>
          <tbody>
            ${playlists.length ? playlists.map((playlist) => html`
              <tr>
                <td><strong>${playlist.name}</strong><div class="muted mono">${playlist.id}</div></td>
                <td>${display(playlist.trackCount)}</td>
                <td>${display(playlist.localTracks)}</td>
                <td>${display(playlist.externalTracks)}</td>
                <td><span class="mono">${display(playlist.syncSchedule)}</span></td>
                <td class="row-actions">
                  <button @click=${async () => { await API.refreshPlaylist(playlist.name); this.toast("Playlist refresh requested"); }}>Refresh</button>
                  <button @click=${async () => { await API.matchPlaylist(playlist.name); this.toast("Matching requested"); }}>Match</button>
                  <button @click=${async () => { await API.clearPlaylistCache(playlist.name); this.toast("Cache cleared"); }}>Clear</button>
                  <button class="danger" @click=${async () => { await API.removePlaylist(playlist.name); await this.loadPlaylists(true); this.toast("Playlist removed"); }}>Remove</button>
                </td>
              </tr>
            `) : html`<tr><td colspan="6"><div class="empty">No injected playlists loaded.</div></td></tr>`}
          </tbody>
        </table>
      </div>
    `;
  }

  addInjectedPlaylist = async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    await API.addPlaylist(data.get("name"), data.get("spotifyId"));
    form.reset();
    await this.loadPlaylists(true);
    this.restartKeys = new Set([...this.restartKeys, "SPOTIFY_IMPORT_PLAYLISTS"]);
    this.toast("Playlist added");
  };

  renderMappings() {
    const mappings = asArray(this.mappings?.mappings || this.mappings?.Mappings);
    const stats = this.mappings?.stats || this.mappings?.Stats || {};
    const pagination = this.mappings?.pagination || this.mappings?.Pagination || {};

    return html`
      <div class="grid">
        <div class="card metric"><span class="metric-label">Total</span><span class="metric-value">${display(stats.total ?? 0)}</span></div>
        <div class="card metric"><span class="metric-label">Accepted</span><span class="metric-value">${display(stats.accepted ?? 0)}</span></div>
        <div class="card metric"><span class="metric-label">Needs review</span><span class="metric-value">${display(stats.review ?? 0)}</span></div>
        <div class="card metric"><span class="metric-label">Unresolved</span><span class="metric-value">${display(stats.unresolved ?? 0)}</span></div>
      </div>
      <div class="panel">
        <div class="toolbar">
          <div class="form-row">
            <label>Search</label>
            <input .value=${this.mappingFilters.search} @input=${(event) => { this.mappingFilters.search = event.target.value; }}>
          </div>
          <div class="form-row">
            <label>State</label>
            <select .value=${this.mappingFilters.state} @change=${(event) => { this.mappingFilters.state = event.target.value; }}>
              <option value="">All</option>
              <option value="unresolved">Unresolved</option>
              <option value="suggested">Suggested</option>
              <option value="ambiguous">Ambiguous</option>
              <option value="accepted">Accepted</option>
              <option value="pinned">Pinned</option>
              <option value="rejected">Rejected</option>
            </select>
          </div>
          <div class="form-row"><label>Library scope</label><input .value=${this.mappingFilters.libraryScopeId} @input=${(event) => { this.mappingFilters.libraryScopeId = event.target.value; }} placeholder="music"></div>
          <button class="primary" @click=${async () => { this.mappingFilters.page = 1; await this.loadMappings(); }}>Apply</button>
        </div>
      </div>
      <div class="panel">
        <h3>Manual match review</h3>
        <p class="muted">Pin a provider snapshot to an indexed local track, or reject the current match. Provider identities remain attached to the same canonical recording.</p>
        <form class="toolbar" data-match-review @submit=${this.saveManualMapping}>
          <div class="form-row"><label>External snapshot ID</label><input name="externalSnapshotId" required></div>
          <div class="form-row">
            <label>Decision</label>
            <select name="decision">
              <option value="pin">Pin local track</option>
              <option value="reject">Reject match</option>
            </select>
          </div>
          <div class="form-row"><label>Indexed library track ID</label><input name="libraryTrackId"></div>
          <div class="form-row"><label>Reason</label><input name="reason" required placeholder="Reviewed against my local library"></div>
          <button>Save review</button>
        </form>
      </div>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Provider track</th><th>State</th><th>Local match</th><th>Provider identities</th><th>Confidence</th><th></th></tr></thead>
          <tbody>
            ${mappings.length ? mappings.map((mapping) => this.renderMappingRow(mapping)) : html`
              <tr><td colspan="6"><div class="empty">No mappings found.</div></td></tr>
            `}
          </tbody>
        </table>
      </div>
      <div class="panel">
        <div class="actions">
          <button ?disabled=${(pagination.page ?? 1) <= 1} @click=${async () => { this.mappingFilters.page -= 1; await this.loadMappings(); }}>Previous</button>
          <span class="chip">Page ${display(pagination.page ?? 1)} of ${display(pagination.totalPages ?? 1)}</span>
          <button ?disabled=${(pagination.page ?? 1) >= (pagination.totalPages ?? 1)} @click=${async () => { this.mappingFilters.page += 1; await this.loadMappings(); }}>Next</button>
        </div>
      </div>
    `;
  }

  renderMappingRow(mapping) {
    const snapshotId = mapping.externalSnapshotId;
    const local = mapping.localTrack;
    const identities = asArray(mapping.providerIdentities);
    return html`
      <tr>
        <td>
          <strong>${display(mapping.title, "Unknown track")}</strong>
          <div class="muted">${display(mapping.artist, "Unknown artist")} · ${display(mapping.album, "Unknown album")}</div>
          <div class="mono">${display(mapping.providerId)} · ${display(snapshotId)}</div>
        </td>
        <td><span class="chip">${display(mapping.state)}</span></td>
        <td>
          ${local ? html`<strong>${display(local.title)}</strong><div class="muted">${display(local.artist)}</div><div class="mono">${display(local.id)}</div>` : html`<span class="muted">No accepted local track</span>`}
          ${asArray(mapping.candidates).map((candidate) => html`<div><button @click=${() => this.prefillMatchReview({ ...mapping, libraryTrackId: candidate.libraryTrackId }, "pin")}>Pin ${display(candidate.backendItemId, candidate.libraryTrackId)} (${Math.round(Number(candidate.confidence || 0) * 100)}%)</button></div>`)}
        </td>
        <td>${identities.length ? identities.map((item) => html`<span class="chip">${display(item.providerId)}: <span class="mono">${display(item.externalId)}</span></span>`) : html`<span class="muted">Not linked yet</span>`}</td>
        <td>${mapping.confidence == null ? html`<span class="muted">—</span>` : html`${Math.round(Number(mapping.confidence) * 100)}%<div class="muted">threshold ${Math.round(Number(mapping.threshold) * 100)}%</div>`}</td>
        <td>
          <button @click=${() => this.prefillMatchReview(mapping, "pin")}>Pin</button>
          <button @click=${() => this.prefillMatchReview(mapping, "reject")}>Reject</button>
          ${mapping.overrideId ? html`<button class="danger" @click=${async () => { await API.deleteMapping(mapping.overrideId, mapping.overrideRevision ?? 0); await this.loadMappings(); this.toast("Manual review cleared"); }}>Clear review</button>` : ""}
        </td>
      </tr>
    `;
  }

  prefillMatchReview(mapping, decision) {
    const form = this.renderRoot.querySelector("form[data-match-review]") || this.renderRoot.querySelector('form input[name="externalSnapshotId"]')?.form;
    if (!form) return;
    form.elements.externalSnapshotId.value = mapping.externalSnapshotId || "";
    form.elements.decision.value = decision;
    form.elements.libraryTrackId.value = decision === "pin" ? (mapping.libraryTrackId || "") : "";
    form.elements.reason.focus();
  }

  saveManualMapping = async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const decision = data.get("decision");
    const payload = {
      decision,
      libraryTrackId: decision === "pin" ? (data.get("libraryTrackId") || null) : null,
      reason: data.get("reason"),
    };
    await API.saveMapping(data.get("externalSnapshotId"), payload);
    form.reset();
    await this.loadMappings();
    this.toast("Match review saved");
  };

  renderMissingTracks() {
    const playlists = asArray(this.playlists?.playlists || this.playlists?.Playlists);
    const missingCount = playlists.reduce((sum, playlist) => sum + Number(playlist.externalMissing || playlist.ExternalMissing || 0), 0);
    return html`
      <div class="panel">
        <div class="toolbar">
          <button class="primary" @click=${async () => { await this.loadMigrationData(); this.toast("Missing track data refreshed"); }}>Refresh</button>
          <span class="chip warning">${missingCount} unmatched</span>
        </div>
      </div>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Playlist</th><th>Tracks</th><th>Local</th><th>External</th><th>Missing</th></tr></thead>
          <tbody>
            ${playlists.length ? playlists.map((playlist) => html`
              <tr>
                <td><strong>${playlist.name}</strong></td>
                <td>${display(playlist.trackCount)}</td>
                <td>${display(playlist.localTracks)}</td>
                <td>${display(playlist.externalTracks)}</td>
                <td><span class="chip ${Number(playlist.externalMissing || 0) > 0 ? "warning" : "success"}">${display(playlist.externalMissing || 0)}</span></td>
              </tr>
            `) : html`<tr><td colspan="5"><div class="empty">No playlist data loaded.</div></td></tr>`}
          </tbody>
        </table>
      </div>
    `;
  }

  renderSongMigration() {
    const playlists = asArray(this.playlists?.playlists || this.playlists?.Playlists);
    const externalTotal = playlists.reduce((sum, playlist) => sum + Number(playlist.externalTracks || 0), 0);
    return html`
      <div class="grid">
        <div class="card metric"><span class="metric-label">Playlists</span><span class="metric-value">${playlists.length}</span></div>
        <div class="card metric"><span class="metric-label">External tracks</span><span class="metric-value">${externalTotal}</span></div>
      </div>
      <div class="panel">
        <div class="toolbar">
          <button class="primary" @click=${async () => { await this.loadMigrationData(); this.toast("Migration data refreshed"); }}>Refresh</button>
          <button @click=${() => this.downloadMigrationCsv()}>Export CSV</button>
        </div>
      </div>
      ${this.renderMissingTracks()}
    `;
  }

  downloadMigrationCsv() {
    const playlists = asArray(this.playlists?.playlists || this.playlists?.Playlists);
    const csv = [
      "playlist,total,local,external,missing",
      ...playlists.map((playlist) => [
        playlist.name,
        playlist.trackCount,
        playlist.localTracks,
        playlist.externalTracks,
        playlist.externalMissing || 0,
      ].map((value) => `"${String(value ?? "").replaceAll("\"", "\"\"")}"`).join(",")),
    ].join("\n");
    const blob = new Blob([csv], { type: "text/csv" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `allstarr-song-migration-${new Date().toISOString().slice(0, 10)}.csv`;
    link.click();
    URL.revokeObjectURL(url);
  }

  renderKeptDownloads() {
    const files = asArray(this.downloads?.files || this.downloads?.Files);
    return html`
      <div class="grid">
        <div class="card metric"><span class="metric-label">Files</span><span class="metric-value">${display(this.downloads?.count ?? this.downloads?.Count ?? files.length)}</span></div>
        <div class="card metric"><span class="metric-label">Size</span><span class="metric-value">${display(this.downloads?.totalSizeFormatted ?? this.downloads?.TotalSizeFormatted)}</span></div>
      </div>
      <div class="panel">
        <div class="actions">
          <button class="primary" @click=${async () => { await this.loadDownloads(); this.toast("Downloads refreshed"); }}>Refresh</button>
          <button class="danger" @click=${async () => { if (confirm("Delete all kept downloads?")) { await API.deleteAllDownloads(); await this.loadDownloads(); this.toast("Downloads deleted"); } }}>Delete all</button>
        </div>
      </div>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Artist</th><th>Album</th><th>File</th><th>Size</th><th></th></tr></thead>
          <tbody>
            ${files.length ? files.map((file) => html`
              <tr>
                <td>${display(file.artist)}</td>
                <td>${display(file.album)}</td>
                <td class="mono">${display(file.fileName)}</td>
                <td>${display(file.sizeFormatted)}</td>
                <td><button class="danger" @click=${async () => { await API.deleteDownload(file.path); await this.loadDownloads(); this.toast("Download deleted"); }}>Delete</button></td>
              </tr>
            `) : html`<tr><td colspan="5"><div class="empty">No kept downloads found.</div></td></tr>`}
          </tbody>
        </table>
      </div>
    `;
  }

  renderExternalPlaylistExplorer() {
    const results = asArray(this.externalPlaylists?.results || this.externalPlaylists?.Results);
    return html`
      <div class="panel">
        <div class="toolbar">
          <div class="form-row">
            <label>Provider</label>
            <select .value=${this.externalPlaylistProvider} @change=${(event) => { this.externalPlaylistProvider = event.target.value; }}>
              <option value="deezer">Deezer</option>
              <option value="qobuz">Qobuz</option>
            </select>
          </div>
          <div class="form-row">
            <label>Query</label>
            <input .value=${this.externalPlaylistQuery} @input=${(event) => { this.externalPlaylistQuery = event.target.value; }} @keydown=${(event) => { if (event.key === "Enter") this.searchExternalPlaylists(); }}>
          </div>
          <button class="primary" @click=${() => this.searchExternalPlaylists()}>Search</button>
        </div>
      </div>
      <div class="provider-grid">
        ${results.length ? results.map((playlist) => this.renderExternalPlaylistCard(playlist)) : html`<div class="empty">No external playlists loaded.</div>`}
      </div>
    `;
  }

  async searchExternalPlaylists() {
    if (!this.externalPlaylistQuery.trim()) {
      return;
    }
    this.externalPlaylists = await API.externalPlaylistSearch(this.externalPlaylistQuery.trim(), this.externalPlaylistProvider);
  }

  renderExternalPlaylistCard(playlist) {
    const provider = playlist.externalProvider || playlist.Provider;
    const externalId = playlist.externalId || playlist.ExternalId;
    const key = `${provider}:${externalId}`;
    const tracks = this.externalPlaylistTracks.get(key);
    return html`
      <div class="card provider-card">
        <div class="provider-head">
          <div class="provider-title">
            <strong>${playlist.name || playlist.Name}</strong>
            <span>${display(playlist.curatorName || playlist.CuratorName, provider)}</span>
          </div>
          <span class="status-chip configured">${provider}</span>
        </div>
        <div class="stat-list">
          <div class="stat-row"><span>Tracks</span><strong>${display(playlist.trackCount || playlist.TrackCount)}</strong></div>
          <div class="stat-row"><span>ID</span><span class="mono">${display(externalId)}</span></div>
        </div>
        <button @click=${async () => {
          const data = await API.externalPlaylistTracks(provider, externalId, 25);
          this.externalPlaylistTracks = new Map([...this.externalPlaylistTracks, [key, data]]);
        }}>Preview tracks</button>
        ${tracks ? html`
          <div class="activity-list">
            ${asArray(tracks.results || tracks.Results).slice(0, 5).map((track) => html`
              <div class="activity-item">
                <strong>${track.title || track.Title}</strong>
                <span class="muted">${track.artist || track.Artist}</span>
              </div>
            `)}
          </div>
        ` : nothing}
      </div>
    `;
  }

  renderSources() {
    if (!this.isAdministrator()) {
      return html`
        <section class="view-stack">
          <div class="view-header">
            <div>
              <h2>Provider accounts</h2>
              <p>Manage credentials for your own music provider accounts.</p>
            </div>
          </div>
          ${this.renderFavoritePolicy()}
          ${this.renderProviderAccounts()}
        </section>
      `;
    }

    const providers = asArray(this.schema?.providers);
    const providerGroups = [
      ["healthy", "Observed healthy", providers.filter((provider) => this.providerStatus(provider) === "healthy")],
      ["available", "Available but untested", providers.filter((provider) => ["unknown", "available", "partial_config"].includes(this.providerStatus(provider)))],
      ["attention", "Needs attention", providers.filter((provider) => ["needs_config", "needs_login", "testing", "degraded"].includes(this.providerStatus(provider)))],
      ["disabled", "Disabled providers", providers.filter((provider) => this.providerStatus(provider) === "disabled")],
    ].filter(([, , items]) => items.length > 0);
    return html`
      <section class="view-stack">
        <div class="view-header">
          <div>
            <h2>Services and sources</h2>
            <p>Connect providers and choose where Allstarr finds your music.</p>
          </div>
        </div>
        ${this.renderProviderAccounts()}
        ${providerGroups.map(([id, label, items]) => this.renderProviderSection(id, label, items))}
        <details class="content-disclosure" @toggle=${(event) => {
          if (event.currentTarget.open) void this.loadExtensionControlPlane();
        }}>
          <summary><span><strong>Routing and favorite behavior</strong><small>Source priority, capability details, and actions after favoriting a song</small></span></summary>
          <div class="disclosure-body">
            ${this.renderFavoritePolicy()}
            ${this.renderPriorityGroups()}
            ${this.renderProviderSupportMatrix()}
          </div>
        </details>
        <details class="content-disclosure">
          <summary><span><strong>Extension marketplace</strong><small>Add registries and manage optional provider extensions</small></span></summary>
          <div class="disclosure-body">${this.renderExtensions()}</div>
        </details>
      </section>
    `;
  }

  renderFavoritePolicy() {
    const effective = this.favoritePolicy?.effective || {};
    const managementMode = String(this.schema?.providerAccountManagementMode || "Hybrid");
    const canOverride = managementMode !== "AdminManaged" || this.isAdministrator();
    const toggle = (name, label, fallback = false) => html`<label class="toggle-row"><input type="checkbox" name=${name}
      .checked=${Boolean(effective[name] ?? fallback)}><span>${label}</span></label>`;
    return html`<div class="panel">
      <div class="section-heading"><div><h3>Favorite actions</h3>
        <p>Choose the optional work that runs after this backend successfully favorites a song. Downloads, metadata changes, placement, and refresh stay off until selected.</p>
        <p class="muted">Policies are isolated by your user, backend, and optional library. Current source: ${display(effective.source || "configured default")}</p>
      </div></div>
      ${canOverride ? html`<form class="config-grid" @submit=${this.saveFavoritePolicy}>
        <div class="form-row"><label>Protocol</label><select name="protocol"><option value="jellyfin">Jellyfin</option><option value="subsonic">Subsonic / Navidrome</option></select></div>
        <div class="form-row"><label>Backend instance ID</label><input name="backendInstanceId" maxlength="200" required></div>
        <div class="form-row"><label>Library scope (optional)</label><input name="libraryScopeId" maxlength="300"></div>
        ${this.isAdministrator() ? html`<div class="form-row"><label>Save as</label><select name="policyOwner"><option value="global">Global policy for this tenant/backend</option><option value="me">My override</option></select></div>` : nothing}
        <div class="form-row full-span"><div class="toggle-list">
          ${toggle("addToVirtualLiked", "Add to Allstarr virtual liked songs", true)}
          ${toggle("matchLocalLibrary", "Match against the local library")}
          ${toggle("autoDownload", "Download when no safe local match exists")}
          ${toggle("enrichMetadata", "Create and apply a managed-file metadata plan")}
          ${toggle("placeManagedFile", "Place the managed file into the selected library")}
          ${toggle("refreshBackendLibrary", "Ask the backend to refresh its library")}
        </div></div>
        <div class="actions"><button class="primary">Save favorite actions</button></div>
      </form>` : html`<div class="empty">Favorite actions are managed by an administrator.</div>`}
    </div>`;
  }

  renderProviderAccounts() {
    const accounts = asArray(this.providerAccounts);
    const administrator = Boolean(this.session?.isAdministrator || this.session?.IsAdministrator);
    const managementMode = String(this.schema?.providerAccountManagementMode || "Hybrid");
    const canManageAll = administrator && managementMode !== "UserManaged";
    const canManage = canManageAll || managementMode !== "AdminManaged";
    return html`
      <div class="panel">
        <div class="section-heading">
          <div>
            <h3>Provider accounts</h3>
            <p>Credentials are write-only, encrypted outside the database key boundary, and scoped to a user, library, or administrator-owned global account.</p>
            <p class="muted">Management mode: <span class="status-chip configured">${managementMode}</span></p>
          </div>
        </div>
        ${canManage ? html`<form class="config-grid" @submit=${this.createProviderAccount}>
          <div class="form-row"><label>Provider ID</label><input name="providerId" pattern="[a-z0-9]+(?:-[a-z0-9]+)*" required></div>
          <div class="form-row"><label>Display name</label><input name="displayName" required></div>
          <div class="form-row">
            <label>Scope</label>
            <select name="scope">
              <option value="User">My account</option>
              ${canManageAll ? html`<option value="Global">Global/shared</option><option value="Library">Library</option>` : nothing}
            </select>
          </div>
          ${canManageAll ? html`<div class="form-row"><label>Library scope (library accounts)</label><input name="libraryScopeId"></div>` : nothing}
          <div class="form-row full-span">
            <label>Credential JSON</label>
            <textarea name="secret" rows="3" placeholder='{"arl":"..."}' required></textarea>
            <small>Deezer uses <span class="mono">arl</span>. Qobuz uses <span class="mono">userAuthToken</span> and <span class="mono">userId</span>. Spotify uses <span class="mono">sp_dc</span> or <span class="mono">sessionCookie</span>.</small>
          </div>
          <div class="actions"><button class="primary">Add encrypted account</button></div>
        </form>` : html`<div class="empty">Provider accounts are managed by an administrator.</div>`}
      </div>
      ${canManage ? html`<div class="table-wrap">
        <table>
          <thead><tr><th>Provider</th><th>Scope</th><th>Credential</th><th>Capabilities</th><th>Status</th><th></th></tr></thead>
          <tbody>
            ${accounts.length ? accounts.map((account) => {
              const id = account.Id || account.id;
              const providerId = account.ProviderId || account.providerId;
              const secret = account.secret || account.Secret || {};
              const enabled = account.Enabled ?? account.enabled;
              const capabilities = this.providerHealth.filter((item) =>
                String(item.providerAccountId || item.ProviderAccountId).toLowerCase() === String(id).toLowerCase());
              return html`
                <tr>
                  <td><strong>${display(account.DisplayName || account.displayName)}</strong><div class="muted mono">${display(providerId)}</div></td>
                  <td>${display(account.scope || account.Scope)}${account.LibraryScopeId || account.libraryScopeId ? html`<div class="muted">${account.LibraryScopeId || account.libraryScopeId}</div>` : nothing}</td>
                  <td><span class="status-chip ${secret.configured ? "configured" : "needs_config"}">${secret.configured ? `Encrypted · v${secret.version || "?"}` : "Not set"}</span></td>
                  <td>
                    ${administrator && capabilities.length ? html`<div class="activity-list compact">
                      ${capabilities.map((capability) => {
                        const capabilityId = capability.capability || capability.Capability;
                        const health = capability.health || capability.Health || "unknown";
                        const configuration = capability.configuration || capability.Configuration || "needs_configuration";
                        const testKey = `${id}:${capabilityId}`;
                        const testing = this.providerTests.has(testKey);
                        const stateClass = health === "healthy"
                          ? "configured"
                          : health === "degraded"
                            ? "degraded"
                            : configuration === "needs_configuration" ? "needs_config" : "unknown";
                        return html`<div class="activity-item">
                          <div>
                            <strong>${titleCase(capabilityId)}</strong>
                            <div class="muted">${titleCase(configuration)} · ${titleCase(health)}${capability.testedAt ? ` · ${formatDate(capability.testedAt)}` : ""}</div>
                          </div>
                          <div class="actions">
                            <span class="status-chip ${stateClass}">${titleCase(health)}</span>
                            <button
                              ?disabled=${testing || !enabled}
                              @click=${() => this.testProviderAccountCapability(id, providerId, capabilityId)}
                            >${testing ? "Testing..." : "Test"}</button>
                          </div>
                        </div>`;
                      })}
                    </div>` : html`<span class="muted">${administrator ? "No testable capabilities" : "Administrator tested"}</span>`}
                  </td>
                  <td><span class="status-chip ${enabled ? "configured" : "disabled"}">${enabled ? "Enabled" : "Revoked"}</span></td>
                  <td>${enabled && canManage ? html`<button class="danger" @click=${async () => { await API.revokeProviderAccount(id); await this.loadProviderAccounts(); this.toast("Provider account revoked"); }}>Revoke</button>` : nothing}</td>
                </tr>
              `;
            }) : html`<tr><td colspan="6"><div class="empty">No scoped provider accounts yet.</div></td></tr>`}
          </tbody>
        </table>
      </div>` : nothing}
    `;
  }

  createProviderAccount = async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    let secret;
    try {
      secret = JSON.parse(String(data.get("secret") || "{}"));
    } catch {
      this.toast("Credential JSON is invalid", "error");
      return;
    }
    await API.createProviderAccount({
      providerId: String(data.get("providerId") || "").trim(),
      displayName: String(data.get("displayName") || "").trim(),
      scope: String(data.get("scope") || "User"),
      libraryScopeId: String(data.get("libraryScopeId") || "").trim() || null,
      enabled: true,
      secret,
    });
    form.reset();
    await this.loadProviderAccounts();
    this.toast("Encrypted provider account added");
  };

  async testProviderAccountCapability(accountId, provider, capability) {
    const testKey = `${accountId}:${capability}`;
    this.providerTests = new Set([...this.providerTests, testKey]);
    try {
      const result = await API.testProviderAccountCapability(accountId, provider, capability);
      await this.loadProviderAccounts();
      this.toast(
        `${providerDisplayName(provider, this.schema?.providers)} ${titleCase(capability)} test ${result.success ? "passed" : "failed"}`,
        result.success ? "success" : "error",
      );
    } catch (error) {
      this.toast(error.message, "error");
    } finally {
      const next = new Set(this.providerTests);
      next.delete(testKey);
      this.providerTests = next;
    }
  }

  renderProviderSupportMatrix() {
    const providers = asArray(this.schema?.providerSupportMatrix);
    return html`
      <div class="panel">
        <div class="section-heading">
          <div>
            <h3>Verified provider support</h3>
            <p>Current adapter coverage and limits. Partial does not mean every upstream feature is exposed.</p>
          </div>
        </div>
        <div class="table-wrap">
          <table class="support-matrix">
            <thead><tr><th>Provider</th><th>Account</th><th>Capabilities</th></tr></thead>
            <tbody>
              ${providers.map((provider) => html`
                <tr>
                  <td><strong>${provider.name}</strong><div class="muted">${provider.configuration}</div></td>
                  <td>${titleCase(provider.accountScope || "none")}</td>
                  <td>
                    <div class="chip-list capability-list">
                      ${asArray(provider.capabilities).map((capability) => html`
                        <span
                          class="chip support-${capability.state}"
                          title=${`${capability.protocolLimit} Tests: ${capability.testCoverage}`}
                        >${capability.id}: ${titleCase(capability.state)}</span>
                      `)}
                    </div>
                  </td>
                </tr>
              `)}
            </tbody>
          </table>
        </div>
      </div>
    `;
  }

  providerStatus(provider) {
    if (provider.id !== "apple-download" || !this.appleMusicStatus) {
      return provider.status;
    }

    const accountState = appleLoginState(this.appleMusicStatus);
    if (accountState === "awaiting_2fa") {
      return "needs_login";
    }
    if (this.appleMusicStatus.logged_in &&
        this.appleMusicStatus.staged &&
        this.appleMusicStatus.daemon_running &&
        this.appleMusicStatus.wrapper_healthy) {
      return "healthy";
    }
    if (this.appleMusicStatus.error ||
        this.appleMusicStatus.daemon_running === false ||
        this.appleMusicStatus.wrapper_healthy === false) {
      return "degraded";
    }
    return provider.status;
  }

  renderProviderSection(id, label, providers) {
    return html`
      <div class="provider-section provider-section-${id}">
        <h3>${label}</h3>
        ${providers.length ? html`
          <div class="provider-grid">
            ${providers.map((provider) => this.renderProviderCard(provider))}
          </div>
        ` : html`<div class="empty">No providers in this section.</div>`}
      </div>
    `;
  }

  renderProviderCard(provider) {
    const status = this.providerStatus(provider);
    const providerId = String(provider.id || provider.Id || "").toLowerCase();
    const logoUrl = providerLogoUrl(provider);
    const showBrandMark = Boolean(logoUrl) || !providersWithoutCardMark.has(providerId);
    const hasEditableConfig = asArray(provider.configSchema).length > 0;
    const open = hasEditableConfig &&
      (this.providerConfigOpen.has(providerId) || ["needs_config", "needs_login"].includes(status));
    return html`
      <div class="card provider-card">
        <div class="provider-head">
          <div class="provider-brand">
            ${showBrandMark ? html`
              <span class="provider-logo provider-${providerId}">
                ${logoUrl
                  ? html`<img src="${logoUrl}" alt="${provider.name} logo">`
                  : providerMark(provider)}
              </span>
            ` : nothing}
            <div class="provider-title">
              <strong>${provider.name}</strong>
              <span>${provider.id === "musicbrainz" ? "Genre enrichment" : "Provider"}</span>
            </div>
          </div>
          <span class="status-chip ${status}">${titleCase(status)}</span>
        </div>
        <div class="row-actions provider-actions">
          ${status !== "disabled" && hasEditableConfig ? html`
            <button @click=${() => {
              const next = new Set(this.providerConfigOpen);
              next.has(providerId) ? next.delete(providerId) : next.add(providerId);
              this.providerConfigOpen = next;
            }}>${open ? "Hide config" : "Configure"}</button>
          ` : nothing}
          ${status === "disabled" ? html`
            <button class="primary" @click=${() => this.setProviderDisabled(provider, false)}>Enable</button>
          ` : html`
            <button class="danger" @click=${() => this.setProviderDisabled(provider, true)}>Disable</button>
          `}
        </div>
        <div class="chip-list capability-list">
          ${asArray(provider.categories).map((category) => this.renderCapabilityPill(provider, category))}
          ${asArray(provider.notes).map((note) => html`<span class="chip">${note}</span>`)}
        </div>
        ${asArray(provider.runtimeCapabilities).length ? html`
          <div class="chip-list capability-list" aria-label="Runtime capability status">
            ${asArray(provider.runtimeCapabilities).map((capability) => html`
              <span
                class="chip runtime-${capability.health}"
                title=${capability.reasonCode
                  ? `${titleCase(capability.reasonCode)}; last tested ${formatDate(capability.testedAt)}`
                  : `Last tested ${formatDate(capability.testedAt)}`}
              >${titleCase(capability.id)}: ${titleCase(capability.configuration)} · ${titleCase(capability.health)}</span>
            `)}
          </div>
        ` : nothing}
        ${open ? html`
          <div class="config-grid">
            ${asArray(provider.configSchema).map((field) => this.renderConfigField(field))}
          </div>
          ${provider.id === "apple-download" ? this.renderAppleMusicManager() : nothing}
        ` : nothing}
      </div>
    `;
  }

  renderCapabilityPill(provider, category) {
    const enabled = this.providerCapabilityEnabled(provider, category);
    return html`
      <button
        class="chip capability-pill ${enabled ? "success" : "muted-chip"}"
        title=${`${enabled ? "Disable" : "Enable"} ${provider.name} for ${category}`}
        @click=${() => this.toggleProviderCapability(provider, category, !enabled)}>
        ${titleCase(category)}
      </button>
    `;
  }

  renderAppleMusicManager() {
    const status = this.appleMusicStatus || {};
    const account = status.account || {};
    const loginState = appleLoginState(status);
    const result = this.serviceResults.applemusic;
    const discoveredCapabilities = asArray(status.capabilities);
    return html`
      <div class="inline-panel">
        <div class="stat-list compact">
          <div class="stat-row"><span>External gateway</span><span class="status-chip ${status.ready ? "configured" : "needs_config"}">${titleCase(status.state || "unknown")}</span></div>
          <div class="stat-row"><span>API contract</span><span>${status.api_version || "Not discovered"}</span></div>
          <div class="stat-row"><span>Session</span><span class="status-chip ${status.logged_in || account.state === "authenticated" ? "configured" : "needs_config"}">${titleCase(loginState)}</span></div>
        </div>
        ${discoveredCapabilities.length ? html`
          <div>
            <strong>Discovered capabilities</strong>
            <div class="chip-list capability-list" aria-label="Discovered Apple download capabilities">
              ${discoveredCapabilities.map((capability) => html`
                <span
                  class="chip ${capability.state === "available" ? "success" : capability.state === "unsupported" ? "muted-chip" : "warning"}"
                  title=${capability.reason_code ? titleCase(capability.reason_code) : "Advertised and verified"}
                >${titleCase(capability.id)}: ${titleCase(capability.state)}</span>
              `)}
            </div>
          </div>
        ` : nothing}
        <form class="form-stack compact-form" @submit=${this.submitAppleLogin}>
          <div class="form-row"><label>Apple ID</label><input name="username" autocomplete="username" required></div>
          <div class="form-row"><label>Password</label><input name="password" type="password" autocomplete="current-password" required></div>
          <button class="primary">Start login</button>
        </form>
        ${isAwaitingApple2fa({ ...status, state: loginState }) || result?.state === "warning" ? html`
          <form class="form-stack compact-form" @submit=${this.submitApple2fa}>
            <div class="form-row"><label>2FA code</label><input name="code" inputmode="numeric" autocomplete="one-time-code" required></div>
            <button class="primary">Submit 2FA</button>
          </form>
        ` : nothing}
        ${result ? html`<div class="callout ${result.state}">${result.message}</div>` : nothing}
      </div>
    `;
  }

  renderPriorityGroups() {
    const providers = asArray(this.schema?.providers);
    return html`
      <div class="panel">
        <h3>Provider priority</h3>
        <div class="grid">
          ${asArray(this.schema?.priorityGroups).map((group) => html`
            <div class="card">
              <h3>${group.label}</h3>
              <p class="muted priority-help">Drag providers into order. With the keyboard, use Alt + Up or Alt + Down.</p>
              <div class="priority-list" role="list" aria-label=${group.label}>
                ${asArray(group.providers).map((provider, index) => html`
                  <div
                    class="priority-item ${this.priorityDrag?.groupId === group.id && this.priorityDrag?.index === index ? "dragging" : ""}"
                    role="listitem"
                    draggable="true"
                    tabindex="0"
                    data-priority-group=${group.id}
                    aria-label=${`${providerDisplayName(provider, providers)}, position ${index + 1} of ${group.providers.length}`}
                    @dragstart=${(event) => this.startPriorityDrag(event, group, index)}
                    @dragover=${(event) => this.allowPriorityDrop(event, group)}
                    @drop=${(event) => this.dropPriority(event, group, index)}
                    @dragend=${() => { this.priorityDrag = null; }}
                    @keydown=${(event) => this.handlePriorityKeydown(event, group, index)}
                  >
                    <span class="priority-drag-handle" aria-hidden="true">⠿</span>
                    ${this.renderProviderToken(provider, providers)}
                    <span class="priority-position">${index + 1}</span>
                  </div>
                `)}
              </div>
            </div>
          `)}
        </div>
      </div>
    `;
  }

  renderProviderToken(providerId, providers = asArray(this.schema?.providers)) {
    const label = providerDisplayName(providerId, providers);
    const normalizedProviderId = String(providerId).toLowerCase();
    const logoUrl = providerLogoUrl({ id: normalizedProviderId, name: label });
    const showBrandMark = Boolean(logoUrl) || !providersWithoutCardMark.has(normalizedProviderId);
    return html`
      <span class="provider-token">
        ${showBrandMark ? html`
          <span class="provider-token-logo provider-${normalizedProviderId}">
            ${logoUrl ? html`<img src="${logoUrl}" alt="">` : providerMark({ id: normalizedProviderId, name: label }).slice(0, 2)}
          </span>
        ` : nothing}
        <span>${label}</span>
      </span>
    `;
  }

  startPriorityDrag(event, group, index) {
    this.priorityDrag = { groupId: group.id, index };
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData("text/plain", String(group.providers[index]));
  }

  allowPriorityDrop(event, group) {
    if (this.priorityDrag?.groupId !== group.id) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = "move";
  }

  async dropPriority(event, group, targetIndex) {
    if (this.priorityDrag?.groupId !== group.id) return;
    event.preventDefault();
    const sourceIndex = this.priorityDrag.index;
    this.priorityDrag = null;
    await this.reorderPriority(group, sourceIndex, targetIndex);
  }

  async handlePriorityKeydown(event, group, index) {
    if (!event.altKey || !["ArrowUp", "ArrowDown"].includes(event.key)) return;
    event.preventDefault();
    const target = index + (event.key === "ArrowUp" ? -1 : 1);
    if (target < 0 || target >= group.providers.length) return;
    await this.reorderPriority(group, index, target);
    this.updateComplete.then(() => {
      const items = this.querySelectorAll(`[data-priority-group="${CSS.escape(group.id)}"]`);
      items[target]?.focus();
    });
  }

  async reorderPriority(group, sourceIndex, targetIndex) {
    if (sourceIndex === targetIndex) return;
    const providers = [...group.providers];
    const [provider] = providers.splice(sourceIndex, 1);
    providers.splice(targetIndex, 0, provider);
    await this.savePriority(group, providers);
  }

  renderExtensions() {
    const registries = asArray(this.extensionRegistries);
    const packages = asArray(this.extensionPackages);
    const storeItems = asArray(this.extensionStore?.items || this.extensionStore?.Items || this.extensionStore);
    const errors = asArray(this.extensionStore?.errors || this.extensionStore?.Errors);
    return html`
      <div class="panel">
        <div class="view-header">
          <div>
            <h3>Extension control plane</h3>
            <p>Packages stay inactive until their checksum is verified, every requested permission is reviewed, and an administrator activates them.</p>
          </div>
          <div class="actions">
            <button @click=${async () => { await this.loadExtensionControlPlane(); this.toast("Extension status refreshed"); }}>Refresh status</button>
            <button class="primary" @click=${async () => { await this.loadExtensionStore(); this.toast("Registry catalogs loaded"); }}>Load catalogs</button>
          </div>
        </div>
        <div class="extension-safety-note">Registries must use HTTPS. Staging requires the package's published SHA-256 checksum and never activates code automatically.</div>
      </div>
      ${errors.length ? html`<div class="panel">${errors.map((error) => html`<div class="error-text">${error.Repository || error.repository}: ${error.Message || error.message}</div>`)}</div>` : nothing}
      <div class="extension-control-grid">
        <div class="panel">
          <h3>Registries</h3>
          <form class="config-grid" @submit=${(event) => this.createExtensionRegistry(event)}>
            <label class="config-field"><span>Name</span><input name="name" required maxlength="200" autocomplete="off" placeholder="Community registry"></label>
            <label class="config-field"><span>HTTPS registry JSON URL</span><input name="registryUrl" type="url" required pattern="https://.*" autocomplete="off" aria-describedby="extension-registry-help" placeholder="https://example.org/allstarr/registry.json"><small id="extension-registry-help">Use the direct URL to an Allstarr registry JSON document, not a GitHub repository or file-view page.</small></label>
            <div class="config-field extension-form-action"><span>&nbsp;</span><button class="primary" type="submit" ?disabled=${Boolean(this.extensionActions.registry)}>${this.extensionActions.registry || "Add registry"}</button></div>
          </form>
          ${this.extensionRegistryError ? html`<div class="error-text" role="alert">${this.extensionRegistryError}</div>` : nothing}
          <div class="activity-list">
            ${registries.length ? registries.map((item) => {
              const enabled = item.enabled ?? item.Enabled;
              const action = this.extensionActions[`registry:${item.id || item.Id}`];
              return html`
                <div class="activity-item">
                  <strong>${item.name || item.Name}</strong>
                  <span class="muted extension-value">${item.registryUrl || item.RegistryUrl}</span>
                  <div class="row-actions">
                    <span class="status-chip ${enabled ? "configured" : "disabled"}">${enabled ? "Enabled" : "Disabled"}</span>
                    <button ?disabled=${Boolean(action)} @click=${() => this.setExtensionRegistryEnabled(item, !enabled)}>${action || (enabled ? "Disable" : "Enable")}</button>
                  </div>
                </div>
              `;
            }) : html`<div class="empty">No registries configured. Allstarr does not add one automatically.</div>`}
          </div>
        </div>
        <div class="panel">
          <h3>Stage a package</h3>
          <form class="config-grid" @submit=${(event) => this.stageExtensionPackage(event)}>
            <label class="config-field"><span>Package URL</span><input name="downloadUrl" type="url" required pattern="https://.*" autocomplete="off" placeholder="https://example.org/provider.zip"></label>
            <label class="config-field"><span>SHA-256</span><input name="sha256" required minlength="64" maxlength="64" pattern="[A-Fa-f0-9]{64}" autocomplete="off" spellcheck="false"></label>
            <label class="config-field"><span>Registry</span><select name="registryId"><option value="">Direct package</option>${registries.filter((item) => item.enabled ?? item.Enabled).map((item) => html`<option value=${item.id || item.Id}>${item.name || item.Name}</option>`)}</select></label>
            <div class="config-field extension-form-action"><span>&nbsp;</span><button class="primary" type="submit">Verify and stage</button></div>
          </form>
        </div>
      </div>
      <div class="panel">
        <div class="view-header">
          <div><h3>Packages</h3><p>Each version has its own review, state, and rollback history.</p></div>
        </div>
        <div class="activity-list">
          ${packages.length ? packages.map((item) => {
              const id = item.id || item.Id;
              const action = this.extensionActions[id];
              const rawState = String(item.state || item.State || "unknown").replace(/[^a-z]/gi, "").toLowerCase();
              const state = ({ reviewrequired: "review required", rolledback: "rolled back" })[rawState] || rawState;
              const revision = item.revision ?? item.Revision ?? 0;
              const previousPackageId = item.previousPackageId || item.PreviousPackageId;
              const permissions = this.extensionPermissions.get(id);
              const allDecided = permissions?.length && permissions.every((review) => review.uiDecision === "approved" || review.uiDecision === "denied");
              return html`
                <div class="activity-item extension-package">
                  <div class="extension-package-heading">
                    <div><strong>${item.displayName || item.DisplayName || item.extensionId || item.ExtensionId}</strong><span class="muted">${item.extensionId || item.ExtensionId} · v${item.version || item.Version} · SDK ${item.sdkVersion || item.SdkVersion}</span></div>
                    <span class="status-chip ${state === "active" ? "configured" : state === "failed" ? "error" : state === "disabled" || state === "rolled back" || state === "uninstalled" ? "disabled" : "warning"}">${titleCase(state)}</span>
                  </div>
                  <span class="muted extension-checksum" title=${item.sha256 || item.Sha256}>SHA-256 ${item.sha256 || item.Sha256}</span>
                  <div class="row-actions">
                    ${state === "review required" ? html`<button ?disabled=${Boolean(action)} @click=${() => this.loadExtensionPermissions(item)}>${permissions ? "Reload permissions" : "Review permissions"}</button>` : nothing}
                    ${state === "staged" ? html`<button class="primary" ?disabled=${Boolean(action)} @click=${() => this.runExtensionAction(item, "Activating", () => API.activateExtensionPackage(id, revision), "Extension activated")}>Activate</button>` : nothing}
                    ${state === "active" ? html`<button ?disabled=${Boolean(action)} @click=${() => this.runExtensionAction(item, "Disabling", () => API.disableExtensionPackage(id, revision), "Extension disabled")}>Disable</button>` : nothing}
                    ${state === "active" && previousPackageId ? html`<button class="danger" ?disabled=${Boolean(action)} @click=${() => this.runExtensionAction(item, "Rolling back", () => API.rollbackExtensionPackage(id, revision), "Previous extension version restored")}>Rollback</button>` : nothing}
                    ${["staged", "disabled", "rolled back", "failed"].includes(state) ? html`<button class="danger" ?disabled=${Boolean(action)} @click=${() => { if (window.confirm("Remove this package version? Provider accounts and encrypted secrets will be retained.")) return this.runExtensionAction(item, "Uninstalling", () => API.uninstallExtensionPackage(id, revision), "Extension package uninstalled; provider accounts retained"); }}>Uninstall</button>` : nothing}
                    <button ?disabled=${Boolean(action)} @click=${async () => { this.selectedExtensionPackageId = id; const response = await API.extensionLogs(id); this.extensionLogs = asArray(response?.items || response?.Items || response); }}>View logs</button>
                  </div>
                  ${state === "failed" && (item.failureCode || item.FailureCode) ? html`<div class="error-text">${titleCase(item.failureCode || item.FailureCode)}</div>` : nothing}
                  ${permissions ? html`
                    <div class="extension-permission-review">
                      <strong>Requested permissions</strong>
                      ${permissions.map((review) => {
                        const permissionId = review.id || review.Id;
                        const decision = review.uiDecision;
                        return html`
                          <div class="extension-permission-row">
                            <div><span class="chip">${review.permissionKind || review.PermissionKind}</span> <span class="extension-value">${review.permissionValue || review.PermissionValue}</span>${(review.required ?? review.Required) ? html` <span class="chip support-policy_blocked">Required</span>` : nothing}</div>
                            <div class="row-actions" role="group" aria-label="Permission decision">
                              <button class=${decision === "approved" ? "primary" : ""} @click=${() => this.setExtensionPermissionDecision(id, permissionId, true)}>Approve</button>
                              <button class=${decision === "denied" ? "danger" : ""} @click=${() => this.setExtensionPermissionDecision(id, permissionId, false)}>Deny</button>
                            </div>
                          </div>
                        `;
                      })}
                      <div class="row-actions"><button class="primary" ?disabled=${!allDecided || Boolean(action)} @click=${() => this.reviewExtensionPermissions(item)}>Submit every decision</button><span class="muted">Every permission needs an explicit choice.</span></div>
                    </div>
                  ` : nothing}
                  ${action ? html`<div class="progress indeterminate"><span></span></div>` : nothing}
                </div>
              `;
            }) : html`<div class="empty">No packages staged.</div>`}
        </div>
      </div>
      <div class="extension-control-grid">
        <div class="panel">
          <h3>Store</h3>
          <div class="activity-list">
            ${storeItems.length ? storeItems.map((item) => {
              const key = item.id || item.Id || item.displayName || item.DisplayName;
              const action = this.extensionActions[key];
              const checksum = item.sha256 || item.Sha256;
              return html`
                <div class="activity-item">
                  <strong>${item.displayName || item.DisplayName}</strong>
                  <span class="muted">${display(item.description || item.Description)}</span>
                  <div class="row-actions">
                    <span class="chip">${display(item.version || item.Version)}</span>
                    ${checksum ? html`<span class="chip success">Checksum published</span>` : html`<span class="chip support-policy_blocked">No checksum</span>`}
                    <button class="primary" ?disabled=${!checksum || Boolean(action)} @click=${() => this.installExtension(item)}>
                      ${action || "Verify and stage"}
                    </button>
                  </div>
                  ${action ? html`<div class="progress indeterminate"><span></span></div>` : nothing}
                </div>
              `;
            }) : html`<div class="empty">Load the store to browse extensions.</div>`}
          </div>
        </div>
        <div class="panel">
          <h3>${this.selectedExtensionPackageId ? "Package logs" : "Recent extension logs"}</h3>
          <div class="extension-log-list">
            ${asArray(this.extensionLogs?.items || this.extensionLogs?.Items || this.extensionLogs).length ? asArray(this.extensionLogs?.items || this.extensionLogs?.Items || this.extensionLogs).map((entry) => html`
              <div class="extension-log-entry">
                <span class="chip">${entry.level || entry.Level}</span>
                <strong>${entry.eventCode || entry.EventCode}</strong>
                <span>${entry.message || entry.Message}</span>
                <small>${display(entry.createdAt || entry.CreatedAt)} · ${entry.correlationId || entry.CorrelationId}</small>
              </div>
            `) : html`<div class="empty">No extension events recorded.</div>`}
          </div>
        </div>
      </div>
    `;
  }

  renderActivity() {
    return html`
      <section class="view-stack">
        <div class="view-header">
          <div>
            <h2>Activity</h2>
            <p>Durable work, download activity, scrobbling, and endpoint usage.</p>
          </div>
          <button class="primary" @click=${async () => { await Promise.all([this.loadEndpointUsage(), this.loadScrobbling(), this.loadQueue(), this.loadJobs()]); this.toast("Activity refreshed"); }}>Refresh</button>
        </div>
        <div class="panel">
          <h3>Durable jobs</h3>
          ${this.renderDurableJobs()}
        </div>
        <div class="wide-grid">
          <div class="panel">
            <h3>Download queue</h3>
            ${this.renderActivityList(this.activity)}
          </div>
          <div class="panel">
            <h3>Scrobbling</h3>
            ${this.renderScrobbling()}
          </div>
        </div>
        <div class="panel">
          <div class="view-header">
            <div><h3>API analytics</h3></div>
            <button class="danger" @click=${async () => { await API.clearEndpointUsage(); await this.loadEndpointUsage(); this.toast("Endpoint usage cleared"); }}>Clear</button>
          </div>
          ${this.renderEndpointUsage()}
        </div>
      </section>
    `;
  }

  renderActivityList(items) {
    const entries = asArray(items);
    if (!entries.length) {
      return html`<div class="empty">No active download or playback activity.</div>`;
    }
    return html`
      <div class="activity-list">
        ${entries.map((entry) => html`
          <div class="activity-item">
            <div class="stat-row">
              <div>
                <strong>${display(entry.title || entry.Title, "Unknown track")}</strong>
                <div class="muted">${display(entry.artist || entry.Artist)} ${entry.externalProvider || entry.ExternalProvider ? `- ${entry.externalProvider || entry.ExternalProvider}` : ""}</div>
              </div>
              <span class="status-chip ${entry.isPlaying || entry.IsPlaying ? "configured" : ""}">${display(entry.status || entry.Status)}</span>
            </div>
            <div class="progress" style=${`--progress:${percent(entry.playbackProgress ?? entry.PlaybackProgress ?? entry.progress ?? entry.Progress)}%`}>
              <span></span>
            </div>
          </div>
        `)}
      </div>
    `;
  }

  renderDurableJobs() {
    const jobs = asArray(this.jobs);
    if (!jobs.length) {
      return html`<div class="empty">No durable jobs recorded.</div>`;
    }
    const terminal = new Set(["Succeeded", "Failed", "Cancelled"]);
    return html`
      <div class="table-wrap">
        <table>
          <thead><tr><th>Type</th><th>State</th><th>Runs / budgets</th><th>Available / finished</th><th>Failure</th><th></th></tr></thead>
          <tbody>
            ${jobs.map((job) => {
              const id = job.id || job.Id;
              const state = job.state || job.State;
              const failure = job.lastErrorMessage || job.LastErrorMessage || job.lastErrorCode || job.LastErrorCode;
              return html`
                <tr>
                  <td><strong>${display(job.type || job.Type)}</strong><div class="muted mono">${id}</div></td>
                  <td><span class="status-chip ${state === "Succeeded" ? "configured" : state === "Failed" ? "degraded" : "needs_config"}">${display(state)}</span></td>
                  <td>
                    <div>Runs: ${display(job.attemptCount ?? job.AttemptCount ?? 0)}</div>
                    <div class="muted">Failures: ${display(job.failureCount ?? job.FailureCount ?? 0)} / ${display(job.maxAttempts ?? job.MaxAttempts ?? 0)}</div>
                    <div class="muted">Waits: ${display(job.deferralCount ?? job.DeferralCount ?? 0)} / ${display(job.maxDeferrals ?? job.MaxDeferrals ?? 0)}</div>
                  </td>
                  <td>${formatDate(job.completedAt || job.CompletedAt || job.availableAt || job.AvailableAt)}</td>
                  <td>${failure ? html`<span class="error-text">${failure}</span>` : html`<span class="muted">—</span>`}</td>
                  <td>${!terminal.has(state) ? html`<button class="danger" @click=${async () => { await API.cancelJob(id); await this.loadJobs(); this.toast("Cancellation requested"); }}>Cancel</button>` : nothing}</td>
                </tr>
              `;
            })}
          </tbody>
        </table>
      </div>
    `;
  }

  renderScrobbling() {
    const status = this.scrobbling || {};
    const config = this.config?.scrobbling || {};
    const fields = [
      { key: "SCROBBLING_ENABLED", label: "Scrobbling", type: "toggle", valuePath: "scrobbling.enabled" },
      { key: "SCROBBLING_LOCAL_TRACKS_ENABLED", label: "Local tracks", type: "toggle", valuePath: "scrobbling.localTracksEnabled" },
      { key: "SCROBBLING_LASTFM_ENABLED", label: "Last.fm", type: "toggle", valuePath: "scrobbling.lastFm.enabled" },
      { key: "SCROBBLING_LASTFM_API_KEY", label: "Last.fm API key", type: "password", valuePath: "scrobbling.lastFm.apiKey", sensitive: true },
      { key: "SCROBBLING_LASTFM_SHARED_SECRET", label: "Last.fm secret", type: "password", valuePath: "scrobbling.lastFm.sharedSecret", sensitive: true },
      { key: "SCROBBLING_LISTENBRAINZ_ENABLED", label: "ListenBrainz", type: "toggle", valuePath: "scrobbling.listenBrainz.enabled" },
      { key: "SCROBBLING_LISTENBRAINZ_USER_TOKEN", label: "ListenBrainz token", type: "password", valuePath: "scrobbling.listenBrainz.userToken", sensitive: true },
    ];
    return html`
      <div class="stat-list">
        <div class="stat-row"><span>Runtime</span><span class="status-chip ${status.enabled || status.Enabled ? "configured" : "needs_config"}">${status.enabled || status.Enabled ? "Enabled" : "Disabled"}</span></div>
      </div>
      <div class="config-grid">${fields.map((field) => this.renderConfigField(field))}</div>
      <div class="actions scrobble-actions">
        <button @click=${() => this.runServiceAction("lastfm", API.testLastFm)}>Test Last.fm</button>
        <button @click=${() => this.runServiceAction("listenbrainz", API.testListenBrainz)}>Test ListenBrainz</button>
      </div>
      ${this.serviceResults.lastfm ? html`<div class="callout ${this.serviceResults.lastfm.state}">Last.fm: ${this.serviceResults.lastfm.message}</div>` : nothing}
      ${this.serviceResults.listenbrainz ? html`<div class="callout ${this.serviceResults.listenbrainz.state}">ListenBrainz: ${this.serviceResults.listenbrainz.message}</div>` : nothing}
    `;
  }

  renderEndpointUsage() {
    const endpoints = asArray(this.endpointUsage?.endpoints || this.endpointUsage?.Endpoints);
    return html`
      <div class="grid">
        <div class="card metric"><span class="metric-label">Requests</span><span class="metric-value">${display(this.endpointUsage?.totalRequests || this.endpointUsage?.TotalRequests || 0)}</span></div>
        <div class="card metric"><span class="metric-label">Endpoints</span><span class="metric-value">${display(this.endpointUsage?.totalEndpoints || this.endpointUsage?.TotalEndpoints || endpoints.length)}</span></div>
      </div>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Endpoint</th><th>Count</th></tr></thead>
          <tbody>
            ${endpoints.length ? endpoints.map((item) => html`<tr><td class="mono">${item.endpoint || item.Endpoint}</td><td>${item.count || item.Count}</td></tr>`) : html`<tr><td colspan="2"><div class="empty">No endpoint usage data.</div></td></tr>`}
          </tbody>
        </table>
      </div>
    `;
  }

  renderSettings() {
    return html`
      <section class="view-stack">
        <div class="view-header">
          <div>
            <h2>Settings</h2>
            <p>Configuration changes are saved on blur and marked when a restart is needed.</p>
          </div>
        </div>
        ${asArray(this.schema?.configSections).map((section) => html`
          <details class="content-disclosure panel">
            <summary><span><strong>${section.label}</strong><small>Show configuration</small></span></summary>
            <div class="config-grid disclosure-body">
              ${asArray(section.fields).map((field) => this.renderConfigField(field))}
            </div>
          </details>
        `)}
        <details class="content-disclosure panel">
          <summary><span><strong>Backup and restore</strong><small>Database backups and bootstrap export</small></span></summary>
          <div class="disclosure-body">
            <div class="stat-list compact">
              <div class="stat-row"><span>Durable database</span><span>${display(this.status?.durableStorage?.provider || this.status?.DurableStorage?.Provider)}</span></div>
              <div class="stat-row"><span>Readiness</span><span class="status-chip ${(this.status?.durableStorage?.readiness || this.status?.DurableStorage?.Readiness) === "Ready" ? "configured" : "degraded"}">${display(this.status?.durableStorage?.readiness || this.status?.DurableStorage?.Readiness, "Unknown")}</span></div>
            </div>
            <div class="actions">
              <button class="primary" @click=${async () => { await API.createDatabaseBackup(); this.toast("Verified database backup created"); }}>Create database backup</button>
              <button @click=${() => this.exportEnv()}>Export bootstrap .env</button>
            </div>
            <p class="muted">Restore and database-provider migration are offline operator procedures; the app never restores over its active database or fails over to SQLite.</p>
          </div>
        </details>
        <div class="setup-launcher">
          <div><h3>Setup guide</h3><p>Revisit the media server, sources, and first playlist steps whenever you need them.</p></div>
          <button @click=${() => this.openSetupGuide()}>Open setup guide</button>
        </div>
        ${this.renderEnvMigrationWizard()}
        <details class="content-disclosure panel danger-disclosure">
          <summary><span><strong>Maintenance actions</strong><small>Cache and restart controls</small></span></summary>
          <div class="actions disclosure-body">
            <button class="danger" @click=${async () => { if (confirm("Clear cache?")) { await API.clearCache(); this.toast("Cache clear requested"); } }}>Clear cache</button>
            <button class="danger" @click=${async () => { if (confirm("Restart Allstarr?")) { await API.restart(); this.toast("Restart requested"); } }}>Restart</button>
          </div>
        </details>
      </section>
    `;
  }

  canOfferEnvMigration() {
    const status = this.envMigrationStatus || {};
    const available = status.eligible ?? status.Eligible ?? status.firstRun ?? status.FirstRun ??
      status.available ?? status.Available ?? false;
    return Boolean(available) &&
      !Boolean(status.completed ?? status.Completed);
  }

  shouldShowSetupGuide() {
    return this.setupGuideOpen && Boolean(this.schema) && Boolean(this.config);
  }

  openSetupGuide() {
    this.setupStep = Math.max(0, Math.min(SETUP_GUIDE_LAST_STEP, Number(localStorage.getItem(SETUP_GUIDE_STEP_KEY)) || 0));
    this.setupGuideOpen = true;
    this.updateComplete.then(() => this.querySelector("#setup-guide-title")?.focus());
  }

  closeSetupGuide() {
    localStorage.setItem(SETUP_GUIDE_DISMISSED_KEY, "1");
    this.setupGuideOpen = false;
  }

  async completeSetupGuide() {
    if (this.onboardingSaving) return;
    this.onboardingSaving = true;
    try {
      this.onboardingStatus = await API.completeOnboarding();
      const migration = this.onboardingStatus?.migration || this.onboardingStatus?.Migration;
      if (migration) this.envMigrationStatus = migration;
      localStorage.setItem(SETUP_GUIDE_DISMISSED_KEY, "1");
      localStorage.removeItem(SETUP_GUIDE_STEP_KEY);
      this.setupGuideOpen = false;
      this.toast("Setup completion saved");
    } catch (error) {
      this.toast(error.message, "error");
    } finally {
      this.onboardingSaving = false;
    }
  }

  setSetupStep(step) {
    this.setupStep = Math.max(0, Math.min(SETUP_GUIDE_LAST_STEP, Number(step) || 0));
    localStorage.setItem(SETUP_GUIDE_STEP_KEY, String(this.setupStep));
    this.updateComplete.then(() => this.querySelector("#setup-guide-title")?.focus());
  }

  leaveSetupGuideFor(path) {
    localStorage.setItem(SETUP_GUIDE_STEP_KEY, String(this.setupStep));
    this.setupGuideOpen = false;
    this.navigate(path);
  }

  async refreshSetupChecks() {
    this.serviceResults = { ...this.serviceResults, setup: { state: "running", message: "Refreshing media server and provider readiness..." } };
    try {
      // Loading provider accounts also refreshes provider health for administrators.
      await Promise.all([this.loadStatus(), this.loadProviderAccounts()]);
      this.serviceResults = { ...this.serviceResults, setup: { state: "success", message: "Readiness refreshed. Your signed-in media server session is connected; review any source marked as needing setup." } };
    } catch (error) {
      this.serviceResults = { ...this.serviceResults, setup: { state: "error", message: error.message } };
    }
  }

  async revealRouteTarget(path, selector) {
    this.navigate(path);
    for (let attempt = 0; attempt < 6; attempt += 1) {
      await new Promise((resolve) => window.requestAnimationFrame(resolve));
      await this.updateComplete;
      const target = this.querySelector(selector);
      if (!target) continue;
      const reducedMotion = window.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches;
      target.scrollIntoView({ behavior: reducedMotion ? "auto" : "smooth", block: "start" });
      target.focus({ preventScroll: true });
      return true;
    }
    return false;
  }

  openEnvMigrationSettings() {
    localStorage.setItem(SETUP_GUIDE_STEP_KEY, String(this.setupStep));
    this.setupGuideOpen = false;
    void this.revealRouteTarget("/settings", "#env-migration-title");
  }

  handleSetupGuideKeydown(event) {
    if (event.key === "Escape") {
      event.preventDefault();
      this.closeSetupGuide();
      return;
    }
    if (event.key !== "Tab") return;
    const controls = [...event.currentTarget.querySelectorAll("button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), textarea:not([disabled])")];
    if (!controls.length) return;
    const first = controls[0];
    const last = controls.at(-1);
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  renderSetupGuide() {
    if (!this.shouldShowSetupGuide()) return nothing;
    const steps = ["Welcome", "Media server", "Sources", "First playlist", "Ready"];
    const activeBackendName = display(this.schema?.activeBackend || this.config?.backendType, "media server");
    const activeBackend = asArray(this.schema?.backends).find((backend) =>
      String(backend.id).toLowerCase() === String(activeBackendName).toLowerCase());
    const backendFields = asArray(activeBackend?.configSchema);
    const backendUrl = getPathValue(this.config, String(activeBackendName).toLowerCase() === "subsonic" ? "subsonic.url" : "jellyfin.url", "");
    const signedInBackend = String(this.authBackend || "").toLowerCase();
    const expectedBackend = String(activeBackendName).toLowerCase();
    const backendConnected = this.authenticated && signedInBackend === expectedBackend;
    const backendUser = display(this.session?.name || this.session?.Name, "your account");
    const providers = asArray(this.schema?.providers).filter((provider) => provider.status !== "disabled").slice(0, 6);
    const stepBody = [
      html`
        <h2 id="setup-guide-title" tabindex="-1" autofocus>Welcome to your music hub</h2>
        <p>Allstarr sits between your music apps, your local library, and the services you choose. Let’s connect the important parts first. You can fine-tune everything later.</p>
        <div class="setup-choice-grid">
          ${asArray(this.schema?.backends).map((backend) => {
            const active = String(backend.id).toLowerCase() === String(activeBackendName).toLowerCase();
            return html`<div class="setup-choice ${active ? "active" : ""}">
              <strong>${display(backend.name)}</strong>
              <span class="status-chip ${active ? "configured" : "disabled"}">${active ? "Active" : "Available"}</span>
              <small>${active ? "This is the media server selected by your Compose setup." : "You can switch backends later through your deployment configuration."}</small>
            </div>`;
          })}
        </div>
        ${this.canOfferEnvMigration() ? html`<div class="setup-legacy-path">
          <strong>Upgrading from Allstarr 2.x?</strong>
          <p>Import your old <code>.env</code> through a safe preview. Nothing is applied until you review and confirm it.</p>
          <button class="ghost" @click=${() => this.openEnvMigrationSettings()}>Import an Allstarr 2.x .env</button>
        </div>` : nothing}
      `,
      html`
        <h2 id="setup-guide-title" tabindex="-1" autofocus>Connect ${display(activeBackend?.name, activeBackendName)}</h2>
        <p>Check the server details below. Editable values save when you leave the field. Deployment-owned choices stay read-only so this screen cannot quietly rewrite your Compose setup.</p>
        <div class="setup-field-grid">
          ${backendFields.length ? backendFields.map((field) => this.renderConfigField(field)) : html`<div class="empty">No media server fields are available.</div>`}
        </div>
        <div class="setup-legacy-path">
          <div class="actions"><span class="status-chip ${backendConnected ? "healthy" : backendUrl ? "configured" : "needs_config"}">${backendConnected ? `Connected as ${backendUser}` : backendUrl ? "Server URL configured; sign-in check needed" : "Server URL needed"}</span><button @click=${() => this.refreshSetupChecks()}>Refresh readiness</button></div>
          <p class="muted">The WebUI only opens after the selected media server accepts your login, so this signed-in session is the connection test. Refresh checks the Allstarr control plane and configured source accounts.</p>
          ${this.serviceResults.setup ? html`<div class="callout ${this.serviceResults.setup.state}" role="status">${this.serviceResults.setup.message}</div>` : nothing}
        </div>
      `,
      html`
        <h2 id="setup-guide-title" tabindex="-1" autofocus>Choose how Allstarr finds music</h2>
        <p>You only need the services you actually use. Accounts are encrypted and can be shared by an administrator or kept per user.</p>
        <div class="setup-choice-grid">
          ${providers.map((provider) => {
            const observed = this.providerHealth.filter((item) => String(item.provider || item.Provider || item.providerId || item.ProviderId).toLowerCase() === String(provider.id).toLowerCase());
            const healthy = observed.some((item) => String(item.health || item.Health).toLowerCase() === "healthy");
            const state = healthy ? "healthy" : provider.status;
            return html`<div class="setup-choice">
              <strong>${display(provider.name)}</strong>
              <span class="status-chip ${state}">${healthy ? "Observed healthy" : titleCase(provider.status)}</span>
              <small>${asArray(provider.categories).map(titleCase).join(" · ") || "Optional music source"}</small>
            </div>`;
          })}
        </div>
        <div class="setup-legacy-path"><p>Provider logins, health tests, and source priority live together on the Sources screen.</p><div class="actions"><button @click=${() => this.refreshSetupChecks()}>Refresh health</button><button @click=${() => this.leaveSetupGuideFor("/sources")}>Configure sources now</button></div>${this.serviceResults.setup ? html`<div class="callout ${this.serviceResults.setup.state}" role="status">${this.serviceResults.setup.message}</div>` : nothing}</div>
      `,
      html`
        <h2 id="setup-guide-title" tabindex="-1" autofocus>Link your first playlist</h2>
        <p>Start with one playlist you know well. Allstarr previews its local matches, skipped tracks, order, artwork, and description before anything is written to Jellyfin or Navidrome.</p>
        <div class="setup-summary">
          <div class="setup-summary-item"><strong>1. Pick a source</strong><small>Choose a playlist from a connected provider account.</small></div>
          <div class="setup-summary-item"><strong>2. Review matches</strong><small>Accept or pin the local songs you trust. Unmatched tracks stay visible.</small></div>
          <div class="setup-summary-item"><strong>3. Choose delivery</strong><small>Keep it virtual, reconcile a backend playlist, or explicitly recreate it each run.</small></div>
        </div>
        <div class="setup-legacy-path"><p>This step is safe to revisit. Playlist sync never deletes or rewrites audio files.</p><button @click=${() => this.leaveSetupGuideFor("/library/link")}>Open playlist links</button></div>
      `,
      html`
        <h2 id="setup-guide-title" tabindex="-1" autofocus>Your hub is ready to shape</h2>
        <p>The basics are in place. Allstarr will keep your original music files in their library folders while its database stores settings, matches, jobs, and other durable state.</p>
        <div class="setup-summary">
          <div class="setup-summary-item"><strong>${display(activeBackend?.name, activeBackendName)}</strong><small>${backendConnected ? `Connected as ${backendUser}` : backendUrl ? "Server URL configured; reconnect to verify it" : "Server URL still needs attention"}</small></div>
          <div class="setup-summary-item"><strong>Sources</strong><small>Connect only the providers you want, then drag them into your preferred order.</small></div>
          <div class="setup-summary-item"><strong>Playlists</strong><small>Link external playlists when you are ready. Preview matching before the first run.</small></div>
        </div>
      `,
    ][this.setupStep];
    return html`
      <div class="modal-backdrop setup-guide-backdrop" @keydown=${(event) => this.handleSetupGuideKeydown(event)}>
        <section class="setup-guide" role="dialog" aria-modal="true" aria-labelledby="setup-guide-title">
          <header class="setup-guide-header">
            <div class="setup-guide-brand"><strong>Allstarr setup</strong><button class="ghost" @click=${() => this.closeSetupGuide()} aria-label="Close setup guide">Close</button></div>
            <ol class="setup-progress" aria-label="Setup progress">${steps.map((label, index) => html`<li class=${index === this.setupStep ? "active" : index < this.setupStep ? "complete" : ""} aria-current=${index === this.setupStep ? "step" : nothing}><span>${label}</span></li>`)}</ol>
          </header>
          <div class="setup-guide-body">${stepBody}</div>
          <footer class="setup-guide-footer">
            <button class="ghost" @click=${() => this.closeSetupGuide()}>Skip for now</button>
            <div class="actions">
              ${this.setupStep > 0 ? html`<button @click=${() => this.setSetupStep(this.setupStep - 1)}>Back</button>` : nothing}
              ${this.setupStep < SETUP_GUIDE_LAST_STEP ? html`<button class="primary" @click=${() => this.setSetupStep(this.setupStep + 1)}>Continue</button>` : html`<button class="primary" @click=${() => this.completeSetupGuide()} ?disabled=${this.onboardingSaving}>${this.onboardingSaving ? "Saving…" : "Finish setup"}</button>`}
            </div>
          </footer>
        </section>
      </div>
    `;
  }

  renderConfigField(field) {
    const rawValue = getPathValue(this.config, field.valuePath, "");
    const value = field.sensitive ? "" : rawValue;
    const saved = this.restartKeys.has(field.key);
    const readOnly = Boolean(field.readOnly || field.ownership === "deployment");
    const onCommit = async (event) => {
      const target = event.currentTarget;
      const nextValue = field.type === "toggle" ? (target.checked ? "true" : "false") : target.value;
      try {
        await this.saveField(field, nextValue);
      } catch (error) {
        this.toast(error.message, "error");
      }
    };

    return html`
      <div class="config-field">
        <div class="field-heading">
          <label class="field-label" for=${field.key}>${field.label}</label>
          ${readOnly ? html`<span class="ownership-mark">Deployment owned</span>` : saved ? html`<span class="restart-mark">Restart needed</span>` : nothing}
        </div>
        ${field.type === "select" ? html`
          <select id=${field.key} .value=${String(value)} @change=${onCommit} ?disabled=${readOnly} aria-describedby=${field.helpText ? `${field.key}-help` : nothing}>
            ${asArray(field.options).map((option) => html`<option value=${option}>${option}</option>`)}
          </select>
        ` : field.type === "toggle" ? html`
          <label class="inline-check">
            <input id=${field.key} type="checkbox" .checked=${parseBoolValue(rawValue)} @change=${onCommit} ?disabled=${readOnly} aria-describedby=${field.helpText ? `${field.key}-help` : nothing}>
            <span>${parseBoolValue(rawValue) ? "Enabled" : "Disabled"}</span>
          </label>
        ` : html`
          <input
            id=${field.key}
            type=${field.type === "password" ? "password" : field.type === "number" ? "number" : field.type === "url" ? "url" : "text"}
            .value=${String(value)}
            min=${field.min ?? nothing}
            max=${field.max ?? nothing}
            placeholder=${field.sensitive ? display(rawValue, "Set value") : display(field.placeholder, "")}
            ?readonly=${readOnly}
            aria-readonly=${readOnly ? "true" : "false"}
            aria-describedby=${field.helpText ? `${field.key}-help` : nothing}
            @blur=${onCommit}>
        `}
        ${field.helpText ? html`<small id=${`${field.key}-help`} class="field-help">${field.helpText}</small>` : nothing}
      </div>
    `;
  }

  async exportEnv() {
    const blob = await API.exportEnv();
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = "allstarr.env";
    link.click();
    URL.revokeObjectURL(url);
  }

  resetEnvMigration() {
    this.envMigration = { state: "idle", sourceName: "", preview: null, result: null, error: "" };
    const fileInput = this.querySelector("#legacy-env-file");
    if (fileInput) fileInput.value = "";
  }

  async previewEnvMigration(source, sourceName) {
    const isBlob = source instanceof Blob;
    const text = isBlob ? "" : String(source ?? "");
    const byteLength = isBlob ? source.size : new TextEncoder().encode(text).length;
    if (byteLength === 0 || (!isBlob && !text.trim())) {
      this.envMigration = { ...this.envMigration, state: "error", error: "Choose a legacy .env file or paste its contents first." };
      return;
    }
    if (byteLength > 1024 * 1024) {
      this.envMigration = { ...this.envMigration, state: "error", error: "The legacy .env must be 1 MB or smaller." };
      return;
    }

    this.envMigration = { state: "previewing", sourceName, preview: null, result: null, error: "" };
    try {
      const preview = await API.previewEnvMigration(source, sourceName);
      this.envMigration = { state: "preview", sourceName, preview, result: null, error: "" };
    } catch (error) {
      this.envMigration = { state: "error", sourceName, preview: null, result: null, error: error.message };
    }
  }

  async selectEnvMigrationFile(event) {
    const file = event.currentTarget.files?.[0];
    if (!file) return;
    try {
      await this.previewEnvMigration(file, file.name);
    } catch (error) {
      this.envMigration = { state: "error", sourceName: file.name, preview: null, result: null, error: error.message };
    }
  }

  async previewPastedEnv(event) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    await this.previewEnvMigration(data.get("legacyEnv"), "pasted .env");
  }

  async applyEnvMigration(event) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    if (data.get("confirmMigration") !== "on") return;
    const preview = this.envMigration.preview || {};
    const previewToken = preview.previewToken || preview.PreviewToken || preview.previewId || preview.PreviewId || preview.token || preview.Token;
    const revision = preview.revision || preview.Revision;
    if (!previewToken || !revision) {
      this.envMigration = { ...this.envMigration, state: "error", error: "The migration preview expired or did not return a confirmation token. Preview the file again." };
      return;
    }

    this.envMigration = { ...this.envMigration, state: "applying", error: "" };
    try {
      const result = await API.applyEnvMigration(previewToken, revision);
      this.envMigration = { ...this.envMigration, state: "success", result, error: "" };
      await this.loadConfig();
      await this.loadEnvMigrationStatus();
      this.toast("Legacy settings migrated. Imported settings are active now.");
    } catch (error) {
      this.envMigration = { ...this.envMigration, state: "error", error: error.message };
    }
  }

  migrationCategories() {
    const preview = this.envMigration.preview || {};
    const categories = asArray(preview.categories || preview.Categories);
    if (categories.length) return categories;
    const rawPreviewItems = asArray(preview.items || preview.Items || preview.entries || preview.Entries || preview.changes || preview.Changes);
    const hasProviderSummaries = asArray(preview.providerAccounts || preview.ProviderAccounts).length > 0;
    const hasPlaylistHandoffs = asArray(preview.playlistHandoffs || preview.PlaylistHandoffs).length > 0;
    const providerSourceLines = new Map();
    for (const item of rawPreviewItems.filter((entry) =>
      String(entry.classification || entry.Classification) === "provider_account")) {
      const providerId = String(item.providerId || item.ProviderId || "");
      const line = item.sourceLine ?? item.SourceLine;
      if (providerId && line) providerSourceLines.set(providerId, [...(providerSourceLines.get(providerId) || []), line]);
    }
    const playlistSourceLine = rawPreviewItems.find((entry) =>
      String(entry.classification || entry.Classification) === "playlist_handoff")?.sourceLine ??
      rawPreviewItems.find((entry) => String(entry.classification || entry.Classification) === "playlist_handoff")?.SourceLine;
    const entries = rawPreviewItems
      .filter((entry) => {
        const classification = String(entry.classification || entry.Classification || "");
        return !(hasProviderSummaries && classification === "provider_account") &&
          !(hasPlaylistHandoffs && classification === "playlist_handoff");
      })
      .map((entry) => ({
        ...entry,
        category: entry.category || entry.Category || entry.classification || entry.Classification,
        destination: entry.destination || entry.Destination || entry.target || entry.Target ||
          entry.durableKey || entry.DurableKey || entry.providerId || entry.ProviderId,
        displayValue: entry.displayValue ?? entry.DisplayValue ?? entry.valuePreview ?? entry.ValuePreview,
        warning: entry.warning || entry.Warning || entry.reason || entry.Reason,
        sourceLine: entry.sourceLine ?? entry.SourceLine,
      }));
    for (const account of asArray(preview.providerAccounts || preview.ProviderAccounts)) {
      const providerId = account.providerId || account.ProviderId;
      entries.push({
        key: providerId,
        category: "disabled_shared_accounts",
        action: account.action || account.Action,
        destination: "Disabled encrypted provider account",
        displayValue: asArray(account.fields || account.Fields).join(", "),
        warning: account.reason || account.Reason,
        sourceLine: (providerSourceLines.get(String(providerId)) || []).join(", "),
      });
    }
    for (const playlist of asArray(preview.playlistHandoffs || preview.PlaylistHandoffs)) {
      const sourcePlaylistId = playlist.sourcePlaylistId || playlist.SourcePlaylistId;
      const syncSchedule = playlist.syncSchedule || playlist.SyncSchedule || "manual schedule";
      const localTracksPosition = playlist.localTracksPosition || playlist.LocalTracksPosition || "configured order";
      const hasLegacyOwner = Boolean(playlist.hasLegacyOwner ?? playlist.HasLegacyOwner);
      entries.push({
        key: playlist.name || playlist.Name,
        category: "playlist_ownership_handoffs",
        action: playlist.action || playlist.Action || "requires_target_selection",
        destination: playlist.jellyfinTargetPlaylistId || playlist.JellyfinTargetPlaylistId || "Choose a target and owner",
        displayValue: `${sourcePlaylistId} · ${syncSchedule} · local tracks ${localTracksPosition}`,
        warning: hasLegacyOwner
          ? "Map the legacy owner to a current user, then select the destination backend, library, and target playlist."
          : "Choose an owning user, destination backend, library, and target playlist before scheduling this playlist.",
        sourceLine: playlistSourceLine,
      });
    }
    for (const conflict of asArray(preview.conflicts || preview.Conflicts)) {
      entries.push({ key: "Conflict", category: "conflicts", action: "manual_review", warning: conflict });
    }
    if (!entries.length) return [];
    const grouped = new Map();
    for (const entry of entries) {
      const id = String(entry.category || entry.Category || "unknowns").trim().toLowerCase().replaceAll("-", "_");
      if (!grouped.has(id)) grouped.set(id, []);
      grouped.get(id).push(entry);
    }
    return [...grouped].map(([id, categoryEntries]) => ({ id, label: this.migrationCategoryLabel(id), entries: categoryEntries }));
  }

  migrationCategoryLabel(id) {
    return ({
      settings: "Imported durable settings",
      durable_settings: "Imported durable settings",
      imported_durable_settings: "Imported durable settings",
      durable_setting: "Imported durable settings",
      accounts: "Disabled shared accounts",
      shared_accounts: "Disabled shared accounts",
      disabled_shared_accounts: "Disabled shared accounts",
      deployment: "Deployment checklist",
      deployment_only: "Deployment checklist",
      deployment_checklist: "Deployment checklist",
      user_accounts: "Per-user reconnects",
      reconnects: "Per-user reconnects",
      per_user_reconnects: "Per-user reconnects",
      per_user_manual: "Per-user reconnects",
      conflicts: "Conflicts",
      unknown: "Unknown keys",
      unknowns: "Unknown keys",
      unsupported: "Unknown keys",
      playlists: "Playlist ownership handoffs",
      playlist_handoffs: "Playlist ownership handoffs",
      playlist_ownership_handoffs: "Playlist ownership handoffs",
    })[String(id)] || titleCase(id);
  }

  migrationEntryState(entry) {
    return String(entry.status || entry.Status || entry.action || entry.Action || "review").toLowerCase().replaceAll("-", "_");
  }

  migrationEntryStatusClass(entry) {
    const state = this.migrationEntryState(entry);
    if (["import", "imported", "import_if_absent", "ready", "durable"].includes(state)) return "configured";
    if (["skip", "skipped", "unknown", "unsupported"].includes(state)) return "disabled";
    return "warning";
  }

  migrationCategoryId(category) {
    return `migration-category-${String(category.id || category.Id || "settings").toLowerCase().replace(/[^a-z0-9_-]+/g, "-")}`;
  }

  migrationResultCount(value) {
    return Array.isArray(value) ? value.length : Number.isFinite(Number(value)) ? Number(value) : 0;
  }

  migrationChecklistText(item) {
    if (typeof item === "string") return item;
    return display(
      item.message || item.Message || item.warning || item.Warning || item.key || item.Key || item.name || item.Name,
      "Review this item in Settings.",
    );
  }

  migrationResultSections() {
    return this.migrationCategories().map((category) => {
      const categoryId = String(category.id || category.Id);
      let items = asArray(category.entries || category.Entries || category.items || category.Items);
      if (["settings", "durable_settings", "imported_durable_settings", "durable_setting"].includes(categoryId)) {
        items = items.filter((item) => this.migrationEntryState(item) === "import_if_absent");
      }
      return {
        id: categoryId.replace(/[^a-z0-9_-]+/gi, "-"),
        label: category.label || category.Label || this.migrationCategoryLabel(categoryId),
        items,
      };
    }).filter((section) => section.items.length);
  }

  migrationHasDeploymentChecklist() {
    return this.migrationResultSections().some((section) =>
      ["deployment", "deployment_only", "deployment_checklist"].includes(section.id));
  }

  migrationEntryIsSensitive(entry) {
    if (entry.sensitive ?? entry.Sensitive ?? entry.isSecret ?? entry.IsSecret) return true;
    const key = String(entry.key || entry.Key || entry.sourceKey || entry.SourceKey || "");
    return /(password|secret|token|cookie|api[_-]?key|\barl\b|credential)/i.test(key);
  }

  migrationEntryValue(entry) {
    if (this.migrationEntryIsSensitive(entry)) return "[redacted]";
    return display(entry.displayValue ?? entry.DisplayValue ?? entry.previewValue ?? entry.PreviewValue ??
      entry.redactedValue ?? entry.RedactedValue ?? entry.value ?? entry.Value);
  }

  renderEnvMigrationWizard() {
    const migration = this.envMigration;
    const durableStatus = this.envMigrationStatus || {};
    const migrationCompleted = Boolean(durableStatus.completed ?? durableStatus.Completed);
    const migrationAppliedAt = durableStatus.lastAppliedAt ?? durableStatus.LastAppliedAt;
    if (migrationCompleted && migration.state !== "success") {
      return html`
        <div class="panel env-migration" aria-labelledby="env-migration-title">
          <div class="env-migration-heading">
            <div>
              <h3 id="env-migration-title" tabindex="-1">Allstarr 2.x migration</h3>
              <p class="muted">This tenant already completed its legacy environment import. The import form stays hidden so it is not offered again on another browser or device.</p>
            </div>
            <span class="status-chip configured">Completed</span>
          </div>
          ${migrationAppliedAt ? html`<p class="muted">Applied ${formatDate(migrationAppliedAt)}</p>` : nothing}
        </div>
      `;
    }
    const preview = migration.preview || {};
    const warnings = asArray(preview.warnings || preview.Warnings);
    const categories = this.migrationCategories();
    const busy = migration.state === "previewing" || migration.state === "applying";
    const result = migration.result || {};
    const resultWarnings = asArray(result.warnings || result.Warnings);
    const resultSections = this.migrationResultSections();
    const hasDeploymentChecklist = this.migrationHasDeploymentChecklist();
    const canApply = preview.canApply ?? preview.CanApply ?? categories.length > 0;
    const summary = preview.summary || preview.Summary;
    const importedSettingCount = preview.importedSettingCount ?? preview.ImportedSettingCount ?? 0;
    const providerAccountCount = preview.providerAccountCount ?? preview.ProviderAccountCount ?? 0;
    const manualCount = preview.manualCount ?? preview.ManualCount ?? 0;
    const sourceSha256 = preview.sourceSha256 || preview.SourceSha256;
    const parserVersion = preview.parserVersion || preview.ParserVersion;

    return html`
      <div class="panel env-migration" aria-labelledby="env-migration-title" aria-busy=${busy ? "true" : "false"}>
        <div class="env-migration-heading">
          <div>
            <h3 id="env-migration-title" tabindex="-1">Migrate a legacy .env</h3>
            <p class="muted">Preview supported settings before importing them into the new configuration and encrypted account model. The original file is never displayed back to you.</p>
          </div>
          ${migration.state !== "idle" ? html`<button type="button" class="ghost" @click=${() => this.resetEnvMigration()} ?disabled=${busy}>Start over</button>` : nothing}
        </div>

        ${migration.state === "idle" || migration.state === "error" ? html`
          <div class="env-migration-source">
            <label class="config-field" for="legacy-env-file">
              <span>Choose a legacy .env file</span>
              <input id="legacy-env-file" type="file" @change=${(event) => this.selectEnvMigrationFile(event)}>
              <small>The picker shows all files because macOS and other systems may hide extensionless <code>.env</code> files. The preview still accepts only a valid <code>.env</code> filename.</small>
            </label>
            <span class="env-migration-or" aria-hidden="true">or</span>
            <form class="config-field" @submit=${(event) => this.previewPastedEnv(event)}>
              <label for="legacy-env-paste">Paste legacy .env contents</label>
              <textarea id="legacy-env-paste" name="legacyEnv" rows="8" autocomplete="off" autocapitalize="off" spellcheck="false" placeholder="BACKEND_TYPE=Jellyfin&#10;JELLYFIN_URL=http://…"></textarea>
              <button class="primary" type="submit">Preview migration</button>
            </form>
          </div>
        ` : nothing}

        ${migration.state === "previewing" ? html`
          <div class="env-migration-progress" role="status" aria-live="polite">
            <progress></progress><strong>Reading and validating ${display(migration.sourceName, "legacy .env")}…</strong>
          </div>
        ` : nothing}

        ${migration.state === "preview" || migration.state === "applying" ? html`
          <div class="env-migration-review">
            <div class="callout warning"><strong>Review before applying.</strong> Existing settings are not changed until you confirm. Secret values stay redacted in this preview.</div>
            ${summary ? html`<div class="callout"><strong>Preview summary</strong><p>${display(summary.message || summary.Message || summary)}</p></div>` : nothing}
            <dl class="env-migration-provenance" aria-label="Migration preview provenance">
              <div><dt>Source SHA-256</dt><dd class="mono">${display(sourceSha256)}</dd></div>
              <div><dt>Parser version</dt><dd>${display(parserVersion)}</dd></div>
            </dl>
            <dl class="env-migration-summary"><div><dt>Durable settings ready</dt><dd>${importedSettingCount}</dd></div><div><dt>Disabled accounts ready</dt><dd>${providerAccountCount}</dd></div><div><dt>Manual follow-ups</dt><dd>${manualCount}</dd></div></dl>
            ${warnings.length ? html`<div class="callout warning" role="alert"><strong>Warnings</strong><ul>${warnings.map((warning) => html`<li>${display(warning.message || warning.Message || warning)}</li>`)}</ul></div>` : nothing}
            ${categories.length ? categories.map((category) => {
              const entries = asArray(category.entries || category.Entries || category.items || category.Items);
              const categoryId = this.migrationCategoryId(category);
              return html`<section class="env-migration-category" aria-labelledby=${categoryId}>
                <h4 id=${categoryId}>${display(category.label || category.Label || category.name || category.Name, "Settings")}</h4>
                <div class="table-wrap"><table>
                  <thead><tr><th scope="col">Line</th><th scope="col">Legacy key</th><th scope="col">Destination</th><th scope="col">Value</th><th scope="col">Outcome</th></tr></thead>
                  <tbody>${entries.map((entry) => html`<tr>
                    <td class="mono">${display(entry.sourceLine ?? entry.SourceLine)}</td>
                    <td class="mono">${display(entry.key || entry.Key || entry.sourceKey || entry.SourceKey)}</td>
                    <td>${display(entry.destination || entry.Destination || entry.target || entry.Target)}</td>
                    <td class=${this.migrationEntryIsSensitive(entry) ? "migration-redacted" : ""}>${this.migrationEntryValue(entry)}</td>
                    <td><span class="status-chip ${this.migrationEntryStatusClass(entry)}">${titleCase(this.migrationEntryState(entry))}</span>${entry.warning || entry.Warning ? html`<div class="warning-text">${display(entry.warning || entry.Warning)}</div>` : nothing}</td>
                  </tr>`)}</tbody>
                </table></div>
              </section>`;
            }) : html`<div class="empty">No supported legacy settings were found. Nothing will be changed.</div>`}
            <div class="callout"><strong>What confirmation means</strong><p>Only rows marked for durable import are applied automatically. Disabled shared accounts remain disabled, users reconnect personal accounts themselves, deployment-only values stay on the host checklist, and playlists requiring a target or owner remain handoffs.</p></div>
            <form class="env-migration-confirm" @submit=${(event) => this.applyEnvMigration(event)}>
              <label class="inline-check">
                <input name="confirmMigration" type="checkbox" required ?disabled=${migration.state === "applying"}>
                <span>I reviewed this preview and authorize Allstarr to add the settings marked ready and create the listed shared provider accounts in a disabled state. Existing durable settings stay unchanged.</span>
              </label>
              <button class="primary" type="submit" ?disabled=${migration.state === "applying" || !canApply}>${migration.state === "applying" ? "Applying migration…" : "Apply migration"}</button>
            </form>
            ${migration.state === "applying" ? html`<div class="env-migration-progress" role="status" aria-live="polite"><progress></progress><strong>Applying the confirmed migration…</strong></div>` : nothing}
          </div>
        ` : nothing}

        ${migration.state === "success" ? html`
          <div class="callout success env-migration-result" role="status" aria-live="polite">
            <h4>Migration completed</h4>
            <p>${display(result.message || result.Message, "The confirmed durable values were imported. Review every checklist below before considering the upgrade finished.")}</p>
            <div class="callout">
              <strong>Restart status</strong>
              <p>Imported durable settings are active immediately. They do not require an Allstarr restart.</p>
              ${hasDeploymentChecklist ? html`<p>Deployment-owned values were not copied to the server. Review the deployment checklist, update Compose or the host <code>.env</code>, then recreate the Allstarr container to apply those separate changes.</p>` : html`<p>No container restart is required for this migration.</p>`}
            </div>
            <dl><div><dt>Durable settings</dt><dd>${this.migrationResultCount(result.settingsImported ?? result.SettingsImported ?? result.importedSettings ?? result.ImportedSettings)}</dd></div><div><dt>Disabled accounts created</dt><dd>${this.migrationResultCount(result.providerAccountsCreated ?? result.ProviderAccountsCreated)}</dd></div><div><dt>Skipped</dt><dd>${this.migrationResultCount(result.settingsSkipped ?? result.SettingsSkipped) + this.migrationResultCount(result.providerAccountsSkipped ?? result.ProviderAccountsSkipped)}</dd></div><div><dt>Manual checklist</dt><dd>${this.migrationResultCount(result.manualChecklistItems ?? result.ManualChecklistItems)}</dd></div><div><dt>Playlist handoffs</dt><dd>${this.migrationResultCount(result.playlistHandoffsPending ?? result.PlaylistHandoffsPending)}</dd></div></dl>
            ${resultSections.map((section) => html`<section class="env-migration-result-section" aria-labelledby=${`migration-result-${section.id}`}>
              <h5 id=${`migration-result-${section.id}`}>${section.label}</h5>
              <ul>${section.items.map((item) => html`<li>${this.migrationChecklistText(item)}</li>`)}</ul>
            </section>`)}
            ${resultWarnings.length ? html`<ul>${resultWarnings.map((warning) => html`<li>${display(warning.message || warning.Message || warning)}</li>`)}</ul>` : nothing}
          </div>
        ` : nothing}

        ${migration.state === "error" ? html`<div class="callout error" role="alert" tabindex="-1"><strong>Migration could not continue.</strong> ${display(migration.error)}</div>` : nothing}
      </div>
    `;
  }

  renderRestartBar() {
    if (!this.restartKeys.size) {
      return nothing;
    }
    return html`
      <div class="restart-bar">
        <span>${this.restartKeys.size} saved change${this.restartKeys.size === 1 ? "" : "s"} need restart</span>
        <button class="primary" @click=${async () => { await API.restart(); this.restartKeys = new Set(); this.toast("Restart requested"); }}>Restart</button>
        <button class="ghost" @click=${() => { this.restartKeys = new Set(); }}>Dismiss</button>
      </div>
    `;
  }

  renderNowPlaying() {
    const current = this.activity.find((item) => item.isPlaying || item.IsPlaying) || this.activity[0];
    const progress = current ? percent(current.playbackProgress ?? current.PlaybackProgress ?? current.progress ?? current.Progress) : 0;
    const title = current ? display(current.title || current.Title, "Active download") : "No active playback";
    const artist = current ? display(current.artist || current.Artist) : "Queue is idle";
    const coverArtUrl = current?.coverArtUrl || current?.CoverArtUrl || "/placeholder.png";
    return html`
      <footer class="now-playing">
        <div class="now-track">
          <img class="art" src=${coverArtUrl} alt="">
          <div>
            <div class="now-title">${title}</div>
            <div class="now-meta">${artist}</div>
          </div>
        </div>
        <div class="progress" style=${`--progress:${progress}%`}><span></span></div>
      </footer>
    `;
  }

  renderToasts() {
    return html`
      <div class="toast-stack">
        ${this.toasts.map((toast) => html`<div class="toast ${toast.type}">${toast.message}</div>`)}
      </div>
    `;
  }
}

customElements.define("allstarr-app", AllstarrApp);
