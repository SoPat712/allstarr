// API calls
async function readErrorMessage(res, fallback) {
  try {
    const error = await res.json();
    return error.error || error.message || fallback;
  } catch {
    return fallback;
  }
}

async function requestJson(
  url,
  options = {},
  fallbackError = "Request failed",
) {
  const res = await fetch(url, options);
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, fallbackError));
  }
  return await res.json();
}

async function requestBlob(
  url,
  options = {},
  fallbackError = "Request failed",
) {
  const res = await fetch(url, options);
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, fallbackError));
  }
  return await res.blob();
}

async function requestOptionalJson(url, options = {}) {
  const res = await fetch(url, options);
  if (!res.ok) {
    return null;
  }
  return await res.json();
}

function asJsonBody(payload) {
  return {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  };
}

export async function fetchAdminSession() {
  return requestJson(
    "/api/admin/auth/me",
    { cache: "no-store" },
    "Failed to fetch admin session",
  );
}

export async function loginAdminSession(username, password) {
  return requestJson(
    "/api/admin/auth/login",
    asJsonBody({ username, password }),
    "Authentication failed",
  );
}

export async function logoutAdminSession() {
  return requestJson(
    "/api/admin/auth/logout",
    { method: "POST" },
    "Failed to logout",
  );
}

export async function fetchStatus() {
  return requestJson("/api/admin/status", {}, "Failed to fetch status");
}

export async function fetchPlaylists() {
  return requestJson(
    "/api/admin/playlists",
    {},
    "Failed to fetch playlists",
  );
}

export async function fetchPlaylistTracks(name) {
  return requestJson(
    `/api/admin/playlists/${encodeURIComponent(name)}/tracks`,
    {},
    "Failed to fetch playlist tracks",
  );
}

export async function fetchTrackMappings() {
  return requestJson(
    "/api/admin/mappings/tracks",
    {},
    "Failed to fetch track mappings",
  );
}

export async function deleteTrackMapping(playlist, spotifyId) {
  return requestJson(
    `/api/admin/mappings/tracks?playlist=${encodeURIComponent(playlist)}&spotifyId=${encodeURIComponent(spotifyId)}`,
    { method: "DELETE" },
    "Failed to remove mapping",
  );
}

export async function fetchDownloads() {
  return requestJson(
    "/api/admin/downloads",
    {},
    "Failed to fetch downloads",
  );
}

export async function deleteDownload(path) {
  return requestJson(
    `/api/admin/downloads?path=${encodeURIComponent(path)}`,
    { method: "DELETE" },
    "Failed to delete download",
  );
}

export async function fetchConfig() {
  return requestJson(
    "/api/admin/config",
    { cache: "no-store" },
    "Failed to fetch config",
  );
}

export async function updateConfig(key, value) {
  return requestJson(
    "/api/admin/config",
    asJsonBody({ key, value }),
    "Failed to update setting",
  );
}

export async function fetchJellyfinUsers() {
  return requestOptionalJson("/api/admin/jellyfin/users");
}

export async function fetchJellyfinPlaylists(userId = null) {
  let url = "/api/admin/jellyfin/playlists";
  if (userId) {
    url += "?userId=" + encodeURIComponent(userId);
  }

  return requestJson(url, {}, "Failed to fetch Jellyfin playlists");
}

export async function fetchSpotifyUserPlaylists(userId = null) {
  let url = "/api/admin/spotify/user-playlists";
  if (userId) {
    url += "?userId=" + encodeURIComponent(userId);
  }

  return requestJson(url, {}, "Failed to fetch Spotify playlists");
}

export async function fetchSpotifySessionCookieStatus(userId = null) {
  let url = "/api/admin/spotify/session-cookie/status";
  if (userId) {
    url += "?userId=" + encodeURIComponent(userId);
  }

  return requestJson(
    url,
    {},
    "Failed to fetch Spotify session cookie status",
  );
}

export async function setSpotifySessionCookie(sessionCookie, userId = null) {
  const payload = { sessionCookie };
  if (userId) {
    payload.userId = userId;
  }

  return requestJson(
    "/api/admin/spotify/session-cookie",
    asJsonBody(payload),
    "Failed to save Spotify session cookie",
  );
}

export async function linkPlaylist(
  jellyfinId,
  spotifyId,
  syncSchedule,
  userId,
) {
  const payload = { spotifyPlaylistId: spotifyId, syncSchedule };
  if (userId) {
    payload.userId = userId;
  }

  return requestJson(
    `/api/admin/jellyfin/playlists/${encodeURIComponent(jellyfinId)}/link`,
    asJsonBody(payload),
    "Failed to link playlist",
  );
}

export async function unlinkPlaylist(jellyfinId) {
  return requestJson(
    `/api/admin/jellyfin/playlists/${encodeURIComponent(jellyfinId)}/unlink`,
    { method: "DELETE" },
    "Failed to unlink playlist",
  );
}

export async function refreshPlaylists() {
  return requestJson(
    "/api/admin/playlists/refresh",
    { method: "POST" },
    "Failed to refresh playlists",
  );
}

export async function refreshPlaylist(name) {
  return requestJson(
    `/api/admin/playlists/${encodeURIComponent(name)}/refresh`,
    { method: "POST" },
    "Failed to refresh playlist",
  );
}

export async function clearPlaylistCache(name) {
  return requestJson(
    `/api/admin/playlists/${encodeURIComponent(name)}/clear-cache`,
    { method: "POST" },
    "Failed to clear playlist cache",
  );
}

export async function matchPlaylistTracks(name) {
  return requestJson(
    `/api/admin/playlists/${encodeURIComponent(name)}/match`,
    { method: "POST" },
    "Failed to match playlist tracks",
  );
}

export async function matchAllPlaylists() {
  return requestJson(
    "/api/admin/playlists/match-all",
    { method: "POST" },
    "Failed to match all playlists",
  );
}

export async function rebuildAllPlaylists() {
  return requestJson(
    "/api/admin/playlists/rebuild-all",
    { method: "POST" },
    "Failed to rebuild all playlists",
  );
}

export async function clearCache() {
  return requestJson(
    "/api/admin/cache/clear",
    { method: "POST" },
    "Failed to clear cache",
  );
}

export async function restartContainer() {
  return requestJson(
    "/api/admin/restart",
    { method: "POST" },
    "Failed to restart container",
  );
}

export async function fetchEndpointUsage(top = 50) {
  return requestJson(
    `/api/admin/debug/endpoint-usage?top=${top}`,
    {},
    "Failed to fetch endpoint usage",
  );
}

export async function clearEndpointUsage() {
  return requestJson(
    "/api/admin/debug/endpoint-usage",
    { method: "DELETE" },
    "Failed to clear endpoint usage",
  );
}

export async function addPlaylist(name, spotifyId) {
  return requestJson(
    "/api/admin/playlists",
    asJsonBody({ name, spotifyId }),
    "Failed to add playlist",
  );
}

export async function removePlaylist(name) {
  return requestJson(
    `/api/admin/playlists/${encodeURIComponent(name)}`,
    { method: "DELETE" },
    "Failed to remove playlist",
  );
}

export async function editPlaylistSchedule(playlistName, syncSchedule) {
  return requestJson(
    `/api/admin/playlists/${encodeURIComponent(playlistName)}/schedule`,
    {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ syncSchedule }),
    },
    "Failed to update schedule",
  );
}

export async function searchJellyfin(query) {
  return requestJson(
    `/api/admin/jellyfin/search?query=${encodeURIComponent(query)}`,
    {},
    "Failed to search Jellyfin",
  );
}

export async function searchExternalTracks(query, provider = "squidwtf") {
  return requestJson(
    `/api/admin/external/search?query=${encodeURIComponent(query)}&provider=${encodeURIComponent(provider)}`,
    {},
    "Failed to search external provider",
  );
}

export async function getJellyfinTrack(jellyfinId) {
  return requestJson(
    `/api/admin/jellyfin/track/${jellyfinId}`,
    {},
    "Failed to fetch Jellyfin track",
  );
}

export async function saveTrackMapping(playlistName, mapping) {
  return requestJson(
    `/api/admin/playlists/${encodeURIComponent(playlistName)}/map`,
    asJsonBody(mapping),
    "Failed to save mapping",
  );
}

export async function saveLyricsMapping(
  artist,
  title,
  album,
  durationSeconds,
  lyricsId,
) {
  return requestJson(
    "/api/admin/lyrics/map",
    asJsonBody({ artist, title, album, durationSeconds, lyricsId }),
    "Failed to save lyrics mapping",
  );
}

export async function updateConfigSetting(key, value) {
  return requestJson(
    "/api/admin/config",
    asJsonBody({ updates: { [key]: value } }),
    "Failed to update setting",
  );
}

export async function initCookieDate() {
  return requestJson(
    "/api/admin/config/init-cookie-date",
    { method: "POST" },
    "Failed to initialize cookie date",
  );
}

export async function exportEnv() {
  return requestBlob(
    "/api/admin/export-env",
    {},
    "Export failed",
  );
}

export async function importEnv(file) {
  const formData = new FormData();
  formData.append("file", file);

  return requestJson(
    "/api/admin/import-env",
    {
      method: "POST",
      body: formData,
    },
    "Failed to import .env file",
  );
}

export async function getSquidWTFBaseUrl() {
  return requestJson(
    "/api/admin/squidwtf-base-url",
    {},
    "Failed to fetch SquidWTF base URL",
  );
}

export async function fetchScrobblingStatus() {
  return requestJson(
    "/api/admin/scrobbling/status",
    {},
    "Failed to fetch scrobbling status",
  );
}

export async function updateLocalTracksScrobbling(enabled) {
  return requestJson(
    "/api/admin/scrobbling/local-tracks/update",
    asJsonBody({ enabled }),
    "Failed to update local track scrobbling",
  );
}

export async function authenticateLastFm() {
  return requestJson(
    "/api/admin/scrobbling/lastfm/authenticate",
    {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
    },
    "Failed to authenticate with Last.fm",
  );
}

export async function testLastFmConnection() {
  return requestJson(
    "/api/admin/scrobbling/lastfm/test",
    { method: "POST" },
    "Failed to test Last.fm connection",
  );
}

export async function validateListenBrainzToken(userToken) {
  return requestJson(
    "/api/admin/scrobbling/listenbrainz/validate",
    asJsonBody({ userToken }),
    "Failed to validate ListenBrainz token",
  );
}

export async function testListenBrainzConnection() {
  return requestJson(
    "/api/admin/scrobbling/listenbrainz/test",
    { method: "POST" },
    "Failed to test ListenBrainz connection",
  );
}
