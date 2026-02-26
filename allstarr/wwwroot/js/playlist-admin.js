import { escapeHtml, escapeJs, showToast } from "./utils.js";
import * as API from "./api.js";
import { openModal, closeModal } from "./modals.js";

let currentLinkMode = "select";
let spotifyUserPlaylists = [];
let spotifyUserPlaylistsScopeUserId = null;
let isAdminSession = () => false;
let showRestartBanner = () => {};
let fetchPlaylists = async () => {};
let fetchJellyfinPlaylists = async () => {};

function switchLinkMode(mode) {
  currentLinkMode = mode;

  const selectGroup = document.getElementById("link-select-group");
  const manualGroup = document.getElementById("link-manual-group");
  const selectBtn = document.getElementById("select-mode-btn");
  const manualBtn = document.getElementById("manual-mode-btn");

  if (!selectGroup || !manualGroup || !selectBtn || !manualBtn) {
    return;
  }

  if (mode === "select") {
    selectGroup.style.display = "block";
    manualGroup.style.display = "none";
    selectBtn.classList.add("primary");
    manualBtn.classList.remove("primary");
  } else {
    selectGroup.style.display = "none";
    manualGroup.style.display = "block";
    selectBtn.classList.remove("primary");
    manualBtn.classList.add("primary");
  }
}

function cleanSpotifyPlaylistId(spotifyId) {
  let cleanSpotifyId = spotifyId;
  if (spotifyId.startsWith("spotify:playlist:")) {
    cleanSpotifyId = spotifyId.replace("spotify:playlist:", "");
  } else if (spotifyId.includes("spotify.com/playlist/")) {
    const match = spotifyId.match(/playlist\/([a-zA-Z0-9]+)/);
    if (match) {
      cleanSpotifyId = match[1];
    }
  }

  return cleanSpotifyId.split("?")[0].split("#")[0].replace(/\/$/, "");
}

async function openLinkPlaylist(jellyfinId, name) {
  document.getElementById("link-jellyfin-id").value = jellyfinId;
  document.getElementById("link-jellyfin-name").value = name;
  document.getElementById("link-spotify-id").value = "";

  switchLinkMode("select");

  const selectedUserId = isAdminSession()
    ? document.getElementById("jellyfin-user-select")?.value || null
    : null;

  if (
    spotifyUserPlaylists.length === 0 ||
    spotifyUserPlaylistsScopeUserId !== selectedUserId
  ) {
    const select = document.getElementById("link-spotify-select");
    if (select) {
      select.innerHTML = '<option value="">Loading playlists...</option>';
    }

    try {
      spotifyUserPlaylists = await API.fetchSpotifyUserPlaylists(selectedUserId);
      spotifyUserPlaylistsScopeUserId = selectedUserId;
      const availablePlaylists = spotifyUserPlaylists.filter((p) => !p.isLinked);

      if (!select) {
        openModal("link-playlist-modal");
        return;
      }

      if (availablePlaylists.length === 0) {
        select.innerHTML = '<option value="">No playlists available</option>';
        switchLinkMode("manual");
      } else {
        select.innerHTML =
          '<option value="">-- Select a playlist --</option>' +
          availablePlaylists
            .map(
              (p) =>
                `<option value="${escapeHtml(p.id)}">${escapeHtml(p.name)} (${p.trackCount} tracks)</option>`,
            )
            .join("");
      }
    } catch {
      const select = document.getElementById("link-spotify-select");
      if (select) {
        select.innerHTML = '<option value="">Failed to load playlists</option>';
      }
      switchLinkMode("manual");
    }
  }

  openModal("link-playlist-modal");
}

async function linkPlaylist() {
  const jellyfinId = document.getElementById("link-jellyfin-id").value;
  const syncSchedule = document.getElementById("link-sync-schedule").value.trim();

  if (!syncSchedule) {
    showToast("Sync schedule is required", "error");
    return;
  }

  const cronParts = syncSchedule.split(/\s+/);
  if (cronParts.length !== 5) {
    showToast(
      "Invalid cron format. Expected: minute hour day month dayofweek",
      "error",
    );
    return;
  }

  let spotifyId = "";
  if (currentLinkMode === "select") {
    spotifyId = document.getElementById("link-spotify-select").value;
    if (!spotifyId) {
      showToast("Please select a Spotify playlist", "error");
      return;
    }
  } else {
    spotifyId = document.getElementById("link-spotify-id").value.trim();
    if (!spotifyId) {
      showToast("Spotify Playlist ID is required", "error");
      return;
    }
  }

  try {
    const selectedUserId = isAdminSession()
      ? document.getElementById("jellyfin-user-select")?.value || null
      : null;

    await API.linkPlaylist(
      jellyfinId,
      cleanSpotifyPlaylistId(spotifyId),
      syncSchedule,
      selectedUserId,
    );

    showToast("Playlist linked!", "success");
    if (isAdminSession()) {
      showRestartBanner();
    } else {
      showToast(
        "Ask an administrator to restart Allstarr to apply changes.",
        "warning",
      );
    }

    closeModal("link-playlist-modal");
    resetPlaylistAdminState();

    if (isAdminSession()) {
      await fetchPlaylists();
    }

    await fetchJellyfinPlaylists();
  } catch (error) {
    showToast(error.message || "Failed to link playlist", "error");
  }
}

async function unlinkPlaylist(playlistIdentifier, name = null) {
  const displayName = name || playlistIdentifier;
  if (
    !confirm(
      `Unlink playlist "${displayName}"? This will stop filling in missing tracks.`,
    )
  ) {
    return;
  }

  try {
    await API.unlinkPlaylist(playlistIdentifier);
    showToast("Playlist unlinked.", "success");

    if (isAdminSession()) {
      showRestartBanner();
    } else {
      showToast(
        "Ask an administrator to restart Allstarr to apply changes.",
        "warning",
      );
    }

    resetPlaylistAdminState();

    if (isAdminSession()) {
      await fetchPlaylists();
    }

    await fetchJellyfinPlaylists();
  } catch (error) {
    showToast(error.message || "Failed to unlink playlist", "error");
  }
}

function openAddPlaylist() {
  document.getElementById("new-playlist-name").value = "";
  document.getElementById("new-playlist-id").value = "";
  openModal("add-playlist-modal");
}

async function addPlaylist() {
  const name = document.getElementById("new-playlist-name").value.trim();
  const id = document.getElementById("new-playlist-id").value.trim();

  if (!name || !id) {
    showToast("Name and ID are required", "error");
    return;
  }

  try {
    await API.addPlaylist(name, id);
    showToast("Playlist added.", "success");
    showRestartBanner();
    closeModal("add-playlist-modal");
  } catch (error) {
    showToast(error.message || "Failed to add playlist", "error");
  }
}

async function editPlaylistSchedule(playlistName, currentSchedule) {
  const newSchedule = prompt(
    `Edit sync schedule for "${playlistName}"\n\nCron format: minute hour day month dayofweek\nExamples:\n• 0 8 * * * = Daily 8 AM\n• 0 8 * * 1 = Monday 8 AM\n• 0 6 * * * = Daily 6 AM\n• 0 20 * * 5 = Friday 8 PM\n\nUse https://crontab.guru/ to build your schedule`,
    currentSchedule,
  );

  if (!newSchedule || newSchedule === currentSchedule) {
    return;
  }

  const cronParts = newSchedule.trim().split(/\s+/);
  if (cronParts.length !== 5) {
    showToast(
      "Invalid cron format. Expected: minute hour day month dayofweek",
      "error",
    );
    return;
  }

  try {
    await API.editPlaylistSchedule(playlistName, newSchedule.trim());
    showToast("Sync schedule updated!", "success");
    showRestartBanner();
    await fetchPlaylists();
  } catch (error) {
    console.error("Failed to update schedule:", error);
    showToast(error.message || "Failed to update schedule", "error");
  }
}

async function removePlaylist(name) {
  if (!confirm(`Remove playlist "${name}"?`)) {
    return;
  }

  try {
    await API.removePlaylist(name);
    showToast("Playlist removed.", "success");
    showRestartBanner();
    await fetchPlaylists();
  } catch (error) {
    showToast(error.message || "Failed to remove playlist", "error");
  }
}

export function resetPlaylistAdminState() {
  currentLinkMode = "select";
  spotifyUserPlaylists = [];
  spotifyUserPlaylistsScopeUserId = null;
}

export function initPlaylistAdmin(options) {
  isAdminSession = options.isAdminSession;
  showRestartBanner = options.showRestartBanner;
  fetchPlaylists = options.fetchPlaylists;
  fetchJellyfinPlaylists = options.fetchJellyfinPlaylists;

  window.switchLinkMode = switchLinkMode;
  window.openLinkPlaylist = openLinkPlaylist;
  window.linkPlaylist = linkPlaylist;
  window.unlinkPlaylist = unlinkPlaylist;
  window.openAddPlaylist = openAddPlaylist;
  window.addPlaylist = addPlaylist;
  window.editPlaylistSchedule = editPlaylistSchedule;
  window.removePlaylist = removePlaylist;

  return {
    resetPlaylistAdminState,
  };
}
