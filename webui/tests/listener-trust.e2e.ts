import { expect, test, type Page, type Route } from "@playwright/test";
import type { HouseholdPromotionProfile, PromotionProfile } from "../src/lib/promotion-profile-api";

const profile: PromotionProfile = {
  namingScript: "%artist%/%title%.%_extension%", libraryRootId: null,
  qualityFloor: { codecFamily: "lossless", minBitDepth: 16, minSampleRate: 44100 },
  hiResCeiling: { maxBitDepth: 24, maxSampleRate: 96000 },
  fakeLosslessCheck: true, allowLossy: false, musicBrainzTags: true, coverMode: "both", lyricsMode: ".lrc",
  replayGain: true, defaultUnit: "album", afterAdding: false, removeKeptCopy: true,
};
const trustLabel = "Adds to the library without approval";

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}

async function fixture(page: Page, policy: HouseholdPromotionProfile["addWithoutApproval"] = "admins-and-trusted-listeners") {
  const state = {
    trusted: false, failTrust: false, failPolicy: false,
    household: { values: structuredClone(profile), addWithoutApproval: policy } as HouseholdPromotionProfile,
    trustWrites: [] as { id: string; body: { isTrustedListener: boolean } }[],
    householdWrites: [] as HouseholdPromotionProfile[],
  };
  await page.route("**/api/admin/**", async route => {
    const path = new URL(route.request().url()).pathname, method = route.request().method();
    if (path === "/api/admin/auth/me") return json(route, { authenticated: true, backend: "Jellyfin", user: { id: "admin", name: "Alex", isAdministrator: true } });
    if (path === "/api/admin/auth/oidc/status") return json(route, { enabled: false });
    if (path === "/api/admin/onboarding/status") return json(route, { completed: true, shouldRedirectToSetup: false, migration: { available: false }, recoveryNotices: [] });
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    if (path === "/api/admin/ui/schema") return json(route, { configSections: [], providers: [] });
    if (path === "/api/admin/provider-accounts") return json(route, { listenersCanConnectOwnAccounts: true, audienceUsers: [{ id: "sam", displayName: "Sam", isTrustedListener: state.trusted }], accounts: [] });
    if (path === "/api/admin/provider-accounts/listeners/sam/trust" && method === "PUT") {
      const body = route.request().postDataJSON(); state.trustWrites.push({ id: "sam", body });
      if (state.failTrust) return json(route, { error: "Unavailable" }, 503);
      state.trusted = body.isTrustedListener;
      return json(route, { id: "sam", isTrustedListener: state.trusted });
    }
    if (path === "/api/admin/playlist-links") return json(route, { playlistLinks: [] });
    if (path.endsWith("/playlist-forks")) return json(route, []);
    if (path.endsWith("/clean-only-lock")) return json(route, { values: { explicitFilter: "All" }, householdDefaults: {}, usesHouseholdDefaults: false, revision: "first", cleanOnlyLocked: false });
    if (path === "/api/admin/library-promotions/profiles/household") {
      if (state.failPolicy) return json(route, { error: "Unavailable" }, 503);
      if (method === "PUT") { state.household = route.request().postDataJSON(); state.householdWrites.push(state.household); }
      return json(route, state.household);
    }
    if (path === "/api/admin/library-promotions/profiles/me") return json(route, { values: profile, householdDefault: state.household, usesHouseholdDefault: false });
    if (path === "/api/admin/library-promotions/roots") return json(route, []);
    if (path === "/api/admin/library-promotions/profiles/preview") return json(route, { script: route.request().postDataJSON().script, scriptOnly: false, tokens: [], examples: [], errors: [] });
    return json(route, {});
  });
  return state;
}

async function openListener(page: Page) {
  await page.goto("/#/settings/listeners");
  await page.getByRole("button", { name: "Open Sam", exact: true }).click();
  return page.getByRole("dialog", { name: "Sam", exact: true });
}

test("Listener library permission saves on and off and survives reloads", async ({ page }) => {
  const state = await fixture(page);
  let panel = await openListener(page);
  const toggle = () => panel.getByRole("switch", { name: trustLabel, exact: true });
  await expect(toggle()).toBeVisible(); await expect(toggle()).not.toBeChecked();
  await toggle().click();
  await expect.poll(() => state.trusted).toBe(true);
  await expect(toggle()).toBeChecked();
  await page.reload(); panel = await openListener(page);
  await expect(toggle()).toBeChecked();
  await toggle().click();
  await expect.poll(() => state.trusted).toBe(false);
  await page.reload(); panel = await openListener(page);
  await expect(toggle()).not.toBeChecked();
  expect(state.trustWrites).toEqual([
    { id: "sam", body: { isTrustedListener: true } },
    { id: "sam", body: { isTrustedListener: false } },
  ]);
});

test("Failed listener library permission save restores the saved choice and can be retried", async ({ page }) => {
  const state = await fixture(page); state.trusted = true; state.failTrust = true;
  const panel = await openListener(page), toggle = panel.getByRole("switch", { name: trustLabel, exact: true });
  await expect(toggle).toBeChecked(); await toggle.click();
  await expect(page.getByRole("alert").filter({ hasText: "Listener library permission could not be saved." })).toBeVisible();
  await expect(toggle).toBeChecked(); await expect(toggle).toBeEnabled();
  expect(state.trusted).toBe(true);
  state.failTrust = false; await toggle.click();
  await expect.poll(() => state.trusted).toBe(false);
  await expect(toggle).not.toBeChecked();
  expect(state.trustWrites).toEqual(Array(2).fill({ id: "sam", body: { isTrustedListener: false } }));
});

test("Admins-only household policy hides the listener library permission", async ({ page }) => {
  const state = await fixture(page, "admins"); state.trusted = true;
  const panel = await openListener(page);
  await expect(panel.getByRole("switch", { name: "Lock clean only", exact: true })).toBeVisible();
  await expect(panel.getByRole("switch", { name: trustLabel, exact: true })).toHaveCount(0);
  expect(state.trustWrites).toHaveLength(0);
});

test("Unavailable household policy keeps listener settings usable and trust hidden until retry", async ({ page }) => {
  const state = await fixture(page); state.failPolicy = true;
  const panel = await openListener(page);
  await expect(panel.getByRole("switch", { name: "Lock clean only", exact: true })).toBeEnabled();
  await expect(panel.getByRole("switch", { name: trustLabel, exact: true })).toHaveCount(0);
  const error = panel.getByRole("alert").filter({ hasText: "Library approval settings could not be loaded" });
  await expect(error).toBeVisible();
  state.failPolicy = false; await error.getByRole("button", { name: "Try again", exact: true }).click();
  await expect(panel.getByRole("switch", { name: trustLabel, exact: true })).toBeVisible();
  await expect(error).toHaveCount(0);
});

test("Household approval choices persist and control listener library permission visibility", async ({ page }) => {
  const state = await fixture(page);
  async function choosePolicy(option: string) {
    await page.goto("/#/settings/adding-to-library");
    await page.getByRole("radio", { name: "Household default", exact: true }).click();
    await page.getByRole("button", { name: "Who adds without approval", exact: true }).click();
    await page.getByRole("option", { name: option, exact: true }).click();
  }
  await choosePolicy("Admins");
  await expect.poll(() => state.household.addWithoutApproval).toBe("admins");
  await page.reload();
  await page.getByRole("radio", { name: "Household default", exact: true }).click();
  await expect(page.getByRole("button", { name: "Who adds without approval", exact: true })).toHaveText("Admins");
  let panel = await openListener(page);
  await expect(panel.getByRole("switch", { name: trustLabel, exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Close", exact: true }).click();
  await choosePolicy("Admins and trusted listeners");
  await expect.poll(() => state.household.addWithoutApproval).toBe("admins-and-trusted-listeners");
  await page.reload();
  await page.getByRole("radio", { name: "Household default", exact: true }).click();
  await expect(page.getByRole("button", { name: "Who adds without approval", exact: true })).toHaveText("Admins and trusted listeners");
  panel = await openListener(page);
  await expect(panel.getByRole("switch", { name: trustLabel, exact: true })).toBeVisible();
  expect(state.householdWrites.map(write => write.addWithoutApproval)).toEqual(["admins", "admins-and-trusted-listeners"]);
});
