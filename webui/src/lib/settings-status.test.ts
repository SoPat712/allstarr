import { describe, expect, it } from "vitest";
import type { ConfigField } from "$lib/api";
import { settingsSummary } from "$lib/settings-status";

const field = (key: string, valuePath: string, type = "text"): ConfigField => ({ key, label: key, type, valuePath } as ConfigField);
const fields = [
  field("AUDIO_QUALITY", "audio.quality", "audio-quality"),
  field("PLAYBACK_START_PREFERENCE", "playback.startPreference", "select"),
  field("BACKEND_MUSIC_LIBRARY_IDS", "operatorSettings.Backend:MusicLibraryIds"),
  field("DOWNLOAD_MODE", "library.downloadMode", "select"),
  field("PLAYBACK_LASTFM_SIMILAR_ENABLED", "playback.lastFmSimilarEnabled", "toggle"),
  field("PLAYBACK_LISTENBRAINZ_SIMILAR_ENABLED", "playback.listenBrainzSimilarEnabled", "toggle"),
  field("SCROBBLING_LOCAL_TRACKS_ENABLED", "scrobbling.localTracksEnabled", "toggle"),
  field("CLIENT_API_ENABLED", "clientApi.enabled", "toggle"),
  field("CACHE_AUDIO_MAXIMUM_GIB", "cache.audioMaximumGiB", "number"),
  field("PROVIDER_STREAMS_PER_LISTENER", "playback.providerStreamsPerListener", "number"),
];
const config = {
  audio: { quality: "CdLossless" }, playback: { startPreference: "Balanced", lastFmSimilarEnabled: true, listenBrainzSimilarEnabled: false, providerStreamsPerListener: 6 },
  library: { downloadMode: "Album" }, operatorSettings: { "Backend:MusicLibraryIds": "a, b,a" },
  scrobbling: { localTracksEnabled: false }, clientApi: { enabled: true }, cache: { audioMaximumGiB: 2 },
};
const summary = (page: string, source: Record<string, unknown> = config) => settingsSummary(page, source, fields);

describe("settings summaries", () => {
  it("says what each category has set, from the loaded settings", () => {
    expect(summary("quality")).toBe("CD lossless · Balanced start");
    expect(summary("library")).toBe("2 libraries · whole albums");
    expect(summary("mixes")).toBe("Sound analysis · 1 fallback");
    expect(summary("history")).toBe("Streamed songs scrobble");
    expect(summary("apps")).toBe("Allstarr features on");
    expect(summary("storage")).toBe("2 GiB cache · 6 streams");
  });

  it("falls back to a plain description when nothing is loaded", () => {
    expect(summary("quality", {})).toBe("Quality and how songs start");
    expect(summary("library", {})).toBe("Every library");
    expect(settingsSummary("apps", config, [])).toBe("Music apps");
    expect(summary("appearance")).toBe("Theme and colours");
    expect(settingsSummary("alerts", {}, [], false)).toBe("Your source connections");
    expect(summary("unknown")).toBe("");
  });
});
