import type { MixCard, MixSong } from "../api";
import { mixName } from "../for-you";
import { PALETTES } from "./palettes";
import { hash } from "./random";

export type CoverStyle = "liquid" | "stars" | "radar" | "stamped" | "sun" | "waves" | "ruled" | "zigzag" | "punched" | "moon" | "letter" | "lastfm" | "listenbrainz";
export type CoverDescription = {
  id: string;
  style: CoverStyle;
  number: string;
  tag: string;
  artists: string;
  seed: string;
  lines: string[];
  sub: string;
  count: number;
  layoutSeed: number;
  wobbleSeed: number;
};

const kinds: Record<string, [CoverStyle, string[]]> = {
  "discover-weekly": ["stars", ["Discover", "Weekly"]],
  "release-radar": ["radar", ["Release", "Radar"]],
  "on-repeat": ["stamped", ["On", "Repeat"]],
  rewind: ["sun", ["Rewind"]],
  chill: ["waves", ["Chill"]],
  focus: ["ruled", ["Focus"]],
  workout: ["zigzag", ["Workout"]],
  party: ["punched", ["Party"]],
  sleep: ["moon", ["Sleep"]],
};
const paletteNames = Object.keys(PALETTES);
const compact = (value: string) => value.toLowerCase().replace(/[^a-z0-9]/g, "");

function songGenre(songs: MixSong[] = []) {
  const counts = new Map<string, { name: string; count: number }>();
  for (const { song } of songs) {
    const unique = new Map([...song.genres ?? [], song.genre ?? ""].map(tag => [tag.trim().toLowerCase(), tag.trim()]));
    for (const [key, name] of unique) if (key) {
      const entry = counts.get(key) ?? { name, count: 0 };
      entry.count++; counts.set(key, entry);
    }
  }
  return [...counts].sort(([a, x], [b, y]) => y.count - x.count || a.localeCompare(b))[0]?.[1].name;
}

export function coverFor(mix: MixCard, songs?: MixSong[]): CoverDescription {
  const name = mix.kind === "external"
    ? mixName(mix.name.replace(/\s*·\s*/g, " · ").replace(/\b(7day|1month|3month|12month|overall)\b/gi, value => value.toLowerCase())).trim()
    : mix.name.trim();
  const seed = name.replace(/\s+mix$/i, "").trim() || "Mix";
  const cover: CoverDescription = {
    id: mix.id, style: "letter", number: "", tag: paletteNames[hash(mix.id) % paletteNames.length], artists: "", seed,
    lines: [seed], sub: "", count: songs ? Math.min(14, songs.length) : 9, layoutSeed: 0, wobbleSeed: 0,
  };
  if (mix.kind === "custom") cover.tag = songGenre(songs) || cover.tag;
  const daily = /^daily-([1-4])$/.exec(mix.kind);
  const preset = Object.prototype.hasOwnProperty.call(kinds, mix.kind) ? kinds[mix.kind] : undefined;
  if (daily) {
    const label = /^Daily Mix\s+\d+(?:\s*·\s*([^·]*))?(?:\s*·\s*(.*))?$/i.exec(mix.name.trim());
    cover.style = "liquid";
    cover.number = daily[1];
    cover.tag = label?.[1]?.trim() || cover.tag;
    cover.artists = label?.[2]?.trim() || "";
    cover.lines = ["Daily", "Mix"];
  } else if (preset) {
    cover.style = preset[0];
    cover.lines = [...preset[1]];
  } else if (mix.kind === "external") {
    const provider = compact(mix.sourceLabel?.trim() || mix.name);
    if (provider.includes("lastfm")) {
      cover.style = "lastfm";
      if (/loved/i.test(name)) {
        cover.lines = ["Loved", "Tracks"];
        cover.sub = "Your hearts on Last.fm";
      } else if (/similar/i.test(name)) {
        cover.lines = ["Similar", "Artists"];
        cover.sub = "Like your top artists";
      } else if (/top tracks/i.test(name)) {
        cover.lines = ["Top Tracks"];
        cover.sub = name.split("·").slice(1).join(" · ").trim() || "Your listening on Last.fm";
      } else {
        cover.lines = name.replace(/last[\s._-]*fm/gi, "").split("·").map(value => value.trim()).filter(Boolean);
      }
    } else if (provider.includes("listenbrainz")) {
      cover.style = "listenbrainz";
      cover.lines = /weekly exploration/i.test(name) ? ["Weekly", "Exploration"] : /weekly jams/i.test(name) ? ["Weekly Jams"]
        : name.replace(/listen[\s._-]*brainz/gi, "").replace(/created for you/gi, "").split("·").map(value => value.trim()).filter(Boolean);
    }
    if (!cover.lines.length) cover.lines = ["Your", "Mix"];
  }
  if (cover.style !== "letter" && cover.style !== "liquid") cover.tag = "";
  cover.layoutSeed = hash((cover.style === "letter" ? cover.seed : mix.id) + cover.tag);
  cover.wobbleSeed = hash(cover.style === "letter" ? cover.seed : mix.id);
  return cover;
}
