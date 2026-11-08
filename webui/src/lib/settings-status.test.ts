import { describe, expect, it } from "vitest";
import type { ConfigField } from "$lib/api";
import { settingsStatus } from "$lib/settings-status";

const field = (key: string, valuePath: string, type = "text"): ConfigField => ({ key, label: key, type, valuePath } as ConfigField);
const fields = [
  field("AUDIO_QUALITY", "audio.quality", "audio-quality"),
  field("PLAYBACK_START_PREFERENCE", "playback.startPreference", "select"),
  field("EXPLICIT_FILTER", "explicitFilter", "select"),
  field("BACKEND_MUSIC_LIBRARY_IDS", "operatorSettings.Backend:MusicLibraryIds"),
  field("DOWNLOAD_MODE", "library.downloadMode", "select"),
  field("STORAGE_MODE", "library.storageMode", "select"),
  field("PLAYBACK_LASTFM_SIMILAR_ENABLED", "playback.lastFmSimilarEnabled", "toggle"),
  field("PLAYBACK_LISTENBRAINZ_SIMILAR_ENABLED", "playback.listenBrainzSimilarEnabled", "toggle"),
  field("SCROBBLING_LOCAL_TRACKS_ENABLED", "scrobbling.localTracksEnabled", "toggle"),
  field("EVENT_LOG_RETENTION_DAYS", "operatorSettings.Operations:EventLog:RetentionDays", "number"),
  field("CLIENT_API_ENABLED", "clientApi.enabled", "toggle"),
  field("LISTENERS_CAN_CONNECT_OWN_ACCOUNTS", "providerAccounts.listenersCanConnectOwnAccounts", "boolean"),
];
const config = {
  audio: { quality: "CdLossless" }, playback: { startPreference: "Balanced", lastFmSimilarEnabled: true, listenBrainzSimilarEnabled: false },
  explicitFilter: "CleanOnly", library: { downloadMode: "Track", storageMode: "Cache" },
  operatorSettings: { "Backend:MusicLibraryIds": "a, b,a", "Operations:EventLog:RetentionDays": "30" },
  scrobbling: { localTracksEnabled: false }, clientApi: { enabled: true }, providerAccounts: { listenersCanConnectOwnAccounts: false },
};
const labels = (page: string, source: Record<string, unknown> = config) => settingsStatus(page, source, fields).map(item => item.label);

describe("settings status", () => {
  it("says how each page stands from the loaded settings", () => {
    expect(labels("playback")).toEqual(["Up to CD lossless", "Balanced start", "Clean songs only"]);
    expect(labels("library")).toEqual(["Reads 2 libraries", "Downloads keep just the song", "Played songs are cached"]);
    expect(labels("discovery")).toEqual(["1 similar-song fallback"]);
    expect(labels("history")).toEqual(["Only streamed songs scrobble", "Activity kept 30 days"]);
    expect(labels("people")).toEqual(["Apps on", "Only administrators connect accounts"]);
  });

  it("leaves out what it can't read and pages it doesn't describe", () => {
    expect(labels("library", {})).toEqual(["Reads every library", "Downloads keep just the song", "Played songs are kept"]);
    expect(settingsStatus("playback", config, [])).toEqual([]);
    expect(labels("appearance")).toEqual([]);
  });
});
