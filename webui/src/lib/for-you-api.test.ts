import { afterEach, describe, expect, it, vi } from "vitest";
import { activityWork, matchReview, mixes, playlistLinks, releaseInbox, taste } from "./api";
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });
describe("mix requests", () => {
  it("keeps native artist identity when reading candidates and saving a service or no service", async () => {
    const fetch = vi.fn(async (_path: string, _options?: RequestInit) => Response.json({}));
    vi.stubGlobal("fetch", fetch);
    await releaseInbox.artistCandidates("native/artist");
    await releaseInbox.chooseArtist("native/artist", "deezer", "service-artist");
    await releaseInbox.chooseArtist("native/artist", null, null);
    expect(fetch.mock.calls).toEqual([
      ["/api/admin/releases/artists/candidates?libraryArtistId=native%2Fartist", expect.any(Object)],
      ["/api/admin/releases/artists/choice", expect.objectContaining({ method: "PUT", body: JSON.stringify({ libraryArtistId: "native/artist", providerId: "deezer", artistId: "service-artist" }) })],
      ["/api/admin/releases/artists/choice", expect.objectContaining({ method: "PUT", body: JSON.stringify({ libraryArtistId: "native/artist", providerId: null, artistId: null }) })],
    ]);
  });
  it("times out a stalled mix detail and allows a fresh retry", async () => {
    const controller = new AbortController();
    const timeout = vi.spyOn(AbortSignal, "timeout").mockReturnValueOnce(new AbortController().signal).mockReturnValueOnce(controller.signal);
    const fetch = vi.fn((_path: string, options?: RequestInit) => new Promise<Response>((_resolve, reject) => {
      options?.signal?.addEventListener("abort", () => reject(options.signal?.reason), { once: true });
    })).mockResolvedValueOnce(Response.json({ mix: {}, songs: [] }));
    vi.stubGlobal("fetch", fetch);
    await mixes.get("ready");
    const stalled = mixes.get("waiting");
    const rejected = expect(stalled).rejects.toMatchObject({ name: "AbortError" });
    controller.abort();
    await rejected;
    fetch.mockResolvedValueOnce(Response.json({ mix: { id: "waiting" }, songs: [] }));
    expect(await mixes.get("waiting")).toMatchObject({ mix: { id: "waiting" } });
    expect(timeout).toHaveBeenCalledWith(20_000);
    expect(fetch.mock.calls.every(([, options]) => options?.signal instanceof AbortSignal)).toBe(true);
  });
  it("adds and removes app membership separately from deleting a custom mix", async () => {
    const fetch = vi.fn(async (_path: string, _options?: RequestInit) => Response.json({})); vi.stubGlobal("fetch", fetch);
    await mixes.add("daily/1"); await mixes.remove("daily/1"); await mixes.delete("custom/1");
    expect(fetch.mock.calls).toEqual([
      ["/api/admin/mixes/daily%2F1/in-apps", expect.objectContaining({ method: "POST" })],
      ["/api/admin/mixes/daily%2F1/in-apps", expect.objectContaining({ method: "DELETE" })],
      ["/api/admin/mixes/custom%2F1", expect.objectContaining({ method: "DELETE" })],
    ]);
    expect(fetch.mock.calls.every(([, options]) => !(options as RequestInit).body)).toBe(true);
  });
  it("keeps playable seed identities from local and external searches", async () => {
    const fetch = vi.fn(async (path: string) => Response.json({ tracks: path.includes("/local?")
      ? [{ id: "row", backendItemId: "local/song", title: "Home", artist: "North", artworkUrl: null }]
      : [{ id: "45", externalProvider: "catalog", externalId: "45", title: "Away", artist: "Cedar", artworkUrl: null }] }));
    vi.stubGlobal("fetch", fetch);
    expect((await mixes.seeds("summer")).map(row => row.id)).toEqual(["local/song", "ext-catalog-song-45"]);
    expect(fetch.mock.calls.every(([path]) => !path.includes("mixes/search"))).toBe(true);
  });
  it("hands over library seeds before services answer and keeps them first", async () => {
    let answer!: (response: Response) => void;
    const services = new Promise<Response>(resolve => { answer = resolve; });
    const fetch = vi.fn((path: string) => path.includes("/local?")
      ? Promise.resolve(Response.json({ tracks: [{ id: "a", backendItemId: "local/a", title: "Home" }, { id: "b", backendItemId: "local/b", title: "Away" }] }))
      : services);
    vi.stubGlobal("fetch", fetch);
    const early: string[][] = [];
    const seeds = mixes.seeds("juice", rows => early.push(rows.map(row => row.id)));
    await vi.waitFor(() => expect(early).toEqual([["local/a", "local/b"]]));
    answer(Response.json({ tracks: [{ externalProvider: "catalog", externalId: "1", title: "Lucid" }, { externalProvider: "catalog", externalId: "1", title: "Lucid again" }, { title: "No identity" }] }));
    expect((await seeds).map(row => [row.id, row.title])).toEqual([["local/a", "Home"], ["local/b", "Away"], ["ext-catalog-song-1", "Lucid"]]);
    const provider = new URL(fetch.mock.calls.find(([path]) => path.includes("/provider?"))![0], "http://localhost");
    expect(provider.searchParams.get("answer")).toBe("fast");
  });
  it("asks match review searches for every service", async () => {
    const fetch = vi.fn(async (_path: string) => Response.json({ tracks: [], providers: [] })); vi.stubGlobal("fetch", fetch);
    await matchReview.searchProviders("song", "snapshot", true);
    expect(new URL(fetch.mock.calls[0][0], "http://localhost").searchParams.has("answer")).toBe(false);
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
