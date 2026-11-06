import { afterEach, expect, it, vi } from "vitest";
import { matchReview } from "./api";

afterEach(() => vi.unstubAllGlobals());
it("reads the selected recording facts without starting work", async () => {
  const target = { providerId: "catalog", externalId: "recording", title: "Actual recording" };
  const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({ externalSnapshotId: "song", target })));
  vi.stubGlobal("fetch", fetch);
  expect(await matchReview.current("song/id")).toMatchObject({ target });
  expect(fetch).toHaveBeenCalledWith("/api/admin/track-matches/song%2Fid/current", expect.objectContaining({ credentials: "same-origin" }));
  expect(fetch.mock.calls[0][1].method).toBeUndefined();
});
