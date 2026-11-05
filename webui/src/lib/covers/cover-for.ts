import type { MixCard, MixSong } from "../api";
import { hash, INK, LIQUID, PAPER, PALETTES, paletteFor } from "./palettes";

const colours: Record<string, string[]> = {
  "discover-weekly": [PAPER.blue, "#e9e2d0", INK.yellow],
  "release-radar": [PAPER.cream, INK.green, INK.pink, INK.red],
  "on-repeat": [PAPER.white, INK.navy, INK.red],
  rewind: [PAPER.cream, INK.orange, INK.pink],
  chill: [PAPER.white, INK.blue],
  focus: [PAPER.white, "#7fb3d8", INK.red, INK.black],
  workout: [PAPER.cream, INK.red, INK.black],
  party: [PAPER.white, INK.pink, INK.yellow, INK.blue],
  sleep: [PAPER.blue, "#f4dc8a", "#e9e2d0"],
};
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

export function coverFor(mix: MixCard, songs?: MixSong[]) {
  let tag = Object.keys(PALETTES)[hash(mix.id) % Object.keys(PALETTES).length];
  let stops = colours[mix.kind];
  if (/^daily-[1-4]$/.test(mix.kind)) {
    const metadata = /^Made for you\s*·\s*([^·]*)/.exec(mix.description.trim())?.[1]?.trim();
    tag = (metadata && !/^Blend [1-4]$/i.test(metadata) ? metadata : "") || /^(.+?)\s+Blend$/i.exec(mix.name.trim())?.[1]?.trim() || tag;
    stops = LIQUID[paletteFor(tag).name];
  } else if (mix.kind === "external") {
    const provider = compact(mix.sourceLabel?.trim() || mix.name);
    if (provider.includes("lastfm")) stops = ["#d51007", "#f6efe6"];
    else if (provider.includes("listenbrainz")) stops = ["#353070", "#eb743b", "#9a93e6"];
  }
  if (!stops) {
    const palette = paletteFor(mix.kind === "custom" ? songGenre(songs) || tag : tag);
    stops = [palette.paper, ...palette.inks];
  }
  return `linear-gradient(135deg, ${stops.join(", ")})`;
}
