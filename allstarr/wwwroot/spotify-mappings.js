// Spotify Mappings Page JavaScript
// Handles filtering, sorting, pagination, and CRUD operations for Spotify track mappings

let currentPage = 1;
const pageSize = 50;
let currentFilters = {
  targetType: "all",
  source: "all",
  search: "",
  sortBy: null,
  sortOrder: "asc",
};

let localMapContext = null;
let localMapResults = [];
let localMapSelectedIndex = -1;
let externalMapContext = null;
const modalFocusState = new Map();

function showToast(message, type = "success", duration = 3000) {
  const toast = document.createElement("div");
  toast.className = `toast ${type}`;
  toast.textContent = message;
  document.body.appendChild(toast);
  setTimeout(() => toast.remove(), duration);
}

async function readErrorMessage(response, fallback) {
  try {
    const data = await response.json();
    return data.error || data.message || fallback;
  } catch {
    return fallback;
  }
}

/**
 * Loads mappings from the API with current filters and pagination
 */
async function loadMappings() {
  try {
    // Build query string with filters
    const params = new URLSearchParams({
      page: currentPage,
      pageSize: pageSize,
      enrichMetadata: true,
    });

    if (currentFilters.targetType !== "all") {
      params.append("targetType", currentFilters.targetType);
    }

    if (currentFilters.source !== "all") {
      params.append("source", currentFilters.source);
    }

    if (currentFilters.search) {
      params.append("search", currentFilters.search);
    }

    if (currentFilters.sortBy) {
      params.append("sortBy", currentFilters.sortBy);
      params.append("sortOrder", currentFilters.sortOrder);
    }

    const response = await fetch(`/api/admin/spotify/mappings?${params}`);
    if (!response.ok) {
      throw new Error(
        await readErrorMessage(response, "Failed to load mappings"),
      );
    }

    const data = await response.json();

    // Update stats (using PascalCase from C# API)
    document.getElementById("stat-total").textContent =
      data.stats.TotalMappings.toLocaleString();
    document.getElementById("stat-local").textContent =
      data.stats.LocalMappings.toLocaleString();
    document.getElementById("stat-external").textContent =
      data.stats.ExternalMappings.toLocaleString();
    document.getElementById("stat-manual").textContent =
      data.stats.ManualMappings.toLocaleString();
    document.getElementById("stat-auto").textContent =
      data.stats.AutoMappings.toLocaleString();

    // Update pagination
    updatePagination(data.pagination);

    // Render table
    renderMappings(data.mappings);
  } catch (error) {
    console.error("Error loading mappings:", error);
    document.getElementById("content").innerHTML =
      `<div class="error">Failed to load mappings: ${escapeHtml(error.message || "Unknown error")}</div>`;
  }
}

/**
 * Updates pagination controls
 */
function updatePagination(pagination) {
  document.getElementById("page-info").textContent =
    `Page ${pagination.page} of ${pagination.totalPages} (${pagination.totalCount} total)`;
  document.getElementById("prev-btn").disabled = currentPage === 1;
  document.getElementById("next-btn").disabled =
    currentPage === pagination.totalPages;
  document.getElementById("pagination").style.display = "flex";
}

/**
 * Renders the mappings table
 */
function renderMappings(mappings) {
  const content = document.getElementById("content");

  if (mappings.length === 0) {
    content.innerHTML = `
            <div class="empty-state">
                <h3>No mappings found</h3>
                <p>Try adjusting your filters or search query.</p>
            </div>
        `;
    return;
  }

  const rows = mappings
    .map((mapping) => {
      const metadata = mapping.Metadata || {};
      const artworkUrl = metadata.ArtworkUrl || "/placeholder.png";
      const title = metadata.Title || "Unknown Track";
      const artist = metadata.Artist || "Unknown Artist";
      const targetInfo =
        mapping.TargetType === "local"
          ? mapping.LocalId
          : `${mapping.ExternalProvider}:${mapping.ExternalId}`;

      const escapedSpotifyId = escapeHtml(escapeJs(mapping.SpotifyId || ""));
      const escapedTitle = escapeHtml(escapeJs(title));
      const escapedArtist = escapeHtml(escapeJs(artist));

      return `
            <tr>
                <td>
                    <div class="track-info">
                        <img src="${artworkUrl}" alt="${escapeHtml(title)}" class="track-artwork"
                             onerror="this.src='/placeholder.png'">
                        <div class="track-details">
                            <div class="track-title">${escapeHtml(title)}</div>
                            <div class="track-artist">${escapeHtml(artist)}</div>
                        </div>
                    </div>
                </td>
                <td>
                    <span class="mono">${escapeHtml(mapping.SpotifyId)}</span>
                </td>
                <td>
                    <span class="badge ${mapping.TargetType}">${escapeHtml(mapping.TargetType)}</span>
                </td>
                <td>
                    <span class="mono">${escapeHtml(targetInfo)}</span>
                </td>
                <td>
                    <span class="badge ${mapping.Source}">${escapeHtml(mapping.Source)}</span>
                </td>
                <td>
                    <span class="mono">${new Date(mapping.CreatedAt).toLocaleDateString()}</span>
                </td>
                <td>
                    <div class="actions-cell">
                        <button class="action-btn local" onclick="openLocalMapModal('${escapedSpotifyId}', '${escapedTitle}', '${escapedArtist}')">
                            Map to Local
                        </button>
                        <button class="action-btn external" onclick="openExternalMapModal('${escapedSpotifyId}', '${escapedTitle}', '${escapedArtist}')">
                            Map to External
                        </button>
                        <button class="action-btn danger" onclick="deleteMapping('${escapedSpotifyId}', '${escapedTitle}')">
                            Delete
                        </button>
                    </div>
                </td>
            </tr>
        `;
    })
    .join("");

  const sortIndicator = (column) => {
    if (currentFilters.sortBy === column) {
      return currentFilters.sortOrder === "asc" ? " ▲" : " ▼";
    }
    return "";
  };

  content.innerHTML = `
        <table>
            <thead>
                <tr>
                    <th class="sortable" onclick="sortBy('title')">Track${sortIndicator("title")}</th>
                    <th class="sortable" onclick="sortBy('spotifyid')">Spotify ID${sortIndicator("spotifyid")}</th>
                    <th class="sortable" onclick="sortBy('type')">Type${sortIndicator("type")}</th>
                    <th>Target ID</th>
                    <th class="sortable" onclick="sortBy('source')">Source${sortIndicator("source")}</th>
                    <th class="sortable" onclick="sortBy('created')">Created${sortIndicator("created")}</th>
                    <th>Actions</th>
                </tr>
            </thead>
            <tbody>
                ${rows}
            </tbody>
        </table>
    `;
}

/**
 * Sorts the table by the specified column
 */
function sortBy(column) {
  if (currentFilters.sortBy === column) {
    // Toggle sort order
    currentFilters.sortOrder =
      currentFilters.sortOrder === "asc" ? "desc" : "asc";
  } else {
    // New column, default to ascending
    currentFilters.sortBy = column;
    currentFilters.sortOrder = "asc";
  }

  currentPage = 1; // Reset to first page
  loadMappings();
}

/**
 * Applies filters and reloads mappings
 */
function applyFilters() {
  currentFilters.targetType = document.getElementById("filter-type").value;
  currentFilters.source = document.getElementById("filter-source").value;
  currentFilters.search = document.getElementById("search").value;

  currentPage = 1; // Reset to first page when filtering
  loadMappings();
}

function toggleModal(modalId, shouldOpen) {
  const modal = document.getElementById(modalId);
  if (!modal) {
    return;
  }

  if (shouldOpen) {
    const previousActive = document.activeElement;
    modalFocusState.set(modalId, previousActive);
    modal.setAttribute("role", "dialog");
    modal.setAttribute("aria-modal", "true");
    modal.removeAttribute("aria-hidden");
    modal.classList.add("active");
    const firstFocusable = modal.querySelector(
      'button, input, select, textarea, a[href], [tabindex]:not([tabindex="-1"])',
    );
    if (firstFocusable) {
      firstFocusable.focus();
    }
  } else {
    modal.classList.remove("active");
    modal.setAttribute("aria-hidden", "true");
    const previousActive = modalFocusState.get(modalId);
    if (previousActive && typeof previousActive.focus === "function") {
      previousActive.focus();
    }
    modalFocusState.delete(modalId);
  }
}

function openLocalMapModal(spotifyId, title, artist) {
  localMapContext = { spotifyId, title, artist };
  localMapResults = [];
  localMapSelectedIndex = -1;

  document.getElementById("local-map-title").textContent = title;
  document.getElementById("local-map-artist").textContent = artist;
  document.getElementById("local-map-spotify-id").textContent = spotifyId;
  document.getElementById("local-map-search").value =
    `${title} ${artist}`.trim();
  document.getElementById("local-map-save-btn").disabled = true;
  document.getElementById("local-map-results").innerHTML =
    '<div class="loading" style="padding:16px;">Search to find matching local tracks.</div>';

  toggleModal("local-map-modal", true);
}

function closeLocalMapModal() {
  toggleModal("local-map-modal", false);
  localMapContext = null;
  localMapResults = [];
  localMapSelectedIndex = -1;
}

function normalizeLocalTrack(track) {
  return {
    id: track.id || track.Id || "",
    title: track.title || track.name || track.Name || "Unknown Track",
    artist:
      track.artist ||
      track.Artist ||
      (Array.isArray(track.artists) ? track.artists[0] || "" : ""),
    album: track.album || track.Album || "",
  };
}

function renderLocalMapResults() {
  const resultsContainer = document.getElementById("local-map-results");

  if (!localMapResults.length) {
    resultsContainer.innerHTML =
      '<div class="loading" style="padding:16px;">No local tracks found for this search.</div>';
    return;
  }

  resultsContainer.innerHTML = localMapResults
    .map((track, index) => {
      const selectedClass = index === localMapSelectedIndex ? " selected" : "";
      return `
            <div class="local-result${selectedClass}" data-index="${index}">
                <div>
                    <strong>${escapeHtml(track.title)}</strong>
                    <div class="meta">${escapeHtml(track.artist || "Unknown Artist")}</div>
                    <div class="meta">${escapeHtml(track.album || "Unknown Album")}</div>
                </div>
                <div class="mono">${escapeHtml(track.id)}</div>
            </div>
        `;
    })
    .join("");

  Array.from(resultsContainer.querySelectorAll(".local-result")).forEach(
    (row) => {
      row.addEventListener("click", () => {
        const index = Number(row.getAttribute("data-index"));
        localMapSelectedIndex = index;
        document.getElementById("local-map-save-btn").disabled = false;
        renderLocalMapResults();
      });
    },
  );
}

async function searchLocalTracks() {
  const query = document.getElementById("local-map-search").value.trim();
  if (!query) {
    showToast("Enter a search query first.", "error");
    return;
  }

  const resultsContainer = document.getElementById("local-map-results");
  resultsContainer.innerHTML =
    '<div class="loading" style="padding:16px;">Searching local library...</div>';

  try {
    const response = await fetch(
      `/api/admin/jellyfin/search?query=${encodeURIComponent(query)}`,
    );
    if (!response.ok) {
      throw new Error(await readErrorMessage(response, "Search failed"));
    }

    const data = await response.json();
    const rawTracks = Array.isArray(data.tracks)
      ? data.tracks
      : Array.isArray(data.results)
        ? data.results
        : [];

    localMapResults = rawTracks
      .map(normalizeLocalTrack)
      .filter((track) => track.id);
    localMapSelectedIndex = -1;
    document.getElementById("local-map-save-btn").disabled = true;
    renderLocalMapResults();
  } catch (error) {
    console.error("Local search failed:", error);
    resultsContainer.innerHTML = `<div class="error" style="margin:10px;">${escapeHtml(error.message || "Search failed")}</div>`;
  }
}

async function saveLocalMap() {
  if (
    !localMapContext ||
    localMapSelectedIndex < 0 ||
    localMapSelectedIndex >= localMapResults.length
  ) {
    showToast("Select a local track first.", "error");
    return;
  }

  const selectedTrack = localMapResults[localMapSelectedIndex];
  const saveBtn = document.getElementById("local-map-save-btn");
  saveBtn.disabled = true;
  const originalText = saveBtn.textContent;
  saveBtn.textContent = "Saving...";

  try {
    const response = await fetch("/api/admin/spotify/mappings", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        SpotifyId: localMapContext.spotifyId,
        TargetType: "local",
        LocalId: selectedTrack.id,
        Metadata: {
          Title: selectedTrack.title || localMapContext.title,
          Artist: selectedTrack.artist || localMapContext.artist,
          Album: selectedTrack.album || "",
        },
      }),
    });

    if (!response.ok) {
      throw new Error(
        await readErrorMessage(response, "Failed to save mapping"),
      );
    }

    closeLocalMapModal();
    showToast(`Mapped to local track: ${selectedTrack.title}`, "success");
    await loadMappings();
  } catch (error) {
    console.error("Error saving local mapping:", error);
    showToast(error.message || "Failed to save local mapping", "error");
  } finally {
    saveBtn.textContent = originalText;
    saveBtn.disabled = false;
  }
}

function openExternalMapModal(spotifyId, title, artist) {
  externalMapContext = { spotifyId, title, artist };

  document.getElementById("external-map-title").textContent = title;
  document.getElementById("external-map-artist").textContent = artist;
  document.getElementById("external-map-spotify-id").textContent = spotifyId;
  document.getElementById("external-map-provider").value = "squidwtf";
  document.getElementById("external-map-id").value = "";
  document.getElementById("external-map-save-btn").disabled = true;

  toggleModal("external-map-modal", true);
}

function closeExternalMapModal() {
  toggleModal("external-map-modal", false);
  externalMapContext = null;
}

function validateExternalMapForm() {
  const externalId = document.getElementById("external-map-id").value.trim();
  document.getElementById("external-map-save-btn").disabled = !externalId;
}

async function saveExternalMap() {
  if (!externalMapContext) {
    return;
  }

  const provider = document
    .getElementById("external-map-provider")
    .value.trim()
    .toLowerCase();
  const externalId = document.getElementById("external-map-id").value.trim();

  if (!externalId) {
    showToast("Enter an external ID first.", "error");
    return;
  }

  const saveBtn = document.getElementById("external-map-save-btn");
  saveBtn.disabled = true;
  const originalText = saveBtn.textContent;
  saveBtn.textContent = "Saving...";

  try {
    const response = await fetch("/api/admin/spotify/mappings", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        SpotifyId: externalMapContext.spotifyId,
        TargetType: "external",
        ExternalProvider: provider,
        ExternalId: externalId,
        Metadata: {
          Title: externalMapContext.title,
          Artist: externalMapContext.artist,
        },
      }),
    });

    if (!response.ok) {
      throw new Error(
        await readErrorMessage(response, "Failed to save mapping"),
      );
    }

    closeExternalMapModal();
    showToast(`Mapped to external track: ${provider}:${externalId}`, "success");
    await loadMappings();
  } catch (error) {
    console.error("Error saving external mapping:", error);
    showToast(error.message || "Failed to save external mapping", "error");
  } finally {
    saveBtn.textContent = originalText;
    saveBtn.disabled = false;
  }
}

/**
 * Escapes HTML to prevent XSS
 */
function escapeHtml(text) {
  const div = document.createElement("div");
  div.textContent = text;
  return div.innerHTML;
}

function escapeJs(text) {
  return String(text)
    .replace(/\\/g, "\\\\")
    .replace(/'/g, "\\'")
    .replace(/\"/g, '\\"')
    .replace(/\n/g, "\\n");
}

/**
 * Deletes a Spotify track mapping
 */
async function deleteMapping(spotifyId, title) {
  if (!confirm(`Delete mapping for "${title}"?`)) {
    return;
  }

  try {
    const response = await fetch(`/api/admin/spotify/mappings/${spotifyId}`, {
      method: "DELETE",
    });

    if (!response.ok) {
      throw new Error(
        await readErrorMessage(response, "Failed to delete mapping"),
      );
    }

    showToast(`Deleted mapping for "${title}"`, "success");
    await loadMappings();
  } catch (error) {
    console.error("Error deleting mapping:", error);
    showToast(error.message || "Failed to delete mapping", "error");
  }
}

/**
 * Initializes event listeners
 */
function initializeEventListeners() {
  // Search with debounce
  let searchTimeout;
  document.getElementById("search").addEventListener("input", () => {
    clearTimeout(searchTimeout);
    searchTimeout = setTimeout(applyFilters, 300);
  });

  // Filter dropdowns
  document
    .getElementById("filter-type")
    .addEventListener("change", applyFilters);
  document
    .getElementById("filter-source")
    .addEventListener("change", applyFilters);

  // Pagination
  document.getElementById("prev-btn").addEventListener("click", () => {
    if (currentPage > 1) {
      currentPage--;
      loadMappings();
    }
  });

  document.getElementById("next-btn").addEventListener("click", () => {
    currentPage++;
    loadMappings();
  });

  // Local map modal
  document
    .getElementById("local-map-cancel-btn")
    .addEventListener("click", closeLocalMapModal);
  document
    .getElementById("local-map-search-btn")
    .addEventListener("click", searchLocalTracks);
  document
    .getElementById("local-map-save-btn")
    .addEventListener("click", saveLocalMap);
  document
    .getElementById("local-map-search")
    .addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        searchLocalTracks();
      }
    });

  // External map modal
  document
    .getElementById("external-map-cancel-btn")
    .addEventListener("click", closeExternalMapModal);
  document
    .getElementById("external-map-id")
    .addEventListener("input", validateExternalMapForm);
  document
    .getElementById("external-map-provider")
    .addEventListener("change", validateExternalMapForm);
  document
    .getElementById("external-map-save-btn")
    .addEventListener("click", saveExternalMap);

  // Backdrop close
  document
    .getElementById("local-map-modal")
    .addEventListener("click", (event) => {
      if (event.target.id === "local-map-modal") {
        closeLocalMapModal();
      }
    });

  document
    .getElementById("external-map-modal")
    .addEventListener("click", (event) => {
      if (event.target.id === "external-map-modal") {
        closeExternalMapModal();
      }
    });

  // Escape to close modals
  document.addEventListener("keydown", (event) => {
    if (event.key !== "Escape") {
      return;
    }

    closeLocalMapModal();
    closeExternalMapModal();
  });

  document.querySelectorAll(".modal-overlay").forEach((modal) => {
    modal.setAttribute("aria-hidden", "true");
  });
}

// Initialize on page load
document.addEventListener("DOMContentLoaded", () => {
  initializeEventListeners();
  loadMappings();
});
