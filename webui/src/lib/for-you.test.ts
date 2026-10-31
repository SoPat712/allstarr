import { describe, expect, it } from "vitest";
import { greeting, mixDescription, mosaicArtwork, reasonLabel, shortReadiness, tasteSummary, nudgeMarker, mixName, mixNextStep } from "./for-you";

describe("personal mix presentation", () => {
  it.each([["r&b", "R&B"], ["indie rock", "Indie rock"], ["edm", "EDM"], ["uk garage", "UK garage"]])("shows the %s style in plain display case", (raw, label) => {
    expect(mixDescription(`Made for you · ${raw} · North, Cedar`, "daily-1")).toBe(`Made for you · ${label} · North, Cedar`);
    expect(mixDescription("Made for you · · North", "daily-1")).toBe("Made for you · · North");
    expect(mixDescription("Made for you · Just Dropped", "release-radar")).toBe("Made for you · Just Dropped");
  });
  it("greets by local hour with an optional first name", () => {
    expect(greeting(5, "Josh Patra")).toBe("Good morning, Josh");
    expect(greeting(11)).toBe("Good morning");
    expect(greeting(12)).toBe("Good afternoon");
    expect(greeting(17)).toBe("Good evening");
    expect(greeting(0, "  ")).toBe("Good evening");
  });
  it("uses four artwork references or one for a short mosaic", () => {
    expect(mosaicArtwork([])).toEqual([]);
    expect(mosaicArtwork(["a", "b", "c"])).toEqual(["a"]);
    expect(mosaicArtwork(["a", "b", "c", "d", "e"])).toEqual(["a", "b", "c", "d"]);
    expect(mosaicArtwork(["", "a", "b"])).toEqual(["a"]);
  });
  it("uses short human reasons without exposing unknown codes", () => {
    expect(reasonLabel("Played 6 times in 30 days")).toBe("You play this a lot");
    expect(reasonLabel("New release from an artist you follow or play often")).toBe("New release");
    expect(reasonLabel("A close new pick")).toBe("New for you");
    expect(reasonLabel("Like SZA")).toBe("Like SZA");
    expect(reasonLabel("Calm sound")).toBe("Calm sound");
    expect(reasonLabel("Tagged chill")).toBe("Tagged chill");
    expect(reasonLabel("internal_reason")).toBe("Picked for you");
  });
  it("shortens readiness without replacing the detailed explanation", () => {
    expect(shortReadiness("Play about 80 more songs, or import your listening history.")).toBe("Needs more listening");
    expect(shortReadiness("Follow artists, or play an artist at least five times.")).toBe("Follow more artists");
    expect(shortReadiness("Connect a service with recommendations, or add unheard tagged songs to your library.")).toBe("Connect a music service");
    expect(shortReadiness("No eligible playable songs found.")).toBe("Needs more music");
  });
  it("summarizes both taste layers and every empty fallback", () => {
    expect(tasteSummary(["R&B", "pop", "extra"], ["indie rock", "EDM", "hip-hop", "extra"])).toBe("Lately you've been into R&B and pop. Usually it's indie rock, EDM and hip-hop.");
    expect(tasteSummary(["pop"], [])).toBe("Lately you've been into pop.");
    expect(tasteSummary([], ["rock"])).toBe("Usually it's rock.");
    expect(tasteSummary([], [])).toBe("Play some music and your taste will show up here.");
  });
  it("turns linked mix period names into plain words", () => {
    expect(mixName("Your top tracks · 1month")).toBe("Your top tracks · Last month");
    expect(mixName("Created for you · weekly jams")).toBe("Weekly Jams");
    expect(mixName("R&B Blend")).toBe("R&B Blend");
    expect(mixName("Blend 4")).toBe("Blend 4");
  });
  it("offers the relevant next step for an unready mix", () => {
    expect(mixNextStep("daily-1", "Play about 80 more songs.", false).label).toBe("Import history");
    expect(mixNextStep("discover-weekly", "Connect a service.", false).href).toBe("#/sources");
    expect(mixNextStep("release-radar", "Follow artists.", false).label).toBe("Follow artists");
    expect(mixNextStep("chill", "Add songs.", true).href).toBe("#/settings/sound-analysis");
    expect(mixNextStep("chill", "Add songs.", false).label).toBe("Open library");
  });
  it("marks more, less and never nudges", () => {
    expect([undefined, "more", "less", "never"].map(mode => nudgeMarker(mode))).toEqual(["", "↑", "↓", "⊘"]);
  });
});
