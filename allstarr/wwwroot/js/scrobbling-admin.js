import { showToast } from "./utils.js";
import * as API from "./api.js";
import { runAction } from "./operations.js";
import { updateConfigUI } from "./ui.js";

let showRestartBanner = () => {};

async function runScrobblingAction({
  task,
  success,
  error,
  before,
  reload = true,
  onSuccess,
}) {
  const result = await runAction({
    task,
    success,
    error,
    before,
  });

  if (!result) {
    return null;
  }

  if (reload) {
    await loadScrobblingConfig();
  }

  if (onSuccess) {
    await onSuccess(result);
  }

  return result;
}

function parseBoolean(value) {
  if (typeof value === "boolean") {
    return value;
  }

  if (typeof value === "number") {
    return value !== 0;
  }

  if (typeof value === "string") {
    const normalized = value.trim().toLowerCase();
    if (["true", "1", "yes", "on", "enabled"].includes(normalized)) {
      return true;
    }
    if (["false", "0", "no", "off", "disabled"].includes(normalized)) {
      return false;
    }
  }

  return false;
}

async function loadScrobblingConfig() {
  try {
    const [data, status] = await Promise.all([
      API.fetchConfig(),
      API.fetchScrobblingStatus(),
    ]);

    updateConfigUI(data);

    const username = data.scrobbling.lastFm.username;
    const password = data.scrobbling.lastFm.password;
    const sessionKey = data.scrobbling.lastFm.sessionKey;

    const hasApiKey =
      data.scrobbling.lastFm.apiKey &&
      data.scrobbling.lastFm.apiKey !== "(not set)" &&
      !data.scrobbling.lastFm.apiKey.startsWith("(not set)");
    const hasSecret =
      data.scrobbling.lastFm.sharedSecret &&
      data.scrobbling.lastFm.sharedSecret !== "(not set)" &&
      !data.scrobbling.lastFm.sharedSecret.startsWith("(not set)");
    const hasUsername = username && username !== "(not set)";
    const hasPassword = password && password !== "(not set)";
    const hasSessionKey =
      sessionKey && sessionKey !== "(not set)" && sessionKey.length > 0;

    const lastFmStatus = status?.lastFm;
    const usingLegacyKey = lastFmStatus?.usingHardcodedCredentials === true;
    let statusHtml = "";
    if (usingLegacyKey) {
      statusHtml =
        '<span style="color: var(--error);">✗ Suspended API key — set SCROBBLING_LASTFM_API_KEY and SCROBBLING_LASTFM_SHARED_SECRET in .env</span>';
    } else if (data.scrobbling.lastFm.enabled && hasApiKey && hasSecret && hasSessionKey) {
      statusHtml =
        '<span style="color: var(--success);">✓ Configured & Enabled</span>';
    } else if (data.scrobbling.lastFm.enabled && hasSessionKey && (!hasApiKey || !hasSecret)) {
      statusHtml =
        '<span style="color: var(--error);">✗ Missing API key/secret in .env — add SCROBBLING_LASTFM_API_KEY and SCROBBLING_LASTFM_SHARED_SECRET</span>';
    } else if (
      hasApiKey &&
      hasSecret &&
      hasUsername &&
      hasPassword &&
      !hasSessionKey
    ) {
      statusHtml =
        '<span style="color: var(--warning);">⚠️ Ready to Authenticate</span>';
    } else if (hasApiKey && hasSecret && (!hasUsername || !hasPassword)) {
      statusHtml =
        '<span style="color: var(--warning);">⚠️ Needs Username & Password</span>';
    } else if (!hasApiKey || !hasSecret) {
      statusHtml =
        '<span style="color: var(--warning);">⚠️ Set SCROBBLING_LASTFM_API_KEY and SCROBBLING_LASTFM_SHARED_SECRET in .env</span>';
    } else {
      statusHtml = '<span style="color: var(--muted);">○ Not Configured</span>';
    }
    document.getElementById("lastfm-status-value").innerHTML = statusHtml;

    const hasToken =
      data.scrobbling.listenBrainz.userToken &&
      data.scrobbling.listenBrainz.userToken !== "(not set)";

    let lbStatus = "";
    if (data.scrobbling.listenBrainz.enabled && hasToken) {
      lbStatus =
        '<span style="color: var(--success);">✓ Configured & Enabled</span>';
    } else if (hasToken && !data.scrobbling.listenBrainz.enabled) {
      lbStatus =
        '<span style="color: var(--warning);">⚠️ Token Set (Not Enabled)</span>';
    } else if (!hasToken && data.scrobbling.listenBrainz.enabled) {
      lbStatus =
        '<span style="color: var(--warning);">⚠️ Enabled (No Token)</span>';
    } else {
      lbStatus = '<span style="color: var(--muted);">○ Not Configured</span>';
    }
    document.getElementById("listenbrainz-status-value").innerHTML = lbStatus;
  } catch (error) {
    console.error("Failed to load scrobbling config:", error);
    showToast(
      "Failed to load scrobbling configuration: " + error.message,
      "error",
    );
  }
}

async function toggleScrobblingSetting(envKey, label, selector) {
  await runScrobblingAction({
    task: async () => {
      const data = await API.fetchConfig();
      const currentValue = parseBoolean(selector(data));
      const newValue = !currentValue;
      await API.updateConfigSetting(envKey, newValue.toString());
      return newValue;
    },
    success: (newValue) => `${label} ${newValue ? "enabled" : "disabled"}`,
    error: (error) => `Failed to toggle ${label}: ${error.message}`,
  });
}

async function toggleScrobblingEnabled() {
  await toggleScrobblingSetting(
    "SCROBBLING_ENABLED",
    "Scrobbling",
    (config) => config?.scrobbling?.enabled,
  );
}

async function toggleLocalTracksEnabled() {
  await runScrobblingAction({
    task: async () => {
      const data = await API.fetchScrobblingStatus();
      const newValue = !data.localTracksEnabled;
      return API.updateLocalTracksScrobbling(newValue);
    },
    success: (result) => result.message || "Local track scrobbling updated",
    error: (error) =>
      "Failed to toggle local track scrobbling: " + error.message,
  });
}

async function toggleSyntheticLocalPlayedSignalEnabled() {
  await toggleScrobblingSetting(
    "SCROBBLING_SYNTHETIC_LOCAL_PLAYED_SIGNAL_ENABLED",
    "Synthetic local played signal",
    (config) => config?.scrobbling?.syntheticLocalPlayedSignalEnabled,
  );
}

async function toggleLastFmEnabled() {
  await toggleScrobblingSetting(
    "SCROBBLING_LASTFM_ENABLED",
    "Last.fm",
    (config) => config?.scrobbling?.lastFm?.enabled,
  );
}

async function toggleListenBrainzEnabled() {
  await toggleScrobblingSetting(
    "SCROBBLING_LISTENBRAINZ_ENABLED",
    "ListenBrainz",
    (config) => config?.scrobbling?.listenBrainz?.enabled,
  );
}

async function editLastFmUsername() {
  const value = prompt("Enter your Last.fm username:");
  if (value === null) {
    return;
  }

  await runScrobblingAction({
    task: () =>
      API.updateConfigSetting("SCROBBLING_LASTFM_USERNAME", value.trim()),
    success: "Last.fm username updated",
    error: (error) => "Failed to update username: " + error.message,
  });
}

async function editLastFmPassword() {
  const value = prompt(
    "Enter your Last.fm password:\n\nThis is stored encrypted and only used for authentication.",
  );
  if (value === null) {
    return;
  }

  await runScrobblingAction({
    task: () =>
      API.updateConfigSetting("SCROBBLING_LASTFM_PASSWORD", value.trim()),
    success: "Last.fm password updated",
    error: (error) => "Failed to update password: " + error.message,
  });
}

async function editListenBrainzToken() {
  const value = prompt(
    "Enter your ListenBrainz User Token:\n\nGet from https://listenbrainz.org/profile/",
  );
  if (value === null) {
    return;
  }

  await runScrobblingAction({
    task: () =>
      API.updateConfigSetting(
        "SCROBBLING_LISTENBRAINZ_USER_TOKEN",
        value.trim(),
      ),
    success: "ListenBrainz token updated",
    error: (error) => "Failed to update token: " + error.message,
  });
}

async function authenticateLastFm() {
  try {
    if (window.saveAllSettings) {
      await window.saveAllSettings('tab-services', { silent: true });
    }
  } catch (err) {
    showToast("Failed to save credentials before authentication", "error");
    return;
  }

  await runScrobblingAction({
    before: async () => {
      showToast("Authenticating with Last.fm...", "info");
    },
    task: () => API.authenticateLastFm(),
    success:
      "✓ Authentication successful! Session key saved. Please restart the container.",
    error: (error) => "Authentication failed: " + error.message,
    onSuccess: async () => {
      showRestartBanner();
    },
  });
}

async function testLastFmConnection() {
  await runAction({
    task: () => API.testLastFmConnection(),
    success: (data) =>
      `✓ Last.fm connection successful! User: ${data.username}, Scrobbles: ${data.playcount}`,
    error: (error) => "Failed to test connection: " + error.message,
  });
}

async function validateListenBrainzToken() {
  const token = document.getElementById("listenbrainz-token-value")?.value?.trim() || "";
  if (!token) {
    showToast("Please enter a ListenBrainz token", "error");
    return;
  }

  if (token === "••••••••") {
    // Validate currently saved token
    await runScrobblingAction({
      before: async () => {
        showToast("Validating ListenBrainz token...", "info");
      },
      task: () => API.validateListenBrainzToken(),
      success: (data) =>
        `✓ Token validated! User: ${data.username}.`,
      error: (error) => "Validation failed: " + error.message,
    });
    return;
  }

  try {
    if (window.saveAllSettings) {
      await window.saveAllSettings('tab-services', { silent: true });
    }
  } catch (err) {
    showToast("Failed to save token before validation", "error");
    return;
  }

  await runScrobblingAction({
    before: async () => {
      showToast("Validating ListenBrainz token...", "info");
    },
    task: () => API.validateListenBrainzToken(token),
    success: (data) =>
      `✓ Token validated! User: ${data.username}. Please restart the container.`,
    error: (error) => "Validation failed: " + error.message,
    onSuccess: async () => {
      showRestartBanner();
    },
  });
}

async function testListenBrainzConnection() {
  await runAction({
    task: () => API.testListenBrainzConnection(),
    success: (data) =>
      `✓ ListenBrainz connection successful! User: ${data.username}`,
    error: (error) => "Failed to test connection: " + error.message,
  });
}

export function initScrobblingAdmin(options) {
  showRestartBanner = options.showRestartBanner;

  window.loadScrobblingConfig = loadScrobblingConfig;
  window.toggleScrobblingEnabled = toggleScrobblingEnabled;
  window.toggleLocalTracksEnabled = toggleLocalTracksEnabled;
  window.toggleSyntheticLocalPlayedSignalEnabled =
    toggleSyntheticLocalPlayedSignalEnabled;
  window.toggleLastFmEnabled = toggleLastFmEnabled;
  window.toggleListenBrainzEnabled = toggleListenBrainzEnabled;
  window.editLastFmUsername = editLastFmUsername;
  window.editLastFmPassword = editLastFmPassword;
  window.editListenBrainzToken = editListenBrainzToken;
  window.authenticateLastFm = authenticateLastFm;
  window.testLastFmConnection = testLastFmConnection;
  window.validateListenBrainzToken = validateListenBrainzToken;
  window.testListenBrainzConnection = testListenBrainzConnection;
}
