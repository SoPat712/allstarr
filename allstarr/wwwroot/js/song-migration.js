// Song Migration view module.
//
// Renders a list of all injected Spotify playlists where each playlist can be
// expanded to show every track that is NOT in the local Jellyfin library.
// That means tracks matched via external providers (SquidWTF, Deezer, Qobuz)
// and tracks that are still missing. A CSV download is provided so users can
// grab all non-Jellyfin tracks and feed them into their preferred download tool.

import { escapeHtml, showToast, capitalizeProvider } from "./utils.js";
import * as API from "./api.js";

let isAdminSession = () => false;
let songMigrationRequestToken = 0;

// Cache of playlist name -> tracks array, to avoid re-fetching for CSV export
// after the table has already been populated.
const trackCache = new Map();

// Tracks which playlist rows are currently expanded so refreshes preserve
// expansion state.
const expandedSongMigrationPlaylists = new Set();

// Tracks which playlists have been kicked off fetching so we don't double-fetch.
const inFlightTrackFetches = new Map();

function isNonLocalTrack(track) {
  // A track is "non-local" if it is not confirmed local in Jellyfin.
  // isLocal === true -> local (Jellyfin)     : excluded
  // isLocal === false -> external match       : included
  // isLocal === null/undefined -> missing     : included
  return track && track.isLocal !== true;
}

function summarizeTracks(tracks) {
  let external = 0;
  let missing = 0;
  for (const track of tracks) {
    if (!track) continue;
    if (track.isLocal === false) {
      external += 1;
    } else if (track.isLocal === null || track.isLocal === undefined) {
      missing += 1;
    }
  }
  return { external, missing, total: external + missing };
}

async function fetchTracksForPlaylist(playlistName) {
  if (trackCache.has(playlistName)) {
    return trackCache.get(playlistName);
  }

  if (inFlightTrackFetches.has(playlistName)) {
    return inFlightTrackFetches.get(playlistName);
  }

  const promise = (async () => {
    try {
      const data = await API.fetchPlaylistTracks(playlistName);
      const tracks = Array.isArray(data?.tracks) ? data.tracks : [];
      trackCache.set(playlistName, tracks);
      return tracks;
    } catch (error) {
      console.error(
        `Failed to fetch tracks for playlist "${playlistName}":`,
        error,
      );
      return [];
    } finally {
      inFlightTrackFetches.delete(playlistName);
    }
  })();

  inFlightTrackFetches.set(playlistName, promise);
  return promise;
}

function formatDuration(ms) {
  if (typeof ms !== "number" || Number.isNaN(ms) || ms < 0) {
    return "-";
  }
  const totalSeconds = Math.floor(ms / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${String(seconds).padStart(2, "0")}`;
}

function renderNonLocalTrackRow(track, index) {
  const artists = Array.isArray(track.artists) ? track.artists.join(", ") : "";
  const isMissing = track.isLocal === null || track.isLocal === undefined;
  const providerLabel = isMissing
    ? '<span class="status-pill warning">Missing</span>'
    : `<span class="status-pill info">${escapeHtml(
        capitalizeProvider(track.externalProvider) ||
          track.externalProvider ||
          "External",
      )}</span>`;

  const spotifyUrl = track.spotifyId
    ? `https://open.spotify.com/track/${encodeURIComponent(track.spotifyId)}`
    : null;

  const spotifyLink = spotifyUrl
    ? `<a href="${escapeHtml(
        spotifyUrl,
      )}" target="_blank" rel="noopener" class="mono" style="color:var(--accent);text-decoration:underline;">${escapeHtml(
        track.spotifyId,
      )}</a>`
    : "-";

  const isrcCell = track.isrc
    ? `<span class="mono">${escapeHtml(track.isrc)}</span>`
    : "-";

  return `
        <tr>
            <td style="color:var(--text-secondary);">${index + 1}</td>
            <td><strong>${escapeHtml(track.title || "-")}</strong></td>
            <td>${escapeHtml(artists || "-")}</td>
            <td style="color:var(--text-secondary);">${escapeHtml(track.album || "-")}</td>
            <td>${providerLabel}</td>
            <td>${isrcCell}</td>
            <td>${spotifyLink}</td>
            <td style="color:var(--text-secondary);">${escapeHtml(formatDuration(track.durationMs))}</td>
        </tr>
    `;
}

function renderNonLocalTracksPanel(tracks) {
  if (!Array.isArray(tracks) || tracks.length === 0) {
    return `
            <div class="details-panel">
                <p class="text-secondary" style="margin:0;padding:16px;">
                    🎉 Every track in this playlist is already in your Jellyfin library.
                </p>
            </div>
        `;
  }

  return `
        <div class="details-panel song-migration-tracks-panel">
            <div class="table-scroll">
                <table class="playlist-table song-migration-tracks-table">
                    <thead>
                        <tr>
                            <th style="width:40px;">#</th>
                            <th>Title</th>
                            <th>Artist</th>
                            <th>Album</th>
                            <th>Status</th>
                            <th>ISRC</th>
                            <th>Spotify</th>
                            <th>Duration</th>
                        </tr>
                    </thead>
                    <tbody>
                        ${tracks.map(renderNonLocalTrackRow).join("")}
                    </tbody>
                </table>
            </div>
        </div>
    `;
}

function renderGuidance(playlists, totals) {
  const container = document.getElementById("song-migration-guidance");
  if (!container) return;

  const messages = [];

  if (!playlists || playlists.length === 0) {
    messages.push({
      tone: "info",
      title: "No injected playlists yet.",
      detail:
        "Link a Jellyfin playlist to Spotify and run a match before migrating songs.",
    });
  } else if (totals.total === 0) {
    messages.push({
      tone: "success",
      title: "Every injected track is already in your Jellyfin library.",
      detail: "Nothing to migrate right now.",
    });
  } else {
    messages.push({
      tone: "info",
      title: `${totals.total} tracks across ${playlists.length} playlists are not in Jellyfin.`,
      detail:
        "Expand a playlist to review its non-Jellyfin tracks, or use Download CSV to grab the whole list for bulk downloading.",
    });
    if (totals.missing > 0) {
      messages.push({
        tone: "warning",
        title: `${totals.missing} tracks still could not be matched to any provider.`,
        detail:
          "These are labelled Missing in the CSV. You can map them manually from the Injected Playlists tab.",
      });
    }
  }

  container.innerHTML = messages
    .map((msg) => {
      const toneClass = msg.tone || "info";
      return `
            <div class="guidance-banner ${toneClass}">
                <strong>${escapeHtml(msg.title)}</strong>
                ${msg.detail ? `<div>${escapeHtml(msg.detail)}</div>` : ""}
            </div>
        `;
    })
    .join("");
}

function renderPlaylistRow(playlist, index) {
  const playlistName = playlist.name || "";
  const detailsRowId = `song-migration-details-${index}`;
  const detailsKey = playlistName;
  const isExpanded = expandedSongMigrationPlaylists.has(detailsKey);

  const external = playlist.externalMatched || 0;
  const missing = playlist.externalMissing || 0;
  const nonLocal = external + missing;
  const spotifyTotal = playlist.trackCount || 0;

  const escapedPlaylistName = escapeHtml(playlistName);

  const statusBadges = [];
  if (external > 0) {
    statusBadges.push(
      `<span class="status-pill info">${external} External</span>`,
    );
  }
  if (missing > 0) {
    statusBadges.push(
      `<span class="status-pill warning">${missing} Missing</span>`,
    );
  }
  if (statusBadges.length === 0) {
    statusBadges.push('<span class="status-pill success">All Local</span>');
  }

  const detailsLabel = isExpanded ? "Hide" : "Details";

  return `
        <tr class="compact-row ${isExpanded ? "expanded" : ""}"
            data-song-migration-row="${escapedPlaylistName}">
            <td>
                <div class="name-cell">
                    <strong>${escapedPlaylistName}</strong>
                    <span class="meta-text subtle-mono">${escapeHtml(playlist.id || "-")}</span>
                </div>
            </td>
            <td>
                <span class="track-count">${nonLocal}</span>
                <div class="meta-text">of ${spotifyTotal} Spotify tracks</div>
            </td>
            <td>${statusBadges.join(" ")}</td>
            <td class="row-controls">
                <button class="icon-btn song-migration-details-trigger"
                    data-song-migration-target="${escapedPlaylistName}"
                    aria-expanded="${isExpanded ? "true" : "false"}">${detailsLabel}</button>
                <button class="icon-btn song-migration-csv-btn"
                    data-song-migration-csv="${escapedPlaylistName}"
                    title="Download this playlist's non-Jellyfin tracks as CSV">CSV</button>
            </td>
        </tr>
        <tr id="${detailsRowId}"
            class="details-row"
            data-song-migration-details-for="${escapedPlaylistName}"
            ${isExpanded ? "" : "hidden"}>
            <td colspan="4">
                <div class="song-migration-details-content"
                    data-song-migration-details-content="${escapedPlaylistName}">
                    <div class="loading" style="padding:16px;">
                        <span class="spinner"></span> Loading tracks...
                    </div>
                </div>
            </td>
        </tr>
    `;
}

async function populateDetailsContent(playlistName) {
  const container = document.querySelector(
    `[data-song-migration-details-content="${CSS.escape(playlistName)}"]`,
  );
  if (!container) return;

  const tracks = await fetchTracksForPlaylist(playlistName);
  const nonLocal = tracks.filter(isNonLocalTrack);
  container.innerHTML = renderNonLocalTracksPanel(nonLocal);
}

function bindRowEvents(tbody) {
  tbody
    .querySelectorAll(".song-migration-details-trigger")
    .forEach((button) => {
      button.addEventListener("click", async (event) => {
        event.preventDefault();
        event.stopPropagation();

        const playlistName = button.getAttribute("data-song-migration-target");
        if (!playlistName) return;

        const detailsRow = tbody.querySelector(
          `tr[data-song-migration-details-for="${CSS.escape(playlistName)}"]`,
        );
        const mainRow = tbody.querySelector(
          `tr[data-song-migration-row="${CSS.escape(playlistName)}"]`,
        );
        if (!detailsRow) return;

        const isHidden = detailsRow.hasAttribute("hidden");
        if (isHidden) {
          detailsRow.removeAttribute("hidden");
          expandedSongMigrationPlaylists.add(playlistName);
          button.setAttribute("aria-expanded", "true");
          button.textContent = "Hide";
          if (mainRow) mainRow.classList.add("expanded");
          await populateDetailsContent(playlistName);
        } else {
          detailsRow.setAttribute("hidden", "");
          expandedSongMigrationPlaylists.delete(playlistName);
          button.setAttribute("aria-expanded", "false");
          button.textContent = "Details";
          if (mainRow) mainRow.classList.remove("expanded");
        }
      });
    });

  tbody.querySelectorAll(".song-migration-csv-btn").forEach((button) => {
    button.addEventListener("click", async (event) => {
      event.preventDefault();
      event.stopPropagation();

      const playlistName = button.getAttribute("data-song-migration-csv");
      if (!playlistName) return;

      await downloadPlaylistCsv(playlistName);
    });
  });
}

export async function fetchSongMigration() {
  if (!isAdminSession()) {
    return;
  }

  const tbody = document.getElementById("song-migration-table-body");
  if (!tbody) return;

  const requestToken = ++songMigrationRequestToken;

  try {
    const data = await API.fetchPlaylists();
    if (requestToken !== songMigrationRequestToken) return;

    const playlists = Array.isArray(data?.playlists) ? data.playlists : [];

    // Invalidate caches so expanding rows reflects any fresh match state.
    trackCache.clear();

    const totalExternal = playlists.reduce(
      (sum, playlist) => sum + (playlist.externalMatched || 0),
      0,
    );
    const totalMissing = playlists.reduce(
      (sum, playlist) => sum + (playlist.externalMissing || 0),
      0,
    );
    const totalNonLocal = totalExternal + totalMissing;

    const playlistCountEl = document.getElementById(
      "song-migration-playlist-count",
    );
    if (playlistCountEl) {
      playlistCountEl.textContent = String(playlists.length);
    }

    const externalCountEl = document.getElementById(
      "song-migration-external-count",
    );
    if (externalCountEl) {
      externalCountEl.textContent = String(totalExternal);
    }

    const missingCountEl = document.getElementById(
      "song-migration-missing-count",
    );
    if (missingCountEl) {
      missingCountEl.textContent = String(totalMissing);
    }

    const totalCountEl = document.getElementById("song-migration-total-count");
    if (totalCountEl) {
      totalCountEl.textContent = String(totalNonLocal);
    }

    renderGuidance(playlists, {
      external: totalExternal,
      missing: totalMissing,
      total: totalNonLocal,
    });

    if (playlists.length === 0) {
      tbody.innerHTML =
        '<tr><td colspan="4" style="text-align:center;color:var(--text-secondary);padding:40px;">No playlists configured. Link playlists from the Link Playlists tab.</td></tr>';
      return;
    }

    tbody.innerHTML = playlists
      .map((playlist, index) => renderPlaylistRow(playlist, index))
      .join("");

    bindRowEvents(tbody);

    // Re-populate any previously expanded rows so state survives refresh.
    for (const playlistName of expandedSongMigrationPlaylists) {
      await populateDetailsContent(playlistName);
    }
  } catch (error) {
    if (requestToken !== songMigrationRequestToken) return;
    console.error("Failed to fetch song migration data:", error);
    tbody.innerHTML = `
            <tr>
                <td colspan="4" style="text-align:center;color:var(--error);padding:40px;">
                    Failed to load playlists: ${escapeHtml(error?.message || "Unknown error")}
                </td>
            </tr>
        `;
  }
}

function csvEscape(value) {
  if (value === null || value === undefined) {
    return "";
  }
  const str = String(value);
  if (/[",\r\n]/.test(str)) {
    return `"${str.replace(/"/g, '""')}"`;
  }
  return str;
}

function buildCsvRows(entries) {
  const headers = [
    "Playlist",
    "Position",
    "Title",
    "Artists",
    "Album",
    "ISRC",
    "Spotify ID",
    "Spotify URL",
    "Duration (ms)",
    "Duration",
    "Status",
    "Provider",
    "Manual Mapping ID",
  ];

  const lines = [headers.map(csvEscape).join(",")];

  for (const entry of entries) {
    const { playlistName, track } = entry;
    const status = track.isLocal === false ? "External" : "Missing";
    const provider =
      track.isLocal === false ? track.externalProvider || "" : "";
    const artists = Array.isArray(track.artists)
      ? track.artists.join(", ")
      : "";
    const spotifyUrl = track.spotifyId
      ? `https://open.spotify.com/track/${track.spotifyId}`
      : "";

    lines.push(
      [
        playlistName,
        track.position ?? "",
        track.title ?? "",
        artists,
        track.album ?? "",
        track.isrc ?? "",
        track.spotifyId ?? "",
        spotifyUrl,
        typeof track.durationMs === "number" ? track.durationMs : "",
        formatDuration(track.durationMs),
        status,
        provider,
        track.manualMappingId ?? "",
      ]
        .map(csvEscape)
        .join(","),
    );
  }

  return lines.join("\r\n");
}

function triggerCsvDownload(filename, csvContent) {
  // Prefix BOM so Excel reads UTF-8 correctly.
  const blob = new Blob(["\uFEFF" + csvContent], {
    type: "text/csv;charset=utf-8;",
  });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
}

function sanitizeFilenameSegment(name) {
  return (
    String(name || "playlist")
      .replace(/[^\w\-]+/g, "_")
      .replace(/^_+|_+$/g, "")
      .slice(0, 80) || "playlist"
  );
}

async function downloadPlaylistCsv(playlistName) {
  try {
    showToast(`Preparing CSV for "${playlistName}"...`, "success", 1500);
    const tracks = await fetchTracksForPlaylist(playlistName);
    const nonLocal = tracks.filter(isNonLocalTrack);

    if (nonLocal.length === 0) {
      showToast(`No non-Jellyfin tracks in "${playlistName}"`, "warning");
      return;
    }

    const entries = nonLocal.map((track) => ({ playlistName, track }));
    const csv = buildCsvRows(entries);
    const filename = `song-migration-${sanitizeFilenameSegment(playlistName)}.csv`;
    triggerCsvDownload(filename, csv);
    showToast(
      `Downloaded ${nonLocal.length} tracks from "${playlistName}"`,
      "success",
    );
  } catch (error) {
    console.error("Failed to build playlist CSV:", error);
    showToast(
      `Failed to build CSV: ${error?.message || "Unknown error"}`,
      "error",
    );
  }
}

export async function downloadSongMigrationCsv() {
  try {
    const data = await API.fetchPlaylists();
    const playlists = Array.isArray(data?.playlists) ? data.playlists : [];

    if (playlists.length === 0) {
      showToast("No injected playlists configured.", "warning");
      return;
    }

    showToast("Building CSV, this may take a moment...", "success", 2000);

    const entries = [];
    let scanned = 0;

    for (const playlist of playlists) {
      const tracks = await fetchTracksForPlaylist(playlist.name);
      const nonLocal = tracks.filter(isNonLocalTrack);
      for (const track of nonLocal) {
        entries.push({ playlistName: playlist.name, track });
      }
      scanned += 1;
    }

    if (entries.length === 0) {
      showToast(
        "Every track across all playlists is already in Jellyfin.",
        "success",
      );
      return;
    }

    const csv = buildCsvRows(entries);
    const timestamp = new Date().toISOString().replace(/[:.]/g, "-");
    const filename = `song-migration-all-${timestamp}.csv`;
    triggerCsvDownload(filename, csv);

    showToast(
      `Downloaded ${entries.length} tracks across ${scanned} playlists.`,
      "success",
    );
  } catch (error) {
    console.error("Failed to build combined CSV:", error);
    showToast(
      `Failed to build CSV: ${error?.message || "Unknown error"}`,
      "error",
    );
  }
}

export function resetSongMigrationState() {
  trackCache.clear();
  inFlightTrackFetches.clear();
  expandedSongMigrationPlaylists.clear();
  songMigrationRequestToken = 0;
}

export function initSongMigration(options = {}) {
  isAdminSession = options.isAdminSession || (() => false);

  // Expose to window so tab-switch hooks and the ActionDispatcher can call
  // these without tight-coupling to this module's import path.
  window.fetchSongMigration = fetchSongMigration;
  window.downloadSongMigrationCsv = downloadSongMigrationCsv;

  return {
    fetchSongMigration,
    downloadSongMigrationCsv,
    resetSongMigrationState,
  };
}
