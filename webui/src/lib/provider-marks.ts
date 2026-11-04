import { adminUrl } from "./admin-url";
import type { ProviderDefinition } from "./api";

const aliases: Record<string, string> = {
  amazon: "amazonmusic", appledownload: "applemusic", applemusicdownload: "applemusic",
  applemusickit: "applemusic", spotifyweb: "spotify", tidalweb: "tidal", qobuzweb: "qobuz",
  ytmusic: "youtubemusic", ytmusicspotiflac: "youtubemusic", youtube: "youtubemusic",
  melonmusic: "melon", geniemusic: "genie", bugsmusic: "bugs",
  local: "jellyfin", native: "jellyfin", musicbrainzenrichment: "musicbrainz",
};
const legacyFiles: Record<string, string> = Object.fromEntries([
  "allstarr", "amazonmusic", "applemusic", "artistgrid", "deezer", "genius", "jellyfin", "koito", "lastfm",
  "listenbrainz", "lrclib", "lrcred", "lyricsplus", "monochrome", "musicbrainz", "musixmatch",
  "paxsenix", "qobuz", "soundcloud", "spotify", "tidal", "unison", "youtubemusic",
].map(brand => [brand, `${brand}.svg`]));
legacyFiles.multiscrobbler = "multi-scrobbler.svg";
legacyFiles.spicylyrics = "spicylyrics.ico";
const generic = new Set(["extension", "lyrics"]);
const markless = new Set(["mediaserverlyrics", "keptlrc"]);
const key = (value: string) => {
  const plain = value.toLowerCase().replace(/^spotiflac-/, "").replace(/[^a-z0-9]/g, "");
  return aliases[plain] ?? plain;
};

export function providerMarkSources(id: string, definition?: ProviderDefinition, label = definition?.name ?? id): string[] {
  const icon = definition?.icon && !generic.has(definition.icon)
    ? definition.icon
    : definition?.icon && generic.has(definition.icon) || !label ? key(id) : key(label);
  if (markless.has(icon)) return [];

  const supplied = definition?.logoUrl;
  if (supplied && /\/[^/]*-cover(?:-\d+)?\.png(?:[?#]|$)/.test(supplied)) return [adminUrl(supplied)];
  const variant = /^spotiflac-/i.test(id);
  const localFile = supplied?.match(/^\/images\/providers\/([^/?#]+)\.(?:svg|webp|ico)$/);
  const colorFile = supplied?.match(/^\/images\/providers\/brands\/([^/?#]+)\/color\.(?:svg|png)$/);
  const suppliedBrand = key(localFile?.[1] ?? colorFile?.[1] ?? "");
  const identity = key(id);
  let brand = "";
  if (variant) {
    brand = legacyFiles[identity] || ["melon", "genie", "bugs"].includes(identity)
      ? identity : id.toLowerCase().replace(/^spotiflac-/, "").replace(/[^a-z0-9-]/g, "");
  } else if (suppliedBrand && legacyFiles[suppliedBrand]) {
    brand = suppliedBrand;
  } else if (!supplied) {
    brand = legacyFiles[identity] ? identity : legacyFiles[key(icon)] ? key(icon) : "";
  }

  const legacy = `/images/providers/${brand ? legacyFiles[brand] ?? `${identity}.svg` : `${encodeURIComponent(icon || id.toLowerCase())}.svg`}`;
  const candidates = brand
    ? [`/images/providers/brands/${brand}/color.svg`, `/images/providers/brands/${brand}/color.png`, supplied, supplied && localFile ? undefined : legacy]
    : [supplied || legacy];
  return [...new Set(candidates.filter((value): value is string => Boolean(value)).map(adminUrl))];
}
