import { describe, expect, it } from "vitest";
import type { ConfigField } from "$lib/api";
import { searchSettings, settingPage } from "$lib/settings-search";

const field = (key: string, label: string): ConfigField => ({ key, label, type: "toggle", valuePath: key } as ConfigField);
const fields = [field("ODESLI_ENABLED", "Enable cross-service song links"), field("ODESLI_API_KEY", "Optional song.link API key"),
  field("CACHE_AUDIO_MAXIMUM_GIB", "Maximum audio cache (GiB)"), field("SCROBBLING_LOCAL_TRACKS_ENABLED", "Scrobble local")];
const admin = { quality: "Sound quality", storage: "Storage and speed", history: "History and scrobbling", appearance: "Appearance", alerts: "Alerts", advanced: "Advanced" };

describe("Find a setting", () => {
  it("finds song.link settings in Advanced by their plain names", () => {
    const matches = searchSettings("song.link", fields, admin);
    expect(matches.map(match => match.label)).toEqual(expect.arrayContaining(["song.link API key", "Look up songs on your other services"]));
    expect(matches.every(match => match.href.startsWith("#/settings/advanced?setting=ODESLI_"))).toBe(true);
  });

  it("opens a setting on its page and highlights it", () => {
    const [match] = searchSettings("audio cache", fields, admin);
    expect(match).toMatchObject({ page: "storage", detail: "Storage and speed", href: "#/settings/storage?setting=CACHE_AUDIO_MAXIMUM_GIB" });
  });

  it("matches cards and pages, and skips pages a listener can't open", () => {
    expect(searchSettings("theme", [], admin)[0]).toMatchObject({ page: "appearance", label: "Theme" });
    expect(searchSettings("listeners", [], { appearance: "Appearance" })).toEqual([]);
    expect(searchSettings("", fields, admin)).toEqual([]);
  });

  it("places every moved setting on its new page", () => {
    expect(settingPage("BACKEND_MUSIC_LIBRARY_IDS")).toBe("library");
    expect(settingPage("PLAYBACK_LASTFM_SIMILAR_ENABLED")).toBe("mixes");
    expect(settingPage("CORS_ALLOWED_ORIGINS")).toBe("apps");
    expect(settingPage("CHALLENGE_SOLVER_URL")).toBe("advanced");
    expect(settingPage("DEBUG_LOG_ALL_REQUESTS")).toBe("backups");
    expect(settingPage("EXPLICIT_FILTER")).toBe("people");
    expect(settingPage("SPOTIFY_IMPORT_ENABLED")).toBe("library");
  });
});
