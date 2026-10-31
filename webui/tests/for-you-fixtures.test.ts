import { expect, it, vi } from "vitest";
import type { Page, Route } from "@playwright/test";
import { mockForYou, songs } from "./for-you-fixtures";

it("serves external mix details without creating another playlist", async () => {
  let handle: (route: Route) => Promise<unknown>;
  const page = { route: async (_pattern: string, handler: typeof handle) => { handle = handler; } } as unknown as Page;
  const state = await mockForYou(page);
  async function request(method: string, body: object | null, path = "/api/admin/mixes/external") {
    const fulfill = vi.fn();
    await handle({
      request: () => ({
        url: () => `http://127.0.0.1${path}`,
        method: () => method,
        postData: () => body ? JSON.stringify(body) : null,
        postDataJSON: () => body,
      }),
      fulfill,
    } as unknown as Route);
    return fulfill.mock.calls[0][0];
  }
  expect(await request("GET", null, "/api/admin/playlist-links")).toMatchObject({ json: { playlistLinks: [] } });
  const created = await request("POST", { source: "lastfm", kind: "loved" });
  expect(created).toMatchObject({ status: 201, json: { id: "external", initialJobId: "external-job" } });
  const count = state.cards.length;
  expect(await request("GET", null)).toMatchObject({ json: { mix: created.json, songs } });
  expect(state.cards).toHaveLength(count);
  expect(await request("GET", null, "/api/admin/playlist-links")).toMatchObject({ json: { playlistLinks: [expect.objectContaining({ id: created.json.id, name: "Loved on Last.fm" })] } });
  expect(await request("DELETE", null, "/api/admin/mixes/external/in-apps")).toMatchObject({ json: { inApps: false } });
  expect(await request("GET", null, "/api/admin/playlist-links")).toMatchObject({ json: { playlistLinks: [expect.objectContaining({ enabled: false })] } });
  expect(await request("POST", { source: "lastfm", kind: "loved" })).toMatchObject({ json: { id: created.json.id, inApps: true } });
  expect(state.cards).toHaveLength(count);
  state.publish = false;
  expect(await request("GET", null)).toMatchObject({ json: { mix: { inApps: true, appsAllowed: false } } });
  expect(await request("POST", null, "/api/admin/mixes/external/in-apps")).toMatchObject({ status: 403 });
});
