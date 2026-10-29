export type Random = () => number;
export function hash(text: string) { let h = 2166136261; for (const ch of text) { h ^= ch.codePointAt(0)!; h = Math.imul(h, 16777619); } return h >>> 0; }
export function rng(seed: number) { let a = seed; return () => { a = (a + 0x6d2b79f5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
export function alpha(hex: string, a: number) { const n = parseInt(hex.slice(1), 16); return `rgba(${n >> 16 & 255},${n >> 8 & 255},${n & 255},${a})`; }
export const between = (r: Random, a: number, b: number) => a + r() * (b - a);

