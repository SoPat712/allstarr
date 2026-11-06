import { afterEach, describe, expect, it, vi } from "vitest";
import { migrations } from "./api";

afterEach(() => vi.unstubAllGlobals());

describe("collection import requests", () => {
  it("previews an account with a read-only request and preserves the next page", async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({
      Items: [{ SourceId: "liked-songs", Name: "My music", Count: 12, Kind: "liked-songs" }],
      NextCursor: "following", Unsupported: [],
    })));
    vi.stubGlobal("fetch", fetch);

    expect(await migrations.preview("personal/account", "next/page")).toMatchObject({
      items: [{ sourceId: "liked-songs", count: 12 }], nextCursor: "following",
    });
    expect(fetch).toHaveBeenCalledWith("/api/admin/migrations/preview?providerAccountId=personal%2Faccount&cursor=next%2Fpage", expect.objectContaining({ credentials: "same-origin" }));
    expect(fetch.mock.calls[0][1].method).toBeUndefined();
  });

  it("sends the playlist destination preference only when confirming the selection", async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(null, { status: 202 }));
    vi.stubGlobal("fetch", fetch);

    await migrations.choose("import", [{ SourceId: "evening", KeepInSync: false }], false);

    expect(fetch).toHaveBeenCalledWith("/api/admin/migrations/import/choose", expect.objectContaining({
      method: "POST", body: JSON.stringify({ Collections: [{ SourceId: "evening", KeepInSync: false }], MakePlaylists: false }),
    }));
  });
});
