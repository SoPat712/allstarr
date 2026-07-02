// UI updates and DOM manipulation

import { escapeHtml, escapeJs, capitalizeProvider } from "./utils.js";
import {
  collectExternalTargets,
  renderExternalTargetsHtml,
} from "./mapping-targets.js";

let rowMenuHandlersBound = false;
let tableRowHandlersBound = false;
const expandedInjectedPlaylistDetails = new Set();
let openInjectedPlaylistMenuKey = null;

function bindRowMenuHandlers() {
  if (rowMenuHandlersBound) {
    return;
  }

  document.addEventListener("click", () => {
    closeAllRowMenus();
  });

  rowMenuHandlersBound = true;
}

function bindTableRowHandlers() {
  if (tableRowHandlersBound) {
    return;
  }

  document.addEventListener("click", (event) => {
    const detailsTrigger = event.target.closest?.(
      "button.details-trigger[data-details-target]",
    );
    if (detailsTrigger) {
      const target = detailsTrigger.getAttribute("data-details-target");
      if (target) {
        toggleDetailsRow(event, target);
      }
      return;
    }

    const row = event.target.closest?.("tr.compact-row[data-details-row]");
    if (!row) {
      return;
    }

    if (event.target.closest("button, a, .row-actions-menu")) {
      return;
    }

    const detailsRowId = row.getAttribute("data-details-row");
    if (detailsRowId) {
      toggleDetailsRow(null, detailsRowId);
    }
  });

  tableRowHandlersBound = true;
}

function closeAllRowMenus(exceptId = null) {
  document.querySelectorAll(".row-actions-menu.open").forEach((menu) => {
    if (!exceptId || menu.id !== exceptId) {
      menu.classList.remove("open");
      const trigger = menu.parentElement?.querySelector?.(".menu-trigger");
      if (trigger) {
        trigger.setAttribute("aria-expanded", "false");
      }
    }
  });

  if (!exceptId) {
    openInjectedPlaylistMenuKey = null;
  }
}

function closeRowMenu(event, menuId) {
  if (event) {
    event.stopPropagation();
  }

  const menu = document.getElementById(menuId);
  if (menu) {
    menu.classList.remove("open");
    const trigger = menu.parentElement?.querySelector?.(".menu-trigger");
    if (trigger) {
      trigger.setAttribute("aria-expanded", "false");
    }
    if (menu.dataset.menuKey) {
      openInjectedPlaylistMenuKey = null;
    }
  }
}

function toggleRowMenu(event, menuId) {
  if (event) {
    event.stopPropagation();
  }

  const menu = document.getElementById(menuId);
  if (!menu) {
    return;
  }

  const isOpen = menu.classList.contains("open");
  closeAllRowMenus(menuId);
  menu.classList.toggle("open", !isOpen);
  const trigger = menu.parentElement?.querySelector?.(".menu-trigger");
  if (trigger) {
    trigger.setAttribute("aria-expanded", String(!isOpen));
  }

  if (menu.dataset.menuKey) {
    openInjectedPlaylistMenuKey = isOpen ? null : menu.dataset.menuKey;
  }
}

function toggleDetailsRow(event, detailsRowId) {
  if (event) {
    event.stopPropagation();
  }

  const detailsRow = document.getElementById(detailsRowId);
  if (!detailsRow) {
    return;
  }

  const isHidden = detailsRow.hasAttribute("hidden");
  if (isHidden) {
    detailsRow.removeAttribute("hidden");
  } else {
    detailsRow.setAttribute("hidden", "");
  }

  const isExpanded = isHidden;
  document
    .querySelectorAll(`[data-details-target="${detailsRowId}"]`)
    .forEach((trigger) => {
      trigger.setAttribute("aria-expanded", String(isExpanded));
      if (trigger.classList.contains("details-trigger")) {
        trigger.textContent = isExpanded ? "Hide" : "Details";
      }
    });

  const parentRow = document.querySelector(
    `tr[data-details-row="${detailsRowId}"]`,
  );
  if (parentRow) {
    parentRow.classList.toggle("expanded", isExpanded);

    // Persist Injected Playlists details expansion across auto-refreshes.
    if (parentRow.closest("#playlist-table-body")) {
      const detailsKey = parentRow.getAttribute("data-details-key");
      if (detailsKey) {
        if (isExpanded) {
          expandedInjectedPlaylistDetails.add(detailsKey);
        } else {
          expandedInjectedPlaylistDetails.delete(detailsKey);
        }
      }
    }
  }
}

function onCompactRowClick(event, detailsRowId) {
  if (event.target.closest("button, a, .row-actions-menu")) {
    return;
  }

  toggleDetailsRow(null, detailsRowId);
}

function renderGuidance(containerId, entries) {
  const container = document.getElementById(containerId);
  if (!container) {
    return;
  }

  if (!entries || entries.length === 0) {
    container.innerHTML = "";
    return;
  }

  container.innerHTML = entries
    .map((entry) => {
      const tone =
        entry.tone === "warning"
          ? "warning"
          : entry.tone === "success"
            ? "success"
            : "info";
      const defaultIcon =
        tone === "warning" ? "⚠️" : tone === "success" ? "✔" : "ℹ️";
      const icon = escapeHtml(entry.icon || defaultIcon);
      const title = escapeHtml(entry.title || "");
      const detail = entry.detail
        ? `<div class="guidance-detail">${escapeHtml(entry.detail)}</div>`
        : "";

      return `
                <div class="guidance-banner ${tone}">
                    <span>${icon}</span>
                    <div class="guidance-content">
                        <div class="guidance-title">${title}</div>
                        ${detail}
                    </div>
                </div>
            `;
    })
    .join("");
}

function getPlaylistStatusSummary(playlist) {
  const spotifyTotal = playlist.trackCount || 0;
  const localCount = playlist.localTracks || 0;
  const externalMatched = playlist.externalMatched || 0;
  const externalMissing = playlist.externalMissing || 0;
  const totalPlayable = playlist.totalPlayable || localCount + externalMatched;
  const completionPct =
    spotifyTotal > 0 ? Math.round((totalPlayable / spotifyTotal) * 100) : 0;

  let statusClass = "info";
  let statusLabel = "In Progress";

  if (spotifyTotal === 0) {
    statusClass = "neutral";
    statusLabel = "No Tracks";
  } else if (externalMissing > 0) {
    statusClass = "warning";
    statusLabel = `${externalMissing} Missing`;
  } else if (completionPct >= 100) {
    statusClass = "success";
    statusLabel = "Complete";
  } else {
    statusClass = "info";
    statusLabel = `${completionPct}% Matched`;
  }

  const completionClass =
    completionPct >= 100 ? "success" : externalMissing > 0 ? "warning" : "info";

  return {
    spotifyTotal,
    localCount,
    externalMatched,
    externalMissing,
    totalPlayable,
    completionPct,
    statusClass,
    statusLabel,
    completionClass,
  };
}

function syncElementAttributes(target, source) {
  if (!target || !source) {
    return;
  }

  const sourceAttributes = new Map(
    Array.from(source.attributes || []).map((attribute) => [
      attribute.name,
      attribute.value,
    ]),
  );

  Array.from(target.attributes || []).forEach((attribute) => {
    if (!sourceAttributes.has(attribute.name)) {
      target.removeAttribute(attribute.name);
    }
  });

  sourceAttributes.forEach((value, name) => {
    target.setAttribute(name, value);
  });
}

function syncPlaylistRowActionsWrap(existingWrap, nextWrap) {
  if (!existingWrap || !nextWrap) {
    return;
  }

  syncElementAttributes(existingWrap, nextWrap);

  const activeElement = document.activeElement;
  let focusTarget = null;

  if (activeElement && existingWrap.contains(activeElement)) {
    if (activeElement.classList.contains("menu-trigger")) {
      focusTarget = { type: "trigger" };
    } else if (activeElement.tagName === "BUTTON") {
      focusTarget = {
        type: "menu-item",
        action: activeElement.getAttribute("data-action") || "",
        text: activeElement.textContent || "",
      };
    }
  }

  const existingTrigger = existingWrap.querySelector(".menu-trigger");
  const nextTrigger = nextWrap.querySelector(".menu-trigger");
  if (existingTrigger && nextTrigger) {
    syncElementAttributes(existingTrigger, nextTrigger);
    existingTrigger.textContent = nextTrigger.textContent;
  } else if (nextTrigger && !existingTrigger) {
    existingWrap.prepend(nextTrigger.cloneNode(true));
  } else if (existingTrigger && !nextTrigger) {
    existingTrigger.remove();
  }

  const existingMenu = existingWrap.querySelector(".row-actions-menu");
  const nextMenu = nextWrap.querySelector(".row-actions-menu");
  if (existingMenu && nextMenu) {
    syncElementAttributes(existingMenu, nextMenu);
    existingMenu.replaceChildren(
      ...Array.from(nextMenu.children).map((child) => child.cloneNode(true)),
    );
  } else if (nextMenu && !existingMenu) {
    existingWrap.append(nextMenu.cloneNode(true));
  } else if (existingMenu && !nextMenu) {
    existingMenu.remove();
  }

  if (!focusTarget) {
    return;
  }

  if (focusTarget.type === "trigger") {
    existingWrap.querySelector(".menu-trigger")?.focus();
    return;
  }

  const matchingButton =
    Array.from(existingWrap.querySelectorAll(".row-actions-menu button")).find(
      (button) =>
        (button.getAttribute("data-action") || "") === focusTarget.action &&
        button.textContent === focusTarget.text,
    ) ||
    Array.from(existingWrap.querySelectorAll(".row-actions-menu button")).find(
      (button) =>
        (button.getAttribute("data-action") || "") === focusTarget.action,
    );

  matchingButton?.focus();
}

function syncPlaylistControlsCell(
  existingControlsCell,
  nextControlsCell,
  preserveOpenMenu = false,
) {
  if (!existingControlsCell || !nextControlsCell) {
    return;
  }

  syncElementAttributes(existingControlsCell, nextControlsCell);

  if (!preserveOpenMenu) {
    existingControlsCell.innerHTML = nextControlsCell.innerHTML;
    return;
  }

  const existingDetailsTrigger =
    existingControlsCell.querySelector(".details-trigger");
  const nextDetailsTrigger = nextControlsCell.querySelector(".details-trigger");
  const existingWrap = existingControlsCell.querySelector(".row-actions-wrap");
  const nextWrap = nextControlsCell.querySelector(".row-actions-wrap");

  if (
    !existingDetailsTrigger ||
    !nextDetailsTrigger ||
    !existingWrap ||
    !nextWrap
  ) {
    existingControlsCell.innerHTML = nextControlsCell.innerHTML;
    return;
  }

  syncElementAttributes(existingDetailsTrigger, nextDetailsTrigger);
  existingDetailsTrigger.textContent = nextDetailsTrigger.textContent;
  syncPlaylistRowActionsWrap(existingWrap, nextWrap);
}

function syncPlaylistMainRow(
  existingMainRow,
  nextMainRow,
  preserveOpenMenu = false,
) {
  if (!existingMainRow || !nextMainRow) {
    return;
  }

  syncElementAttributes(existingMainRow, nextMainRow);

  const nextCells = Array.from(nextMainRow.children);
  const existingCells = Array.from(existingMainRow.children);

  if (!preserveOpenMenu || nextCells.length !== existingCells.length) {
    existingMainRow.innerHTML = nextMainRow.innerHTML;
    return;
  }

  nextCells.forEach((nextCell, index) => {
    const existingCell = existingCells[index];
    if (!existingCell) {
      existingMainRow.append(nextCell.cloneNode(true));
      return;
    }

    if (index === nextCells.length - 1) {
      syncPlaylistControlsCell(existingCell, nextCell, preserveOpenMenu);
      return;
    }

    existingCell.replaceWith(nextCell.cloneNode(true));
  });

  while (existingMainRow.children.length > nextCells.length) {
    existingMainRow.lastElementChild?.remove();
  }
}

function syncPlaylistDetailsRow(existingDetailsRow, nextDetailsRow) {
  if (!existingDetailsRow || !nextDetailsRow) {
    return;
  }

  syncElementAttributes(existingDetailsRow, nextDetailsRow);
  existingDetailsRow.innerHTML = nextDetailsRow.innerHTML;
}

function renderPlaylistRowPairMarkup(playlist, index) {
  const summary = getPlaylistStatusSummary(playlist);
  const detailsRowId = `playlist-details-${index}`;
  const menuId = `playlist-menu-${index}`;
  const detailsKey = `${playlist.id || playlist.name || index}`;
  const isExpanded = expandedInjectedPlaylistDetails.has(detailsKey);
  const isMenuOpen = openInjectedPlaylistMenuKey === detailsKey;
  const syncSchedule = playlist.syncSchedule || "0 8 * * *";
  const escapedPlaylistName = escapeHtml(playlist.name);
  const escapedSyncSchedule = escapeHtml(syncSchedule);
  const escapedDetailsKey = escapeHtml(detailsKey);

  const breakdownBadges = [
    `<span class="status-pill neutral">${summary.localCount} Local</span>`,
    `<span class="status-pill info">${summary.externalMatched} External</span>`,
  ];

  if (summary.externalMissing > 0) {
    breakdownBadges.push(
      `<span class="status-pill warning">${summary.externalMissing} Missing</span>`,
    );
  }

  return `
            <tr class="compact-row ${isExpanded ? "expanded" : ""}" data-details-row="${detailsRowId}" data-details-key="${escapedDetailsKey}">
                <td>
                    <div class="name-cell">
                        <strong>${escapeHtml(playlist.name)}</strong>
                        <span class="meta-text subtle-mono">${escapeHtml(playlist.id || "-")}</span>
                    </div>
                </td>
                <td>
                    <span class="track-count">${summary.totalPlayable}/${summary.spotifyTotal}</span>
                    <div class="meta-text">${summary.completionPct}% playable</div>
                </td>
                <td><span class="status-pill ${summary.statusClass}">${summary.statusLabel}</span></td>
                <td class="row-controls">
                    <button class="icon-btn details-trigger" data-details-target="${detailsRowId}" aria-expanded="${isExpanded ? "true" : "false"}">${isExpanded ? "Hide" : "Details"}</button>
                    <div class="row-actions-wrap">
                        <button class="icon-btn menu-trigger" aria-haspopup="true" aria-expanded="${isMenuOpen ? "true" : "false"}"
                            data-action="toggleRowMenu" data-arg-menu-id="${menuId}">...</button>
                        <div class="row-actions-menu ${isMenuOpen ? "open" : ""}" id="${menuId}" data-menu-key="${escapedDetailsKey}" role="menu">
                            <button data-action="viewTracks" data-arg-playlist-name="${escapedPlaylistName}">View Tracks</button>
                            <button data-action="refreshPlaylist" data-arg-playlist-name="${escapedPlaylistName}">Refresh</button>
                            <button data-action="matchPlaylistTracks" data-arg-playlist-name="${escapedPlaylistName}">Rematch</button>
                            <button data-action="clearPlaylistCache" data-arg-playlist-name="${escapedPlaylistName}">Rebuild</button>
                            <button data-action="editPlaylistSchedule" data-arg-playlist-name="${escapedPlaylistName}" data-arg-sync-schedule="${escapedSyncSchedule}">Edit Schedule</button>
                            <hr>
                            <button class="danger-item" data-action="removePlaylist" data-arg-playlist-name="${escapedPlaylistName}">Remove Playlist</button>
                        </div>
                    </div>
                </td>
            </tr>
            <tr id="${detailsRowId}" class="details-row" ${isExpanded ? "" : "hidden"}>
                <td colspan="4">
                    <div class="details-panel">
                        <div class="details-grid">
                            <div class="detail-item">
                                <span class="detail-label">Sync Schedule</span>
                                <span class="detail-value mono">
                                    ${escapeHtml(syncSchedule)}
                                    <button class="inline-action-link" data-action="editPlaylistSchedule" data-arg-playlist-name="${escapedPlaylistName}" data-arg-sync-schedule="${escapedSyncSchedule}">Edit</button>
                                </span>
                            </div>
                            <div class="detail-item">
                                <span class="detail-label">Cache Age</span>
                                <span class="detail-value">${escapeHtml(playlist.cacheAge || "-")}</span>
                            </div>
                            <div class="detail-item">
                                <span class="detail-label">Track Breakdown</span>
                                <span class="detail-value">${breakdownBadges.join(" ")}</span>
                            </div>
                            <div class="detail-item">
                                <span class="detail-label">Completion</span>
                                <div class="completion-bar">
                                    <div class="completion-fill ${summary.completionClass}" style="width:${Math.max(0, Math.min(summary.completionPct, 100))}%;"></div>
                                </div>
                            </div>
                        </div>
                    </div>
                </td>
            </tr>
        `;
}

function createPlaylistRowPair(playlist, index) {
  const template = document.createElement("template");
  template.innerHTML = renderPlaylistRowPairMarkup(playlist, index).trim();
  const [mainRow, detailsRow] = template.content.querySelectorAll("tr");
  return { mainRow, detailsRow };
}

if (typeof window !== "undefined") {
  window.toggleRowMenu = toggleRowMenu;
  window.closeRowMenu = closeRowMenu;
  window.toggleDetailsRow = toggleDetailsRow;
  window.onCompactRowClick = onCompactRowClick;
}

bindRowMenuHandlers();
bindTableRowHandlers();

export function updateStatusUI(data) {
  const sidebarVersionEl = document.getElementById("sidebar-version");
  if (sidebarVersionEl) sidebarVersionEl.textContent = "v" + data.version;

  const backendTypeEl = document.getElementById("backend-type");
  if (backendTypeEl) backendTypeEl.textContent = data.backendType;

  const jellyfinUrlEl = document.getElementById("jellyfin-url");
  if (jellyfinUrlEl) jellyfinUrlEl.textContent = data.jellyfinUrl || "-";

  const playlistCountEl = document.getElementById("playlist-count");
  if (playlistCountEl) {
    playlistCountEl.textContent = data.spotifyImport.playlistCount;
  }

  const cacheDurationEl = document.getElementById("cache-duration");
  if (cacheDurationEl) {
    cacheDurationEl.textContent = data.spotify.cacheDurationMinutes + " min";
  }

  const isrcMatchingEl = document.getElementById("isrc-matching");
  if (isrcMatchingEl) {
    isrcMatchingEl.textContent = data.spotify.preferIsrcMatching
      ? "Enabled"
      : "Disabled";
  }

  const spotifyUserEl = document.getElementById("spotify-user");
  if (spotifyUserEl) spotifyUserEl.textContent = data.spotify.user || "-";

  const statusBadge = document.getElementById("spotify-status");
  const authStatus = document.getElementById("spotify-auth-status");
  const guidance = [];

  if (data.spotify.authStatus === "configured") {
    if (statusBadge) {
      statusBadge.className = "status-badge success";
      statusBadge.innerHTML = '<span class="status-dot"></span>Spotify Ready';
    }
    if (authStatus) {
      authStatus.textContent = "Cookie Set";
      authStatus.className = "stat-value success";
    }
    guidance.push({
      tone: "success",
      title: "Spotify is connected and ready.",
      detail: "Use Rebuild only when Spotify playlist content changes.",
    });
  } else if (data.spotify.authStatus === "missing_cookie") {
    if (statusBadge) {
      statusBadge.className = "status-badge warning";
      statusBadge.innerHTML = '<span class="status-dot"></span>Cookie Missing';
    }
    if (authStatus) {
      authStatus.textContent = "No Cookie";
      authStatus.className = "stat-value warning";
    }
    guidance.push({
      tone: "warning",
      title: "Spotify session cookie is missing.",
      detail: "Open Configuration > Spotify API Settings and add sp_dc.",
    });
  } else {
    if (statusBadge) {
      statusBadge.className = "status-badge info";
      statusBadge.innerHTML = '<span class="status-dot"></span>Not Configured';
    }
    if (authStatus) {
      authStatus.textContent = "Not Configured";
      authStatus.className = "stat-value info";
    }
    guidance.push({
      tone: "info",
      title: "Spotify is not configured yet.",
      detail:
        "Enable Spotify API and set a valid session cookie to link playlists.",
    });
  }

  renderGuidance("dashboard-guidance", guidance);
}

export function updatePlaylistsUI(data) {
  const tbody = document.getElementById("playlist-table-body");
  if (!tbody) {
    return;
  }

  const playlists = data.playlists || [];

  if (playlists.length === 0) {
    expandedInjectedPlaylistDetails.clear();
    openInjectedPlaylistMenuKey = null;
    tbody.innerHTML =
      '<tr><td colspan="4" style="text-align:center;color:var(--text-secondary);padding:40px;">No playlists configured. Link playlists from the Link Playlists tab.</td></tr>';
    renderGuidance("playlists-guidance", [
      {
        tone: "info",
        title: "No injected playlists yet.",
        detail:
          "Go to Link Playlists and connect a Jellyfin playlist to Spotify.",
      },
    ]);
    return;
  }

  const missingTotal = playlists.reduce(
    (total, playlist) => total + (playlist.externalMissing || 0),
    0,
  );
  const incompleteCount = playlists.reduce((total, playlist) => {
    const summary = getPlaylistStatusSummary(playlist);
    return total + (summary.completionPct < 100 ? 1 : 0);
  }, 0);

  const guidance = [];
  if (missingTotal > 0) {
    const playlistsWithMissing = playlists.filter(
      (playlist) => (playlist.externalMissing || 0) > 0,
    ).length;
    guidance.push({
      tone: "warning",
      title: `${missingTotal} tracks still need attention across ${playlistsWithMissing} playlists.`,
      detail:
        "Open a row and use ... > Rematch, then map any tracks that still cannot be matched.",
    });
  } else if (incompleteCount > 0) {
    guidance.push({
      tone: "info",
      title: `${incompleteCount} playlists are still syncing.`,
      detail: "Use Rematch when your local library changed.",
    });
  } else {
    guidance.push({
      tone: "success",
      title: "All injected playlists are fully matched.",
      detail: "No action needed right now.",
    });
  }
  guidance.push({
    tone: "info",
    title: "Use Rebuild only when Spotify playlist content changed.",
    detail: "Use Rematch when your local library changed.",
  });
  renderGuidance("playlists-guidance", guidance);

  const existingPairs = new Map();
  Array.from(
    tbody.querySelectorAll("tr.compact-row[data-details-key]"),
  ).forEach((mainRow) => {
    const detailsKey = mainRow.getAttribute("data-details-key");
    if (!detailsKey || existingPairs.has(detailsKey)) {
      return;
    }

    const detailsRowId = mainRow.getAttribute("data-details-row");
    const detailsRow =
      (detailsRowId && document.getElementById(detailsRowId)) ||
      mainRow.nextElementSibling;
    if (!detailsRow) {
      return;
    }

    existingPairs.set(detailsKey, { mainRow, detailsRow });
  });

  const orderedRows = [];
  playlists.forEach((playlist, index) => {
    const detailsKey = `${playlist.id || playlist.name || index}`;
    const { mainRow: nextMainRow, detailsRow: nextDetailsRow } =
      createPlaylistRowPair(playlist, index);
    const existingPair = existingPairs.get(detailsKey);

    if (!existingPair) {
      orderedRows.push(nextMainRow, nextDetailsRow);
      return;
    }

    syncPlaylistMainRow(
      existingPair.mainRow,
      nextMainRow,
      detailsKey === openInjectedPlaylistMenuKey,
    );
    syncPlaylistDetailsRow(existingPair.detailsRow, nextDetailsRow);

    orderedRows.push(existingPair.mainRow, existingPair.detailsRow);
    existingPairs.delete(detailsKey);
  });

  const activeRows = new Set(orderedRows);
  orderedRows.forEach((row) => {
    tbody.append(row);
  });
  Array.from(tbody.children).forEach((row) => {
    if (!activeRows.has(row)) {
      row.remove();
    }
  });

  if (
    openInjectedPlaylistMenuKey &&
    !playlists.some(
      (playlist, index) =>
        `${playlist.id || playlist.name || index}` === openInjectedPlaylistMenuKey,
    )
  ) {
    openInjectedPlaylistMenuKey = null;
  }
}

export function updateTrackMappingsUI(data) {
  document.getElementById("mappings-total").textContent =
    data.externalCount || 0;
  document.getElementById("mappings-external").textContent =
    data.externalCount || 0;

  const tbody = document.getElementById("mappings-table-body");

  if (data.mappings.length === 0) {
    tbody.innerHTML =
      '<tr><td colspan="6" style="text-align:center;color:var(--text-secondary);padding:40px;">No manual mappings found.</td></tr>';
    return;
  }

  const externalMappings = data.mappings.filter((m) => m.type === "external");

  if (externalMappings.length === 0) {
    tbody.innerHTML =
      '<tr><td colspan="6" style="text-align:center;color:var(--text-secondary);padding:40px;">No external mappings found.</td></tr>';
    return;
  }

  tbody.innerHTML = externalMappings
    .map((m) => {
      const typeColor = "var(--success)";
      const typeBadge = `<span style="display:inline-block;padding:2px 8px;border-radius:4px;font-size:0.8rem;background:${typeColor}20;color:${typeColor};font-weight:500;">external</span>`;
      const targets = collectExternalTargets(m);
      const targetDisplay = renderExternalTargetsHtml(targets, {
        showRemove: true,
        playlist: m.playlist,
        spotifyId: m.spotifyId,
      });
      const createdDate = m.createdAt
        ? new Date(m.createdAt).toLocaleString()
        : "-";

      return `
            <tr>
                <td><strong>${escapeHtml(m.playlist)}</strong></td>
                <td style="font-family:monospace;font-size:0.85rem;color:var(--text-secondary);">${escapeHtml(m.spotifyId)}</td>
                <td>${typeBadge}</td>
                <td>${targetDisplay}</td>
                <td style="color:var(--text-secondary);font-size:0.85rem;">${createdDate}</td>
                <td>
                    <button type="button" class="danger delete-mapping-btn" style="padding:4px 12px;font-size:0.8rem;"
                        data-playlist="${escapeHtml(m.playlist)}" data-spotify-id="${escapeHtml(m.spotifyId)}"
                        title="Remove all mappings for this track">Remove all</button>
                </td>
            </tr>
        `;
    })
    .join("");

  bindTrackMappingDeleteHandlers(tbody);
}

function bindTrackMappingDeleteHandlers(tbody) {
  tbody.querySelectorAll(".delete-mapping-provider-btn").forEach((button) => {
    button.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      const playlist = button.getAttribute("data-playlist");
      const spotifyId = button.getAttribute("data-spotify-id");
      const provider = button.getAttribute("data-provider");
      if (!playlist || !spotifyId || !provider) {
        return;
      }
      window.deleteTrackMapping?.(playlist, spotifyId, provider);
    });
  });

  tbody.querySelectorAll(".delete-mapping-btn").forEach((button) => {
    button.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      const playlist = button.getAttribute("data-playlist");
      const spotifyId = button.getAttribute("data-spotify-id");
      if (!playlist || !spotifyId) {
        return;
      }
      window.deleteTrackMapping?.(playlist, spotifyId);
    });
  });
}

export function updateDownloadsUI(data) {
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
}

export function updateConfigUI(data) {
  window.lastConfigData = data;
  if (typeof currentWizardStep !== 'undefined') {
    renderWizardStep(currentWizardStep);
  }
  document.getElementById("config-backend-type").textContent =
    data.backendType || "Jellyfin";
  document.getElementById("config-music-service").textContent =
    data.musicService || "SquidWTF";
  document.getElementById("config-storage-mode").textContent =
    data.library?.storageMode || "Cache";
  document.getElementById("config-cache-duration-hours").textContent =
    data.library?.cacheDurationHours || "24";
  document.getElementById("config-download-mode").textContent =
    data.library?.downloadMode || "Track";
  document.getElementById("config-explicit-filter").textContent =
    data.explicitFilter || "All";
  document.getElementById("config-enable-external-playlists").textContent =
    data.enableExternalPlaylists ? "Yes" : "No";
  document.getElementById("config-playlists-directory").textContent =
    data.playlistsDirectory || "(not set)";
  document.getElementById("config-redis-enabled").textContent =
    data.redisEnabled ? "Yes" : "No";
  document.getElementById("config-debug-log-requests").textContent = data.debug
    ?.logAllRequests
    ? "Enabled"
    : "Disabled";
  document.getElementById("config-admin-bind-any-ip").textContent = data.admin
    ?.bindAnyIp
    ? "Enabled"
    : "Disabled";
  document.getElementById("config-admin-trusted-subnets").textContent =
    data.admin?.trustedSubnets?.trim() || "(localhost only)";

  document.getElementById("config-spotify-enabled").textContent = data
    .spotifyApi.enabled
    ? "Yes"
    : "No";
  document.getElementById("config-spotify-cookie").textContent =
    data.spotifyApi.sessionCookie;
  document.getElementById("config-cache-duration").textContent =
    data.spotifyApi.cacheDurationMinutes + " minutes";
  document.getElementById("config-isrc-matching").textContent = data.spotifyApi
    .preferIsrcMatching
    ? "Enabled"
    : "Disabled";

  document.getElementById("config-deezer-arl").textContent =
    data.deezer.arl || "(not set)";
  document.getElementById("config-deezer-quality").textContent =
    data.deezer.quality;
  document.getElementById("config-deezer-ratelimit").textContent =
    (data.deezer.minRequestIntervalMs || 200) + " ms";
  document.getElementById("config-squid-quality").textContent =
    data.squidWtf.quality;
  document.getElementById("config-squid-ratelimit").textContent =
    (data.squidWtf.minRequestIntervalMs || 200) + " ms";
  document.getElementById("config-applemusic-baseurl").textContent =
    data.appleMusic.baseUrl || "http://gamdl-aio:8000";
  document.getElementById("config-applemusic-quality").textContent =
    data.appleMusic.quality || "alac-16-44";
  document.getElementById("config-musicbrainz-enabled").textContent = data
    .musicBrainz.enabled
    ? "Yes"
    : "No";
  document.getElementById("config-qobuz-token").textContent =
    data.qobuz.userAuthToken || "(not set)";
  document.getElementById("config-qobuz-quality").textContent =
    data.qobuz.quality || "FLAC";
  document.getElementById("config-qobuz-ratelimit").textContent =
    (data.qobuz.minRequestIntervalMs || 200) + " ms";
  document.getElementById("config-jellyfin-url").textContent =
    data.jellyfin.url || "-";
  document.getElementById("config-jellyfin-api-key").textContent =
    data.jellyfin.apiKey;
  document.getElementById("config-jellyfin-user-id").textContent =
    data.jellyfin.userId || "(not set)";
  document.getElementById("config-jellyfin-library-id").textContent =
    data.jellyfin.libraryId || "-";
  document.getElementById("config-download-path").textContent =
    data.library?.downloadPath || "./downloads";
  document.getElementById("config-kept-path").textContent =
    data.library?.keptPath || "/app/kept";
  document.getElementById("config-spotify-import-enabled").textContent = data
    .spotifyImport?.enabled
    ? "Yes"
    : "No";
  document.getElementById("config-matching-interval").textContent =
    (data.spotifyImport?.matchingIntervalHours || 24) + " hours";

  if (data.cache) {
    document.getElementById("config-cache-playlist-images").textContent =
      data.cache.playlistImagesHours || "168";
    document.getElementById("config-cache-spotify-items").textContent =
      data.cache.spotifyPlaylistItemsHours || "168";
    document.getElementById("config-cache-matched-tracks").textContent =
      data.cache.spotifyMatchedTracksDays || "30";
    document.getElementById("config-cache-lyrics").textContent =
      data.cache.lyricsDays || "14";
    document.getElementById("config-cache-genres").textContent =
      data.cache.genreDays || "30";
    document.getElementById("config-cache-metadata").textContent =
      data.cache.metadataDays || "7";
    document.getElementById("config-cache-odesli").textContent =
      data.cache.odesliLookupDays || "60";
    document.getElementById("config-cache-proxy-images").textContent =
      data.cache.proxyImagesDays || "14";
  }
}

export function updateJellyfinPlaylistsUI(data) {
  const tbody = document.getElementById("jellyfin-playlist-table-body");
  const playlists = data.playlists || [];

  if (playlists.length === 0) {
    tbody.innerHTML =
      '<tr><td colspan="4" style="text-align:center;color:var(--text-secondary);padding:40px;">No playlists found in Jellyfin</td></tr>';
    renderGuidance("jellyfin-guidance", [
      {
        tone: "info",
        title: "No Jellyfin playlists found.",
        detail: "Create playlists in Jellyfin first, then link them here.",
      },
    ]);
    return;
  }

  const unlinkedCount = playlists.filter(
    (playlist) => !playlist.isConfigured,
  ).length;
  renderGuidance(
    "jellyfin-guidance",
    unlinkedCount > 0
      ? [
          {
            tone: "warning",
            title: `${unlinkedCount} playlists are not linked to Spotify yet.`,
            detail: "Open a row, then use ... > Link to Spotify.",
          },
        ]
      : [
          {
            tone: "success",
            title: "All visible Jellyfin playlists are linked.",
            detail: "No linking action needed right now.",
          },
        ],
  );

  tbody.innerHTML = playlists
    .map((playlist, index) => {
      const detailsRowId = `jellyfin-details-${index}`;
      const menuId = `jellyfin-menu-${index}`;
      const statsPending = Boolean(playlist.statsPending);
      const localCount = playlist.localTracks || 0;
      const externalCount = playlist.externalTracks || 0;
      const externalAvailable = playlist.externalAvailable || 0;
      const escapedId = escapeHtml(playlist.id);
      const escapedName = escapeHtml(playlist.name);
      const statusClass = playlist.isConfigured ? "success" : "info";
      const statusLabel = playlist.isConfigured ? "Linked" : "Not Linked";

      const actionButtons = playlist.isConfigured
        ? `
            <button data-action="fetchJellyfinPlaylists">Refresh Row Data</button>
            <button class="danger-item" data-action="unlinkPlaylist" data-arg-jellyfin-id="${escapedId}" data-arg-jellyfin-name="${escapedName}">Unlink from Spotify</button>
        `
        : `
            <button data-action="openLinkPlaylist" data-arg-jellyfin-id="${escapedId}" data-arg-jellyfin-name="${escapedName}">Link to Spotify</button>
            <button data-action="fetchJellyfinPlaylists">Refresh Row Data</button>
        `;

      return `
            <tr class="compact-row" data-details-row="${detailsRowId}">
                <td>
                    <div class="name-cell">
                        <strong>${escapeHtml(playlist.name)}</strong>
                        <span class="meta-text subtle-mono">${escapeHtml(playlist.id || "-")}</span>
                    </div>
                </td>
                <td>
                    <span class="track-count">${statsPending ? "..." : localCount + externalAvailable}</span>
                    <div class="meta-text">${statsPending ? "Loading track stats..." : `L ${localCount} • E ${externalAvailable}/${externalCount}`}</div>
                </td>
                <td><span class="status-pill ${statusClass}">${statusLabel}</span></td>
                <td class="row-controls">
                    <button class="icon-btn details-trigger" data-details-target="${detailsRowId}" aria-expanded="false">Details</button>
                    <div class="row-actions-wrap">
                        <button class="icon-btn menu-trigger" aria-haspopup="true" aria-expanded="false"
                            data-action="toggleRowMenu" data-arg-menu-id="${menuId}">...</button>
                        <div class="row-actions-menu" id="${menuId}" role="menu">
                            ${actionButtons}
                        </div>
                    </div>
                </td>
            </tr>
            <tr id="${detailsRowId}" class="details-row" hidden>
                <td colspan="4">
                    <div class="details-panel">
                        <div class="details-grid">
                            <div class="detail-item">
                                <span class="detail-label">Local Tracks</span>
                                <span class="detail-value">${statsPending ? "..." : localCount}</span>
                            </div>
                            <div class="detail-item">
                                <span class="detail-label">External Tracks</span>
                                <span class="detail-value">${statsPending ? "Loading..." : `${externalAvailable}/${externalCount}`}</span>
                            </div>
                            <div class="detail-item">
                                <span class="detail-label">Linked Spotify ID</span>
                                <span class="detail-value mono">${escapeHtml(playlist.linkedSpotifyId || "-")}</span>
                            </div>
                        </div>
                    </div>
                </td>
            </tr>
        `;
    })
    .join("");
}

export function updateJellyfinUsersUI(data, preferredUserId = null) {
  const select = document.getElementById("jellyfin-user-select");
  if (!select) {
    return;
  }

  const normalizedPreferredUserId = preferredUserId?.trim() || "";
  select.innerHTML =
    '<option value="">All Users</option>' +
    data.users
      .map((u) => `<option value="${u.id}">${escapeHtml(u.name)}</option>`)
      .join("");

  if (normalizedPreferredUserId) {
    const matchingOption = Array.from(select.options).find(
      (option) => option.value === normalizedPreferredUserId,
    );
    if (matchingOption) {
      select.value = normalizedPreferredUserId;
      return;
    }
  }

  select.value = "";
}

export function updateEndpointUsageUI(data) {
  document.getElementById("endpoints-total-requests").textContent =
    data.totalRequests?.toLocaleString() || "0";
  document.getElementById("endpoints-unique-count").textContent =
    data.totalEndpoints?.toLocaleString() || "0";

  const mostCalled =
    data.endpoints && data.endpoints.length > 0
      ? data.endpoints[0].endpoint
      : "-";
  document.getElementById("endpoints-most-called").textContent = mostCalled;

  const tbody = document.getElementById("endpoints-table-body");

  if (!data.endpoints || data.endpoints.length === 0) {
    tbody.innerHTML =
      '<tr><td colspan="4" style="text-align:center;color:var(--text-secondary);padding:40px;">No endpoint usage data available yet.</td></tr>';
    return;
  }

  tbody.innerHTML = data.endpoints
    .map((ep, index) => {
      const percentage =
        data.totalRequests > 0
          ? ((ep.count / data.totalRequests) * 100).toFixed(1)
          : "0.0";

      let countColor = "var(--text-primary)";
      if (ep.count > 1000) countColor = "var(--error)";
      else if (ep.count > 100) countColor = "var(--warning)";
      else if (ep.count > 10) countColor = "var(--accent)";

      let endpointDisplay = ep.endpoint;
      if (ep.endpoint.includes("/stream")) {
        endpointDisplay = `<span style="color:var(--success)">${escapeHtml(ep.endpoint)}</span>`;
      } else if (ep.endpoint.includes("/Playing")) {
        endpointDisplay = `<span style="color:var(--accent)">${escapeHtml(ep.endpoint)}</span>`;
      } else if (ep.endpoint.includes("/Search")) {
        endpointDisplay = `<span style="color:var(--warning)">${escapeHtml(ep.endpoint)}</span>`;
      } else {
        endpointDisplay = escapeHtml(ep.endpoint);
      }

      return `
            <tr>
                <td style="color:var(--text-secondary);text-align:center;">${index + 1}</td>
                <td style="font-family:monospace;font-size:0.85rem;">${endpointDisplay}</td>
                <td style="text-align:right;font-weight:600;color:${countColor}">${ep.count.toLocaleString()}</td>
                <td style="text-align:right;color:var(--text-secondary)">${percentage}%</td>
            </tr>
        `;
    })
    .join("");
}

export function showErrorState(message) {
  const statusBadge = document.getElementById("spotify-status");
  if (statusBadge) {
    statusBadge.className = "status-badge error";
    statusBadge.innerHTML = '<span class="status-dot"></span>Connection Error';
  }
  const authStatus = document.getElementById("spotify-auth-status");
  if (authStatus) authStatus.textContent = "Error";
  renderGuidance("dashboard-guidance", [
    {
      tone: "warning",
      title: "Unable to load dashboard status.",
      detail: "Check connectivity and refresh the page.",
    },
  ]);
}

export function showPlaylistRebuildingIndicator(playlistName) {
  const playlistCards = document.querySelectorAll(".playlist-card");
  for (const card of playlistCards) {
    const nameEl = card.querySelector("h3");
    if (nameEl && nameEl.textContent.trim() === playlistName) {
      const existingIndicator = card.querySelector(".rebuilding-indicator");
      if (!existingIndicator) {
        const indicator = document.createElement("div");
        indicator.className = "rebuilding-indicator";
        indicator.style.cssText = `
                    position: absolute;
                    top: 8px;
                    right: 8px;
                    background: var(--warning);
                    color: white;
                    padding: 4px 8px;
                    border-radius: 12px;
                    font-size: 0.7rem;
                    font-weight: 500;
                    display: flex;
                    align-items: center;
                    gap: 4px;
                    z-index: 10;
                `;
        indicator.innerHTML =
          '<span class="spinner" style="width: 10px; height: 10px;"></span>Rebuilding...';
        card.style.position = "relative";
        card.appendChild(indicator);

        setTimeout(() => {
          indicator.remove();
        }, 30000);
      }
      break;
    }
  }
}

// --- Apple Music Sidecar Manager Logic ---

// Poll status of Apple Music container
async function pollAppleMusicStatus() {
  const card = document.getElementById("applemusic-manager-card");
  if (!card) return;

  // Only poll if AppleMusic is the active provider or if we are looking at configuration
  const currentTab = document.querySelector(".sidebar-link.active")?.getAttribute("data-tab");
  if (currentTab !== "config") return;

  try {
    const res = await fetch("/api/admin/applemusic/status");
    if (!res.ok) {
      throw new Error("Sidecar returned error status");
    }
    const data = await res.json();

    updateAmIndicator("am-staged-status", data.staged);
    updateAmIndicator("am-daemon-status", data.daemon_running);
    updateAmIndicator("am-auth-status", data.logged_in);

    // Conditionally show setup, login, 2fa forms
    if (!data.staged) {
      document.getElementById("am-upload-section").style.display = "block";
      document.getElementById("am-login-section").style.display = "none";
      document.getElementById("am-tfa-section").style.display = "none";
    } else if (!data.logged_in) {
      document.getElementById("am-upload-section").style.display = "none";
      if (document.getElementById("am-tfa-section").style.display !== "block") {
        document.getElementById("am-login-section").style.display = "block";
      }
    } else {
      document.getElementById("am-upload-section").style.display = "none";
      document.getElementById("am-login-section").style.display = "none";
      document.getElementById("am-tfa-section").style.display = "none";
    }
  } catch (error) {
    updateAmIndicator("am-staged-status", false);
    updateAmIndicator("am-daemon-status", false);
    updateAmIndicator("am-auth-status", false);
    document.getElementById("am-upload-section").style.display = "none";
    document.getElementById("am-login-section").style.display = "none";
    document.getElementById("am-tfa-section").style.display = "none";
  }
}

function updateAmIndicator(id, active) {
  const el = document.getElementById(id);
  if (!el) return;
  if (active) {
    el.innerHTML = '<span class="status-indicator green"></span> Active / Yes';
  } else {
    el.innerHTML = '<span class="status-indicator red"></span> Offline / No';
  }
}

// Upload APK
document.addEventListener("DOMContentLoaded", () => {
  const apkInput = document.getElementById("am-apk-input");
  if (apkInput) {
    apkInput.addEventListener("change", (e) => {
      if (e.target.files.length > 0) {
        uploadAppleMusicApk(e.target.files[0]);
      }
    });
  }
  
  // Start polling status
  setInterval(pollAppleMusicStatus, 3500);
  
  // Also poll once immediately after tab load or startup
  setTimeout(pollAppleMusicStatus, 1000);
});

async function uploadAppleMusicApk(file) {
  const progressContainer = document.getElementById("am-upload-progress-container");
  const progressBar = document.getElementById("am-upload-progress-bar");
  const progressText = document.getElementById("am-upload-progress-text");
  
  progressContainer.style.display = "block";
  progressBar.style.width = "0%";
  progressText.textContent = "Uploading... 0%";

  const xhr = new XMLHttpRequest();
  const formData = new FormData();
  formData.append("file", file);

  xhr.open("POST", "/api/admin/applemusic/setup", true);

  xhr.upload.addEventListener("progress", (e) => {
    if (e.lengthComputable) {
      const percentComplete = Math.round((e.loaded / e.total) * 100);
      progressBar.style.width = percentComplete + "%";
      progressText.textContent = `Uploading... ${percentComplete}%`;
    }
  });

  xhr.onload = function() {
    if (xhr.status === 200) {
      progressText.textContent = "Staging libraries on sidecar... Please wait";
      setTimeout(() => {
        progressContainer.style.display = "none";
        pollAppleMusicStatus();
      }, 5000);
    } else {
      progressContainer.style.display = "none";
      alert("Staging failed: " + xhr.responseText);
    }
  };

  xhr.onerror = function() {
    progressContainer.style.display = "none";
    alert("Connection error occurred during upload.");
  };

  xhr.send(formData);
}

// Login
async function submitAppleMusicLogin() {
  const username = document.getElementById("am-username-input").value.trim();
  const password = document.getElementById("am-password-input").value;
  
  if (!username || !password) {
    alert("Please enter both Apple ID and password.");
    return;
  }

  try {
    const res = await fetch("/api/admin/applemusic/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password })
    });
    
    if (res.status === 200) {
      alert("Login successful!");
      pollAppleMusicStatus();
    } else if (res.status === 202) {
      // 2FA required
      document.getElementById("am-login-section").style.display = "none";
      document.getElementById("am-tfa-section").style.display = "block";
    } else {
      const text = await res.text();
      alert("Login failed: " + text);
    }
  } catch (err) {
    alert("Error logging in: " + err.message);
  }
}

    } else {
      const text = await res.text();
      alert("Verification failed: " + text);
    }
  } catch (err) {
    alert("Error verifying code: " + err.message);
  }
}

// --- Interactive Setup Wizard Logic ---

let currentWizardStep = 1;

function initSetupWizard() {
  const container = document.getElementById("wizard-step-container");
  if (!container) return;
  
  // Render step 1 immediately
  renderWizardStep(1);
}

function renderWizardStep(step) {
  currentWizardStep = step;
  const container = document.getElementById("wizard-step-container");
  if (!container) return;

  // Update Stepper indicators
  const steps = document.querySelectorAll(".setup-step");
  steps.forEach((el, index) => {
    const stepNum = index + 1;
    el.classList.remove("active", "completed");
    if (stepNum === step) {
      el.classList.add("active");
    } else if (stepNum < step) {
      el.classList.add("completed");
    }
  });

  // Update Stepper Line Progress Bar
  const progressBar = document.getElementById("wizard-progress-bar");
  if (progressBar) {
    progressBar.style.width = ((step - 1) / 3) * 100 + "%";
  }

  // Update actions buttons visibility/text
  const backBtn = document.getElementById("wizard-back-btn");
  const skipBtn = document.getElementById("wizard-skip-btn");
  const nextBtn = document.getElementById("wizard-next-btn");

  if (backBtn) {
    backBtn.style.visibility = step === 1 ? "hidden" : "visible";
  }
  if (skipBtn) {
    skipBtn.style.display = step === 4 ? "none" : "inline-block";
  }
  if (nextBtn) {
    nextBtn.textContent = step === 4 ? "Restart Allstarr" : "Next Step →";
    nextBtn.className = step === 4 ? "primary success" : "primary";
    if (step === 4) {
      nextBtn.onclick = completeWizardSetup;
    } else {
      nextBtn.onclick = nextWizardStep;
    }
  }

  // Inject Step Contents HTML
  const config = window.lastConfigData || {};

  if (step === 1) {
    container.innerHTML = `
      <h3>🔌 Step 1: Connect your media server backend</h3>
      <p class="step-desc">Allstarr syncs local playlists and streams audio files through your server. Let's link it now.</p>
      
      <div class="form-group mb-12">
          <label class="text-secondary" style="display:block; margin-bottom:6px;">Backend Type</label>
          <select id="wizard-backend-type" onchange="toggleWizardBackendFields()" style="width:100%; padding:10px; background:var(--bg-secondary); border:1px solid var(--border); border-radius:6px; color:var(--text-primary);">
              <option value="Jellyfin" ${config.backendType === "Jellyfin" ? "selected" : ""}>Jellyfin (Recommended)</option>
              <option value="Subsonic" ${config.backendType === "Subsonic" ? "selected" : ""}>Subsonic (Navidrome, Gonic, Airsonic)</option>
          </select>
      </div>
      
      <div class="form-group mb-12">
          <label class="text-secondary" style="display:block; margin-bottom:6px;">Server URL</label>
          <input type="text" id="wizard-backend-url" value="${config.backendType === "Subsonic" ? (config.subsonic?.url || "") : (config.jellyfin?.url || "")}" placeholder="e.g. http://192.168.1.100:8096" style="width:100%; padding:10px; background:var(--bg-secondary); border:1px solid var(--border); border-radius:6px; color:var(--text-primary);">
      </div>

      <div id="wizard-jellyfin-fields" style="display: ${config.backendType === "Subsonic" ? "none" : "block"};">
          <div class="form-group mb-12">
              <label class="text-secondary" style="display:block; margin-bottom:6px;">API Key</label>
              <input type="password" id="wizard-jellyfin-api-key" value="${config.jellyfin?.apiKey || ""}" placeholder="Enter Jellyfin API key..." style="width:100%; padding:10px; background:var(--bg-secondary); border:1px solid var(--border); border-radius:6px; color:var(--text-primary);">
          </div>
          <div class="form-group mb-12">
              <label class="text-secondary" style="display:block; margin-bottom:6px;">User ID (optional)</label>
              <input type="text" id="wizard-jellyfin-user-id" value="${config.jellyfin?.userId || ""}" placeholder="Optional. Required to sync playlists." style="width:100%; padding:10px; background:var(--bg-secondary); border:1px solid var(--border); border-radius:6px; color:var(--text-primary);">
          </div>
      </div>

      <div id="wizard-subsonic-fields" style="display: ${config.backendType === "Subsonic" ? "block" : "none"};">
          <p class="text-secondary" style="font-size:0.85rem; margin-top:8px;">Subsonic auth handles token generation automatically from settings after save.</p>
      </div>
    `;
  } else if (step === 2) {
    container.innerHTML = `
      <h3>🎵 Step 2: Choose and authorize your music provider</h3>
      <p class="step-desc">Allstarr uses a streaming provider to search and fetch decryptable audio files.</p>
      
      <div class="form-group mb-16">
          <label class="text-secondary" style="display:block; margin-bottom:6px;">Music Service Provider</label>
          <select id="wizard-music-service" onchange="toggleWizardServiceFields()" style="width:100%; padding:10px; background:var(--bg-secondary); border:1px solid var(--border); border-radius:6px; color:var(--text-primary);">
              <option value="SquidWTF" ${config.musicService === "SquidWTF" ? "selected" : ""}>SquidWTF (Tidal)</option>
              <option value="Deezer" ${config.musicService === "Deezer" ? "selected" : ""}>Deezer</option>
              <option value="Qobuz" ${config.musicService === "Qobuz" ? "selected" : ""}>Qobuz</option>
              <option value="AppleMusic" ${config.musicService === "AppleMusic" ? "selected" : ""}>Apple Music (gamdl-aio sidecar)</option>
          </select>
      </div>

      <!-- Deezer Form -->
      <div id="wizard-deezer-fields" style="display: ${config.musicService === "Deezer" ? "block" : "none"};">
          <div class="form-group mb-12">
              <label class="text-secondary" style="display:block; margin-bottom:6px;">ARL Token</label>
              <input type="password" id="wizard-deezer-arl" placeholder="Paste your Deezer ARL cookie token..." style="width:100%; padding:10px; background:var(--bg-secondary); border:1px solid var(--border); border-radius:6px; color:var(--text-primary);">
          </div>
      </div>

      <!-- Qobuz Form -->
      <div id="wizard-qobuz-fields" style="display: ${config.musicService === "Qobuz" ? "block" : "none"};">
          <div class="form-group mb-12">
              <label class="text-secondary" style="display:block; margin-bottom:6px;">Qobuz Auth Token</label>
              <input type="password" id="wizard-qobuz-token" value="${config.qobuz?.userAuthToken || ""}" placeholder="Paste Qobuz Auth Token..." style="width:100%; padding:10px; background:var(--bg-secondary); border:1px solid var(--border); border-radius:6px; color:var(--text-primary);">
          </div>
      </div>

      <!-- Apple Music Form -->
      <div id="wizard-applemusic-fields" style="display: ${config.musicService === "AppleMusic" ? "block" : "none"};">
          <div style="background: rgba(59, 130, 246, 0.08); border: 1px solid var(--border); border-radius: 8px; padding: 16px; margin-bottom: 16px;">
              <h4 style="margin-bottom: 8px;">gamdl-aio Sidecar Setup</h4>
              
              <div class="config-section" style="margin-bottom:12px; display:grid; gap:8px;">
                  <div class="config-item" style="grid-template-columns: 200px 1fr; border:none; padding:0;">
                      <span class="label">Native Libs Staged:</span>
                      <span class="value" id="wizard-am-staged"><span class="status-indicator red"></span> Offline</span>
                  </div>
                  <div class="config-item" style="grid-template-columns: 200px 1fr; border:none; padding:0;">
                      <span class="label">Subscription Authorized:</span>
                      <span class="value" id="wizard-am-auth"><span class="status-indicator red"></span> Offline</span>
                  </div>
              </div>

              <!-- Stage APK -->
              <div id="wizard-am-apk-section" style="display:none; border-top: 1px solid var(--border); padding-top:12px; margin-top:12px;">
                  <p class="text-secondary" style="font-size:0.85rem; margin-bottom:8px;">
                      Staging native FairPlay binaries required. Browse and upload a compatible client APK/APKM (version 3.6.0-beta, build 1109):
                  </p>
                  <input type="file" id="wizard-am-apk-input" accept=".apk,.apkm" style="display:none;">
                  <button type="button" class="primary" onclick="document.getElementById('wizard-am-apk-input').click()">Browse & Upload APK/APKM</button>
                  <div id="wizard-am-progress-container" style="display:none; margin-top:8px;">
                      <div style="background: var(--bg-secondary); border-radius:4px; height:8px; overflow:hidden; position:relative; margin-bottom:4px;">
                          <div id="wizard-am-progress-bar" style="background: var(--accent); width:0%; height:100%;"></div>
                      </div>
                      <span id="wizard-am-progress-text" style="font-size:0.75rem;" class="text-secondary">0%</span>
                  </div>
              </div>

              <!-- Login credentials -->
              <div id="wizard-am-login-section" style="display:none; border-top: 1px solid var(--border); padding-top:12px; margin-top:12px;">
                  <p class="text-secondary" style="font-size:0.85rem; margin-bottom:8px;">Enter Apple Music credentials to login:</p>
                  <div class="form-group mb-8">
                      <input type="email" id="wizard-am-user" placeholder="Apple ID Email" style="width:100%; padding:8px; background:var(--bg-secondary); border:1px solid var(--border); border-radius:6px; color:var(--text-primary);">
                  </div>
                  <div class="form-group mb-12">
                      <input type="password" id="wizard-am-pass" placeholder="Password" style="width:100%; padding:8px; background:var(--bg-secondary); border:1px solid var(--border); border-radius:6px; color:var(--text-primary);">
                  </div>
                  <button type="button" class="primary" onclick="submitWizardAmLogin()">Authorize Login</button>
              </div>

              <!-- 2FA -->
              <div id="wizard-am-tfa-section" style="display:none; border-top: 1px solid var(--border); padding-top:12px; margin-top:12px;">
                  <p class="text-secondary" style="font-size:0.85rem; margin-bottom:8px;">Enter the 6-digit verification code sent to your devices:</p>
                  <input type="text" id="wizard-am-tfa" placeholder="123456" maxlength="6" style="width:100%; padding:8px; margin-bottom:12px; background:var(--bg-secondary); border:1px solid var(--border); border-radius:6px; color:var(--text-primary); text-align:center; font-size:1.2rem; letter-spacing:4px;">
                  <button type="button" class="primary" onclick="submitWizardAmTfa()">Verify Code</button>
              </div>
          </div>
      </div>
    `;

    // Start polling status if Apple Music is active
    if (document.getElementById("wizard-music-service").value === "AppleMusic") {
      pollWizardAmStatus();
      setupWizardApkListener();
    }
  } else if (step === 3) {
    container.innerHTML = `
      <h3>🔑 Step 3: Link Spotify API (Recommended)</h3>
      <p class="step-desc">Allows Allstarr to import active Spotify playlists, map ISRCs, and fetch lyrics on the fly.</p>
      
      <div class="form-group mb-16" style="display:flex; align-items:center; gap:10px;">
          <input type="checkbox" id="wizard-spotify-enabled" ${config.spotifyApi?.enabled ? "checked" : ""} onchange="toggleWizardSpotifyFields()" style="width:20px; height:20px; cursor:pointer;">
          <label for="wizard-spotify-enabled" style="font-weight:600; cursor:pointer;">Enable Spotify Playlist Sync & Matching</label>
      </div>

      <div id="wizard-spotify-fields" style="display: ${config.spotifyApi?.enabled ? "block" : "none"};">
          <div class="form-group mb-12">
              <label class="text-secondary" style="display:block; margin-bottom:6px;">Spotify Session Cookie (<code>sp_dc</code>)</label>
              <input type="password" id="wizard-spotify-cookie" value="${config.spotifyApi?.sessionCookie || ""}" placeholder="Paste your Spotify sp_dc session cookie..." style="width:100%; padding:10px; background:var(--bg-secondary); border:1px solid var(--border); border-radius:6px; color:var(--text-primary);">
              <small class="text-secondary" style="margin-top:4px; display:block; line-height:1.4;">
                  Get this from browser dev tools (Application tab -> Cookies) while logged into spotify.com. Key typically lasts ~1 year.
              </small>
          </div>
      </div>
    `;
  } else if (step === 4) {
    const backend = config.backendType || "Jellyfin";
    const service = config.musicService || "SquidWTF";
    const spotifyActive = config.spotifyApi?.enabled;

    container.innerHTML = `
      <h3>🎉 Guided Setup Complete!</h3>
      <p class="step-desc">Your basic configuration is ready. Apply and restart the service to initialize all components.</p>
      
      <div style="background: rgba(16, 185, 129, 0.08); border: 1px solid var(--success); border-radius: 8px; padding: 16px; margin-bottom: 24px;">
          <h4 style="color:var(--success); margin-bottom:12px; font-weight:600;">Configuration Checklist:</h4>
          <ul style="list-style:none; padding:0; display:grid; gap:8px;">
              <li>✔️ Backend server set to <strong>${backend}</strong></li>
              <li>✔️ Music downloader set to <strong>${service}</strong></li>
              <li>${spotifyActive ? "✔️ Spotify integration configured" : "❌ Spotify integration skipped (optional)"}</li>
          </ul>
      </div>
      <p class="text-secondary" style="font-size:0.9rem; margin-bottom:12px;">Clicking "Restart Allstarr" will write all configuration properties to your environment and reboot the proxy server.</p>
    `;
  }
}

function toggleWizardBackendFields() {
  const type = document.getElementById("wizard-backend-type").value;
  document.getElementById("wizard-jellyfin-fields").style.display = type === "Jellyfin" ? "block" : "none";
  document.getElementById("wizard-subsonic-fields").style.display = type === "Subsonic" ? "block" : "none";
}

function toggleWizardServiceFields() {
  const val = document.getElementById("wizard-music-service").value;
  document.getElementById("wizard-deezer-fields").style.display = val === "Deezer" ? "block" : "none";
  document.getElementById("wizard-qobuz-fields").style.display = val === "Qobuz" ? "block" : "none";
  document.getElementById("wizard-applemusic-fields").style.display = val === "AppleMusic" ? "block" : "none";

  if (val === "AppleMusic") {
    pollWizardAmStatus();
    setupWizardApkListener();
  }
}

function toggleWizardSpotifyFields() {
  const chk = document.getElementById("wizard-spotify-enabled").checked;
  document.getElementById("wizard-spotify-fields").style.display = chk ? "block" : "none";
}

// Staging Listeners
function setupWizardApkListener() {
  setTimeout(() => {
    const input = document.getElementById("wizard-am-apk-input");
    if (input && !input.dataset.hasListener) {
      input.dataset.hasListener = "true";
      input.addEventListener("change", (e) => {
        if (e.target.files.length > 0) {
          uploadWizardAmApk(e.target.files[0]);
        }
      });
    }
  }, 100);
}

// Upload Wizard APK
async function uploadWizardAmApk(file) {
  const progressContainer = document.getElementById("wizard-am-progress-container");
  const progressBar = document.getElementById("wizard-am-progress-bar");
  const progressText = document.getElementById("wizard-am-progress-text");
  
  if (!progressContainer) return;
  progressContainer.style.display = "block";
  progressBar.style.width = "0%";
  progressText.textContent = "Uploading... 0%";

  const xhr = new XMLHttpRequest();
  const formData = new FormData();
  formData.append("file", file);

  xhr.open("POST", "/api/admin/applemusic/setup", true);

  xhr.upload.addEventListener("progress", (e) => {
    if (e.lengthComputable) {
      const percentComplete = Math.round((e.loaded / e.total) * 100);
      progressBar.style.width = percentComplete + "%";
      progressText.textContent = `Uploading... ${percentComplete}%`;
    }
  });

  xhr.onload = function() {
    if (xhr.status === 200) {
      progressText.textContent = "Staging libraries on sidecar... Please wait";
      setTimeout(() => {
        progressContainer.style.display = "none";
        pollWizardAmStatus();
      }, 5000);
    } else {
      progressContainer.style.display = "none";
      alert("Staging failed: " + xhr.responseText);
    }
  };

  xhr.send(formData);
}

// Poll Apple Music status in Wizard
async function pollWizardAmStatus() {
  if (currentWizardStep !== 2) return;
  const service = document.getElementById("wizard-music-service")?.value;
  if (service !== "AppleMusic") return;

  try {
    const res = await fetch("/api/admin/applemusic/status");
    if (!res.ok) return;
    const data = await res.json();

    const stagedVal = document.getElementById("wizard-am-staged");
    const authVal = document.getElementById("wizard-am-auth");

    if (stagedVal) {
      stagedVal.innerHTML = data.staged 
        ? '<span class="status-indicator green"></span> Active / Yes'
        : '<span class="status-indicator red"></span> Offline / No';
    }
    if (authVal) {
      authVal.innerHTML = data.logged_in
        ? '<span class="status-indicator green"></span> Active / Yes'
        : '<span class="status-indicator red"></span> Offline / No';
    }

    // Sections visibility
    const apkSec = document.getElementById("wizard-am-apk-section");
    const loginSec = document.getElementById("wizard-am-login-section");
    const tfaSec = document.getElementById("wizard-am-tfa-section");

    if (!data.staged) {
      if (apkSec) apkSec.style.display = "block";
      if (loginSec) loginSec.style.display = "none";
      if (tfaSec) tfaSec.style.display = "none";
    } else if (!data.logged_in) {
      if (apkSec) apkSec.style.display = "none";
      if (tfaSec && tfaSec.style.display !== "block") {
        if (loginSec) loginSec.style.display = "block";
      }
    } else {
      if (apkSec) apkSec.style.display = "none";
      if (loginSec) loginSec.style.display = "none";
      if (tfaSec) tfaSec.style.display = "none";
    }
  } catch (e) {
    // Silent fail
  }
}

// Authorize Login in Wizard
async function submitWizardAmLogin() {
  const username = document.getElementById("wizard-am-user").value.trim();
  const password = document.getElementById("wizard-am-pass").value;
  if (!username || !password) {
    alert("Please enter both Apple ID email and password.");
    return;
  }

  try {
    const res = await fetch("/api/admin/applemusic/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password })
    });

    if (res.status === 200) {
      alert("Apple Music Login Successful!");
      pollWizardAmStatus();
    } else if (res.status === 202) {
      document.getElementById("wizard-am-login-section").style.display = "none";
      document.getElementById("wizard-am-tfa-section").style.display = "block";
    } else {
      const text = await res.text();
      alert("Login failed: " + text);
    }
  } catch (err) {
    alert("Error logging in: " + err.message);
  }
}

// 2FA in Wizard
async function submitWizardAmTfa() {
  const code = document.getElementById("wizard-am-tfa").value.trim();
  if (!code || code.length !== 6) {
    alert("Please enter the 6-digit code.");
    return;
  }

  try {
    const res = await fetch("/api/admin/applemusic/login/2fa", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ code })
    });

    if (res.ok) {
      alert("2FA Verification Successful! Logged In.");
      pollWizardAmStatus();
    } else {
      const text = await res.text();
      alert("Verification failed: " + text);
    }
  } catch (e) {
    alert("Error verifying code: " + e.message);
  }
}

// Save config on step navigation
async function saveWizardStepData(step) {
  const updates = {};

  if (step === 1) {
    const type = document.getElementById("wizard-backend-type").value;
    const url = document.getElementById("wizard-backend-url").value.trim();
    updates["BACKEND_TYPE"] = type;
    if (type === "Jellyfin") {
      updates["JELLYFIN_URL"] = url;
      updates["JELLYFIN_API_KEY"] = document.getElementById("wizard-jellyfin-api-key").value.trim();
      updates["JELLYFIN_USER_ID"] = document.getElementById("wizard-jellyfin-user-id").value.trim();
    } else {
      updates["SUBSONIC_URL"] = url;
    }
  } else if (step === 2) {
    const service = document.getElementById("wizard-music-service").value;
    updates["MUSIC_SERVICE"] = service;
    if (service === "Deezer") {
      updates["DEEZER_ARL"] = document.getElementById("wizard-deezer-arl").value.trim();
    } else if (service === "Qobuz") {
      updates["QOBUZ_USER_AUTH_TOKEN"] = document.getElementById("wizard-qobuz-token").value.trim();
    }
  } else if (step === 3) {
    const spotifyEnabled = document.getElementById("wizard-spotify-enabled").checked;
    updates["SPOTIFY_API_ENABLED"] = spotifyEnabled ? "true" : "false";
    if (spotifyEnabled) {
      updates["SPOTIFY_API_SESSION_COOKIE"] = document.getElementById("wizard-spotify-cookie").value.trim();
    }
  }

  try {
    const res = await fetch("/api/admin/config", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ updates })
    });
    if (!res.ok) {
      throw new Error("Failed to save step settings");
    }
    // Update local lastConfigData properties
    if (!window.lastConfigData) window.lastConfigData = {};
    Object.assign(window.lastConfigData, updates);
  } catch (e) {
    console.error(e);
    alert("Warning: Failed to auto-save step configuration to server: " + e.message);
  }
}

// Navigation Triggers
async function nextWizardStep() {
  await saveWizardStepData(currentWizardStep);
  if (currentWizardStep < 4) {
    renderWizardStep(currentWizardStep + 1);
  }
}

function prevWizardStep() {
  if (currentWizardStep > 1) {
    renderWizardStep(currentWizardStep - 1);
  }
}

function skipWizardStep() {
  if (currentWizardStep < 4) {
    renderWizardStep(currentWizardStep + 1);
  }
}

function jumpToWizardStep(step) {
  if (step >= 1 && step <= 4) {
    renderWizardStep(step);
  }
}

// Final Save & Restart
async function completeWizardSetup() {
  if (confirm("Configuration properties saved. Restart Allstarr container now?")) {
    try {
      const res = await fetch("/api/admin/config/restart", { method: "POST" });
      if (res.ok) {
        alert("Server is restarting. Please wait 10-15 seconds and refresh this page.");
      } else {
        alert("Restart command sent. Please refresh the page in a few moments.");
      }
    } catch (e) {
      alert("Restart requested. Reconnecting shortly.");
    }
  }
}

// Expose functions globally for HTML triggers
window.initSetupWizard = initSetupWizard;
window.renderWizardStep = renderWizardStep;
window.toggleWizardBackendFields = toggleWizardBackendFields;
window.toggleWizardServiceFields = toggleWizardServiceFields;
window.toggleWizardSpotifyFields = toggleWizardSpotifyFields;
window.nextWizardStep = nextWizardStep;
window.prevWizardStep = prevWizardStep;
window.skipWizardStep = skipWizardStep;
window.jumpToWizardStep = jumpToWizardStep;
window.pollWizardAmStatus = pollWizardAmStatus;
window.submitWizardAmLogin = submitWizardAmLogin;
window.submitWizardAmTfa = submitWizardAmTfa;
window.completeWizardSetup = completeWizardSetup;

// Upload APK
document.addEventListener("DOMContentLoaded", () => {
  const apkInput = document.getElementById("am-apk-input");
  if (apkInput) {
    apkInput.addEventListener("change", (e) => {
      if (e.target.files.length > 0) {
        uploadAppleMusicApk(e.target.files[0]);
      }
    });
  }
  
  // Start polling status
  setInterval(pollAppleMusicStatus, 3500);
  
  // Also poll once immediately after tab load or startup
  setTimeout(pollAppleMusicStatus, 1000);

  // Initialize Guided Setup Wizard
  setTimeout(() => {
    initSetupWizard();
  }, 500);
});
