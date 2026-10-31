import type { CoverDescription, CoverStyle } from "./cover-for";
import { INK, PAPER, paletteFor } from "./palettes";
import { alpha, between, type Random } from "./random";
import { DISPLAY, paper, ink, cut, blob, pen, penCircle, halftone, pasted, fitSize, label, stamp, type Point } from "./toolkit";
export type CoverDrawer = (ctx: CanvasRenderingContext2D, S: number, L: Random, J: Random, m: CoverDescription) => void;
const LASTFM = ("M10.584 17.21l-.88-2.392s-1.43 1.594-3.573 1.594c-1.897 0-3.244-1.649-3.244-4.288 0-3.382 1.704-4.591 3.381-4.591 2.42 0 3.189 1.567 3.849 3.574l.88 2.749c.88 2.666 2.529 4.81 7.285 4.81 3.409 0 5.718-1.044 5.718-3.793 0-2.227-1.265-3.381-3.63-3.931l-1.758-.385c-1.21-.275-1.567-.77-1.567-1.595 0-.934.742-1.484 1.952-1.484 1.32 0 2.034.495 2.144 1.677l2.749-.33c-.22-2.474-1.924-3.492-4.729-3.492-2.474 0-4.893.935-4.893 3.932 0 1.87.907 3.051 3.189 3.601l1.87.44c1.402.33 1.869.907 1.869 1.704 0 1.017-.99 1.43-2.86 1.43-2.776 0-3.93-1.457-4.59-3.464l-.907-2.75c-1.155-3.573-2.997-4.893-6.653-4.893C2.144 5.333 0 7.89 0 12.233c0 4.18 2.144 6.434 5.993 6.434 3.106 0 4.591-1.457 4.591-1.457z");
let lastfmPath: Path2D | undefined;

export const STYLES: Record<Exclude<CoverStyle, "liquid">, CoverDrawer> = {
  stars(ctx, S, L, J, m) { // Uncharted: a star chart drawn in pen, one new star circled.
    paper(ctx, S, PAPER.blue, L, true);
    const pts: Point[] = Array.from({ length: 14 }, () => [S * between(L, 0.1, 0.9), S * between(L, 0.08, 0.62)]);
    ink(ctx, S, "#e9e2d0", J, g => {
      pen(g, [pts[0], pts[3], pts[5], pts[8], pts[2]], S * 0.004, J, S); pen(g, [pts[1], pts[6], pts[9]], S * 0.004, J, S);
      pts.forEach(([x, y], i) => { const s = S * (i % 4 === 0 ? 0.018 : 0.009); pen(g, [[x - s, y], [x + s, y]], S * 0.004, J, S); pen(g, [[x, y - s], [x, y + s]], S * 0.004, J, S); });
    }, { blend: "source-over", specks: 500 });
    const [nx, ny] = pts[11];
    ink(ctx, S, INK.yellow, J, g => { blob(g, nx, ny, S * 0.022, J, 0.1); penCircle(g, nx, ny, S * 0.07, S * 0.006, J, S); }, { blend: "source-over" });
    ctx.fillStyle = "#e9e2d0"; const size = fitSize(ctx, m.lines[0], "700 " + DISPLAY, S * 0.86, S * 0.15);
    pasted(ctx, m.lines[0], S * 0.07, S * 0.9, size * 1.1, "700 " + DISPLAY, J);
  },
  radar(ctx, S, L, J, m) { // Just Dropped: hand-drawn rings, a stamp for each new release.
    paper(ctx, S, PAPER.cream, L);
    const cx = S * 0.58, cy = S * 0.42;
    ink(ctx, S, INK.green, J, g => { for (let i = 1; i <= 4; i++) penCircle(g, cx, cy, S * 0.09 * i, S * 0.005, J, S); pen(g, [[cx, cy], [cx + S * 0.36, cy - S * 0.2]], S * 0.006, J, S); });
    ink(ctx, S, INK.pink, J, g => { for (let i = 0; i < m.count; i++) { const a = L() * 6.283, d = S * between(L, 0.06, 0.36); blob(g, cx + Math.cos(a) * d, cy + Math.sin(a) * d, S * 0.017, J, 0.15); } });
    ink(ctx, S, INK.black, J, g => { const s = fitSize(g, m.lines.join(" "), "700 " + DISPLAY, S * 0.86, S * 0.16); pasted(g, m.lines[0], S * 0.07, S * 0.8, s * 1.15, "700 " + DISPLAY, J); pasted(g, m.lines[1], S * 0.07, S * 0.93, s * 1.15, "700 " + DISPLAY, J); });
    stamp(ctx, S, J, S * 0.76, S * 0.84, "NEW", INK.red, -0.25);
  },
  stamped(ctx, S, L, J, m) { // Heavy Rotation: the same rubber stamp, pressed over and over.
    paper(ctx, S, PAPER.white, L);
    for (let i = 0; i < 9; i++) stamp(ctx, S, J, S * between(L, 0.2, 0.8), S * between(L, 0.12, 0.88), m.lines.join(" ").toUpperCase(), i % 3 === 0 ? INK.red : INK.navy, between(L, -0.5, 0.5), between(L, 0.6, 1));
  },
  sun(ctx, S, L, J, m) { // Long Lost: a two-ink print of an old sunset, a bit faded.
    paper(ctx, S, PAPER.cream, L);
    ink(ctx, S, INK.orange, J, g => halftone(g, S, S * 0.03, 0.4, (x, y) => Math.max(0, Math.min(1, 1.15 - Math.hypot(x - S * 0.5, y - S * 0.52) / (S * 0.3)))), { alpha: 0.85 });
    ink(ctx, S, INK.pink, J, g => { for (let i = 0; i < 6; i++) cut(g, [[0, S * (0.56 + i * 0.05)], [S, S * (0.55 + i * 0.05)], [S, S * (0.575 + i * 0.05)], [0, S * (0.58 + i * 0.05)]], J, S, 0.004); }, { alpha: 0.8 });
    label(ctx, S, J, L, S * 0.08, S * 0.08, S * 0.44, [m.lines.join(" ").toLowerCase(), "side b"], { rot: -0.04 });
  },
  waves(ctx, S, L, J, m) { // Chill: wavy lines in one blue pen.
    paper(ctx, S, PAPER.white, L);
    ink(ctx, S, INK.blue, J, g => { for (let i = 0; i < 9; i++) { const base = S * (0.12 + i * 0.065), pts: Point[] = [];
      for (let x = -S * 0.02; x <= S * 1.02; x += S / 24) pts.push([x, base + Math.sin(x / S * 6.283 * 1.3 + i * 0.7) * S * 0.025]); pen(g, pts, S * 0.006, J, S); } });
    ink(ctx, S, INK.blue, J, g => { const s = S * 0.2; pasted(g, "Chill", S * 0.07, S * 0.9, s, "700 " + DISPLAY, J); });
  },
  ruled(ctx, S, L, J, m) { // Focus: a notebook page with one thing circled.
    paper(ctx, S, PAPER.white, L);
    ink(ctx, S, "#7fb3d8", J, g => { for (let i = 1; i < 16; i++) pen(g, [[0, S * i / 16], [S, S * i / 16]], S * 0.0025, J, S); }, { specks: 300 });
    ink(ctx, S, INK.red, J, g => pen(g, [[S * 0.14, 0], [S * 0.14, S]], S * 0.003, J, S), { specks: 200 });
    ink(ctx, S, INK.black, J, g => { pasted(g, "Focus", S * 0.2, S * 0.43, S * 0.2, "700 " + DISPLAY, J); });
    ink(ctx, S, INK.red, J, g => penCircle(g, S * 0.4, S * 0.37, S * 0.24, S * 0.007, J, S));
  },
  zigzag(ctx, S, L, J, m) { // Workout: cut strips, fast and slanted.
    paper(ctx, S, PAPER.cream, L);
    ink(ctx, S, INK.red, J, g => { for (let i = 0; i < 5; i++) { const y = S * (0.08 + i * 0.13); cut(g, [[-S * 0.1, y + S * 0.12], [S * 1.1, y - S * 0.08], [S * 1.1, y - S * 0.02], [-S * 0.1, y + S * 0.18]], J, S, 0.008); } });
    ink(ctx, S, INK.black, J, g => { g.save(); g.translate(S * 0.07, S * 0.9); g.transform(1, 0, -0.2, 1, 0, 0); pasted(g, "Workout", 0, 0, fitSize(g, "Workout", "700 " + DISPLAY, S * 0.8, S * 0.22), "700 " + DISPLAY, J); g.restore(); });
  },
  punched(ctx, S, L, J, m) { // Party: hole-punch confetti in three inks.
    paper(ctx, S, PAPER.white, L);
    [INK.pink, INK.yellow, INK.blue].forEach((c, k) => ink(ctx, S, c, J, g => { for (let i = 0; i < 26; i++) { const x = S * L(), y = S * L() * 0.72; if (L() < 0.65) blob(g, x, y, S * between(L, 0.015, 0.03), J, 0.08);
      else { g.save(); g.translate(x, y); g.rotate(L() * 6.28); cut(g, [[-S * 0.03, -S * 0.008], [S * 0.03, -S * 0.008], [S * 0.03, S * 0.008], [-S * 0.03, S * 0.008]], J, S, 0.002); g.restore(); } } }, { alpha: 0.85 }));
    ink(ctx, S, INK.black, J, g => pasted(g, "Party", S * 0.07, S * 0.92, S * 0.22, "700 " + DISPLAY, J));
  },
  moon(ctx, S, L, J, m) { // Sleep: a paper moon on navy paper, pencil stars.
    paper(ctx, S, PAPER.blue, L, true);
    const moon = document.createElement("canvas"); moon.width = moon.height = S; const mc = moon.getContext("2d")!; mc.fillStyle = "#f4dc8a";
    blob(mc, S * 0.62, S * 0.36, S * 0.2, J, 0.03); mc.globalCompositeOperation = "destination-out"; blob(mc, S * 0.71, S * 0.3, S * 0.18, J, 0.03);
    ctx.save(); ctx.fillStyle = alpha("#000000", 0.3); ctx.globalCompositeOperation = "source-over"; ctx.drawImage(moon, S * 0.008, S * 0.01); ctx.restore();
    ink(ctx, S, "#f4dc8a", J, g => g.drawImage(moon, 0, 0), { blend: "source-over", alpha: 1, specks: 400 });
    ink(ctx, S, "#e9e2d0", J, g => { for (let i = 0; i < 16; i++) { const x = S * between(L, 0.06, 0.94), y = S * between(L, 0.05, 0.68), s = S * between(L, 0.006, 0.014);
      pen(g, [[x - s, y], [x + s, y]], S * 0.003, J, S); pen(g, [[x, y - s], [x, y + s]], S * 0.003, J, S); } }, { blend: "source-over", specks: 200 });
    moon.width = moon.height = 0;
    ctx.fillStyle = "#e9e2d0"; pasted(ctx, "Sleep", S * 0.07, S * 0.92, S * 0.2, "700 " + DISPLAY, J);
  },
  letter(ctx, S, L, J, m) { // A saved mix: the seed's first letter, cut big, over a halftone in its genre's inks.
    const p = paletteFor(m.tag), blend = p.dark ? "source-over" : "multiply"; paper(ctx, S, p.paper, L, p.dark);
    ink(ctx, S, p.inks[1], J, g => halftone(g, S, S * 0.034, 0.79, (x, y) => 0.25 + 0.6 * (x / S) * (1 - y / S)), { blend });
    ink(ctx, S, p.inks[0], J, g => { g.font = `700 ${S * 0.95}px ${DISPLAY}`; g.save(); g.translate(S * 0.08, S * 0.82); g.rotate(between(L, -0.08, 0.05)); g.fillText(m.seed[0].toUpperCase(), 0, 0); g.restore(); }, { alpha: 0.9, blend });
    label(ctx, S, J, L, S * 0.4, S * 0.74, S * 0.54, ["mix from", m.seed.toLowerCase()], { plain: false });
  },
  lastfm(ctx, S, L, J, m) { // Last.fm: their red paper, their logo as a rubber stamp, a typed label.
    paper(ctx, S, "#d51007", L, true);
    ink(ctx, S, "#f6efe6", J, g => { g.save(); g.translate(S * 0.1, S * 0.08); g.rotate(between(L, -0.08, 0.02)); g.scale(S * 0.5 / 24, S * 0.5 / 24); g.fill(lastfmPath ??= new Path2D(LASTFM)); g.restore(); }, { blend: "source-over", alpha: 0.95, specks: 2600 });
    label(ctx, S, J, L, S * 0.07, S * 0.66, S * 0.7, [...m.lines.map(l => l.toLowerCase()), m.sub.toLowerCase()]);
  },
  listenbrainz(ctx, S, L, J, m) { // ListenBrainz: their colours as cut paper; their name typed (no logo drawn).
    paper(ctx, S, "#353070", L, true);
    ink(ctx, S, "#eb743b", J, g => { cut(g, [[S * 0.1, S * 0.1], [S * 0.62, S * 0.14], [S * 0.56, S * 0.56], [S * 0.12, S * 0.5]], J, S, 0.01); }, { blend: "source-over" });
    ink(ctx, S, "#9a93e6", J, g => blob(g, S * 0.66, S * 0.42, S * 0.17, J, 0.05), { blend: "source-over", alpha: 0.9 });
    label(ctx, S, J, L, S * 0.07, S * 0.66, S * 0.72, ["listenbrainz", ...m.lines.map(l => l.toLowerCase())], { plain: false });
  },
};

