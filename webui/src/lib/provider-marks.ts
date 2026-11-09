import { adminUrl } from "./admin-url";
import type { ProviderDefinition } from "./api";

const aliases: Record<string, string> = {
  amazon: "amazonmusic", appledownload: "applemusic", applemusicdownload: "applemusic",
  applemusickit: "applemusic", spotifyweb: "spotify", tidalweb: "tidal", qobuzweb: "qobuz",
  ytmusic: "youtubemusic", ytmusicspotiflac: "youtubemusic", youtube: "youtubemusic",
  melonmusic: "melon", geniemusic: "genie", bugsmusic: "bugs",
  neteaselyrics: "netease", qqlyrics: "qqmusic", kugoulyrics: "kugou",
  opensubsonic: "subsonic", local: "jellyfin", native: "jellyfin", musicbrainzenrichment: "musicbrainz",
  audiomuseai: "audiomuse",
};
const colorFiles: Record<string, string> = Object.fromEntries([
  "allstarr", "amazonmusic", "applemusic", "artistgrid", "deezer", "genius", "jellyfin", "navidrome", "subsonic", "listenbrainz",
  "lrcmux", "lrcred", "netease", "lyricsplus", "monochrome", "multiscrobbler", "musicbrainz", "musixmatch", "paxsenix",
  "qobuz", "soundcloud", "spicylyrics", "spotify", "tidal", "unison", "youtubemusic",
].map(brand => [brand, "color.svg"]));
for (const brand of ["amll", "audiomuse", "bugs", "genie", "jiosaavn", "koito", "kugou", "lastfm", "lrclib", "melon", "qqmusic"])
  colorFiles[brand] = "color.png";
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
    brand = colorFiles[identity]
      ? identity : id.toLowerCase().replace(/^spotiflac-/, "").replace(/[^a-z0-9-]/g, "");
  } else if (suppliedBrand && colorFiles[suppliedBrand]) {
    brand = suppliedBrand;
  } else if (!supplied) {
    brand = colorFiles[identity] ? identity : colorFiles[key(icon)] ? key(icon) : "";
  }

  const legacy = `/images/providers/${brand ? `${identity}.svg` : `${encodeURIComponent(icon || id.toLowerCase())}.svg`}`;
  const fallback = `/images/providers/${brand === "multiscrobbler" ? "multi-scrobbler.svg" : brand === "spicylyrics" ? "spicylyrics.ico" : `${brand}.svg`}`;
  const candidates = colorFiles[brand]
    ? [`/images/providers/brands/${brand}/${colorFiles[brand]}`, !colorFile ? supplied : undefined, fallback]
    : brand
      ? [`/images/providers/brands/${brand}/color.svg`, `/images/providers/brands/${brand}/color.png`, supplied, supplied && localFile ? undefined : legacy]
      : [supplied || legacy];
  return [...new Set(candidates.filter((value): value is string => Boolean(value)).map(adminUrl))];
}
