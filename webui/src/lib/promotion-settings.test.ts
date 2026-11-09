import { afterEach, expect, it, vi } from "vitest";
import type { HouseholdPromotionProfile, PromotionProfile } from "./promotion-profile-api";
import { loadPromotionSettings } from "./promotion-settings";

const values: PromotionProfile = {
  namingScript: "%artist%/%title%.%_extension%", libraryRootId: "music",
  qualityFloor: { codecFamily: "lossless", minBitDepth: 16, minSampleRate: 44100 },
  hiResCeiling: { maxBitDepth: 24, maxSampleRate: 96000 },
  fakeLosslessCheck: true, allowLossy: false, musicBrainzTags: true, coverMode: "both",
  lyricsMode: ".lrc", replayGain: true, defaultUnit: "album", removeKeptCopy: true,
};
const household: HouseholdPromotionProfile = { values, addWithoutApproval: "admins" };
const base = "/api/admin/library-promotions";

afterEach(() => vi.unstubAllGlobals());

async function load(administrator: boolean) {
  const requests: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const path = String(input);
    requests.push(path);
    if (path.endsWith("/profiles/me")) return Response.json({ values, householdDefault: household, usesHouseholdDefault: false });
    if (path.endsWith("/roots")) return Response.json([{ id: "music", name: "Music" }]);
    if (path.endsWith("/profiles/household")) return Response.json(household);
    if (path.endsWith("/setup")) return administrator
      ? Response.json({ ready: true, allowedBaseConfigured: true, reason: null, rootId: "music" })
      : Response.json({ error: "Administrator access required." }, { status: 403 });
    if (path.endsWith("/folders")) return Response.json({ available: true, reason: null, folders: [] });
    throw new Error(`Unexpected request: ${path}`);
  }));
  return { requests, result: await loadPromotionSettings(administrator) };
}

it("loads listener profiles and available folders without administrator setup", async () => {
  const { requests, result: [personal, folders, shared, setup, discovery] } = await load(false);
  expect(requests).toEqual([`${base}/profiles/me`, `${base}/roots`]);
  expect(personal.values).toEqual(values);
  expect(folders).toEqual([{ id: "music", name: "Music" }]);
  expect([shared, setup, discovery]).toEqual([null, null, null]);
});

it("loads administrator setup alongside personal and household profiles", async () => {
  const { requests, result: [, , shared, setup, discovery] } = await load(true);
  expect(requests).toEqual([
    `${base}/profiles/me`, `${base}/roots`, `${base}/profiles/household`, `${base}/setup`, `${base}/folders`,
  ]);
  expect(shared).toEqual(household);
  expect(setup).toMatchObject({ ready: true, allowedBaseConfigured: true });
  expect(discovery).toEqual({ available: true, reason: null, folders: [] });
});
