import { LitElement, html, nothing } from "/js/lit-3.3.3.js";
import { icon } from "/js/ui/icons.js";

const THEME_KEY = "allstarr-theme";
const DEFAULT_ROUTE = "/home";
const SETUP_GUIDE_DISMISSED_KEY = "allstarr-setup-guide-dismissed";
const SETUP_GUIDE_STEP_KEY = "allstarr-setup-guide-step";
const SETUP_GUIDE_LAST_STEP = 4;
const REDACTION_MODE_KEY = "allstarr-sharing-redaction";
const SIDEBAR_COLLAPSED_KEY = "allstarr-sidebar-collapsed";
const ACCOUNT_MANAGED_PROVIDERS = new Set(["spotify", "deezer", "qobuz", "lastfm", "listenbrainz", "apple-musickit"]);

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

function formatRelativeTime(value) {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "—";
  const seconds = Math.round((Date.now() - date.getTime()) / 1000);
  if (Math.abs(seconds) < 60) return "just now";
  const minutes = Math.round(seconds / 60);
  if (Math.abs(minutes) < 60) return `${Math.abs(minutes)}m ${minutes < 0 ? "from now" : "ago"}`;
  const hours = Math.round(minutes / 60);
  if (Math.abs(hours) < 24) return `${Math.abs(hours)}h ${hours < 0 ? "from now" : "ago"}`;
  const days = Math.round(hours / 24);
  return `${Math.abs(days)}d ${days < 0 ? "from now" : "ago"}`;
}

function percent(value) {
  const numeric = Number(value);
  if (!Number.isFinite(numeric)) {
    return 0;
  }
  return Math.max(0, Math.min(100, numeric * (numeric <= 1 ? 100 : 1)));
}

function formatDuration(value) {
  const seconds = Math.max(0, Math.floor(Number(value) || 0));
  const minutes = Math.floor(seconds / 60);
  return `${minutes}:${String(seconds % 60).padStart(2, "0")}`;
}

function jobCopy(type, state, failure) {
  const normalized = String(type || "").toLowerCase();
  const known = {
    "playback.signal.process": ["Process playback activity", "Updates listening history and other enabled playback actions."],
    "playlist.link.run": ["Synchronize linked playlist", "Reads the source playlist, resolves tracks, and updates its target."],
    "library.action.execute": ["Apply library action", "Runs an explicitly enabled favorite or library workflow."],
    "recommendation.generate": ["Build recommendations", "Generates a provider-neutral recommendation set."],
  };
  const [label, description] = known[normalized] || [titleCase(String(type || "Background job").replaceAll(".", " ")), "Durable background work managed by Allstarr."];
  if (String(state).toLowerCase() !== "failed") return { label, description, explanation: failure || "No failure." };
  const attempts = normalized === "playback.signal.process"
    ? "The playback update exhausted its retries. Check the scrobbling account tests above; future playback events will continue normally."
    : "Allstarr exhausted the retry budget. Open the related source or playlist, correct the reported problem, then run it again.";
  return { label, description, explanation: failure ? `${failure} ${attempts}` : attempts };
}

function formatSchedule(value) {
  const parts = String(value || "").trim().split(/\s+/);
  if (parts.length !== 5) return display(value, "Manual");
  const [minute, hour, day, month, weekday] = parts;
  if (/^\d+$/.test(minute) && /^\d+$/.test(hour) && day === "*" && month === "*") {
    const time = `${String(Number(hour)).padStart(2, "0")}:${String(Number(minute)).padStart(2, "0")}`;
    return weekday === "*" ? `Every day · ${time}` : `Weekly · ${time}`;
  }
  return value;
}

function providerAccountDisplayName(value, providerName = "") {
  const provider = String(providerName || "").trim();
  const name = String(value || "").trim().replace(/^shared\s+/i, "");
  return name || (provider ? `${provider} account` : "Not connected");
}

function configOptionLabel(field, option) {
  const key = String(field?.key || "").toUpperCase();
  const labels = {
    APPLE_DOWNLOAD_QUALITY: {
      "alac-16-44": "Standard lossless · 16-bit / 44.1 kHz",
      "alac-24-48": "Enhanced lossless · 24-bit / 48 kHz",
      "alac-24-96": "High-resolution · 24-bit / 96 kHz (one below maximum)",
      "alac-24-192": "Maximum · 24-bit / 192 kHz",
    },
    DEEZER_QUALITY: {
      MP3_128: "Data saver · MP3 128 kbps",
      MP3_320: "High · MP3 320 kbps",
      FLAC: "Lossless · FLAC (provider maximum)",
    },
    QOBUZ_QUALITY: {
      MP3_320: "High · MP3 320 kbps",
      FLAC: "Lossless · CD quality",
      HI_RES: "High-resolution · up to provider maximum",
    },
  };
  return labels[key]?.[option] || option;
}

function asArray(value) {
  return Array.isArray(value) ? value : [];
}

function pageHeader(title, subtitle, actions = nothing) {
  return html`<header class="view-header">
    <div><h2>${title}</h2><p>${subtitle}</p></div>
    ${actions === nothing ? nothing : html`<div class="actions">${actions}</div>`}
  </header>`;
}

function sectionHeader(title, subtitle = "", trailing = nothing) {
  return html`<header class="section-heading">
    <div><h3>${title}</h3>${subtitle ? html`<p>${subtitle}</p>` : nothing}</div>
    ${trailing}
  </header>`;
}

function emptyState(message) {
  return html`<div class="empty"><span>${message}</span></div>`;
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
    "apple-musickit": "Apple Music",
    "apple-download": "Apple download",
    deezer: "Deezer",
    qobuz: "Qobuz",
    squidwtf: "SquidWTF",
    musicbrainz: "MusicBrainz",
    lyricsplus: "Lyrics+",
    lrclib: "LRCLib",
    jellyfin: "Jellyfin",
    extensions: "Extensions",
  };
  return marks[id] || titleCase(provider?.name || provider?.Name || id);
}

function providerLogoUrl(provider) {
  const supplied = provider?.logoUrl || provider?.LogoUrl || provider?.branding?.logoReference || provider?.Branding?.LogoReference;
  if (supplied) return String(supplied);
  const id = String(provider?.id || provider?.Id || provider?.name || provider?.Name || "").toLowerCase();
  const logoAliases = {
    squidwtf: "squidwtf",
    "apple-download": "applemusic",
    "apple-musickit": "applemusic",
    "spotiflac-amazon": "amazonmusic",
    amazon: "amazonmusic",
    "amazon-music": "amazonmusic",
    "spotiflac-deezer": "deezer",
    "spotiflac-qobuz-web": "qobuz",
    "spotiflac-soundcloud": "soundcloud",
    "spotiflac-tidal-web": "tidal",
    tidal: "tidal",
    "spotiflac-ytmusic-spotiflac": "youtubemusic",
    "youtube-music": "youtubemusic",
    youtube_music: "youtubemusic",
    "last.fm": "lastfm",
    "listen-brainz": "listenbrainz",
  };
  const logos = new Set(["spotify", "applemusic", "amazonmusic", "deezer", "qobuz", "musicbrainz", "jellyfin", "soundcloud", "tidal", "youtubemusic", "lastfm", "listenbrainz", "squidwtf"]);
  const nameId = String(provider?.name || provider?.Name || "").toLowerCase().replace(/[^a-z0-9]/g, "");
  const logoId = logoAliases[id] || (logos.has(id) ? id : logoAliases[nameId] || nameId);
  return logos.has(logoId) ? `/images/providers/${logoId}.svg` : "";
}

const providersWithoutCardMark = new Set(["lyricsplus", "lrclib"]);

function providerDisplayName(providerId, providers = []) {
  const provider = asArray(providers).find((item) =>
    String(item?.id || item?.Id || "").toLowerCase() === String(providerId).toLowerCase());
  return provider?.name || provider?.Name || providerMark({ id: providerId });
}

function compareExtensionVersions(left, right) {
  const parts = (value) => String(value || "0").replace(/^v/i, "").split(/[.+-]/).map((part) => /^\d+$/.test(part) ? Number(part) : part.toLowerCase());
  const a = parts(left);
  const b = parts(right);
  for (let index = 0; index < Math.max(a.length, b.length); index++) {
    const av = a[index] ?? 0;
    const bv = b[index] ?? 0;
    if (av === bv) continue;
    if (typeof av === "number" && typeof bv === "number") return av - bv;
    if (typeof av === "number") return 1;
    if (typeof bv === "number") return -1;
    return String(av).localeCompare(String(bv));
  }
  return 0;
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
  providerSummaries: () => requestJson("/api/admin/ui/provider-summaries", { cache: "no-store" }, "Failed to load provider summaries"),
  dashboardActivity: (limit = 20, before = "", beforeId = "") => {
    const params = new URLSearchParams({ limit: String(limit) });
    if (before) params.set("before", before);
    if (beforeId) params.set("beforeId", beforeId);
    return requestJson(`/api/admin/ui/activity?${params}`, { cache: "no-store" }, "Failed to load dashboard activity");
  },
  status: () => requestJson("/api/admin/status", { cache: "no-store" }, "Failed to load status"),
  mediaProbe: () => requestJson("/api/admin/media-probe", { cache: "no-store" }, "Media pipeline test failed"),
  playlistReadiness: () => requestJson("/api/admin/playlist-readiness", { cache: "no-store" }, "Playlist readiness test failed"),
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
  trackMappingDetails: (spotifyId, backendItemId = "") => {
    const params = new URLSearchParams();
    if (backendItemId) params.set("backendItemId", backendItemId);
    const query = params.size ? `?${params}` : "";
    return requestJson(`/api/admin/track-matches/spotify/${encodeURIComponent(spotifyId)}${query}`, {}, "Failed to load track mapping history");
  },
  searchLocalTracks: (query) =>
    requestJson(`/api/admin/jellyfin/search?query=${encodeURIComponent(query)}`, {}, "Failed to search the local library"),
  searchExternalTracks: (query, provider, limit = 20) => {
    const params = new URLSearchParams({ query, provider, limit: String(limit) });
    return requestJson(`/api/admin/external/search?${params}`, {}, "Failed to search the provider");
  },
  saveInjectedTrackMapping: (name, payload) =>
    requestJson(`/api/admin/playlists/${encodeURIComponent(name)}/map`, jsonBody(payload), "Failed to save the track match"),
  clearInjectedTrackMapping: (name, spotifyId) => {
    const params = new URLSearchParams({ playlist: name, spotifyId });
    return requestJson(`/api/admin/mappings/tracks?${params}`, { method: "DELETE" }, "Failed to clear the track match");
  },
  refreshPlaylists: () => requestJson("/api/admin/playlists/refresh", { method: "POST" }, "Failed to refresh playlists"),
  refreshPlaylist: (name) =>
    requestJson(`/api/admin/playlists/${encodeURIComponent(name)}/refresh`, { method: "POST" }, "Failed to refresh playlist"),
  matchPlaylist: (name) =>
    requestJson(`/api/admin/playlists/${encodeURIComponent(name)}/match`, { method: "POST" }, "Failed to match playlist"),
  matchAllPlaylists: () =>
    requestJson("/api/admin/playlists/match-all", { method: "POST" }, "Failed to match playlists"),
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
  playlistSources: () =>
    requestJson("/api/admin/playlist-sources", { cache: "no-store" }, "Failed to load playlist sources"),
  sourcePlaylists: (accountId, query = "", cursor = "") => {
    const params = new URLSearchParams({ limit: "30" });
    if (query) params.set("query", query);
    if (cursor) params.set("cursor", cursor);
    return requestJson(`/api/admin/playlist-sources/${encodeURIComponent(accountId)}/playlists?${params}`, { cache: "no-store" }, "Failed to browse source playlists");
  },
  mediaTargets: () =>
    requestJson("/api/admin/media-targets", { cache: "no-store" }, "Failed to load media targets"),
  targetPlaylists: (targetId, query = "", cursor = "") => {
    const params = new URLSearchParams({ limit: "30" });
    if (query) params.set("query", query);
    if (cursor) params.set("cursor", cursor);
    return requestJson(`/api/admin/media-targets/${encodeURIComponent(targetId)}/playlists?${params}`, { cache: "no-store" }, "Failed to browse target playlists");
  },
  createPlaylistLink: (payload) =>
    requestJson("/api/admin/playlist-links", jsonBody(payload), "Failed to create playlist link"),
  updatePlaylistLink: (id, payload) =>
    requestJson(`/api/admin/playlist-links/${encodeURIComponent(id)}`, jsonBody(payload, "PUT"), "Failed to update playlist link"),
  deletePlaylistLink: (id, expectedRevision) =>
    requestJson(`/api/admin/playlist-links/${encodeURIComponent(id)}`, jsonBody({ expectedRevision }, "DELETE"), "Failed to remove playlist"),
  setPlaylistLinkEnabled: (id, expectedRevision, enabled) =>
    requestJson(`/api/admin/playlist-links/${encodeURIComponent(id)}/state`, jsonBody({ expectedRevision, enabled }, "PATCH"), "Failed to update playlist state"),
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
  jobs: () => requestJson("/api/admin/jobs?limit=100", {}, "Failed to load background jobs"),
  cancelJob: (id) =>
    requestJson(`/api/admin/jobs/${encodeURIComponent(id)}/cancel`, { method: "POST" }, "Failed to cancel job"),
  providerAccounts: () =>
    requestJson("/api/admin/provider-accounts", {}, "Failed to load provider accounts"),
  ctsMeasurements: () =>
    requestJson("/api/admin/provider-diagnostics/deep-stream/latest", { cache: "no-store" }, "Failed to load click-to-stream measurements"),
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
  testProviderAccount: (accountId, provider) =>
    requestJson(
      `/api/admin/providers/test/${encodeURIComponent(provider)}?accountId=${encodeURIComponent(accountId)}`,
      { method: "POST" },
      "Failed to test provider account",
    ),
  testProviderCapability: (provider, capability) =>
    requestJson(
      `/api/admin/providers/test/${encodeURIComponent(provider)}/${encodeURIComponent(capability)}`,
      { method: "POST" },
      "Failed to test provider capability",
    ),
  createProviderAccount: (payload) =>
    requestJson("/api/admin/provider-accounts", jsonBody(payload), "Failed to create provider account"),
  authenticateLastFmAccount: (payload) =>
    requestJson("/api/admin/scrobbling/lastfm/authenticate", jsonBody(payload), "Failed to connect Last.fm"),
  replaceProviderAccountSecret: (id, secret) =>
    requestJson(`/api/admin/provider-accounts/${encodeURIComponent(id)}/secret`, jsonBody({ secret }, "PUT"), "Failed to replace provider credential"),
  setProviderAccountEnabled: (id, enabled, expectedRevision) =>
    requestJson(`/api/admin/provider-accounts/${encodeURIComponent(id)}`, jsonBody({ enabled, expectedRevision }, "PATCH"), "Failed to update provider account"),
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
  legacyMappings: () =>
    requestJson("/api/admin/mappings/tracks", { cache: "no-store" }, "Failed to load imported legacy mappings"),
  saveMapping: (externalSnapshotId, payload) =>
    requestJson(`/api/admin/playlist-links/matches/${encodeURIComponent(externalSnapshotId)}/override`, jsonBody(payload), "Failed to save match review"),
  deleteMapping: (overrideId, expectedRevision = 0) =>
    requestJson(`/api/admin/playlist-links/matches/overrides/${encodeURIComponent(overrideId)}?expectedRevision=${encodeURIComponent(expectedRevision)}`, { method: "DELETE" }, "Failed to clear match review"),
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
  uninstallExtensionPackage: (packageId, expectedRevision, retainProviderAccounts = true) =>
    requestJson(`/api/admin/extensions/packages/${encodeURIComponent(packageId)}`, jsonBody({ expectedRevision, retainProviderAccounts }, "DELETE"), "Failed to uninstall extension package"),
  extensionLogs: (packageId = "", limit = 100) => {
    const query = new URLSearchParams({ limit: String(limit) });
    if (packageId) query.set("packageId", packageId);
    return requestJson(`/api/admin/extensions/logs?${query}`, { cache: "no-store" }, "Failed to load extension logs");
  },
  extensionSession: (packageId) =>
    requestJson(`/api/admin/extensions/packages/${encodeURIComponent(packageId)}/session`, { cache: "no-store" }, "Failed to load extension session"),
  startExtensionSession: (packageId) =>
    requestJson(`/api/admin/extensions/packages/${encodeURIComponent(packageId)}/session/start`, { method: "POST" }, "Failed to start extension authorization"),
  completeExtensionSession: (packageId, grant) =>
    requestJson(`/api/admin/extensions/packages/${encodeURIComponent(packageId)}/session/grant`, jsonBody({ grant }), "Failed to complete extension authorization"),
  clearExtensionSession: (packageId) =>
    requestJson(`/api/admin/extensions/packages/${encodeURIComponent(packageId)}/session`, { method: "DELETE" }, "Failed to clear extension authorization"),
  installExtension: (item) =>
    requestJson("/api/admin/extensions/install", jsonBody({ id: item.id || item.Id, downloadUrl: item.downloadUrl || item.DownloadUrl || "", sha256: item.sha256 || item.Sha256 || "", registryId: item.registryId || item.RegistryId || null }), "Failed to install extension"),
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
  appleMusicSetup: (file) => {
    const data = new FormData();
    data.append("file", file, file.name);
    return requestJson("/api/admin/apple-download/setup", { method: "POST", body: data }, "Failed to stage Apple Music package");
  },
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
    sidebarCollapsed: { type: Boolean },
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
    playlistSources: { state: true },
    mediaTargets: { state: true },
    sourcePlaylistResults: { state: true },
    targetPlaylistResults: { state: true },
    playlistWizard: { state: true },
    playlistLinkPreview: { state: true },
    selectedPlaylistLinkId: { state: true },
    editingPlaylistLink: { state: true },
    selectedInjectedPlaylist: { state: true },
    injectedPlaylistDetails: { state: true },
    injectedTrackMenuId: { state: true },
    injectedTrackEditor: { state: true },
    selectedTrackDetails: { state: true },
    selectedTrackContext: { state: true },
    trackDetailsLoading: { state: true },
    downloads: { state: true },
    jobs: { state: true },
    providerAccounts: { state: true },
    providerHealth: { state: true },
    providerSummaries: { state: true },
    dashboardActivity: { state: true },
    eventLogCursor: { state: true },
    eventLogCursorId: { state: true },
    eventLogHasMore: { state: true },
    eventLogLoading: { state: true },
    eventLogTime: { state: true },
    eventLogSeverity: { state: true },
    eventLogProvider: { state: true },
    eventLogPlaylist: { state: true },
    eventLogCorrelation: { state: true },
    providerTests: { state: true },
    providerTestResults: { state: true },
    ctsMeasurements: { state: true },
    endpointUsage: { state: true },
    mappings: { state: true },
    legacyMappings: { state: true },
    extensionStore: { state: true },
    extensionRegistries: { state: true },
    extensionPackages: { state: true },
    extensionPermissions: { state: true },
    extensionPermissionPackageId: { state: true },
    extensionPermissionConfirmed: { state: true },
    extensionLogs: { state: true },
    selectedExtensionPackageId: { state: true },
    extensionSession: { state: true },
    extensionViewTab: { state: true },
    extensionInstallOpen: { state: true },
    extensionInstallTab: { state: true },
    extensionSearch: { state: true },
    scrobbling: { state: true },
    appleMusicStatus: { state: true },
    serviceResults: { state: true },
    extensionActions: { state: true },
    extensionRegistryError: { state: true },
    providerConfigOpen: { state: true },
    providerAccountConfigOpen: { state: true },
    providerAccountModalOpen: { state: true },
    newProviderAccountId: { state: true },
    nowPlayingClock: { state: true },
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
    redactionMode: { state: true },
    globalSearchOpen: { state: true },
    globalSearchQuery: { state: true },
    selectedProviderId: { state: true },
    sourceCatalogOpen: { state: true },
    injectedSearch: { state: true },
    injectedStatusFilter: { state: true },
    injectedScheduleFilter: { state: true },
    injectedPage: { state: true },
    injectedPageSize: { state: true },
    injectedTrackFilter: { state: true },
    injectedAddOpen: { state: true },
    selectedInjectedPlaylists: { state: true },
  };

  constructor() {
    super();
    this.authenticated = false;
    this.loading = true;
    this.route = normalizeRoute();
    this.navOpen = false;
    this.sidebarCollapsed = localStorage.getItem(SIDEBAR_COLLAPSED_KEY) === "1";
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
    this.playlistSources = [];
    this.playlistSourceBlockedAccounts = [];
    this.playlistSourceProviders = [];
    this.mediaTargets = [];
    this.sourcePlaylistResults = [];
    this.targetPlaylistResults = [];
    this.playlistWizard = this.newPlaylistWizardDraft();
    this.playlistLinkPreview = null;
    this.selectedPlaylistLinkId = "";
    this.editingPlaylistLink = null;
    this.selectedInjectedPlaylist = "";
    this.injectedPlaylistDetails = null;
    this.injectedTrackMenuId = "";
    this.injectedTrackEditor = null;
    this.selectedTrackDetails = null;
    this.selectedTrackContext = null;
    this.trackDetailsLoading = false;
    this.downloads = null;
    this.jobs = [];
    this.providerAccounts = [];
    this.providerHealth = [];
    this.providerSummaries = [];
    this.dashboardActivity = [];
    this.eventLogCursor = "";
    this.eventLogCursorId = "";
    this.eventLogHasMore = false;
    this.eventLogLoading = false;
    this.eventLogTime = "all";
    this.eventLogSeverity = "all";
    this.eventLogProvider = "all";
    this.eventLogPlaylist = "all";
    this.eventLogCorrelation = "";
    this.eventLogCategory = "all";
    this.eventLogSource = "all";
    this.eventLogState = "all";
    this.eventLogQuery = "";
    this.providerTests = new Set();
    this.providerTestResults = new Map();
    this.ctsMeasurements = [];
    this.endpointUsage = null;
    this.mappings = null;
    this.legacyMappings = null;
    this.extensionStore = null;
    this.extensionRegistries = [];
    this.extensionPackages = [];
    this.extensionPermissions = new Map();
    this.extensionPermissionPackageId = "";
    this.extensionPermissionConfirmed = false;
    this.extensionLogs = [];
    this.selectedExtensionPackageId = "";
    this.extensionSession = null;
    this.extensionViewTab = "installed";
    this.extensionInstallOpen = false;
    this.extensionInstallTab = "registry";
    this.extensionSearch = "";
    this.scrobbling = null;
    this.appleMusicStatus = null;
    this.appleUpload = null;
    this.serviceResults = {};
    this.extensionActions = {};
    this.extensionRegistryError = "";
    this.providerConfigOpen = new Set();
    this.providerAccountConfigOpen = new Set();
    this.providerAccountModalOpen = false;
    this.newProviderAccountId = "spotify";
    this.nowPlayingClock = Date.now();
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
    this.redactionPreferenceSet = localStorage.getItem(REDACTION_MODE_KEY) !== null;
    this.redactionMode = localStorage.getItem(REDACTION_MODE_KEY) === "1";
    this.globalSearchOpen = false;
    this.globalSearchQuery = "";
    this.selectedProviderId = "";
    this.sourceCatalogOpen = false;
    this.injectedSearch = "";
    this.injectedStatusFilter = "";
    this.injectedScheduleFilter = "";
    this.injectedPage = 1;
    this.injectedPageSize = 10;
    this.injectedTrackFilter = "";
    this.injectedAddOpen = false;
    this.selectedInjectedPlaylists = new Set();
    this.playlistLinkFilters = { libraryScopeId: "" };
    this.mappingFilters = { page: 1, pageSize: 50, state: "", libraryScopeId: "", search: "" };
    this.activitySource = null;
    this.routeLoadKey = "";
    this.envMigrationExpiryTimer = null;
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
      const content = this.renderRoot.querySelector("main.content");
      if (content) content.scrollTop = 0;
      this.loadFailures = {};
      this.loadForRoute();
    };
    window.addEventListener("hashchange", this.onHashChange);
    this.nowPlayingTimer = window.setInterval(() => { this.nowPlayingClock = Date.now(); }, 500);
    this.bootstrap();
  }

  disconnectedCallback() {
    window.removeEventListener("hashchange", this.onHashChange);
    clearInterval(this.nowPlayingTimer);
    this.stopActivityStream();
    clearTimeout(this.envMigrationExpiryTimer);
    super.disconnectedCallback();
  }

  updated() {
    if (this.providerAccountModalOpen) {
      const dialog = this.querySelector(".provider-account-dialog");
      if (dialog && !dialog.contains(document.activeElement)) {
        dialog.querySelector("[autofocus]")?.focus();
      }
      return;
    }
    const activeDialog = this.querySelector(
      ".provider-detail-dialog, .source-catalog-dialog, .compact-dialog, .injected-playlist-dialog, .extension-manage-dialog, .extension-permission-dialog, .extension-install-dialog",
    );
    if (activeDialog && !activeDialog.contains(document.activeElement)) {
      (activeDialog.querySelector("[autofocus]") || activeDialog).focus();
      return;
    }
    if (!this.shouldShowSetupGuide()) return;
    const dialog = this.querySelector(".setup-guide");
    if (dialog && !dialog.contains(document.activeElement)) {
      dialog.querySelector("[autofocus]")?.focus();
    }
  }

  handleDialogKeydown(event, close) {
    if (event.key === "Escape") {
      event.preventDefault();
      close();
      return;
    }
    if (event.key !== "Tab") return;
    const dialog = event.currentTarget.querySelector?.('[role="dialog"]');
    if (!dialog) return;
    const focusable = [...dialog.querySelectorAll('button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [href], [tabindex]:not([tabindex="-1"])')];
    if (!focusable.length) {
      event.preventDefault();
      dialog.focus();
      return;
    }
    const first = focusable[0];
    const last = focusable.at(-1);
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
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
      if (!this.redactionPreferenceSet) {
        this.redactionMode = Boolean(this.config?.admin?.redactSensitiveValues ?? this.config?.Admin?.RedactSensitiveValues);
      }
      this.clearLoadFailure("config");
    } catch (error) {
      this.recordLoadFailure("config", "Configuration", error);
      throw error;
    }
  }

  toggleRedactionMode() {
    this.redactionMode = !this.redactionMode;
    this.redactionPreferenceSet = true;
    localStorage.setItem(REDACTION_MODE_KEY, this.redactionMode ? "1" : "0");
    this.toast(this.redactionMode ? "Sharing redaction enabled" : "Sharing redaction disabled");
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

  runMediaProbe = async () => {
    this.serviceResults = {
      ...this.serviceResults,
      media: { state: "running", message: "Testing library metadata and album artwork..." },
    };
    try {
      const result = await API.mediaProbe();
      const artwork = result?.artwork || result?.Artwork;
      const playerArtwork = result?.playerArtwork || result?.PlayerArtwork;
      const playerStreaming = result?.playerStreaming || result?.PlayerStreaming;
      const success = Boolean(result?.success ?? result?.Success);
      const message = result?.message || result?.Message ||
        (success ? "The media pipeline is healthy." : "The media pipeline needs attention.");
      const playerTested = Boolean(playerArtwork?.tested ?? playerArtwork?.Tested);
      const streamTested = Boolean(playerStreaming?.tested ?? playerStreaming?.Tested);
      const details = success && artwork
        ? `${display(artwork.contentType || artwork.ContentType, "image")} · ${Number(artwork.bytes || artwork.Bytes || 0).toLocaleString()} artwork bytes${playerTested ? " · player artwork passed" : ""}${streamTested ? ` · audio stream passed (${Number(playerStreaming.bytes || playerStreaming.Bytes || 0).toLocaleString()} bytes)` : ""}`
        : "";
      this.serviceResults = {
        ...this.serviceResults,
        media: { state: success ? "success" : "warning", message, details },
      };
    } catch (error) {
      this.serviceResults = {
        ...this.serviceResults,
        media: { state: "error", message: error.message },
      };
    }
  };

  runPlaylistReadinessProbe = async () => {
    this.serviceResults = {
      ...this.serviceResults,
      playlists: { state: "running", message: "Checking restored playlists and playable cache entries..." },
    };
    try {
      const result = await API.playlistReadiness();
      const success = Boolean(result?.success ?? result?.Success);
      const configured = Number(result?.configuredPlaylists ?? result?.ConfiguredPlaylists ?? 0);
      const sources = Number(result?.sourcePlaylists ?? result?.SourcePlaylists ?? 0);
      const rendered = Number(result?.renderedPlaylists ?? result?.RenderedPlaylists ?? 0);
      const playable = Number(result?.playableItems ?? result?.PlayableItems ?? 0);
      const unavailable = Number(result?.unavailableItems ?? result?.UnavailableItems ?? 0);
      this.serviceResults = {
        ...this.serviceResults,
        playlists: {
          state: success ? "success" : "warning",
          message: result?.message || result?.Message || "Playlist readiness checked.",
          details: `${configured} configured · ${sources} with source data · ${rendered} visible · ${playable} playable · ${unavailable} unavailable`,
        },
      };
    } catch (error) {
      this.serviceResults = {
        ...this.serviceResults,
        playlists: { state: "error", message: error.message },
      };
    }
  };

  runCoreReadiness = async () => {
    this.serviceResults = {
      ...this.serviceResults,
      readiness: { state: "running", message: "Checking player artwork, playlists, and provider health..." },
    };
    const [mediaOutcome, playlistOutcome, providerOutcome] = await Promise.allSettled([
      API.mediaProbe(),
      API.playlistReadiness(),
      this.loadProviderAccounts(),
    ]);
    const media = mediaOutcome.status === "fulfilled" ? mediaOutcome.value : null;
    const playlists = playlistOutcome.status === "fulfilled" ? playlistOutcome.value : null;
    const mediaReady = Boolean(media?.success ?? media?.Success);
    const playlistsReady = Boolean(playlists?.success ?? playlists?.Success);
    const affectedPlaylists = asArray(playlists?.affectedPlaylists ?? playlists?.AffectedPlaylists);
    const failedRequests = [mediaOutcome, playlistOutcome, providerOutcome].filter((item) => item.status === "rejected");
    const playlistSources = this.providerHealth.filter((item) =>
      String(item.capability || item.Capability || "").toLowerCase() === "playlist");
    const playlistSourceReady = playlistSources.some((item) =>
      String(item.health || item.Health || "unknown").toLowerCase() === "healthy");
    const state = !failedRequests.length && mediaReady && playlistsReady && playlistSourceReady ? "success" : "warning";
    this.serviceResults = {
      ...this.serviceResults,
      readiness: {
        state,
        message: state === "success"
          ? "All core music paths are ready."
          : "Your playable library is available, but one or more refresh or connection checks need attention.",
        mediaReady,
        playlistsReady,
        playlistSourceReady,
        failedRequests: failedRequests.length,
        playlistCode: playlists?.code || playlists?.Code || "",
        playlistMessage: playlists?.message || playlists?.Message || "Playlist check unavailable.",
        unavailableItems: Number(playlists?.unavailableItems ?? playlists?.UnavailableItems ?? 0),
        affectedPlaylists,
      },
    };
  };

  rematchUnavailablePlaylists = async () => {
    const readiness = this.serviceResults.readiness;
    const affectedPlaylists = asArray(readiness?.affectedPlaylists);
    if (!affectedPlaylists.length) {
      this.navigate("/library/playlists");
      return;
    }

    this.serviceResults = {
      ...this.serviceResults,
      readiness: { ...readiness, rematching: true },
    };
    const results = await Promise.allSettled(affectedPlaylists.map((name) => API.matchPlaylist(name)));
    const matched = results.filter((result) => result.status === "fulfilled").length;
    const failed = results.length - matched;
    this.serviceResults = {
      ...this.serviceResults,
      readiness: { ...this.serviceResults.readiness, rematching: false },
    };
    this.toast(failed
      ? `Rematching started for ${matched} ${matched === 1 ? "playlist" : "playlists"}; ${failed} could not be started.`
      : `Rematching started for ${matched} ${matched === 1 ? "playlist" : "playlists"}.`);
    this.navigate("/library/playlists");
  };

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
    const [zone] = routeParts(route);
    return this.authenticated && !this.isAdministrator() && !["sources", "settings", "intelligence"].includes(zone)
      ? "/sources"
      : route;
  }

  async loadForRoute(force = false, authenticationRetry = false) {
    if (!this.authenticated) {
      return;
    }

    if (!this.isAdministrator() && !["sources", "settings", "intelligence"].includes(routeParts(this.route)[0])) {
      this.route = "/sources";
      window.history.replaceState(null, "", "#/sources");
    }

    const routeKey = `${this.route}`;
    if (!force && routeKey === this.routeLoadKey) {
      return;
    }
    this.routeLoadKey = routeKey;
    this.clearLoadFailure(`route:${routeKey}`);
    const failureKeysBeforeLoad = new Set(Object.keys(this.loadFailures));

    const [zone, sub] = routeParts(this.route);
    try {
      if (zone === "library") {
        if (!sub || ["playlists", "link", "injected", "external"].includes(sub)) {
          await Promise.all([this.loadPlaylistLinks(), this.loadPlaylists()]);
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
            this.loadDashboardPresentation(),
            this.loadAppleMusicStatus().catch((error) => {
              this.appleMusicStatus = { error: error.message, logged_in: false };
            }),
          ]);
        } else {
          await this.loadProviderAccounts();
        }
      } else if (zone === "settings") {
        if (sub === "extensions" && this.isAdministrator()) {
          await Promise.all([this.loadExtensionControlPlane(), this.loadExtensionStore()]);
        } else {
          await this.loadProviderAccounts();
        }
      } else if (zone === "activity") {
        await Promise.all([this.loadDashboardPresentation(), this.loadEndpointUsage(), this.loadScrobbling(), this.loadQueue(), this.loadJobs(), this.loadProviderAccounts()]);
      } else if (zone === "home" || !zone) {
        await Promise.all([
          this.loadProviderAccounts(),
          this.loadPlaylists(),
          this.loadJobs(),
          this.loadDashboardPresentation(),
        ]);
      }
    } catch (error) {
      if (error?.status === 401) {
        const sessionState = await this.confirmDashboardSession();
        if (sessionState === false) {
          this.handleExpiredSession();
          return;
        }
        if (sessionState === true && !authenticationRetry) {
          this.routeLoadKey = "";
          await this.loadForRoute(true, true);
          return;
        }
      }
      const specificFailureRecorded = Object.keys(this.loadFailures)
        .some((key) => !failureKeysBeforeLoad.has(key));
      if (!specificFailureRecorded) {
        this.recordLoadFailure(`route:${routeKey}`, `${titleCase(routeParts(routeKey)[0] || "page")} data`, error);
      }
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

  async loadDashboardPresentation() {
    const [summaries, activity] = await Promise.all([
      API.providerSummaries().catch(() => ({ providers: [] })),
      API.dashboardActivity(100).catch(() => ({ items: [] })),
    ]);
    this.providerSummaries = asArray(summaries?.providers || summaries?.Providers);
    this.dashboardActivity = asArray(activity?.items || activity?.Items);
    this.eventLogCursor = activity?.nextCursor || activity?.NextCursor || "";
    this.eventLogCursorId = activity?.nextCursorId || activity?.NextCursorId || "";
    this.eventLogHasMore = Boolean(activity?.hasMore ?? activity?.HasMore);
  }

  async loadEarlierEvents() {
    if (this.eventLogLoading || !this.eventLogHasMore || !this.eventLogCursor) return;
    this.eventLogLoading = true;
    try {
      const response = await API.dashboardActivity(100, this.eventLogCursor, this.eventLogCursorId);
      const incoming = asArray(response?.items || response?.Items);
      const known = new Set(this.dashboardActivity.map((entry) => String(entry.id || entry.Id)));
      this.dashboardActivity = [...this.dashboardActivity, ...incoming.filter((entry) => !known.has(String(entry.id || entry.Id)))];
      this.eventLogCursor = response?.nextCursor || response?.NextCursor || "";
      this.eventLogCursorId = response?.nextCursorId || response?.NextCursorId || "";
      this.eventLogHasMore = Boolean(response?.hasMore ?? response?.HasMore);
    } catch (error) {
      this.toast(error.message || "Earlier events could not be loaded", "error");
    } finally {
      this.eventLogLoading = false;
    }
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
      if (!this.playlistSources.length || !this.mediaTargets.length) {
        await this.loadPlaylistDiscovery();
      }
    } catch (error) {
      this.recordLoadFailure("playlistLinks", "Playlists", error);
      throw error;
    }
  }

  newPlaylistWizardDraft() {
    return {
      step: 0,
      sourceAccountId: "",
      sourceQuery: "",
      sourceNextCursor: "",
      sourcePlaylist: null,
      targetIdentityId: "",
      targetQuery: "",
      targetNextCursor: "",
      targetPlaylist: null,
      createTarget: false,
      mode: "virtual",
      materializationMode: "reconcile",
      trigger: "manual",
      cronExpression: "0 8 * * *",
      timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC",
      syncName: true,
      syncDescription: true,
      syncArtwork: true,
      preserveManualEntries: true,
      mirrorStaleEntries: false,
      legacyHandoff: null,
      loading: false,
      error: "",
    };
  }

  updatePlaylistWizard(patch) {
    this.playlistWizard = { ...this.playlistWizard, ...patch, error: "" };
    if (!Object.hasOwn(patch, "loading")) this.playlistDryRunPreview = null;
  }

  async loadPlaylistDiscovery() {
    const [sources, targets] = await Promise.all([API.playlistSources(), API.mediaTargets()]);
    this.playlistSources = asArray(sources?.accounts || sources?.Accounts);
    this.playlistSourceBlockedAccounts = asArray(sources?.blockedAccounts || sources?.BlockedAccounts);
    this.playlistSourceProviders = asArray(sources?.providers || sources?.Providers);
    this.mediaTargets = asArray(targets?.targets || targets?.Targets);
  }

  async choosePlaylistSourceAccount(accountId) {
    this.updatePlaylistWizard({ sourceAccountId: accountId, sourcePlaylist: null, sourceQuery: "", sourceNextCursor: "", loading: true });
    this.sourcePlaylistResults = [];
    try {
      const response = await API.sourcePlaylists(accountId);
      this.sourcePlaylistResults = asArray(response?.items || response?.Items);
      const preferredId = this.playlistWizard.legacyHandoff?.sourcePlaylistId || this.playlistWizard.legacyHandoff?.SourcePlaylistId;
      const preferred = preferredId
        ? this.sourcePlaylistResults.find((item) => String(item.id || item.Id) === String(preferredId))
        : null;
      this.updatePlaylistWizard({ loading: false, sourcePlaylist: preferred || null, sourceNextCursor: response?.nextCursor || response?.NextCursor || "" });
    } catch (error) {
      this.playlistWizard = { ...this.playlistWizard, loading: false, error: error.message };
    }
  }

  async searchSourcePlaylists() {
    const draft = this.playlistWizard;
    if (!draft.sourceAccountId) return;
    this.updatePlaylistWizard({ loading: true });
    try {
      const response = await API.sourcePlaylists(draft.sourceAccountId, draft.sourceQuery.trim());
      this.sourcePlaylistResults = asArray(response?.items || response?.Items);
      this.updatePlaylistWizard({ loading: false, sourceNextCursor: response?.nextCursor || response?.NextCursor || "" });
    } catch (error) {
      this.playlistWizard = { ...this.playlistWizard, loading: false, error: error.message };
    }
  }

  async loadMoreSourcePlaylists() {
    const draft = this.playlistWizard;
    if (!draft.sourceAccountId || !draft.sourceNextCursor || draft.loading) return;
    this.updatePlaylistWizard({ loading: true });
    try {
      const response = await API.sourcePlaylists(draft.sourceAccountId, draft.sourceQuery.trim(), draft.sourceNextCursor);
      const incoming = asArray(response?.items || response?.Items);
      const existing = new Set(this.sourcePlaylistResults.map((item) => String(item.id || item.Id)));
      this.sourcePlaylistResults = [...this.sourcePlaylistResults, ...incoming.filter((item) => !existing.has(String(item.id || item.Id)))];
      this.updatePlaylistWizard({ loading: false, sourceNextCursor: response?.nextCursor || response?.NextCursor || "" });
    } catch (error) {
      this.playlistWizard = { ...this.playlistWizard, loading: false, error: error.message };
    }
  }

  async chooseMediaTarget(identityId) {
    this.updatePlaylistWizard({ targetIdentityId: identityId, targetPlaylist: null, targetQuery: "", targetNextCursor: "", createTarget: false, loading: true });
    this.targetPlaylistResults = [];
    try {
      const response = await API.targetPlaylists(identityId);
      this.targetPlaylistResults = asArray(response?.items || response?.Items);
      const preferredId = this.playlistWizard.legacyHandoff?.jellyfinTargetPlaylistId || this.playlistWizard.legacyHandoff?.JellyfinTargetPlaylistId;
      const preferred = preferredId
        ? this.targetPlaylistResults.find((item) => String(item.id || item.Id) === String(preferredId))
        : null;
      this.updatePlaylistWizard({ loading: false, targetPlaylist: preferred || null, targetNextCursor: response?.nextCursor || response?.NextCursor || "" });
    } catch (error) {
      this.playlistWizard = { ...this.playlistWizard, loading: false, error: error.message };
    }
  }

  async searchTargetPlaylists() {
    const draft = this.playlistWizard;
    if (!draft.targetIdentityId) return;
    this.updatePlaylistWizard({ loading: true });
    try {
      const response = await API.targetPlaylists(draft.targetIdentityId, draft.targetQuery.trim());
      this.targetPlaylistResults = asArray(response?.items || response?.Items);
      this.updatePlaylistWizard({ loading: false, targetNextCursor: response?.nextCursor || response?.NextCursor || "" });
    } catch (error) {
      this.playlistWizard = { ...this.playlistWizard, loading: false, error: error.message };
    }
  }

  async loadMoreTargetPlaylists() {
    const draft = this.playlistWizard;
    if (!draft.targetIdentityId || !draft.targetNextCursor || draft.loading) return;
    this.updatePlaylistWizard({ loading: true });
    try {
      const response = await API.targetPlaylists(draft.targetIdentityId, draft.targetQuery.trim(), draft.targetNextCursor);
      const incoming = asArray(response?.items || response?.Items);
      const existing = new Set(this.targetPlaylistResults.map((item) => String(item.id || item.Id)));
      this.targetPlaylistResults = [...this.targetPlaylistResults, ...incoming.filter((item) => !existing.has(String(item.id || item.Id)))];
      this.updatePlaylistWizard({ loading: false, targetNextCursor: response?.nextCursor || response?.NextCursor || "" });
    } catch (error) {
      this.playlistWizard = { ...this.playlistWizard, loading: false, error: error.message };
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

    const [response, health, cts] = await Promise.all([
      API.providerAccounts(),
      administrator ? API.providerHealth() : Promise.resolve([]),
      administrator ? API.ctsMeasurements() : Promise.resolve({ measurements: [] }),
    ]);
    this.providerAccounts = asArray(response?.accounts || response?.Accounts);
    this.providerHealth = asArray(health);
    this.ctsMeasurements = asArray(cts?.measurements || cts?.Measurements);
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
    const [mappings, legacyMappings] = await Promise.all([
      API.mappings(this.mappingFilters),
      API.legacyMappings(),
    ]);
    this.mappings = mappings;
    this.legacyMappings = legacyMappings;
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

  async installExtension(item, updating = false) {
    const key = item.id || item.Id || item.displayName || item.DisplayName;
    this.extensionActions = { ...this.extensionActions, [key]: updating ? "Updating" : "Installing" };
    try {
      const installed = await API.installExtension(item);
      const packageId = installed.packageId || installed.PackageId;
      const revision = installed.revision ?? installed.Revision ?? 0;
      const state = String(installed.state || installed.State || "").replace(/[^a-z]/gi, "").toLowerCase();
      if (state === "staged" && packageId) {
        await API.activateExtensionPackage(packageId, revision);
      }
      await Promise.all([this.loadExtensionControlPlane(), this.loadExtensionStore()]);
      if (state === "reviewrequired" && packageId) {
        const extensionPackage = asArray(this.extensionPackages).find((entry) => String(entry.id || entry.Id) === String(packageId));
        if (extensionPackage) await this.loadExtensionPermissions(extensionPackage);
        this.toast(`Review permissions to finish ${updating ? "updating" : "installing"}`);
      } else {
        this.toast(`Extension ${updating ? "updated" : "installed"} and enabled`);
      }
    } catch (error) {
      this.toast(error.message || "Extension installation failed", "error");
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
    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    this.extensionRegistryError = "";
    this.extensionActions = { ...this.extensionActions, registry: "Adding" };
    try {
      await API.createExtensionRegistry({ name: form.get("name"), registryUrl: form.get("registryUrl"), enabled: true });
      formElement.reset();
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
    const response = await API.extensionPermissions(id);
    const next = new Map(this.extensionPermissions);
    next.set(id, asArray(response?.items || response?.Items || response));
    this.extensionPermissions = next;
    this.extensionPermissionPackageId = id;
    this.extensionPermissionConfirmed = false;
  }

  async reviewExtensionPermissions(item) {
    const id = item.id || item.Id;
    const reviews = this.extensionPermissions.get(id) || [];
    const decisions = reviews.map((review) => ({
      kind: review.permissionKind || review.PermissionKind,
      value: review.permissionValue || review.PermissionValue,
      approved: (review.uiDecision || review.UiDecision || review.decision || review.Decision || "pending").toString().toLowerCase() === "approved",
    }));
    this.extensionActions = { ...this.extensionActions, [id]: "Enabling" };
    try {
      const reviewed = await API.reviewExtensionPermissions(id, {
        expectedRevision: item.revision ?? item.Revision ?? 0,
        decisions,
      });
      const state = String(reviewed.state || reviewed.State || "").replace(/[^a-z]/gi, "").toLowerCase();
      if (state === "staged") {
        await API.activateExtensionPackage(id, reviewed.revision ?? reviewed.Revision ?? 0);
      }
      await Promise.all([this.loadExtensionControlPlane(), this.loadSchema()]);
      const nextPermissions = new Map(this.extensionPermissions);
      nextPermissions.delete(id);
      this.extensionPermissions = nextPermissions;
      this.extensionPermissionPackageId = "";
      this.extensionPermissionConfirmed = false;
      this.toast(state === "staged" || state === "active" ? "Extension enabled" : "Permission choices saved");
    } catch (error) {
      await this.loadExtensionControlPlane().catch(() => {});
      this.toast(error.message || "Extension activation failed", "error");
    } finally {
      const nextActions = { ...this.extensionActions };
      delete nextActions[id];
      this.extensionActions = nextActions;
    }
  }

  setExtensionPermissionDecision(packageId, permissionId, approved) {
    const next = new Map(this.extensionPermissions);
    next.set(packageId, asArray(next.get(packageId)).map((review) =>
      String(review.id || review.Id) === String(permissionId)
        ? { ...review, uiDecision: approved ? "approved" : "denied" }
        : review));
    this.extensionPermissions = next;
  }

  async approveAllExtensionPermissions(item) {
    const id = item.id || item.Id;
    const next = new Map(this.extensionPermissions);
    next.set(id, asArray(next.get(id)).map((review) => ({ ...review, uiDecision: "approved" })));
    this.extensionPermissions = next;
    await this.reviewExtensionPermissions(item);
  }

  async runExtensionAction(item, label, action, message) {
    const id = item.id || item.Id;
    this.extensionActions = { ...this.extensionActions, [id]: label };
    try {
      await action();
      await Promise.all([this.loadExtensionControlPlane(), this.loadSchema()]);
      this.toast(message);
    } catch (error) {
      this.toast(error.message || `${label} failed`, "error");
    } finally {
      const nextActions = { ...this.extensionActions };
      delete nextActions[id];
      this.extensionActions = nextActions;
    }
  }

  async openExtensionManager(item) {
    const id = item.id || item.Id;
    this.selectedExtensionPackageId = id;
    this.extensionSession = null;
    const requests = [API.extensionLogs(id)];
    if (item.usesSignedSession || item.UsesSignedSession) requests.push(API.extensionSession(id));
    const results = await Promise.allSettled(requests);
    if (results[0].status === "fulfilled") this.extensionLogs = asArray(results[0].value?.items || results[0].value?.Items || results[0].value);
    if (results[1]?.status === "fulfilled") this.extensionSession = results[1].value;
  }

  closeExtensionManager() {
    this.selectedExtensionPackageId = "";
    this.extensionSession = null;
  }

  async startExtensionAuthorization(item) {
    const id = item.id || item.Id;
    // Open synchronously so browsers do not treat the authorization tab as a popup
    // created after an asynchronous request.
    const authorizationWindow = window.open("about:blank", "_blank");
    try {
      const result = await API.startExtensionSession(id);
      if (result.success === false) throw new Error(result.error || "Authorization could not start");
      const authUrl = result.auth_url || result.open_auth_url || result.authUrl || result.openAuthUrl;
      this.extensionSession = { ...(this.extensionSession || {}), ...result, authUrl, authorizationError: "" };
      if (authUrl && authorizationWindow) {
        authorizationWindow.opener = null;
        authorizationWindow.location.replace(authUrl);
      } else {
        authorizationWindow?.close();
      }
      this.toast(authUrl ? "Authorization opened in a new tab" : "Extension session is ready");
    } catch (error) {
      authorizationWindow?.close();
      this.extensionSession = { ...(this.extensionSession || {}), authorizationError: error.message };
      this.toast(error.message, "error");
    }
  }

  async completeExtensionAuthorization(event, item) {
    event.preventDefault();
    const grant = String(new FormData(event.currentTarget).get("grant") || "").trim();
    try {
      const result = await API.completeExtensionSession(item.id || item.Id, grant);
      if (result.success === false) throw new Error(result.error || "The grant was rejected");
      this.extensionSession = await API.extensionSession(item.id || item.Id);
      event.currentTarget.reset();
      this.toast("Extension account authorized");
    } catch (error) {
      this.extensionSession = { ...(this.extensionSession || {}), authorizationError: error.message };
      this.toast(error.message, "error");
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

    const priorityGroup = config.group || this.providerGroup(category);
    let nextPriorityProviders = config.group ? splitCsv(nextProviders) : null;
    if (config.valuePath && priorityGroup) {
      const currentPriorityProviders = asArray(priorityGroup.providers);
      nextPriorityProviders = enabled
        ? splitCsv(joinCsv([...currentPriorityProviders, providerId]))
        : currentPriorityProviders.filter((item) => item !== providerId);
      if (joinCsv(nextPriorityProviders) !== joinCsv(currentPriorityProviders)) {
        await API.updateConfig(priorityGroup.envKey, joinCsv(nextPriorityProviders));
      }
    }

    if (priorityGroup && nextPriorityProviders) {
      this.schema = {
        ...this.schema,
        priorityGroups: this.schema.priorityGroups.map((group) =>
          group.id === priorityGroup.id ? { ...group, providers: nextPriorityProviders } : group,
        ),
      };
    }

    this.restartKeys = new Set([
      ...this.restartKeys,
      config.envKey,
      ...(priorityGroup && config.valuePath ? [priorityGroup.envKey] : []),
    ]);
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

  async submitApplePackage(event) {
    event.preventDefault();
    const form = event.currentTarget;
    const file = form.elements.namedItem("package")?.files?.[0];
    if (!file) {
      this.serviceResults = { ...this.serviceResults, applemusic: { state: "warning", message: "Choose an .apk or .apkm package first." } };
      return;
    }
    this.serviceResults = { ...this.serviceResults, applemusic: { state: "running", message: "Uploading Apple Music package..." } };
    try {
      const result = await API.appleMusicSetup(file);
      this.appleUpload = result;
      form.reset();
      this.serviceResults = { ...this.serviceResults, applemusic: { state: "success", message: result.message || "Package staged." } };
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
      <div class="app-shell ${this.sidebarCollapsed ? "sidebar-collapsed" : ""}" data-testid="app-shell">
        ${this.navOpen ? html`
          <button
            type="button"
            class="sidebar-backdrop"
            aria-label="Close menu"
            @click=${() => { this.navOpen = false; }}
          ></button>
        ` : nothing}
        ${this.renderSidebar()}
        <div class="main-shell ${administrator && this.getRecentPlayback() ? "has-now-playing" : ""}" data-testid="main-shell">
          ${this.renderTopbar()}
          <main class="content">
            ${this.renderLoadFailures()}
            ${this.renderRoute()}
          </main>
          ${administrator ? this.renderNowPlaying() : nothing}
        </div>
      </div>
      ${administrator ? this.renderRestartBar() : nothing}
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
    const primaryRoutes = ["home", "library", "sources", "activity", "settings"]
      .map((id) => routeById.get(id))
      .filter(Boolean);
    const renderNavLink = (route) => html`
      <a class="nav-link ${this.isRouteActive(route.path) ? "active" : ""}" href=${route.path} title=${route.label} aria-label=${route.label}>
        ${icon(route.id)}<span>${route.label}</span>
      </a>`;
    return html`
      <aside id="primary-sidebar" class="sidebar ${this.navOpen ? "open" : ""}" data-testid="primary-sidebar">
        <div class="brand">
          <button
            type="button"
            class="mobile-menu sidebar-close"
            aria-label="Close menu"
            @click=${() => { this.navOpen = false; }}
            @keydown=${(event) => {
              if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                this.navOpen = false;
              }
            }}
          >×</button>
          <div class="brand-heading">
            <a class="brand-title" href=${administrator ? "#/home" : "#/sources"} title="Allstarr home" aria-label="Allstarr home">
              <span class="brand-mark" aria-hidden="true">A</span>
              <span><strong>Allstarr</strong><small>Music control center</small></span>
            </a>
            <button
              type="button"
              class="ghost icon-button sidebar-collapse"
              title=${this.sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar"}
              aria-label=${this.sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar"}
              aria-expanded=${this.sidebarCollapsed ? "false" : "true"}
              @click=${() => {
                this.sidebarCollapsed = !this.sidebarCollapsed;
                localStorage.setItem(SIDEBAR_COLLAPSED_KEY, this.sidebarCollapsed ? "1" : "0");
              }}
            >${icon(this.sidebarCollapsed ? "chevronRight" : "chevronLeft")}</button>
          </div>
          <div class="brand-status">
            <span class="status-dot" aria-hidden="true"></span>
            <span>${display(this.schema?.activeBackend || this.config?.backendType)}</span>
            <span class="brand-version">v${display(this.status?.version || this.status?.Version, "—")}</span>
          </div>
        </div>
        <nav class="nav-list" aria-label="Primary">
          <div class="nav-section">${primaryRoutes.map(renderNavLink)}</div>
        </nav>
        <div class="sidebar-footer">
          <div class="user-summary"><span class="user-avatar">${this.session?.avatarUrl || this.session?.AvatarUrl ? html`<img src=${this.session.avatarUrl || this.session.AvatarUrl} alt="">` : display(this.session?.name || this.session?.Name, "U").slice(0, 1).toUpperCase()}</span><span><small>Signed in as</small><strong>${display(this.session?.name || this.session?.Name)}</strong></span></div>
          ${administrator ? html`<button class="ghost" title=${this.redactionMode ? "Sharing redaction on" : "Redact for sharing"} aria-label=${this.redactionMode ? "Sharing redaction on" : "Redact for sharing"} aria-pressed=${this.redactionMode ? "true" : "false"} @click=${this.toggleRedactionMode}>${icon("shield")}<span>${this.redactionMode ? "Sharing redaction on" : "Redact for sharing"}</span></button>` : nothing}
          <button class="ghost" title="Logout" aria-label="Logout" @click=${this.logout}>${icon("logout")}<span>Logout</span></button>
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
    const administrator = this.isAdministrator();
    return html`
      <header class="topbar" data-testid="topbar">
        <div class="topbar-main">
          <button
            type="button"
            class="mobile-menu menu-trigger"
            aria-label="Open menu"
            aria-controls="primary-sidebar"
            aria-expanded=${this.navOpen ? "true" : "false"}
            @click=${() => { this.navOpen = true; }}
            @keydown=${(event) => {
              if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                this.navOpen = true;
              }
            }}
          >
            <span class="menu-trigger-lines" aria-hidden="true">
              <span></span><span></span><span></span>
            </span>
          </button>
          <div class="topbar-title-group">
            <h1>Workspace</h1>
          </div>
        </div>
        ${administrator ? this.renderGlobalSearch() : nothing}
        <div class="actions">
          <details class="theme-menu">
            <summary aria-label="Choose theme" title="Theme">${titleCase(this.theme)}</summary>
            <div class="theme-menu-popover" role="menu" aria-label="Theme">
              ${["system", "dark", "light"].map((theme) => html`<button
                type="button"
                role="menuitemradio"
                aria-checked=${this.theme === theme ? "true" : "false"}
                class=${this.theme === theme ? "active" : ""}
                @click=${(event) => {
                  this.setTheme(theme);
                  event.currentTarget.closest("details")?.removeAttribute("open");
                }}
              ><span>${titleCase(theme)}</span>${this.theme === theme ? icon("check", 16) : nothing}</button>`)}
            </div>
          </details>
          ${administrator ? html`<button class="refresh-button icon-button ghost" aria-label="Refresh current status" title="Refresh" @click=${async () => { await Promise.all([this.loadStatus(), this.loadConfig(), this.loadEnvMigrationStatus(), this.loadForRoute(true)]); this.toast("Status refreshed"); }}>${icon("refresh")}</button>` : nothing}
        </div>
      </header>
    `;
  }

  renderGlobalSearch() {
    const query = this.globalSearchQuery.trim().toLowerCase();
    const routes = [...asArray(this.schema?.routes).map((route) => ({
      kind: "Page", label: route.label, detail: "Open page", path: route.path,
    })), ...(this.isAdministrator() ? [{ kind: "Page", label: "Extensions", detail: "Install and manage extensions", path: "#/settings/extensions" }] : [])];
    const providers = asArray(this.schema?.providers).map((provider) => ({
      kind: "Source", label: provider.name, detail: titleCase(this.providerStatus(provider)), path: "#/sources",
    }));
    const playlists = asArray(this.playlists?.playlists || this.playlists?.Playlists).map((playlist) => ({
      kind: "Playlist", label: playlist.name, detail: `${display(playlist.trackCount, 0)} tracks`, path: "#/library/playlists", playlist: playlist.name,
    }));
    const results = query
      ? [...routes, ...providers, ...playlists].filter((item) => `${item.label} ${item.kind} ${item.detail}`.toLowerCase().includes(query)).slice(0, 8)
      : [];
    return html`<div class="global-search">
      ${icon("search")}
      <input aria-label="Search Allstarr" placeholder="Search pages, sources, playlists…" .value=${this.globalSearchQuery}
        @focus=${() => { this.globalSearchOpen = true; }}
        @input=${(event) => { this.globalSearchQuery = event.target.value; this.globalSearchOpen = true; }}
        @keydown=${(event) => { if (event.key === "Escape") { this.globalSearchOpen = false; event.target.blur(); } }}>
      ${this.globalSearchOpen && query ? html`<div class="global-search-results" role="listbox">
        ${results.length ? results.map((item) => html`<button role="option" @click=${async () => {
          this.globalSearchOpen = false;
          this.globalSearchQuery = "";
          window.location.hash = item.path;
          if (item.playlist) {
            await this.loadPlaylists();
            await this.openInjectedPlaylist(item.playlist);
          }
        }}><span>${item.label}</span><small>${item.kind} · ${item.detail}</small></button>`) : html`<div class="global-search-empty">No matching pages, sources, or playlists.</div>`}
      </div>` : nothing}
    </div>`;
  }

  renderRoute() {
    if (!this.isAdministrator()) {
      const [zone] = routeParts(this.route);
      if (zone === "intelligence") return this.renderIntelligence();
      if (zone === "settings") return this.renderSettings();
      return this.renderSources();
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
    const spotifyImport = this.status?.spotifyImport || this.status?.SpotifyImport || {};
    const downloadCanAttempt = asArray(this.schema?.providers).some((provider) =>
      asArray(provider.runtimeCapabilities).some((capability) =>
        capability.id === "download" && capability.canAttempt),
    );
    const playlistHealthRows = this.providerHealth.filter((item) =>
      String(item.capability || item.Capability || "").toLowerCase() === "playlist" &&
      (item.enabled ?? item.Enabled) !== false);
    const playlistProviderIds = [...new Set(playlistHealthRows
      .map((item) => String(item.provider || item.Provider || "").toLowerCase())
      .filter(Boolean))];
    const readyPlaylistProviderIds = playlistProviderIds.filter((providerId) =>
      playlistHealthRows.some((item) =>
        String(item.provider || item.Provider || "").toLowerCase() === providerId &&
        String(item.health || item.Health || "").toLowerCase() === "healthy"));
    const latestPlaylistCheck = playlistHealthRows
      .map((item) => item.testedAt || item.TestedAt)
      .filter(Boolean)
      .sort((left, right) => new Date(right).getTime() - new Date(left).getTime())[0];
    const playlistRefreshState = playlistProviderIds.length > 0 && readyPlaylistProviderIds.length === playlistProviderIds.length
      ? "healthy"
      : readyPlaylistProviderIds.length ? "partial" : "unknown";
    const playlistProviderNames = playlistProviderIds.map((providerId) =>
      providerDisplayName(providerId, this.schema?.providers));
    const readiness = this.serviceResults.readiness;
    const playlists = asArray(this.playlists?.playlists || this.playlists?.Playlists);
    const activeJobs = asArray(this.jobs).filter((job) => !["Succeeded", "Failed", "Cancelled"].includes(job.state || job.State)).length;
    const providerSummaries = asArray(this.providerSummaries);

    return html`
      <section class="view-stack home-view" data-testid="home-workspace">
        ${pageHeader("Home", "Runtime state, provider readiness, and current activity.")}

        <div class="overview-grid">
          <div class="card overview-card">
            <span class="overview-icon backend">${icon("server", 22)}</span>
            <div><span class="metric-label">Backend</span><span class="metric-value">${display(this.status?.backendType || this.config?.backendType)}</span></div>
            <small class="health-line healthy"><span></span>Running</small>
          </div>
          <div class="card overview-card">
            <span class="overview-icon provider">${icon("refresh", 22)}</span>
            <div><span class="metric-label">Playlist sources</span><span class="metric-value">${playlistProviderIds.length ? `${readyPlaylistProviderIds.length} / ${playlistProviderIds.length} ready` : "None"}</span></div>
            <small class="health-line ${playlistRefreshState === "healthy" ? "healthy" : "warning"}" title=${playlistProviderNames.join(", ")}><span></span>${playlistProviderIds.length ? `${playlistProviderNames.join(", ")} · ${latestPlaylistCheck ? formatRelativeTime(latestPlaylistCheck) : "Awaiting check"}` : "Connect a playlist provider"}</small>
          </div>
          <div class="card overview-card">
            <span class="overview-icon playlists">${icon("playlist", 22)}</span>
            <div><span class="metric-label">Managed playlists</span><span class="metric-value">${display(playlists.length || spotifyImport.playlistCount || 0)}</span></div>
            <small class="health-line info"><span></span>Total in library</small>
          </div>
          <div class="card overview-card">
            <span class="overview-icon tasks">${icon("tasks", 22)}</span>
            <div><span class="metric-label">Active tasks</span><span class="metric-value">${activeJobs + this.activity.filter((item) => String(item.status || item.Status).toLowerCase().includes("progress")).length}</span></div>
            <small class="health-line ${activeJobs ? "warning" : "healthy"}"><span></span>${activeJobs ? `${activeJobs} running` : "No active tasks"}</small>
          </div>
        </div>

        <div class="panel home-readiness" data-testid="readiness-panel">
          <div class="section-heading">
            <div><h3>Core readiness</h3><p>One read-only check covers the player artwork route, restored playlists, and source health.</p></div>
            <div class="actions readiness-heading-actions">
              ${readiness ? html`<span class="status-chip ${readiness.state}">${readiness.state === "success" ? "Ready" : readiness.state === "running" ? "Checking" : "Action needed"}</span>` : html`<span class="status-chip unknown">Not checked</span>`}
              <button class="primary icon-label" ?disabled=${readiness?.state === "running"} @click=${this.runCoreReadiness}>${icon("shield", 17)}<span>${readiness?.state === "running" ? "Running…" : "Run check"}</span></button>
            </div>
          </div>
          ${readiness ? html`
            <div class="callout ${readiness.state}" role="status"><strong>${readiness.state === "success" ? "Core music paths ready" : readiness.state === "running" ? "Running core checks" : "Some paths need attention"}</strong><span>${readiness.message}</span></div>
            ${readiness.state !== "running" ? html`<div class="readiness-checks">
              ${this.renderReadinessCheck("Player artwork", readiness.mediaReady, "Authenticated Jellyfin artwork route")}
              ${this.renderReadinessCheck("Restored playlists", readiness.playlistsReady, readiness.playlistMessage)}
              ${this.renderReadinessCheck("Playlist source", readiness.playlistSourceReady, readiness.playlistSourceReady ? "At least one playlist provider is healthy." : "Connect or repair a playlist-capable source.")}
            </div>` : nothing}
          ` : html`<div class="readiness-empty">${icon("shield", 20)}<span>Run the check after an update, provider change, or player problem.</span></div>`}
          ${readiness?.state === "warning" ? html`<div class="actions">
            ${readiness.affectedPlaylists?.length ? html`<button class="primary" ?disabled=${readiness.rematching} @click=${this.rematchUnavailablePlaylists}>${readiness.rematching ? "Starting rematch..." : "Rematch with available providers"}</button><button @click=${() => this.navigate("/library/playlists")}>Review affected playlists</button>` : html`<button class="primary" @click=${() => this.navigate("/sources")}>Fix source connections</button>`}
            <button @click=${() => this.navigate("/settings")}>Open detailed diagnostics</button>
          </div>` : nothing}
        </div>

        <div class="setup-launcher premium-callout">
          <div>
            <h3>Need a hand getting everything connected?</h3>
            <p>Open the setup guide again at any time. It will walk through your media server, sources, and the optional Allstarr 2.x import.</p>
          </div>
          <button @click=${() => this.openSetupGuide()}>Open setup guide</button>
        </div>

        <div class="wide-grid home-detail-grid">
          <div class="panel">
            <h3>Setup</h3>
            <div class="stat-list">
              ${this.renderSetupStep("Backend URL configured", Boolean(this.config?.jellyfin?.url || this.config?.subsonic?.url))}
              ${this.renderSetupStep("Playlist source connected", readyPlaylistProviderIds.length > 0)}
              ${this.renderSetupStep("Download capability configured", downloadCanAttempt)}
              ${this.renderSetupStep("Playlist sync enabled", Boolean(this.config?.spotifyImport?.enabled))}
            </div>
          </div>
          <div class="panel">
            <h3>Provider health</h3>
            <div class="stat-list">
              ${asArray(this.schema?.providers).filter((provider) => !["disabled"].includes(this.providerStatus(provider))).slice(0, 6).map((provider) => {
                const summary = providerSummaries.find((item) => String(item.providerId).toLowerCase() === String(provider.id).toLowerCase());
                const status = this.providerStatus(provider);
                return html`
                <div class="stat-row">
                  <span class="provider-row-label">${this.renderProviderLogo(provider.id, "tiny")}<span>${provider.name}</span></span>
                  <span class="status-chip ${status}" title=${summary?.lastCheckedAt ? `Checked ${formatDate(summary.lastCheckedAt)}` : "No recent check"}>${this.providerStatusLabel(status)}</span>
                </div>`;
              })}
            </div>
          </div>
        </div>

        ${this.renderDashboardActivity()}
        ${this.renderHomeLibraryOverview(playlists)}
      </section>
    `;
  }

  renderDashboardActivity() {
    const items = asArray(this.dashboardActivity).slice(0, 8);
    return html`<div class="panel dashboard-activity">
      <div class="section-heading"><div><h3>Event log</h3><p>Recent matching, playlist, provider, and administrative events.</p></div><button class="ghost" @click=${() => this.navigate("/activity")}>View event log</button></div>
      ${items.length ? html`<div class="activity-table" role="table" aria-label="Recent activity">
        <div class="activity-table-head" role="row"><span>Time</span><span>Source</span><span>Event</span><span>Details</span></div>
        ${items.map((item) => html`<div class="activity-table-row" role="row">
          <time>${formatRelativeTime(item.occurredAt || item.OccurredAt)}</time>
          <span class="provider-row-label">${this.renderProviderLogo(item.source || item.Source, "tiny")}<span>${providerDisplayName(item.source || item.Source, this.schema?.providers)}</span></span>
          <span class="activity-event ${item.state || item.State}">${["healthy", "succeeded"].includes(String(item.state || item.State).toLowerCase()) ? icon("check", 15) : ["failed", "degraded", "unavailable"].includes(String(item.state || item.State).toLowerCase()) ? icon("warning", 15) : icon("clock", 15)}${titleCase(item.label || item.Label)}</span>
          <span class="muted">${display(item.detail || item.Detail)}</span>
        </div>`)}
      </div>` : html`<div class="empty compact">No recent provider checks or background jobs.</div>`}
    </div>`;
  }

  renderHomeLibraryOverview(playlists) {
    return html`<div class="panel home-library-overview">
      <div class="section-heading"><div><h3>Library overview</h3><p>Your recently synchronized playlists.</p></div><button @click=${() => this.navigate("/library/playlists")}>View all playlists ${icon("chevronRight", 16)}</button></div>
      ${playlists.length ? html`<div class="compact-playlist-table">
        <div class="compact-playlist-head"><span>Playlist</span><span>Tracks</span><span>Matched</span><span>Provider</span><span>Last sync</span><span>Status</span></div>
        ${playlists.slice(0, 6).map((playlist) => html`<button class="compact-playlist-row" @click=${() => { this.navigate("/library/playlists"); window.setTimeout(() => this.openInjectedPlaylist(playlist.name), 0); }}>
          <span class="playlist-cell"><img src=${playlist.artworkUrl || "/images/playlist-placeholder.svg"} alt=""><span><strong>${playlist.name}</strong><small>${playlist.sourceProvider ? providerDisplayName(playlist.sourceProvider, this.schema?.providers) : "Managed playlist"}</small></span></span>
          <span>${display(playlist.trackCount, 0)}</span>
          <span>${display(playlist.matchedTracks ?? Number(playlist.localTracks || 0) + Number(playlist.externalTracks || 0), 0)} <small>${display(playlist.matchPercent, 0)}%</small></span>
          <span class="provider-row-label">${playlist.sourceProvider ? this.renderProviderLogo(playlist.sourceProvider, "tiny") : icon("warning", 15)}<span>${playlist.sourceProvider ? providerDisplayName(playlist.sourceProvider, this.schema?.providers) : "Unknown source"}</span></span>
          <span>${formatRelativeTime(playlist.lastSyncAt || playlist.lastFetched)}</span>
          <span><span class="status-chip ${playlist.syncStatus || "unknown"}">${titleCase(playlist.syncStatus || "pending")}</span></span>
        </button>`)}
      </div>` : html`<div class="empty compact">No injected playlists configured yet.</div>`}
    </div>`;
  }

  renderProviderLogo(providerId, size = "default") {
    const provider = asArray(this.schema?.providers).find((item) => String(item.id || item.Id).toLowerCase() === String(providerId).toLowerCase()) || { id: providerId, name: providerDisplayName(providerId, this.schema?.providers) };
    const logoUrl = providerLogoUrl(provider);
    return html`<span class="provider-logo provider-${String(providerId).toLowerCase()} logo-${size}">${logoUrl ? html`<img src=${logoUrl} alt="">` : html`<span>${providerMark(provider).slice(0, 2)}</span>`}</span>`;
  }

  renderExtensionLogo(item, size = "default") {
    const extensionId = String(item?.extensionId || item?.ExtensionId || item?.id || item?.Id || "").toLowerCase();
    const name = item?.displayName || item?.DisplayName || item?.name || item?.Name || "Extension";
    const packageIcon = item?.iconUrl || item?.IconUrl;
    const builtInIcon = providerLogoUrl({ id: extensionId, name });
    const logoUrl = packageIcon || builtInIcon;
    return html`<span class="provider-logo extension-logo provider-${extensionId} logo-${size}">
      ${logoUrl
        ? html`<img src=${logoUrl} alt="" @error=${(event) => { event.currentTarget.hidden = true; event.currentTarget.nextElementSibling?.removeAttribute("hidden"); }}><span class="extension-logo-fallback" hidden>${icon("extensions", size === "hero" ? 28 : 20)}</span>`
        : html`<span class="extension-logo-fallback">${icon("extensions", size === "hero" ? 28 : 20)}</span>`}
    </span>`;
  }

  renderReadinessCheck(label, ready, detail) {
    return html`<div class="readiness-check ${ready ? "ready" : "attention"}"><span class="readiness-icon" aria-hidden="true">${ready ? "✓" : "!"}</span><div><strong>${label}</strong><small>${detail}</small></div><span class="status-chip ${ready ? "healthy" : "degraded"}">${ready ? "Ready" : "Attention"}</span></div>`;
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
    const sub = !requestedSub || ["playlists", "link", "injected", "external"].includes(requestedSub)
      ? "playlists"
      : requestedSub;
    return html`
      <section class="view-stack" data-testid="library-workspace">
        ${pageHeader("Library", "Match provider playlists to your local library, keep their order, and choose where they show up.")}
        ${this.renderLibraryNav(sub)}
        ${sub === "playlists" ? this.renderPlaylistsWorkspace() :
          sub === "mappings" ? this.renderMappings() :
          sub === "missing" ? this.renderMissingTracks() :
          sub === "migration" ? this.renderSongMigration() :
          sub === "kept" ? this.renderKeptDownloads() :
          this.renderPlaylistsWorkspace()}
      </section>
    `;
  }

  renderLibraryNav(active) {
    const items = [
      ["playlists", "Playlists", "playlist"],
      ["mappings", "Mappings", "sources"],
      ["kept", "Kept", "check"],
    ];
    return html`
      <nav class="subnav library-tabs" data-testid="library-tabs">
        ${items.map(([id, label, iconName]) => html`<a class=${active === id ? "active" : ""} href="#/library/${id}">${icon(iconName, 16)}<span>${label}</span></a>`)}
      </nav>
    `;
  }

  renderLibraryOverview() {
    return html`
      <div class="grid">
        <button class="card" @click=${() => this.navigate("/library/playlists")}>
          <h3>Playlists</h3>
          <p class="muted">Bring a provider playlist into Jellyfin or Navidrome and keep it synchronized.</p>
        </button>
        <button class="card" @click=${() => this.navigate("/library/mappings")}>
          <h3>Track mappings</h3>
          <p class="muted">Review the canonical Postgres track map from any source provider to local and playable provider identities.</p>
        </button>
        <button class="card" @click=${() => this.navigate("/library/kept")}>
          <h3>Kept downloads</h3>
          <p class="muted">Review permanent downloads and archives.</p>
        </button>
      </div>
    `;
  }

  renderPlaylistsWorkspace() {
    const imported = asArray(this.playlists);
    return html`
      ${this.renderLinkPlaylists()}
      ${imported.length ? html`<section class="legacy-playlist-section">
        <div class="section-heading"><div><span class="eyebrow">Imported configuration</span><h3>Existing playlists</h3><p>These playlists were created by the earlier injected-playlist workflow. They remain fully manageable while you move them into the unified workflow.</p></div></div>
        ${this.renderInjectedPlaylists()}
      </section>` : nothing}
    `;
  }

  renderLinkPlaylists() {
    const links = asArray(this.playlistLinks);
    return html`
      <div class="playlist-link-layout">
        <div class="view-stack">
          ${this.renderPlaylistLinkWizard()}

          <div class="table-wrap"><table class="responsive-data-table"><thead><tr><th>Playlist</th><th>Source</th><th>Target</th><th>Mode</th><th>Last run</th><th>Actions</th></tr></thead><tbody>
            ${links.length ? links.map((link) => this.renderPlaylistLinkRow(link)) : html`<tr class="empty-table-row"><td class="empty-table-cell" colspan="6"><div class="empty">No synchronized playlists yet.</div></td></tr>`}
          </tbody></table></div>
        </div>
      </div>
      ${this.renderPlaylistLinkPreview()}${this.renderPlaylistBehaviorDialog()}`;
  }

  renderPlaylistLinkWizard() {
    const draft = this.playlistWizard;
    const steps = ["Source", "Target", "Behavior", "Review"];
    return html`<section class="panel playlist-link-wizard" aria-labelledby="playlist-wizard-title">
      <header class="section-heading"><div><h3 id="playlist-wizard-title">Add a playlist</h3><p>Choose a source playlist and where it should appear. Allstarr handles the connection and matching.</p></div></header>
      <ol class="wizard-steps" aria-label="Playlist link progress">
        ${steps.map((label, index) => html`<li class=${index === draft.step ? "current" : index < draft.step ? "complete" : ""} aria-current=${index === draft.step ? "step" : nothing}><span>${index + 1}</span>${label}</li>`)}
      </ol>
      ${draft.error ? html`<div class="inline-alert error" role="alert">${draft.error}</div>` : nothing}
      <div class="wizard-body">
        ${draft.step === 0 ? this.renderPlaylistSourceStep() :
          draft.step === 1 ? this.renderPlaylistTargetStep() :
          draft.step === 2 ? this.renderPlaylistBehaviorStep() :
          this.renderPlaylistReviewStep()}
      </div>
      <footer class="actions wizard-actions">
        ${draft.step > 0 ? html`<button @click=${() => this.updatePlaylistWizard({ step: draft.step - 1 })}>Back</button>` : html`<button class="ghost" @click=${() => { this.playlistWizard = this.newPlaylistWizardDraft(); this.sourcePlaylistResults = []; this.targetPlaylistResults = []; }}>Reset</button>`}
        <span class="action-spacer"></span>
        ${draft.step < 3 ? html`<button class="primary" ?disabled=${!this.playlistWizardStepComplete()} @click=${() => this.updatePlaylistWizard({ step: draft.step + 1 })}>Continue</button>` : html`<button class="primary" ?disabled=${draft.loading} @click=${() => this.createPlaylistLink(false)}>Add playlist</button><button ?disabled=${draft.loading} @click=${() => this.createPlaylistLink(true)}>Add and sync now</button>`}
      </footer>
    </section>`;
  }

  renderPlaylistSourceStep() {
    const draft = this.playlistWizard;
    const blocked = this.playlistSourceBlockedAccounts;
    const providerNames = this.playlistSourceProviders.map((provider) => provider.displayName || provider.DisplayName).filter(Boolean);
    return html`<div class="wizard-step-panel"><div class="step-copy"><h4>Choose the source playlist</h4><p class="muted">Every connected provider or extension that exposes the Playlist capability can appear here.</p></div>
      ${draft.legacyHandoff ? html`<div class="callout"><strong>Imported from Allstarr 2.x</strong><p>Choose the Spotify account that owns <strong>${draft.legacyHandoff.name || draft.legacyHandoff.Name}</strong>. Allstarr will select source ID <span class="mono">${draft.legacyHandoff.sourcePlaylistId || draft.legacyHandoff.SourcePlaylistId}</span> when that account can see it.</p></div>` : nothing}
      ${this.playlistSources.length ? html`<div class="choice-grid account-choice-grid">
        ${this.playlistSources.map((account) => {
          const id = String(account.id || account.Id);
          const provider = account.providerId || account.ProviderId;
          const access = account.accessLabel || account.AccessLabel || titleCase(account.scope || account.Scope || "account");
          return html`<button class="choice-card ${draft.sourceAccountId === id ? "selected" : ""}" @click=${() => this.choosePlaylistSourceAccount(id)}><span class="provider-choice-icon">${this.renderProviderLogo(provider, "tiny")}</span><span><strong>${account.displayName || account.DisplayName}</strong><small>${providerDisplayName(provider, this.schema?.providers)} · ${access}</small></span></button>`;
        })}
      </div>` : blocked.length ? html`<div class="inline-alert warning playlist-source-policy"><strong>Shared playlist credentials are configured but disabled by policy.</strong><span>${blocked.map((account) => `${providerDisplayName(account.providerId || account.ProviderId, this.schema?.providers)} (${account.displayName || account.DisplayName})`).join(", ")}</span><small>Enable shared credentials for personal playlist operations in deployment settings, or connect a personal provider account.</small><button @click=${() => this.navigate("/settings")}>Review Settings</button></div>` : html`<div class="empty playlist-source-empty"><strong>No playlist source is connected.</strong><span>Connect any provider or extension with Playlist capability${providerNames.length ? `. Available now: ${providerNames.join(", ")}.` : "."}</span><button @click=${() => this.navigate("/sources")}>Open Sources</button></div>`}
      ${this.playlistSources.length && blocked.length ? html`<div class="inline-alert warning playlist-source-policy"><strong>${blocked.length} shared source${blocked.length === 1 ? " is" : "s are"} hidden by policy.</strong><span>Use a personal or library-shared account, or explicitly allow deployment-shared credentials.</span></div>` : nothing}
      ${draft.sourceAccountId ? html`<div class="picker-search"><input aria-label="Search source playlists" placeholder="Search playlists" .value=${draft.sourceQuery} @input=${(event) => this.updatePlaylistWizard({ sourceQuery: event.target.value })} @keydown=${(event) => { if (event.key === "Enter") this.searchSourcePlaylists(); }}><button @click=${() => this.searchSourcePlaylists()}>Search</button></div>${this.renderPlaylistChoices(this.sourcePlaylistResults, draft.sourcePlaylist, (playlist) => this.updatePlaylistWizard({ sourcePlaylist: playlist }), "source")}${draft.sourceNextCursor ? html`<button class="load-more" ?disabled=${draft.loading} @click=${() => this.loadMoreSourcePlaylists()}>Load more playlists</button>` : nothing}` : nothing}
    </div>`;
  }

  renderPlaylistTargetStep() {
    const draft = this.playlistWizard;
    return html`<div class="wizard-step-panel"><div class="step-copy"><h4>Choose where it should appear</h4><p class="muted">Credentials and backend IDs come from your connected media-server account.</p></div>
      ${draft.legacyHandoff?.jellyfinTargetPlaylistId || draft.legacyHandoff?.JellyfinTargetPlaylistId ? html`<div class="callout"><strong>Existing Jellyfin target found</strong><p>After you choose the correct Jellyfin connection, Allstarr will look for target playlist <span class="mono">${draft.legacyHandoff.jellyfinTargetPlaylistId || draft.legacyHandoff.JellyfinTargetPlaylistId}</span>.</p></div>` : nothing}
      <div class="choice-grid account-choice-grid">
        ${this.mediaTargets.map((target) => {
          const id = String(target.id || target.Id);
          const protocol = target.protocol || target.Protocol;
          return html`<button class="choice-card ${draft.targetIdentityId === id ? "selected" : ""}" @click=${() => this.chooseMediaTarget(id)}><span class="provider-choice-icon">${protocol === "jellyfin" ? this.renderProviderLogo("jellyfin", "tiny") : icon("library", 22)}</span><span><strong>${target.displayName || target.DisplayName || (protocol === "jellyfin" ? "Jellyfin" : "Navidrome / Subsonic")}</strong><small>${protocol === "jellyfin" ? "Jellyfin" : "Navidrome / Subsonic"}</small></span></button>`;
        })}
      </div>
      ${draft.targetIdentityId ? html`<div class="picker-search"><input aria-label="Search target playlists" placeholder="Search existing playlists" .value=${draft.targetQuery} @input=${(event) => this.updatePlaylistWizard({ targetQuery: event.target.value })} @keydown=${(event) => { if (event.key === "Enter") this.searchTargetPlaylists(); }}><button @click=${() => this.searchTargetPlaylists()}>Search</button></div><button class="choice-card create-choice ${draft.createTarget ? "selected" : ""}" @click=${() => this.updatePlaylistWizard({ createTarget: true, targetPlaylist: null })}>${icon("plus")}<span><strong>Create a new playlist</strong><small>Use the source playlist name and artwork</small></span></button>${this.renderPlaylistChoices(this.targetPlaylistResults, draft.targetPlaylist, (playlist) => this.updatePlaylistWizard({ targetPlaylist: playlist, createTarget: false }), "target")}${draft.targetNextCursor ? html`<button class="load-more" ?disabled=${draft.loading} @click=${() => this.loadMoreTargetPlaylists()}>Load more playlists</button>` : nothing}` : nothing}
    </div>`;
  }

  renderPlaylistChoices(items, selected, choose, side) {
    if (this.playlistWizard.loading) return html`<div class="empty">Loading playlists…</div>`;
    if (!items.length) return html`<div class="empty">No playlists found. Try a search or choose another account.</div>`;
    return html`<div class="playlist-choice-grid">${items.map((playlist) => {
      const id = String(playlist.id || playlist.Id);
      return html`<button class="playlist-choice ${String(selected?.id || selected?.Id) === id ? "selected" : ""}" @click=${() => choose(playlist)}>${this.renderPlaylistArtwork(playlist, side)}<span><strong>${playlist.name || playlist.Name}</strong><small>${display(playlist.owner || playlist.Owner, `${display(playlist.trackCount || playlist.TrackCount, 0)} tracks`)}</small></span></button>`;
    })}</div>`;
  }

  playlistArtworkUrl(playlist) {
    return playlist?.artworkUrl || playlist?.ArtworkUrl
      || playlist?.imageUrl || playlist?.ImageUrl
      || playlist?.iconUrl || playlist?.IconUrl
      || playlist?.coverUrl || playlist?.CoverUrl
      || "";
  }

  renderPlaylistArtwork(playlist, side, large = false, providerOverride = "") {
    const artwork = this.playlistArtworkUrl(playlist);
    if (artwork) return html`<img src=${artwork} alt="" loading="lazy">`;
    const draft = this.playlistWizard;
    const source = this.playlistSources.find((item) => String(item.id || item.Id) === draft.sourceAccountId);
    const target = this.mediaTargets.find((item) => String(item.id || item.Id) === draft.targetIdentityId);
    const provider = providerOverride || (side === "source"
      ? source?.providerId || source?.ProviderId
      : target?.protocol || target?.Protocol);
    const mark = String(provider).toLowerCase() === "jellyfin"
      ? this.renderProviderLogo("jellyfin", large ? "hero" : "tiny")
      : side === "source" && provider
        ? this.renderProviderLogo(provider, large ? "hero" : "tiny")
        : icon("library", large ? 28 : 20);
    return html`<span class="playlist-art-fallback ${large ? "large" : ""}">${mark}</span>`;
  }

  renderPlaylistBehaviorStep() {
    const draft = this.playlistWizard;
    const check = (key, label) => html`<label class="inline-check"><input type="checkbox" .checked=${draft[key]} @change=${(event) => this.updatePlaylistWizard({ [key]: event.target.checked })}> ${label}</label>`;
    return html`<div class="wizard-step-panel"><div class="step-copy"><h4>Choose synchronization behavior</h4><p class="muted">These settings can be changed after the link is created.</p></div><div class="playlist-link-form-grid">
      <div class="form-row"><label>Playback behavior</label><select .value=${draft.mode} @change=${(event) => this.updatePlaylistWizard({ mode: event.target.value })}><option value="virtual">Stream matched tracks</option><option value="materialized">Use local tracks only</option><option value="hybrid">Both</option></select></div>
      <div class="form-row"><label>Update behavior</label><select .value=${draft.materializationMode} @change=${(event) => this.updatePlaylistWizard({ materializationMode: event.target.value })}><option value="reconcile">Keep synchronized</option><option value="recreate">Rebuild target each run</option></select></div>
      <div class="form-row"><label>Run</label><select .value=${draft.trigger} @change=${(event) => this.updatePlaylistWizard({ trigger: event.target.value })}><option value="manual">Manually</option><option value="scheduled">On a schedule</option></select></div>
      ${draft.trigger === "scheduled" ? html`<div class="form-row"><label>Schedule (cron)</label><input .value=${draft.cronExpression} @input=${(event) => this.updatePlaylistWizard({ cronExpression: event.target.value })}><label>Time zone</label><input .value=${draft.timeZoneId} @input=${(event) => this.updatePlaylistWizard({ timeZoneId: event.target.value })}></div>` : nothing}
    </div><div class="playlist-link-options">${check("syncName", "Copy name")}${check("syncDescription", "Copy description")}${check("syncArtwork", "Copy artwork")}${check("preserveManualEntries", "Keep manually added songs")}${check("mirrorStaleEntries", "Remove stale synchronized songs")}</div></div>`;
  }

  async runPlaylistDryRunPreview() {
    if (this.playlistDryRunBusy) return;
    const draft = this.playlistWizard;
    const account = this.playlistSources.find((item) => String(item.id || item.Id) === draft.sourceAccountId);
    const target = this.mediaTargets.find((item) => String(item.id || item.Id) === draft.targetIdentityId);
    if (!account || !target || !draft.sourcePlaylist || (!draft.createTarget && !draft.targetPlaylist)) {
      this.toast("Choose both a source and target playlist before previewing", "error");
      return;
    }
    const protocol = target.protocol || target.Protocol;
    const backendInstanceId = target.backendInstanceId || target.BackendInstanceId;
    this.playlistDryRunBusy = true;
    this.playlistDryRunPreview = null;
    this.requestUpdate();
    try {
      this.playlistDryRunPreview = await requestJson("/api/admin/playlist-preview", jsonBody({
        providerAccountId: account.id || account.Id,
        playlistId: draft.sourcePlaylist.id || draft.sourcePlaylist.Id,
        libraryScopeId: account.libraryScopeId || account.LibraryScopeId || `${protocol}:${backendInstanceId}`,
        targetPlaylistId: draft.createTarget ? null : draft.targetPlaylist.id || draft.targetPlaylist.Id,
      }), "Failed to preview playlist");
    } catch (error) {
      this.toast(error?.message || "Failed to preview playlist", "error");
    } finally {
      this.playlistDryRunBusy = false;
      this.requestUpdate();
    }
  }

  renderPlaylistDryRunResult() {
    const preview = this.playlistDryRunPreview;
    if (!preview) return nothing;
    const summary = preview.summary || preview.Summary || {};
    const entries = asArray(preview.entries || preview.Entries);
    return html`<section class="playlist-dry-run" aria-live="polite">
      <div class="playlist-dry-run-heading"><div><span class="eyebrow">No-write preview</span><h4>${preview.source?.name || preview.Source?.Name || "Playlist preview"}</h4></div><span class="chip success">No changes made</span></div>
      <div class="playlist-dry-run-metrics">
        ${[["Tracks", summary.total], ["Local matches", summary.localMatches], ["Provider matches", summary.providerMatches], ["Suggested", summary.suggested], ["Ambiguous", summary.ambiguous], ["Unresolved", summary.unresolved], ["Estimated adds", summary.estimatedAdds]].map(([label, value]) => html`<div><strong>${value ?? "-"}</strong><span>${label}</span></div>`)}
      </div>
      ${summary.providerMatches == null ? html`<p class="playlist-dry-run-note">Provider fallback availability is evaluated separately and is not counted as a local match.</p>` : nothing}
      ${entries.length ? html`<div class="playlist-dry-run-entries" role="list" aria-label="Previewed track decisions">
        ${entries.map((entry) => html`<div role="listitem"><span class="playlist-dry-run-position">${Number(entry.position ?? 0) + 1}</span><div><strong>${entry.title || "Unknown track"}</strong><small>${asArray(entry.artists).join(", ") || "Unknown artist"}</small></div><span class=${`chip ${entry.state || "unresolved"}`}>${titleCase(entry.state || "unresolved")}</span></div>`)}
      </div>${preview.entriesTruncated ? html`<p class="playlist-dry-run-note">Showing the first ${preview.returnedEntries} decisions.</p>` : nothing}` : nothing}
    </section>`;
  }

  renderPlaylistReviewStep() {
    const draft = this.playlistWizard;
    const sourceAccount = this.playlistSources.find((item) => String(item.id || item.Id) === draft.sourceAccountId);
    const target = this.mediaTargets.find((item) => String(item.id || item.Id) === draft.targetIdentityId);
    return html`<div class="wizard-step-panel"><div class="step-copy"><h4>Review the playlist</h4><p class="muted">Allstarr will match in your configured provider order and preserve source ordering.</p></div><div class="playlist-review-pair">
      ${this.renderPlaylistReviewCard("Source", draft.sourcePlaylist, sourceAccount?.providerId || sourceAccount?.ProviderId, "source")}
      <span class="review-arrow">→</span>
      ${this.renderPlaylistReviewCard("Target", draft.createTarget ? { ...draft.sourcePlaylist, name: draft.sourcePlaylist?.name || draft.sourcePlaylist?.Name } : draft.targetPlaylist, target?.protocol || target?.Protocol, "target")}
    </div><dl class="review-facts"><div><dt>Playback</dt><dd>${draft.mode === "virtual" ? "Stream matched tracks" : draft.mode === "materialized" ? "Local tracks only" : "Streaming and local"}</dd></div><div><dt>Updates</dt><dd>${draft.materializationMode === "recreate" ? "Rebuild target" : "Keep synchronized"}</dd></div><div><dt>Run</dt><dd>${draft.trigger === "scheduled" ? `${draft.cronExpression} · ${draft.timeZoneId}` : "Manually"}</dd></div></dl>
      <div class="playlist-preview-actions"><button class="secondary" ?disabled=${this.playlistDryRunBusy} @click=${() => this.runPlaylistDryRunPreview()}>${this.playlistDryRunBusy ? "Previewing..." : "Run no-write preview"}</button></div>
      ${this.renderPlaylistDryRunResult()}
    </div>`;
  }

  renderPlaylistReviewCard(label, playlist, provider, side) {
    return html`<article class="playlist-review-card"><span class="eyebrow">${label}</span>${this.renderPlaylistArtwork(playlist, side, true, provider)}<div><strong>${playlist?.name || playlist?.Name || "New playlist"}</strong><small>${providerDisplayName(provider || "media server", this.schema?.providers)}</small></div></article>`;
  }

  playlistWizardStepComplete() {
    const draft = this.playlistWizard;
    if (draft.step === 0) return Boolean(draft.sourceAccountId && draft.sourcePlaylist);
    if (draft.step === 1) return Boolean(draft.targetIdentityId && (draft.createTarget || draft.targetPlaylist));
    if (draft.step === 2) return draft.trigger !== "scheduled" || Boolean(draft.cronExpression.trim() && draft.timeZoneId.trim());
    return true;
  }

  renderPlaylistLinkRow(link) {
    const id = link.id || link.Id;
    const provider = link.provider || link.Provider || link.providerId || link.ProviderId || "provider";
    const target = link.targetProtocol || link.TargetProtocol || link.targetBackendType || link.TargetBackendType || link.backendType || link.BackendType;
    const enabled = Boolean(link.enabled ?? link.Enabled ?? true);
    const state = enabled ? link.lastRunState || link.LastRunState || link.state || link.State || "ready" : "paused";
    return html`<tr>
      <td class="mobile-primary" data-label="Playlist"><div class="provider-brand">${this.renderPlaylistArtwork(link, "source", false, provider)}<div><strong>${link.name || link.Name || "Playlist"}</strong>${link.description || link.Description ? html`<small>${link.description || link.Description}</small>` : nothing}</div></div></td>
      <td data-label="Source">${providerDisplayName(provider, this.schema?.providers)}</td>
      <td data-label="Target">${String(target).toLowerCase() === "subsonic" ? "Navidrome / Subsonic" : display(target)}</td>
      <td data-label="Mode">${titleCase(link.mode || link.Mode)} · ${titleCase(link.materializationMode || link.MaterializationMode)}</td>
      <td data-label="Last run"><span class="status-chip ${String(state).toLowerCase()}">${titleCase(state)}</span><div class="muted">${formatDate(link.lastRunAt || link.LastRunAt)}</div></td>
      <td class="row-actions mobile-actions" data-label="Actions"><button @click=${() => this.loadPlaylistLinkPreview(id)}>Preview</button><button class="primary" ?disabled=${!enabled} @click=${() => this.runPlaylistLink(id)}>Run now</button><details class="action-menu playlist-action-menu"><summary class="icon-button" aria-label="More actions for ${link.name || link.Name || "playlist"}">${icon("more")}</summary><div><button @click=${() => { this.editingPlaylistLink = link; }}>Edit behavior</button><button @click=${() => this.togglePlaylistLink(link)}>${enabled ? "Pause" : "Resume"}</button><button ?disabled=${!enabled} @click=${() => this.loadPlaylistLinkPreview(id, true)}>Refresh source</button>${String(target).toLowerCase() === "subsonic" ? html`<details><summary>Rotate credentials</summary><form class="form-stack" @submit=${(event) => this.savePlaylistBackendCredential(link, event)}><input name="username" aria-label="Subsonic username" autocomplete="username" required><input name="password" aria-label="Subsonic password" type="password" autocomplete="new-password" required><button type="submit">Save encrypted credentials</button></form></details>` : nothing}<button class="danger-text" @click=${() => this.deletePlaylistLink(link)}>Remove playlist</button></div></details></td>
    </tr>`;
  }

  renderPlaylistLinkPreview() {
    if (!this.selectedPlaylistLinkId) {
      return nothing;
    }
    if (!this.playlistLinkPreview) {
      return html`<div class="modal-backdrop playlist-preview-backdrop" @click=${() => { this.selectedPlaylistLinkId = ""; }}><aside class="panel playlist-preview" aria-live="polite" @click=${(event) => event.stopPropagation()}><p>Loading preview…</p></aside></div>`;
    }
    const preview = this.playlistLinkPreview;
    const entries = asArray(preview.entries || preview.Entries);
    const included = entries.filter((entry) => String(entry.status || entry.Status).toLowerCase() === "included").length;
    const conflicts = entries.filter((entry) => ["ambiguous", "conflict"].includes(String(entry.status || entry.Status).toLowerCase())).length;
    return html`<div class="modal-backdrop playlist-preview-backdrop" @click=${() => { this.selectedPlaylistLinkId = ""; this.playlistLinkPreview = null; }}><aside class="panel playlist-preview" role="dialog" aria-modal="true" aria-label="Playlist preview" aria-live="polite" @click=${(event) => event.stopPropagation()}>
      <div class="view-header"><div><h3>${preview.name || preview.Name || "Playlist preview"}</h3><p>${included} included · ${entries.length - included} skipped · ${conflicts} conflicts</p></div><button class="ghost" aria-label="Close preview" @click=${() => { this.selectedPlaylistLinkId = ""; this.playlistLinkPreview = null; }}>Close</button></div>
      <div class="actions"><button class="primary" @click=${() => this.runPlaylistLink(this.selectedPlaylistLinkId)}>Run now</button></div>
      <ol class="playlist-preview-list">${entries.length ? entries.map((entry) => this.renderPlaylistPreviewEntry(entry)) : html`<li class="empty">The source playlist has no tracks.</li>`}</ol>
    </aside></div>`;
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

  createPlaylistLink = async (runNow = false) => {
    const draft = this.playlistWizard;
    const account = this.playlistSources.find((item) => String(item.id || item.Id) === draft.sourceAccountId);
    const target = this.mediaTargets.find((item) => String(item.id || item.Id) === draft.targetIdentityId);
    if (!account || !target || !draft.sourcePlaylist || (!draft.createTarget && !draft.targetPlaylist)) {
      this.playlistWizard = { ...draft, error: "Choose both a source and target playlist before creating the link." };
      return;
    }
    this.updatePlaylistWizard({ loading: true });
    try {
      const protocol = target.protocol || target.Protocol;
      const backendInstanceId = target.backendInstanceId || target.BackendInstanceId;
      const payload = {
        providerAccountId: account.id || account.Id,
        sourceProviderId: String(account.providerId || account.ProviderId).toLowerCase(),
        sourcePlaylistId: draft.sourcePlaylist.id || draft.sourcePlaylist.Id,
        libraryScopeId: account.libraryScopeId || account.LibraryScopeId || `${protocol}:${backendInstanceId}`,
        targetProtocol: protocol,
        targetBackendInstanceId: backendInstanceId,
        mode: draft.mode,
        materializationMode: draft.materializationMode,
        targetPlaylistId: draft.createTarget ? null : draft.targetPlaylist.id || draft.targetPlaylist.Id,
        targetCredentialReferenceId: target.credentialReferenceId || target.CredentialReferenceId || null,
        mirrorStaleEntries: draft.mirrorStaleEntries,
        preserveManualEntries: draft.preserveManualEntries,
        syncName: draft.syncName,
        syncDescription: draft.syncDescription,
        syncArtwork: draft.syncArtwork,
      };
      const created = await API.createPlaylistLink(payload);
      const linkId = created.id || created.Id || created.playlistLink?.id || created.PlaylistLink?.Id;
      if (draft.trigger === "scheduled" && linkId) {
        await API.createPlaylistSchedule(linkId, { cronExpression: draft.cronExpression.trim(), timeZoneId: draft.timeZoneId.trim(), overlapPolicy: "skip", misfirePolicy: "runOnce", enabled: true });
      }
      if (runNow && linkId) await API.runPlaylistLink(linkId);
      this.playlistWizard = this.newPlaylistWizardDraft();
      this.sourcePlaylistResults = [];
      this.targetPlaylistResults = [];
      await this.loadPlaylistLinks();
      this.toast(runNow ? "Playlist link created and sync queued" : "Playlist link created");
    } catch (error) {
      this.playlistWizard = { ...this.playlistWizard, loading: false, error: error.message };
    }
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

  async deletePlaylistLink(link) {
    const id = link.id || link.Id;
    const name = link.name || link.Name || "this playlist";
    if (!id || !window.confirm(`Remove ${name}? This stops future synchronization and removes its Allstarr history. The target playlist and reusable track matches are kept.`)) return;
    try {
      await API.deletePlaylistLink(id, link.revision ?? link.Revision ?? 0);
      if (String(this.selectedPlaylistLinkId) === String(id)) {
        this.selectedPlaylistLinkId = "";
        this.playlistLinkPreview = null;
      }
      await this.loadPlaylistLinks();
      this.toast("Playlist removed; target playlist kept");
    } catch (error) {
      await this.loadPlaylistLinks().catch(() => {});
      this.toast(error.message || "Playlist could not be removed", "error");
    }
  }

  async togglePlaylistLink(link) {
    const id = link.id || link.Id;
    const enabled = Boolean(link.enabled ?? link.Enabled ?? true);
    if (!id) return;
    try {
      await API.setPlaylistLinkEnabled(id, link.revision ?? link.Revision ?? 0, !enabled);
      await this.loadPlaylistLinks();
      this.toast(enabled ? "Playlist paused" : "Playlist resumed");
    } catch (error) {
      await this.loadPlaylistLinks().catch(() => {});
      this.toast(error.message || "Playlist state could not be updated", "error");
    }
  }

  async savePlaylistBehavior(event) {
    event.preventDefault();
    const link = this.editingPlaylistLink;
    if (!link) return;
    const data = new FormData(event.currentTarget);
    try {
      await API.updatePlaylistLink(link.id || link.Id, {
        expectedRevision: link.revision ?? link.Revision ?? 0,
        mode: String(data.get("mode") || "virtual"),
        materializationMode: String(data.get("materializationMode") || "reconcile"),
        scheduleId: link.scheduleId || link.ScheduleId || null,
        targetPlaylistId: link.targetPlaylistId || link.TargetPlaylistId || null,
        targetCredentialReferenceId: link.targetCredentialReferenceId || link.TargetCredentialReferenceId || null,
        mirrorStaleEntries: data.has("mirrorStaleEntries"),
        preserveManualEntries: data.has("preserveManualEntries"),
        syncName: data.has("syncName"),
        syncDescription: data.has("syncDescription"),
        syncArtwork: data.has("syncArtwork"),
        ruleVersion: link.ruleVersion || link.RuleVersion || "playlist-rules-v1",
        policyVersion: link.policyVersion || link.PolicyVersion || "playlist-policy-v1",
      });
      this.editingPlaylistLink = null;
      await this.loadPlaylistLinks();
      this.toast("Playlist behavior updated");
    } catch (error) {
      await this.loadPlaylistLinks().catch(() => {});
      this.toast(error.message || "Playlist behavior could not be updated", "error");
    }
  }

  renderPlaylistBehaviorDialog() {
    const link = this.editingPlaylistLink;
    if (!link) return nothing;
    const close = () => { this.editingPlaylistLink = null; };
    const mode = String(link.mode || link.Mode || "virtual").toLowerCase();
    const materialization = String(link.materializationMode || link.MaterializationMode || "reconcile").toLowerCase();
    return html`<div class="modal-backdrop" @click=${(event) => { if (event.target === event.currentTarget) close(); }} @keydown=${(event) => this.handleDialogKeydown(event, close)}>
      <section class="panel dialog" role="dialog" aria-modal="true" aria-labelledby="playlist-behavior-title" tabindex="-1">
        <div class="dialog-header"><div><h3 id="playlist-behavior-title">Playlist behavior</h3><p>${link.name || link.Name || "Playlist"}</p></div><button class="icon-button ghost" type="button" aria-label="Close behavior editor" @click=${close}>${icon("close")}</button></div>
        <form class="config-grid" @submit=${(event) => this.savePlaylistBehavior(event)}>
          <label class="config-field"><span>Playback</span><select name="mode" .value=${mode}><option value="virtual">Stream matched tracks</option><option value="hybrid">Streaming and local tracks</option><option value="materialized">Local tracks only</option></select></label>
          <label class="config-field"><span>Target updates</span><select name="materializationMode" .value=${materialization}><option value="reconcile">Keep synchronized</option><option value="recreate">Rebuild target each run</option></select></label>
          <label class="toggle-row"><input type="checkbox" name="preserveManualEntries" .checked=${Boolean(link.preserveManualEntries ?? link.PreserveManualEntries)}><span>Keep manually added target tracks</span></label>
          <label class="toggle-row"><input type="checkbox" name="mirrorStaleEntries" .checked=${Boolean(link.mirrorStaleEntries ?? link.MirrorStaleEntries)}><span>Remove tracks no longer in the source</span></label>
          <fieldset class="config-field full-span"><legend>Keep target details synchronized</legend><label class="compact-check"><input type="checkbox" name="syncName" .checked=${Boolean(link.syncName ?? link.SyncName)}> Name</label><label class="compact-check"><input type="checkbox" name="syncDescription" .checked=${Boolean(link.syncDescription ?? link.SyncDescription)}> Description</label><label class="compact-check"><input type="checkbox" name="syncArtwork" .checked=${Boolean(link.syncArtwork ?? link.SyncArtwork)}> Artwork</label></fieldset>
          <div class="dialog-actions full-span"><button type="button" @click=${close}>Cancel</button><button class="primary" type="submit">Save behavior</button></div>
        </form>
      </section>
    </div>`;
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
    const query = this.injectedSearch.trim().toLowerCase();
    const filtered = playlists.filter((playlist) => {
      const status = String(playlist.syncStatus || "pending").toLowerCase();
      const scheduled = Boolean(playlist.syncSchedule);
      return (!query || `${playlist.name} ${playlist.id}`.toLowerCase().includes(query)) &&
        (!this.injectedStatusFilter || status === this.injectedStatusFilter) &&
        (!this.injectedScheduleFilter || (this.injectedScheduleFilter === "scheduled" ? scheduled : !scheduled));
    });
    const pageCount = Math.max(1, Math.ceil(filtered.length / this.injectedPageSize));
    const page = Math.min(this.injectedPage, pageCount);
    const visible = filtered.slice((page - 1) * this.injectedPageSize, page * this.injectedPageSize);
    const paginationPages = pageCount <= 7
      ? Array.from({ length: pageCount }, (_, index) => index + 1)
      : [...new Set([1, page - 1, page, page + 1, pageCount].filter((item) => item >= 1 && item <= pageCount))].sort((a, b) => a - b);
    const paginationItems = paginationPages.flatMap((pageNumber, index) =>
      index > 0 && pageNumber - paginationPages[index - 1] > 1
        ? [`gap-${pageNumber}`, pageNumber]
        : [pageNumber]);
    const selected = this.selectedInjectedPlaylists;
    const updateSelection = (name, checked) => {
      const next = new Set(selected);
      checked ? next.add(name) : next.delete(name);
      this.selectedInjectedPlaylists = next;
    };
    return html`
      <div class="injected-page-heading">
        <div><h3>Imported playlists</h3><p>Playlists retained from the earlier configuration format.</p></div>
        <div class="actions injected-heading-actions"><button @click=${() => { this.injectedAddOpen = true; }}>${icon("plus")}<span>Add playlist</span></button><button class="primary" @click=${async () => {
          const names = selected.size ? [...selected] : playlists.map((item) => item.name);
          if (selected.size) {
            await Promise.all(names.map(async (name) => {
              await API.refreshPlaylist(name);
              await API.matchPlaylist(name);
            }));
            await this.loadPlaylists(true);
            this.toast(`Refreshed and rematched ${names.length} ${names.length === 1 ? "playlist" : "playlists"}`);
          } else {
            await API.refreshPlaylists();
            await API.matchAllPlaylists();
            await this.loadPlaylists(true);
            this.toast("Playlist rematching queued. Progress appears in the operation center.");
          }
        }}>${icon("refresh")}<span>Sync ${selected.size ? `${selected.size} selected` : "all now"}</span></button></div>
      </div>
      <div class="playlist-toolbar">
        <label class="search-control">${icon("search")}<input aria-label="Search playlists" placeholder="Search playlists…" .value=${this.injectedSearch} @input=${(event) => { this.injectedSearch = event.target.value; this.injectedPage = 1; }}></label>
        <select aria-label="Filter by status" .value=${this.injectedStatusFilter} @change=${(event) => { this.injectedStatusFilter = event.target.value; this.injectedPage = 1; }}><option value="">All statuses</option><option value="synced">Synced</option><option value="partial">Partial</option><option value="needs_matching">Needs matching</option><option value="pending">Pending</option></select>
        <select aria-label="Filter by schedule" .value=${this.injectedScheduleFilter} @change=${(event) => { this.injectedScheduleFilter = event.target.value; this.injectedPage = 1; }}><option value="">All schedules</option><option value="scheduled">Scheduled</option><option value="manual">Manual</option></select>
      </div>
      ${this.renderInjectedPlaylistDetails()}
      <div class="table-wrap injected-playlist-table">
        <div class="injected-table-wrap" role="region" aria-label="Managed playlists" tabindex="0">
          <table class="injected-data-table">
            <colgroup>
              <col class="select-col">
              <col class="playlist-col">
              <col class="tracks-col">
              <col class="matched-col">
              <col class="unmatched-col">
              <col class="schedule-col">
              <col class="status-col">
              <col class="last-sync-col">
              <col class="actions-col">
            </colgroup>
            <thead><tr>
              <th scope="col"><input type="checkbox" aria-label="Select visible playlists" .checked=${visible.length > 0 && visible.every((item) => selected.has(item.name))} @change=${(event) => { const next = new Set(selected); visible.forEach((item) => event.target.checked ? next.add(item.name) : next.delete(item.name)); this.selectedInjectedPlaylists = next; }}></th>
              <th scope="col">Playlist</th><th scope="col">Tracks</th><th scope="col">Matched</th><th scope="col">Unmatched</th><th scope="col">Schedule</th><th scope="col">Status</th><th scope="col">Last sync</th><th scope="col">Actions</th>
            </tr></thead>
            <tbody>
          ${visible.length ? visible.map((playlist) => {
            const matched = Number(playlist.matchedTracks ?? Number(playlist.localTracks || 0) + Number(playlist.externalTracks || 0));
            const unmatched = Number(playlist.unmatchedTracks ?? Math.max(0, Number(playlist.trackCount || 0) - matched));
            const matchPercent = Number(playlist.matchPercent ?? (playlist.trackCount ? matched * 100 / playlist.trackCount : 0));
            const status = playlist.syncStatus || (unmatched === 0 && playlist.trackCount ? "synced" : matched === 0 ? "needs_matching" : "partial");
            const openRow = (event) => {
              if (event.target.closest("button, input, details, summary, a, select")) return;
              this.openInjectedPlaylist(playlist.name);
            };
            return html`<tr class="injected-table-row-interactive" tabindex="0"
              aria-label="Open ${playlist.name} playlist details" @click=${openRow}
              @keydown=${(event) => { if ((event.key === "Enter" || event.key === " ") && !event.target.closest("button, input, details, summary, a, select")) { event.preventDefault(); this.openInjectedPlaylist(playlist.name); } }}>
              <td class="selection-cell"><input type="checkbox" aria-label="Select ${playlist.name}" .checked=${selected.has(playlist.name)} @change=${(event) => updateSelection(playlist.name, event.target.checked)}></td>
              <td class="playlist-main-cell" data-label="Playlist"><span class="playlist-cell playlist-name-button"><img src=${playlist.artworkUrl || "/images/playlist-placeholder.svg"} alt=""><span><strong>${playlist.name}</strong><small>Managed playlist</small></span></span></td>
              <td data-label="Tracks">${display(playlist.trackCount, 0)}</td>
              <td data-label="Matched"><strong>${matched}</strong><small>${matchPercent.toFixed(1)}%</small></td>
              <td data-label="Unmatched"><strong>${unmatched}</strong><small>${(100 - matchPercent).toFixed(1)}%</small></td>
              <td data-label="Schedule"><span class="schedule-cell">${icon("clock", 15)}<span>${formatSchedule(playlist.syncSchedule)}<small>${playlist.nextSyncAt ? `Next ${formatRelativeTime(playlist.nextSyncAt)}` : ""}</small></span></span></td>
              <td data-label="Status"><span class="status-chip ${status}">${titleCase(status)}</span></td>
              <td data-label="Last sync">${playlist.lastSyncAt ? formatRelativeTime(playlist.lastSyncAt) : "Not synced yet"}</td>
              <td class="actions-cell" data-label="Actions"><div class="playlist-row-actions"><button class="primary compact" @click=${() => this.syncInjectedPlaylist(playlist.name)}>Sync now</button><details class="action-menu playlist-action-menu"><summary class="icon-button" aria-label="More actions for ${playlist.name}">${icon("more")}</summary><div><button @click=${async () => { await API.refreshPlaylist(playlist.name); this.toast("Source refresh requested"); }}>Refresh source</button><button @click=${async () => { await API.matchPlaylist(playlist.name); this.toast("Rematching requested"); }}>Rematch</button><button @click=${async () => { await API.clearPlaylistCache(playlist.name); this.toast("Cache cleared"); }}>Clear cache</button><button class="danger-text" @click=${async () => { if (!window.confirm(`Remove ${playlist.name}?`)) return; await API.removePlaylist(playlist.name); await this.loadPlaylists(true); this.toast("Playlist removed"); }}>Remove</button></div></details></div></td>
            </tr>`;
          }) : html`<tr><td colspan="9"><div class="empty">No playlists match these filters.</div></td></tr>`}
            </tbody>
          </table>
        </div>
        <div class="table-pagination">
          <span>Showing ${filtered.length ? (page - 1) * this.injectedPageSize + 1 : 0}–${Math.min(page * this.injectedPageSize, filtered.length)} of ${filtered.length} playlists</span>
          <div class="table-pagination-controls" aria-label="Playlist pages">
            <button class="icon-button" aria-label="Previous page" ?disabled=${page <= 1} @click=${() => { this.injectedPage = page - 1; }}>${icon("chevronLeft")}</button>
            ${paginationItems.map((item) => typeof item === "string" ? html`<span class="pagination-gap" aria-hidden="true">…</span>` : html`
              <button
                class="page-number ${item === page ? "active" : ""}"
                aria-label="Page ${item}"
                aria-current=${item === page ? "page" : nothing}
                ?disabled=${item === page}
                @click=${() => { this.injectedPage = item; }}
              >${item}</button>
            `)}
            <button class="icon-button" aria-label="Next page" ?disabled=${page >= pageCount} @click=${() => { this.injectedPage = page + 1; }}>${icon("chevronRight")}</button>
          </div>
        </div>
      </div>
      ${this.renderInjectedAddModal()}
    `;
  }

  async syncInjectedPlaylist(name) {
    await API.refreshPlaylist(name);
    await API.matchPlaylist(name);
    this.toast(`Sync started for ${name}`);
  }

  renderInjectedAddModal() {
    if (!this.injectedAddOpen) return nothing;
    const close = () => { this.injectedAddOpen = false; };
    return html`<div class="modal-backdrop" @click=${(event) => { if (event.target === event.currentTarget) close(); }} @keydown=${(event) => this.handleDialogKeydown(event, close)}><section class="panel compact-dialog" role="dialog" aria-modal="true" aria-labelledby="add-injected-title" tabindex="-1"><div class="dialog-header"><div><h3 id="add-injected-title">Add a playlist</h3><p>Connect a playlist to your media server.</p></div><button class="icon-button ghost" @click=${close} aria-label="Close">${icon("close")}</button></div><form class="form-stack" @submit=${async (event) => { await this.addInjectedPlaylist(event); close(); }}><div class="form-row"><label>Name</label><input name="name" required autofocus></div><div class="form-row"><label>Spotify ID</label><input name="spotifyId" required></div><div class="actions dialog-actions"><button type="button" @click=${close}>Cancel</button><button class="primary">Add playlist</button></div></form></section></div>`;
  }

  async openInjectedPlaylist(name) {
    this.selectedInjectedPlaylist = String(name || "");
    this.injectedPlaylistDetails = null;
    this.injectedTrackMenuId = "";
    this.injectedTrackEditor = null;
    this.injectedTrackFilter = "";
    await this.updateComplete;
    this.renderRoot.querySelector(".injected-playlist-dialog")?.focus();
    try {
      this.injectedPlaylistDetails = await API.playlistTracks(this.selectedInjectedPlaylist);
    } catch (error) {
      this.selectedInjectedPlaylist = "";
      this.toast(error.message, "error");
    }
  }

  renderInjectedPlaylistDetails() {
    if (!this.selectedInjectedPlaylist) return nothing;
    const details = this.injectedPlaylistDetails;
    const tracks = asArray(details?.tracks || details?.Tracks);
    const query = this.injectedTrackFilter.trim().toLowerCase();
    const filtered = tracks.filter((track) => !query || `${track.title} ${asArray(track.artists).join(" ")} ${track.album || ""}`.toLowerCase().includes(query));
    const playable = Number(details?.totalPlayable ?? details?.TotalPlayable ?? 0);
    const localTracks = Number(details?.localTracks ?? details?.LocalTracks ?? 0);
    const externalTracks = Number(details?.externalTracks ?? details?.ExternalTracks ?? 0);
    const unmatchedTracks = Number(details?.unmatchedTracks ?? details?.UnmatchedTracks ?? Math.max(0, tracks.length - playable));
    const lastSourceRefreshAt = details?.lastSourceRefreshAt || details?.LastSourceRefreshAt;
    const lastSuccessfulSyncAt = details?.lastSuccessfulSyncAt || details?.LastSuccessfulSyncAt;
    const nextSyncAt = details?.nextSyncAt || details?.NextSyncAt;
    const matchStatus = details?.matchStatus || details?.MatchStatus || "pending";
    const sourceProvider = details?.sourceProvider || "unknown";
    const targetBackend = details?.targetBackend || String(this.status?.backendType || this.config?.backendType || "unknown").toLowerCase();
    const close = () => {
      this.selectedInjectedPlaylist = "";
      this.injectedPlaylistDetails = null;
      this.injectedTrackMenuId = "";
      this.injectedTrackEditor = null;
      this.selectedTrackDetails = null;
      this.selectedTrackContext = null;
    };
    return html`<div class="modal-backdrop injected-playlist-backdrop"
      @click=${(event) => { if (event.target === event.currentTarget) close(); }}
      @keydown=${(event) => {
        if (event.key === "Tab") {
          this.handleDialogKeydown(event, close);
          return;
        }
        if (event.key !== "Escape") return;
        if (this.injectedTrackEditor) this.injectedTrackEditor = null;
        else if (this.injectedTrackMenuId) this.injectedTrackMenuId = "";
        else close();
      }}>
      <section class="panel injected-playlist-dialog redesigned-dialog" role="dialog" aria-modal="true" data-testid="playlist-dialog"
        aria-labelledby="injected-playlist-title" tabindex="-1">
        <div class="playlist-dialog-hero">
          <img class="playlist-hero-art" src=${details?.artworkUrl || "/images/playlist-placeholder.svg"} alt="">
          <div class="playlist-hero-content"><h3 id="injected-playlist-title">${display(details?.name || details?.Name || this.selectedInjectedPlaylist)}</h3><p>${details ? `${tracks.length} tracks in provider order` : "Loading tracks…"}</p>
            <div class="playlist-hero-stats">
              <div>${this.renderProviderLogo(sourceProvider, "small")}<span><small>Source provider</small><strong>${providerDisplayName(sourceProvider, this.schema?.providers)}</strong></span></div>
              <div><span class="hero-stat-icon">${icon("check")}</span><span><small>Playable</small><strong>${playable} / ${tracks.length}</strong></span></div>
              <div><span class="hero-stat-icon">${icon("library")}</span><span><small>Target</small><strong>${titleCase(targetBackend)}</strong></span></div>
            </div>
          </div>
          <button class="icon-button ghost dialog-close" @click=${close} aria-label="Close playlist tracks">${icon("close")}</button>
          ${details ? html`<div class="playlist-operation-row"><div class="playlist-operation-summary" aria-label="Playlist synchronization details">
            <span><small>Local</small><strong>${localTracks}</strong></span>
            <span><small>External</small><strong>${externalTracks}</strong></span>
            <span class=${unmatchedTracks ? "needs-attention" : ""}><small>Unmatched</small><strong>${unmatchedTracks}</strong></span>
            <span><small>Source refreshed</small><strong>${lastSourceRefreshAt ? formatRelativeTime(lastSourceRefreshAt) : "Not recorded"}</strong></span>
            <span><small>Last synced</small><strong>${lastSuccessfulSyncAt ? formatRelativeTime(lastSuccessfulSyncAt) : "Not synced yet"}</strong></span>
            <span><small>Next rematch</small><strong>${nextSyncAt ? formatRelativeTime(nextSyncAt) : "Manual only"}</strong></span>
          </div><button class="primary compact playlist-rematch-action" @click=${async () => {
            await this.syncInjectedPlaylist(this.selectedInjectedPlaylist);
            await this.reloadInjectedPlaylistDetails();
          }}>${icon("refresh", 15)}<span>Sync & rematch</span></button></div>
          ${matchStatus === "rematch_required" ? html`<div class="playlist-match-notice" role="status">
            ${icon("warning", 17)}<span><strong>Current source snapshot needs matching</strong><small>The provider playlist changed after its last completed match. Run a sync now or wait for the next scheduled sync.</small></span>
          </div>` : nothing}` : nothing}
        </div>
        <div class="injected-playlist-scroll">
          ${this.renderInjectedTrackEditor()}
          ${details ? html`<label class="search-control playlist-track-search">${icon("search")}<input aria-label="Filter playlist tracks" placeholder="Filter tracks…" .value=${this.injectedTrackFilter} @input=${(event) => { this.injectedTrackFilter = event.target.value; }}></label>
            <div class="playlist-track-table">
              <div class="playlist-track-head"><span>#</span><span>Track</span><span>Artist</span><span>Album</span><span>Provider</span><span></span></div>
              ${filtered.length ? filtered.map((track, index) => html`<div class="playlist-track-row playlist-track-inspectable" role="button" tabindex="0"
                aria-label="Open mapping details for ${display(track.title, "track")}" @click=${() => this.openTrackDetails(track)}
                @keydown=${(event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); this.openTrackDetails(track); } }}>
                <span class="track-position" title=${track.sourcePosition ? `Provider position ${track.sourcePosition}` : ""}>${display(track.position ?? index + 1)}</span>
                <span class="track-title-cell"><img src=${track.albumArtUrl || "/placeholder.png"} alt=""><strong>${display(track.title)}</strong></span>
                <span class="track-artist-cell" data-label="Artist">${asArray(track.artists).join(", ") || "Unknown artist"}</span>
                <span class="track-album-cell" data-label="Album">${display(track.album)}</span>
                <span class="track-provider-cell" data-label="Provider"><span class="provider-badge ${track.matchState || "unmatched"}">${track.isLocal === true ? this.renderProviderLogo(targetBackend, "tiny") : track.isLocal === false ? this.renderProviderLogo(track.externalProvider, "tiny") : icon("warning", 14)}${track.isLocal === true ? titleCase(targetBackend) : track.isLocal === false ? providerDisplayName(track.externalProvider, this.schema?.providers) : "Unmatched"}</span></span>
                <span class="track-menu-cell" @click=${(event) => event.stopPropagation()} @keydown=${(event) => event.stopPropagation()}>${this.renderInjectedTrackMenu(track, index)}</span>
              </div>`) : html`<div class="empty compact">No tracks match this filter.</div>`}
            </div>
            <div class="playlist-track-summary">${query ? `Showing ${filtered.length} of ${tracks.length} tracks` : `All ${tracks.length} tracks`}</div>
          ` : html`<div class="empty">Loading playlist tracks…</div>`}
        </div>
      </section>
    </div>${this.renderTrackDetailsModal()}`;
  }

  async openTrackDetails(track) {
    const spotifyId = String(track?.spotifyId || "").trim();
    if (!spotifyId) {
      this.toast("This playlist entry has no source track identifier", "error");
      return;
    }
    this.selectedTrackContext = track;
    this.selectedTrackDetails = null;
    this.trackDetailsLoading = true;
    this.injectedTrackMenuId = "";
    try {
      this.selectedTrackDetails = await API.trackMappingDetails(spotifyId, String(track?.backendItemId || "").trim());
    } catch (error) {
      this.toast(error.message, "error");
    } finally {
      this.trackDetailsLoading = false;
      await this.updateComplete;
      this.renderRoot.querySelector(".track-details-dialog")?.focus();
    }
  }

  renderTrackDetailsModal() {
    const context = this.selectedTrackContext;
    if (!context) return nothing;
    const details = this.selectedTrackDetails;
    const metadata = details?.metadata || {};
    const identities = asArray(details?.providerIdentities);
    const localTracks = asArray(details?.localTracks);
    const history = asArray(details?.matchHistory);
    const activity = asArray(details?.activity);
    const artifacts = asArray(details?.cache?.artifacts);
    const legacy = details?.legacyMapping;
    const materializedBackendItemId = String(context?.backendItemId || "").trim();
    const close = () => {
      this.selectedTrackContext = null;
      this.selectedTrackDetails = null;
      this.trackDetailsLoading = false;
    };
    const durationMs = Number(details?.durationMilliseconds ?? context.durationMs ?? 0);
    const title = metadata.title || context.title || "Track details";
    const artist = metadata.artist || asArray(context.artists).join(", ") || "Unknown artist";
    const lastCached = details?.cache?.lastAudioCachedAt || details?.cache?.lastMetadataCachedAt;
    return html`<div class="modal-backdrop track-details-backdrop" @click=${(event) => { event.stopPropagation(); if (event.target === event.currentTarget) close(); }}
      @keydown=${(event) => { event.stopPropagation(); this.handleDialogKeydown(event, close); }}>
      <section class="panel track-details-dialog redesigned-dialog" role="dialog" aria-modal="true" aria-labelledby="track-details-title" tabindex="-1" data-testid="track-details-dialog">
        <header class="track-details-hero">
          <img src=${metadata.artworkUrl || context.albumArtUrl || "/placeholder.png"} alt="">
          <div><span class="eyebrow">Track details</span><h3 id="track-details-title">${display(title)}</h3><p>${display(artist)}${(metadata.album || context.album) ? ` · ${metadata.album || context.album}` : ""}</p>
            <div class="track-detail-badges"><span class="chip">${durationMs ? formatDuration(durationMs / 1000) : "Duration unavailable"}</span><span class="chip mono">Spotify ${display(context.spotifyId)}</span></div>
          </div>
          <button class="icon-button ghost dialog-close" @click=${close} aria-label="Close track mapping details">${icon("close")}</button>
        </header>
        <div class="track-details-scroll">
        ${this.trackDetailsLoading ? html`<div class="empty">Loading mapping history…</div>` : details ? html`
          <div class="track-detail-stat-strip" aria-label="Track mapping summary">
            <div><small>Playback</small><strong>${context.isLocal === true ? titleCase(this.status?.backendType || "Jellyfin") : context.isLocal === false ? providerDisplayName(context.externalProvider, this.schema?.providers) : "Not matched"}</strong></div>
            <div><small>Mapped</small><strong>${details.lastMappedAt ? formatRelativeTime(details.lastMappedAt) : "Not yet"}</strong></div>
            <div><small>Cached</small><strong>${lastCached ? formatRelativeTime(lastCached) : "No"}</strong></div>
          </div>
          <div class="track-details-grid compact-track-details">
            <section class="track-detail-section"><div class="section-heading"><div><h4>Current route</h4><p>Where Allstarr will play this entry.</p></div></div>
              <div class="track-identity-list">
                ${context.isLocal === true && materializedBackendItemId ? html`<div><span class="provider-badge configured">${icon("library", 14)} ${titleCase(this.status?.backendType || "Jellyfin")}</span><strong>Local library</strong><small class="mono">${materializedBackendItemId}</small></div>` : nothing}
                ${context.isLocal === false ? html`<div><span class="provider-badge configured">${this.renderProviderLogo(context.externalProvider, "tiny")} ${providerDisplayName(context.externalProvider, this.schema?.providers)}</span><strong>External playback</strong><small>${legacy?.lastValidatedAt ? `Checked ${formatRelativeTime(legacy.lastValidatedAt)}` : "Ready for the next validation"}</small></div>` : nothing}
                ${context.isLocal == null ? html`<div class="empty compact"><strong>No playable route yet</strong><span>Allstarr checked the local library first, then the enabled providers in your configured order. Use Rematch after changing the library or provider order.</span></div>` : nothing}
              </div>
            </section>
            <section class="track-detail-section"><div class="section-heading"><div><h4>Known services</h4><p>Other identities associated with this recording.</p></div></div>
              <div class="track-identity-list">
                <div><span class="provider-badge configured">${this.renderProviderLogo("spotify", "tiny")} Spotify</span><strong>Source track</strong></div>
                ${identities.filter((identity) => identity.providerId !== "spotify").map((identity) => html`<div><span class="provider-badge configured">${this.renderProviderLogo(identity.providerId, "tiny")} ${providerDisplayName(identity.providerId, this.schema?.providers)}</span><strong>Linked</strong></div>`)}
                ${localTracks.filter((track) => track.backendItemId !== materializedBackendItemId).map(() => html`<div><span class="provider-badge configured">${icon("library", 14)} ${titleCase(this.status?.backendType || "Jellyfin")}</span><strong>Another local copy</strong></div>`)}
              </div>
            </section>
          </div>
          <details class="track-technical-history">
            <summary>Technical history</summary>
            <div class="track-details-grid compact-track-details">
              <section class="track-detail-section"><h4>Match decisions</h4><div class="track-history-list">${history.length ? history.slice(0, 3).map((item) => html`<div><span class="status-chip ${item.state}">${titleCase(item.state)}</span><strong>${item.confidence != null && Number.isFinite(Number(item.confidence)) ? `${(Number(item.confidence) * 100).toFixed(0)}% match` : titleCase(item.source || "automatic")}</strong><small>${item.decidedAt ? formatRelativeTime(item.decidedAt) : "Current playlist"}</small></div>`) : html`<div class="empty compact">No saved decision yet.</div>`}</div></section>
              <section class="track-detail-section"><h4>Recent activity</h4><div class="track-activity-list">${activity.length ? activity.slice(0, 5).map((item) => html`<div><span>${icon(item.kind === "download" ? "download" : item.kind === "cache" ? "metadata" : item.kind === "validation" ? "check" : "link", 16)}</span><div><strong>${display(item.title)}</strong><small>${formatRelativeTime(item.at)}</small></div></div>`) : html`<div class="empty compact">No activity recorded yet.</div>`}</div></section>
            </div>
          </details>
        ` : html`<div class="empty">Mapping history could not be loaded.</div>`}
        </div>
      </section>
    </div>`;
  }

  renderInjectedTrackMenu(track, index = 0) {
    const spotifyId = String(track?.spotifyId || track?.SpotifyId || track?.backendItemId || track?.backend_id || track?.id || track?.Id || "").trim();
    const menuId = spotifyId || `track-${index}`;
    const open = this.injectedTrackMenuId === menuId;
    const toggleMenu = () => { this.injectedTrackMenuId = open ? "" : menuId; };
    return html`<div class="track-action-menu">
      <button class="track-action-trigger" type="button" aria-label="Actions for ${display(track.title, "track")}" aria-haspopup="menu"
        aria-expanded=${open ? "true" : "false"}
        @click=${(event) => { event.preventDefault(); event.stopPropagation(); toggleMenu(); }}
        @keydown=${(event) => {
          if (!(["Enter", " ", "Spacebar"].includes(event.key))) return;
          event.preventDefault();
          event.stopPropagation();
          toggleMenu();
        }}>
        &#8942;
      </button>
      ${open ? html`<div class="track-action-popover" role="menu" @click=${(event) => event.stopPropagation()} @keydown=${(event) => event.stopPropagation()}>
        <button role="menuitem" @click=${(event) => { event.stopPropagation(); this.openInjectedTrackEditor(track, "local"); }}>Search local library</button>
        <button role="menuitem" @click=${(event) => { event.stopPropagation(); this.openInjectedTrackEditor(track, "external"); }}>Search music providers</button>
        <button role="menuitem" @click=${(event) => { event.stopPropagation(); this.rematchInjectedTrack(track); }}>Rematch automatically</button>
        <button role="menuitem" class="danger-text" ?disabled=${track.isLocal == null && !track.isManualMapping}
          @click=${(event) => { event.stopPropagation(); this.clearInjectedTrackMapping(track); }}>Clear match</button>
      </div>` : nothing}
    </div>`;
  }

  openInjectedTrackEditor(track, mode) {
    this.injectedTrackMenuId = "";
    this.injectedTrackEditor = {
      track,
      mode,
      query: track.searchQuery || `${track.title || ""} ${asArray(track.artists)[0] || ""}`.trim(),
      provider: "deezer",
      results: [],
      searched: false,
      loading: false,
    };
  }

  renderInjectedTrackEditor() {
    const editor = this.injectedTrackEditor;
    if (!editor) return nothing;
    const localMode = editor.mode === "local";
    return html`<section class="track-match-editor" aria-label="Configure match for ${display(editor.track?.title, "track")}">
      <div class="section-heading">
        <div><h4>${localMode ? "Choose a local match" : "Choose a provider match"}</h4><p><strong>${display(editor.track?.title)}</strong> · ${asArray(editor.track?.artists).join(", ") || "Unknown artist"}</p></div>
        <button class="ghost" @click=${() => { this.injectedTrackEditor = null; }}>Close</button>
      </div>
      <form class="track-match-search" @submit=${this.searchInjectedTrackMatches}>
        ${!localMode ? html`<label>Provider<select .value=${editor.provider}
          @change=${(event) => { this.injectedTrackEditor = { ...editor, provider: event.currentTarget.value, results: [], searched: false }; }}>
          <option value="deezer">Deezer</option>
          <option value="qobuz">Qobuz</option>
          <option value="applemusic">Apple Music</option>
        </select></label>` : nothing}
        <label class="track-match-query">Search<input required .value=${editor.query}
          @input=${(event) => { this.injectedTrackEditor = { ...editor, query: event.currentTarget.value }; }}></label>
        <button class="primary" ?disabled=${editor.loading}>${editor.loading ? "Searching…" : "Search"}</button>
      </form>
      <div class="track-match-results" aria-live="polite">
        ${editor.results.length ? editor.results.map((result) => html`<button class="track-match-result"
          @click=${() => this.applyInjectedTrackMatch(result)}>
          <span><strong>${display(result.title || result.name)}</strong><small>${display(result.artist, "Unknown artist")}${result.album ? ` · ${result.album}` : ""}</small></span>
          <span class="chip">Use match</span>
        </button>`) : editor.searched && !editor.loading ? html`<div class="empty">No matching tracks found.</div>` : nothing}
      </div>
    </section>`;
  }

  searchInjectedTrackMatches = async (event) => {
    event.preventDefault();
    const editor = this.injectedTrackEditor;
    if (!editor?.query?.trim()) return;
    this.injectedTrackEditor = { ...editor, loading: true, searched: true };
    try {
      const response = editor.mode === "local"
        ? await API.searchLocalTracks(editor.query.trim())
        : await API.searchExternalTracks(editor.query.trim(), editor.provider);
      this.injectedTrackEditor = { ...editor, results: asArray(response.results || response.tracks), loading: false, searched: true };
    } catch (error) {
      this.injectedTrackEditor = { ...editor, loading: false, searched: true, results: [] };
      this.toast(error.message, "error");
    }
  };

  async applyInjectedTrackMatch(result) {
    const editor = this.injectedTrackEditor;
    if (!editor?.track?.spotifyId) return;
    const payload = editor.mode === "local"
      ? { spotifyId: editor.track.spotifyId, jellyfinId: result.id }
      : { spotifyId: editor.track.spotifyId, externalProvider: result.externalProvider || editor.provider, externalId: result.externalId || result.id };
    try {
      await API.saveInjectedTrackMapping(this.selectedInjectedPlaylist, payload);
      await this.reloadInjectedPlaylistDetails();
      this.injectedTrackEditor = null;
      this.toast("Track match saved");
    } catch (error) {
      this.toast(error.message, "error");
    }
  }

  async clearInjectedTrackMapping(track, rematch = false) {
    this.injectedTrackMenuId = "";
    try {
      await API.clearInjectedTrackMapping(this.selectedInjectedPlaylist, track.spotifyId);
      if (rematch) await API.matchPlaylist(this.selectedInjectedPlaylist);
      await this.reloadInjectedPlaylistDetails();
      this.toast(rematch ? "Track rematched" : "Track match cleared");
    } catch (error) {
      if (rematch && error.status === 404) {
        await API.matchPlaylist(this.selectedInjectedPlaylist);
        await this.reloadInjectedPlaylistDetails();
        this.toast("Track rematched");
        return;
      }
      this.toast(error.message, "error");
    }
  }

  async rematchInjectedTrack(track) {
    await this.clearInjectedTrackMapping(track, true);
  }

  async reloadInjectedPlaylistDetails() {
    this.injectedPlaylistDetails = await API.playlistTracks(this.selectedInjectedPlaylist);
    await this.loadPlaylists();
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
    const legacyMappings = asArray(this.legacyMappings?.mappings || this.legacyMappings?.Mappings);
    const playableLegacyMappings = legacyMappings.filter((mapping) => mapping.playable ?? mapping.Playable ?? false);
    const reviewLegacyMappings = legacyMappings.filter((mapping) => !(mapping.playable ?? mapping.Playable ?? false));
    const stats = this.mappings?.stats || this.mappings?.Stats || {};
    const pagination = this.mappings?.pagination || this.mappings?.Pagination || {};

    return html`
      <div class="section-heading mapping-page-heading"><div><h3>Canonical track map</h3><p>These provider-neutral identities and local matches are stored durably in Postgres. Provider IDs stay attached to the same recording instead of creating separate provider-specific maps.</p></div><span class="chip success">Postgres</span></div>
      <div class="grid">
        <div class="card metric"><span class="metric-label">Total</span><span class="metric-value">${display(stats.total ?? 0)}</span></div>
        <div class="card metric"><span class="metric-label">Accepted</span><span class="metric-value">${display(stats.accepted ?? 0)}</span></div>
        <div class="card metric"><span class="metric-label">Needs review</span><span class="metric-value">${display(stats.review ?? 0)}</span></div>
        <div class="card metric"><span class="metric-label">Unresolved</span><span class="metric-value">${display(stats.unresolved ?? 0)}</span></div>
        <div class="card metric"><span class="metric-label">Legacy ready</span><span class="metric-value">${display(playableLegacyMappings.length)}</span></div>
        <div class="card metric"><span class="metric-label">Legacy review</span><span class="metric-value">${display(reviewLegacyMappings.length)}</span></div>
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
      ${legacyMappings.length ? html`<div class="panel legacy-mappings-panel">
        <div class="section-heading"><div><h3>Imported legacy decisions</h3><p class="muted">Your previous decisions are intact. Ready targets can play now; preserved targets stay visible until a safe replacement can be confirmed.</p></div><div class="actions"><span class="chip success">${playableLegacyMappings.length} ready</span>${reviewLegacyMappings.length ? html`<span class="chip warning">${reviewLegacyMappings.length} need review</span>` : nothing}</div></div>
        ${reviewLegacyMappings.length ? html`<div class="callout warning"><strong>${reviewLegacyMappings.length} old ${reviewLegacyMappings.length === 1 ? "decision uses" : "decisions use"} an unavailable provider.</strong><span>Nothing was deleted or guessed. Open the affected playlist in Playlists, choose Match, and select a playable Jellyfin or provider result.</span><button @click=${() => this.navigate("/library/playlists")}>Review affected playlists</button></div>` : html`<div class="callout success"><strong>Every imported decision has a playable target.</strong></div>`}
        <div class="table-wrap"><table class="responsive-data-table"><thead><tr><th>Status</th><th>Playlist</th><th>Spotify track</th><th>Target</th><th>Created</th></tr></thead><tbody>${legacyMappings.map((mapping) => {
          const playable = mapping.playable ?? mapping.Playable ?? false;
          return html`<tr class=${playable ? "" : "mapping-needs-review"}><td data-label="Status"><span class="chip ${playable ? "success" : "warning"}">${playable ? "Ready" : "Review"}</span></td><td class="mobile-primary" data-label="Playlist">${display(mapping.playlist || mapping.Playlist)}</td><td class="mono" data-label="Spotify track">${display(mapping.spotifyId || mapping.SpotifyId)}</td><td data-label="Target">${mapping.jellyfinId || mapping.JellyfinId ? html`Jellyfin <span class="mono">${mapping.jellyfinId || mapping.JellyfinId}</span>` : html`${titleCase(mapping.externalProvider || mapping.ExternalProvider)} <span class="mono">${mapping.externalId || mapping.ExternalId}</span>`}</td><td data-label="Created">${formatDate(mapping.createdAt || mapping.CreatedAt)}</td></tr>`;
        })}</tbody></table></div>
      </div>` : nothing}
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
        <table class="responsive-data-table">
          <thead><tr><th>Provider track</th><th>State</th><th>Local match</th><th>Provider identities</th><th>Confidence</th><th></th></tr></thead>
          <tbody>
            ${mappings.length ? mappings.map((mapping) => this.renderMappingRow(mapping)) : html`
              <tr class="empty-table-row"><td class="empty-table-cell" colspan="6"><div class="empty">No mappings found.</div></td></tr>
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
        <td class="mobile-primary" data-label="Provider track">
          <strong>${display(mapping.title, "Unknown track")}</strong>
          <div class="muted">${display(mapping.artist, "Unknown artist")} · ${display(mapping.album, "Unknown album")}</div>
          <div class="mono">${display(mapping.providerId)} · ${display(snapshotId)}</div>
        </td>
        <td data-label="State"><span class="chip">${display(mapping.state)}</span></td>
        <td data-label="Local match">
          ${local ? html`<strong>${display(local.title)}</strong><div class="muted">${display(local.artist)}</div><div class="mono">${display(local.id)}</div>` : html`<span class="muted">No accepted local track</span>`}
          ${asArray(mapping.candidates).map((candidate) => html`<div><button @click=${() => this.prefillMatchReview({ ...mapping, libraryTrackId: candidate.libraryTrackId }, "pin")}>Pin ${display(candidate.backendItemId, candidate.libraryTrackId)} (${Math.round(Number(candidate.confidence || 0) * 100)}%)</button></div>`)}
        </td>
        <td data-label="Provider identities">${identities.length ? identities.map((item) => html`<span class="chip">${display(item.providerId)}: <span class="mono">${display(item.externalId)}</span></span>`) : html`<span class="muted">Not linked yet</span>`}</td>
        <td data-label="Confidence">${mapping.confidence == null ? html`<span class="muted">—</span>` : html`${Math.round(Number(mapping.confidence) * 100)}%<div class="muted">threshold ${Math.round(Number(mapping.threshold) * 100)}%</div>`}</td>
        <td class="mobile-actions" data-label="Actions">
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
        <table class="responsive-data-table">
          <thead><tr><th>Playlist</th><th>Tracks</th><th>Local</th><th>External</th><th>Missing</th></tr></thead>
          <tbody>
            ${playlists.length ? playlists.map((playlist) => html`
              <tr>
                <td class="mobile-primary" data-label="Playlist"><strong>${playlist.name}</strong></td>
                <td data-label="Tracks">${display(playlist.trackCount)}</td>
                <td data-label="Local">${display(playlist.localTracks)}</td>
                <td data-label="External">${display(playlist.externalTracks)}</td>
                <td data-label="Missing"><span class="chip ${Number(playlist.externalMissing || 0) > 0 ? "warning" : "success"}">${display(playlist.externalMissing || 0)}</span></td>
              </tr>
            `) : html`<tr class="empty-table-row"><td class="empty-table-cell" colspan="5"><div class="empty">No playlist data loaded.</div></td></tr>`}
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
      <section class="panel kept-surface" data-testid="kept-downloads">
        <header class="kept-surface-header">
          <div class="stat-strip" aria-label="Kept download totals">
            <div><span class="metric-label">Files</span><strong>${display(this.downloads?.count ?? this.downloads?.Count ?? files.length)}</strong></div>
            <div><span class="metric-label">Size</span><strong>${display(this.downloads?.totalSizeFormatted ?? this.downloads?.TotalSizeFormatted, "—")}</strong></div>
          </div>
          <div class="actions">
            <button class="primary" @click=${async () => { await this.loadDownloads(); this.toast("Downloads refreshed"); }}>Refresh</button>
            ${files.length ? html`<button class="danger" @click=${async () => { if (confirm("Delete all kept downloads?")) { await API.deleteAllDownloads(); await this.loadDownloads(); this.toast("Downloads deleted"); } }}>Delete all</button>` : nothing}
          </div>
        </header>
        <div class="table-wrap">
          ${files.length ? html`<table class="responsive-data-table">
            <thead><tr><th>Artist</th><th>Album</th><th>File</th><th>Size</th><th></th></tr></thead>
            <tbody>
              ${files.map((file) => html`
                <tr>
                  <td class="mobile-primary" data-label="Artist">${display(file.artist)}</td>
                  <td data-label="Album">${display(file.album)}</td>
                  <td class="mono" data-label="File">${display(file.fileName)}</td>
                  <td data-label="Size">${display(file.sizeFormatted)}</td>
                  <td class="mobile-actions" data-label="Actions"><button class="danger" @click=${async () => { await API.deleteDownload(file.path); await this.loadDownloads(); this.toast("Download deleted"); }}>Delete</button></td>
                </tr>
              `)}
            </tbody>
          </table>` : emptyState("No kept downloads. Tracks you keep will appear here.")}
        </div>
      </section>
    `;
  }

  renderSources() {
    const canManageAccounts = this.canManageProviderAccounts();
    if (!this.isAdministrator()) {
      return html`
        <section class="view-stack">
          <div class="view-header sources-page-header">
            <div class="sources-page-title">
              <h2>Sources</h2>
              <p>See which music and metadata services are available to Allstarr.</p>
            </div>
            ${canManageAccounts ? html`<div class="sources-page-actions"><button class="primary icon-label" @click=${() => this.navigate("/settings")}>${icon("settings", 16)}<span>Manage accounts</span></button></div>` : nothing}
          </div>
          <div class="empty">Provider configuration is managed from Settings.</div>
        </section>
      `;
    }

    const providers = asArray(this.schema?.providers);
    const statusOrder = { degraded: 0, needs_config: 1, needs_login: 1, partial_config: 1, unknown: 2, available: 2, testing: 2, healthy: 3, disabled: 4 };
    const orderedProviders = [...providers].sort((left, right) =>
      (statusOrder[this.providerStatus(left)] ?? 2) - (statusOrder[this.providerStatus(right)] ?? 2) ||
      String(left.name).localeCompare(String(right.name)));
    const musicProviders = orderedProviders.filter((provider) =>
      asArray(provider.categories).some((category) => ["streaming", "download", "playlist"].includes(String(category).toLowerCase())));
    const helperProviders = orderedProviders.filter((provider) => !musicProviders.includes(provider));
    return html`
      <section class="view-stack sources-view" data-testid="sources-workspace">
        <div class="view-header sources-page-header">
          <div class="sources-page-title">
            <h2>Sources</h2>
            <p>Connect and manage music providers, download helpers, metadata, and lyrics services.</p>
          </div>
          <div class="sources-page-actions">
            ${canManageAccounts ? html`<button class="icon-label" @click=${() => this.navigate("/settings")}>${icon("settings", 16)}<span>Manage accounts</span></button>` : nothing}
            <button class="primary icon-label" @click=${() => { this.sourceCatalogOpen = true; }}>${icon("plus", 16)}<span>Add source</span></button>
          </div>
        </div>
        ${this.renderProviderSection("music", "Music providers", musicProviders)}
        ${this.renderProviderSection("helpers", "Metadata & helpers", helperProviders)}
        <details class="content-disclosure" @toggle=${(event) => {
          if (event.currentTarget.open) void this.loadExtensionControlPlane();
        }}>
          <summary><span><strong>Source behavior</strong><small>Capability details and actions after favoriting a song</small></span></summary>
          <div class="disclosure-body">
            ${this.renderFavoritePolicy()}
            ${this.renderProviderSupportMatrix()}
          </div>
        </details>
        ${this.renderProviderDetailModal()}
        ${this.renderSourceCatalogModal()}
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

  providerAccountPermissions() {
    const administrator = Boolean(this.session?.isAdministrator || this.session?.IsAdministrator);
    const managementMode = String(this.schema?.providerAccountManagementMode || "Hybrid");
    const canManageAll = administrator && managementMode !== "UserManaged";
    return { administrator, managementMode, canManageAll, canManage: canManageAll || managementMode !== "AdminManaged" };
  }

  canManageProviderAccounts() {
    return this.providerAccountPermissions().canManage;
  }

  openProviderAccountModal(providerId = "spotify") {
    this.newProviderAccountId = providerId;
    this.providerAccountModalOpen = true;
  }

  closeProviderAccountModal() {
    this.providerAccountModalOpen = false;
  }

  renderProviderAccounts() {
    const accounts = asArray(this.providerAccounts);
    const { administrator, canManage } = this.providerAccountPermissions();
    if (!canManage) return html`<div class="empty">Provider accounts are managed by an administrator.</div>`;
    return html`<div class="provider-account-grid">${accounts.length ? accounts.map((account) => this.renderProviderAccountCard(account, administrator)) : html`<div class="empty">No provider accounts yet. Use Add account to connect one.</div>`}</div>`;
  }

  renderProviderAccountModal() {
    if (!this.providerAccountModalOpen) return nothing;
    const { canManageAll, canManage, managementMode } = this.providerAccountPermissions();
    if (!canManage) return nothing;
    return html`
      <div class="modal-backdrop provider-account-backdrop"
        @click=${(event) => { if (event.target === event.currentTarget) this.closeProviderAccountModal(); }}
        @keydown=${(event) => { if (event.key === "Escape") this.closeProviderAccountModal(); }}>
        <section class="panel provider-account-dialog" role="dialog" aria-modal="true" aria-labelledby="provider-account-dialog-title">
          <div class="section-heading provider-account-dialog-heading">
            <div><h3 id="provider-account-dialog-title">Add an account</h3><p>Connect one provider account. Allstarr saves it encrypted and checks the connection immediately.</p></div>
            <button class="ghost" type="button" @click=${() => this.closeProviderAccountModal()} aria-label="Close add account dialog">Close</button>
          </div>
          <form class="config-grid" @submit=${this.createProviderAccount}>
            <div class="form-row"><label>Provider</label><select name="providerId" autofocus .value=${this.newProviderAccountId} @change=${(event) => { this.newProviderAccountId = event.target.value; }}>${this.providerAccountChoices().map((provider) => html`<option value=${provider.id}>${provider.name}</option>`)}</select></div>
            <div class="form-row"><label>Account name</label><input name="displayName" placeholder=${`My ${providerDisplayName(this.newProviderAccountId, this.schema?.providers)} account`}></div>
            <div class="form-row"><label>Who can use it?</label><select name="scope"><option value="User">Only me</option>${canManageAll ? html`<option value="Global">Everyone</option><option value="Library">One library</option>` : nothing}</select></div>
            ${canManageAll ? html`<div class="form-row"><label>Library ID (only for one library)</label><input name="libraryScopeId"></div>` : nothing}
            ${this.renderNewProviderCredentialFields(this.newProviderAccountId)}
            <div class="provider-account-dialog-footer full-span"><span class="status-chip configured">${managementMode}</span><div class="actions"><button type="button" @click=${() => this.closeProviderAccountModal()}>Cancel</button><button class="primary">Save and test</button></div></div>
          </form>
        </section>
      </div>
    `;
  }

  renderNewProviderCredentialFields(providerId) {
    if (providerId === "spotify") return html`<div class="form-row full-span"><label>Spotify browser session (sp_dc)</label><input name="sessionCookie" type="password" autocomplete="off" required><small>Sign in at spotify.com and copy the current <span class="mono">sp_dc</span> cookie. You may paste just its value, <span class="mono">sp_dc=…</span>, or the full Cookie header.</small></div>`;
    if (providerId === "deezer") return html`<div class="form-row full-span"><label>ARL cookie</label><input name="arl" type="password" autocomplete="off" required></div>`;
    if (providerId === "qobuz") return html`<div class="form-row"><label>User auth token</label><input name="userAuthToken" type="password" autocomplete="off" required></div><div class="form-row"><label>User ID</label><input name="userId" required></div>`;
    if (providerId === "lastfm") return html`<div class="form-row full-span"><div class="callout"><strong>One-time Last.fm application setup</strong><p>Last.fm no longer accepts the shared Jellyfin plugin key. Create a free API application, paste its key and shared secret below, then sign in normally. Allstarr exchanges the password for a session and does not save the password.</p><a href="https://www.last.fm/api/account/create" target="_blank" rel="noopener noreferrer">Create a Last.fm API application</a></div></div><div class="form-row"><label>Application API key</label><input name="apiKey" type="password" autocomplete="off" required></div><div class="form-row"><label>Application shared secret</label><input name="sharedSecret" type="password" autocomplete="off" required></div><div class="form-row"><label>Last.fm username</label><input name="username" autocomplete="username" required></div><div class="form-row"><label>Last.fm password</label><input name="password" type="password" autocomplete="current-password" required><small>Used once to request a Last.fm session; never stored by Allstarr.</small></div>`;
    if (providerId === "listenbrainz") return html`<div class="form-row full-span"><label>ListenBrainz user token</label><input name="token" type="password" autocomplete="off" required></div>`;
    if (providerId === "apple-musickit") return html`
      <div class="form-row full-span"><div class="callout"><strong>Apple Music playlist access</strong><p>This account reads personal Apple Music playlists. It does not provide audio or lyrics; install and configure the relevant extension providers for metadata, search, lyrics, streaming, or downloads.</p></div></div>
      <div class="form-row"><label>Apple developer token</label><input name="DeveloperToken" type="password" autocomplete="off" required><small>Developer token created by your Apple MusicKit integration.</small></div>
      <div class="form-row"><label>Music User Token</label><input name="MusicUserToken" type="password" autocomplete="off" required><small>Per-user authorization token for personal playlist access.</small></div>`;
    const provider = asArray(this.schema?.providers).find((item) => String(item.id).toLowerCase() === String(providerId).toLowerCase());
    const fields = asArray(provider?.accountSettings);
    return fields.length ? fields.map((field) => {
      const help = field.description || field.helpText;
      const defaultJson = field.defaultValueJson ?? field.defaultJson;
      let defaultValue = "";
      if (defaultJson != null) {
        try { defaultValue = JSON.parse(defaultJson); }
        catch { defaultValue = defaultJson; }
      }
      return html`<div class="form-row"><label class="extension-setting-label"><span>${field.label}</span>${help ? html`<span class="field-info" title=${help} aria-label=${help}>${icon("info", 14)}</span>` : nothing}</label>${field.type === "select"
      ? html`<select name=${field.key} .value=${String(defaultValue)} ?required=${field.required}>${asArray(field.options).map((option) => html`<option value=${option}>${qualityLabel(providerId, option)}</option>`)}</select>`
      : field.type === "toggle"
        ? html`<input name=${field.key} type="checkbox" .checked=${Boolean(defaultValue)}>`
        : html`<input name=${field.key} type=${field.sensitive ? "password" : field.type === "number" ? "number" : "text"} .value=${String(defaultValue)} autocomplete="off" ?required=${field.required}>`}${help ? html`<small>${help}</small>` : nothing}</div>`;
    })
      : html`<div class="empty full-span">This provider does not require account details.</div>`;
  }

  providerAccountChoices() {
    const builtIns = [
      { id: "spotify", name: "Spotify" }, { id: "deezer", name: "Deezer" },
      { id: "qobuz", name: "Qobuz" }, { id: "lastfm", name: "Last.fm" },
      { id: "listenbrainz", name: "ListenBrainz" }, { id: "apple-musickit", name: "Apple Music library" },
    ];
    const extensions = asArray(this.schema?.providers)
      .filter((provider) => asArray(provider.accountSettings).length)
      .map((provider) => ({ id: provider.id, name: provider.name }));
    return [...builtIns, ...extensions.filter((provider) => !builtIns.some((item) => item.id === provider.id))];
  }

  renderProviderAccountCard(account, administrator) {
    const id = account.Id || account.id;
    const providerId = String(account.ProviderId || account.providerId || "").toLowerCase();
    const provider = asArray(this.schema?.providers).find((item) => String(item.id).toLowerCase() === providerId) || { id: providerId, name: titleCase(providerId) };
    const secret = account.secret || account.Secret || {};
    const enabled = Boolean(account.Enabled ?? account.enabled);
    const capabilities = this.providerHealth.filter((item) => String(item.providerAccountId || item.ProviderAccountId).toLowerCase() === String(id).toLowerCase());
    return html`<article class="card provider-account-card">
      <div class="provider-head">
        <div class="provider-brand"><span class="provider-logo provider-${providerId}">${providerLogoUrl(provider) ? html`<img src=${providerLogoUrl(provider)} alt="">` : providerMark(provider)}</span><div class="provider-title"><strong>${providerAccountDisplayName(account.DisplayName || account.displayName, provider.name || titleCase(providerId))}</strong><span>${provider.name || titleCase(providerId)}</span></div></div>
        <span class="status-chip ${enabled ? "configured" : "disabled"}">${enabled ? "Enabled" : "Disabled"}</span>
      </div>
      <div class="account-meta"><span class="chip">${titleCase(account.scope || account.Scope)}</span><span class="chip ${secret.configured ? "success" : "warning"}">${secret.configured ? "Account details stored" : "Account setup needed"}</span>${account.LibraryScopeId || account.libraryScopeId ? html`<span class="chip">Library ${account.LibraryScopeId || account.libraryScopeId}</span>` : nothing}</div>
      ${this.renderProviderRecovery(account, capabilities)}
      ${administrator && capabilities.length ? this.renderProviderAccountHealth(account, capabilities) : html`<p class="muted">No automatic connection test is available for this provider.</p>`}
      <div class="account-actions">
        <button class=${enabled ? "" : "primary"} @click=${async () => { await API.setProviderAccountEnabled(id, !enabled, account.revision ?? account.Revision); await this.loadProviderAccounts(); this.toast(`Provider account ${enabled ? "disabled" : "enabled"}`); }}>${enabled ? "Disable" : "Enable"}</button>
        <button @click=${() => this.toggleProviderAccountConfiguration(id)} aria-expanded=${this.providerAccountConfigOpen.has(String(id)) ? "true" : "false"}>${this.providerAccountConfigOpen.has(String(id)) ? "Close setup" : "Configure"}</button>
        ${administrator && enabled && capabilities.some((item) => Boolean(item.canTest ?? item.CanTest)) ? html`<button class="primary" ?disabled=${this.providerTests.has(`${id}:account`)} @click=${() => this.testProviderAccount(account)}>${this.providerTests.has(`${id}:account`) ? "Testing..." : "Test connection"}</button>` : nothing}
        <button class="ghost danger-text" @click=${async () => { if (!window.confirm("Remove this saved credential?")) return; await API.revokeProviderAccount(id); await this.loadProviderAccounts(); this.toast("Provider credential removed"); }}>Remove</button>
      </div>
      ${this.providerAccountConfigOpen.has(String(id)) ? this.renderProviderCredentialEditor(account) : nothing}
    </article>`;
  }

  renderProviderRecovery(account, capabilities) {
    const providerId = String(account.ProviderId || account.providerId || "").toLowerCase();
    const failed = capabilities.find((item) => String(item.health || item.Health || "").toLowerCase() === "degraded");
    if (!failed) return nothing;
    const reason = String(failed.reasonCode || failed.ReasonCode || "").toLowerCase();
    const failedCapability = String(failed.capability || failed.Capability || "").toLowerCase();
    const expiredSpotify = providerId === "spotify" && failedCapability === "playlist" &&
      (["provider_unauthorized", "unauthorized", "invalid_credentials"].includes(reason) || reason.includes("credential"));
    const rejectedLastFm = providerId === "lastfm" && failedCapability === "scrobbling";
    const message = expiredSpotify
      ? "Spotify rejected the saved session. Add a fresh sp_dc cookie to resume playlist refreshes; cached playlists will keep working meanwhile."
      : providerId === "spotify" && failedCapability === "playlist" && reason === "invalid_response"
        ? "Spotify accepted the request but did not return a usable web-player token. The browser-session method may have changed; your saved cookie has not been proven invalid."
      : providerId === "spotify" && failedCapability === "playlist" && reason === "upstream_blocked"
        ? "Spotify blocked the web-player token request before checking your saved session. Your cookie has not been rejected; this connection method is currently unavailable from the Allstarr server."
      : providerId === "spotify" && failedCapability === "playlist" && reason.startsWith("upstream_http_")
        ? `Spotify returned HTTP ${reason.slice("upstream_http_".length)} while checking playlists. This does not necessarily mean your cookie is wrong.`
      : rejectedLastFm
        ? "Last.fm rejected the saved session. Reconnect with your password; if this account used the old Jellyfin app key, replace the application key and shared secret too."
      : reason === "timeout" || reason === "unreachable"
        ? `${providerDisplayName(providerId, this.schema?.providers)} could not be reached. Check the service, then test again.`
        : `${providerDisplayName(providerId, this.schema?.providers)} needs attention. Open setup to review its saved connection, then test again.`;
    return html`<div class="provider-recovery" role="status">
      <div><strong>${expiredSpotify ? "Reconnect Spotify" : rejectedLastFm ? "Reconnect Last.fm" : "Connection needs attention"}</strong><span>${message}</span></div>
      <button class=${expiredSpotify || rejectedLastFm ? "primary compact" : "compact"} @click=${() => this.toggleProviderAccountConfiguration(account.Id || account.id)}>${this.providerAccountConfigOpen.has(String(account.Id || account.id)) ? "Close setup" : rejectedLastFm ? "Reconnect" : "Open setup"}</button>
    </div>`;
  }

  renderProviderAccountHealth(account, capabilities) {
    const testable = capabilities.filter((item) => Boolean(item.canTest ?? item.CanTest));
    const passing = testable.filter((item) => String(item.health || item.Health) === "healthy").length;
    const tested = testable.filter((item) => !["", "unknown"].includes(String(item.health || item.Health || "unknown"))).length;
    const summary = !testable.length ? "No automatic tests" : !tested ? `${testable.length} test${testable.length === 1 ? "" : "s"} ready` : `${passing}/${testable.length} passing`;
    return html`<div class="account-health-panel">
      <div class="account-health-summary"><strong>Connection status</strong><span>${summary}</span></div>
      <div class="account-capability-list">${capabilities.map((capability) => this.renderProviderAccountCapability(account, capability))}</div>
    </div>`;
  }

  renderProviderAccountCapability(account, capability) {
    const id = account.Id || account.id;
    const providerId = account.ProviderId || account.providerId;
    const enabled = Boolean(account.Enabled ?? account.enabled);
    const capabilityId = capability.capability || capability.Capability;
    const health = capability.health || capability.Health || "unknown";
    const configuration = capability.configuration || capability.Configuration || "needs_configuration";
    const testKey = `${id}:${capabilityId}`;
    const testing = this.providerTests.has(testKey);
    const canTest = Boolean(capability.canTest ?? capability.CanTest);
    const ctsOpen = this.deepStreamDiagnosticTarget?.accountId === String(id);
    const cts = capabilityId === "streaming"
      ? this.ctsMeasurements.find((item) => String(item.providerAccountId || item.ProviderAccountId) === String(id))
      : null;
    return html`<div class="account-capability"><div><strong>${titleCase(capabilityId)}</strong><small>${configuration === "not_required" ? "No account needed" : configuration === "configured" ? "Ready" : "Needs setup"} · ${health === "unknown" ? "Not tested" : titleCase(health)}</small></div>${this.renderConnectivityMeter(this.providerTestResults.get(testKey))}${cts ? html`<span class="cts-persisted"><small>CTS ${cts.latencyMs ?? cts.LatencyMs} ms</small>${this.renderConnectivityMeter(cts)}</span>` : nothing}<span class="capability-test-actions">${canTest ? html`<button class="compact" ?disabled=${testing || !enabled} @click=${() => this.testProviderAccountCapability(id, providerId, capabilityId)}>${testing ? "Testing..." : enabled ? "Test" : "Enable to test"}</button>` : html`<span class="muted">No probe</span>`}${capabilityId === "streaming" && enabled ? html`<button class="compact" @click=${() => this.toggleDeepStreamDiagnostic(id, providerId)}>${ctsOpen ? "Close CTS" : "Measure CTS"}</button>` : nothing}</span></div>${capabilityId === "streaming" && ctsOpen ? this.renderDeepStreamDiagnostic(id, providerId) : nothing}`;
  }

  renderConnectivityMeter(result) {
    if (!result) return nothing;
    const bars = Number(result.bars ?? result.Bars ?? 0);
    const latency = Number(result.latencyMs ?? result.LatencyMs ?? 0);
    const metric = result.metric || result.Metric || "api-latency";
    const testedAt = result.testedAt || result.TestedAt || new Date().toISOString();
    const label = `${bars} of 4 connectivity bars, ${latency} milliseconds ${metric === "cts" ? "click to stream" : "API latency"}, tested ${formatDate(testedAt)}`;
    return html`<span class="connectivity-meter" role="img" aria-label=${label} title=${label}>${[1, 2, 3, 4].map((bar) => html`<i class=${bar <= bars ? "active" : ""}></i>`)}</span>`;
  }

  toggleDeepStreamDiagnostic(accountId, providerId) {
    const key = String(accountId);
    this.deepStreamDiagnosticTarget = this.deepStreamDiagnosticTarget?.accountId === key
      ? null
      : { accountId: key, providerId };
    this.deepStreamDiagnosticResult = null;
    this.requestUpdate();
  }

  renderDeepStreamDiagnostic(accountId, providerId) {
    const result = this.deepStreamDiagnosticResult;
    return html`<form class="deep-stream-diagnostic" @submit=${(event) => this.runDeepStreamDiagnostic(event, accountId, providerId)}>
      <div class="deep-stream-copy"><strong>Cold click-to-stream test</strong><p>Reads at most 256 KiB, requests an uncached response, and keeps no media. Allstarr rotates through up to 100 known tracks for this provider.</p></div>
      <label><span>Provider track ID (optional)</span><input name="trackId" autocomplete="off" placeholder="Choose automatically"></label>
      <label><span>Track label (optional)</span><input name="trackLabel" autocomplete="off" placeholder="Artist - Title"></label>
      <label><span>Quality</span><select name="quality"><option value="Any">Automatic</option><option value="Lossy">Lossy</option><option value="Lossless">Lossless</option><option value="HighResolution">High resolution</option></select></label>
      <button class="primary" ?disabled=${this.deepStreamDiagnosticBusy}>${this.deepStreamDiagnosticBusy ? "Measuring..." : "Run CTS test"}</button>
      ${result ? html`<div class="deep-stream-result" role="status">
        ${this.renderConnectivityMeter({ bars: result.bars, latencyMs: result.clickToStreamMilliseconds, metric: "cts", testedAt: result.measuredAt })}
        <dl><div><dt>Track</dt><dd>${result.trackLabel}</dd></div><div><dt>Selection</dt><dd>${result.selectionMode === "rotating-corpus" ? `Rotating corpus (${result.corpusSize} tracks)` : "Manual track"}</dd></div><div><dt>Resolve</dt><dd>${result.resolveMilliseconds} ms</dd></div><div><dt>First byte</dt><dd>${result.firstByteMilliseconds} ms</dd></div><div><dt>Throughput</dt><dd>${result.throughputKbps} kbps</dd></div><div><dt>Sample</dt><dd>${formatBytes(result.sampleBytes)}</dd></div><div><dt>Cache</dt><dd>${titleCase(result.cacheState || "unknown")}</dd></div></dl>
      </div>` : nothing}
    </form>`;
  }

  async runDeepStreamDiagnostic(event, accountId, providerId) {
    event.preventDefault();
    if (this.deepStreamDiagnosticBusy) return;
    const data = new FormData(event.currentTarget);
    this.deepStreamDiagnosticBusy = true;
    this.deepStreamDiagnosticResult = null;
    this.requestUpdate();
    try {
      this.deepStreamDiagnosticResult = await requestJson("/api/admin/provider-diagnostics/deep-stream", jsonBody({
        providerId,
        providerAccountId: accountId,
        trackId: String(data.get("trackId") || "").trim(),
        trackLabel: String(data.get("trackLabel") || "").trim() || null,
        quality: ({ Any: 0, Lossy: 1, Lossless: 2, HighResolution: 3 })[String(data.get("quality") || "Any")],
      }), "Failed to measure click-to-stream time");
      await this.loadProviderAccounts();
      this.toast("Click-to-stream measurement completed");
    } catch (error) {
      this.toast(error?.message || "Click-to-stream measurement failed", "error");
    } finally {
      this.deepStreamDiagnosticBusy = false;
      this.requestUpdate();
    }
  }

  toggleProviderAccountConfiguration(id) {
    const key = String(id);
    const next = new Set(this.providerAccountConfigOpen);
    if (next.has(key)) next.delete(key); else next.add(key);
    this.providerAccountConfigOpen = next;
  }

  createProviderAccount = async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const providerId = String(data.get("providerId") || "").trim();
    const secret = providerId === "spotify" ? { sessionCookie: String(data.get("sessionCookie") || ""), sessionCookieSetDate: new Date().toISOString() }
      : providerId === "deezer" ? { arl: String(data.get("arl") || "") }
      : providerId === "qobuz" ? { userAuthToken: String(data.get("userAuthToken") || ""), userId: String(data.get("userId") || "") }
      : providerId === "lastfm" ? { apiKey: String(data.get("apiKey") || ""), sharedSecret: String(data.get("sharedSecret") || ""), username: String(data.get("username") || "") }
      : providerId === "listenbrainz" ? { token: String(data.get("token") || "") }
      : Object.fromEntries(asArray(asArray(this.schema?.providers).find((provider) => String(provider.id).toLowerCase() === providerId.toLowerCase())?.accountSettings)
          .map((field) => [field.key, field.type === "toggle" ? data.get(field.key) === "on" : String(data.get(field.key) || "")]));
    const created = await API.createProviderAccount({
      providerId,
      displayName: String(data.get("displayName") || "").trim() || `My ${providerDisplayName(providerId, this.schema?.providers)} account`,
      scope: String(data.get("scope") || "User"),
      libraryScopeId: String(data.get("libraryScopeId") || "").trim() || null,
      enabled: true,
      secret,
    });
    let tested = true;
    try {
      if (providerId === "lastfm") {
        await API.authenticateLastFmAccount({
          accountId: created.id || created.Id,
          username: String(data.get("username") || ""),
          password: String(data.get("password") || ""),
        });
      }
      const hasConnectionProbe = ["spotify", "deezer", "qobuz", "lastfm", "listenbrainz"].includes(providerId) ||
        this.providerHealth.some((item) => String(item.provider || item.Provider).toLowerCase() === providerId && Boolean(item.canTest ?? item.CanTest));
      if (hasConnectionProbe) await API.testProviderAccount(created.id || created.Id, providerId);
    } catch (error) {
      tested = false;
      this.toast(`Account saved, but the connection test failed: ${error.message}`, "error");
    }
    form.reset();
    this.newProviderAccountId = "spotify";
    await this.loadProviderAccounts();
    this.closeProviderAccountModal();
    if (tested) this.toast("Encrypted provider account added and connection verified");
  };

  renderProviderCredentialEditor(account) {
    const providerId = String(account.providerId || account.ProviderId || "").toLowerCase();
    return html`<div class="credential-editor" aria-label="${titleCase(providerId)} account setup">
      <form class="form-stack compact-form" @submit=${(event) => this.replaceProviderAccountCredential(event, account)}>
        ${providerId === "spotify" ? html`<div class="callout"><strong>Reconnect Spotify</strong><p>Paste a fresh browser cookie below. Allstarr accepts the value, <span class="mono">sp_dc=…</span>, or a full Cookie header.</p></div><label>New sp_dc cookie<input name="sessionCookie" type="password" autocomplete="off" required></label>` : nothing}
        ${providerId === "deezer" ? html`<label>New ARL cookie<input name="arl" type="password" autocomplete="off" required></label>` : nothing}
        ${providerId === "qobuz" ? html`<label>User auth token<input name="userAuthToken" type="password" autocomplete="off" required></label><label>User ID<input name="userId" required></label>` : nothing}
        ${providerId === "listenbrainz" ? html`<label>New user token<input name="token" type="password" autocomplete="off" required></label>` : nothing}
        ${providerId === "lastfm" ? html`<div class="callout"><strong>Reconnect Last.fm</strong><p>Usually only your password is needed. It is used once to request a new session and is never stored.</p></div><label>Username (optional)<input name="username" autocomplete="username"><small>Leave blank to keep the saved username.</small></label><label>Password<input name="password" type="password" autocomplete="current-password" required></label><details><summary>Replace Last.fm application credentials</summary><p class="muted">Required if this account used the suspended Jellyfin plugin key.</p><label>New application API key<input name="apiKey" type="password" autocomplete="off"></label><label>New application shared secret<input name="sharedSecret" type="password" autocomplete="off"></label></details>` : nothing}
        ${providerId === "apple-musickit" ? html`<div class="callout"><strong>Replace Apple Music playlist authorization</strong><p>Both tokens are replaced together. This account does not control the Apple metadata or lyrics extensions.</p></div>` : nothing}
        ${!["spotify", "deezer", "qobuz", "listenbrainz", "lastfm"].includes(providerId) ? this.renderExtensionCredentialEditor(providerId) : nothing}
        <div class="actions"><button class="primary">Save and test</button><button type="button" @click=${() => this.toggleProviderAccountConfiguration(account.id || account.Id)}>Cancel</button></div>
      </form>
    </div>`;
  }

  renderExtensionCredentialEditor(providerId) {
    const provider = asArray(this.schema?.providers).find((item) => String(item.id).toLowerCase() === providerId);
    const fields = asArray(provider?.accountSettings);
    return fields.length ? fields.map((field) => html`<label>${field.label}${field.type === "select"
      ? html`<select name=${field.key} ?required=${field.required}>${asArray(field.options).map((option) => html`<option value=${option}>${qualityLabel(providerId, option)}</option>`)}</select>`
      : field.type === "toggle"
        ? html`<input name=${field.key} type="checkbox">`
        : html`<input name=${field.key} type=${field.sensitive ? "password" : field.type === "number" ? "number" : "text"} autocomplete="off" ?required=${field.required}>`}</label>`)
      : html`<p class="muted">This extension has no account settings to replace.</p>`;
  }

  async replaceProviderAccountCredential(event, account) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const providerId = String(account.providerId || account.ProviderId || "").toLowerCase();
    let secret;
    let replaceSecret = true;
    try {
      if (providerId === "spotify") secret = { sessionCookie: String(data.get("sessionCookie") || ""), sessionCookieSetDate: new Date().toISOString() };
      else if (providerId === "deezer") secret = { arl: String(data.get("arl") || "") };
      else if (providerId === "qobuz") secret = { userAuthToken: String(data.get("userAuthToken") || ""), userId: String(data.get("userId") || "") };
      else if (providerId === "listenbrainz") secret = { token: String(data.get("token") || "") };
      else if (providerId === "lastfm") {
        const apiKey = String(data.get("apiKey") || "").trim();
        const sharedSecret = String(data.get("sharedSecret") || "").trim();
        if (Boolean(apiKey) !== Boolean(sharedSecret)) {
          this.toast("Enter both the Last.fm application API key and shared secret, or leave both blank", "error");
          return;
        }
        replaceSecret = Boolean(apiKey);
        secret = { apiKey, sharedSecret, username: String(data.get("username") || "").trim() };
      }
      else {
        const provider = asArray(this.schema?.providers).find((item) => String(item.id).toLowerCase() === providerId);
        secret = Object.fromEntries(asArray(provider?.accountSettings).map((field) =>
          [field.key, field.type === "toggle" ? data.get(field.key) === "on" : String(data.get(field.key) || "")]));
      }
    } catch {
      this.toast("Credential JSON is invalid", "error");
      return;
    }
    const accountId = account.id || account.Id;
    const provider = account.providerId || account.ProviderId;
    if (replaceSecret) await API.replaceProviderAccountSecret(accountId, secret);
    let tested = null;
    try {
      if (providerId === "lastfm") {
        await API.authenticateLastFmAccount({
          accountId,
          username: String(data.get("username") || "").trim() || null,
          password: String(data.get("password") || ""),
        });
      }
      tested = await API.testProviderAccount(accountId, provider);
    } catch (error) {
      tested = { healthy: false, error: error.message };
    }
    form.reset();
    await this.loadProviderAccounts();
    this.toggleProviderAccountConfiguration(accountId);
    const healthy = Boolean(tested?.healthy ?? tested?.success);
    const reason = tested?.reasonCode || tested?.error;
    const providerName = providerDisplayName(providerId, this.schema?.providers);
    const spotifyRejected = ["provider_unauthorized", "unauthorized", "invalid_credentials"].includes(String(reason || "").toLowerCase());
    const failure = providerId === "spotify" && spotifyRejected
      ? "Account details stored; Spotify returned 401/403 for this browser session. Open setup and try a newly copied sp_dc cookie."
      : providerId === "spotify"
        ? `Account details stored; Spotify playlist verification failed${reason ? ` (${titleCase(reason)})` : ""}. This does not prove the cookie is invalid.`
      : `Account details stored; ${providerName} connection failed${reason ? ` (${titleCase(reason)})` : ""}.`;
    this.toast(healthy ? `${providerName} connected` : failure, healthy ? "success" : "error");
  }

  async testProviderAccount(account) {
    const id = account.id || account.Id;
    const provider = account.providerId || account.ProviderId;
    const testKey = `${id}:account`;
    this.providerTests = new Set([...this.providerTests, testKey]);
    try {
      const result = await API.testProviderAccount(id, provider);
      this.providerTestResults = new Map([...this.providerTestResults, [testKey, result]]);
      await this.loadProviderAccounts();
      const healthy = Boolean(result.healthy ?? result.success);
      this.toast(`${providerDisplayName(provider, this.schema?.providers)} connection ${healthy ? "passed" : "failed"}`, healthy ? "success" : "error");
    } catch (error) {
      this.toast(error.message, "error");
    } finally {
      const next = new Set(this.providerTests); next.delete(testKey); this.providerTests = next;
    }
  }

  async testProviderAccountCapability(accountId, provider, capability) {
    const testKey = `${accountId}:${capability}`;
    this.providerTests = new Set([...this.providerTests, testKey]);
    try {
      const result = await API.testProviderAccountCapability(accountId, provider, capability);
      this.providerTestResults = new Map([...this.providerTestResults, [testKey, result]]);
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
    const capabilityNames = {
      metadata: "Music search & details",
      streaming: "Playback",
      download: "Downloads",
      playlist: "Playlists",
      lyrics: "Lyrics",
      health: "Connection checks",
      scrobbling: "Listening history",
      enrichment: "Library enrichment",
      recommendation: "Smart mixes",
    };
    const capabilityDescriptions = {
      recommendation: "Suggests similar songs for generated playlists.",
      scrobbling: "Sends completed listens to this service.",
      enrichment: "Adds useful identity, credit, release, and genre details.",
      health: "Lets Allstarr check whether this connection is working.",
    };
    return html`
      <div class="panel">
        <div class="section-heading">
          <div>
            <h3>What each provider can do</h3>
            <p>Only useful features are shown. “Limited” means the feature works with the restriction described in its details.</p>
          </div>
        </div>
        <div class="support-provider-list">
          ${providers.map((provider) => {
            const useful = asArray(provider.capabilities).filter((capability) => capability.state !== "unavailable");
            return html`<article class="support-provider-row">
              <div class="support-provider-summary">
                <div><strong>${provider.name}</strong><small>${provider.configuration}</small></div>
                <span class="account-scope">${provider.accountScope === "user" ? "Personal account" : provider.accountScope === "global" ? "Server-wide" : provider.accountScope === "mixed" ? "Public + account" : "No account needed"}</span>
              </div>
              <div class="support-feature-list" aria-label="Supported features">
                ${useful.map((capability) => html`
                  <span class="support-feature support-${capability.state}" title=${capability.protocolLimit}>
                    <strong>${capabilityNames[capability.id] || titleCase(capability.id)}</strong>
                    <small>${capability.state === "supported" ? "Ready" : capability.state === "policy_blocked" ? "Disabled for safety" : "Limited"}${capabilityDescriptions[capability.id] ? ` · ${capabilityDescriptions[capability.id]}` : ""}</small>
                  </span>
                `)}
              </div>
              <details class="support-details"><summary>Technical limits and test coverage</summary>
                <dl>${useful.map((capability) => html`<div><dt>${capabilityNames[capability.id] || titleCase(capability.id)}</dt><dd>${capability.protocolLimit}<br><span class="muted">Covered by: ${capability.testCoverage}</span></dd></div>`)}</dl>
              </details>
            </article>`;
          })}
        </div>
      </div>
    `;
  }

  providerStatus(provider) {
    const providerId = String(provider.id || provider.Id || "").toLowerCase();
    if (ACCOUNT_MANAGED_PROVIDERS.has(providerId)) {
      const accounts = asArray(this.providerAccounts).filter((account) =>
        String(account.providerId || account.ProviderId).toLowerCase() === providerId);
      const enabled = accounts.filter((account) => Boolean(account.enabled ?? account.Enabled));
      if (!enabled.length) {
        return "disabled";
      }
      const accountIds = new Set(enabled.map((account) => String(account.id || account.Id).toLowerCase()));
      const health = asArray(this.providerHealth).filter((item) =>
        accountIds.has(String(item.providerAccountId || item.ProviderAccountId).toLowerCase()) &&
        Boolean(item.canTest ?? item.CanTest));
      if (health.some((item) => String(item.health || item.Health).toLowerCase() === "degraded")) {
        return "degraded";
      }
      if (health.length && health.every((item) => String(item.health || item.Health).toLowerCase() === "healthy")) {
        return "healthy";
      }
    }
    const accountConfigured = asArray(this.providerAccounts).some((account) => {
      const secret = account.secret || account.Secret || {};
      return String(account.providerId || account.ProviderId).toLowerCase() === String(provider.id).toLowerCase() &&
        Boolean(account.enabled ?? account.Enabled) && Boolean(secret.configured) && !Boolean(secret.revoked);
    });
    if (accountConfigured && provider.status === "needs_config") {
      return "configured";
    }
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
    const connected = providers.filter((provider) => ["healthy", "configured"].includes(this.providerStatus(provider))).length;
    return html`
      <div class="provider-section provider-section-${id}">
        <div class="provider-section-heading"><h3>${label}</h3><span class="chip">${connected} connected</span></div>
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
    const accountManaged = ACCOUNT_MANAGED_PROVIDERS.has(providerId);
    const account = asArray(this.providerAccounts).find((item) =>
      String(item.providerId || item.ProviderId).toLowerCase() === providerId && Boolean(item.enabled ?? item.Enabled));
    const summary = asArray(this.providerSummaries).find((item) => String(item.providerId).toLowerCase() === providerId);
    const healthRows = asArray(this.providerHealth).filter((item) => String(item.provider || item.Provider).toLowerCase() === providerId);
    const runtimeRows = asArray(provider.runtimeCapabilities);
    const totalChecks = Number(summary?.capabilityTotal ?? healthRows.filter((item) => item.canTest ?? item.CanTest).length ?? runtimeRows.length);
    const healthyChecks = Number(summary?.healthyCapabilityCount ?? healthRows.filter((item) => String(item.health || item.Health).toLowerCase() === "healthy").length ?? 0);
    const lastChecked = summary?.lastCheckedAt || [...healthRows, ...runtimeRows].map((item) => item.testedAt || item.TestedAt).filter(Boolean).sort().at(-1);
    return html`
      <article class="card source-card ${status}">
        <div class="source-card-head">
          <div class="provider-brand">${this.renderProviderLogo(providerId, "large")}<div class="provider-title"><strong>${provider.name}</strong><span>${provider.id === "musicbrainz" ? "Enrichment service" : asArray(provider.categories).includes("lyrics") && asArray(provider.categories).length === 1 ? "Lyrics service" : "Provider"}</span></div></div>
          <div class="source-card-actions">
            <span class="status-chip ${status}">${this.providerStatusLabel(status)}</span>
            <button @click=${() => { this.selectedProviderId = providerId; }}>Manage</button>
          </div>
        </div>
        <div class="chip-list capability-list">
          ${asArray(provider.categories).map((category) => html`<span class="chip ${this.providerCapabilityEnabled(provider, category) ? "success" : "muted-chip"}">${titleCase(category)}</span>`)}
          ${asArray(provider.notes).map((note) => html`<span class="chip">${note}</span>`)}
        </div>
        <div class="source-metrics">
          <div><span>Capabilities</span><strong>${asArray(provider.categories).length}</strong></div>
          <div><span>Passing checks</span><strong>${totalChecks ? `${healthyChecks}/${totalChecks}` : "—"}</strong></div>
          <div><span>Last check</span><strong>${lastChecked ? formatRelativeTime(lastChecked) : "—"}</strong></div>
          <div><span>Failures</span><strong class=${Number(summary?.failedCapabilityCount || 0) ? "warning-text" : ""}>${summary?.failedCapabilityCount ?? "—"}</strong></div>
        </div>
        <div class="source-card-footer">
          <span>${accountManaged ? "Connected account" : "Source type"}</span>
          <strong>${accountManaged ? providerAccountDisplayName(account?.displayName || account?.DisplayName || summary?.connectedAccountName) : provider.id === "musicbrainz" ? "Built-in enrichment" : "Allstarr source"}</strong>
        </div>
        ${status === "degraded" ? html`<div class="source-warning">${icon("warning", 16)}<span>${titleCase(summary?.lastFailureCode || "Connection needs attention")}</span><button @click=${() => { this.selectedProviderId = providerId; }}>View details</button></div>` : nothing}
      </article>
    `;
  }

  renderProviderDetailModal() {
    if (!this.selectedProviderId) return nothing;
    const provider = asArray(this.schema?.providers).find((item) => String(item.id || item.Id).toLowerCase() === this.selectedProviderId);
    if (!provider) return nothing;
    const providerId = String(provider.id || provider.Id).toLowerCase();
    const status = this.providerStatus(provider);
    const accountManaged = ACCOUNT_MANAGED_PROVIDERS.has(providerId);
    const capabilities = accountManaged
      ? asArray(this.providerHealth).filter((item) => String(item.provider || item.Provider).toLowerCase() === providerId)
      : asArray(provider.runtimeCapabilities);
    const close = () => { this.selectedProviderId = ""; };
    return html`<div class="modal-backdrop provider-detail-backdrop" @click=${(event) => { if (event.target === event.currentTarget) close(); }} @keydown=${(event) => this.handleDialogKeydown(event, close)}>
      <section class="panel provider-detail-dialog" role="dialog" aria-modal="true" aria-labelledby="provider-detail-title" tabindex="-1">
        <div class="dialog-header"><div class="provider-brand">${this.renderProviderLogo(providerId, "hero")}<div><h3 id="provider-detail-title">${provider.name}</h3><p>Configure this source and verify each supported capability.</p></div></div><button class="icon-button ghost" aria-label="Close provider details" @click=${close}>${icon("close")}</button></div>
        <div class="provider-detail-summary"><span class="status-chip ${status}">${this.providerStatusLabel(status)}</span>${asArray(provider.categories).map((category) => html`<span class="chip">${titleCase(category)}</span>`)}</div>
        ${capabilities.length ? html`<div class="runtime-capability-table" role="table" aria-label="Runtime capability status">
          <div class="runtime-capability-header" role="row"><span>Capability</span><span>Setup</span><span>Last check</span><span></span></div>
          ${accountManaged ? capabilities.map((capability) => { const capabilityId = capability.capability || capability.Capability; const key = `${capability.providerAccountId || capability.ProviderAccountId}:${capabilityId}`; return html`<div class="runtime-capability" role="row"><strong>${titleCase(capabilityId)}</strong><span>${String(capability.configuration || capability.Configuration) === "configured" ? "Configured" : "Needs setup"}</span><span class="runtime-health runtime-${capability.health || capability.Health}">${titleCase(capability.health || capability.Health || "unknown")}<small>${formatDate(capability.testedAt || capability.TestedAt)}</small></span>${this.renderConnectivityMeter(this.providerTestResults.get(key))}${capability.canTest ?? capability.CanTest ? html`<button class="compact" @click=${() => this.testProviderAccountCapability(capability.providerAccountId || capability.ProviderAccountId, providerId, capabilityId)}>Test</button>` : html`<span></span>`}</div>`; }) : capabilities.map((capability) => this.renderRuntimeCapability(provider, capability))}
        </div>` : html`<div class="empty compact">No automatic capability probes are available.</div>`}
        ${accountManaged ? html`<div class="provider-detail-cta"><div><strong>Account and credentials</strong><p>Accounts are managed separately so Sources stays focused on routing and health.</p></div><button class="primary" @click=${() => this.navigate("/settings")}>Open account settings</button></div>` : html`<div class="config-grid">${asArray(provider.configSchema).map((field) => this.renderConfigField(field))}</div>`}
        ${providerId === "apple-download" ? this.renderAppleMusicManager() : nothing}
        <div class="dialog-actions provider-detail-actions">
          <button class=${status === "disabled" ? "primary" : "danger"} @click=${async () => { await this.setProviderDisabled(provider, status !== "disabled"); close(); }}>${status === "disabled" ? "Enable source" : "Disable source"}</button>
        </div>
      </section>
    </div>`;
  }

  renderSourceCatalogModal() {
    if (!this.sourceCatalogOpen) return nothing;
    const providers = asArray(this.schema?.providers);
    const close = () => { this.sourceCatalogOpen = false; };
    return html`<div class="modal-backdrop source-catalog-backdrop" @click=${(event) => { if (event.target === event.currentTarget) close(); }} @keydown=${(event) => this.handleDialogKeydown(event, close)}>
      <section class="panel source-catalog-dialog" role="dialog" aria-modal="true" aria-labelledby="source-catalog-title" tabindex="-1">
        <div class="dialog-header"><div><h3 id="source-catalog-title">Add a source</h3><p>Enable a built-in source or connect its account in Settings.</p></div><button class="icon-button ghost" aria-label="Close source catalog" @click=${close}>${icon("close")}</button></div>
        <div class="source-catalog-grid">${providers.map((provider) => {
          const providerId = String(provider.id || provider.Id).toLowerCase();
          const accountManaged = ACCOUNT_MANAGED_PROVIDERS.has(providerId);
          const status = this.providerStatus(provider);
          return html`<article class="source-catalog-item">${this.renderProviderLogo(providerId, "large")}<div><strong>${provider.name}</strong><small>${asArray(provider.categories).map(titleCase).join(" · ") || "Extension provider"}</small></div><span class="status-chip ${status}">${this.providerStatusLabel(status)}</span><button @click=${async () => { close(); if (accountManaged) this.navigate("/settings"); else if (status === "disabled") await this.setProviderDisabled(provider, false); else this.selectedProviderId = providerId; }}>${accountManaged ? status === "healthy" ? "Account settings" : "Connect" : status === "disabled" ? "Enable" : "Manage"}</button></article>`;
        })}</div>
      </section>
    </div>`;
  }

  providerStatusLabel(status) {
    if (status === "healthy") return "Connected";
    if (status === "degraded") return "Test failed";
    if (["needs_config", "needs_login", "partial_config"].includes(status)) return "Needs setup";
    if (["unknown", "available"].includes(status)) return "Not checked yet";
    if (status === "testing") return "Checking";
    if (status === "disabled") return "Disabled";
    return titleCase(status);
  }

  renderRuntimeCapability(provider, capability) {
    const providerId = String(provider.id || provider.Id || "").toLowerCase();
    const configuration = String(capability.configuration || "needs_configuration");
    const health = String(capability.health || "unknown");
    const configurationLabel = configuration === "not_required" ? "No account needed" : configuration === "configured" ? "Configured" : "Needs setup";
    const healthLabel = health === "unknown" ? "Not tested" : health === "healthy" ? "Healthy" : health === "degraded" ? "Failed" : titleCase(health);
    const testKey = `global:${providerId}:${capability.id}`;
    const testing = this.providerTests.has(testKey);
    return html`<div class="runtime-capability" role="row" title=${capability.reasonCode ? titleCase(capability.reasonCode) : `Last tested ${formatDate(capability.testedAt)}`}>
      <strong>${titleCase(capability.id)}</strong>
      <span>${configurationLabel}</span>
      <span class="runtime-health runtime-${health}">${healthLabel}${capability.testedAt ? html`<small>${formatDate(capability.testedAt)}</small>` : nothing}</span>
      ${this.renderConnectivityMeter(this.providerTestResults.get(testKey))}
      ${capability.canTest && capability.canAttempt ? html`<button class="compact" ?disabled=${testing} @click=${() => this.testProviderCapability(providerId, capability.id)}>${testing ? "Testing..." : "Test"}</button>` : nothing}
    </div>`;
  }

  async testProviderCapability(provider, capability) {
    const testKey = `global:${provider}:${capability}`;
    this.providerTests = new Set([...this.providerTests, testKey]);
    try {
      const result = await API.testProviderCapability(provider, capability);
      this.providerTestResults = new Map([...this.providerTestResults, [testKey, result]]);
      await this.loadSchema();
      this.toast(`${providerDisplayName(provider, this.schema?.providers)} ${titleCase(capability)} test ${result.success ? "passed" : "failed"}`, result.success ? "success" : "error");
    } catch (error) {
      this.toast(error.message, "error");
    } finally {
      const next = new Set(this.providerTests);
      next.delete(testKey);
      this.providerTests = next;
    }
  }

  renderCapabilityPill(provider, category) {
    const accountBlocked = ACCOUNT_MANAGED_PROVIDERS.has(String(provider.id || provider.Id || "").toLowerCase()) &&
      !asArray(this.providerAccounts).some((account) =>
        String(account.providerId || account.ProviderId).toLowerCase() === String(provider.id || provider.Id || "").toLowerCase() && Boolean(account.enabled ?? account.Enabled));
    const enabled = !accountBlocked && this.providerCapabilityEnabled(provider, category);
    return html`
      <button
        class="chip capability-pill ${enabled ? "success" : "muted-chip"}"
        ?disabled=${accountBlocked}
        title=${accountBlocked ? `Enable a ${provider.name} account first` : `${enabled ? "Disable" : "Enable"} ${provider.name} for ${category}`}
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
    const gatewayReady = Boolean(status.staged && status.daemon_running && status.wrapper_healthy);
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
        ${gatewayReady && !status.logged_in ? html`<form class="form-stack compact-form" @submit=${this.submitAppleLogin}>
          <div class="form-row"><label>Apple ID</label><input name="username" autocomplete="username" required></div>
          <div class="form-row"><label>Password</label><input name="password" type="password" autocomplete="current-password" required></div>
          <button class="primary">Start login</button>
        </form>` : !gatewayReady ? html`<div class="callout warning"><strong>Apple gateway setup required</strong><p>Upload the legally obtained Apple Music Android package here. Allstarr stages it in the host profile; run the one-line host helper afterward to verify, build, and start the gateway.</p><form class="form-stack compact-form" @submit=${this.submitApplePackage}><div class="form-row"><label for="apple-package">Apple Music package</label><input id="apple-package" name="package" type="file" accept=".apk,.apkm,application/vnd.android.package-archive" required></div><button class="primary" type="submit">Upload package</button></form>${this.appleUpload ? html`<div class="callout success"><strong>Package staged</strong><p>${this.appleUpload.fileName || "Apple package"} is ready on the host.</p><pre><code>./allstarr.sh install-apple x86_64</code></pre></div>` : nothing}</div>` : html`<div class="callout success"><strong>Apple Music connected</strong><p>The external gateway and saved Apple session are ready.</p></div>`}
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
        <p class="muted provider-priority-intro">Local library is always tried first. The lists below control which provider fills a missing track.</p>
        <div class="grid">
          ${asArray(this.schema?.priorityGroups).map((group) => html`
            <div class="card">
              <h3>${group.label}</h3>
              ${group.description ? html`<p class="muted priority-description">${group.description}</p>` : nothing}
              <p class="muted priority-help">Drag providers top-to-bottom to set order. Keyboard: Alt + Up or Alt + Down.</p>
              <div class="priority-list" role="list" aria-label=${group.label}>
                ${group.pinnedProvider ? html`
                  <div
                    class="priority-item priority-item-pinned"
                    role="listitem"
                    aria-label=${`${group.pinnedProvider.name}, fixed at position 1 of ${(group.providers?.length ?? 0) + 1}`}
                    data-priority-group=${group.id}
                    data-priority-pinned="true"
                  >
                    <span class="priority-drag-handle" aria-hidden="true">${icon("lock", 14)}</span>
                    ${this.renderPinnedProviderToken(group.pinnedProvider)}
                    <span class="priority-position">1</span>
                  </div>
                ` : nothing}
                ${asArray(group.providers).map((provider, index) => html`
                  <div
                    class="priority-item ${this.priorityDrag?.groupId === group.id && this.priorityDrag?.index === index ? "dragging" : ""}"
                    role="listitem"
                    draggable="true"
                    tabindex="0"
                    data-priority-group=${group.id}
                    aria-label=${`${providerDisplayName(provider, providers)}, position ${index + (group.pinnedProvider ? 2 : 1)} of ${(group.providers?.length ?? 0) + (group.pinnedProvider ? 1 : 0)}`}
                    @dragstart=${(event) => this.startPriorityDrag(event, group, index)}
                    @dragover=${(event) => this.allowPriorityDrop(event, group)}
                    @drop=${(event) => this.dropPriority(event, group, index)}
                    @dragend=${() => { this.priorityDrag = null; }}
                    @keydown=${(event) => this.handlePriorityKeydown(event, group, index)}
                  >
                    <span class="priority-drag-handle" aria-hidden="true">⠿</span>
                    ${this.renderProviderToken(provider, providers)}
                    <span class="priority-position">${index + (group.pinnedProvider ? 2 : 1)}</span>
                  </div>
                `)}
              </div>
            </div>
          `)}
        </div>
      </div>
    `;
  }

  renderPinnedProviderToken(pinned) {
    if (!pinned) return nothing;
    const isJellyfinLibrary = String(pinned.name || "").toLowerCase().includes("jellyfin");
    const logoUrl = providerLogoUrl({ id: pinned.id, name: pinned.name })
      || (isJellyfinLibrary ? "/images/providers/jellyfin.svg" : "");
    return html`
      <span class="provider-token provider-token-pinned" title=${pinned.reason || ""}>
        <span class="provider-token-logo provider-${String(pinned.id).toLowerCase()} pinned">
          ${logoUrl ? html`<img src="${logoUrl}" alt="">` : providerMark({ id: pinned.id, name: pinned.name }).slice(0, 2)}
        </span>
        <span>${pinned.name}</span>
      </span>
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

  renderExtensionManager(item, packageState) {
    if (!item) return nothing;
    const close = () => this.closeExtensionManager();
    const id = item.id || item.Id;
    const extensionId = item.extensionId || item.ExtensionId;
    const name = item.displayName || item.DisplayName || extensionId;
    const state = packageState(item);
    const revision = item.revision ?? item.Revision ?? 0;
    const previousPackageId = item.previousPackageId || item.PreviousPackageId;
    const settings = asArray(item.settings || item.Settings);
    const requiredSettings = settings.filter((setting) => setting.required ?? setting.Required);
    const qualityOptions = asArray(item.qualityOptions || item.QualityOptions);
    const accounts = asArray(this.providerAccounts).filter((account) =>
      String(account.providerId || account.ProviderId).toLowerCase() === String(extensionId).toLowerCase());
    const usesSession = item.usesSignedSession || item.UsesSignedSession;
    const sessionAuthenticated = this.extensionSession?.authenticated ?? this.extensionSession?.Authenticated;
    const action = this.extensionActions[id];
    const capabilities = this.extensionCapabilities(item);
    const capabilityCatalog = [
      ["metadata", "Search and enrich track, album, and artist details"],
      ["playlist", "Read and synchronize provider playlists"],
      ["streaming", "Resolve playable media streams"],
      ["download", "Retrieve media for managed local storage"],
      ["lyrics", "Find and display synchronized or plain lyrics"],
    ];
    const extensionActivity = asArray(this.extensionLogs).filter((entry) => {
      const packageId = entry.extensionPackageId || entry.ExtensionPackageId;
      const logExtensionId = entry.extensionId || entry.ExtensionId;
      return packageId ? String(packageId) === String(id) : logExtensionId ? String(logExtensionId).toLowerCase() === String(extensionId).toLowerCase() : true;
    }).slice(0, 12);
    return html`
      <div class="modal-backdrop extension-manage-backdrop" @click=${(event) => { if (event.target === event.currentTarget) close(); }} @keydown=${(event) => this.handleDialogKeydown(event, close)}>
        <section class="panel extension-manage-dialog" role="dialog" aria-modal="true" aria-labelledby="extension-manage-title" tabindex="-1">
          <div class="dialog-header extension-manage-hero">
            <div class="provider-brand">
              ${this.renderExtensionLogo(item, "hero")}
              <div><h3 id="extension-manage-title">${name}</h3><span class="muted">Extension package · Version ${item.version || item.Version}${item.author || item.Author ? ` · ${item.author || item.Author}` : ""}</span></div>
            </div>
            <div class="row-actions"><span class="status-chip ${state === "active" ? "configured" : state === "failed" ? "error" : "warning"}">${state === "active" ? "Enabled" : titleCase(state)}</span><button class="icon-button ghost" aria-label="Close extension manager" @click=${close}>${icon("close")}</button></div>
          </div>
          <section class="extension-about" aria-labelledby="extension-about-title"><div><h4 id="extension-about-title">About</h4><p class="extension-manage-description">${display(item.description || item.Description, "No description supplied by this extension.")}</p></div><dl class="extension-package-facts"><div><dt>Extension ID</dt><dd><code>${extensionId}</code></dd></div><div><dt>Version</dt><dd>${item.version || item.Version}</dd></div><div><dt>Author</dt><dd>${display(item.author || item.Author, "Not provided")}</dd></div><div><dt>Installed</dt><dd>${formatDate(item.stagedAt || item.StagedAt || item.createdAt || item.CreatedAt)}</dd></div><div><dt>Runtime</dt><dd>${display(item.compatibility || item.Compatibility, "Allstarr extension SDK")}</dd></div></dl></section>

          <div class="extension-manage-grid">
            <div class="extension-manage-main">
              ${settings.length ? html`
                <section class="extension-manage-section">
                  <div class="section-heading"><div><h4>Provider configuration</h4><p>${requiredSettings.length ? `${requiredSettings.length} required field${requiredSettings.length === 1 ? "" : "s"}` : "Optional provider preferences"}</p></div><span class="status-chip ${accounts.length ? "configured" : requiredSettings.length ? "warning" : "disabled"}">${accounts.length ? "Account saved" : requiredSettings.length ? "Setup required" : "Optional"}</span></div>
                  ${state !== "active" ? html`<div class="empty"><strong>Enable this extension first</strong><span>Configuration becomes available after its runtime is loaded.</span></div>` : accounts.length ? html`
                    <div class="extension-account-summary">${accounts.map((account) => html`<div><strong>${providerAccountDisplayName(account.displayName || account.DisplayName, name)}</strong><span class="muted">${account.enabled ?? account.Enabled ? "Enabled" : "Disabled"}</span></div>`)}</div>
                    <button @click=${() => { close(); location.hash = "#/settings"; this.providerAccountConfigOpen = new Set(accounts.map((account) => account.id || account.Id)); }}>Manage saved account</button>
                  ` : html`
                    <form class="config-grid extension-config-form" @submit=${this.createProviderAccount}>
                      <input type="hidden" name="providerId" value=${extensionId}>
                      <input type="hidden" name="scope" value="User">
                      <div class="form-row full-span"><label>Account name</label><input name="displayName" value=${name} required></div>
                      ${this.renderNewProviderCredentialFields(extensionId)}
                      <div class="actions full-span"><button class="primary">Save and test</button></div>
                    </form>
                  `}
                </section>` : nothing}

              ${usesSession ? html`
                <section class="extension-manage-section">
                  <div class="section-heading"><div><h4>Session authorization</h4><p>Secure sign-in required by this extension’s service.</p></div><span class="status-chip ${sessionAuthenticated ? "configured" : "warning"}">${sessionAuthenticated ? "Authorized" : "Not authorized"}</span></div>
                  ${state !== "active" ? html`<div class="empty"><strong>Enable this extension first</strong><span>Session authorization becomes available after its runtime is loaded.</span></div>` : sessionAuthenticated ? html`<div class="row-actions"><button class="danger" @click=${async () => { await API.clearExtensionSession(id); this.extensionSession = await API.extensionSession(id); this.toast("Extension session cleared"); }}>Sign out session</button></div>` : html`
                    <div class="callout"><strong>Authorize in two steps</strong><p>Open the verification page, finish its challenge, then paste either the complete <code>spotiflac://session-grant/…</code> callback or its raw grant below.</p></div>
                    <div class="row-actions"><button class="primary" @click=${() => this.startExtensionAuthorization(item)}>Open authorization</button></div>
                    ${this.extensionSession?.authUrl ? html`<a class="button-link" href=${this.extensionSession.authUrl} target="_blank" rel="noopener noreferrer">Continue authorization ${icon("externalApi", 16)}</a>` : nothing}
                    <form class="extension-grant-form" @submit=${(event) => this.completeExtensionAuthorization(event, item)}>
                      <label><span>Authorization result</span><input name="grant" required autocomplete="off" spellcheck="false" placeholder="Paste callback URL or one-time grant"></label>
                      <button>Complete sign-in</button>
                    </form>
                    ${this.extensionSession?.authorizationError ? html`<div class="error-text" role="alert">${this.extensionSession.authorizationError}</div>` : nothing}
                  `}
                </section>` : nothing}

              <section class="extension-manage-section">
                <div class="section-heading"><div><h4>Capabilities</h4><p>Declared provider functions available to Allstarr.</p></div></div>
                <div class="extension-capability-matrix">${capabilityCatalog.map(([capability, detail]) => {
                  const supported = capabilities.includes(capability);
                  return html`<div class=${supported ? "supported" : "unsupported"}>${icon(({ metadata: "metadata", playlist: "playlist", streaming: "streaming", download: "download", lyrics: "lyrics" })[capability], 18)}<span><strong>${titleCase(capability)}</strong><small>${detail}</small></span><span class="extension-capability-state" title=${supported ? "Supported" : "Not declared"}>${icon(supported ? "check" : "close", 16)}</span></div>`;
                })}</div>
                <div class="extension-runtime-summary">
                  <strong>Runtime</strong>
                  <span class="muted">${display(item.compatibility || item.Compatibility, "Allstarr extension SDK")}</span>
                  ${asArray(item.requiredRuntimeFeatures || item.RequiredRuntimeFeatures).map((feature) => html`<span class="chip">${feature}</span>`)}
                </div>
                ${qualityOptions.length ? html`<div class="extension-quality-options"><strong>Supported quality modes</strong>${qualityOptions.map((option) => html`<span class="chip" title=${option.description || option.Description || ""}>${option.label || option.Label || option.id || option.Id}</span>`)}</div>` : nothing}
                ${state === "failed" && (item.failureCode || item.FailureCode) ? html`<div class="error-text">${titleCase(item.failureCode || item.FailureCode)}</div>` : nothing}
              </section>
            </div>
            <aside class="extension-manage-side">
              <section class="extension-manage-section"><h4>Quick actions</h4><div class="extension-action-stack">
                ${state === "reviewrequired" ? html`<button class="primary" @click=${() => this.loadExtensionPermissions(item)}>Review permissions</button>` : nothing}
                ${["staged", "disabled"].includes(state) ? html`<button class="primary" ?disabled=${Boolean(action)} @click=${() => this.runExtensionAction(item, "Enabling", () => API.activateExtensionPackage(id, revision), "Extension enabled")}>${action || "Enable extension"}</button>` : nothing}
                ${state === "active" ? html`<button ?disabled=${Boolean(action)} @click=${() => this.runExtensionAction(item, "Disabling", () => API.disableExtensionPackage(id, revision), "Extension disabled")}>${action || "Disable extension"}</button>` : nothing}
                ${state === "active" && previousPackageId ? html`<button ?disabled=${Boolean(action)} @click=${() => {
                  if (!window.confirm("Restore the previous extension version? The current runtime will be replaced and provider requests may pause briefly.")) return;
                  return this.runExtensionAction(item, "Restoring", () => API.rollbackExtensionPackage(id, revision), "Previous extension version restored");
                }}>Restore previous version</button>` : nothing}
              </div>${this.renderExtensionUninstallControl(item)}</section>
              <section class="extension-manage-section extension-manager-activity"><div class="section-heading"><div><h4>Recent activity</h4><p>Runtime, authorization, and lifecycle events.</p></div></div><div class="extension-mini-log">${extensionActivity.length ? extensionActivity.map((entry) => html`<details><summary><span class=${`activity-dot level-${String(entry.level || entry.Level || "info").toLowerCase()}`}></span><strong>${titleCase(entry.summary || entry.Summary || "Extension event")}</strong><time>${formatRelativeTime(entry.createdAt || entry.CreatedAt)}</time>${icon("chevronRight", 15)}</summary><div><p>${display(entry.message || entry.Message, "No additional details were recorded.")}</p><small>${formatDate(entry.createdAt || entry.CreatedAt)} · ${titleCase(entry.level || entry.Level || "info")}</small></div></details>`) : html`<div class="empty compact">No activity recorded for this extension.</div>`}</div><button class="ghost" @click=${() => { close(); this.extensionViewTab = "activity"; }}>View all extension activity</button></section>
            </aside>
          </div>
        </section>
      </div>`;
  }

  extensionPackageState(item) {
    return String(item?.state || item?.State || "unknown").replace(/[^a-z]/gi, "").toLowerCase();
  }

  installedExtensionPackages() {
    const installed = new Map();
    for (const item of asArray(this.extensionPackages)) {
      const state = this.extensionPackageState(item);
      if (["uninstalled", "rolledback"].includes(state)) continue;
      const extensionId = String(item.extensionId || item.ExtensionId || "").toLowerCase();
      const current = installed.get(extensionId);
      const stagedAt = new Date(item.stagedAt || item.StagedAt || 0).getTime();
      const currentStagedAt = new Date(current?.stagedAt || current?.StagedAt || 0).getTime();
      if (!current || stagedAt >= currentStagedAt) installed.set(extensionId, item);
    }
    return [...installed.values()];
  }

  extensionCapabilities(item) {
    const raw = asArray(item?.capabilities || item?.Capabilities || item?.types || item?.Types);
    const aliases = { metadata_provider: "metadata", download_provider: "download", playlist_provider: "playlist", lyrics_provider: "lyrics", stream_provider: "streaming" };
    return [...new Set(raw.map((value) => aliases[String(value).toLowerCase()] || String(value).toLowerCase()))];
  }

  renderExtensionCapabilityChip(capability) {
    const normalized = String(capability || "").toLowerCase();
    const capabilityIcon = ({
      metadata: "metadata",
      playlist: "playlist",
      streaming: "streaming",
      stream: "streaming",
      download: "download",
      lyrics: "lyrics",
      scrobbling: "activity",
      scrobble: "activity",
      externalapi: "externalApi",
      library: "edit",
    })[normalized] || "extensions";
    return html`<span class="chip icon-label" title=${`${titleCase(normalized)} capability`}>${icon(capabilityIcon, 14)}<span>${titleCase(normalized)}</span></span>`;
  }

  renderExtensionPermissionModal(item) {
    if (!item || this.extensionPackageState(item) !== "reviewrequired") return nothing;
    const id = item.id || item.Id;
    const permissions = this.extensionPermissions.get(id);
    if (!permissions) return nothing;
    const action = this.extensionActions[id];
    const close = () => { this.extensionPermissionPackageId = ""; this.extensionPermissionConfirmed = false; };
    const allDecided = permissions.length > 0 && permissions.every((review) => ["approved", "denied"].includes(review.uiDecision));
    return html`<div class="modal-backdrop extension-permission-backdrop" @click=${(event) => { if (event.target === event.currentTarget) close(); }} @keydown=${(event) => this.handleDialogKeydown(event, close)}>
      <section class="panel extension-permission-dialog" role="dialog" aria-modal="true" aria-labelledby="extension-permission-title" tabindex="-1">
        <div class="dialog-header extension-modal-identity">
          <div class="provider-brand">${this.renderExtensionLogo(item, "large")}<div><h3 id="extension-permission-title">Review permissions</h3><p>${item.displayName || item.DisplayName} needs access before it can be enabled.</p></div></div>
          <button class="icon-button ghost" aria-label="Close permission review" @click=${close}>${icon("close")}</button>
        </div>
        <div class="permission-summary"><div><strong>${permissions.length} permission${permissions.length === 1 ? "" : "s"} requested</strong><span>Review individually or allow the complete request.</span></div><button @click=${() => { const next = new Map(this.extensionPermissions); next.set(id, permissions.map((review) => ({ ...review, uiDecision: "approved" }))); this.extensionPermissions = next; }}>Allow all requested access</button></div>
        <div class="extension-permission-list">${permissions.map((review) => {
          const permissionId = review.id || review.Id;
          const decision = review.uiDecision;
          const permissionKind = String(review.permissionKind || review.PermissionKind || "externalApi");
          const permissionIcon = ({ network: "externalApi", filesystem: "download", secrets: "security", metadata: "metadata", playlist: "playlist", streaming: "streaming", download: "download", lyrics: "lyrics", library: "edit" })[permissionKind.toLowerCase()] || "security";
          return html`<div class="extension-permission-row">
            <span class="extension-permission-icon" aria-hidden="true">${icon(permissionIcon, 18)}</span><div><strong>${titleCase(permissionKind)}</strong><small class="extension-value">${review.permissionValue || review.PermissionValue}</small>${(review.required ?? review.Required) ? html`<span class="chip warning">Required</span>` : nothing}</div>
            <div class="row-actions" role="group" aria-label="Permission decision"><button class=${decision === "approved" ? "primary" : ""} @click=${() => this.setExtensionPermissionDecision(id, permissionId, true)}>Allow</button><button class=${decision === "denied" ? "danger" : ""} @click=${() => this.setExtensionPermissionDecision(id, permissionId, false)}>Deny</button></div>
          </div>`;
        })}</div>
        <label class="permission-confirm"><input type="checkbox" .checked=${this.extensionPermissionConfirmed} @change=${(event) => { this.extensionPermissionConfirmed = event.currentTarget.checked; }}><span>I understand the access requested by this extension.</span></label>
        <div class="dialog-actions"><button @click=${close}>Cancel</button><button class="primary" ?disabled=${Boolean(action) || !allDecided || !this.extensionPermissionConfirmed} @click=${() => this.reviewExtensionPermissions(item)}>${action || "Save choices and enable"}</button></div>
      </section>
    </div>`;
  }

  renderExtensionInstallModal(storeItems, installedByExtension) {
    if (!this.extensionInstallOpen) return nothing;
    const close = () => { this.extensionInstallOpen = false; };
    const query = this.extensionSearch.trim().toLowerCase();
    const available = storeItems.filter((item) => {
      const installed = installedByExtension.get(String(item.id || item.Id || "").toLowerCase());
      return !installed || String(installed.version || installed.Version) !== String(item.version || item.Version);
    })
      .filter((item) => !query || `${item.displayName || item.DisplayName} ${item.description || item.Description}`.toLowerCase().includes(query));
    return html`<div class="modal-backdrop extension-install-backdrop" @click=${(event) => { if (event.target === event.currentTarget) close(); }} @keydown=${(event) => this.handleDialogKeydown(event, close)}>
      <section class="panel extension-install-dialog" role="dialog" aria-modal="true" aria-labelledby="extension-install-title" tabindex="-1">
        <div class="dialog-header"><div class="provider-brand"><span class="workspace-icon">${icon("extensions", 25)}</span><div><h3 id="extension-install-title">Install extension</h3><p>Add an extension from a registry or a verified package URL.</p></div></div><button class="icon-button ghost" aria-label="Close installer" @click=${close}>${icon("close")}</button></div>
        <div class="workspace-tabs" role="tablist" aria-label="Extension installation method"><button role="tab" aria-selected=${this.extensionInstallTab === "registry"} tabindex=${this.extensionInstallTab === "registry" ? "0" : "-1"} class=${this.extensionInstallTab === "registry" ? "active" : ""} @keydown=${(event) => this.moveSegmentedTabFocus(event)} @click=${() => { this.extensionInstallTab = "registry"; }}>Registry</button><button role="tab" aria-selected=${this.extensionInstallTab === "direct"} tabindex=${this.extensionInstallTab === "direct" ? "0" : "-1"} class=${this.extensionInstallTab === "direct" ? "active" : ""} @keydown=${(event) => this.moveSegmentedTabFocus(event)} @click=${() => { this.extensionInstallTab = "direct"; }}>Direct URL</button></div>
        ${this.extensionInstallTab === "registry" ? html`
          <label class="extension-search">${icon("search", 17)}<input autofocus aria-label="Search extensions" placeholder="Search registry packages…" .value=${this.extensionSearch} @input=${(event) => { this.extensionSearch = event.target.value; }}></label>
          <div class="extension-install-results">${available.length ? available.map((item) => {
            const action = this.extensionActions[item.id || item.Id];
            const checksum = item.sha256 || item.Sha256;
            const updating = installedByExtension.has(String(item.id || item.Id || "").toLowerCase());
            return html`<article class="extension-install-result">${this.renderExtensionLogo(item, "large")}<div><strong>${item.displayName || item.DisplayName}</strong><small>${updating ? `Update to v${item.version || item.Version}` : `v${item.version || item.Version}`}${item.author || item.Author ? ` · ${item.author || item.Author}` : ""}</small><p>${display(item.description || item.Description, "No description supplied.")}</p><div class="extension-row-chips">${this.extensionCapabilities(item).map((capability) => this.renderExtensionCapabilityChip(capability))}</div></div><button class="primary" ?disabled=${!checksum || Boolean(action)} @click=${() => this.installExtension(item, updating)}>${action || (checksum ? updating ? "Review update" : "Install" : "Unavailable")}</button></article>`;
          }) : html`<div class="empty"><strong>No matching extensions</strong><span>Everything may already be installed.</span></div>`}</div>
        ` : html`<form class="config-grid extension-direct-form" @submit=${(event) => this.stageExtensionPackage(event)}>
          <label class="config-field full-span"><span>Package URL</span><input autofocus name="downloadUrl" type="url" required pattern="https://.*" autocomplete="off" placeholder="https://example.org/provider.sflx"></label>
          <label class="config-field full-span"><span>SHA-256 checksum</span><input name="sha256" required minlength="64" maxlength="64" pattern="[A-Fa-f0-9]{64}" autocomplete="off" spellcheck="false"><small>Allstarr verifies the package before opening permission review.</small></label>
          <label class="config-field"><span>Registry attribution</span><select name="registryId"><option value="">Direct package</option>${asArray(this.extensionRegistries).filter((entry) => entry.enabled ?? entry.Enabled).map((entry) => html`<option value=${entry.id || entry.Id}>${entry.name || entry.Name}</option>`)}</select></label>
          <div class="dialog-actions full-span"><button type="button" @click=${close}>Cancel</button><button class="primary" type="submit">Verify and install</button></div>
        </form>`}
      </section>
    </div>`;
  }

  renderExtensionUninstallControl(packageRecord) {
    if (!packageRecord) return nothing;
    const packageId = packageRecord.id || packageRecord.Id;
    const revision = packageRecord.revision ?? packageRecord.Revision;
    const displayName = packageRecord.displayName || packageRecord.DisplayName || packageRecord.extensionId || packageRecord.ExtensionId || "extension";
    const confirming = this.extensionUninstallConfirmId === packageId;
    const busy = this.extensionUninstallBusyId === packageId;
    if ((packageRecord.state || packageRecord.State || "").toLowerCase() === "uninstalled") return nothing;
    return html`
      <aside class="extension-uninstall-control" aria-label="Uninstall ${displayName}">
        ${confirming ? html`
          <div class="extension-uninstall-confirm" role="alertdialog" aria-labelledby="extension-uninstall-title">
            <div>
              <strong id="extension-uninstall-title">Uninstall ${displayName}?</strong>
              <p>The package and runtime are removed. Provider accounts can be retained for a later reinstall.</p>
            </div>
            <label class="compact-check">
              <input type="checkbox" .checked=${this.extensionUninstallRetainAccounts !== false}
                @change=${(event) => { this.extensionUninstallRetainAccounts = event.target.checked; }}>
              Retain provider accounts
            </label>
            <div class="extension-uninstall-actions">
              <button class="ghost" ?disabled=${busy} @click=${() => { this.extensionUninstallConfirmId = null; }}>Cancel</button>
              <button class="danger" ?disabled=${busy} @click=${() => this.uninstallExtensionPackage(packageRecord)}>
                ${busy ? "Uninstalling..." : "Confirm uninstall"}
              </button>
            </div>
          </div>
        ` : html`
          <button class="danger" @click=${() => {
            this.extensionUninstallRetainAccounts = true;
            this.extensionUninstallConfirmId = packageId;
          }}>${icon("trash", 17)} Uninstall extension</button>
        `}
      </aside>`;
  }

  async uninstallExtensionPackage(packageRecord) {
    const packageId = packageRecord?.id || packageRecord?.Id;
    if (!packageId || this.extensionUninstallBusyId) return;
    this.extensionUninstallBusyId = packageId;
    try {
      await API.uninstallExtensionPackage(
        packageId,
        packageRecord.revision ?? packageRecord.Revision,
        this.extensionUninstallRetainAccounts !== false,
      );
      this.extensionPackages = asArray(this.extensionPackages).filter((entry) => (entry.id || entry.Id) !== packageId);
      this.extensionUninstallConfirmId = null;
      this.extensionManagePackageId = null;
      this.extensionManagedPackageId = null;
      this.toast("Extension uninstalled");
    } catch (error) {
      this.toast(error?.message || "Failed to uninstall extension", "error");
    } finally {
      this.extensionUninstallBusyId = null;
    }
  }

  moveSegmentedTabFocus(event) {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
    const tabs = [...event.currentTarget.parentElement.querySelectorAll('[role="tab"]')];
    const current = tabs.indexOf(event.currentTarget);
    if (current < 0 || tabs.length === 0) return;
    event.preventDefault();
    const next = event.key === "Home" ? 0 : event.key === "End" ? tabs.length - 1 :
      (current + (event.key === "ArrowRight" ? 1 : -1) + tabs.length) % tabs.length;
    tabs[next].focus();
    tabs[next].click();
  }

  renderExtensions() {
    const registries = asArray(this.extensionRegistries);
    const storeItems = asArray(this.extensionStore?.items || this.extensionStore?.Items || this.extensionStore);
    const errors = asArray(this.extensionStore?.errors || this.extensionStore?.Errors);
    const packageHistory = this.installedExtensionPackages();
    const installedByExtension = new Map();
    for (const item of packageHistory) {
      const key = String(item.extensionId || item.ExtensionId).toLowerCase();
      if (!installedByExtension.has(key)) installedByExtension.set(key, item);
    }
    const installedPackages = [...installedByExtension.values()];
    const permissionPackage = packageHistory.find((item) => String(item.id || item.Id) === String(this.extensionPermissionPackageId));
    const managedPackage = packageHistory.find((item) => String(item.id || item.Id) === String(this.selectedExtensionPackageId));
    const available = storeItems.filter((item) => {
      const installed = installedByExtension.get(String(item.id || item.Id || "").toLowerCase());
      return !installed || String(installed.version || installed.Version) !== String(item.version || item.Version);
    });
    const activeRegistries = registries.filter((item) => item.enabled ?? item.Enabled);
    const renderActivityEntry = (entry, compact = false) => {
      const packageId = String(entry.extensionPackageId || entry.ExtensionPackageId || "");
      const sourcePackage = packageHistory.find((item) => String(item.id || item.Id) === packageId);
      const packageName = sourcePackage?.displayName || sourcePackage?.DisplayName || entry.extensionId || entry.ExtensionId || "Extension runtime";
      const level = String(entry.level || entry.Level || "information").toLowerCase();
      return html`<details class="extension-activity-entry ${compact ? "compact" : ""}"><summary><span class=${`activity-dot level-${level}`}></span><span><strong>${titleCase(entry.summary || entry.Summary || "Extension event")}</strong><small>${packageName}</small></span><time>${compact ? formatRelativeTime(entry.createdAt || entry.CreatedAt) : formatDate(entry.createdAt || entry.CreatedAt)}</time>${icon("chevronRight", 16)}</summary><div><p>${display(entry.message || entry.Message, "No additional details were recorded.")}</p><dl><div><dt>Extension</dt><dd>${packageName}</dd></div><div><dt>Level</dt><dd>${titleCase(level)}</dd></div><div><dt>Recorded</dt><dd>${formatDate(entry.createdAt || entry.CreatedAt)}</dd></div></dl>${sourcePackage ? html`<button class="ghost compact" @click=${() => this.openExtensionManager(sourcePackage)}>Open extension details</button>` : nothing}</div></details>`;
    };
    const renderInstalled = () => html`<div class="extension-workspace-grid extension-workspace-grid-linear"><div>
      <div class="extension-registry-bar"><div><strong>Active registries</strong><span>${activeRegistries.length ? `${activeRegistries.length} connected` : "No registry connected"}</span></div><button @click=${async () => { await Promise.all([this.loadExtensionControlPlane(), this.loadExtensionStore()]); this.toast("Extension store refreshed"); }}>${icon("refresh", 16)} Refresh</button></div>
      <div class="extension-list">${installedPackages.length ? installedPackages.map((item) => {
        const id = item.id || item.Id;
        const state = this.extensionPackageState(item);
        const action = this.extensionActions[id];
        const label = ({ active: "Enabled", reviewrequired: "Review needed", staged: "Ready", disabled: "Disabled", failed: "Needs attention" })[state] || titleCase(state);
        const extensionId = String(item.extensionId || item.ExtensionId).toLowerCase();
        const update = storeItems.filter((candidate) => String(candidate.id || candidate.Id).toLowerCase() === extensionId && compareExtensionVersions(candidate.version || candidate.Version, item.version || item.Version) > 0).sort((a, b) => compareExtensionVersions(b.version || b.Version, a.version || a.Version))[0];
        const updateAction = update ? this.extensionActions[update.id || update.Id] : "";
        return html`<article class="extension-row">${this.renderExtensionLogo(item, "large")}<div class="extension-row-copy"><div class="extension-row-title"><strong>${item.displayName || item.DisplayName}</strong><span>v${item.version || item.Version}</span></div><small>${display(item.author || item.Author, "Extension package")}</small><p>${display(item.description || item.Description, "No description supplied by this extension.")}</p></div><div class="extension-row-chips">${this.extensionCapabilities(item).map((capability) => html`<span class="chip">${titleCase(capability)}</span>`)}</div><span class="status-chip ${state === "active" ? "configured" : state === "failed" ? "error" : state === "disabled" ? "disabled" : "warning"}">${update ? `v${update.version || update.Version} available` : label}</span><div class="extension-row-actions">${update ? html`<button class="primary" ?disabled=${Boolean(updateAction)} @click=${() => this.installExtension(update, true)}>${updateAction || "Update"}</button>` : nothing}<button ?disabled=${Boolean(action)} @click=${() => this.openExtensionManager(item)}>Manage</button></div></article>`;
      }) : html`<div class="empty"><strong>No extensions installed</strong><span>Install one from a connected registry.</span></div>`}</div>
    </div><section class="panel extension-activity-summary extension-activity-preview"><div class="section-heading"><div><h3>Recent extension activity</h3><p>Expand an event to inspect its package, level, timestamp, and runtime message.</p></div><button @click=${() => { this.extensionViewTab = "activity"; }}>View all activity</button></div><div class="extension-activity-feed">${asArray(this.extensionLogs).slice(0, 6).map((entry) => renderActivityEntry(entry, true))}</div></section></div>`;
    const renderAvailable = () => html`<div class="panel extension-catalog"><div class="section-heading"><div><h3>Available extensions</h3><p>Packages are verified before permission review.</p></div><label class="extension-search">${icon("search", 17)}<input aria-label="Search available extensions" placeholder="Search extensions…" .value=${this.extensionSearch} @input=${(event) => { this.extensionSearch = event.target.value; }}></label></div>${errors.map((error) => html`<div class="error-text">${error.Repository || error.repository}: ${error.Message || error.message}</div>`)}<div class="extension-store-grid">${available.filter((item) => !this.extensionSearch || `${item.displayName || item.DisplayName} ${item.description || item.Description}`.toLowerCase().includes(this.extensionSearch.toLowerCase())).map((item) => html`<article class="extension-store-card"><div class="extension-store-card-heading"><div class="provider-brand">${this.renderExtensionLogo(item, "large")}<div><strong>${item.displayName || item.DisplayName}</strong><small>v${item.version || item.Version}</small></div></div></div><p>${display(item.description || item.Description)}</p><div class="extension-row-chips">${this.extensionCapabilities(item).map((capability) => html`<span class="chip">${titleCase(capability)}</span>`)}</div><button class="primary" ?disabled=${!(item.sha256 || item.Sha256)} @click=${() => this.installExtension(item)}>Install</button></article>`)}</div></div>`;
    const renderRegistries = () => html`<div class="panel"><div class="section-heading"><div><h3>Registries</h3><p>Catalog sources that supply verified extension packages.</p></div></div><form class="config-grid extension-registry-form" @submit=${(event) => this.createExtensionRegistry(event)}><label class="config-field"><span>Name</span><input name="name" required maxlength="200" placeholder="Community catalog"></label><label class="config-field"><span>Registry JSON URL</span><input name="registryUrl" type="url" required pattern="https://.*" placeholder="https://example.org/registry.json"></label><button class="primary" ?disabled=${Boolean(this.extensionActions.registry)}>${this.extensionActions.registry || "Add registry"}</button></form>${this.extensionRegistryError ? html`<div class="error-text">${this.extensionRegistryError}</div>` : nothing}<div class="extension-registry-list">${registries.map((item) => { const enabled = item.enabled ?? item.Enabled; return html`<div><span>${icon("link", 18)}</span><p><strong>${item.name || item.Name}</strong><small>${item.registryUrl || item.RegistryUrl}</small></p><span class="status-chip ${enabled ? "configured" : "disabled"}">${enabled ? "Enabled" : "Disabled"}</span><button @click=${() => this.setExtensionRegistryEnabled(item, !enabled)}>${enabled ? "Disable" : "Enable"}</button></div>`; })}</div></div>`;
    const renderActivity = () => html`<div class="panel extension-activity-workspace"><div class="section-heading"><div><h3>Extension activity</h3><p>Install, update, authorization, and runtime events. Expand a record for its complete context.</p></div></div><div class="extension-activity-feed">${asArray(this.extensionLogs).length ? asArray(this.extensionLogs).map((entry) => renderActivityEntry(entry)) : html`<div class="empty">No extension activity recorded.</div>`}</div></div>`;
    return html`<section class="view-stack extensions-view"><div class="view-header extensions-page-header"><div class="extensions-page-title"><span class="workspace-icon">${icon("extensions", 28)}</span><div><h2>Extensions</h2><p>Install, enable, update, and manage optional provider modules.</p></div></div><button class="primary" @click=${() => { this.extensionInstallOpen = true; }}>${icon("plus", 17)} Install extension</button></div><div class="workspace-tabs" role="tablist" aria-label="Extension views">${[["installed", "Installed", installedPackages.length], ["available", "Available", available.length], ["registries", "Registries", registries.length], ["activity", "Activity", ""]].map(([id, label, count]) => html`<button role="tab" aria-selected=${this.extensionViewTab === id} tabindex=${this.extensionViewTab === id ? "0" : "-1"} class=${this.extensionViewTab === id ? "active" : ""} @keydown=${(event) => this.moveSegmentedTabFocus(event)} @click=${() => { this.extensionViewTab = id; }}>${label}${count !== "" ? html`<span>${count}</span>` : nothing}</button>`)}</div>${this.extensionViewTab === "installed" ? renderInstalled() : this.extensionViewTab === "available" ? renderAvailable() : this.extensionViewTab === "registries" ? renderRegistries() : renderActivity()}${this.renderExtensionManager(managedPackage, (entry) => this.extensionPackageState(entry))}${this.renderExtensionPermissionModal(permissionPackage)}${this.renderExtensionInstallModal(storeItems, installedByExtension)}</section>`;
  }

  renderActivity() {
    return html`
      <section class="view-stack" data-testid="activity-workspace">
        <div class="view-header">
          <div>
            <h2>Event log</h2>
            <p>Background work, download activity, scrobbling, and endpoint usage.</p>
          </div>
          <button class="primary" @click=${async () => { await Promise.all([this.loadDashboardPresentation(), this.loadEndpointUsage(), this.loadScrobbling(), this.loadQueue(), this.loadJobs()]); this.toast("Event log refreshed"); }}>Refresh</button>
        </div>
        ${this.renderEventLogFeed()}
        <div class="panel durable-jobs-panel">
          <h3>Background jobs</h3>
          <p class="muted">Queued work that Allstarr remembers across restarts, retries when appropriate, and records until it finishes or is cancelled.</p>
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
        <div class="panel api-analytics-panel">
          <div class="view-header">
            <div><h3>API analytics</h3></div>
            <button class="danger" @click=${async () => { await API.clearEndpointUsage(); await this.loadEndpointUsage(); this.toast("Endpoint usage cleared"); }}>Clear</button>
          </div>
          ${this.renderEndpointUsage()}
        </div>
      </section>
    `;
  }

  renderEventLogFeed() {
    const all = asArray(this.dashboardActivity);
    const categories = [...new Set(all.map((entry) => entry.kind || entry.Kind).filter(Boolean))].sort();
    const sources = [...new Set(all.map((entry) => entry.source || entry.Source).filter(Boolean))].sort();
    const states = [...new Set(all.map((entry) => entry.state || entry.State).filter(Boolean))].sort();
    const severities = [...new Set(all.map((entry) => entry.severity || entry.Severity || "info"))].sort();
    const providers = [...new Set(all.map((entry) => entry.providerId || entry.ProviderId).filter(Boolean))].sort();
    const playlists = new Map(all.map((entry) => [entry.playlistLinkId || entry.PlaylistLinkId, entry.playlistName || entry.PlaylistName]).filter(([id]) => id));
    const category = this.eventLogCategory || "all";
    const source = this.eventLogSource || "all";
    const selectedState = this.eventLogState || "all";
    const selectedTime = this.eventLogTime || "all";
    const selectedSeverity = this.eventLogSeverity || "all";
    const selectedProvider = this.eventLogProvider || "all";
    const selectedPlaylist = this.eventLogPlaylist || "all";
    const selectedCorrelation = String(this.eventLogCorrelation || "").trim().toLowerCase();
    const query = String(this.eventLogQuery || "").trim().toLowerCase();
    const items = all.filter((entry) => {
      const occurredAt = new Date(entry.occurredAt || entry.OccurredAt).getTime();
      const timeWindow = { hour: 60 * 60 * 1000, day: 24 * 60 * 60 * 1000, week: 7 * 24 * 60 * 60 * 1000 }[selectedTime];
      if (timeWindow && (!Number.isFinite(occurredAt) || occurredAt < Date.now() - timeWindow)) return false;
      if (category !== "all" && (entry.kind || entry.Kind) !== category) return false;
      if (source !== "all" && (entry.source || entry.Source) !== source) return false;
      if (selectedState !== "all" && (entry.state || entry.State) !== selectedState) return false;
      if (selectedSeverity !== "all" && (entry.severity || entry.Severity || "info") !== selectedSeverity) return false;
      if (selectedProvider !== "all" && (entry.providerId || entry.ProviderId) !== selectedProvider) return false;
      if (selectedPlaylist !== "all" && (entry.playlistLinkId || entry.PlaylistLinkId) !== selectedPlaylist) return false;
      if (selectedCorrelation && !String(entry.correlationId || entry.CorrelationId || "").toLowerCase().includes(selectedCorrelation)) return false;
      if (!query) return true;
      return [entry.label, entry.Label, entry.source, entry.Source, entry.detail, entry.Detail, entry.state, entry.State, entry.correlationId, entry.CorrelationId]
        .filter(Boolean).some((value) => String(value).toLowerCase().includes(query));
    });
    const eventIcon = (kind) => ({
      administration: "settings",
      job: "tasks",
      library: "library",
      matching: "search",
      playlist: "playlist",
      provider_health: "activity",
      scrobble: "headphones",
      streaming: "streaming",
    })[kind] || "activity";
    const eventStatusClass = (state) => ["accepted", "delivered", "healthy", "pinned", "succeeded", "success"].includes(String(state).toLowerCase())
      ? "configured"
      : state;
    const groups = [];
    for (const entry of items) {
      const key = [entry.kind || entry.Kind, entry.source || entry.Source, entry.label || entry.Label, entry.state || entry.State]
        .map((value) => String(value || "").toLowerCase()).join("|");
      const previous = groups[groups.length - 1];
      if (previous?.key === key) previous.entries.push(entry);
      else groups.push({ key, entries: [entry] });
    }
    return html`<section class="panel event-log-panel" aria-labelledby="event-log-heading">
      <div class="section-heading"><div><h3 id="event-log-heading">Recent events</h3><p>Matching, playlists, providers, jobs, and administrative changes.</p></div><span class="chip">${items.length} shown</span></div>
      <div class="event-log-filters">
        <label><span>Time</span><select .value=${selectedTime} @change=${(event) => { this.eventLogTime = event.target.value; }}><option value="all">All loaded</option><option value="hour">Last hour</option><option value="day">Last 24 hours</option><option value="week">Last 7 days</option></select></label>
        <label><span>Severity</span><select .value=${selectedSeverity} @change=${(event) => { this.eventLogSeverity = event.target.value; }}><option value="all">All severities</option>${severities.map((value) => html`<option value=${value}>${titleCase(value)}</option>`)}</select></label>
        <label><span>Category</span><select .value=${category} @change=${(event) => { this.eventLogCategory = event.target.value; this.requestUpdate(); }}><option value="all">All categories</option>${categories.map((value) => html`<option value=${value}>${titleCase(value)}</option>`)}</select></label>
        <label><span>Provider / source</span><select .value=${source} @change=${(event) => { this.eventLogSource = event.target.value; this.requestUpdate(); }}><option value="all">All sources</option>${sources.map((value) => html`<option value=${value}>${providerDisplayName(value, this.schema?.providers)}</option>`)}</select></label>
        <label><span>Provider</span><select .value=${selectedProvider} @change=${(event) => { this.eventLogProvider = event.target.value; }}><option value="all">All providers</option>${providers.map((value) => html`<option value=${value}>${providerDisplayName(value, this.schema?.providers)}</option>`)}</select></label>
        <label><span>Playlist</span><select .value=${selectedPlaylist} @change=${(event) => { this.eventLogPlaylist = event.target.value; }}><option value="all">All playlists</option>${[...playlists].map(([id, name]) => html`<option value=${id}>${name || "Playlist"}</option>`)}</select></label>
        <label><span>Outcome</span><select .value=${selectedState} @change=${(event) => { this.eventLogState = event.target.value; this.requestUpdate(); }}><option value="all">All outcomes</option>${states.map((value) => html`<option value=${value}>${titleCase(value)}</option>`)}</select></label>
        <label><span>Correlation ID</span><input .value=${this.eventLogCorrelation || ""} placeholder="Exact request or job" @input=${(event) => { this.eventLogCorrelation = event.target.value; }}></label>
        <label><span>Search</span><input type="search" .value=${this.eventLogQuery || ""} placeholder="Event, provider, correlation ID" @input=${(event) => { this.eventLogQuery = event.target.value; this.requestUpdate(); }}></label>
      </div>
      <div class="event-log-list" role="list">
        ${groups.length ? groups.map((group) => {
          const entry = group.entries[0];
          const kind = entry.kind || entry.Kind || "event";
          const state = entry.state || entry.State || "unknown";
          const severity = entry.severity || entry.Severity || "info";
          const sourceName = providerDisplayName(entry.source || entry.Source || "system", this.schema?.providers);
          return html`<details class="event-log-group" role="listitem">
            <summary class="event-log-entry">
              <span class=${`event-kind event-${kind}`}>${icon(eventIcon(kind), 17)}</span>
              <span class="event-log-summary-copy"><strong>${titleCase(entry.label || entry.Label || kind)}</strong><small>${sourceName} · ${entry.detail || entry.Detail || "No additional detail"}</small></span>
              ${group.entries.length > 1 ? html`<span class="event-log-group-count">${group.entries.length} events</span>` : nothing}
              <span class=${`status-chip ${eventStatusClass(state)}`} title=${`${titleCase(severity)} severity`}>${titleCase(state)}</span>
              <time datetime=${entry.occurredAt || entry.OccurredAt}>${formatRelativeTime(entry.occurredAt || entry.OccurredAt)}</time>
              ${icon("chevronRight", 16)}
            </summary>
            <div class="event-log-details">
              ${group.entries.map((item) => {
                const correlation = item.correlationId || item.CorrelationId;
                const provider = item.providerId || item.ProviderId;
                const playlist = item.playlistName || item.PlaylistName;
                return html`<article class="event-log-detail">
                  <div><strong>${item.detail || item.Detail || "No additional detail"}</strong><time datetime=${item.occurredAt || item.OccurredAt}>${formatDate(item.occurredAt || item.OccurredAt)}</time></div>
                  <dl>
                    <div><dt>Source</dt><dd>${providerDisplayName(item.source || item.Source || "system", this.schema?.providers)}</dd></div>
                    ${provider ? html`<div><dt>Provider</dt><dd>${providerDisplayName(provider, this.schema?.providers)}</dd></div>` : nothing}
                    ${playlist ? html`<div><dt>Playlist</dt><dd>${playlist}</dd></div>` : nothing}
                    ${correlation ? html`<div><dt>Correlation ID</dt><dd><code>${correlation}</code></dd></div>` : nothing}
                  </dl>
                </article>`;
              })}
              ${group.entries.length > 1 ? html`<button class="event-log-collapse" @click=${(event) => event.currentTarget.closest("details")?.removeAttribute("open")}>Collapse ${group.entries.length} events</button>` : nothing}
            </div>
          </details>`;
        }) : html`<div class="empty">No events match these filters.</div>`}
      </div>
      ${this.eventLogHasMore ? html`<div class="event-log-pagination"><button ?disabled=${this.eventLogLoading} @click=${() => this.loadEarlierEvents()}>${this.eventLogLoading ? "Loading…" : "Load earlier events"}</button></div>` : nothing}
    </section>`;
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
      return html`
        <div class="empty">
          <strong>No background jobs have been enqueued yet.</strong>
          <p class="muted">This list covers background work that survives restarts and keeps its history.</p>
          <p class="muted">Imported legacy playlists use the earlier scheduler and appear under <a href="#/library/playlists">Library &gt; Playlists</a>; that older work is not recorded here yet.</p>
          <p class="muted">Managed playlist syncs, library actions, recommendations, and other background operations will appear here after they run.</p>
        </div>
      `;
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
              const copy = jobCopy(job.type || job.Type, state, failure);
              return html`
                <tr>
                  <td><strong>${copy.label}</strong><div class="muted">${copy.description}</div><details class="job-technical-details"><summary>Technical details</summary><span class="mono">${display(job.type || job.Type)} · ${id}</span></details></td>
                  <td><span class="status-chip ${state === "Succeeded" ? "configured" : state === "Failed" ? "degraded" : "needs_config"}">${display(state)}</span></td>
                  <td>
                    <div>Runs: ${display(job.attemptCount ?? job.AttemptCount ?? 0)}</div>
                    <div class="muted">Failures: ${display(job.failureCount ?? job.FailureCount ?? 0)} / ${display(job.maxAttempts ?? job.MaxAttempts ?? 0)}</div>
                    <div class="muted">Waits: ${display(job.deferralCount ?? job.DeferralCount ?? 0)} / ${display(job.maxDeferrals ?? job.MaxDeferrals ?? 0)}</div>
                  </td>
                  <td>${formatDate(job.completedAt || job.CompletedAt || job.availableAt || job.AvailableAt)}</td>
                  <td>${failure ? html`<span class="error-text">${copy.explanation}</span>` : html`<span class="muted">No failure</span>`}</td>
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
    const lastFm = status.lastFm || status.LastFm || {};
    const listenBrainz = status.listenBrainz || status.ListenBrainz || {};
    const lastFmManaged = String(lastFm.source || lastFm.Source || "") === "user_account";
    const listenBrainzManaged = String(listenBrainz.source || listenBrainz.Source || "") === "user_account";
    const capabilityHealth = (provider) => {
      const samples = this.providerHealth.filter((item) =>
        String(item.provider || item.Provider || item.providerId || item.ProviderId || "").toLowerCase() === provider);
      if (samples.some((item) => String(item.health || item.Health).toLowerCase() === "healthy")) return "healthy";
      if (samples.some((item) => String(item.health || item.Health).toLowerCase() === "degraded")) return "degraded";
      return "unknown";
    };
    const lastFmHealth = capabilityHealth("lastfm");
    const listenBrainzHealth = capabilityHealth("listenbrainz");
    const accountLabel = (configured, health) => !configured ? "Needs setup" : health === "healthy" ? "Connected" : health === "degraded" ? "Rejected" : "Stored · not tested";
    const accountClass = (configured, health) => !configured ? "needs_config" : health === "healthy" ? "configured" : health === "degraded" ? "degraded" : "unknown";
    const fields = [
      ...(!lastFmManaged ? [
        { key: "SCROBBLING_LASTFM_ENABLED", label: "Last.fm", type: "toggle", valuePath: "scrobbling.lastFm.enabled" },
        { key: "SCROBBLING_LASTFM_API_KEY", label: "Last.fm API key", type: "password", valuePath: "scrobbling.lastFm.apiKey", sensitive: true },
        { key: "SCROBBLING_LASTFM_SHARED_SECRET", label: "Last.fm secret", type: "password", valuePath: "scrobbling.lastFm.sharedSecret", sensitive: true },
      ] : []),
      ...(!listenBrainzManaged ? [
        { key: "SCROBBLING_LISTENBRAINZ_ENABLED", label: "ListenBrainz", type: "toggle", valuePath: "scrobbling.listenBrainz.enabled" },
        { key: "SCROBBLING_LISTENBRAINZ_USER_TOKEN", label: "ListenBrainz token", type: "password", valuePath: "scrobbling.listenBrainz.userToken", sensitive: true },
      ] : []),
    ];
    const localTracksEnabled = Boolean(status.localTracksEnabled ?? status.LocalTracksEnabled);
    const lastFmConfigured = Boolean(lastFm.configured ?? lastFm.Configured);
    const listenBrainzConfigured = Boolean(listenBrainz.configured ?? listenBrainz.Configured);
    return html`
      <div class="stat-list">
        <div class="stat-row"><span>Runtime</span><span class="status-chip ${status.enabled || status.Enabled ? "configured" : "needs_config"}">${status.enabled || status.Enabled ? "Enabled" : "Disabled"}</span></div>
        <div class="stat-row"><span>Last.fm account</span><span class="actions"><span class="status-chip ${accountClass(lastFmConfigured, lastFmHealth)}">${accountLabel(lastFmConfigured, lastFmHealth)}</span>${lastFmHealth === "degraded" ? html`<button class="compact" @click=${() => this.navigate("/sources")}>Reconnect</button>` : nothing}</span></div>
        <div class="stat-row"><span>ListenBrainz account</span><span class="status-chip ${accountClass(listenBrainzConfigured, listenBrainzHealth)}">${accountLabel(listenBrainzConfigured, listenBrainzHealth)}</span></div>
      </div>
      ${lastFmManaged || listenBrainzManaged ? html`<div class="callout"><strong>Personal accounts are managed in Sources</strong><p>Imported credentials are encrypted in your Allstarr account, so their old host <code>.env</code> fields are intentionally blank and hidden here.</p><button @click=${() => this.navigate("/sources")}>Manage connected accounts</button></div>` : nothing}
      ${!localTracksEnabled ? html`<div class="callout warning"><strong>Local songs are not being scrobbled</strong><p>Enable Local tracks below if plays from your Jellyfin or Subsonic library should be submitted to Last.fm and ListenBrainz.</p></div>` : nothing}
      <form class="config-grid" @submit=${this.saveScrobblingSettings}>
        <div class="config-field"><div class="field-heading"><label class="field-label" for="scrobbling-runtime-enabled">Scrobbling</label></div><label class="inline-check"><input id="scrobbling-runtime-enabled" name="enabled" type="checkbox" .checked=${parseBoolValue(getPathValue(config, "scrobbling.enabled", false))}><span>Submit eligible listening activity</span></label></div>
        <div class="config-field"><div class="field-heading"><label class="field-label" for="scrobbling-local-enabled">Local tracks</label></div><label class="inline-check"><input id="scrobbling-local-enabled" name="localTracksEnabled" type="checkbox" .checked=${parseBoolValue(getPathValue(config, "scrobbling.localTracksEnabled", false))}><span>Include songs stored in my media library</span></label></div>
        <div class="actions full-span"><button class="primary" type="submit">Save scrobbling settings</button><small class="muted">A restart prompt appears when a changed setting needs to be applied.</small></div>
      </form>
      <div class="config-grid">${fields.map((field) => this.renderConfigField(field))}</div>
      <div class="actions scrobble-actions">
        <button @click=${() => this.runServiceAction("lastfm", API.testLastFm)}>Test Last.fm</button>
        <button @click=${() => this.runServiceAction("listenbrainz", API.testListenBrainz)}>Test ListenBrainz</button>
      </div>
      ${this.serviceResults.lastfm ? html`<div class="callout ${this.serviceResults.lastfm.state}">Last.fm: ${this.serviceResults.lastfm.message}</div>` : nothing}
      ${this.serviceResults.listenbrainz ? html`<div class="callout ${this.serviceResults.listenbrainz.state}">ListenBrainz: ${this.serviceResults.listenbrainz.message}</div>` : nothing}
    `;
  }

  saveScrobblingSettings = async (event) => {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    const settings = [
      { key: "SCROBBLING_ENABLED", label: "Scrobbling", type: "toggle", valuePath: "scrobbling.enabled", requiresRestart: true, value: data.has("enabled") ? "true" : "false" },
      { key: "SCROBBLING_LOCAL_TRACKS_ENABLED", label: "Local tracks", type: "toggle", valuePath: "scrobbling.localTracksEnabled", requiresRestart: true, value: data.has("localTracksEnabled") ? "true" : "false" },
    ];
    try {
      for (const setting of settings) await this.saveField(setting, setting.value);
      this.toast("Scrobbling settings saved");
    } catch (error) {
      this.toast(error.message, "error");
    }
  };

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
    const [, sub] = routeParts(this.route);
    if (sub === "extensions" && this.isAdministrator()) return this.renderExtensions();
    if (!this.isAdministrator()) {
      return html`
        <section class="view-stack">
          <div class="view-header"><div><h2>Settings</h2><p>Manage your own connected provider accounts.</p></div></div>
          <div class="panel">
            <div class="section-heading account-section-heading"><div><h3>Connected accounts</h3><p>Credentials are encrypted and kept separate from the Sources catalog. Apple MusicKit uses a Music User Token for your library and playlists; the Apple Music extension adds its own account option for subscription lyrics.</p></div>${this.canManageProviderAccounts() ? html`<button class="primary icon-label" @click=${() => this.openProviderAccountModal()}>${icon("plus", 17)}<span>Add account</span></button>` : nothing}</div>
            ${this.renderProviderAccounts()}
          </div>
          ${this.renderProviderAccountModal()}
        </section>`;
    }
    return html`
      <section class="view-stack settings-view">
        <div class="view-header settings-page-header">
          <div>
            <h2>Settings</h2>
            <p>Accounts, application preferences, and maintenance.</p>
          </div>
          <button @click=${() => this.navigate("/settings/extensions")}>${icon("extensions", 17)} Manage extensions</button>
        </div>
        <div class="panel">
          <div class="section-heading account-section-heading"><div><h3>Connected accounts</h3><p>Credentials are encrypted and kept separate from the Sources catalog. Apple MusicKit uses a Music User Token for your library and playlists; the Apple Music extension adds its own account option for subscription lyrics.</p></div>${this.canManageProviderAccounts() ? html`<button class="primary icon-label" @click=${() => this.openProviderAccountModal()}>${icon("plus", 17)}<span>Add account</span></button>` : nothing}</div>
          ${this.renderProviderAccounts()}
        </div>
        <section class="settings-routing" aria-labelledby="provider-routing-heading">
          <div class="section-heading">
            <div>
              <h3 id="provider-routing-heading">Provider routing</h3>
              <p>Allstarr tries local library tracks first. Remaining tracks follow each provider order from top to bottom.</p>
              <p class="muted">This section is at: <strong>Settings → Provider routing</strong>.</p>
            </div>
          </div>
          ${this.renderPriorityGroups()}
        </section>
        ${this.renderProviderAccountModal()}
        ${asArray(this.schema?.configSections).map((section) => html`
          <details class="content-disclosure panel settings-disclosure">
            <summary><span><strong>${section.label}</strong><small>Show configuration</small></span></summary>
            <div class="config-grid disclosure-body">
              ${asArray(section.fields).map((field) => this.renderConfigField(field))}
            </div>
          </details>
        `)}
        <details class="content-disclosure panel settings-disclosure">
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
        <details class="content-disclosure panel settings-disclosure" open>
          <summary><span><strong>Media diagnostics</strong><small>Verify metadata and album artwork through Allstarr</small></span></summary>
          <div class="disclosure-body">
            <p class="muted">Runs a read-only check against a small sample from your active media server. It does not reveal track names, IDs, credentials, or server addresses.</p>
            <div class="actions">
              <button
                class="primary"
                ?disabled=${this.serviceResults.media?.state === "running"}
                @click=${this.runMediaProbe}
              >${this.serviceResults.media?.state === "running" ? "Testing media..." : "Test metadata and artwork"}</button>
            </div>
            ${this.serviceResults.media ? html`
              <div class="callout ${this.serviceResults.media.state}" role="status">
                <strong>${this.serviceResults.media.state === "success" ? "Media pipeline ready" : this.serviceResults.media.state === "running" ? "Checking media pipeline" : "Media pipeline needs attention"}</strong>
                <span>${this.serviceResults.media.message}</span>
                ${this.serviceResults.media.details ? html`<small>${this.serviceResults.media.details}</small>` : nothing}
              </div>` : nothing}
          </div>
        </details>
        <details class="content-disclosure panel settings-disclosure" open>
          <summary><span><strong>Playlist diagnostics</strong><small>Verify restored playlists and playable entries</small></span></summary>
          <div class="disclosure-body">
            <p class="muted">Checks configured playlists, cached provider data, and the final items shown to players. It does not reveal playlist or track names.</p>
            <div class="actions">
              <button
                class="primary"
                ?disabled=${this.serviceResults.playlists?.state === "running"}
                @click=${this.runPlaylistReadinessProbe}
              >${this.serviceResults.playlists?.state === "running" ? "Testing playlists..." : "Test playlist readiness"}</button>
            </div>
            ${this.serviceResults.playlists ? html`
              <div class="callout ${this.serviceResults.playlists.state}" role="status">
                <strong>${this.serviceResults.playlists.state === "success" ? "Playlist pipeline ready" : this.serviceResults.playlists.state === "running" ? "Checking playlists" : "Playlist pipeline needs attention"}</strong>
                <span>${this.serviceResults.playlists.message}</span>
                ${this.serviceResults.playlists.details ? html`<small>${this.serviceResults.playlists.details}</small>` : nothing}
              </div>` : nothing}
          </div>
        </details>
        <div class="setup-launcher">
          <div><h3>Setup guide</h3><p>Revisit the media server, sources, and first playlist steps whenever you need them.</p></div>
          <button @click=${() => this.openSetupGuide()}>Open setup guide</button>
        </div>
        ${this.renderEnvMigrationWizard()}
        <details class="content-disclosure panel danger-disclosure settings-disclosure">
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
        <div class="setup-legacy-path"><p>This step is safe to revisit. Playlist sync never deletes or rewrites audio files.</p><button @click=${() => this.leaveSetupGuideFor("/library/playlists")}>Open playlists</button></div>
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
            ${asArray(field.options).map((option) => html`
              <option value=${option} ?selected=${String(option) === String(value)}>${configOptionLabel(field, option)}</option>
            `)}
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
    clearTimeout(this.envMigrationExpiryTimer);
    this.envMigrationExpiryTimer = null;
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
      clearTimeout(this.envMigrationExpiryTimer);
      const expiresAt = Date.parse(preview.expiresAt || preview.ExpiresAt || "");
      if (Number.isFinite(expiresAt)) {
        this.envMigrationExpiryTimer = setTimeout(() => {
          this.resetEnvMigration();
          this.toast("Migration preview expired. Upload the file again to continue.", "warning");
        }, Math.max(0, expiresAt - Date.now()));
      }
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
      const playlistHandoffs = asArray(preview.playlistHandoffs || preview.PlaylistHandoffs);
      const result = await API.applyEnvMigration(previewToken, revision);
      clearTimeout(this.envMigrationExpiryTimer);
      this.envMigrationExpiryTimer = null;
      this.envMigration = { state: "success", sourceName: this.envMigration.sourceName, preview: null, result: { ...result, playlistHandoffs }, error: "" };
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
        handoff: playlist,
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
      user_accounts: "Your personal accounts",
      reconnects: "Your personal accounts",
      per_user_reconnects: "Your personal accounts",
      per_user_manual: "Your personal accounts",
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
    if (["import", "imported", "import_if_absent", "import_for_current_user", "ready", "durable"].includes(state)) return "configured";
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

  migrationOptionalRuntimeServices() {
    const preview = this.envMigration.preview || {};
    const keys = new Set(asArray(preview.items || preview.Items)
      .map((item) => String(item.key || item.Key || "").toUpperCase()));
    const services = [];
    if (keys.has("SPOTIFY_LYRICS_API_URL")) {
      services.push({
        id: "spotify-lyrics",
        title: "Spotify lyrics sidecar",
        text: "The endpoint URL is imported as a runtime setting, but the WebUI does not start containers or give the sidecar your Spotify cookie. Put the cookie in the host .env, then enable the saved optional profile with Allstarr's deployment helper.",
        guide: "https://github.com/SoPat712/allstarr/blob/dev/docs/operations/spotify-lyrics-sidecar.md",
        command: "./allstarr.sh enable spotify-lyrics\n./allstarr.sh up\n./allstarr.sh status",
      });
    }
    if (["APPLE_DOWNLOAD_URL", "APPLE_MUSIC_AIO_URL", "APPLE_DOWNLOAD_QUALITY", "APPLE_MUSIC_QUALITY"]
      .some((key) => keys.has(key))) {
      services.push({
        id: "apple-download",
        title: "Apple download gateway",
        text: "The endpoint URL is imported as a runtime setting. Prepare the optional Apple profile with legally obtained Apple Music Android libraries, then verify it under Sources > Apple download. Do not point Allstarr directly at wrapper-v2.",
        guide: "https://github.com/SoPat712/allstarr/blob/dev/docs/operations/apple-download-provider.md",
        command: "./allstarr.sh install-apple x86_64\n# Upload the package in Sources > Apple download first, then finish login",
      });
    }
    return services;
  }

  renderMigrationOptionalRuntimeGuidance() {
    const services = this.migrationOptionalRuntimeServices();
    if (!services.length) return nothing;
    return html`<aside class="callout warning" aria-label="Optional service setup required">
      <h4>Optional services still need setup</h4>
      <p>These endpoints stay outside Allstarr. No URL, login, token, or session value is shown here.</p>
      ${services.map((service) => html`<section>
        <strong>${service.title}</strong>
        <p>${service.text}</p>
        <p><a href=${service.guide} target="_blank" rel="noopener noreferrer">Open the setup guide</a></p>
        <pre><code>${service.command}</code></pre>
      </section>`)}
    </aside>`;
  }

  migrationEntryIsSensitive(entry) {
    if (entry.sensitive ?? entry.Sensitive ?? entry.isSecret ?? entry.IsSecret) return true;
    const key = String(entry.key || entry.Key || entry.sourceKey || entry.SourceKey || "");
    return /(password|secret|token|cookie|api[_-]?key|\barl\b|credential)/i.test(key);
  }

  migrationEntryValue(entry) {
    if (this.redactionMode && this.migrationEntryIsSensitive(entry)) return "[redacted]";
    return display(entry.displayValue ?? entry.DisplayValue ?? entry.previewValue ?? entry.PreviewValue ??
      entry.redactedValue ?? entry.RedactedValue ?? entry.value ?? entry.Value);
  }

  async continueLegacyPlaylistHandoff(handoff) {
    await this.loadPlaylistDiscovery();
    const sourceAccounts = this.playlistSources.filter((item) =>
      String(item.providerId || item.ProviderId).toLowerCase() === "spotify");
    const jellyfinTargets = this.mediaTargets.filter((item) =>
      String(item.protocol || item.Protocol).toLowerCase() === "jellyfin");
    const sourceAccountId = sourceAccounts.length === 1 ? String(sourceAccounts[0].id || sourceAccounts[0].Id) : "";
    const targetIdentityId = jellyfinTargets.length === 1 ? String(jellyfinTargets[0].id || jellyfinTargets[0].Id) : "";
    const sourcePlaylist = {
      id: handoff.sourcePlaylistId || handoff.SourcePlaylistId,
      name: handoff.name || handoff.Name,
    };
    const targetPlaylistId = handoff.jellyfinTargetPlaylistId || handoff.JellyfinTargetPlaylistId || "";
    const schedule = handoff.syncSchedule || handoff.SyncSchedule || "0 8 * * *";
    this.playlistWizard = {
      ...this.newPlaylistWizardDraft(),
      sourceAccountId,
      sourcePlaylist,
      sourceQuery: sourcePlaylist.id,
      targetIdentityId,
      targetPlaylist: targetIdentityId && targetPlaylistId ? { id: targetPlaylistId, name: sourcePlaylist.name } : null,
      createTarget: Boolean(targetIdentityId && !targetPlaylistId),
      trigger: "scheduled",
      cronExpression: schedule,
      legacyHandoff: handoff,
      step: sourceAccountId ? 1 : 0,
    };
    this.sourcePlaylistResults = [sourcePlaylist];
    this.targetPlaylistResults = this.playlistWizard.targetPlaylist ? [this.playlistWizard.targetPlaylist] : [];
    this.navigate("/library/playlists");
    this.toast(sourceAccountId
      ? "Legacy playlist details loaded. Confirm the destination and behavior."
      : "Legacy playlist details loaded. Choose its owning Spotify account to continue.");
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
    const resultPlaylistHandoffs = asArray(result.playlistHandoffs || result.PlaylistHandoffs);
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
            <p class="muted">Preview supported settings before importing them into the new configuration and encrypted account model. Uploaded values exist only in this short-lived preview.</p>
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
            <div class="callout warning"><strong>Review before applying.</strong> Existing settings are not changed until you confirm. ${this.redactionMode ? "Sharing redaction is on, so sensitive values are hidden." : "Sensitive values are visible to you in this short-lived preview. Turn on sharing redaction in the sidebar before taking screenshots."}</div>
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
                    <td><span class="status-chip ${this.migrationEntryStatusClass(entry)}">${titleCase(this.migrationEntryState(entry))}</span>${entry.warning || entry.Warning ? html`<div class="warning-text">${display(entry.warning || entry.Warning)}</div>` : nothing}${entry.handoff ? html`<button class="compact" type="button" @click=${() => this.continueLegacyPlaylistHandoff(entry.handoff)}>Continue in Playlists</button>` : nothing}</td>
                  </tr>`)}</tbody>
                </table></div>
              </section>`;
            }) : html`<div class="empty">No supported legacy settings were found. Nothing will be changed.</div>`}
            <div class="callout"><strong>What confirmation means</strong><p>Rows marked for durable import are applied automatically. The signed-in administrator's Last.fm and ListenBrainz credentials become encrypted personal accounts owned only by that user. Disabled shared accounts remain disabled, deployment-only values stay on the host checklist, and playlists requiring a target or owner remain handoffs.</p></div>
            ${this.renderMigrationOptionalRuntimeGuidance()}
            <form class="env-migration-confirm" @submit=${(event) => this.applyEnvMigration(event)}>
              <label class="inline-check">
                <input name="confirmMigration" type="checkbox" required ?disabled=${migration.state === "applying"}>
                <span>I reviewed this preview and authorize Allstarr to add the settings marked ready, create the listed shared provider accounts in a disabled state, and create the listed encrypted personal accounts for my signed-in user. Existing durable settings stay unchanged, and existing accounts are never overwritten.</span>
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
            ${this.renderMigrationOptionalRuntimeGuidance()}
            <dl><div><dt>Durable settings</dt><dd>${this.migrationResultCount(result.settingsImported ?? result.SettingsImported ?? result.importedSettings ?? result.ImportedSettings)}</dd></div><div><dt>Disabled accounts created</dt><dd>${this.migrationResultCount(result.providerAccountsCreated ?? result.ProviderAccountsCreated)}</dd></div><div><dt>Skipped</dt><dd>${this.migrationResultCount(result.settingsSkipped ?? result.SettingsSkipped) + this.migrationResultCount(result.providerAccountsSkipped ?? result.ProviderAccountsSkipped)}</dd></div><div><dt>Manual checklist</dt><dd>${this.migrationResultCount(result.manualChecklistItems ?? result.ManualChecklistItems)}</dd></div><div><dt>Playlist handoffs</dt><dd>${this.migrationResultCount(result.playlistHandoffsPending ?? result.PlaylistHandoffsPending)}</dd></div></dl>
            ${resultPlaylistHandoffs.length ? html`<section class="env-migration-result-section" aria-labelledby="migration-result-playlists"><h5 id="migration-result-playlists">Finish playlist migration</h5><div class="migration-playlist-handoffs">${resultPlaylistHandoffs.map((handoff) => html`<div><span><strong>${handoff.name || handoff.Name}</strong><small class="mono">${handoff.sourcePlaylistId || handoff.SourcePlaylistId}</small></span><button type="button" @click=${() => this.continueLegacyPlaylistHandoff(handoff)}>Continue in Playlists</button></div>`)}</div></section>` : nothing}
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

  getRecentPlayback() {
    const current = this.activity.find((item) => item.isPlaying || item.IsPlaying);
    if (!current) return null;

    const rawLastActivity = current.playbackLastActivity || current.PlaybackLastActivity;
    if (rawLastActivity) {
      const lastActivity = Date.parse(rawLastActivity);
      if (Number.isFinite(lastActivity) && Date.now() - lastActivity > 2 * 60 * 1000) {
        return null;
      }
    }
    return current;
  }

  renderNowPlaying() {
    const current = this.getRecentPlayback();
    if (!current) return nothing;
    const title = current ? display(current.title || current.Title, "Active download") : "No active playback";
    const artist = current ? display(current.artist || current.Artist) : "Queue is idle";
    const coverArtUrl = current?.coverArtUrl || current?.CoverArtUrl || "/placeholder.png";
    const position = Number(current?.playbackPositionSeconds ?? current?.PlaybackPositionSeconds) || 0;
    const duration = Number(current?.durationSeconds ?? current?.DurationSeconds) || 0;
    const playbackProgress = current?.playbackProgress ?? current?.PlaybackProgress;
    const lastActivity = Date.parse(current?.playbackLastActivity || current?.PlaybackLastActivity || "");
    const interpolationSeconds = Number.isFinite(lastActivity)
      ? Math.max(0, Math.min(5, (this.nowPlayingClock - lastActivity) / 1000))
      : 0;
    const visualPosition = duration > 0 ? Math.min(duration, position + interpolationSeconds) : position;
    const progress = duration > 0 ? percent(visualPosition / duration) : percent(playbackProgress);
    const source = providerDisplayName(current?.externalProvider || current?.ExternalProvider || "jellyfin", this.schema?.providers);
    const sourceId = current?.externalProvider || current?.ExternalProvider || "jellyfin";
    const scrobbled = Boolean(current?.scrobbled ?? current?.Scrobbled);
    const scrobbleError = current?.scrobbleError || current?.ScrobbleError || current?.scrobbleFailure || current?.ScrobbleFailure;
    const scrobbleState = scrobbleError ? "failed" : scrobbled ? "delivered" : "pending";
    return html`
      <footer class="now-playing" data-testid="now-playing">
        <div class="now-track">
          <img class="art" src=${coverArtUrl} alt="">
          <div>
            <div class="now-title">${title}</div>
            <div class="now-meta">${artist}</div>
          </div>
        </div>
        <div class="now-progress">
          <div class="now-progress-labels"><span>${formatDuration(visualPosition)}</span><strong>Now playing</strong><span>${duration > 0 ? formatDuration(duration) : "–:––"}</span></div>
          <div class="progress" role="progressbar" aria-label="Playback progress" aria-valuemin="0" aria-valuemax="100" aria-valuenow=${Math.round(progress)} style=${`--progress-scale:${progress / 100}`}><span></span></div>
        </div>
        <div class="now-status" aria-label="Playback source and scrobbling status">
          <span class="playback-source">${this.renderProviderLogo(sourceId, "tiny")}${source}</span>
          <span class="scrobble-status ${scrobbleState}" title=${scrobbleError || (scrobbled ? "Listening history delivered" : "Waiting for the scrobble threshold")}>${scrobbleState === "delivered" ? html`${icon("check", 15)} Scrobbled` : scrobbleState === "failed" ? html`${icon("warning", 15)} Scrobble failed` : html`${icon("clock", 15)} Scrobble pending`}</span>
        </div>
      </footer>
    `;
  }

  renderToasts() {
    const activeJobs = asArray(this.jobs).filter((job) => !["Succeeded", "Failed", "Cancelled"].includes(job.state || job.State)).slice(0, 3);
    const activeDownloads = asArray(this.activity).filter((item) => String(item.status || item.Status).toLowerCase().includes("progress")).slice(0, 3);
    if (!this.toasts.length && !activeJobs.length && !activeDownloads.length) return nothing;
    return html`
      <div class="toast-stack operation-center" aria-live="polite">
        ${activeJobs.length || activeDownloads.length ? html`<div class="operation-heading"><span>${icon("activity", 16)}<strong>Working</strong></span><small>${activeJobs.length + activeDownloads.length} active</small></div>` : nothing}
        ${activeJobs.map((job) => html`<div class="operation-item"><span><strong>${titleCase(job.type || job.Type)}</strong><small>${titleCase(job.state || job.State)}</small></span><div class="progress indeterminate"><span></span></div></div>`)}
        ${activeDownloads.map((item) => html`<div class="operation-item"><span><strong>${display(item.title || item.Title)}</strong><small>${display(item.status || item.Status)}</small></span><div class="progress" style=${`--progress:${percent(item.progress ?? item.Progress)}%`}><span></span></div></div>`)}
        ${this.toasts.map((toast) => html`<div class="toast ${toast.type}">${toast.type === "error" ? icon("warning", 16) : icon("check", 16)}<span>${toast.message}</span></div>`)}
      </div>
    `;
  }
}

customElements.define("allstarr-app", AllstarrApp);
