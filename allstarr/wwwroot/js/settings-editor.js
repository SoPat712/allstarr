import { escapeHtml, showToast, formatCookieAge } from "./utils.js";
import * as API from "./api.js";
import * as UI from "./ui.js";
import { openModal, closeModal } from "./modals.js";

let currentEditKey = null;
let currentEditType = null;
let currentConfigState = null;
let refreshConfig = async () => {};
let refreshStatus = async () => {};
let showRestartBanner = () => {};

const SETTING_KEY_ALIASES = {
  SearchResultsMinutes: "CACHE_SEARCH_RESULTS_MINUTES",
  PlaylistImagesHours: "CACHE_PLAYLIST_IMAGES_HOURS",
  SpotifyPlaylistItemsHours: "CACHE_SPOTIFY_PLAYLIST_ITEMS_HOURS",
  SpotifyMatchedTracksDays: "CACHE_SPOTIFY_MATCHED_TRACKS_DAYS",
  LyricsDays: "CACHE_LYRICS_DAYS",
  GenreDays: "CACHE_GENRE_DAYS",
  MetadataDays: "CACHE_METADATA_DAYS",
  OdesliLookupDays: "CACHE_ODESLI_LOOKUP_DAYS",
  ProxyImagesDays: "CACHE_PROXY_IMAGES_DAYS",
};

function ensureConfigSection(config, sectionName) {
  if (!config[sectionName] || typeof config[sectionName] !== "object") {
    config[sectionName] = {};
  }

  return config[sectionName];
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

function toToggleValue(value) {
  return parseBoolean(value) ? "true" : "false";
}

function parseInteger(value, fallback) {
  const parsed = Number.parseInt(String(value), 10);
  if (Number.isFinite(parsed)) {
    return parsed;
  }

  return fallback;
}

function textBinding(getter, setter) {
  return {
    get: getter,
    set: setter,
  };
}

function toggleBinding(getter, setter) {
  return {
    get: getter,
    set: (config, value) => setter(config, parseBoolean(value)),
    toInput: toToggleValue,
    fromInput: toToggleValue,
  };
}

function numberBinding(getter, setter, fallbackValue) {
  return {
    get: getter,
    set: (config, value) => {
      const currentValue = Number(getter(config));
      const fallback = Number.isFinite(currentValue)
        ? currentValue
        : fallbackValue;
      setter(config, parseInteger(value, fallback));
    },
  };
}

const SETTINGS_REGISTRY = {
  JELLYFIN_LIBRARY_ID: textBinding(
    (config) => config?.jellyfin?.libraryId ?? "",
    (config, value) => {
      ensureConfigSection(config, "jellyfin").libraryId = value;
    },
  ),
  BACKEND_TYPE: textBinding(
    (config) => config?.backendType ?? "Jellyfin",
    (config, value) => {
      config.backendType = value;
    },
  ),
  MUSIC_SERVICE: textBinding(
    (config) => config?.musicService ?? "SquidWTF",
    (config, value) => {
      config.musicService = value;
    },
  ),
  STORAGE_MODE: textBinding(
    (config) => config?.library?.storageMode ?? "Cache",
    (config, value) => {
      ensureConfigSection(config, "library").storageMode = value;
    },
  ),
  CACHE_DURATION_HOURS: numberBinding(
    (config) => config?.library?.cacheDurationHours ?? 24,
    (config, value) => {
      ensureConfigSection(config, "library").cacheDurationHours = value;
    },
    24,
  ),
  DOWNLOAD_MODE: textBinding(
    (config) => config?.library?.downloadMode ?? "Track",
    (config, value) => {
      ensureConfigSection(config, "library").downloadMode = value;
    },
  ),
  EXPLICIT_FILTER: textBinding(
    (config) => config?.explicitFilter ?? "All",
    (config, value) => {
      config.explicitFilter = value;
    },
  ),
  ENABLE_EXTERNAL_PLAYLISTS: toggleBinding(
    (config) => config?.enableExternalPlaylists ?? false,
    (config, value) => {
      config.enableExternalPlaylists = value;
    },
  ),
  PLAYLISTS_DIRECTORY: textBinding(
    (config) => config?.playlistsDirectory ?? "",
    (config, value) => {
      config.playlistsDirectory = value;
    },
  ),
  REDIS_ENABLED: toggleBinding(
    (config) => config?.redisEnabled ?? false,
    (config, value) => {
      config.redisEnabled = value;
    },
  ),
  ADMIN_BIND_ANY_IP: toggleBinding(
    (config) => config?.admin?.bindAnyIp ?? false,
    (config, value) => {
      ensureConfigSection(config, "admin").bindAnyIp = value;
    },
  ),
  ADMIN_TRUSTED_SUBNETS: textBinding(
    (config) => config?.admin?.trustedSubnets ?? "",
    (config, value) => {
      ensureConfigSection(config, "admin").trustedSubnets = value;
    },
  ),
  DEBUG_LOG_ALL_REQUESTS: toggleBinding(
    (config) => config?.debug?.logAllRequests ?? false,
    (config, value) => {
      ensureConfigSection(config, "debug").logAllRequests = value;
    },
  ),
  SPOTIFY_API_ENABLED: toggleBinding(
    (config) => config?.spotifyApi?.enabled ?? false,
    (config, value) => {
      ensureConfigSection(config, "spotifyApi").enabled = value;
    },
  ),
  SPOTIFY_API_SESSION_COOKIE: textBinding(
    () => "",
    () => {
      // Sensitive values are intentionally never read back into the editor.
    },
  ),
  SPOTIFY_API_CACHE_DURATION_MINUTES: numberBinding(
    (config) => config?.spotifyApi?.cacheDurationMinutes ?? 60,
    (config, value) => {
      ensureConfigSection(config, "spotifyApi").cacheDurationMinutes = value;
    },
    60,
  ),
  SPOTIFY_API_PREFER_ISRC_MATCHING: toggleBinding(
    (config) => config?.spotifyApi?.preferIsrcMatching ?? true,
    (config, value) => {
      ensureConfigSection(config, "spotifyApi").preferIsrcMatching = value;
    },
  ),
  DEEZER_ARL: textBinding(
    () => "",
    () => {
      // Sensitive values are intentionally never read back into the editor.
    },
  ),
  DEEZER_QUALITY: textBinding(
    (config) => config?.deezer?.quality ?? "FLAC",
    (config, value) => {
      ensureConfigSection(config, "deezer").quality = value;
    },
  ),
  DEEZER_MIN_REQUEST_INTERVAL_MS: numberBinding(
    (config) => config?.deezer?.minRequestIntervalMs ?? 200,
    (config, value) => {
      ensureConfigSection(config, "deezer").minRequestIntervalMs = value;
    },
    200,
  ),
  SQUIDWTF_QUALITY: textBinding(
    (config) => config?.squidWtf?.quality ?? "LOSSLESS",
    (config, value) => {
      ensureConfigSection(config, "squidWtf").quality = value;
    },
  ),
  SQUIDWTF_MIN_REQUEST_INTERVAL_MS: numberBinding(
    (config) => config?.squidWtf?.minRequestIntervalMs ?? 200,
    (config, value) => {
      ensureConfigSection(config, "squidWtf").minRequestIntervalMs = value;
    },
    200,
  ),
  APPLE_MUSIC_AIO_URL: textBinding(
    (config) => config?.appleMusic?.baseUrl ?? "http://gamdl-aio:8000",
    (config, value) => {
      ensureConfigSection(config, "appleMusic").baseUrl = value;
    },
  ),
  APPLE_MUSIC_QUALITY: textBinding(
    (config) => config?.appleMusic?.quality ?? "alac-16-44",
    (config, value) => {
      ensureConfigSection(config, "appleMusic").quality = value;
    },
  ),
  MUSICBRAINZ_ENABLED: toggleBinding(
    (config) => config?.musicBrainz?.enabled ?? false,
    (config, value) => {
      ensureConfigSection(config, "musicBrainz").enabled = value;
    },
  ),
  MUSICBRAINZ_USERNAME: textBinding(
    (config) => config?.musicBrainz?.username ?? "",
    (config, value) => {
      ensureConfigSection(config, "musicBrainz").username = value;
    },
  ),
  MUSICBRAINZ_PASSWORD: textBinding(
    () => "",
    () => {
      // Sensitive values are intentionally never read back into the editor.
    },
  ),
  QOBUZ_USER_AUTH_TOKEN: textBinding(
    () => "",
    () => {
      // Sensitive values are intentionally never read back into the editor.
    },
  ),
  QOBUZ_QUALITY: textBinding(
    (config) => config?.qobuz?.quality ?? "FLAC",
    (config, value) => {
      ensureConfigSection(config, "qobuz").quality = value;
    },
  ),
  QOBUZ_MIN_REQUEST_INTERVAL_MS: numberBinding(
    (config) => config?.qobuz?.minRequestIntervalMs ?? 200,
    (config, value) => {
      ensureConfigSection(config, "qobuz").minRequestIntervalMs = value;
    },
    200,
  ),
  JELLYFIN_URL: textBinding(
    (config) => config?.jellyfin?.url ?? "",
    (config, value) => {
      ensureConfigSection(config, "jellyfin").url = value;
    },
  ),
  JELLYFIN_API_KEY: textBinding(
    () => "",
    () => {
      // Sensitive values are intentionally never read back into the editor.
    },
  ),
  JELLYFIN_USER_ID: textBinding(
    (config) => config?.jellyfin?.userId ?? "",
    (config, value) => {
      ensureConfigSection(config, "jellyfin").userId = value;
    },
  ),
  LIBRARY_DOWNLOAD_PATH: textBinding(
    (config) => config?.library?.downloadPath ?? "",
    (config, value) => {
      ensureConfigSection(config, "library").downloadPath = value;
    },
  ),
  LIBRARY_KEPT_PATH: textBinding(
    (config) => config?.library?.keptPath ?? "",
    (config, value) => {
      ensureConfigSection(config, "library").keptPath = value;
    },
  ),
  SPOTIFY_IMPORT_ENABLED: toggleBinding(
    (config) => config?.spotifyImport?.enabled ?? false,
    (config, value) => {
      ensureConfigSection(config, "spotifyImport").enabled = value;
    },
  ),
  SPOTIFY_IMPORT_MATCHING_INTERVAL_HOURS: numberBinding(
    (config) => config?.spotifyImport?.matchingIntervalHours ?? 24,
    (config, value) => {
      ensureConfigSection(config, "spotifyImport").matchingIntervalHours =
        value;
    },
    24,
  ),
  CACHE_SEARCH_RESULTS_MINUTES: numberBinding(
    (config) => config?.cache?.searchResultsMinutes ?? 120,
    (config, value) => {
      ensureConfigSection(config, "cache").searchResultsMinutes = value;
    },
    120,
  ),
  CACHE_PLAYLIST_IMAGES_HOURS: numberBinding(
    (config) => config?.cache?.playlistImagesHours ?? 168,
    (config, value) => {
      ensureConfigSection(config, "cache").playlistImagesHours = value;
    },
    168,
  ),
  CACHE_SPOTIFY_PLAYLIST_ITEMS_HOURS: numberBinding(
    (config) => config?.cache?.spotifyPlaylistItemsHours ?? 168,
    (config, value) => {
      ensureConfigSection(config, "cache").spotifyPlaylistItemsHours = value;
    },
    168,
  ),
  CACHE_SPOTIFY_MATCHED_TRACKS_DAYS: numberBinding(
    (config) => config?.cache?.spotifyMatchedTracksDays ?? 30,
    (config, value) => {
      ensureConfigSection(config, "cache").spotifyMatchedTracksDays = value;
    },
    30,
  ),
  CACHE_LYRICS_DAYS: numberBinding(
    (config) => config?.cache?.lyricsDays ?? 14,
    (config, value) => {
      ensureConfigSection(config, "cache").lyricsDays = value;
    },
    14,
  ),
  CACHE_GENRE_DAYS: numberBinding(
    (config) => config?.cache?.genreDays ?? 30,
    (config, value) => {
      ensureConfigSection(config, "cache").genreDays = value;
    },
    30,
  ),
  CACHE_METADATA_DAYS: numberBinding(
    (config) => config?.cache?.metadataDays ?? 7,
    (config, value) => {
      ensureConfigSection(config, "cache").metadataDays = value;
    },
    7,
  ),
  CACHE_ODESLI_LOOKUP_DAYS: numberBinding(
    (config) => config?.cache?.odesliLookupDays ?? 60,
    (config, value) => {
      ensureConfigSection(config, "cache").odesliLookupDays = value;
    },
    60,
  ),
  CACHE_PROXY_IMAGES_DAYS: numberBinding(
    (config) => config?.cache?.proxyImagesDays ?? 14,
    (config, value) => {
      ensureConfigSection(config, "cache").proxyImagesDays = value;
    },
    14,
  ),
};

function resolveSettingKey(settingKey) {
  return SETTING_KEY_ALIASES[settingKey] || settingKey;
}

function getSettingBinding(settingKey) {
  const resolvedKey = resolveSettingKey(settingKey);
  return { resolvedKey, binding: SETTINGS_REGISTRY[resolvedKey] };
}

function getSettingEditorValue(settingKey, inputType) {
  if (inputType === "password" || !currentConfigState) {
    return "";
  }

  const { binding } = getSettingBinding(settingKey);
  if (!binding || typeof binding.get !== "function") {
    return "";
  }

  const currentValue = binding.get(currentConfigState);

  if (binding.toInput) {
    return binding.toInput(currentValue);
  }

  if (currentValue === null || currentValue === undefined) {
    return "";
  }

  if (typeof currentValue === "string") {
    const normalized = currentValue.trim().toLowerCase();
    if (normalized === "(not set)" || normalized === "-") {
      return "";
    }
  }

  return String(currentValue);
}

function normalizeSettingValueForSave(settingKey, inputType, rawValue) {
  const { binding } = getSettingBinding(settingKey);
  if (binding?.fromInput) {
    return binding.fromInput(rawValue);
  }

  if (inputType === "toggle") {
    return toToggleValue(rawValue);
  }

  return rawValue;
}

function applySettingValueLocally(settingKey, normalizedValue) {
  if (!currentConfigState) {
    return;
  }

  const { binding } = getSettingBinding(settingKey);
  if (!binding || typeof binding.set !== "function") {
    return;
  }

  binding.set(currentConfigState, normalizedValue);
  UI.updateConfigUI(currentConfigState);
  syncConfigUiExtras(currentConfigState);
}

function saveSettingRequiresRestart(settingKey) {
  return settingKey !== "SPOTIFY_API_SESSION_COOKIE";
}

async function persistSettingUpdate(settingKey, value) {
  if (settingKey === "SPOTIFY_API_SESSION_COOKIE") {
    return API.setSpotifySessionCookie(value);
  }

  return API.updateConfigSetting(settingKey, value);
}

function setSelectToCurrentValue(selectEl, currentValue) {
  if (!selectEl || currentValue === null || currentValue === undefined) {
    return;
  }

  const normalizedCurrentValue = String(currentValue).toLowerCase();
  const matchedOption = Array.from(selectEl.options).find(
    (option) => option.value.toLowerCase() === normalizedCurrentValue,
  );

  if (matchedOption) {
    selectEl.value = matchedOption.value;
  }
}

function setConfigTextValue(elementId, value) {
  const element = document.getElementById(elementId);
  if (element) {
    element.textContent = value;
  }
}

export function renderCookieAge(elementId, age) {
  const element = document.getElementById(elementId);
  if (!element) {
    return;
  }

  element.innerHTML = `<span class="${age.class}">${age.text}</span><br><small style="color:var(--text-secondary)">${age.detail}</small>`;
}

function hasConfiguredCookie(maskedCookieValue) {
  const normalized = String(maskedCookieValue || "")
    .trim()
    .toLowerCase();
  return (
    normalized.length > 0 && normalized !== "(not set)" && normalized !== "-"
  );
}

export function syncConfigUiExtras(config) {
  if (!config) {
    return;
  }

  setConfigTextValue(
    "config-musicbrainz-username",
    config.musicBrainz?.username || "(not set)",
  );
  setConfigTextValue(
    "config-musicbrainz-password",
    config.musicBrainz?.password || "(not set)",
  );
  setConfigTextValue(
    "config-cache-search",
    String(config.cache?.searchResultsMinutes ?? 120),
  );
  const configHasCookie = hasConfiguredCookie(config.spotifyApi?.sessionCookie);
  const configCookieAge = formatCookieAge(
    config.spotifyApi?.sessionCookieSetDate,
    configHasCookie,
  );
  renderCookieAge("config-cookie-age", configCookieAge);

  const cacheDurationRow = document.getElementById("cache-duration-row");
  if (cacheDurationRow) {
    cacheDurationRow.style.display =
      config.library?.storageMode === "Cache" ? "" : "none";
  }

  const exportButton = document.getElementById("export-env-btn");
  const exportDisabledHint = document.getElementById(
    "export-env-disabled-hint",
  );
  const allowEnvExport = config.admin?.allowEnvExport === true;

  if (exportButton) {
    exportButton.disabled = !allowEnvExport;
    exportButton.title = allowEnvExport
      ? ""
      : "Disabled by server policy (ADMIN__ENABLE_ENV_EXPORT=false)";
  }

  if (exportDisabledHint) {
    exportDisabledHint.style.display = allowEnvExport ? "none" : "";
  }
}

function openEditSetting(envKey, label, inputType, helpText = "", options = []) {
  currentEditKey = resolveSettingKey(envKey);
  currentEditType = inputType;

  document.getElementById("edit-setting-title").textContent = "Edit " + label;
  document.getElementById("edit-setting-label").textContent = label;

  const helpEl = document.getElementById("edit-setting-help");
  if (helpText) {
    helpEl.textContent = helpText;
    helpEl.style.display = "block";
  } else {
    helpEl.style.display = "none";
  }

  const container = document.getElementById("edit-setting-input-container");
  const currentValue = getSettingEditorValue(currentEditKey, inputType);

  if (inputType === "toggle") {
    container.innerHTML = `
            <select id="edit-setting-value">
                <option value="true">Enabled</option>
                <option value="false">Disabled</option>
            </select>
        `;
    setSelectToCurrentValue(
      document.getElementById("edit-setting-value"),
      currentValue,
    );
  } else if (inputType === "select") {
    const optionHtml = options
      .map((option) => {
        const optionValue = String(option);
        return `<option value="${escapeHtml(optionValue)}">${escapeHtml(optionValue)}</option>`;
      })
      .join("");

    container.innerHTML = `
            <select id="edit-setting-value">
                ${optionHtml}
            </select>
        `;
    setSelectToCurrentValue(
      document.getElementById("edit-setting-value"),
      currentValue,
    );
  } else if (inputType === "password") {
    container.innerHTML = `<input type="password" id="edit-setting-value" placeholder="Enter new value" autocomplete="off">`;
  } else if (inputType === "number") {
    container.innerHTML = `<input type="number" id="edit-setting-value" placeholder="Enter value">`;
    const inputEl = document.getElementById("edit-setting-value");
    if (inputEl) {
      inputEl.value = currentValue;
    }
  } else {
    container.innerHTML = `<input type="text" id="edit-setting-value" placeholder="Enter value">`;
    const inputEl = document.getElementById("edit-setting-value");
    if (inputEl) {
      inputEl.value = currentValue;
    }
  }

  openModal("edit-setting-modal");
}

function openEditCacheSetting(settingKey, label, helpText) {
  const suffix = " (Requires restart to apply)";
  const help = helpText ? `${helpText}${suffix}` : `Cache setting${suffix}`;
  openEditSetting(settingKey, label, "number", help);

  const inputEl = document.getElementById("edit-setting-value");
  if (inputEl) {
    inputEl.min = "1";
  }
}

async function saveEditSetting() {
  const inputEl = document.getElementById("edit-setting-value");
  if (!inputEl) {
    showToast("Setting input is not available", "error");
    return;
  }

  const rawValue = inputEl.value.trim();

  if (
    !rawValue &&
    currentEditType !== "toggle" &&
    currentEditType !== "select"
  ) {
    showToast("Value is required", "error");
    return;
  }

  if (currentEditType === "number" && Number.isNaN(Number(rawValue))) {
    showToast("Please enter a valid number", "error");
    return;
  }

  const value = normalizeSettingValueForSave(
    currentEditKey,
    currentEditType,
    rawValue,
  );

  try {
    await persistSettingUpdate(currentEditKey, value);
    applySettingValueLocally(currentEditKey, value);
    showToast("Setting updated.", "success");
    if (saveSettingRequiresRestart(currentEditKey)) {
      showRestartBanner();
    }
    closeModal("edit-setting-modal");
    await Promise.allSettled([refreshConfig(), refreshStatus()]);
  } catch (error) {
    showToast(error.message || "Failed to update setting", "error");
  }
}

export function setCurrentConfigState(config) {
  currentConfigState = config;
}

export function initSettingsEditor(options) {
  refreshConfig = options.fetchConfig;
  refreshStatus = options.fetchStatus;
  showRestartBanner = options.showRestartBanner;

  window.openEditSetting = openEditSetting;
  window.openEditCacheSetting = openEditCacheSetting;
  window.saveEditSetting = saveEditSetting;

  return {
    setCurrentConfigState,
    syncConfigUiExtras,
  };
}
