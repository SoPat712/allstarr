import { afterEach, describe, expect, it, vi } from "vitest";
import { activityWork, mixes, playlistLinks, taste } from "./api";
afterEach(() => vi.unstubAllGlobals());
describe("mix requests", () => {
  it("keeps playable seed identities from local and external searches", async () => {
    const fetch = vi.fn(async (path: string) => Response.json({ tracks: path.includes("/local?")
      ? [{ id: "row", backendItemId: "local/song", title: "Home", artist: "North", artworkUrl: null }]
      : [{ id: "45", externalProvider: "catalog", externalId: "45", title: "Away", artist: "Cedar", artworkUrl: null }] }));
    vi.stubGlobal("fetch", fetch);
    expect((await mixes.seeds("summer")).map(row => row.id)).toEqual(["local/song", "ext-catalog-song-45"]);
    expect(fetch.mock.calls.every(([path]) => !path.includes("mixes/search"))).toBe(true);
  });
  it("retains results from one search when the other is unavailable", async () => {
    vi.stubGlobal("fetch", vi.fn(async (path: string) => path.includes("/local?")
      ? Response.json({ tracks: [{ id: "row", backendItemId: "local", title: "Home" }] })
      : Response.json({}, { status: 503 })));
    expect(await mixes.seeds("home")).toHaveLength(1);
  });
  it("reads existing matching progress and allows page leave to cancel status reads", async () => {
    const fetch = vi.fn(async (path: string) => Response.json(path.includes("/mixes/external")
      ? { Id: "mix", InitialJobId: "job" }
      : path.includes("/jobs/") ? { Job: { State: "Running" }, Progress: [{ DetailsJson: '{"stage":"playlist.match","completed":4,"total":10}' }] }
      : { MatchedCount: 8, TrackCount: 10 }));
    vi.stubGlobal("fetch", fetch);
    expect(await mixes.external("lastfm", "loved")).toMatchObject({ id: "mix", initialJobId: "job" });
    expect(fetch.mock.calls[0]).toEqual(["/api/admin/mixes/external", expect.objectContaining({ method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ source: "lastfm", kind: "loved" }) })]);
    const controller = new AbortController();
    expect((await activityWork.detail("job", "", "", controller.signal)).progress[0].detailsJson).toContain('"completed":4');
    expect(await playlistLinks.details("mix", undefined, controller.signal)).toMatchObject({ matchedCount: 8, trackCount: 10 });
    expect(fetch.mock.calls.slice(1)).toEqual([
      ["/api/admin/jobs/job?limit=25", expect.objectContaining({ signal: controller.signal })],
      ["/api/admin/playlist-links/mix", expect.objectContaining({ signal: controller.signal })],
    ]);
  });
  it("sends preview, feedback and song preference removal contracts", async () => {
    const fetch = vi.fn(async () => Response.json({})); vi.stubGlobal("fetch", fetch);
    await mixes.start({ id: "seed", title: "Song", artist: "North", kind: "song", artworkUrl: null }, 50);
    await mixes.feedback("daily", "song", "moreLikeThis", true);
    await taste.remove("song", "song/identity");
    expect(fetch.mock.calls).toEqual([
      ["/api/admin/mixes/start", expect.objectContaining({ method: "POST", body: JSON.stringify({ seedKind: "song", seedId: "seed", length: 50 }) })],
      ["/api/admin/mixes/daily/feedback", expect.objectContaining({ method: "POST", body: JSON.stringify({ songId: "song", kind: "moreLikeThis", todayOnly: true }) })],
      ["/api/admin/taste/nudge?kind=song&name=song%2Fidentity", expect.objectContaining({ method: "DELETE" })],
    ]);
  });
});
