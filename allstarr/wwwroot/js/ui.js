// UI updates and DOM manipulation

import { escapeHtml, escapeJs, capitalizeProvider } from "./utils.js";

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
      const targetDisplay = `<span style="font-family:monospace;font-size:0.85rem;color:var(--success);">${m.externalProvider}/${m.externalId}</span>`;
      const createdDate = m.createdAt
        ? new Date(m.createdAt).toLocaleString()
        : "-";

      return `
            <tr>
                <td><strong>${escapeHtml(m.playlist)}</strong></td>
                <td style="font-family:monospace;font-size:0.85rem;color:var(--text-secondary);">${m.spotifyId}</td>
                <td>${typeBadge}</td>
                <td>${targetDisplay}</td>
                <td style="color:var(--text-secondary);font-size:0.85rem;">${createdDate}</td>
                <td>
                    <button class="danger delete-mapping-btn" style="padding:4px 12px;font-size:0.8rem;" data-playlist="${escapeHtml(m.playlist)}" data-spotify-id="${m.spotifyId}">Remove</button>
                </td>
            </tr>
        `;
    })
    .join("");
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
