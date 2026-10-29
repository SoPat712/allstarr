import { afterEach, expect, it, vi } from "vitest";
import { audioMuse } from "./api";

afterEach(() => vi.unstubAllGlobals());

it("preserves the stored key when the connection field is blank", async () => {
  const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({ BaseUrl: "http://sounds.invalid", KeyConfigured: true, Revision: 4 })));
  vi.stubGlobal("fetch", fetch);
  expect(await audioMuse.configure("http://sounds.invalid", "", 3)).toEqual({ baseUrl: "http://sounds.invalid", keyConfigured: true, revision: 4 });
  const body = JSON.parse(fetch.mock.calls[0][1].body);
  expect(body).not.toHaveProperty("apiKey");
  expect(body.revision).toBe(3);
});

it("sends a replacement key only when one was entered", async () => {
  const fetch = vi.fn().mockResolvedValue(new Response("{}"));
  vi.stubGlobal("fetch", fetch);
  await audioMuse.configure("http://sounds.invalid", "replacement", null);
  expect(JSON.parse(fetch.mock.calls[0][1].body)).toMatchObject({ apiKey: "replacement", revision: null });
});
