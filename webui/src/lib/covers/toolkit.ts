import { INK } from "./palettes";
import { alpha, between, type Random } from "./random";
export type Point = [number, number];
export const DISPLAY = '"Archivo Narrow", "Arial Narrow", sans-serif';
export const TYPED = '"Courier Prime", "Courier New", monospace';

export function paper(ctx: CanvasRenderingContext2D, S: number, colour: string, r: Random, dark = false) {
  ctx.fillStyle = colour; ctx.fillRect(0, 0, S, S);
  for (let i = 0; i < 26; i++) { const x = r() * S, y = r() * S, rad = S * between(r, 0.1, 0.35); const g = ctx.createRadialGradient(x, y, 0, x, y, rad);
    const tone = r() < 0.5 ? (dark ? "#000000" : "#6b5a3a") : "#ffffff"; g.addColorStop(0, alpha(tone, 0.05)); g.addColorStop(1, alpha(tone, 0)); ctx.fillStyle = g; ctx.fillRect(0, 0, S, S); }
  for (let i = 0; i < 900; i++) { ctx.fillStyle = alpha(dark ? "#ffffff" : "#3a2f20", between(r, 0.03, 0.12)); ctx.fillRect(r() * S, r() * S, S * between(r, 0.001, 0.003), S * between(r, 0.001, 0.003)); }
  ctx.lineWidth = S * 0.0012;
  for (let i = 0; i < 50; i++) { const x = r() * S, y = r() * S, a = r() * 6.28, l = S * between(r, 0.01, 0.04); ctx.strokeStyle = alpha(dark ? "#ffffff" : "#5a4a30", 0.07);
    ctx.beginPath(); ctx.moveTo(x, y); ctx.quadraticCurveTo(x + Math.cos(a + 0.6) * l / 2, y + Math.sin(a + 0.6) * l / 2, x + Math.cos(a) * l, y + Math.sin(a) * l); ctx.stroke(); }
}

// Prints one ink layer: draws into its own sheet, knocks out specks where the ink didn't take, then lays it down slightly out of register.
export function ink(ctx: CanvasRenderingContext2D, S: number, colour: string, J: Random, drawFn: (g: CanvasRenderingContext2D) => void, opts: { specks?: number; blend?: GlobalCompositeOperation; alpha?: number } = {}) {
  const sheet = document.createElement("canvas"); sheet.width = sheet.height = S; const g = sheet.getContext("2d")!;
  g.fillStyle = colour; g.strokeStyle = colour; drawFn(g);
  g.globalCompositeOperation = "destination-out";
  for (let i = 0; i < (opts.specks ?? 1400); i++) { g.fillStyle = `rgba(0,0,0,${between(J, 0.15, 0.7)})`; g.beginPath(); g.arc(J() * S, J() * S, S * between(J, 0.0008, 0.0035), 0, 6.283); g.fill(); }
  ctx.save(); ctx.globalCompositeOperation = opts.blend || "multiply"; ctx.globalAlpha = opts.alpha ?? 0.93;
  ctx.drawImage(sheet, S * between(J, -0.006, 0.006), S * between(J, -0.006, 0.006)); ctx.restore(); sheet.width = sheet.height = 0;
}

// A shape cut out with scissors: corners never quite square, edges never quite straight.
export function cut(g: CanvasRenderingContext2D, points: Point[], J: Random, S: number, rough = 0.006) {
  g.beginPath();
  points.forEach(([x, y], i) => {
    const next = points[(i + 1) % points.length]; const jx = x + between(J, -rough, rough) * S, jy = y + between(J, -rough, rough) * S;
    if (i === 0) g.moveTo(jx, jy); else g.lineTo(jx, jy);
    for (let k = 1; k < 4; k++) { const f = k / 4; g.lineTo(x + (next[0] - x) * f + between(J, -rough, rough) * S * 0.4, y + (next[1] - y) * f + between(J, -rough, rough) * S * 0.4); }
  });
  g.closePath(); g.fill();
}
export function blob(g: CanvasRenderingContext2D, cx: number, cy: number, rad: number, J: Random, wobble = 0.04, fill = true) {
  g.beginPath(); const n = 34, ph = J() * 6.28;
  for (let i = 0; i <= n; i++) { const a = i / n * 6.283, rr = rad * (1 + Math.sin(a * 3 + ph) * wobble * 0.5 + between(J, -wobble, wobble) * 0.5);
    const x = cx + Math.cos(a) * rr, y = cy + Math.sin(a) * rr; if (i === 0) g.moveTo(x, y); else g.lineTo(x, y); }
  g.closePath(); if (fill) g.fill(); else g.stroke();
}
// A pen line: wanders a little and changes weight with pressure.
export function pen(g: CanvasRenderingContext2D, pts: Point[], width: number, J: Random, S: number) {
  for (let pass = 0; pass < 2; pass++) {
    g.lineCap = "round"; g.lineJoin = "round";
    for (let i = 1; i < pts.length; i++) {
      g.lineWidth = width * between(J, 0.7, 1.15) * (pass ? 0.5 : 1); g.globalAlpha = pass ? 0.5 : 1;
      g.beginPath(); g.moveTo(pts[i - 1][0] + between(J, -1, 1) * S * 0.002, pts[i - 1][1] + between(J, -1, 1) * S * 0.002); g.lineTo(pts[i][0] + between(J, -1, 1) * S * 0.002, pts[i][1] + between(J, -1, 1) * S * 0.002); g.stroke();
    }
  }
  g.globalAlpha = 1;
}
export function penCircle(g: CanvasRenderingContext2D, cx: number, cy: number, rad: number, width: number, J: Random, S: number) { const pts: Point[] = [], n = 48, start = J() * 6.28, over = between(J, 0.05, 0.3);
  for (let i = 0; i <= n; i++) { const a = start + i / n * (6.283 + over), rr = rad * (1 + between(J, -0.015, 0.015)); pts.push([cx + Math.cos(a) * rr, cy + Math.sin(a) * rr]); } pen(g, pts, width, J, S); }
export function halftone(g: CanvasRenderingContext2D, S: number, cell: number, angle: number, amount: (x: number, y: number) => number) {
  g.save(); g.translate(S / 2, S / 2); g.rotate(angle);
  for (let y = -S; y < S; y += cell) for (let x = -S; x < S; x += cell) {
    const wx = S / 2 + x * Math.cos(angle) - y * Math.sin(angle), wy = S / 2 + x * Math.sin(angle) + y * Math.cos(angle), v = amount(wx, wy);
    if (v > 0.02) { g.beginPath(); g.arc(x, y, cell * 0.55 * Math.sqrt(v), 0, 6.283); g.fill(); }
  }
  g.restore();
}
// Letters pasted by hand: each one a touch rotated and off the line.
export function fontStr(size: number, font: string) { const m = /^(\d{3}) (.*)$/.exec(font); return m ? `${m[1]} ${size}px ${m[2]}` : `${size}px ${font}`; }
export function pasted(g: CanvasRenderingContext2D, text: string, x: number, y: number, size: number, font: string, J: Random, wobble = 1) {
  g.font = fontStr(size, font); g.textBaseline = "alphabetic"; let cx = x;
  for (const ch of text) { const w = g.measureText(ch).width; g.save(); g.translate(cx + w / 2, y + between(J, -1, 1) * size * 0.03 * wobble); g.rotate(between(J, -0.035, 0.035) * wobble); g.fillText(ch, -w / 2, 0); g.restore(); cx += w; }
  return cx - x;
}
export function fitSize(g: CanvasRenderingContext2D, text: string, font: string, max: number, size: number) { let s = size; g.font = fontStr(s, font); while (g.measureText(text).width > max && s > 3) { s -= 0.5; g.font = fontStr(s, font); } return s; }
// A torn paper label with typed text on it.
export function label(ctx: CanvasRenderingContext2D, S: number, J: Random, L: Random, x: number, y: number, w: number, lines: string[], opts: { rot?: number; paper?: string; ink?: string; plain?: boolean } = {}) {
  const lh = S * 0.052, h = lh * lines.length + S * 0.045, rot = opts.rot ?? between(L, -0.05, 0.03);
  ctx.save(); ctx.translate(x, y); ctx.rotate(rot);
  ctx.fillStyle = alpha("#000000", 0.18); cut(ctx, [[S * 0.006, S * 0.008], [w + S * 0.006, S * 0.008], [w + S * 0.006, h + S * 0.008], [S * 0.006, h + S * 0.008]], J, S, 0.003);
  ctx.fillStyle = opts.paper || "#fbf7ee"; cut(ctx, [[0, 0], [w, 0], [w, h], [0, h]], J, S, 0.003);
  ctx.fillStyle = opts.ink || INK.black;
  lines.forEach((line, i) => { const bold = i === 0 && !opts.plain;     const size = fitSize(ctx, line, (bold ? "700 " : "") + TYPED, w - S * 0.05, S * 0.042); ctx.globalAlpha = between(J, 0.82, 0.95);
    pasted(ctx, line, S * 0.025, S * 0.022 + lh * (i + 1) - lh * 0.25, size, (bold ? "700 " : "") + TYPED, J, 0.35); });
  ctx.restore(); ctx.globalAlpha = 1;
}

export function stamp(ctx: CanvasRenderingContext2D, S: number, J: Random, x: number, y: number, text: string, colour: string, rot: number, strength = 1) {
  ink(ctx, S, colour, J, g => {
    g.save(); g.translate(x, y); g.rotate(rot); g.font = `700 ${S * 0.085}px ${DISPLAY}`; const w = g.measureText(text).width + S * 0.06, h = S * 0.12;
    g.lineWidth = S * 0.008; g.strokeStyle = colour; g.strokeRect(-w / 2, -h / 2, w, h); g.textAlign = "center"; g.textBaseline = "middle"; g.fillText(text, 0, S * 0.004); g.restore();
  }, { specks: 2600, alpha: 0.75 * strength });
}

