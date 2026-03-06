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

let cookieDateInitialized = false;
let restartRequired = false;

window.showToast = showToast;
window.escapeHtml = escapeHtml;
window.escapeJs = escapeJs;
window.openModal = openModal;
window.closeModal = closeModal;
window.capitalizeProvider = capitalizeProvider;

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
    .querySelectorAll(".tab-content")
    .forEach((content) => content.classList.remove("active"));

  const tab = document.querySelector(`.tab[data-tab="${tabName}"]`);
  const content = document.getElementById(`tab-${tabName}`);

  if (tab && content) {
    tab.classList.add("active");
    content.classList.add("active");
    window.location.hash = tabName;
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

window.viewTracks = viewTracks;
window.openManualMap = openManualMap;
window.openExternalMap = openExternalMap;
window.openMapToLocal = openManualMap;
window.openMapToExternal = openExternalMap;
window.searchJellyfinTracks = searchJellyfinTracks;
window.selectJellyfinTrack = selectJellyfinTrack;
window.saveLocalMapping = saveLocalMapping;
window.saveManualMapping = saveManualMapping;
window.searchExternalTracks = searchExternalTracks;
window.selectExternalTrack = selectExternalTrack;
window.validateExternalMapping = validateExternalMapping;
window.openLyricsMap = openLyricsMap;
window.saveLyricsMapping = saveLyricsMapping;
window.searchProvider = searchProvider;

document.addEventListener("DOMContentLoaded", () => {
  console.log("🚀 Allstarr Admin UI (Modular) loaded");

  document.querySelectorAll(".tab").forEach((tab) => {
    tab.addEventListener("click", () => {
      window.switchTab(tab.dataset.tab);
    });
  });

  const hash = window.location.hash.substring(1);
  if (hash) {
    window.switchTab(hash);
  }

  setupModalBackdropClose();

  const scrobblingTab = document.querySelector('.tab[data-tab="scrobbling"]');
  if (scrobblingTab) {
    scrobblingTab.addEventListener("click", () => {
      if (authSession.isAuthenticated()) {
        window.loadScrobblingConfig();
      }
    });
  }

  authSession.bootstrapAuth();
});

console.log("✅ Main.js module loaded");
