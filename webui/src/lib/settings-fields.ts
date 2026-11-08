import type { ConfigField } from "$lib/api";
import { pathValue } from "$lib/settings";

export function settingRestartNote(description: string) {
  return `${description.trim() ? " · " : ""}Restarts Allstarr`;
}

export function settingValue(config: Record<string, unknown>, field: ConfigField): string {
  if (field.sensitive) return "";
  const value = pathValue(config, field.valuePath);
  if (field.type === "toggle" || field.type === "boolean") return String(value === true || String(value).toLowerCase() === "true");
  return value == null ? "" : String(value);
}

export function splitLibraryIds(value: string): string[] {
  return [...new Set(value.split(",").map(id => id.trim()).filter(Boolean))];
}

export function joinLibraryIds(ids: string[]): string {
  return splitLibraryIds(ids.join(",")).join(",");
}

// Plain names and short help for server settings, shared by the settings pages and Find a setting.
export const settingNames: Record<string, string> = {
  PLAYBACK_START_PREFERENCE: "Playback start for everyone", EXPLICIT_FILTER: "Explicit songs for everyone",
  STORAGE_MODE: "Cache played songs", CACHE_DURATION_HOURS: "Keep cached songs for (hours)",
  BACKEND_MUSIC_LIBRARY_IDS: "Libraries to use", ODESLI_ENABLED: "Find the same song on other services",
  ODESLI_API_KEY: "song.link API key", ODESLI_RATE_LIMIT_MS: "Wait between song.link lookups (milliseconds)",
  EXTENSIONS_ALLOW_REMOTE_INSTALL: "Install extensions from outside your home network",
  PLAYBACK_LASTFM_SIMILAR_ENABLED: "Similar songs from Last.fm when a service has none",
  PLAYBACK_LISTENBRAINZ_SIMILAR_ENABLED: "Similar songs from ListenBrainz as a last resort",
  SCROBBLING_LOCAL_TRACKS_ENABLED: "Scrobble songs from your own library",
  ADMIN_BIND_ANY_IP: "Admin page on all network interfaces", ADMIN_TRUSTED_SUBNETS: "Trusted admin networks",
  DEBUG_LOG_ALL_REQUESTS: "Log request usage", LIBRARY_DOWNLOAD_PATH: "Downloads", LIBRARY_KEPT_PATH: "Kept songs",
};

export const settingHelp: Record<string, string> = {
  CLIENT_API_ENABLED: "Let signed-in music apps use song details, lyrics and mixes.",
  LISTENERS_CAN_CONNECT_OWN_ACCOUNTS: "Let listeners connect personal accounts. Existing connections stay usable.",
  DOWNLOAD_MODE: "Save just the song, or its whole album.",
  PLAYBACK_START_PREFERENCE: "Balanced favors quality when starts stay under 3 seconds.",
  PLAYBACK_PREPARE_UPCOMING_SONGS: "Prepare the next Apple song after 30 seconds of playback. Uses cache space.",
  PLAYBACK_LASTFM_SIMILAR_ENABLED: "Use a connected Last.fm account when a service has no suggestions.",
  PLAYBACK_LISTENBRAINZ_SIMILAR_ENABLED: "Use ListenBrainz when the service and Last.fm have no suggestions.",
  SCROBBLING_LOCAL_TRACKS_ENABLED: "Leave off when your server's own scrobbler (like Jellyfin's Last.fm plugin) sends them. Streamed songs always scrobble.",
  BACKEND_MUSIC_LIBRARY_IDS: "Choose libraries, or use every library your server allows.",
  ODESLI_ENABLED: "Uses Deezer, Apple Music and your enabled sources",
  ODESLI_API_KEY: "Optional. With a key, the official API is tried first. Leave unchanged to keep it.",
  STORAGE_MODE: "Songs saved after streaming start faster next time. Hearts, Keep and retained playlists protect their copies in either mode.",
  CACHE_DURATION_HOURS: "After the last play. The size limit can remove cached songs sooner.",
  MATCHING_LOCAL_PREFERENCE_PERCENT: "Only for matches below the automatic threshold. Never raises confidence.",
};
