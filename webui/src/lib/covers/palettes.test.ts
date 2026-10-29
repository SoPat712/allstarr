import { describe, expect, it } from "vitest";
import { GENRE_WORDS, LIQUID, PALETTES, paletteFor } from "./palettes";
import { hash, rng } from "./random";

describe("cover palettes", () => {
  it.each(GENRE_WORDS)("matches %s to %s", (word, name) => {
    expect(paletteFor(word.toUpperCase()).name).toBe(name);
  });
  it("preserves the first matching genre when tags overlap", () => {
    expect(paletteFor("indie pop").name).toBe("Pop");
    expect(paletteFor("hardcore metal").name).toBe("Metal");
    expect(paletteFor("soul jazz").name).toBe("R&B");
  });
  it.each(["", "unfamiliar genre"])("falls back deterministically for %s", tag => {
    expect(paletteFor(tag).name).toBe(Object.keys(PALETTES)[hash(tag) % Object.keys(PALETTES).length]);
    expect(paletteFor(tag)).toEqual(paletteFor(tag));
  });
  it("has four liquid inks for every print palette", () => {
    for (const name of Object.keys(PALETTES)) expect(LIQUID[name]).toHaveLength(4);
  });
  it("keeps layout randomness stable including unicode names", () => {
    const a = rng(hash("Café 🌊")), b = rng(hash("Café 🌊")), c = rng(hash("Other"));
    const first = Array.from({ length: 10 }, a);
    expect(Array.from({ length: 10 }, b)).toEqual(first);
    expect(Array.from({ length: 10 }, c)).not.toEqual(first);
    expect(first.every(value => value >= 0 && value < 1)).toBe(true);
  });
});
