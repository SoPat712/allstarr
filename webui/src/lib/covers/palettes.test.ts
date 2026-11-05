import { describe, expect, it } from "vitest";
import { GENRE_WORDS, hash, LIQUID, PALETTES, paletteFor } from "./palettes";

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
  it("keeps palette hashes stable including unicode names", () => {
    expect(hash("")).toBe(2166136261);
    expect(hash("a")).toBe(3826002220);
    expect(hash("🎵")).toBe(Math.imul(2166136261 ^ 0x1f3b5, 16777619) >>> 0);
  });
});
