import { expect, it } from "vitest";
import { layoutLabel } from "./label-layout";

const measure = (text: string, size: number) => [...text].length * size * 0.6;

it.each([160, 184, 320])("keeps cover labels readable and inside a %spx cover", size => {
  const label = layoutLabel(size, size * 0.4, size * 0.74, size * 0.54,
    ["mix from", "a long song name with several words"], 12 * size / 160, measure);
  expect(label.size * 160 / size).toBeGreaterThanOrEqual(12);
  expect(label.x).toBeGreaterThanOrEqual(size * 0.03);
  expect(label.y).toBeGreaterThanOrEqual(size * 0.03);
  expect(label.x + label.width).toBeLessThanOrEqual(size * 0.97);
  expect(label.y + label.height).toBeLessThanOrEqual(size * 0.97);
  expect(label.lines.map(line => line.text).join(" ")).toBe("mix from a long song name with several words");
  for (const line of label.lines) expect(measure(line.text, label.size)).toBeLessThanOrEqual(label.width - size * 0.05);
});

it("wraps long artist names and keeps even an oversized label inside its cover", () => {
  const label = layoutLabel(160, 10, 115, 96, ["Electronic Blend", "LongArtistNameWithoutSpaces".repeat(20)], 12, measure);
  expect(label.height).toBeLessThanOrEqual(160 * 0.94);
  expect(label.lines.at(-1)?.text).toMatch(/…$/);
  expect(label.y + label.height).toBeLessThanOrEqual(160 * 0.97);
  expect(label.size).toBeGreaterThanOrEqual(12);
});
