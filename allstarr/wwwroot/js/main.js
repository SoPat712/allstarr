import {
  escapeHtml,
  escapeJs,
  showToast,
  capitalizeProvider,
} from "./utils.js";
import * as API from "./api.js";
import { openModal, closeModal, setupModalBackdropClose } from "./modals.js";
import {
  viewTracks,
  openManualMap,
  openExternalMap,
  searchJellyfinTracks,
  selectJellyfinTrack,
  saveLocalMapping,
  saveManualMapping,
  searchExternalTracks,
  selectExternalTrack,
  validateExternalMapping,
  openLyricsMap,
  saveLyricsMapping,
  searchProvider,
} from "./helpers.js";
import {
  initSettingsEditor,
  setCurrentConfigState,
  syncConfigUiExtras,
} from "./settings-editor.js";
import { initDashboardData } from "./dashboard-data.js";
import { initOperations } from "./operations.js";
import {
  initPlaylistAdmin,
  resetPlaylistAdminState,
} from "./playlist-admin.js";
import { initScrobblingAdmin } from "./scrobbling-admin.js";
import { initAuthSession } from "./auth-session.js";
import { initActionDispatcher } from "./action-dispatcher.js";
import { initNavigationView } from "./views/navigation-view.js";
import { initScrobblingView } from "./views/scrobbling-view.js";

let cookieDateInitialized = false;
let restartRequired = false;

window.showRestartBanner = function () {
  restartRequired = true;
  document.getElementById("restart-banner")?.classList.add("active");
};

window.dismissRestartBanner = function () {
  document.getElementById("restart-banner")?.classList.remove("active");
};

window.switchTab = function (tabName) {
  document
    .querySelectorAll(".tab")
    .forEach((tab) => tab.classList.remove("active"));
  document
    .querySelectorAll(".sidebar-link")
    .forEach((link) => link.classList.remove("active"));
  document
    .querySelectorAll(".tab-content")
    .forEach((content) => content.classList.remove("active"));

  const tab = document.querySelector(`.tab[data-tab="${tabName}"]`);
  const sidebarLink = document.querySelector(
    `.sidebar-link[data-tab="${tabName}"]`,
  );
  const content = document.getElementById(`tab-${tabName}`);

  if (tab && content) {
    tab.classList.add("active");
    if (sidebarLink) {
      sidebarLink.classList.add("active");
    }
    content.classList.add("active");
    window.location.hash = tabName;

    if (tabName === "kept" && typeof window.fetchDownloads === "function") {
      window.fetchDownloads();
    }
  }
};

async function initCookieDate() {
  if (cookieDateInitialized) {
    console.log("Cookie date already initialized, skipping");
    return;
  }

  cookieDateInitialized = true;

  try {
    await API.initCookieDate();
    console.log(
      "Cookie date initialized successfully - restart container to apply",
    );
    showToast(
      "Cookie date set. Restart container to apply changes.",
      "success",
    );
  } catch (error) {
    console.error("Failed to init cookie date:", error);
    cookieDateInitialized = false;
  }
}

initSettingsEditor({
  fetchConfig: async () => window.fetchConfig?.(),
  fetchStatus: async () => window.fetchStatus?.(),
  showRestartBanner: window.showRestartBanner,
});

initScrobblingAdmin({
  showRestartBanner: window.showRestartBanner,
});

const dashboard = initDashboardData({
  isAuthenticated: () => authSession?.isAuthenticated() ?? false,
  isAdminSession: () => authSession?.isAdminSession() ?? false,
  getCurrentUserId: () => authSession?.getCurrentUserId?.() ?? null,
  onCookieNeedsInit: initCookieDate,
  setCurrentConfigState,
  syncConfigUiExtras,
  loadScrobblingConfig: () => window.loadScrobblingConfig?.(),
});

initOperations({
  fetchPlaylists: dashboard.fetchPlaylists,
  fetchTrackMappings: dashboard.fetchTrackMappings,
  fetchDownloads: dashboard.fetchDownloads,
});

initPlaylistAdmin({
  isAdminSession: () => authSession?.isAdminSession() ?? false,
  showRestartBanner: window.showRestartBanner,
  fetchPlaylists: dashboard.fetchPlaylists,
  fetchJellyfinPlaylists: dashboard.fetchJellyfinPlaylists,
});

const authSession = initAuthSession({
  stopDashboardRefresh: dashboard.stopDashboardRefresh,
  loadDashboardData: dashboard.loadDashboardData,
  switchTab: window.switchTab,
  onUnauthenticated: () => {
    resetPlaylistAdminState();
    setCurrentConfigState(null);
  },
});

window.openManualMap = openManualMap;
window.openExternalMap = openExternalMap;
window.openMapToLocal = openManualMap;
window.openMapToExternal = openExternalMap;
window.openModal = openModal;
window.closeModal = closeModal;
window.searchJellyfinTracks = searchJellyfinTracks;
window.saveLocalMapping = saveLocalMapping;
window.saveManualMapping = saveManualMapping;
window.searchExternalTracks = searchExternalTracks;
window.validateExternalMapping = validateExternalMapping;
window.saveLyricsMapping = saveLyricsMapping;
// Note: viewTracks/selectExternalTrack/selectJellyfinTrack/openLyricsMap/searchProvider
// are now wired via the ActionDispatcher and no longer require window exports.

document.addEventListener("DOMContentLoaded", () => {
  console.log("🚀 Allstarr Admin UI (Modular) loaded");

  const dispatcher = initActionDispatcher({ root: document });
  // Register a few core actions first; more will be migrated as inline
  // onclick handlers are removed from HTML and generated markup.
  dispatcher.register("switchTab", ({ args }) => {
    const tab = args?.tab || args?.tabName;
    if (tab) {
      window.switchTab(tab);
    }
  });
  dispatcher.register("logoutAdminSession", () => window.logoutAdminSession?.());
  dispatcher.register("dismissRestartBanner", () =>
    window.dismissRestartBanner?.(),
  );
  dispatcher.register("restartContainer", () => window.restartContainer?.());
  dispatcher.register("refreshPlaylists", () => window.refreshPlaylists?.());
  dispatcher.register("clearCache", () => window.clearCache?.());
  dispatcher.register("openAddPlaylist", () => window.openAddPlaylist?.());
  dispatcher.register("toggleRowMenu", ({ event, args }) =>
    window.toggleRowMenu?.(event, args?.menuId),
  );
  dispatcher.register("toggleDetailsRow", ({ event, args }) =>
    window.toggleDetailsRow?.(event, args?.detailsRowId),
  );
  dispatcher.register("viewTracks", ({ args }) => viewTracks(args?.playlistName));
  dispatcher.register("refreshPlaylist", ({ args }) =>
    window.refreshPlaylist?.(args?.playlistName),
  );
  dispatcher.register("matchPlaylistTracks", ({ args }) =>
    window.matchPlaylistTracks?.(args?.playlistName),
  );
  dispatcher.register("clearPlaylistCache", ({ args }) =>
    window.clearPlaylistCache?.(args?.playlistName),
  );
  dispatcher.register("editPlaylistSchedule", ({ args }) =>
    window.editPlaylistSchedule?.(args?.playlistName, args?.syncSchedule),
  );
  dispatcher.register("removePlaylist", ({ args }) =>
    window.removePlaylist?.(args?.playlistName),
  );
  dispatcher.register("openLinkPlaylist", ({ args }) =>
    window.openLinkPlaylist?.(args?.jellyfinId, args?.jellyfinName),
  );
  dispatcher.register("unlinkPlaylist", ({ args }) =>
    window.unlinkPlaylist?.(args?.jellyfinId, args?.jellyfinName),
  );
  dispatcher.register("fetchJellyfinPlaylists", () =>
    window.fetchJellyfinPlaylists?.(),
  );
  dispatcher.register("searchProvider", ({ args }) =>
    searchProvider(args?.query, args?.provider),
  );
  dispatcher.register("openLyricsMap", ({ args, toNumber }) =>
    openLyricsMap(
      args?.artist,
      args?.title,
      args?.album,
      toNumber(args?.durationSeconds) ?? 0,
    ),
  );
  dispatcher.register("selectJellyfinTrack", ({ args }) =>
    selectJellyfinTrack(args?.jellyfinId),
  );
  dispatcher.register("selectExternalTrack", ({ args, toNumber }) =>
    selectExternalTrack(
      toNumber(args?.resultIndex),
      args?.externalId,
      args?.title,
      args?.artist,
      args?.provider,
      args?.externalUrl,
    ),
  );
  dispatcher.register("downloadFile", ({ args }) =>
    window.downloadFile?.(args?.path),
  );
  dispatcher.register("deleteDownload", ({ args }) =>
    window.deleteDownload?.(args?.path),
  );

  initNavigationView({ switchTab: window.switchTab });

  setupModalBackdropClose();

  initScrobblingView({
    isAuthenticated: () => authSession.isAuthenticated(),
    loadScrobblingConfig: () => window.loadScrobblingConfig?.(),
  });

  authSession.bootstrapAuth();
});

console.log("✅ Main.js module loaded");
