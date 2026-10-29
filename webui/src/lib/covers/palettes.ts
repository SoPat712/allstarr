import { hash } from "./random";
export type Palette = { paper: string; inks: string[]; type: string; dark?: boolean };
export const INK = { pink: "#ff48b0", yellow: "#ffd400", blue: "#0078bf", teal: "#00838a", orange: "#ff6c2f", red: "#e8443a", green: "#00a95c",
  purple: "#765ba7", burgundy: "#914e72", navy: "#2e3b55", rust: "#b8602d", olive: "#6f7a3a", black: "#2a2826", grey: "#8d8a85", white: "#f7f2e8" };
export const PAPER = { cream: "#f1e8d6", kraft: "#d6bf98", white: "#f6f2ea", pinkish: "#f6e3e1", black: "#1f1d1c", blue: "#1d2a52" };

// Each genre: paper, three inks, and the ink used for type.
export const PALETTES: Record<string, Palette> = {
  "R&B":        { paper: PAPER.cream, inks: [INK.burgundy, INK.pink, INK.orange], type: INK.black },
  "Hip-hop":    { paper: PAPER.cream, inks: [INK.yellow, INK.red, INK.black], type: INK.black },
  "Pop":        { paper: PAPER.pinkish, inks: [INK.pink, INK.blue, INK.yellow], type: INK.navy },
  "Indie rock": { paper: PAPER.kraft, inks: [INK.teal, INK.red, INK.navy], type: INK.black },
  "Electronic": { paper: PAPER.black, inks: [INK.pink, "#3fb6ff", INK.yellow], type: INK.white, dark: true },
  "Jazz":       { paper: PAPER.cream, inks: [INK.blue, INK.black, INK.orange], type: INK.black },
  "Folk":       { paper: PAPER.kraft, inks: [INK.green, INK.rust, INK.black], type: INK.black },
  "Metal":      { paper: PAPER.black, inks: ["#bdb8b0", INK.red, "#5c5955"], type: INK.white, dark: true },
  "Latin":      { paper: PAPER.white, inks: [INK.orange, INK.teal, INK.pink], type: INK.black },
  "Classical":  { paper: PAPER.white, inks: [INK.olive, INK.rust, INK.black], type: INK.black },
};
export const GENRE_WORDS = [["r&b", "R&B"], ["soul", "R&B"], ["funk", "R&B"], ["hip", "Hip-hop"], ["rap", "Hip-hop"], ["trap", "Hip-hop"],
  ["k-pop", "Pop"], ["pop", "Pop"], ["indie", "Indie rock"], ["rock", "Indie rock"], ["punk", "Indie rock"], ["alt", "Indie rock"],
  ["edm", "Electronic"], ["house", "Electronic"], ["techno", "Electronic"], ["electr", "Electronic"], ["dance", "Electronic"],
  ["jazz", "Jazz"], ["blues", "Jazz"], ["folk", "Folk"], ["country", "Folk"], ["acoustic", "Folk"], ["metal", "Metal"], ["hardcore", "Metal"],
  ["latin", "Latin"], ["reggaeton", "Latin"], ["salsa", "Latin"], ["classical", "Classical"], ["piano", "Classical"], ["ambient", "Classical"]];

export function paletteFor(tag: string) {
  const lower = (tag || "").toLowerCase();
  const hit = GENRE_WORDS.find(([word]) => lower.includes(word));
  const key = hit ? hit[1] : Object.keys(PALETTES)[hash(lower) % Object.keys(PALETTES).length];
  return { name: key, ...PALETTES[key] };
}

export const LIQUID: Record<string, string[]> = {
  "R&B": ["#2a0b2e", "#c2366b", "#f29e4c", "#9b6bd6"], "Hip-hop": ["#15110c", "#e8a400", "#c8341c", "#6b4a2b"],
  "Pop": ["#ff6fae", "#ffd166", "#5ee7ff", "#a98bff"], "Indie rock": ["#16302f", "#d9653b", "#2f9c95", "#e8d8b0"],
  "Electronic": ["#070b26", "#3a5bff", "#c04cff", "#9dff3a"], "Jazz": ["#0c1a36", "#c99a3b", "#8a2f2f", "#3a6ea5"],
  "Folk": ["#3a2a17", "#c9762f", "#7f9f5f", "#efe0c2"], "Metal": ["#0c0c0e", "#8a1c1c", "#5d636e", "#c0c4cc"],
  "Latin": ["#ff5a3c", "#ffd23f", "#00a6a6", "#7b2cbf"], "Classical": ["#e9e2d2", "#2f4f3a", "#b08d57", "#8a8780"],
};
