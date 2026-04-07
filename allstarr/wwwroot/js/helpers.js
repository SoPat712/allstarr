// Helper functions for complex UI operations

import {
  escapeHtml,
  escapeJs,
  showToast,
  capitalizeProvider,
} from "./utils.js";
import * as API from "./api.js";
import { openModal, closeModal } from "./modals.js";

// View tracks modal
export async function viewTracks(name) {
  document.getElementById("tracks-modal-title").textContent =
    name + " - Tracks";
  document.getElementById("tracks-list").innerHTML =
    '<div class="loading"><span class="spinner"></span> Loading tracks...</div>';
  openModal("tracks-modal");

  try {
    const data = await API.fetchPlaylistTracks(name);

    if (!data || !data.tracks) {
      document.getElementById("tracks-list").innerHTML =
        '<p style="text-align:center;color:var(--error);padding:40px;">Invalid data received from server</p>';
      return;
    }

    if (data.tracks.length === 0) {
      document.getElementById("tracks-list").innerHTML =
        '<p style="text-align:center;color:var(--text-secondary);padding:40px;">No tracks found</p>';
      return;
    }

    document.getElementById("tracks-list").innerHTML = data.tracks
      .map((t, index) => {
        let statusBadge = "";
        let mapButton = "";
        let lyricsBadge = "";

        if (t.hasLyrics) {
          lyricsBadge =
            '<span class="status-badge" style="font-size:0.75rem;padding:2px 8px;margin-left:4px;background:#3b82f6;color:white;"><span class="status-dot" style="background:white;"></span>Lyrics</span>';
        }

        if (t.isLocal === true) {
          statusBadge =
            '<span class="status-badge success" style="font-size:0.75rem;padding:2px 8px;margin-left:8px;"><span class="status-dot"></span>Local</span>';
          if (t.isManualMapping && t.manualMappingType === "jellyfin") {
            statusBadge +=
              '<span class="status-badge" style="font-size:0.75rem;padding:2px 8px;margin-left:4px;background:var(--info);color:white;"><span class="status-dot" style="background:white;"></span>Manual</span>';
          }
        } else if (t.isLocal === false) {
          const provider = capitalizeProvider(t.externalProvider) || "External";
          statusBadge = `<span class="status-badge info" style="font-size:0.75rem;padding:2px 8px;margin-left:8px;"><span class="status-dot"></span>${escapeHtml(provider)}</span>`;
          if (t.isManualMapping && t.manualMappingType === "external") {
            statusBadge +=
              '<span class="status-badge" style="font-size:0.75rem;padding:2px 8px;margin-left:4px;background:var(--info);color:white;"><span class="status-dot" style="background:white;"></span>Manual</span>';
          }
          const firstArtist =
            t.artists && t.artists.length > 0 ? t.artists[0] : "";
          mapButton = `<button class="map-action-btn map-action-local map-track-btn"
                            data-playlist-name="${escapeHtml(name)}"
                            data-position="${t.position}"
                            data-title="${escapeHtml(t.title || "")}"
                            data-artist="${escapeHtml(firstArtist)}"
                            data-spotify-id="${escapeHtml(t.spotifyId || "")}"
                            >Map to Local</button>
                            <button class="map-action-btn map-action-external map-external-btn"
                            data-playlist-name="${escapeHtml(name)}"
                            data-position="${t.position}"
                            data-title="${escapeHtml(t.title || "")}"
                            data-artist="${escapeHtml(firstArtist)}"
                            data-spotify-id="${escapeHtml(t.spotifyId || "")}"
                            >Map to External</button>`;
        } else {
          statusBadge =
            '<span class="status-badge" style="font-size:0.75rem;padding:2px 8px;margin-left:8px;background:rgba(245, 158, 11, 0.2);color:#f59e0b;"><span class="status-dot" style="background:#f59e0b;"></span>Missing</span>';
          const firstArtist =
            t.artists && t.artists.length > 0 ? t.artists[0] : "";
          mapButton = `<button class="map-action-btn map-action-local map-track-btn"
                            data-playlist-name="${escapeHtml(name)}"
                            data-position="${t.position}"
                            data-title="${escapeHtml(t.title || "")}"
                            data-artist="${escapeHtml(firstArtist)}"
                            data-spotify-id="${escapeHtml(t.spotifyId || "")}"
                            >Map to Local</button>
                            <button class="map-action-btn map-action-external map-external-btn"
                            data-playlist-name="${escapeHtml(name)}"
                            data-position="${t.position}"
                            data-title="${escapeHtml(t.title || "")}"
                            data-artist="${escapeHtml(firstArtist)}"
                            data-spotify-id="${escapeHtml(t.spotifyId || "")}"
                            >Map to External</button>`;
        }

        const firstArtist =
          t.artists && t.artists.length > 0 ? t.artists[0] : "";
        const searchLinkText = `${t.title} - ${firstArtist}`;
        const durationSeconds = Math.floor((t.durationMs || 0) / 1000);
        const externalSearchLink =
          t.isLocal === false && t.searchQuery && t.externalProvider
            ? `<br><small style="color:var(--accent)"><a href="#" data-action="searchProvider" data-arg-query="${escapeHtml(escapeJs(t.searchQuery))}" data-arg-provider="${escapeHtml(escapeJs(t.externalProvider))}" style="color:var(--accent);text-decoration:underline;">🔍 Search: ${escapeHtml(searchLinkText)}</a></small>`
            : "";
        const missingSearchLink =
          t.isLocal === null && t.searchQuery
            ? `<br><small style="color:var(--text-secondary)"><a href="#" data-action="searchProvider" data-arg-query="${escapeHtml(escapeJs(t.searchQuery))}" data-arg-provider="squidwtf" style="color:var(--text-secondary);text-decoration:underline;">🔍 Search: ${escapeHtml(searchLinkText)}</a></small>`
            : "";

        const lyricsMapButton = `<button class="small" data-action="openLyricsMap" data-arg-artist="${escapeHtml(escapeJs(firstArtist))}" data-arg-title="${escapeHtml(escapeJs(t.title))}" data-arg-album="${escapeHtml(escapeJs(t.album || ""))}" data-arg-duration-seconds="${durationSeconds}" style="margin-left:4px;font-size:0.75rem;padding:4px 8px;background:#3b82f6;border-color:#3b82f6;color:white;">Map Lyrics ID</button>`;

        return `
                <div class="track-item" data-position="${t.position}">
                    <span class="track-position">${index + 1}</span>
                    <div class="track-info">
                        <h4>${escapeHtml(t.title)}${statusBadge}${lyricsBadge}${mapButton}${lyricsMapButton}</h4>
                        <span class="artists">${escapeHtml((t.artists || []).join(", "))}</span>
                    </div>
                    <div class="track-meta">
                        ${t.album ? escapeHtml(t.album) : ""}
                        ${t.isrc ? "<br><small>ISRC: " + t.isrc + "</small>" : ""}
                        ${externalSearchLink}
                        ${missingSearchLink}
                    </div>
                </div>
            `;
      })
      .join("");

    // Add event listeners
    document.querySelectorAll(".map-track-btn").forEach((btn) => {
      btn.addEventListener("click", function () {
        const playlistName = this.getAttribute("data-playlist-name");
        const position = parseInt(this.getAttribute("data-position"));
        const title = this.getAttribute("data-title");
        const artist = this.getAttribute("data-artist");
        const spotifyId = this.getAttribute("data-spotify-id");
        openManualMap(playlistName, position, title, artist, spotifyId);
      });
    });

    document.querySelectorAll(".map-external-btn").forEach((btn) => {
      btn.addEventListener("click", function () {
        const playlistName = this.getAttribute("data-playlist-name");
        const position = parseInt(this.getAttribute("data-position"));
        const title = this.getAttribute("data-title");
        const artist = this.getAttribute("data-artist");
        const spotifyId = this.getAttribute("data-spotify-id");
        openExternalMap(playlistName, position, title, artist, spotifyId);
      });
    });
  } catch (error) {
    console.error("Error in viewTracks:", error);
    document.getElementById("tracks-list").innerHTML =
      '<p style="text-align:center;color:var(--error);padding:40px;">Failed to load tracks: ' +
      escapeHtml(error?.message || "Unknown error") +
      "</p>";
  }
}

// Manual mapping to local Jellyfin track
export function openManualMap(
  playlistName,
  position,
  title,
  artist,
  spotifyId,
) {
  document.getElementById("local-map-spotify-title").textContent = title;
  document.getElementById("local-map-spotify-artist").textContent = artist;
  document.getElementById("local-map-position").textContent = String(
    position ?? "",
  );
  document.getElementById("local-map-playlist-name").value = playlistName;
  document.getElementById("local-map-spotify-id").value = spotifyId;
  document.getElementById("local-map-jellyfin-id").value = "";
  document.getElementById("local-map-search").value =
    `${title} ${artist}`.trim();
  document.getElementById("local-map-results").innerHTML =
    '<p style="color:var(--text-secondary);text-align:center;padding:20px;">Enter search terms and click Search</p>';
  const saveBtn = document.getElementById("local-map-save-btn");
  if (saveBtn) {
    saveBtn.disabled = true;
  }
  openModal("local-map-modal");
}

// Manual mapping to external provider
export function openExternalMap(
  playlistName,
  position,
  title,
  artist,
  spotifyId,
) {
  document.getElementById("map-spotify-title").textContent = title;
  document.getElementById("map-spotify-artist").textContent = artist;
  document.getElementById("map-position").textContent = String(position ?? "");
  document.getElementById("map-playlist-name").value = playlistName;
  document.getElementById("map-spotify-id").value = spotifyId;
  document.getElementById("map-external-id").value = "";
  const searchInput = document.getElementById("map-external-search");
  if (searchInput) {
    searchInput.value = `${title} ${artist}`.trim();
  }
  const resultsDiv = document.getElementById("map-external-results");
  if (resultsDiv) {
    resultsDiv.innerHTML =
      '<p style="color:var(--text-secondary);text-align:center;padding:20px;">Enter search terms and click Search</p>';
  }
  document.getElementById("map-external-provider").value = "SquidWTF";
  const saveBtn = document.getElementById("map-save-btn");
  if (saveBtn) {
    saveBtn.disabled = true;
  }
  openModal("manual-map-modal");
}

// Search Jellyfin for tracks
export async function searchJellyfinTracks() {
  const query = document.getElementById("local-map-search").value.trim();
  if (!query) {
    showToast("Please enter a search query", "error");
    return;
  }

  const resultsDiv = document.getElementById("local-map-results");
  resultsDiv.innerHTML =
    '<div class="loading"><span class="spinner"></span> Searching...</div>';

  try {
    const data = await API.searchJellyfin(query);

    const results = data.results || data.tracks || [];

    if (results.length === 0) {
      resultsDiv.innerHTML =
        '<p style="color:var(--text-secondary);text-align:center;padding:20px;">No results found</p>';
      return;
    }

    resultsDiv.innerHTML = results
      .map((track) => {
        const id = track.id || "";
        const title = track.name || track.title || "Unknown";
        const artist = track.artist || "";
        const album = track.album || "";
        return `
                <div class="jellyfin-result" data-jellyfin-id="${escapeHtml(id)}" data-action="selectJellyfinTrack" data-arg-jellyfin-id="${escapeHtml(escapeJs(id))}">
                    <div>
                        <strong>${escapeHtml(title)}</strong>
                        <br>
                        <span style="color:var(--text-secondary);">${escapeHtml(artist)}</span>
                        ${album ? "<br><small>" + escapeHtml(album) + "</small>" : ""}
                    </div>
                    <div style="font-family:monospace;font-size:0.75rem;color:var(--text-secondary);">
                        ${id}
                    </div>
                </div>
            `;
      })
      .join("");
  } catch (error) {
    console.error("Search error:", error);
    resultsDiv.innerHTML =
      '<p style="color:var(--error);text-align:center;padding:20px;">Search failed: ' +
      escapeHtml(error?.message || "Unknown error") +
      "</p>";
  }
}

// Select a Jellyfin track from search results
export async function selectJellyfinTrack(jellyfinId) {
  try {
    const data = await API.getJellyfinTrack(jellyfinId);
    const selectedTrack = data.track || data;
    const selectedTitle = selectedTrack?.name || selectedTrack?.title || "Track";

    document.getElementById("local-map-jellyfin-id").value = jellyfinId;
    const saveBtn = document.getElementById("local-map-save-btn");
    if (saveBtn) {
      saveBtn.disabled = false;
    }

    const selectedRow = Array.from(
      document.querySelectorAll(".jellyfin-result"),
    ).find((row) => row.getAttribute("data-jellyfin-id") === jellyfinId);
    if (selectedRow) {
      document.querySelectorAll(".jellyfin-result").forEach((row) => {
        row.style.background = "";
        row.style.border = "";
      });
      selectedRow.style.background = "var(--bg-tertiary)";
      selectedRow.style.border = "1px solid var(--accent)";
    }

    showToast(
      `Track selected: ${selectedTitle}. Click "Save Mapping" to confirm.`,
      "success",
    );
  } catch (error) {
    console.error("Failed to fetch track details:", error);
    showToast("Failed to fetch track details", "error");
  }
}

export async function searchExternalTracks() {
  const query =
    document.getElementById("map-external-search")?.value.trim() || "";
  const provider = (
    document.getElementById("map-external-provider")?.value || "SquidWTF"
  ).toLowerCase();

  if (!query) {
    showToast("Please enter a search query", "error");
    return;
  }

  const resultsDiv = document.getElementById("map-external-results");
  if (!resultsDiv) {
    return;
  }

  resultsDiv.innerHTML =
    '<div class="loading"><span class="spinner"></span> Searching...</div>';

  try {
    const data = await API.searchExternalTracks(query, provider);
    const results = data.results || [];

    if (results.length === 0) {
      resultsDiv.innerHTML =
        '<p style="color:var(--text-secondary);text-align:center;padding:20px;">No results found</p>';
      return;
    }

    resultsDiv.innerHTML = results
      .map((track, index) => {
        const id = String(track.externalId || track.id || "");
        const title = track.title || "Unknown";
        const artist = track.artist || "";
        const album = track.album || "";
        const providerName = track.externalProvider || provider;
        const externalUrl = track.url || "";

        return `
                <div class="external-result" data-result-index="${index}" data-external-id="${escapeHtml(id)}"
                  data-action="selectExternalTrack"
                  data-arg-result-index="${index}"
                  data-arg-external-id="${escapeHtml(escapeJs(id))}"
                  data-arg-title="${escapeHtml(escapeJs(title))}"
                  data-arg-artist="${escapeHtml(escapeJs(artist))}"
                  data-arg-provider="${escapeHtml(escapeJs(providerName))}"
                  data-arg-external-url="${escapeHtml(escapeJs(externalUrl))}"
                >
                    <div>
                        <strong>${escapeHtml(title)}</strong>
                        <br>
                        <span style="color:var(--text-secondary);">${escapeHtml(artist)}</span>
                        ${album ? "<br><small>" + escapeHtml(album) + "</small>" : ""}
                    </div>
                    <div class="external-result-id">
                        ${escapeHtml(id)}
                    </div>
                </div>
            `;
      })
      .join("");
  } catch (error) {
    console.error("External search error:", error);
    resultsDiv.innerHTML =
      '<p style="color:var(--error);text-align:center;padding:20px;">Search failed: ' +
      escapeHtml(error?.message || "Unknown error") +
      "</p>";
  }
}

function normalizeExternalIdForProvider(externalId, provider) {
  const normalizedProvider = (provider || "").trim().toLowerCase();
  const trimmedId = String(externalId || "").trim();

  if (!trimmedId) {
    return "";
  }

  if (normalizedProvider !== "squidwtf") {
    return trimmedId;
  }

  if (/^\d+$/.test(trimmedId)) {
    return trimmedId;
  }

  try {
    const url = new URL(trimmedId);
    const queryId = url.searchParams.get("id")?.trim() || "";
    if (/^\d+$/.test(queryId)) {
      return queryId;
    }

    const pathSegments = url.pathname.split("/").filter(Boolean);
    const lastSegment = pathSegments[pathSegments.length - 1] || "";
    if (/^\d+$/.test(lastSegment)) {
      return lastSegment;
    }
  } catch {
    return trimmedId;
  }

  return trimmedId;
}

export function selectExternalTrack(
  resultIndex,
  externalId,
  title,
  artist,
  provider,
  externalUrl,
) {
  const externalIdInput = document.getElementById("map-external-id");
  const providerSelect = document.getElementById("map-external-provider");
  if (!externalIdInput || !providerSelect) {
    return;
  }

  const normalizedProvider = (provider || "").toLowerCase();
  const providerOptionValue =
    normalizedProvider === "squidwtf" || normalizedProvider === "tidal"
      ? "SquidWTF"
      : normalizedProvider === "deezer"
        ? "Deezer"
        : normalizedProvider === "qobuz"
          ? "Qobuz"
          : providerSelect.value;

  providerSelect.value = providerOptionValue;
  const selectedProvider = providerOptionValue.toLowerCase();
  const normalizedExternalId = normalizeExternalIdForProvider(
    externalId,
    selectedProvider,
  );
  externalIdInput.value = normalizedExternalId;
  validateExternalMapping(normalizedExternalId, selectedProvider);

  const rows = document.querySelectorAll(".external-result");
  rows.forEach((row) => {
    row.classList.remove("selected");
  });

  const selectedRow = Number.isInteger(resultIndex)
    ? document.querySelector(
        `.external-result[data-result-index="${resultIndex}"]`,
      )
    : Array.from(rows).find(
        (row) => row.getAttribute("data-external-id") === externalId,
      );

  if (selectedRow) {
    selectedRow.classList.add("selected");
  }

  const providerLabel = capitalizeProvider(selectedProvider || normalizedProvider || provider);
  const idHint = normalizedExternalId ? ` Using ID ${normalizedExternalId}.` : "";
  const linkHint = externalUrl ? " URL available." : "";
  showToast(
    `Track selected: ${title} by ${artist} (${providerLabel}).${idHint}${linkHint}`,
    "success",
  );
}

// Save local (Jellyfin) mapping
export async function saveLocalMapping() {
  const playlistName = document.getElementById("local-map-playlist-name").value;
  const position = parseInt(
    document.getElementById("local-map-position").textContent || "0",
  );
  const spotifyId = document.getElementById("local-map-spotify-id").value;
  const jellyfinId = document.getElementById("local-map-jellyfin-id").value;

  if (!jellyfinId) {
    showToast("Please select a Jellyfin track first", "error");
    return;
  }

  const saveBtn = document.getElementById("local-map-save-btn");
  const originalText = saveBtn.textContent;
  saveBtn.textContent = "Saving...";
  saveBtn.disabled = true;

  try {
    await API.saveTrackMapping(playlistName, {
      position,
      spotifyId,
      jellyfinId,
      type: "jellyfin",
    });

    showToast("✓ Mapping saved successfully", "success");
    closeModal("local-map-modal");

    if (window.fetchPlaylists) window.fetchPlaylists();
    if (window.fetchTrackMappings) window.fetchTrackMappings();
  } catch (error) {
    showToast(error.message || "Failed to save mapping", "error");
  } finally {
    saveBtn.textContent = originalText;
    saveBtn.disabled = false;
  }
}

// Save external provider mapping
export async function saveManualMapping() {
  const playlistName = document.getElementById("map-playlist-name").value;
  const position = parseInt(
    document.getElementById("map-position").textContent || "0",
  );
  const spotifyId = document.getElementById("map-spotify-id").value;
  const externalId = document.getElementById("map-external-id").value.trim();
  const provider = (
    document.getElementById("map-external-provider").value || ""
  ).toLowerCase();

  if (!externalId) {
    showToast("Please enter an external track ID", "error");
    return;
  }

  if (!validateExternalMapping(externalId, provider)) {
    return;
  }

  const saveBtn = document.getElementById("map-save-btn");
  const originalText = saveBtn.textContent;
  saveBtn.textContent = "Saving...";
  saveBtn.disabled = true;

  try {
    await API.saveTrackMapping(playlistName, {
      position,
      spotifyId,
      externalId,
      externalProvider: provider,
      type: "external",
    });

    showToast("✓ External mapping saved successfully", "success");
    closeModal("manual-map-modal");

    if (window.fetchPlaylists) window.fetchPlaylists();
    if (window.fetchTrackMappings) window.fetchTrackMappings();
  } catch (error) {
    showToast(error.message || "Failed to save mapping", "error");
  } finally {
    saveBtn.textContent = originalText;
    saveBtn.disabled = false;
  }
}

// Validate external mapping ID format
export function validateExternalMapping(externalId, provider) {
  // Support inline validation calls from HTML oninput/onchange handlers.
  if (typeof externalId !== "string" || typeof provider !== "string") {
    externalId =
      document.getElementById("map-external-id")?.value?.trim() || "";
    provider = (
      document.getElementById("map-external-provider")?.value || ""
    ).toLowerCase();
  } else {
    provider = provider.toLowerCase();
  }

  if (!externalId) {
    const saveBtn = document.getElementById("map-save-btn");
    if (saveBtn) {
      saveBtn.disabled = true;
    }
    return false;
  }

  let valid = true;

  if (provider === "squidwtf") {
    if (!/^\d+$/.test(externalId) && !/^https?:\/\//.test(externalId)) {
      showToast("SquidWTF ID should be numeric or a full URL", "error");
      valid = false;
    }
  } else if (provider === "deezer") {
    if (!/^\d+$/.test(externalId) && !externalId.startsWith("http")) {
      showToast("Deezer ID should be numeric or a full URL", "error");
      valid = false;
    }
  } else if (provider === "qobuz") {
    if (!externalId.includes("/") && !/^\d+$/.test(externalId)) {
      showToast("Qobuz ID format appears invalid", "error");
      valid = false;
    }
  }

  const saveBtn = document.getElementById("map-save-btn");
  if (saveBtn) {
    saveBtn.disabled = !valid;
  }

  return valid;
}

// Open lyrics mapping modal
export function openLyricsMap(artist, title, album, durationSeconds) {
  document.getElementById("lyrics-map-artist").textContent = artist;
  document.getElementById("lyrics-map-title").textContent = title;
  document.getElementById("lyrics-map-album").textContent =
    album || "(No album)";
  document.getElementById("lyrics-map-artist-value").value = artist;
  document.getElementById("lyrics-map-title-value").value = title;
  document.getElementById("lyrics-map-album-value").value = album || "";
  document.getElementById("lyrics-map-duration").value = durationSeconds;
  document.getElementById("lyrics-map-id").value = "";

  openModal("lyrics-map-modal");
}

// Save lyrics mapping
export async function saveLyricsMapping() {
  const artist = document.getElementById("lyrics-map-artist-value").value;
  const title = document.getElementById("lyrics-map-title-value").value;
  const album = document.getElementById("lyrics-map-album-value").value;
  const durationSeconds = parseInt(
    document.getElementById("lyrics-map-duration").value,
  );
  const lyricsId = parseInt(document.getElementById("lyrics-map-id").value);

  if (!lyricsId || lyricsId <= 0) {
    showToast("Please enter a valid lyrics ID", "error");
    return;
  }

  const saveBtn = document.getElementById("lyrics-map-save-btn");
  const originalText = saveBtn.textContent;
  saveBtn.textContent = "Saving...";
  saveBtn.disabled = true;

  try {
    const data = await API.saveLyricsMapping(
      artist,
      title,
      album,
      durationSeconds,
      lyricsId,
    );

    if (data.cached && data.lyrics) {
      showToast(
        `✓ Lyrics mapped and cached: ${data.lyrics.trackName} by ${data.lyrics.artistName}`,
        "success",
        5000,
      );
    } else {
      showToast("✓ Lyrics mapping saved successfully", "success");
    }
    closeModal("lyrics-map-modal");
  } catch (error) {
    showToast(error.message || "Failed to save lyrics mapping", "error");
  } finally {
    saveBtn.textContent = originalText;
    saveBtn.disabled = false;
  }
}

// Search provider (open in new tab)
export async function searchProvider(query, provider) {
  try {
    const normalizedProvider = (provider || "squidwtf").toLowerCase();
    let searchUrl = "";

    if (normalizedProvider === "squidwtf" || normalizedProvider === "tidal") {
      const data = await API.getSquidWTFBaseUrl();
      const baseUrl = data.baseUrl;
      searchUrl = `${baseUrl}/music/search?q=${encodeURIComponent(query)}`;
    } else if (normalizedProvider === "deezer") {
      searchUrl = `https://www.deezer.com/search/${encodeURIComponent(query)}`;
    } else if (normalizedProvider === "qobuz") {
      searchUrl = `https://www.qobuz.com/search?query=${encodeURIComponent(query)}`;
    } else {
      const data = await API.getSquidWTFBaseUrl();
      const baseUrl = data.baseUrl;
      searchUrl = `${baseUrl}/music/search?q=${encodeURIComponent(query)}`;
    }

    window.open(searchUrl, "_blank");
  } catch (error) {
    console.error("Failed to open provider search:", error);
    showToast("Failed to open provider search link", "warning");
  }
}
