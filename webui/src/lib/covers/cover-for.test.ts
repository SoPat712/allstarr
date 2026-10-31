import { describe, expect, it } from "vitest";
import type { MixCard, MixSong } from "../api";
import { PALETTES } from "./palettes";
import { hash } from "./random";
import { coverFor } from "./cover-for";

function mix(kind: string, overrides: Partial<MixCard> = {}): MixCard {
  return { id: kind, kind, name: kind, description: "", section: "madeForYou", sourceLabel: null, recipeVersion: null, updatedAt: null, readiness: { ready: true, message: "" }, inApps: true, appsAllowed: true, coverArtwork: [], sortOrder: 0, ...overrides };
}
const songs = (count: number): MixSong[] => Array.from({ length: count }, (_, index) => ({ mixId: "release-radar", generation: 1, source: "library", reason: "", score: 1, song: { id: String(index), canonicalRecordingId: null, title: "Song", artist: "Artist", album: "Album" } }));
const fallbackTag = (id: string) => Object.keys(PALETTES)[hash(id) % Object.keys(PALETTES).length];

describe("coverFor", () => {
  it.each([
    ["daily-1", "liquid"], ["daily-2", "liquid"], ["daily-3", "liquid"], ["daily-4", "liquid"],
    ["discover-weekly", "stars"], ["release-radar", "radar"], ["on-repeat", "stamped"], ["rewind", "sun"],
    ["chill", "waves"], ["focus", "ruled"], ["workout", "zigzag"], ["party", "punched"], ["sleep", "moon"],
    ["custom", "letter"], ["future-kind", "letter"], ["external", "letter"], ["daily-5", "letter"], ["constructor", "letter"],
  ])("maps %s to %s", (kind, style) => {
    const cover = coverFor(mix(kind));
    expect(cover.style).toBe(style);
    if (style !== "letter" && style !== "liquid") expect(cover.tag).toBe("");
  });

  it.each([
    ["discover-weekly", ["Uncharted"]], ["release-radar", ["Just", "Dropped"]],
    ["on-repeat", ["Heavy", "Rotation"]], ["rewind", ["Long Lost"]],
  ] as const)("uses the household name on the %s cover", (kind, lines) => {
    expect(coverFor(mix(kind)).lines).toEqual(lines);
  });
  it("reads a clean Blend name and its description metadata", () => {
    const cover = coverFor(mix("daily-2", { name: "  Hip-Hop Blend  ", description: "Made for you ·  hip-hop  ·  Sade, Cleo Sol  " }));
    expect(cover).toMatchObject({ number: "2", tag: "hip-hop", artists: "Sade, Cleo Sol", lines: ["Hip-Hop Blend"] });
  });

  it("uses the style in a clean Blend name without description metadata", () => {
    expect(coverFor(mix("daily-3", { name: "Jazz Blend" }))).toMatchObject({ tag: "Jazz", artists: "", lines: ["Jazz Blend"] });
  });

  it.each(["", "Blend 2", "Something else · Pop · Singer"])("uses stable Blend fallbacks for %s", name => {
    const card = mix("daily-2", { name });
    expect(coverFor(card)).toMatchObject({ number: "2", tag: fallbackTag(card.id), artists: "", lines: ["Blend 2"] });
  });

  it.each([1, 2, 3, 4])("prints fallback Blend %s without treating it as a style", number => {
    const card = mix(`daily-${number}`, { name: `Blend ${number}`, description: `Made for you · Blend ${number}` });
    expect(coverFor(card)).toMatchObject({ number: String(number), tag: fallbackTag(card.id), artists: "", lines: [`Blend ${number}`] });
  });

  it("keeps artist names when description style is absent", () => {
    expect(coverFor(mix("daily-2", { name: "Blend 2", description: "Made for you · · Singer" }))).toMatchObject({ tag: fallbackTag("daily-2"), artists: "Singer", lines: ["Blend 2"] });
  });

  it("keeps the raw style for palette selection while printing the clean title", () => {
    expect(coverFor(mix("daily-1", { name: "R&B Blend", description: "Made for you · r&b · North, Cedar" }))).toMatchObject({ tag: "r&b", artists: "North, Cedar", lines: ["R&B Blend"] });
  });

  it.each([0, 1, 9, 14, 25])("caps radar marks for %s songs", count => {
    expect(coverFor(mix("release-radar"), songs(count)).count).toBe(Math.min(14, count));
  });

  it("uses nine radar marks when the list has no songs", () => {
    expect(coverFor(mix("release-radar")).count).toBe(9);
  });

  it.each(["Last.fm", "LAST FM", "last-fm"])("recognizes %s source labels", sourceLabel => {
    expect(coverFor(mix("external", { sourceLabel, name: "Loved on Last.fm" }))).toMatchObject({ style: "lastfm", tag: "", lines: ["Loved", "Tracks"], sub: "Your hearts on Last.fm" });
  });

  it.each([["7day", "Last 7 days"], ["1month", "Last month"], ["3month", "Last 3 months"], ["12month", "Last year"], ["overall", "All time"]])("uses a human label for %s", (period, sub) => {
    expect(coverFor(mix("external", { sourceLabel: "Last.fm", name: `Your top tracks · ${period}` }))).toMatchObject({ lines: ["Top Tracks"], sub });
  });

  it("labels similar artists", () => {
    expect(coverFor(mix("external", { sourceLabel: "Last.fm", name: "Similar to your top tracks" }))).toMatchObject({ lines: ["Similar", "Artists"], sub: "Like your top artists" });
  });

  it.each(["ListenBrainz", "LISTEN BRAINZ", "listen-brainz"])("recognizes %s source labels without repeating the provider", sourceLabel => {
    expect(coverFor(mix("external", { sourceLabel, name: "Created for you · weekly jams" }))).toMatchObject({ style: "listenbrainz", tag: "", lines: ["Weekly Jams"] });
  });

  it("labels weekly exploration", () => {
    expect(coverFor(mix("external", { sourceLabel: "ListenBrainz", name: "Created for you · weekly exploration" }))).toMatchObject({ style: "listenbrainz", lines: ["Weekly", "Exploration"] });
  });

  it("removes provider names from other listening service labels", () => {
    const cover = coverFor(mix("external", { sourceLabel: "ListenBrainz", name: "ListenBrainz · Fresh picks" }));
    expect(cover.lines).toEqual(["Fresh picks"]);
  });

  it("can recognize a provider in the name when its source label is absent", () => {
    expect(coverFor(mix("external", { name: "Loved on Last.fm" })).style).toBe("lastfm");
  });

  it("uses a letter for unknown external sources", () => {
    expect(coverFor(mix("external", { sourceLabel: "Other service", name: "My songs Mix" }))).toMatchObject({ style: "letter", seed: "My songs", tag: fallbackTag("external") });
  });

  it("uses human periods on unknown service covers", () => {
    expect(coverFor(mix("external", { sourceLabel: "Other service", name: "Favourites · 1month" })).lines).toEqual(["Favourites · Last month"]);
  });

  it.each([["  Evening mIx  ", "Evening"], ["Mix", "Mix"], ["  ", "Mix"], ["Remix", "Remix"]])("derives the letter seed from %s", (name, seed) => {
    expect(coverFor(mix("custom", { name })).seed).toBe(seed);
  });

  it("uses the most common supplied song genre for a custom mix", () => {
    const rows = songs(3);
    Object.assign(rows[0].song, { genres: [" Jazz ", "jazz"], genre: "Jazz" });
    Object.assign(rows[1].song, { genre: "Jazz" });
    Object.assign(rows[2].song, { genres: ["Pop"] });
    expect(coverFor(mix("custom"), rows).tag).toBe("Jazz");
  });

  it("falls back when songs have no usable genres", () => {
    const rows = songs(1);
    Object.assign(rows[0].song, { genres: [" "], genre: null });
    expect(coverFor(mix("custom"), rows).tag).toBe(fallbackTag("custom"));
  });

  it("breaks genre ties consistently when songs change order", () => {
    const rows = songs(2);
    Object.assign(rows[0].song, { genre: "Pop" });
    Object.assign(rows[1].song, { genre: "Jazz" });
    expect(coverFor(mix("custom"), rows).tag).toBe("Jazz");
    expect(coverFor(mix("custom"), rows.reverse()).tag).toBe("Jazz");
  });

  it("derives letter texture and layout from its name and tag", () => {
    const card = mix("custom", { id: "saved", name: "Evening Mix" });
    const cover = coverFor(card);
    expect(cover.layoutSeed).toBe(hash(cover.seed + cover.tag));
    expect(cover.wobbleSeed).toBe(hash(cover.seed));
    expect(coverFor(card)).toEqual(cover);
  });

  it("keeps printed and liquid seeds stable for the same identity and tag", () => {
    for (const kind of ["daily-1", "discover-weekly", "chill", "external"]) {
      const card = mix(kind, { name: "Pop Blend", description: "Made for you · Pop · Artists", sourceLabel: kind === "external" ? "Last.fm" : null });
      const cover = coverFor(card);
      expect(cover.layoutSeed).toBe(hash(card.id + cover.tag));
      expect(cover.wobbleSeed).toBe(hash(card.id));
      expect(coverFor(card)).toEqual(cover);
    }
  });
});
