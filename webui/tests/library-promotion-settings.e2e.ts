import { expect, test, type Page } from "@playwright/test";
import type { HouseholdPromotionProfile, PromotionProfile } from "../src/lib/promotion-profile-api";
import { appearance, mockForYou } from "./for-you-fixtures";

const initialScript = "%artist%/%title%.%_extension%";
const initialProfile: PromotionProfile = {
  namingScript: initialScript, namingConfirmed: true, libraryRootId: null,
  qualityFloor: { codecFamily: "lossless", minBitDepth: 16, minSampleRate: 44100 }, hiResCeiling: { maxBitDepth: 24, maxSampleRate: 96000 },
  fakeLosslessCheck: true, allowLossy: false, musicBrainzTags: true, coverMode: "both", lyricsMode: ".lrc", replayGain: true,
  defaultUnit: "album", removeKeptCopy: true,
};
const songs = [
  { kind: "album", artist: "North", title: "Blue morning" }, { kind: "multi-disc", artist: "Willow", title: "Open skies" },
  { kind: "compilation", artist: "Various Artists", title: "Summer" }, { kind: "single", artist: "Cedar", title: "Home" },
];
type Detected = { script: string | null; matched: number; sampled: number; examples: { path: string }[]; reason: string | null };
async function fixture(page: Page, admin = true, inherited = false, detected: Detected | null = null) {
  await mockForYou(page, { admin });
  const state = {
    personal: structuredClone(initialProfile), household: { values: { ...structuredClone(initialProfile), namingScript: "%album%/%title%.%_extension%" }, addWithoutApproval: "admins-and-trusted-listeners" } as HouseholdPromotionProfile,
    writes: [] as { scope: string; body: any }[], previews: [] as string[], householdReads: 0, setupReads: 0, failNextSave: false, usesHouseholdDefault: inherited,
    detected, detectRoots: [] as (string | null)[],
  };
  if (inherited) state.personal = structuredClone(state.household.values);
  await page.route("**/api/admin/library-promotions/**", async route => {
    const path = new URL(route.request().url()).pathname, method = route.request().method();
    if (path.endsWith("/folders")) return route.fulfill({ json: { available: true, reason: null, folders: [] } });
    if (path.endsWith("/setup")) {
      state.setupReads++;
      return admin ? route.fulfill({ json: { ready: true, allowedBaseConfigured: true, reason: null, rootId: "00000000-0000-0000-0000-000000000001" } })
        : route.fulfill({ status: 403, json: { error: "Administrator access required." } });
    }
    if (path.endsWith("/roots")) return route.fulfill({ json: [{ id: "00000000-0000-0000-0000-000000000001", name: "Music" }] });
    if (path.endsWith("/naming/detect")) {
      state.detectRoots.push(new URL(route.request().url()).searchParams.get("rootId"));
      return state.detected ? route.fulfill({ json: state.detected }) : route.fulfill({ status: 404, json: { error: "Not found" } });
    }
    if (path.endsWith("/preview")) {
      const { script } = route.request().postDataJSON(); state.previews.push(script);
      const errors = script.includes("%missing%") ? [{ position: script.indexOf("%missing%"), length: 9, message: "The variable is not supported." }] : [];
      const examples = errors.length ? [] : songs.map(song => ({ libraryTrackId: null, recordingId: null, title: song.title, kind: song.kind,
        relativePath: script.replaceAll("$upper(%artist%)", song.artist.toUpperCase()).replaceAll("%artist%", song.artist).replaceAll("%album%", "Warm light").replaceAll("%title%", song.title).replaceAll("%_extension%", "flac"), errors: [] }));
      return route.fulfill({ json: { script, scriptOnly: script.includes("$"), tokens: [], examples, errors } });
    }
    const scope = path.endsWith("/household") ? "household" : "me";
    if (scope === "household") state.householdReads++;
    if (method === "PUT") {
      const body = route.request().postDataJSON();
      state.writes.push({ scope, body });
      if (state.failNextSave) { state.failNextSave = false; return route.fulfill({ status: 409, json: { error: "The promotion profile changed. Reload it and try again." } }); }
      if (scope === "me") { state.personal = body; state.usesHouseholdDefault = false; }
      else state.household = body;
    }
    return route.fulfill({ json: scope === "household" ? state.household : { values: state.usesHouseholdDefault ? state.household.values : state.personal, householdDefault: state.household, usesHouseholdDefault: state.usesHouseholdDefault } });
  });
  await page.goto("/#/settings/adding-to-library");
  await expect(page.getByRole("heading", { name: "How added files are named" })).toBeVisible();
  await expect(page.getByText(inherited ? "Warm light/Blue morning.flac" : "North/Blue morning.flac", { exact: true })).toBeVisible();
  return state;
}
async function scriptField(page: Page) {
  const field = page.getByRole("textbox", { name: "Naming script", exact: true });
  if (!await field.isVisible()) await page.locator("summary").filter({ hasText: /^Naming script$/ }).click();
  await expect(field).toBeVisible(); return field;
}
async function select(page: Page, label: string, option: string) {
  await page.getByRole("button", { name: label, exact: true }).click();
  await page.getByRole("option", { name: option, exact: true }).click();
}

test("Builds a name by dragging fields and tokens, then edits literal text", async ({ page }) => {
  const state = await fixture(page), script = await scriptField(page);
  await script.fill("%title%");
  const tokens = page.getByRole("list", { name: "Naming tokens" });
  await expect(tokens.getByRole("listitem")).toHaveCount(1);
  await page.evaluate(() => window.scrollTo({ top: 0, behavior: "instant" }));
  await page.getByRole("button", { name: "Add Artist", exact: true }).dragTo(tokens.getByRole("listitem").first());
  await expect(script).toHaveValue("%artist%%title%");
  await tokens.getByRole("button", { name: "Title, token 2", exact: true }).dragTo(tokens.getByRole("listitem").first());
  await expect(script).toHaveValue("%title%%artist%");
  await page.getByRole("button", { name: "+ Text", exact: true }).click();
  await page.getByRole("textbox", { name: "Text for token 3", exact: true }).fill("/");
  await expect(script).toHaveValue("%title%%artist%/");
  await tokens.getByRole("button", { name: "Title, token 1", exact: true }).dragTo(tokens.getByRole("listitem").last());
  await expect(script).toHaveValue("%artist%/%title%");
  await expect.poll(() => state.personal.namingScript).toBe("%artist%/%title%");
  await expect(page.getByText("North/Blue morning", { exact: true })).toBeVisible();
});

test("Builds a name with arrow keys and the move menu and removes a token", async ({ page }) => {
  const state = await fixture(page), script = await scriptField(page);
  await script.fill("%artist%%title%");
  const title = page.getByRole("button", { name: "Title, token 2", exact: true });
  await title.focus(); await title.press("ArrowLeft");
  await expect(page.getByRole("button", { name: "Title, token 1", exact: true })).toBeFocused();
  await expect(script).toHaveValue("%title%%artist%");
  await page.getByRole("button", { name: "Move token 1", exact: true }).click();
  await page.getByRole("menuitem", { name: "Move to end", exact: true }).click();
  await expect(script).toHaveValue("%artist%%title%");
  await page.getByRole("button", { name: "Remove token 1", exact: true }).click();
  await expect(script).toHaveValue("%title%");
  await expect.poll(() => state.personal.namingScript).toBe("%title%");
});

test("Script edits update all four examples, preserve script-only expressions and locate validation errors", async ({ page }) => {
  const state = await fixture(page), script = await scriptField(page);
  await script.fill("$upper(%artist%)/%title%.%_extension%");
  await expect(page.getByText("Script only", { exact: true })).toBeVisible();
  await expect(page.getByText("NORTH/Blue morning.flac", { exact: true })).toBeVisible();
  for (const song of songs) await expect(page.getByText(`${song.artist.toUpperCase()}/${song.title}.flac`, { exact: true })).toBeVisible();
  await expect.poll(() => state.personal.namingScript).toBe("$upper(%artist%)/%title%.%_extension%");
  const savedCount = state.writes.length;
  await script.fill("%artist%/%missing%");
  const error = page.getByRole("button", { name: "Character 10: The variable is not supported.", exact: true });
  await expect(error).toBeVisible(); await expect(script).toHaveAttribute("aria-invalid", "true");
  await error.click(); await expect(script).toBeFocused();
  expect(await script.evaluate(node => [(node as HTMLTextAreaElement).selectionStart, (node as HTMLTextAreaElement).selectionEnd])).toEqual([9, 18]);
  expect(state.writes).toHaveLength(savedCount);
  await script.fill("%title%.%_extension%");
  await expect(page.getByRole("list", { name: "Naming tokens" })).toBeVisible();
  await expect(page.getByText("Blue morning.flac", { exact: true })).toBeVisible();
  await expect.poll(() => state.personal.namingScript).toBe("%title%.%_extension%");
});

test("Autosaves the quality, extras and placement groups through the personal profile", async ({ page }) => {
  const state = await fixture(page);
  await select(page, "Lowest quality to add", "Lossy or lossless");
  await select(page, "Keep hi-res up to", "24-bit / 192 kHz");
  await page.getByRole("switch", { name: "Check lossless copies are real", exact: true }).click();
  await page.getByRole("switch", { name: "MusicBrainz tags", exact: true }).click();
  await select(page, "Cover art", "Embedded"); await select(page, "Synced lyrics", "Off");
  await page.getByRole("switch", { name: "ReplayGain", exact: true }).click();
  const music = page.getByRole("list", { name: "Library folders", exact: true }).getByRole("listitem").filter({ hasText: "Music" });
  await expect(music).toContainText("Adds go here"); await expect(music.getByRole("button")).toHaveCount(0);
  await page.getByRole("radio", { name: "Just the song", exact: true }).click();
  await expect(page.getByRole("switch", { name: "After adding", exact: true })).toHaveCount(0);
  await page.getByRole("switch", { name: "Remove kept copy", exact: true }).click();
  await expect.poll(() => state.personal).toMatchObject({ qualityFloor: { codecFamily: "any", minBitDepth: 0, minSampleRate: 44100 }, hiResCeiling: { maxBitDepth: 24, maxSampleRate: 192000 }, allowLossy: true, fakeLosslessCheck: false, musicBrainzTags: false, coverMode: "embedded", lyricsMode: "off", replayGain: false, libraryRootId: null, defaultUnit: "song", removeKeptCopy: false });
  expect(state.writes.every(write => write.scope === "me" && !("values" in write.body))).toBe(true);
  await page.reload(); await expect(page.getByRole("switch", { name: "ReplayGain", exact: true })).not.toBeChecked();
});

test("Offers the naming pattern the library folder already uses", async ({ page }) => {
  const script = "%albumartist%/%album%/$num(%tracknumber%,2) - %title%.%_extension%";
  const state = await fixture(page, true, false, { script, matched: 212, sampled: 240, examples: [{ path: "North/Warm light/01 - Blue morning.flac" }], reason: null });
  const offer = page.getByRole("status").filter({ hasText: "Your library already names files like this" });
  await expect(offer).toContainText("North/Warm light/01 - Blue morning.flac");
  await expect(offer).toContainText("212 of 240 songs in this folder follow the same pattern");
  expect(state.detectRoots).toContain("00000000-0000-0000-0000-000000000001");
  await page.getByRole("region", { name: "How added files are named", exact: true }).screenshot({ path: test.info().outputPath("detected-pattern.png") });
  await offer.getByRole("button", { name: "Use this pattern", exact: true }).click();
  await expect.poll(() => state.personal.namingScript).toBe(script);
  await expect(page.getByText(/This pattern matches .* sampled songs\./)).toBeVisible();
  await expect(offer).toHaveCount(0);
});

test("Shows no pattern offer when the folder has no clear naming pattern", async ({ page }) => {
  await fixture(page, true, false, { script: null, matched: 0, sampled: 40, examples: [], reason: "no-pattern" });
  await expect(page.getByText("Your library already names files like this")).toHaveCount(0);
  await expect(page.getByText(/This pattern matches .* sampled songs\./)).toHaveCount(0);
});

for (const [width, theme] of [[390, "light"], [1280, "dark"]] as const) test(`Autodetect resets edited folders and filenames at ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme);
  const state = await fixture(page, true, false, { script: initialScript, matched: 40, sampled: 40, examples: [], reason: null });
  await expect(page.getByText(/This pattern matches .* sampled songs\./)).toBeVisible();
  const script = await scriptField(page);
  await script.fill("%missing%/custom/%title%");
  await expect(script).toHaveAttribute("aria-invalid", "true");
  const detectedScript = "%artist%/%album%/%title%.%_extension%";
  state.detected = { script: detectedScript, matched: 40, sampled: 40, examples: [], reason: null };
  const requests = state.detectRoots.length;
  const button = page.getByRole("button", { name: "Autodetect", exact: true });
  await button.focus(); await button.press("Enter");
  await expect(script).toHaveValue(detectedScript);
  await expect(script).toHaveAttribute("aria-invalid", "false");
  await expect(page.getByRole("list", { name: "Naming tokens" }).getByRole("button", { name: "Album, token 3", exact: true })).toBeVisible();
  await expect(page.getByText("North/Warm light/Blue morning.flac", { exact: true })).toBeVisible();
  await expect.poll(() => state.personal).toEqual({ ...initialProfile, libraryRootId: "00000000-0000-0000-0000-000000000001", namingScript: detectedScript });
  expect(state.detectRoots.slice(requests)).toEqual(["00000000-0000-0000-0000-000000000001"]);
  expect(state.writes.every(write => write.scope === "me")).toBe(true);
  await expect(page.getByText("Detected folder and filename pattern applied.", { exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.getByRole("region", { name: "How added files are named", exact: true }).screenshot({ path: test.info().outputPath(`autodetect-${width}-${theme}.png`) });
  await page.reload();
  await expect(await scriptField(page)).toHaveValue(detectedScript);
});

for (const outcome of ["no-pattern", "unavailable"] as const) test(`Autodetect keeps edits but requires a naming choice when detection is ${outcome}`, async ({ page }) => {
  const state = await fixture(page, true, false, { script: initialScript, matched: 40, sampled: 40, examples: [], reason: null });
  const script = await scriptField(page), custom = "custom/%title%.%_extension%";
  await script.fill(custom);
  await expect.poll(() => state.personal.namingScript).toBe(custom);
  state.detected = outcome === "no-pattern" ? { script: null, matched: 0, sampled: 40, examples: [], reason: "no-pattern" } : null;
  await page.getByRole("button", { name: "Autodetect", exact: true }).click();
  await expect(page.getByRole("status").filter({ hasText: "Your edited pattern was kept." })).toBeVisible();
  await expect(script).toHaveValue(custom);
  expect(state.personal.namingScript).toBe(custom);
  await expect.poll(() => state.personal.namingConfirmed).toBe(false);
  await page.getByRole("button", { name: "Use this naming pattern", exact: true }).click();
  await expect.poll(() => state.personal.namingConfirmed).toBe(true);
  expect(state.personal.namingScript).toBe(custom);
  await expect(page.getByRole("button", { name: "Autodetect", exact: true })).toBeEnabled();
});

test("Autodetect cannot apply a personal result after switching to the household profile", async ({ page }) => {
  const state = await fixture(page, true, false, { script: initialScript, matched: 40, sampled: 40, examples: [], reason: null });
  await expect(page.getByRole("button", { name: "Autodetect", exact: true })).toBeEnabled();
  let release!: () => void;
  const pending = new Promise<void>(resolve => { release = resolve; });
  let started = false;
  await page.route("**/naming/detect?rootId=*", async route => {
    started = true; await pending;
    await route.fulfill({ json: { script: "stale/%title%.%_extension%", matched: 40, sampled: 40, examples: [], reason: null } });
  });
  await page.getByRole("button", { name: "Autodetect", exact: true }).click();
  await expect.poll(() => started).toBe(true);
  await expect(page.getByRole("button", { name: "Detecting…", exact: true })).toBeDisabled();
  await page.getByRole("radio", { name: "Household default", exact: true }).click();
  release();
  await expect(page.getByRole("button", { name: "Autodetect", exact: true })).toBeEnabled();
  await expect(await scriptField(page)).toHaveValue(state.household.values.namingScript);
  state.detected = { script: "%album%/%artist%/%title%.%_extension%", matched: 40, sampled: 40, examples: [], reason: null };
  await page.getByRole("button", { name: "Autodetect", exact: true }).click();
  await expect.poll(() => state.household.values.namingScript).toBe(state.detected!.script);
  expect(state.personal).toEqual(initialProfile);
  expect(state.writes).toHaveLength(1);
  expect(state.writes[0].scope).toBe("household");
});

test("Only admins see household settings and only that profile edits approval roles", async ({ page }) => {
  const state = await fixture(page);
  await expect(page.getByRole("button", { name: "Who adds without approval", exact: true })).toHaveCount(0);
  await page.getByRole("radio", { name: "Household default", exact: true }).click();
  await expect(page.getByText("Warm light/Blue morning.flac", { exact: true })).toBeVisible();
  await select(page, "Who adds without approval", "Admins");
  await page.getByRole("switch", { name: "ReplayGain", exact: true }).click();
  await expect.poll(() => state.household.addWithoutApproval).toBe("admins");
  await expect.poll(() => state.household.values.replayGain).toBe(false);
  expect(state.personal.replayGain).toBe(true);
  expect(state.writes.at(-1)).toMatchObject({ scope: "household", body: { values: { replayGain: false }, addWithoutApproval: "admins" } });
  await page.getByRole("radio", { name: "Your profile", exact: true }).click();
  await expect(page.getByRole("switch", { name: "ReplayGain", exact: true })).toBeChecked();
  await expect(page.getByRole("button", { name: "Who adds without approval", exact: true })).toHaveCount(0);
});

test("Listeners can open their own settings and never request the household endpoint", async ({ page }) => {
  const state = await fixture(page, false);
  await expect(page.getByRole("radio", { name: "Household default", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Who adds without approval", exact: true })).toHaveCount(0);
  await page.getByRole("switch", { name: "ReplayGain", exact: true }).click();
  await expect.poll(() => state.personal.replayGain).toBe(false);
  expect(state.householdReads).toBe(0);
  expect(state.setupReads).toBe(0);
  await expect(page.getByRole("navigation", { name: "Settings categories", exact: true }).getByRole("link", { name: /^Adding to the library/ })).toBeVisible();
});

test("Inherited profiles follow household saves until a personal profile is saved", async ({ page }) => {
  const state = await fixture(page, true, true);
  await page.getByRole("radio", { name: "Household default", exact: true }).click();
  await page.getByRole("switch", { name: "ReplayGain", exact: true }).click();
  await expect.poll(() => state.household.values.replayGain).toBe(false);
  await page.getByRole("radio", { name: "Your profile", exact: true }).click();
  await expect(page.getByRole("switch", { name: "ReplayGain", exact: true })).not.toBeChecked();
  await page.getByRole("switch", { name: "ReplayGain", exact: true }).click();
  await expect.poll(() => state.usesHouseholdDefault).toBe(false);
  await page.getByRole("radio", { name: "Household default", exact: true }).click();
  await page.getByRole("switch", { name: "MusicBrainz tags", exact: true }).click();
  await expect.poll(() => state.household.values.musicBrainzTags).toBe(false);
  await page.getByRole("radio", { name: "Your profile", exact: true }).click();
  await expect(page.getByRole("switch", { name: "MusicBrainz tags", exact: true })).toBeChecked();
});

test("A failed autosave keeps the edit and can be retried", async ({ page }) => {
  const state = await fixture(page); state.failNextSave = true;
  await page.getByRole("switch", { name: "ReplayGain", exact: true }).click();
  await expect(page.getByText("The promotion profile changed. Reload it and try again.", { exact: true })).toBeVisible();
  await expect(page.getByRole("switch", { name: "ReplayGain", exact: true })).not.toBeChecked();
  await page.getByRole("button", { name: "Retry saving", exact: true }).click();
  await expect.poll(() => state.personal.replayGain).toBe(false);
});

for (const width of [390, 1440]) for (const theme of ["light", "dark"] as const) test(`Library profile fits ${width}px in ${theme}`, async ({ page }, testInfo) => {
  await appearance(page, width, theme); await fixture(page);
  const script = await scriptField(page); await script.fill("%artist%/%title%.%_extension%");
  await expect(page.getByText("North/Blue morning.flac", { exact: true })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath(`library-profile-${width}-${theme}.png`), fullPage: true });
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) test(`Naming script folds like other card rows ${width}px ${theme}`, async ({ page }) => {
  await appearance(page, width, theme); await fixture(page);
  const fold = page.locator(".settings-fold").filter({ has: page.locator("summary", { hasText: /^Naming script$/ }) });
  await expect(fold).toHaveAttribute("data-placement", "card");
  expect(await fold.locator("summary").evaluate(element => [getComputedStyle(element).listStyleType, getComputedStyle(element, "::after").borderRightStyle])).toEqual(["none", "solid"]);
  await scriptField(page);
  const card = page.getByRole("region", { name: "How added files are named", exact: true });
  expect(Math.abs((await fold.boundingBox())!.x - (await card.boundingBox())!.x)).toBeLessThanOrEqual(1);
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await card.screenshot({ path: test.info().outputPath(`05-naming-script-${width}-${theme}.png`) });
});
