import { showToast } from "./utils.js";
import * as API from "./api.js";

let isAuthenticated = false;
let authRecoveryInProgress = false;
let currentSessionUser = null;

let stopDashboardRefresh = () => {};
let loadDashboardData = async () => {};
let switchTab = () => {};
let onUnauthenticated = () => {};

function setAuthenticatedState(authenticated, user = null) {
  isAuthenticated = authenticated;
  currentSessionUser = authenticated ? user : null;

  if (!authenticated) {
    onUnauthenticated();
  }

  const authGate = document.getElementById("auth-gate");
  const mainContainer = document.getElementById("main-container");
  const authUserDisplay = document.getElementById("auth-user-display");
  const authUserName = document.getElementById("auth-user-name");
  const logoutButton = document.getElementById("auth-logout-btn");
  const authError = document.getElementById("auth-error");

  if (
    !authGate ||
    !mainContainer ||
    !authUserDisplay ||
    !authUserName ||
    !logoutButton
  ) {
    return;
  }

  authGate.style.display = authenticated ? "none" : "flex";
  mainContainer.style.display = authenticated ? "block" : "none";
  authUserDisplay.style.display = authenticated ? "block" : "none";
  logoutButton.style.display = authenticated ? "inline-block" : "none";
  authUserName.textContent = user?.name || "-";

  if (authError) {
    authError.textContent = "";
  }

  applyAuthorizationScope();
}

function isAdminSession() {
  return !!currentSessionUser?.isAdministrator;
}

function getDefaultTabForSession() {
  return isAdminSession() ? "dashboard" : "jellyfin-playlists";
}

function ensureValidActiveTab() {
  const activeTab = document.querySelector(".tab.active");
  const activeContent = document.querySelector(".tab-content.active");
  if (!activeTab || !activeContent) {
    switchTab(getDefaultTabForSession());
  }
}

function applyAuthorizationScope() {
  const isAdmin = isAdminSession();
  const adminOnlyTabs = [
    "dashboard",
    "playlists",
    "kept",
    "scrobbling",
    "config",
    "endpoints",
  ];

  adminOnlyTabs.forEach((tabName) => {
    const tab = document.querySelector(`.tab[data-tab="${tabName}"]`);
    const content = document.getElementById(`tab-${tabName}`);

    if (tab) {
      tab.style.display = isAdmin ? "" : "none";
      if (!isAdmin) {
        tab.classList.remove("active");
      }
    }

    if (content) {
      content.style.display = isAdmin ? "" : "none";
      if (!isAdmin) {
        content.classList.remove("active");
      }
    }
  });

  const userFilter = document.getElementById("jellyfin-user-filter");
  if (userFilter) {
    userFilter.style.display = isAdmin ? "flex" : "none";
  }

  const statusIndicator = document.getElementById("status-indicator");
  if (statusIndicator) {
    statusIndicator.style.display = isAdmin ? "" : "none";
  }

  if (isAuthenticated && !isAdmin) {
    switchTab("jellyfin-playlists");
  }

  if (isAuthenticated) {
    ensureValidActiveTab();
  }
}

function patchFetchForAuthRecovery() {
  const nativeFetch = window.fetch.bind(window);
  window.fetch = async (...args) => {
    const response = await nativeFetch(...args);
    const url = typeof args[0] === "string" ? args[0] : args[0]?.url || "";

    if (
      response.status === 401 &&
      url.includes("/api/admin") &&
      !url.includes("/api/admin/auth/") &&
      !authRecoveryInProgress
    ) {
      window.dispatchEvent(new CustomEvent("admin-auth-required"));
    }

    return response;
  };
}

async function ensureAdminSession() {
  try {
    const session = await API.fetchAdminSession();
    if (!session?.authenticated) {
      setAuthenticatedState(false);
      return false;
    }

    setAuthenticatedState(true, session.user);
    return true;
  } catch {
    setAuthenticatedState(false);
    return false;
  }
}

async function handleAuthRequired(
  message = "Session expired. Please sign in again.",
) {
  if (authRecoveryInProgress) {
    return;
  }

  authRecoveryInProgress = true;
  stopDashboardRefresh();
  setAuthenticatedState(false);

  const authError = document.getElementById("auth-error");
  if (authError) {
    authError.textContent = message;
  }

  try {
    await API.logoutAdminSession();
  } catch {
    // Ignore logout errors during auth recovery.
  } finally {
    authRecoveryInProgress = false;
  }
}

async function logoutAdminSession() {
  stopDashboardRefresh();
  try {
    await API.logoutAdminSession();
  } catch {
    // Ignore logout errors; always clear local UI auth state.
  }

  setAuthenticatedState(false);
  showToast("Signed out", "success");
}

function wireLoginForm() {
  const loginForm = document.getElementById("auth-login-form");
  if (!loginForm) {
    return;
  }

  loginForm.addEventListener("submit", async (event) => {
    event.preventDefault();

    const usernameInput = document.getElementById("auth-username");
    const passwordInput = document.getElementById("auth-password");
    const authError = document.getElementById("auth-error");
    const username = usernameInput?.value?.trim() || "";
    const password = passwordInput?.value || "";

    if (!username || !password) {
      if (authError) {
        authError.textContent = "Username and password are required.";
      }
      return;
    }

    try {
      if (authError) {
        authError.textContent = "";
      }

      const result = await API.loginAdminSession(username, password);
      if (passwordInput) {
        passwordInput.value = "";
      }

      setAuthenticatedState(true, result.user);
      switchTab(getDefaultTabForSession());
      await loadDashboardData();
    } catch (error) {
      if (authError) {
        authError.textContent = error.message || "Authentication failed.";
      }
    }
  });
}

async function bootstrapAuth() {
  const hasSession = await ensureAdminSession();
  if (hasSession) {
    await loadDashboardData();
  } else {
    const usernameInput = document.getElementById("auth-username");
    usernameInput?.focus();
  }
}

export function initAuthSession(options) {
  stopDashboardRefresh = options.stopDashboardRefresh;
  loadDashboardData = options.loadDashboardData;
  switchTab = options.switchTab;
  onUnauthenticated = options.onUnauthenticated;

  patchFetchForAuthRecovery();
  wireLoginForm();

  window.addEventListener("admin-auth-required", () => {
    handleAuthRequired();
  });

  window.logoutAdminSession = logoutAdminSession;

  return {
    isAuthenticated: () => isAuthenticated,
    isAdminSession,
    getCurrentUserId: () => currentSessionUser?.id || null,
    bootstrapAuth,
  };
}
