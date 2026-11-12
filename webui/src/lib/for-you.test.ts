import { describe, expect, it } from "vitest";
import type { MixCard } from "./api";
import { appsWaiting, artworkUrl, soundDifference, soundSummary, steeringNames, greeting, mixDescription, mosaicArtwork, reasonLabel, shortReadiness, tasteSummary, nudgeMarker, mixName, mixNextStep, serviceCards, allServiceCards } from "./for-you";

describe("personal mix presentation", () => {
  it("says an added mix is still on its way to the apps", () => {
    expect(appsWaiting({ inApps: true, added: true })).toBe("");
    expect(appsWaiting({ inApps: false, added: false, appsUnavailableReason: "Preparing this mix for your apps." })).toBe("");
    expect(appsWaiting({ inApps: false, added: true, appsUnavailableReason: "Preparing this mix for your apps." })).toBe("Preparing for your apps…");
    expect(appsWaiting({ inApps: false, added: true })).toBe("Preparing for your apps…");
    expect(appsWaiting({ inApps: false, added: true, appsUnavailableReason: "The playlist in your music server is unavailable." }))
      .toBe("The playlist in your music server is unavailable.");
  });
  it("describes each mix without repeating its name", () => {
    expect(mixDescription("Made for you · Uncharted", "discover-weekly")).toBe("New to you, based on your taste");
    expect(mixDescription("Made for you · Long Lost", "rewind")).toBe("Songs you loved and haven't played lately");
    expect(mixDescription("Made for you · Heavy Rotation", "on-repeat")).toBe("What you're playing most right now");
    expect(mixDescription("Made for you · Just Dropped", "release-radar")).toBe("New from artists you play");
    expect(mixDescription("Made for you · hip-hop · Juice WRLD, XXXTENTACION, Post Malone", "daily-1")).toBe("Juice WRLD, XXXTENTACION, Post Malone");
    expect(mixDescription("Updated Monday · Music for focus", "focus")).toBe("Updated Monday");
    expect(mixDescription("From Last.fm · kept in sync", "external")).toBe("From Last.fm · kept in sync");
    expect(mixName("Chill Mix")).toBe("Chill");
  });
  it("greets by local hour with an optional first name", () => {
    expect(greeting(5, "Josh Patra")).toBe("Good morning, Josh");
    expect(greeting(11)).toBe("Good morning");
    expect(greeting(12)).toBe("Good afternoon");
    expect(greeting(17)).toBe("Good evening");
    expect(greeting(0, "  ")).toBe("Good evening");
  });
  it("keeps release cover paths and looks item ids up as artwork", () => {
    expect(artworkUrl("/api/admin/releases/artwork/deezer/42")).toBe("/api/admin/releases/artwork/deezer/42");
    expect(artworkUrl("ext-deezer-song-1")).toBe("/api/admin/downloads/artwork/ext-deezer-song-1");
  });
  it("uses four artwork references or one for a short mosaic", () => {
    expect(mosaicArtwork([])).toEqual([]);
    expect(mosaicArtwork(["a", "b", "c"])).toEqual(["a"]);
    expect(mosaicArtwork(["a", "b", "c", "d", "e"])).toEqual(["a", "b", "c", "d"]);
    expect(mosaicArtwork(["", "a", "b"])).toEqual(["a"]);
  });
  it("uses short human reasons without exposing unknown codes", () => {
    expect(shortReadiness("Refresh this mix to look for songs from your music and services.")).toBe("Refresh to look for songs");
    expect(reasonLabel("Played 6 times in 30 days")).toBe("You play this a lot");
    expect(reasonLabel("New release from an artist you follow or play often")).toBe("New release");
    expect(reasonLabel("A close new pick")).toBe("New for you");
    expect(reasonLabel("Like SZA")).toBe("Like SZA");
    expect(reasonLabel("Calm sound")).toBe("Calm sound");
    expect(reasonLabel("Tagged chill")).toBe("Tagged chill");
    expect(reasonLabel("internal_reason")).toBe("Picked for you");
  });
  it("shortens readiness without replacing the detailed explanation", () => {
    expect(shortReadiness("Play about 80 more songs, or import your listening history.")).toBe("Play about 80 more songs");
    expect(shortReadiness("Follow artists, or play an artist at least five times.")).toBe("Follow more artists");
    expect(shortReadiness("Connect a service with recommendations, or add unheard tagged songs to your library.")).toBe("No new songs to suggest yet");
    expect(shortReadiness("Add about 25 more qualifying analyzed or tagged songs.")).toBe("Needs about 25 more songs");
    expect(shortReadiness("Add about 7 more qualifying analyzed or tagged songs.")).toBe("Needs about 7 more songs");
    expect(shortReadiness("No playable songs are available for this mix yet. Refresh after more listening or new releases.")).toBe("No songs yet");
    expect(shortReadiness("Play about 26 more songs in 30 days.")).toBe("Play about 26 more songs");
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
    expect(mixNextStep("chill", "Add songs.", true).href).toBe("#/settings/discovery");
    expect(mixNextStep("chill", "Add songs.", false).label).toBe("Open library");
  });
  it("marks more, less and never nudges", () => {
    expect([undefined, "more", "less", "never"].map(mode => nudgeMarker(mode))).toEqual(["", "↑", "↓", "⊘"]);
  });
});

it("uses plain exclusion labels without repeating counts", async () => {
  const { mixExclusions } = await import("./for-you");
  expect(mixExclusions({ "Three early skips": 3, Never: 1 })).toBe("3 skipped early · 1 you marked Never");
  expect(mixExclusions({ "three early skips": 3, never: 1 })).toBe("3 skipped early · 1 you marked Never");
  expect(mixExclusions({ "Duplicate recordings": 2, "Listening policy": 1, "No playable route": 1, "Missing song details": 1,
    "Candidate limit": 1, "Changed before saving": 1, "Choices changed during generation": 1, "Outside this recipe": 1, "Artist, release, tier and size limits": 1 }))
    .toBe("2 repeats · 1 left out by your listening settings · 1 you can't play · 1 missing song details · 1 over the song limit · 1 left out before saving · 1 left out after your choices changed · 1 didn't fit this mix · 1 over this mix's limits");
});

describe("steeringNames", () => {
  it("takes turns between long-term favourites and recent listening without repeats", () => {
    expect(steeringNames(["a", "b"], ["x", "a", "y"], 12)).toEqual(["x", "a", "b", "y"]);
  });
  it("stops at the limit", () => {
    expect(steeringNames(["a", "b"], ["x", "y"], 3)).toEqual(["x", "a", "y"]);
  });
});

describe("soundSummary", () => {
  const trait = (name: string, difference: number, lean: number | null = null) => ({ name, difference, lean });
  it("names what sets favourites apart and the pace", () => {
    expect(soundSummary({ traits: [trait("Relaxed", -40), trait("Energetic", 52), trait("Danceable", 31), trait("Happy", 10)], tempo: 124, libraryTempo: 116 }))
      .toBe("Your favourites are more energetic and danceable and less relaxed than the rest of your library, a little faster, around 124 BPM.");
  });
  it("says when favourites sound like the library and adds the clearest skip difference", () => {
    expect(soundSummary({ traits: [trait("Energetic", 8, 3), trait("Melancholy", -12, -38), trait("Happy", 15, 26)], tempo: 118, libraryTempo: 117 }))
      .toBe("Your favourites sound much like the rest of your library, around 118 BPM. You skip melancholy songs more than you finish them.");
  });
  it("signs differences", () => {
    expect([soundDifference(32), soundDifference(-18), soundDifference(0)]).toEqual(["+32", "−18", "0"]);
  });
});

describe("Service feed cards", () => {
  const feed: MixCard = { description: "From Spotify", sortOrder: 0, recipeVersion: null, updatedAt: null, id: "weekly", kind: "external", section: "yourServices", name: "Discover Weekly", source: "spotify", sourcePlaylistId: "weekly", sourceLabel: "Spotify", inApps: false, appsAllowed: true, readiness: { ready: true, message: "Ready" }, coverArtwork: [] };
  it("keeps unsynced feeds and explains the synced state", () => {
    expect(serviceCards([feed])[0]).toMatchObject({ inApps: false, description: "From Spotify" });
    expect(serviceCards([{ ...feed, inApps: true }])[0]).toMatchObject({ inApps: true, description: "From Spotify · kept in sync" });
  });
  it("shows every feed with synced feeds first, then service and name", () => {
    const rows = [
      { ...feed, id: "spotify-z", name: "Z mix" },
      { ...feed, id: "lastfm", sourceLabel: "Last.fm" },
      { ...feed, id: "spotify-a", name: "A mix" },
      { ...feed, id: "apple", sourceLabel: "Apple Music" },
      { ...feed, id: "synced-spotify", inApps: true },
      { ...feed, id: "synced-apple", sourceLabel: "Apple Music", inApps: true },
    ];
    expect(allServiceCards(rows).map(card => card.id)).toEqual(["synced-apple", "synced-spotify", "apple", "lastfm", "spotify-a", "spotify-z"]);
    expect(rows[0].id).toBe("spotify-z");
  });
  it("shows only known song counts, including zero", () => {
    expect(serviceCards([{ ...feed, songCount: 30 }])[0].description).toBe("From Spotify · 30 songs");
    expect(serviceCards([{ ...feed, songCount: 0 }])[0].description).toBe("From Spotify · 0 songs");
    expect(serviceCards([{ ...feed, songCount: null }])[0].description).toBe("From Spotify");
  });
  it("explains preparation and library counts without hiding saved service playlists", () => {
    expect(serviceCards([{ ...feed, readiness: { ready: false, message: "Preparing songs from your service…" } }])[0].description).toBe("Preparing songs…");
    expect(serviceCards([{ ...feed, evidence: { fromMusic: 12 } as MixCard["evidence"], savedFrom: "Riju Patra" }])[0]).toMatchObject({ description: "From Spotify · 12 from your library", savedFrom: "Riju Patra" });
    expect(serviceCards([{ ...feed, evidence: { fromMusic: 1, shown: 2 } as MixCard["evidence"] }])[0].description).toBe("From Spotify · 1 from your library · 2 songs");
    expect(shortReadiness("Preparing songs from your service…")).toBe("Preparing songs");
    expect(serviceCards([{ ...feed, readiness: { ready: false, message: "Your service playlist is unavailable. We will try again later." } }])[0].description).toBe("Playlist unavailable");
  });
  it("omits a recipe already supplied by an automatic feed", () => {
    expect(serviceCards([feed], [{ ...feed, id: "add:spotify:weekly" }])).toHaveLength(1);
    expect(serviceCards([feed], [{ ...feed, id: "add:lastfm:loved", source: "lastfm", sourcePlaylistId: "loved" }])).toHaveLength(2);
  });
});
