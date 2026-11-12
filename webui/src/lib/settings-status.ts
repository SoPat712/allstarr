import type { ConfigField } from "$lib/api";
import { audioQualityOptions, pathValue, playbackStartLabel } from "$lib/settings";
import { splitLibraryIds } from "$lib/settings-fields";
import type { PlaybackStart } from "$lib/api";

// The short line under each Settings category that says what's set, read from the settings already loaded.
// Categories whose state lives in their own cards get a plain description instead.
export function settingsSummary(page: string, config: Record<string, unknown>, fields: ConfigField[], administrator = true): string {
  const value = (key: string) => {
    const field = fields.find(item => item.key === key);
    return field && !field.sensitive ? pathValue(config, field.valuePath) : undefined;
  };
  const has = (key: string) => fields.some(item => item.key === key) && value(key) !== undefined;
  const on = (key: string) => value(key) === true || String(value(key)).toLowerCase() === "true";
  const join = (...parts: (string | false | undefined)[]) => parts.filter(Boolean).join(" · ");
  switch (page) {
    case "listening": return "Your explicit filter, marks and quality";
    case "appearance": return "Theme and colours";
    case "adding": return "File names, quality and extras";
    case "quality": return join(
      audioQualityOptions.find(option => option.value === value("AUDIO_QUALITY"))?.label.split(" · ")[0],
      has("PLAYBACK_START_PREFERENCE") && `${playbackStartLabel(value("PLAYBACK_START_PREFERENCE") as PlaybackStart)} start`) || "Quality and how songs start";
    case "sources": return "Source orders and accounts";
    case "library": {
      const libraries = splitLibraryIds(String(value("BACKEND_MUSIC_LIBRARY_IDS") ?? "")).length;
      return join(
        fields.some(item => item.key === "BACKEND_MUSIC_LIBRARY_IDS") && (libraries ? `${libraries} ${libraries === 1 ? "library" : "libraries"}` : "Every library"),
        has("DOWNLOAD_MODE") && (value("DOWNLOAD_MODE") === "Album" ? "whole albums" : "single songs")) || "Libraries, downloads and folders";
    }
    case "apps": return has("CLIENT_API_ENABLED") ? (on("CLIENT_API_ENABLED") ? "Allstarr features on" : "Allstarr features off") : "Music apps";
    case "mixes": {
      const fallbacks = ["PLAYBACK_LASTFM_SIMILAR_ENABLED", "PLAYBACK_LISTENBRAINZ_SIMILAR_ENABLED"].filter(has);
      const enabled = fallbacks.filter(on).length;
      return fallbacks.length ? `Sound analysis · ${enabled ? `${enabled} ${enabled === 1 ? "fallback" : "fallbacks"}` : "no fallbacks"}` : "Sound analysis and fallbacks";
    }
    case "history": return has("SCROBBLING_LOCAL_TRACKS_ENABLED")
      ? (on("SCROBBLING_LOCAL_TRACKS_ENABLED") ? "Library songs scrobble too" : "Streamed songs scrobble")
      : "Last.fm and ListenBrainz";
    case "people": return "Listeners and sign-in";
    case "alerts": return administrator ? "Problems and where alerts go" : "Your source connections";
    case "storage": {
      const gib = Number(value("CACHE_AUDIO_MAXIMUM_GIB")), streams = Number(value("PROVIDER_STREAMS_PER_LISTENER"));
      return join(gib > 0 && `${gib} GiB cache`, streams > 0 && `${streams} ${streams === 1 ? "stream" : "streams"}`) || "Cache and speed";
    }
    case "backups": return "Backups and health checks";
    case "advanced": return has("ODESLI_ENABLED") ? (on("ODESLI_ENABLED") ? "Song links on" : "Song links off") : "Song links and extensions";
    default: return "";
  }
}
