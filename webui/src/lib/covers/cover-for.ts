import type { MixCard, MixSong } from "../api";
import { hash } from "./palettes";

const compact = (value: string) => value.toLowerCase().replace(/[^a-z0-9]/g, "");

export function artworkFor(mix: MixCard, songs?: MixSong[]) {
  return mix.coverArtwork.length || !songs ? mix.coverArtwork : songs.slice(0, 1).map(item => item.song.id);
}

// The same colours the server's cover uses (MixCoverRenderer.Hsv), so a mix without song art looks alike everywhere.
export function hsv(hue: number, saturation: number, value: number) {
  hue %= 360;
  const chroma = value / 100 * (saturation / 100);
  const x = chroma * (1 - Math.abs(hue / 60 % 2 - 1));
  const match = value / 100 - chroma;
  const [red, green, blue] = hue < 60 ? [chroma, x, 0] : hue < 120 ? [x, chroma, 0] : hue < 180 ? [0, chroma, x]
    : hue < 240 ? [0, x, chroma] : hue < 300 ? [x, 0, chroma] : [chroma, 0, x];
  return `#${[red, green, blue].map(part => Math.round((part + match) * 255).toString(16).padStart(2, "0")).join("")}`;
}

// Pale, bright and deep: saturation and brightness of the first stop, then of the second.
const tones = [[42, 98, 72, 86], [86, 96, 82, 62], [78, 62, 70, 34]];

// Two stops seeded from the mix, drawn the same as the server's cover (MixCoverRenderer.Bold): a hue anywhere on the
// wheel, a second hue 40–75° away, a tone (pale, bright or deep) and an angle.
export function bold(id: string) {
  const seed = hash(id);
  const hue = ((seed % 12) * 30 + (seed >>> 4) % 17 + 352) % 360;
  let swing = 40 + (seed >>> 9) % 36;
  if (((seed >>> 15) & 1) === 0) swing = -swing;
  let tone = (seed >>> 20) % 3;
  if (tone === 2 && hue >= 35 && hue <= 80) tone = 1;
  let [saturation, value, nextSaturation, nextValue] = tones[tone];
  if (tone === 1 && hue >= 80 && hue <= 160) [saturation, value, nextSaturation] = [saturation - 22, value - 12, nextSaturation - 18];
  return {
    stops: [hsv(hue + 360, saturation, value), hsv(hue + swing + 360, nextSaturation, nextValue)],
    // The server measures angles from the right going clockwise; CSS measures from the top.
    angle: ((seed >>> 16) % 360 + 90) % 360,
  };
}

export function coverFor(mix: MixCard) {
  if (mix.kind === "external") {
    const provider = compact(mix.sourceLabel?.trim() || mix.name);
    if (provider.includes("lastfm")) return "linear-gradient(135deg, #d51007, #f6efe6)";
    if (provider.includes("listenbrainz")) return "linear-gradient(135deg, #353070, #eb743b, #9a93e6)";
  }
  const { stops, angle } = bold(mix.id);
  return `linear-gradient(${angle}deg, ${stops.join(", ")})`;
}
