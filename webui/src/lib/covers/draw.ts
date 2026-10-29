import type { CoverDescription } from "./cover-for";
import { LIQUID, paletteFor } from "./palettes";
import { hash, rng, alpha, type Random } from "./random";
import { label } from "./toolkit";
import { STYLES } from "./styles";
import { water } from "./water";

function liquid(ctx: CanvasRenderingContext2D, S: number, L: Random, m: CoverDescription, t: number) {
  const p = paletteFor(m.tag), colours = LIQUID[p.name];
  const image = water(S, colours, (hash(m.id) % 1000) / 37, t * 0.45 + 3);
  if (image) ctx.drawImage(image, 0, 0);
  else { ctx.fillStyle = colours[0]; ctx.fillRect(0, 0, S, S); colours.slice(1).forEach((c, i) => { const g = ctx.createRadialGradient(S * (0.3 + i * 0.25), S * (0.3 + (i % 2) * 0.4), 0, S * (0.3 + i * 0.25), S * (0.3 + (i % 2) * 0.4), S * 0.5); g.addColorStop(0, c); g.addColorStop(1, alpha(c, 0)); ctx.fillStyle = g; ctx.fillRect(0, 0, S, S); }); }
  const steady = rng(hash(m.id + "label"));
  label(ctx, S, steady, L, S * 0.06, S * 0.72, S * 0.6, [`daily mix ${m.number}`, m.tag.toLowerCase(), ...(m.artists ? [m.artists.toLowerCase()] : [])]);
}

export function drawCover(ctx: CanvasRenderingContext2D, S: number, mix: CoverDescription, t = 0) {
  const frame = t > 0 ? Math.floor(t * 6) % 3 : 0;
  const L = rng(mix.layoutSeed), J = rng(mix.wobbleSeed + frame * 7919);
  ctx.save(); ctx.clearRect(0, 0, S, S);
  if (mix.style === "liquid") liquid(ctx, S, L, mix, t);
  else STYLES[mix.style](ctx, S, L, J, mix);
  ctx.restore();
}
