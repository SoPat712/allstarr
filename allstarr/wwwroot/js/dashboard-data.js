import { escapeHtml, showToast, formatCookieAge } from "./utils.js";
import * as API from "./api.js";
import * as UI from "./ui.js";
import { renderCookieAge } from "./settings-editor.js";
import { runAction } from "./operations.js";

let playlistAutoRefreshInterval = null;
let dashboardRefreshInterval = null;
let downloadActivityEventSource = null;

let isAuthenticated = () => false;
let isAdminSession = () => false;
let getCurrentUserId = () => null;
let onCookieNeedsInit = async () => {};
let setCurrentConfigState = () => {};
let syncConfigUiExtras = () => {};
let loadScrobblingConfig = () => {};
let jellyfinPlaylistRequestToken = 0;

async function fetchStatus() {
  try {
    const data = await API.fetchStatus();
    UI.updateStatusUI(data);

    const hasCookie = data.spotify.hasCookie;
    const age = formatCookieAge(data.spotify.cookieSetDate, hasCookie);
    renderCookieAge("spotify-cookie-age", age);
    renderCookieAge("config-cookie-age", age);

    if (age.needsInit) {
      console.log("Cookie exists but date not set, initializing...");
      onCookieNeedsInit();
    }
  } catch (error) {
    console.error("Failed to fetch status:", error);
    showToast("Failed to fetch status: " + error.message, "error");
    UI.showErrorState(error.message);
  }
}

async function fetchPlaylists(silent = false) {
  try {
    const data = await API.fetchPlaylists();
    UI.updatePlaylistsUI(data);
  } catch (error) {
    if (!silent) {
      console.error("Failed to fetch playlists:", error);
      showToast("Failed to fetch playlists", "error");
    }
  }
}

async function fetchTrackMappings() {
  try {
    const data = await API.fetchTrackMappings();
    UI.updateTrackMappingsUI(data);
  } catch (error) {
    console.error("Failed to fetch track mappings:", error);
    showToast("Failed to fetch track mappings", "error");
  }
}

function bindMissingTrackActionButtons(tbody) {
  tbody.querySelectorAll(".missing-track-search-btn").forEach((btn) => {
    btn.addEventListener("click", () => {
      const query = btn.getAttribute("data-query") || "";
      const provider = btn.getAttribute("data-provider") || "squidwtf";
      if (typeof window.searchProvider === "function") {
        window.searchProvider(query, provider);
      }
    });
  });

  tbody.querySelectorAll(".missing-track-local-btn").forEach((btn) => {
    btn.addEventListener("click", () => {
      const playlistName = btn.getAttribute("data-playlist") || "";
      const position = Number.parseInt(
        btn.getAttribute("data-position") || "0",
        10,
      );
      const title = btn.getAttribute("data-title") || "";
      const artist = btn.getAttribute("data-artist") || "";
      const spotifyId = btn.getAttribute("data-spotify-id") || "";
      if (typeof window.openMapToLocal === "function") {
        window.openMapToLocal(
          playlistName,
          Number.isFinite(position) ? position : 0,
          title,
          artist,
          spotifyId,
        );
      }
    });
  });

  tbody.querySelectorAll(".missing-track-external-btn").forEach((btn) => {
    btn.addEventListener("click", () => {
      const playlistName = btn.getAttribute("data-playlist") || "";
      const position = Number.parseInt(
        btn.getAttribute("data-position") || "0",
        10,
      );
      const title = btn.getAttribute("data-title") || "";
      const artist = btn.getAttribute("data-artist") || "";
      const spotifyId = btn.getAttribute("data-spotify-id") || "";
      if (typeof window.openMapToExternal === "function") {
        window.openMapToExternal(
          playlistName,
          Number.isFinite(position) ? position : 0,
          title,
          artist,
          spotifyId,
        );
      }
    });
  });
}

async function fetchMissingTracks() {
  try {
    const data = await API.fetchPlaylists();
    const tbody = document.getElementById("missing-tracks-table-body");
    const missingTracks = [];

    for (const playlist of data.playlists) {
      if (playlist.externalMissing > 0) {
        try {
          const tracksData = await API.fetchPlaylistTracks(playlist.name);
          const missing = tracksData.tracks.filter((t) => t.isLocal === null);
          missing.forEach((t) => {
            missingTracks.push({
              playlist: playlist.name,
              provider: t.externalProvider || t.provider || "squidwtf",
              ...t,
            });
          });
        } catch (err) {
          console.error(`Failed to fetch tracks for ${playlist.name}:`, err);
        }
      }
    }

    document.getElementById("missing-total").textContent = missingTracks.length;

    if (missingTracks.length === 0) {
      tbody.innerHTML =
        '<tr><td colspan="5" style="text-align:center;color:var(--text-secondary);padding:40px;">🎉 No missing tracks! All tracks are matched.</td></tr>';
      return;
    }

    tbody.innerHTML = missingTracks
      .map((t) => {
        const artist =
          t.artists && t.artists.length > 0 ? t.artists.join(", ") : "";
        const searchQuery = `${t.title} ${artist}`;
        const provider = t.provider || "squidwtf";
        const trackPosition = Number.isFinite(t.position)
          ? Number(t.position)
          : 0;
        return `
                <tr>
                    <td><strong>${escapeHtml(t.playlist)}</strong></td>
                    <td>${escapeHtml(t.title)}</td>
                    <td>${escapeHtml(artist)}</td>
                    <td style="color:var(--text-secondary);">${t.album ? escapeHtml(t.album) : "-"}</td>
                    <td class="mapping-actions-cell">
                        <button class="map-action-btn map-action-search missing-track-search-btn"
                            data-query="${escapeHtml(searchQuery)}"
                            data-provider="${escapeHtml(provider)}">🔍 Search</button>
                        <button class="map-action-btn map-action-local missing-track-local-btn"
                            data-playlist="${escapeHtml(t.playlist)}"
                            data-position="${trackPosition}"
                            data-title="${escapeHtml(t.title)}"
                            data-artist="${escapeHtml(artist)}"
                            data-spotify-id="${escapeHtml(t.spotifyId || "")}">Map to Local</button>
                        <button class="map-action-btn map-action-external missing-track-external-btn"
                            data-playlist="${escapeHtml(t.playlist)}"
                            data-position="${trackPosition}"
                            data-title="${escapeHtml(t.title)}"
                            data-artist="${escapeHtml(artist)}"
                            data-spotify-id="${escapeHtml(t.spotifyId || "")}">Map to External</button>
                    </td>
                </tr>
            `;
      })
      .join("");

    bindMissingTrackActionButtons(tbody);
  } catch (error) {
    console.error("Failed to fetch missing tracks:", error);
    showToast("Failed to fetch missing tracks", "error");
  }
}

async function fetchDownloads() {
  if (!isAuthenticated() || !isAdminSession()) {
    return;
  }

  try {
    const data = await API.fetchDownloads();
    const tbody = document.getElementById("downloads-table-body");

    document.getElementById("downloads-count").textContent = data.count;
    document.getElementById("downloads-size").textContent =
      data.totalSizeFormatted;

    if (data.count === 0) {
      tbody.innerHTML =
        '<tr><td colspan="5" style="text-align:center;color:var(--text-secondary);padding:40px;">No downloaded files found.</td></tr>';
      return;
    }

    tbody.innerHTML = data.files
      .map((f) => {
        return `
                <tr data-path="${escapeHtml(f.path)}">
                    <td><strong>${escapeHtml(f.artist)}</strong></td>
                    <td>${escapeHtml(f.album)}</td>
                    <td style="font-family:monospace;font-size:0.85rem;">${escapeHtml(f.fileName)}</td>
                    <td style="color:var(--text-secondary);">${f.sizeFormatted}</td>
                    <td>
                        <button data-action="downloadFile" data-arg-path="${escapeHtml(escapeJs(f.path))}"
                            style="margin-right:4px;font-size:0.75rem;padding:4px 8px;background:var(--accent);border-color:var(--accent);">Download</button>
                        <button data-action="deleteDownload" data-arg-path="${escapeHtml(escapeJs(f.path))}"
                            class="danger" style="font-size:0.75rem;padding:4px 8px;">Delete</button>
                    </td>
                </tr>
            `;
      })
      .join("");
  } catch (error) {
    console.error("Failed to fetch downloads:", error);
    showToast("Failed to fetch downloads", "error");
  }
}

async function fetchConfig() {
  try {
    const data = await API.fetchConfig();
    setCurrentConfigState(data);
    UI.updateConfigUI(data);
    syncConfigUiExtras(data);
  } catch (error) {
    console.error("Failed to fetch config:", error);
  }
}

async function fetchJellyfinPlaylists() {
  const tbody = document.getElementById("jellyfin-playlist-table-body");
  tbody.innerHTML =
    '<tr><td colspan="4" class="loading"><span class="spinner"></span> Loading Jellyfin playlists...</td></tr>';

  try {
    const requestToken = ++jellyfinPlaylistRequestToken;
    const userId = isAdminSession()
      ? document.getElementById("jellyfin-user-select")?.value
      : null;
    const baseData = await API.fetchJellyfinPlaylists(userId, false);
    if (requestToken !== jellyfinPlaylistRequestToken) {
      return;
    }

    UI.updateJellyfinPlaylistsUI(baseData);

    // Enrich counts after initial render so big accounts don't appear empty.
    API.fetchJellyfinPlaylists(userId, true)
      .then((statsData) => {
        if (requestToken !== jellyfinPlaylistRequestToken) {
          return;
        }
        UI.updateJellyfinPlaylistsUI(statsData);
      })
      .catch((err) => {
        console.error("Failed to fetch Jellyfin playlist track stats:", err);
      });
  } catch (error) {
    console.error("Failed to fetch Jellyfin playlists:", error);
    tbody.innerHTML =
      '<tr><td colspan="4" style="text-align:center;color:var(--error);padding:40px;">Failed to fetch playlists</td></tr>';
  }
}

async function fetchJellyfinUsers() {
  if (!isAdminSession()) {
    return;
  }

  try {
    const data = await API.fetchJellyfinUsers();
    if (data) {
      UI.updateJellyfinUsersUI(data, getCurrentUserId());
    }
  } catch (error) {
    console.error("Failed to fetch users:", error);
  }
}

async function fetchEndpointUsage() {
  try {
    const topSelect = document.getElementById("endpoints-top-select");
    const top = topSelect ? topSelect.value : 50;
    const data = await API.fetchEndpointUsage(top);
    UI.updateEndpointUsageUI(data);
  } catch (error) {
    console.error("Failed to fetch endpoint usage:", error);
    const tbody = document.getElementById("endpoints-table-body");
    tbody.innerHTML =
      '<tr><td colspan="4" style="text-align:center;color:var(--error);padding:40px;">Failed to load endpoint usage data</td></tr>';
  }
}

async function clearEndpointUsage() {
  const result = await runAction({
    confirmMessage:
      "Are you sure you want to clear all endpoint usage data? This cannot be undone.",
    task: () => API.clearEndpointUsage(),
    success: (data) => data.message || "Endpoint usage data cleared",
    error: "Failed to clear endpoint usage data",
  });

  if (result) {
    fetchEndpointUsage();
  }
}

function startPlaylistAutoRefresh() {
  if (playlistAutoRefreshInterval) {
    clearInterval(playlistAutoRefreshInterval);
  }

  playlistAutoRefreshInterval = setInterval(() => {
    const playlistsTab = document.getElementById("tab-playlists");
    if (playlistsTab && playlistsTab.classList.contains("active")) {
      fetchPlaylists(true);
    }
  }, 5000);
}

function stopPlaylistAutoRefresh() {
  if (playlistAutoRefreshInterval) {
    clearInterval(playlistAutoRefreshInterval);
    playlistAutoRefreshInterval = null;
  }
}

function stopDashboardRefresh() {
  if (dashboardRefreshInterval) {
    clearInterval(dashboardRefreshInterval);
    dashboardRefreshInterval = null;
  }
  if (downloadActivityEventSource) {
    downloadActivityEventSource.close();
    downloadActivityEventSource = null;
  }
  stopPlaylistAutoRefresh();
}

function startDashboardRefresh() {
  stopDashboardRefresh();
  startPlaylistAutoRefresh();

  dashboardRefreshInterval = setInterval(() => {
    if (!isAuthenticated()) {
      return;
    }

    if (isAdminSession()) {
      fetchStatus();
      fetchPlaylists();
      fetchTrackMappings();
      fetchMissingTracks();
      const keptTab = document.getElementById("tab-kept");
      if (keptTab && keptTab.classList.contains("active")) {
        fetchDownloads();
      }

      const endpointsTab = document.getElementById("tab-endpoints");
      if (endpointsTab && endpointsTab.classList.contains("active")) {
        fetchEndpointUsage();
      }
    } else {
      fetchJellyfinPlaylists();
    }
  }, 30000);
}

async function loadDashboardData() {
  if (isAdminSession()) {
    await Promise.allSettled([
      fetchStatus(),
      fetchPlaylists(),
      fetchTrackMappings(),
      fetchMissingTracks(),
      fetchDownloads(),
      fetchConfig(),
      fetchEndpointUsage(),
    ]);

    // Ensure user filter defaults are populated before loading Link Playlists rows.
    await fetchJellyfinUsers();
    await fetchJellyfinPlaylists();

    loadScrobblingConfig();
  } else {
    await Promise.allSettled([fetchJellyfinPlaylists()]);
  }

  startDashboardRefresh();
}

function startDownloadActivityStream() {
  if (!isAdminSession()) return;
  
  if (downloadActivityEventSource) {
    downloadActivityEventSource.close();
  }

  downloadActivityEventSource = new EventSource("/api/admin/downloads/activity");
  
  downloadActivityEventSource.onmessage = (event) => {
    try {
      const downloads = JSON.parse(event.data);
      renderDownloadActivity(downloads);
    } catch (err) {
      console.error("Failed to parse download activity:", err);
    }
  };

  downloadActivityEventSource.onerror = (err) => {
    console.error("Download activity SSE error:", err);
    // EventSource will auto-reconnect
  };
}

function renderDownloadActivity(downloads) {
  const container = document.getElementById("download-activity-list");
  if (!container) return;

  if (!downloads || downloads.length === 0) {
    container.innerHTML = '<div class="empty-state">No active downloads</div>';
    return;
  }

  const statusIcons = {
    0: '⏳', // NotStarted
    1: '<span class="spinner" style="border-width:2px; height:12px; width:12px; display:inline-block; margin-right:4px;"></span> Downloading', // InProgress
    2: '✅ Completed', // Completed
    3: '❌ Failed' // Failed
  };

  const html = downloads.map(d => {
    const downloadProgress = clampProgress(d.progress);
    const playbackProgress = clampProgress(d.playbackProgress);

    // Determine elapsed/duration text
    let timeText = "";
    if (d.startedAt) {
      const start = new Date(d.startedAt);
      const end = d.completedAt ? new Date(d.completedAt) : new Date();
      const diffSecs = Math.floor((end.getTime() - start.getTime()) / 1000);
      timeText = diffSecs < 60 ? `${diffSecs}s` : `${Math.floor(diffSecs/60)}m ${diffSecs%60}s`;
    }

    const progressMeta = [];
    if (typeof d.durationSeconds === "number" && typeof d.playbackPositionSeconds === "number") {
      progressMeta.push(`${formatSeconds(d.playbackPositionSeconds)} / ${formatSeconds(d.durationSeconds)}`);
    } else if (typeof d.durationSeconds === "number") {
      progressMeta.push(formatSeconds(d.durationSeconds));
    }
    if (d.requestedForStreaming) {
      progressMeta.push("stream");
    }

    const progressMetaText = progressMeta.length > 0
      ? `<div class="download-progress-meta">${progressMeta.map(escapeHtml).join(" • ")}</div>`
      : "";

    const progressBar = `
      <div class="download-progress-bar" aria-hidden="true">
        <div class="download-progress-buffer" style="width:${downloadProgress * 100}%"></div>
        <div class="download-progress-playback" style="width:${playbackProgress * 100}%"></div>
      </div>
      ${progressMetaText}
    `;

    const title = d.title || 'Unknown Title';
    const artist = d.artist || 'Unknown Artist';
    const errorText = d.errorMessage ? `<div style="color:var(--error); font-size:0.8rem; margin-top:4px;">${escapeHtml(d.errorMessage)}</div>` : '';
    const streamBadge = d.requestedForStreaming
      ? '<span class="download-queue-badge">Stream</span>'
      : '';
    const playingBadge = d.isPlaying
      ? '<span class="download-queue-badge is-playing">Playing</span>'
      : '';

    return `
      <div class="download-queue-item">
        <div class="download-queue-info">
          <div class="download-queue-title">${escapeHtml(title)}</div>
          <div class="download-queue-meta">
            <span class="download-queue-artist">${escapeHtml(artist)}</span>
            <span class="download-queue-provider">${escapeHtml(d.externalProvider)}</span>
            ${streamBadge}
            ${playingBadge}
          </div>
          ${progressBar}
          ${errorText}
        </div>
        <div class="download-queue-status">
          <span style="font-size:0.85rem;">${statusIcons[d.status] || 'Unknown'}</span>
          <span class="download-queue-time">${timeText}</span>
        </div>
      </div>
    `;
  }).join('');

  container.innerHTML = html;
}

function clampProgress(value) {
  if (typeof value !== "number" || Number.isNaN(value)) {
    return 0;
  }

  return Math.max(0, Math.min(1, value));
}

function formatSeconds(totalSeconds) {
  if (typeof totalSeconds !== "number" || Number.isNaN(totalSeconds) || totalSeconds < 0) {
    return "0:00";
  }

  const minutes = Math.floor(totalSeconds / 60);
  const seconds = Math.floor(totalSeconds % 60);
  return `${minutes}:${String(seconds).padStart(2, "0")}`;
}

export function initDashboardData(options) {
  isAuthenticated = options.isAuthenticated;
  isAdminSession = options.isAdminSession;
  getCurrentUserId = options.getCurrentUserId || (() => null);
  onCookieNeedsInit = options.onCookieNeedsInit;
  setCurrentConfigState = options.setCurrentConfigState;
  syncConfigUiExtras = options.syncConfigUiExtras;
  loadScrobblingConfig = options.loadScrobblingConfig;

  window.fetchStatus = fetchStatus;
  window.fetchPlaylists = fetchPlaylists;
  window.fetchTrackMappings = fetchTrackMappings;
  window.fetchMissingTracks = fetchMissingTracks;
  window.fetchDownloads = fetchDownloads;
  window.fetchConfig = fetchConfig;
  window.fetchJellyfinPlaylists = fetchJellyfinPlaylists;
  window.fetchJellyfinUsers = fetchJellyfinUsers;
  window.fetchEndpointUsage = fetchEndpointUsage;
  window.clearEndpointUsage = clearEndpointUsage;

  return {
    stopDashboardRefresh,
    startDashboardRefresh,
    loadDashboardData,
    fetchPlaylists,
    fetchTrackMappings,
    fetchDownloads,
    fetchJellyfinPlaylists,
    fetchConfig,
    fetchStatus,
  };
}
