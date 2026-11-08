import type { ConfigField } from "$lib/api";
import { audioQualityOptions, pathValue, playbackStartLabel } from "$lib/settings";
import { splitLibraryIds } from "$lib/settings-fields";
import type { PlaybackStart } from "$lib/api";

export type SettingsStatusItem = { label: string };

// One line under a Settings page title that says how things stand, read from the settings already loaded.
export function settingsStatus(page: string, config: Record<string, unknown>, fields: ConfigField[]): SettingsStatusItem[] {
  const value = (key: string) => {
    const field = fields.find(item => item.key === key);
    return field && !field.sensitive ? pathValue(config, field.valuePath) : undefined;
  };
  const has = (key: string) => fields.some(item => item.key === key);
  const on = (key: string) => value(key) === true || String(value(key)).toLowerCase() === "true";
  const items: (string | false)[] = [];
  if (page === "playback") {
    const quality = audioQualityOptions.find(option => option.value === value("AUDIO_QUALITY"))?.label.split(" · ")[0];
    const explicit = value("EXPLICIT_FILTER");
    items.push(
      Boolean(quality) && `Up to ${quality}`,
      has("PLAYBACK_START_PREFERENCE") && `${playbackStartLabel(value("PLAYBACK_START_PREFERENCE") as PlaybackStart)} start`,
      has("EXPLICIT_FILTER") && (explicit === "CleanOnly" ? "Clean songs only" : explicit === "ExplicitOnly" ? "Explicit versions first" : "All songs"));
  } else if (page === "library") {
    const libraries = splitLibraryIds(String(value("BACKEND_MUSIC_LIBRARY_IDS") ?? "")).length;
    items.push(
      has("BACKEND_MUSIC_LIBRARY_IDS") && (libraries ? `Reads ${libraries} ${libraries === 1 ? "library" : "libraries"}` : "Reads every library"),
      has("DOWNLOAD_MODE") && (value("DOWNLOAD_MODE") === "Album" ? "Downloads keep the whole album" : "Downloads keep just the song"),
      has("STORAGE_MODE") && (value("STORAGE_MODE") === "Cache" ? "Played songs are cached" : "Played songs are kept"));
  } else if (page === "discovery") {
    const fallbacks = ["PLAYBACK_LASTFM_SIMILAR_ENABLED", "PLAYBACK_LISTENBRAINZ_SIMILAR_ENABLED"].filter(key => has(key));
    const enabled = fallbacks.filter(on).length;
    if (fallbacks.length) items.push(enabled ? `${enabled} similar-song ${enabled === 1 ? "fallback" : "fallbacks"}` : "No similar-song fallback");
  } else if (page === "history") {
    const days = Number(value("EVENT_LOG_RETENTION_DAYS"));
    items.push(
      has("SCROBBLING_LOCAL_TRACKS_ENABLED") && (on("SCROBBLING_LOCAL_TRACKS_ENABLED") ? "Library songs scrobble too" : "Only streamed songs scrobble"),
      Number.isFinite(days) && days > 0 && `Activity kept ${days} ${days === 1 ? "day" : "days"}`);
  } else if (page === "people") {
    items.push(
      has("CLIENT_API_ENABLED") && (on("CLIENT_API_ENABLED") ? "Apps on" : "Apps off"),
      has("LISTENERS_CAN_CONNECT_OWN_ACCOUNTS") && (on("LISTENERS_CAN_CONNECT_OWN_ACCOUNTS") ? "Listeners connect their own accounts" : "Only administrators connect accounts"));
  }
  return items.filter((item): item is string => Boolean(item)).map(label => ({ label }));
}
