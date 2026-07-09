import { LitElement, html, nothing } from "https://cdn.jsdelivr.net/npm/lit@3/+esm";

const THEME_KEY = "allstarr-theme";
const DEFAULT_ROUTE = "/home";

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

async function readErrorMessage(response, fallback) {
  try {
    const data = await response.json();
    return data.error || data.message || fallback;
  } catch {
    return fallback;
  }
}

async function requestJson(url, options = {}, fallback = "Request failed") {
  const response = await fetch(url, {
    credentials: "same-origin",
    ...options,
  });

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, fallback));
  }

  return response.json();
}

async function requestBlob(url, options = {}, fallback = "Request failed") {
  const response = await fetch(url, {
    credentials: "same-origin",
    ...options,
  });

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, fallback));
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
  importEnv: (file) => {
    const data = new FormData();
    data.append("file", file);
    return requestJson("/api/admin/import-env", { method: "POST", body: data }, "Failed to import .env");
  },
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
  jellyfinUsers: () => requestJson("/api/admin/jellyfin/users", {}, "Failed to load Jellyfin users"),
  jellyfinPlaylists: (userId = "", includeStats = true) => {
    const params = new URLSearchParams({ includeStats: String(includeStats) });
    if (userId) {
      params.set("userId", userId);
    }
    return requestJson(`/api/admin/jellyfin/playlists?${params}`, {}, "Failed to load backend playlists");
  },
  spotifyUserPlaylists: (userId = "") => {
    const suffix = userId ? `?userId=${encodeURIComponent(userId)}` : "";
    return requestJson(`/api/admin/spotify/user-playlists${suffix}`, {}, "Failed to load Spotify playlists");
  },
  linkPlaylist: (jellyfinId, spotifyPlaylistId, syncSchedule, userId = "") => {
    const payload = { spotifyPlaylistId, syncSchedule };
    if (userId) {
      payload.userId = userId;
    }
    return requestJson(`/api/admin/jellyfin/playlists/${encodeURIComponent(jellyfinId)}/link`, jsonBody(payload), "Failed to link playlist");
  },
  unlinkPlaylist: (jellyfinId) =>
    requestJson(`/api/admin/jellyfin/playlists/${encodeURIComponent(jellyfinId)}/unlink`, { method: "DELETE" }, "Failed to unlink playlist"),
  downloads: () => requestJson("/api/admin/downloads", {}, "Failed to load downloads"),
  deleteDownload: (path) =>
    requestJson(`/api/admin/downloads?path=${encodeURIComponent(path)}`, { method: "DELETE" }, "Failed to delete download"),
  deleteAllDownloads: () => requestJson("/api/admin/downloads/all", { method: "DELETE" }, "Failed to delete downloads"),
  endpointUsage: (top = 50) =>
    requestJson(`/api/admin/debug/endpoint-usage?top=${top}`, {}, "Failed to load endpoint usage"),
  clearEndpointUsage: () => requestJson("/api/admin/debug/endpoint-usage", { method: "DELETE" }, "Failed to clear endpoint usage"),
  queue: () => requestJson("/api/admin/downloads/queue", {}, "Failed to load queue"),
  mappings: (params = {}) => requestJson(`/api/admin/spotify/mappings?${new URLSearchParams(params)}`, {}, "Failed to load mappings"),
  saveMapping: (payload) => requestJson("/api/admin/spotify/mappings", jsonBody(payload), "Failed to save mapping"),
  deleteMapping: (spotifyId, provider = "") => {
    const suffix = provider ? `?provider=${encodeURIComponent(provider)}` : "";
    return requestJson(`/api/admin/spotify/mappings/${encodeURIComponent(spotifyId)}${suffix}`, { method: "DELETE" }, "Failed to delete mapping");
  },
  externalPlaylistSearch: (query, provider, limit = 20) => {
    const params = new URLSearchParams({ query, provider, limit: String(limit) });
    return requestJson(`/api/admin/external/playlists/search?${params}`, {}, "Failed to search playlists");
  },
  externalPlaylistTracks: (provider, externalId, limit = 50) =>
    requestJson(`/api/admin/external/playlists/${encodeURIComponent(provider)}/${encodeURIComponent(externalId)}/tracks?limit=${limit}`, {}, "Failed to load external playlist tracks"),
  extensionStore: () => requestJson("/api/admin/extensions/store", {}, "Failed to load extension store"),
  installedExtensions: () => requestJson("/api/admin/extensions/installed", {}, "Failed to load installed extensions"),
  installExtension: (item) =>
    requestJson("/api/admin/extensions/install", jsonBody({ id: item.id || item.Id, downloadUrl: item.downloadUrl || item.DownloadUrl || "" }), "Failed to install extension"),
  uninstallExtension: (id) =>
    requestJson(`/api/admin/extensions/uninstall/${encodeURIComponent(id)}`, { method: "DELETE" }, "Failed to uninstall extension"),
  scrobblingStatus: () => requestJson("/api/admin/scrobbling/status", {}, "Failed to load scrobbling"),
  updateLocalTracksScrobbling: (enabled) =>
    requestJson("/api/admin/scrobbling/local-tracks/update", jsonBody({ enabled }), "Failed to update local scrobbling"),
  testLastFm: () => requestJson("/api/admin/scrobbling/lastfm/test", { method: "POST" }, "Failed to test Last.fm"),
  validateListenBrainz: (userToken) =>
    requestJson("/api/admin/scrobbling/listenbrainz/validate", jsonBody({ userToken }), "Failed to validate ListenBrainz"),
  testListenBrainz: () => requestJson("/api/admin/scrobbling/listenbrainz/test", { method: "POST" }, "Failed to test ListenBrainz"),
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
    schema: { state: true },
    config: { state: true },
    status: { state: true },
    theme: { state: true },
    loginError: { state: true },
    restartKeys: { state: true },
    toasts: { state: true },
    activity: { state: true },
    playlists: { state: true },
    jellyfinPlaylists: { state: true },
    spotifyPlaylists: { state: true },
    jellyfinUsers: { state: true },
    downloads: { state: true },
    endpointUsage: { state: true },
    mappings: { state: true },
    externalPlaylists: { state: true },
    externalPlaylistTracks: { state: true },
    extensionStore: { state: true },
    installedExtensions: { state: true },
    scrobbling: { state: true },
  };

  constructor() {
    super();
    this.authenticated = false;
    this.loading = true;
    this.route = normalizeRoute();
    this.navOpen = false;
    this.session = null;
    this.schema = null;
    this.config = null;
    this.status = null;
    this.theme = ThemeManager.current();
    this.loginError = "";
    this.restartKeys = new Set();
    this.toasts = [];
    this.activity = [];
    this.playlists = null;
    this.jellyfinPlaylists = null;
    this.spotifyPlaylists = null;
    this.jellyfinUsers = [];
    this.downloads = null;
    this.endpointUsage = null;
    this.mappings = null;
    this.externalPlaylists = null;
    this.externalPlaylistTracks = new Map();
    this.extensionStore = null;
    this.installedExtensions = null;
    this.scrobbling = null;
    this.linkSelections = new Map();
    this.mappingFilters = { page: 1, pageSize: 50, enrichMetadata: true, targetType: "all", source: "all", search: "" };
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
      this.route = normalizeRoute();
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

  async bootstrap() {
    this.loading = true;
    try {
      const authState = await API.me();
      if (!(authState.authenticated || authState.Authenticated)) {
        this.authenticated = false;
        this.session = null;
        return;
      }

      this.session = authState.user || authState.User;
      this.authenticated = true;
      await Promise.all([this.loadSchema(), this.loadConfig(), this.loadStatus()]);
      this.startActivityStream();
      await this.loadForRoute();
    } catch (error) {
      this.authenticated = false;
      this.session = null;
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
    this.config = await API.config();
  }

  async loadStatus() {
    this.status = await API.status();
  }

  async loadForRoute(force = false) {
    if (!this.authenticated) {
      return;
    }

    const routeKey = `${this.route}`;
    if (!force && routeKey === this.routeLoadKey) {
      return;
    }
    this.routeLoadKey = routeKey;

    const [zone, sub] = routeParts(this.route);
    try {
      if (zone === "library") {
        if (sub === "link") {
          await this.loadLinkData();
        } else if (sub === "injected") {
          await this.loadPlaylists();
        } else if (sub === "mappings") {
          await this.loadMappings();
        } else if (sub === "missing" || sub === "migration") {
          await this.loadMigrationData();
        } else if (sub === "kept") {
          await this.loadDownloads();
        }
      } else if (zone === "sources") {
        await this.loadInstalledExtensions();
      } else if (zone === "activity") {
        await Promise.all([this.loadEndpointUsage(), this.loadScrobbling(), this.loadQueue()]);
      }
    } catch (error) {
      this.toast(error.message, "error");
    }
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
    if (field.sensitive && !value) {
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

  async loadLinkData() {
    const userId = this.renderRoot?.querySelector?.("#jellyfin-user-filter")?.value || "";
    const requests = [
      API.jellyfinPlaylists(userId, true),
      API.spotifyUserPlaylists(userId).catch((error) => {
        this.toast(error.message, "error");
        return { playlists: [] };
      }),
    ];
    if (this.session?.isAdministrator || this.session?.IsAdministrator) {
      requests.push(API.jellyfinUsers().catch(() => []));
    }
    const [jellyfin, spotify, users = []] = await Promise.all(requests);
    this.jellyfinPlaylists = jellyfin;
    this.spotifyPlaylists = spotify;
    this.jellyfinUsers = Array.isArray(users) ? users : users.users || users.Users || [];
  }

  async loadDownloads() {
    this.downloads = await API.downloads();
  }

  async loadQueue() {
    this.activity = await API.queue();
  }

  async loadEndpointUsage() {
    this.endpointUsage = await API.endpointUsage(50);
  }

  async loadMappings() {
    this.mappings = await API.mappings(this.mappingFilters);
  }

  async loadMigrationData() {
    await this.loadPlaylists();
  }

  async loadInstalledExtensions() {
    this.installedExtensions = await API.installedExtensions();
  }

  async loadExtensionStore() {
    this.extensionStore = await API.extensionStore();
  }

  async loadScrobbling() {
    this.scrobbling = await API.scrobblingStatus();
  }

  render() {
    if (this.loading) {
      return html`<div class="app-loading"><div class="chip">Loading Allstarr</div></div>`;
    }

    if (!this.authenticated) {
      return this.renderAuth();
    }

    return html`
      <div class="app-shell">
        ${this.renderSidebar()}
        <div class="main-shell">
          ${this.renderTopbar()}
          <main class="content">${this.renderRoute()}</main>
        </div>
      </div>
      ${this.renderRestartBar()}
      ${this.renderNowPlaying()}
      ${this.renderToasts()}
    `;
  }

  renderAuth() {
    return html`
      <section class="auth-screen">
        <div class="auth-card">
          <h1>Allstarr</h1>
          <p>Sign in with a Jellyfin account to manage this server.</p>
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

  renderSidebar() {
    const routes = asArray(this.schema?.routes);
    return html`
      <aside class="sidebar ${this.navOpen ? "open" : ""}">
        <div class="brand">
          <a class="brand-title" href="#/home">Allstarr</a>
          <div class="brand-subtitle">${display(this.status?.version || this.status?.Version, "Media manager")}</div>
          <span class="status-chip configured">${display(this.schema?.activeBackend || this.config?.backendType)}</span>
        </div>
        <nav class="nav-list" aria-label="Primary">
          ${routes.map((route) => html`
            <a class="nav-link ${this.isRouteActive(route.path) ? "active" : ""}" href=${route.path}>
              <span>${route.label}</span>
            </a>
          `)}
        </nav>
        <div class="sidebar-footer">
          <div>Signed in as <strong>${display(this.session?.name || this.session?.Name)}</strong></div>
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
    return html`
      <header class="topbar">
        <div>
          <button class="mobile-menu ghost" @click=${() => { this.navOpen = true; }}>Menu</button>
          <h1>${titleCase(zone || "home")}${sub ? html` <span class="muted">/ ${titleCase(sub)}</span>` : nothing}</h1>
          <div class="topbar-meta">${display(this.status?.spotify?.authStatus || this.status?.Spotify?.AuthStatus, "status unknown")}</div>
        </div>
        <div class="actions">
          <select aria-label="Theme" .value=${this.theme} @change=${(event) => this.setTheme(event.target.value)}>
            <option value="system">System</option>
            <option value="dark">Dark</option>
            <option value="light">Light</option>
          </select>
          <button class="ghost" @click=${async () => { await Promise.all([this.loadStatus(), this.loadConfig()]); this.toast("Status refreshed"); }}>Refresh</button>
        </div>
      </header>
    `;
  }

  renderRoute() {
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
    return this.renderHome();
  }

  renderHome() {
    const spotify = this.status?.spotify || this.status?.Spotify || {};
    const spotifyImport = this.status?.spotifyImport || this.status?.SpotifyImport || {};
    const providerCards = asArray(this.schema?.providers).filter((provider) =>
      ["squidwtf", "applemusic", "deezer", "qobuz"].includes(provider.id),
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

        <div class="wide-grid">
          <div class="panel">
            <h3>Setup</h3>
            <div class="stat-list">
              ${this.renderSetupStep("Backend configured", Boolean(this.config?.jellyfin?.url || this.config?.subsonic?.url))}
              ${this.renderSetupStep("Spotify cookie present", Boolean(spotify.hasCookie || spotify.HasCookie))}
              ${this.renderSetupStep("Download source selected", Boolean(this.config?.musicService))}
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

  renderSetupStep(label, complete) {
    return html`
      <div class="stat-row">
        <span>${label}</span>
        <span class="chip ${complete ? "success" : "warning"}">${complete ? "Ready" : "Needs setup"}</span>
      </div>
    `;
  }

  renderLibrary() {
    const [, sub = "overview"] = routeParts(this.route);
    return html`
      <section class="view-stack">
        <div class="view-header">
          <div>
            <h2>Library</h2>
            <p>Backend playlists, injected playlists, mappings, missing tracks, and kept files.</p>
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
          this.renderLibraryOverview()}
      </section>
    `;
  }

  renderLibraryNav(active) {
    const items = [
      ["overview", "Overview"],
      ["link", "Link playlists"],
      ["injected", "Injected"],
      ["mappings", "Mappings"],
      ["missing", "Missing"],
      ["migration", "Migration"],
      ["kept", "Kept"],
      ["external", "External playlists"],
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
          <p class="muted">Connect backend playlists to Spotify playlist sources.</p>
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
    const playlists = asArray(this.jellyfinPlaylists?.playlists || this.jellyfinPlaylists?.Playlists);
    const spotifyPlaylists = asArray(this.spotifyPlaylists?.playlists || this.spotifyPlaylists?.Playlists || this.spotifyPlaylists);

    return html`
      <div class="panel">
        <div class="toolbar">
          ${this.jellyfinUsers.length ? html`
            <div class="form-row">
              <label for="jellyfin-user-filter">User</label>
              <select id="jellyfin-user-filter" @change=${() => this.loadLinkData()}>
                <option value="">All users</option>
                ${this.jellyfinUsers.map((user) => html`<option value=${user.id || user.Id}>${user.name || user.Name}</option>`)}
              </select>
            </div>
          ` : nothing}
          <button class="primary" @click=${() => this.loadLinkData()}>Refresh</button>
        </div>
      </div>
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Backend playlist</th>
              <th>Tracks</th>
              <th>Status</th>
              <th>Spotify source</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            ${playlists.length ? playlists.map((playlist) => this.renderLinkPlaylistRow(playlist, spotifyPlaylists)) : html`
              <tr><td colspan="5"><div class="empty">No backend playlists loaded.</div></td></tr>
            `}
          </tbody>
        </table>
      </div>
    `;
  }

  renderLinkPlaylistRow(playlist, spotifyPlaylists) {
    const id = playlist.id || playlist.Id;
    const name = playlist.name || playlist.Name;
    const linked = playlist.isConfigured || playlist.IsConfigured;
    const selection = this.linkSelections.get(id) || "";
    return html`
      <tr>
        <td><strong>${name}</strong></td>
        <td>${display(playlist.trackCount ?? playlist.TrackCount ?? playlist.childCount ?? playlist.ChildCount)}</td>
        <td><span class="status-chip ${linked ? "configured" : "needs_config"}">${linked ? "Linked" : "Unlinked"}</span></td>
        <td>
          ${linked ? html`<span class="mono">${display(playlist.linkedSpotifyId || playlist.LinkedSpotifyId)}</span>` : html`
            <select @change=${(event) => { this.linkSelections.set(id, event.target.value); this.requestUpdate(); }}>
              <option value="">Select playlist</option>
              ${spotifyPlaylists.map((item) => html`
                <option value=${item.id || item.Id || item.spotifyId || item.SpotifyId} ?selected=${selection === (item.id || item.Id)}>
                  ${item.name || item.Name}
                </option>
              `)}
            </select>
          `}
        </td>
        <td class="row-actions">
          ${linked ? html`
            <button class="danger" @click=${async () => { await API.unlinkPlaylist(id); await this.loadLinkData(); this.toast("Playlist unlinked"); }}>Unlink</button>
          ` : html`
            <button class="primary" ?disabled=${!selection} @click=${async () => { await API.linkPlaylist(id, selection, "0 8 * * *"); await this.loadLinkData(); this.restartKeys = new Set([...this.restartKeys, "SPOTIFY_IMPORT_PLAYLISTS"]); this.toast("Playlist linked"); }}>Link</button>
          `}
        </td>
      </tr>
    `;
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
    const data = new FormData(event.currentTarget);
    await API.addPlaylist(data.get("name"), data.get("spotifyId"));
    event.currentTarget.reset();
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
        <div class="card metric"><span class="metric-label">Total</span><span class="metric-value">${display(stats.TotalMappings ?? stats.totalMappings ?? 0)}</span></div>
        <div class="card metric"><span class="metric-label">Local</span><span class="metric-value">${display(stats.LocalMappings ?? stats.localMappings ?? 0)}</span></div>
        <div class="card metric"><span class="metric-label">External</span><span class="metric-value">${display(stats.ExternalMappings ?? stats.externalMappings ?? 0)}</span></div>
        <div class="card metric"><span class="metric-label">Manual</span><span class="metric-value">${display(stats.ManualMappings ?? stats.manualMappings ?? 0)}</span></div>
      </div>
      <div class="panel">
        <div class="toolbar">
          <div class="form-row">
            <label>Search</label>
            <input .value=${this.mappingFilters.search} @input=${(event) => { this.mappingFilters.search = event.target.value; }}>
          </div>
          <div class="form-row">
            <label>Target</label>
            <select .value=${this.mappingFilters.targetType} @change=${(event) => { this.mappingFilters.targetType = event.target.value; }}>
              <option value="all">All</option>
              <option value="local">Local</option>
              <option value="external">External</option>
            </select>
          </div>
          <button class="primary" @click=${async () => { this.mappingFilters.page = 1; await this.loadMappings(); }}>Apply</button>
        </div>
      </div>
      <div class="panel">
        <h3>Manual mapping</h3>
        <form class="toolbar" @submit=${this.saveManualMapping}>
          <div class="form-row"><label>Spotify ID</label><input name="spotifyId" required></div>
          <div class="form-row">
            <label>Target type</label>
            <select name="targetType">
              <option value="local">Local</option>
              <option value="external">External</option>
            </select>
          </div>
          <div class="form-row"><label>Local ID</label><input name="localId"></div>
          <div class="form-row"><label>External provider</label><input name="externalProvider" placeholder="deezer"></div>
          <div class="form-row"><label>External ID</label><input name="externalId"></div>
          <button>Save</button>
        </form>
      </div>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Track</th><th>Spotify ID</th><th>Target</th><th>Source</th><th>Created</th><th></th></tr></thead>
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
    const metadata = mapping.Metadata || mapping.metadata || {};
    const spotifyId = mapping.SpotifyId || mapping.spotifyId;
    const targetType = mapping.TargetType || mapping.targetType;
    const source = mapping.Source || mapping.source;
    return html`
      <tr>
        <td>
          <div class="track">
            <img class="art" src=${metadata.ArtworkUrl || metadata.artworkUrl || "/placeholder.png"} alt="">
            <div>
              <strong>${display(metadata.Title || metadata.title, "Unknown track")}</strong>
              <div class="muted">${display(metadata.Artist || metadata.artist, "Unknown artist")}</div>
            </div>
          </div>
        </td>
        <td><span class="mono">${spotifyId}</span></td>
        <td>${this.renderMappingTargets(mapping)}</td>
        <td><span class="chip">${display(source)}</span></td>
        <td>${formatDate(mapping.CreatedAt || mapping.createdAt)}</td>
        <td><button class="danger" @click=${async () => { await API.deleteMapping(spotifyId); await this.loadMappings(); this.toast("Mapping deleted"); }}>Delete</button></td>
      </tr>
    `;
  }

  renderMappingTargets(mapping) {
    const targets = [];
    if ((mapping.TargetType || mapping.targetType) === "local" && (mapping.LocalId || mapping.localId)) {
      targets.push(["local", mapping.LocalId || mapping.localId]);
    }
    for (const item of asArray(mapping.ExternalMappings || mapping.externalMappings)) {
      targets.push([item.Provider || item.provider, item.ExternalId || item.externalId]);
    }
    if (mapping.ExternalProvider || mapping.externalProvider) {
      targets.push([mapping.ExternalProvider || mapping.externalProvider, mapping.ExternalId || mapping.externalId]);
    }
    return html`${targets.length ? targets.map(([label, value]) => html`<span class="chip">${label}: <span class="mono">${value}</span></span>`) : html`<span class="muted">None</span>`}`;
  }

  saveManualMapping = async (event) => {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    const targetType = data.get("targetType");
    const payload = {
      spotifyId: data.get("spotifyId"),
      targetType,
      localId: data.get("localId") || null,
      externalProvider: data.get("externalProvider") || null,
      externalId: data.get("externalId") || null,
    };
    await API.saveMapping(payload);
    event.currentTarget.reset();
    await this.loadMappings();
    this.toast("Mapping saved");
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
              <option value="squidwtf">SquidWTF</option>
              <option value="applemusic">Apple Music</option>
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
    return html`
      <section class="view-stack">
        <div class="view-header">
          <div>
            <h2>Services and sources</h2>
            <p>Provider configuration, source priority, and extension management.</p>
          </div>
        </div>
        <div class="provider-grid">
          ${asArray(this.schema?.providers).map((provider) => this.renderProviderCard(provider))}
        </div>
        ${this.renderPriorityGroups()}
        ${this.renderExtensions()}
      </section>
    `;
  }

  renderProviderCard(provider) {
    return html`
      <div class="card provider-card">
        <div class="provider-head">
          <div class="provider-title">
            <strong>${provider.name}</strong>
            <span>${asArray(provider.categories).join(", ")}</span>
          </div>
          <span class="status-chip ${provider.status}">${titleCase(provider.status)}</span>
        </div>
        <div class="chip-list">
          ${asArray(provider.notes).map((note) => html`<span class="chip">${note}</span>`)}
        </div>
        <div class="config-grid">
          ${asArray(provider.configSchema).map((field) => this.renderConfigField(field))}
        </div>
      </div>
    `;
  }

  renderPriorityGroups() {
    return html`
      <div class="panel">
        <h3>Provider priority</h3>
        <div class="grid">
          ${asArray(this.schema?.priorityGroups).map((group) => html`
            <div class="card">
              <h3>${group.label}</h3>
              <div class="priority-list">
                ${asArray(group.providers).map((provider, index) => html`
                  <span class="priority-item">
                    <span>${provider}</span>
                    <button ?disabled=${index === 0} @click=${() => this.movePriority(group, index, -1)}>Up</button>
                    <button ?disabled=${index === group.providers.length - 1} @click=${() => this.movePriority(group, index, 1)}>Down</button>
                  </span>
                `)}
              </div>
              ${group.enabledEnvKey ? html`
                <div class="config-grid" style="margin-top: var(--space-4);">
                  ${this.renderConfigField({
                    key: group.enabledEnvKey,
                    label: "Enabled providers",
                    type: "text",
                    valuePath: group.id === "metadata" ? "providers.enabledSearch" : "providers.enabledPlaylist",
                    requiresRestart: true,
                  })}
                </div>
              ` : nothing}
            </div>
          `)}
        </div>
      </div>
    `;
  }

  async movePriority(group, index, direction) {
    const providers = [...group.providers];
    const target = index + direction;
    [providers[index], providers[target]] = [providers[target], providers[index]];
    await this.savePriority(group, providers);
  }

  renderExtensions() {
    const installed = asArray(this.installedExtensions);
    const storeItems = asArray(this.extensionStore?.items || this.extensionStore?.Items || this.extensionStore);
    const errors = asArray(this.extensionStore?.errors || this.extensionStore?.Errors);
    const repoField = {
      key: "EXTENSION_REPOSITORIES",
      label: "Extension repositories",
      type: "text",
      valuePath: "extensions.repositories",
      requiresRestart: false,
    };
    return html`
      <div class="panel">
        <div class="view-header">
          <div>
            <h3>Extension store</h3>
            <p>Installed extensions are loaded into the metadata/search sandbox.</p>
          </div>
          <div class="actions">
            <button @click=${async () => { await this.loadInstalledExtensions(); this.toast("Installed extensions refreshed"); }}>Installed</button>
            <button class="primary" @click=${async () => { await this.loadExtensionStore(); this.toast("Store loaded"); }}>Load store</button>
          </div>
        </div>
        <div class="config-grid">${this.renderConfigField(repoField)}</div>
      </div>
      ${errors.length ? html`<div class="panel">${errors.map((error) => html`<div class="error-text">${error.Repository || error.repository}: ${error.Message || error.message}</div>`)}</div>` : nothing}
      <div class="grid">
        <div class="panel">
          <h3>Installed extensions</h3>
          <div class="activity-list">
            ${installed.length ? installed.map((item) => html`
              <div class="activity-item">
                <strong>${item.displayName || item.DisplayName || item.name || item.Name}</strong>
                <span class="muted">${display(item.description || item.Description)}</span>
                <div class="row-actions">
                  ${asArray(item.types || item.Types).map((type) => html`<span class="chip">${type}</span>`)}
                  <button class="danger" @click=${async () => { await API.uninstallExtension(item.id || item.Id); await this.loadInstalledExtensions(); this.toast("Extension uninstalled"); }}>Uninstall</button>
                </div>
              </div>
            `) : html`<div class="empty">No extensions installed.</div>`}
          </div>
        </div>
        <div class="panel">
          <h3>Store</h3>
          <div class="activity-list">
            ${storeItems.length ? storeItems.map((item) => html`
              <div class="activity-item">
                <strong>${item.displayName || item.DisplayName}</strong>
                <span class="muted">${display(item.description || item.Description)}</span>
                <div class="row-actions">
                  <span class="chip">${display(item.version || item.Version)}</span>
                  <button class="primary" ?disabled=${Boolean(item.isInstalled || item.IsInstalled)} @click=${async () => { await API.installExtension(item); await this.loadInstalledExtensions(); await this.loadExtensionStore(); this.toast("Extension installed"); }}>
                    ${item.isInstalled || item.IsInstalled ? "Installed" : "Install"}
                  </button>
                </div>
              </div>
            `) : html`<div class="empty">Load the store to browse extensions.</div>`}
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
            <p>Download queue, scrobbling, and endpoint usage.</p>
          </div>
          <button class="primary" @click=${async () => { await Promise.all([this.loadEndpointUsage(), this.loadScrobbling(), this.loadQueue()]); this.toast("Activity refreshed"); }}>Refresh</button>
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
      <div class="actions">
        <button @click=${async () => { await API.testLastFm(); this.toast("Last.fm test completed"); }}>Test Last.fm</button>
        <button @click=${async () => { await API.testListenBrainz(); this.toast("ListenBrainz test completed"); }}>Test ListenBrainz</button>
      </div>
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
          <div class="panel">
            <h3>${section.label}</h3>
            <div class="config-grid">
              ${asArray(section.fields).map((field) => this.renderConfigField(field))}
            </div>
          </div>
        `)}
        <div class="panel">
          <h3>Backup and restore</h3>
          <div class="actions">
            <button @click=${() => this.exportEnv()}>Export .env</button>
            <input class="hidden" id="env-import-input" type="file" accept=".env,text/plain" @change=${(event) => this.importEnv(event)}>
            <button @click=${() => this.querySelector("#env-import-input")?.click()}>Import .env</button>
          </div>
        </div>
        <div class="panel">
          <h3>Danger zone</h3>
          <div class="actions">
            <button class="danger" @click=${async () => { if (confirm("Clear cache?")) { await API.clearCache(); this.toast("Cache clear requested"); } }}>Clear cache</button>
            <button class="danger" @click=${async () => { if (confirm("Restart Allstarr?")) { await API.restart(); this.toast("Restart requested"); } }}>Restart</button>
          </div>
        </div>
      </section>
    `;
  }

  renderConfigField(field) {
    const rawValue = getPathValue(this.config, field.valuePath, "");
    const value = field.sensitive ? "" : rawValue;
    const saved = this.restartKeys.has(field.key);
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
          ${saved ? html`<span class="restart-mark">Restart needed</span>` : nothing}
        </div>
        ${field.type === "select" ? html`
          <select id=${field.key} .value=${String(value)} @change=${onCommit}>
            ${asArray(field.options).map((option) => html`<option value=${option}>${option}</option>`)}
          </select>
        ` : field.type === "toggle" ? html`
          <label class="inline-check">
            <input id=${field.key} type="checkbox" .checked=${parseBoolValue(rawValue)} @change=${onCommit}>
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
            @blur=${onCommit}>
        `}
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

  async importEnv(event) {
    const file = event.currentTarget.files?.[0];
    if (!file) {
      return;
    }
    await API.importEnv(file);
    await this.loadConfig();
    this.restartKeys = new Set([...this.restartKeys, "IMPORT_ENV"]);
    this.toast(".env imported");
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
    return html`
      <footer class="now-playing">
        <div>
          <div class="now-title">${current ? display(current.title || current.Title, "Active download") : "No active playback"}</div>
          <div class="now-meta">${current ? display(current.artist || current.Artist) : "Queue is idle"}</div>
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
