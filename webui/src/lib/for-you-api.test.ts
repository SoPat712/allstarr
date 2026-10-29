import { afterEach, describe, expect, it, vi } from "vitest";
import { mixes, taste } from "./api";
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
