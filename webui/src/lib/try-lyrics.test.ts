import { describe, expect, it } from "vitest";
import { splitLyricSong } from "./try-lyrics";

describe("lyrics song text", () => {
  it.each([
    [" Get Lucky ", { title: "Get Lucky" }],
    ["Daft Punk - Get Lucky", { title: "Get Lucky", artist: "Daft Punk" }],
    ["Get Lucky — Daft Punk", { title: "Get Lucky", artist: "Daft Punk" }],
    ["  Queen   -   Bohemian Rhapsody  ", { title: "Bohemian Rhapsody", artist: "Queen" }],
    ["Love-Hate", { title: "Love-Hate" }],
    ["Sabrina Carpenter - Espresso - Live", { title: "Espresso - Live", artist: "Sabrina Carpenter" }],
    [" - Title", { title: "- Title" }],
    ["Title — ", { title: "Title —" }],
    ["", { title: "" }],
  ])("parses %j", (text, expected) => {
    expect(splitLyricSong(text)).toEqual(expected);
  });
});
