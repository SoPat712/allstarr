import type { ConfigField } from "$lib/api";
import { settingHelp, settingNames } from "$lib/settings-fields";

export type SettingMatch = { key: string; label: string; detail: string; href: string; page: string };

// Which page holds each server setting.
const pageByKey: [RegExp, string][] = [
  [/^(PLAYBACK_START_PREFERENCE|PLAYBACK_PREPARE_UPCOMING_SONGS|EXPLICIT_FILTER|AUDIO_QUALITY|MATCHING_LOCAL_PREFERENCE_PERCENT)$/, "playback"],
  [/^(BACKEND_TYPE|BACKEND_MUSIC_LIBRARY_IDS|DOWNLOAD_MODE|STORAGE_MODE|LIBRARY_DOWNLOAD_PATH|LIBRARY_KEPT_PATH|PLAYLISTS_DIRECTORY)$/, "library"],
  [/^PLAYBACK_(LASTFM|LISTENBRAINZ)_SIMILAR_ENABLED$/, "discovery"],
  [/^(SCROBBLING_|EVENT_LOG_)/, "history"],
  [/^(LISTENERS_CAN_CONNECT_OWN_ACCOUNTS|CLIENT_API_ENABLED|PUBLIC_LISTENER_URL|ADMIN_|CORS_)/, "people"],
  [/^(CACHE_|PROVIDER_STREAMS_PER_LISTENER|MEDIA_MAXIMUM_CONCURRENT_CONVERSIONS)/, "storage"],
  [/^DEBUG_/, "health"],
  [/^(ODESLI_|EXTENSIONS_ALLOW_REMOTE_INSTALL|CHALLENGE_SOLVER_)/, "sources"],
];

export const settingPage = (key: string) => pageByKey.find(([pattern]) => pattern.test(key))?.[1] ?? null;

// Settings that live in their own cards rather than the server schema.
const cards = [
  { key: "theme", label: "Theme", page: "appearance", words: "dark light colours colors look" },
  { key: "your-quality", label: "Your highest quality", page: "playback", words: "listening preferences explicit labels" },
  { key: "source-order", label: "Source order", page: "playback", words: "routing priority services order" },
  { key: "listeners", label: "Listeners", page: "people", words: "users people trusted playlist editing" },
  { key: "sign-in", label: "Sign-in and single sign-on", page: "people", words: "oidc authentik login media server access" },
  { key: "audiomuse", label: "AudioMuse sound analysis", page: "discovery", words: "mood mixes sound analysis" },
  { key: "library-folder", label: "Library folder", page: "adding-to-library", words: "where who promote naming tags" },
  { key: "scrobbling-accounts", label: "Scrobbling accounts", page: "history", words: "last.fm lastfm listenbrainz" },
  { key: "alert-channels", label: "Alert channels", page: "alerts", words: "email ntfy notifications disk space" },
  { key: "backup-schedule", label: "Backup schedule", page: "backups", words: "daily keep restore" },
];

const words = (text: string) => text.toLowerCase().normalize("NFKD").replace(/[^a-z0-9.]+/g, " ").trim();

export function searchSettings(query: string, fields: ConfigField[], pageLabels: Record<string, string>, limit = 8): SettingMatch[] {
  const terms = words(query).split(" ").filter(Boolean);
  if (!terms.length) return [];
  const candidates = [
    ...fields.map(field => ({
      key: field.key, label: settingNames[field.key] ?? field.label, page: settingPage(field.key),
      words: `${settingHelp[field.key] ?? field.helpText ?? ""} ${field.label}${field.key.startsWith("ODESLI_") ? " song.link odesli" : ""}`,
    })),
    ...cards,
    ...Object.entries(pageLabels).map(([page, label]) => ({ key: `page-${page}`, label, page, words: "" })),
  ].filter((item): item is typeof item & { page: string } => item.page !== null && (item.page === "sources" || item.page in pageLabels));
  return candidates
    .map(item => {
      const title = words(item.label), all = `${title} ${words(item.words)}`;
      if (!terms.every(term => all.includes(term))) return null;
      const score = (title.startsWith(terms[0]) ? 4 : 0) + terms.filter(term => title.includes(term)).length * 2 + (item.key.startsWith("page-") ? 1 : 0);
      return { item, score };
    })
    .filter(result => result !== null)
    .sort((left, right) => right.score - left.score || left.item.label.localeCompare(right.item.label))
    .slice(0, limit)
    .map(({ item }) => ({
      key: item.key,
      label: item.label,
      page: item.page,
      detail: item.page === "sources" ? "Sources › Helpers for every source" : pageLabels[item.page],
      href: item.page === "sources" ? "#/integrations/services" : `#/settings/${item.page}${item.key.startsWith("page-") ? "" : `?setting=${encodeURIComponent(item.key)}`}`,
    }));
}
