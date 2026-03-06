import { showToast } from "./utils.js";
import * as API from "./api.js";

let fetchPlaylists = async () => {};
let fetchTrackMappings = async () => {};
let fetchDownloads = async () => {};

function setMatchingBannerVisible(visible) {
  const banner = document.getElementById("matching-warning-banner");
  if (banner) {
    banner.style.display = visible ? "block" : "none";
  }
}

export async function runAction({
  task,
  success,
  error,
  onDone,
  before,
  after,
  confirmMessage,
}) {
  if (confirmMessage && !confirm(confirmMessage)) {
    return null;
  }

  try {
    if (before) {
      await before();
    }

    const result = await task();

    if (success) {
      const message = typeof success === "function" ? success(result) : success;
      if (message) {
        showToast(message, "success");
      }
    }

    return result;
  } catch (err) {
    const message = typeof error === "function" ? error(err) : error;
    showToast(message || err.message || "Action failed", "error");
    return null;
  } finally {
    if (after) {
      await after();
    }

    if (onDone) {
      await onDone();
    }
  }
}

function downloadFile(path) {
  try {
    window.open(
      `/api/admin/downloads/file?path=${encodeURIComponent(path)}`,
      "_blank",
    );
  } catch (error) {
    console.error("Failed to download file:", error);
    showToast("Failed to download file", "error");
  }
}

function downloadAllKept() {
  try {
    window.open("/api/admin/downloads/all", "_blank");
    showToast("Preparing download archive...", "info");
  } catch (error) {
    console.error("Failed to download all files:", error);
    showToast("Failed to download all files", "error");
  }
}

async function deleteDownload(path) {
  const result = await runAction({
    confirmMessage: `Delete this file?\n\n${path}\n\nThis action cannot be undone.`,
    task: async () => {
      await API.deleteDownload(path);
      const escapedPath = path.replace(/\\/g, "\\\\").replace(/"/g, '\\"');
      const row = document.querySelector(`tr[data-path="${escapedPath}"]`);
      if (row) {
        row.remove();
      }
      return true;
    },
    success: "File deleted successfully",
    error: (err) => err.message || "Failed to delete file",
  });

  if (result) {
    await fetchDownloads();
  }
}

async function deleteTrackMapping(playlist, spotifyId) {
  const confirmMessage = `Remove manual external mapping for ${spotifyId} in playlist "${playlist}"?\n\nThis will:\n• Delete the manual mapping from the cache\n• Allow the track to be matched automatically again\n• The track may be re-matched with potentially better results\n\nThis action cannot be undone.`;

  const result = await runAction({
    confirmMessage,
    task: () => API.deleteTrackMapping(playlist, spotifyId),
    success: "Mapping removed successfully",
    error: (err) => err.message || "Failed to remove mapping",
  });

  if (result) {
    await fetchTrackMappings();
  }
}

async function refreshPlaylists() {
  showToast("Refreshing playlists...", "info");
  const result = await runAction({
    task: () => API.refreshPlaylists(),
    success: (data) => data.message,
    error: "Failed to refresh playlists",
  });

  if (result) {
    setTimeout(fetchPlaylists, 2000);
  }
}

async function refreshPlaylist(name) {
  showToast(`Refreshing ${name} from Spotify...`, "info");
  const result = await runAction({
    task: () => API.refreshPlaylist(name),
    success: (data) => `✓ ${data.message}`,
    error: "Failed to refresh playlist",
  });

  if (result) {
    setTimeout(fetchPlaylists, 2000);
  }
}

async function clearPlaylistCache(name) {
  const result = await runAction({
    confirmMessage: `Rebuild "${name}" from scratch?\n\nThis will:\n• Clear all caches\n• Fetch fresh Spotify playlist data\n• Re-match all tracks\n\nThis uses the same workflow as that playlist's scheduled cron rebuild.\n\nUse this when the Spotify playlist has changed.\n\nThis may take a minute.`,
    before: async () => {
      setMatchingBannerVisible(true);
      showToast(`Rebuilding ${name} from scratch...`, "info");
    },
    task: () => API.clearPlaylistCache(name),
    success: (data) => `✓ ${data.message}`,
    error: "Failed to clear cache",
  });

  if (result) {
    setTimeout(() => {
      fetchPlaylists();
      setMatchingBannerVisible(false);
    }, 3000);
  } else {
    setMatchingBannerVisible(false);
  }
}

async function matchPlaylistTracks(name) {
  const result = await runAction({
    before: async () => {
      setMatchingBannerVisible(true);
      showToast(`Re-matching local tracks for ${name}...`, "info");
    },
    task: () => API.matchPlaylistTracks(name),
    success: (data) => `✓ ${data.message}`,
    error: "Failed to re-match tracks",
  });

  if (result) {
    setTimeout(() => {
      fetchPlaylists();
      setMatchingBannerVisible(false);
    }, 2000);
  } else {
    setMatchingBannerVisible(false);
  }
}

async function matchAllPlaylists() {
  const result = await runAction({
    confirmMessage:
      "Re-match local tracks for ALL playlists?\n\nUse this when your local library has changed.\n\nThis may take a few minutes.",
    before: async () => {
      setMatchingBannerVisible(true);
      showToast("Matching tracks for all playlists...", "info");
    },
    task: () => API.matchAllPlaylists(),
    success: (data) => `✓ ${data.message}`,
    error: "Failed to match tracks",
  });

  if (result) {
    setTimeout(() => {
      fetchPlaylists();
      setMatchingBannerVisible(false);
    }, 2000);
  } else {
    setMatchingBannerVisible(false);
  }
}

async function refreshAndMatchAll() {
  const result = await runAction({
    confirmMessage:
      "Rebuild all playlists from scratch?\n\nThis will:\n• Clear all playlist caches\n• Fetch fresh data from Spotify\n• Re-match all tracks against local library and external providers\n\nThis is a manual bulk rebuild across all playlists.\n\nThis may take several minutes.",
    before: async () => {
      setMatchingBannerVisible(true);
      showToast("Starting full rebuild for all playlists...", "info", 3000);
    },
    task: () => API.rebuildAllPlaylists(),
    success: "✓ Full rebuild complete!",
    error: "Failed to complete rebuild",
  });

  if (result) {
    setTimeout(() => {
      fetchPlaylists();
      setMatchingBannerVisible(false);
    }, 3000);
  } else {
    setMatchingBannerVisible(false);
  }
}

async function clearCache() {
  const result = await runAction({
    confirmMessage: "Clear all cached playlist data?",
    task: () => API.clearCache(),
    success: (data) => data.message,
    error: "Failed to clear cache",
  });

  if (result) {
    await fetchPlaylists();
  }
}

async function exportEnv() {
  const result = await runAction({
    task: async () => {
      const blob = await API.exportEnv();
      const url = window.URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = `.env.backup.${new Date().toISOString().split("T")[0]}`;
      document.body.appendChild(anchor);
      anchor.click();
      window.URL.revokeObjectURL(url);
      document.body.removeChild(anchor);
      return true;
    },
    success: ".env file exported successfully",
    error: (err) => err.message || "Failed to export .env file",
  });

  return result;
}

async function importEnv(event) {
  const file = event.target.files[0];
  if (!file) {
    return;
  }

  const result = await runAction({
    confirmMessage:
      "Import this .env file? This will replace your current configuration.\n\nA backup will be created automatically.\n\nYou will need to restart the container for changes to take effect.",
    task: () => API.importEnv(file),
    success: (data) => data.message,
    error: (err) => err.message || "Failed to import .env file",
  });

  event.target.value = "";
  return result;
}

async function restartContainer() {
  if (
    !confirm(
      "Restart the container to apply configuration changes?\n\nThe dashboard will be temporarily unavailable.",
    )
  ) {
    return;
  }

  const result = await runAction({
    task: () => API.restartContainer(),
    error: "Failed to restart container",
  });

  if (!result) {
    return;
  }

  document.getElementById("restart-overlay")?.classList.add("active");
  const statusEl = document.getElementById("restart-status");
  if (statusEl) {
    statusEl.textContent = "Stopping container...";
  }

  setTimeout(() => {
    if (statusEl) {
      statusEl.textContent = "Waiting for server to come back...";
    }
    checkServerAndReload();
  }, 3000);
}

async function checkServerAndReload() {
  let attempts = 0;
  const maxAttempts = 60;

  const checkHealth = async () => {
    try {
      const res = await fetch("/api/admin/status", {
        method: "GET",
        cache: "no-store",
      });
      if (res.ok) {
        const statusEl = document.getElementById("restart-status");
        if (statusEl) {
          statusEl.textContent = "Server is back! Reloading...";
        }
        window.dismissRestartBanner();
        setTimeout(() => window.location.reload(), 500);
        return;
      }
    } catch {
      // Server still restarting.
    }

    attempts += 1;
    const statusEl = document.getElementById("restart-status");
    if (statusEl) {
      statusEl.textContent = `Waiting for server to come back... (${attempts}s)`;
    }

    if (attempts < maxAttempts) {
      setTimeout(checkHealth, 1000);
    } else {
      document.getElementById("restart-overlay")?.classList.remove("active");
      showToast(
        "Server may still be restarting. Please refresh manually.",
        "warning",
      );
    }
  };

  checkHealth();
}

export function initOperations(options) {
  fetchPlaylists = options.fetchPlaylists;
  fetchTrackMappings = options.fetchTrackMappings;
  fetchDownloads = options.fetchDownloads;

  window.runAction = runAction;
  window.deleteTrackMapping = deleteTrackMapping;
  window.downloadFile = downloadFile;
  window.downloadAllKept = downloadAllKept;
  window.deleteDownload = deleteDownload;
  window.refreshPlaylists = refreshPlaylists;
  window.refreshPlaylist = refreshPlaylist;
  window.clearPlaylistCache = clearPlaylistCache;
  window.matchPlaylistTracks = matchPlaylistTracks;
  window.matchAllPlaylists = matchAllPlaylists;
  window.refreshAndMatchAll = refreshAndMatchAll;
  window.clearCache = clearCache;
  window.exportEnv = exportEnv;
  window.importEnv = importEnv;
  window.restartContainer = restartContainer;

  return {
    runAction,
  };
}
