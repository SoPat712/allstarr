import { expect, test, type Page } from "@playwright/test";
import type { HouseholdPromotionProfile, PromotionProfile } from "../src/lib/promotion-profile-api";
import { appearance, capture, mockForYou } from "./for-you-fixtures";

const initialScript = "%artist%/%title%.%_extension%";
const initialProfile: PromotionProfile = {
  namingScript: initialScript, libraryRootId: null,
  qualityFloor: { codecFamily: "lossless", minBitDepth: 16, minSampleRate: 44100 }, hiResCeiling: { maxBitDepth: 24, maxSampleRate: 96000 },
  fakeLosslessCheck: true, allowLossy: false, musicBrainzTags: true, coverMode: "both", lyricsMode: ".lrc", replayGain: true,
  defaultUnit: "album", afterAdding: false, removeKeptCopy: true,
};
const songs = [
  { kind: "album", artist: "North", title: "Blue morning" }, { kind: "multi-disc", artist: "Willow", title: "Open skies" },
  { kind: "compilation", artist: "Various Artists", title: "Summer" }, { kind: "single", artist: "Cedar", title: "Home" },
];
async function fixture(page: Page, admin = true, inherited = false) {
  await mockForYou(page, { admin });
  const state = {
    personal: structuredClone(initialProfile), household: { values: { ...structuredClone(initialProfile), namingScript: "%album%/%title%.%_extension%" }, addWithoutApproval: "admins-and-trusted-listeners" } as HouseholdPromotionProfile,
    writes: [] as { scope: string; body: any }[], previews: [] as string[], householdReads: 0, failNextSave: false, usesHouseholdDefault: inherited,
  };
  if (inherited) state.personal = structuredClone(state.household.values);
  await page.route("**/api/admin/library-promotions/**", async route => {
    const path = new URL(route.request().url()).pathname, method = route.request().method();
    if (path.endsWith("/roots")) return route.fulfill({ json: [{ id: "00000000-0000-0000-0000-000000000001", name: "Music" }] });
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
  await expect(page.getByRole("heading", { name: "File names and folders" })).toBeVisible();
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
  await select(page, "Library folder", "Music");
  await page.getByRole("radio", { name: "Just the song", exact: true }).click();
  await page.getByRole("switch", { name: "After adding", exact: true }).click();
  await expect.poll(() => state.personal).toMatchObject({ qualityFloor: { codecFamily: "any", minBitDepth: 0, minSampleRate: 44100 }, hiResCeiling: { maxBitDepth: 24, maxSampleRate: 192000 }, allowLossy: true, fakeLosslessCheck: false, musicBrainzTags: false, coverMode: "embedded", lyricsMode: "off", replayGain: false, libraryRootId: "00000000-0000-0000-0000-000000000001", defaultUnit: "song", afterAdding: true, removeKeptCopy: true });
  expect(state.writes.every(write => write.scope === "me" && !("values" in write.body))).toBe(true);
  await page.reload(); await expect(page.getByRole("switch", { name: "ReplayGain", exact: true })).not.toBeChecked();
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
  await expect(page.getByRole("navigation", { name: "Settings sections", exact: true }).getByRole("link", { name: "Adding to the library", exact: true })).toBeVisible();
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

for (const width of [390, 1440]) test(`Library profile fits ${width}px`, async ({ page }) => {
  await appearance(page, width, "light"); await fixture(page);
  const script = await scriptField(page); await script.fill("%artist%/%title%.%_extension%");
  await expect(page.getByText("North/Blue morning.flac", { exact: true })).toBeVisible();
  await capture(page, "library-profile", width, "light");
});
