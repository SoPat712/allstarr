import { expect, test, type Page, type Route } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";

const jf = { userId: "11111111-1111-4111-8111-111111111111", displayName: "Alex", protocol: "jellyfin",
  instanceLabel: "Instance 1a2b3c4d", profileCode: "JF-0123456789ab", identityFingerprint: "a".repeat(64), lastSeenAt: "2026-10-24T12:00:00Z", participation: "available", linkId: null as string | null };
const sub = { ...jf, userId: "22222222-2222-4222-8222-222222222222", protocol: "subsonic", instanceLabel: "Instance 5e6f7890", profileCode: "SUB-abcdef012345", identityFingerprint: "b".repeat(64) };
const linkId = "33333333-3333-4333-8333-333333333333";
const jobId = "44444444-4444-4444-8444-444444444444";

async function reply(route: Route, data: unknown, status = 200) {
  await route.fulfill({ status, contentType: "application/json", body: JSON.stringify(data) });
}

async function mockHearts(page: Page, administrator = true) {
  const state = { members: [jf, sub], links: [] as Record<string, unknown>[], reads: 0, detailReads: 0,
    previewError: false, createError: false, retryConflict: false, hold: false,
    writes: [] as Array<Record<string, unknown>>, previews: [] as Array<Record<string, unknown>>,
    retries: [] as Array<Record<string, unknown>> };
  await page.route("**/api/admin/**", async route => {
    const url = new URL(route.request().url()), path = url.pathname;
    if (path === "/api/admin/auth/me") return reply(route, { authenticated: true, backend: "Jellyfin",
      user: { id: "viewer", name: "Viewer", isAdministrator: administrator } });
    if (path === "/api/admin/auth/oidc/status") return reply(route, { enabled: false });
    if (path === "/api/admin/onboarding/status") return reply(route, { completed: true, setupOpen: false,
      shouldRedirectToSetup: false, schemaVersion: "onboarding-v1", completedSteps: ["backend-identity"],
      completionSource: "setup-guide", revision: 1, recoveryNotices: [], migration: { available: false, completed: false, firstRun: false } });
    if (path === "/api/admin/ui/schema") return reply(route, { activeBackend: "Jellyfin", providers: [], priorityGroups: [] });
    if (path === "/api/admin/provider-accounts") return reply(route, { accounts: [], audienceUsers: [], listenersCanConnectOwnAccounts: true });
    if (path === "/api/admin/providers/status") return reply(route, { providers: [], summaries: [], measurements: [] });
    if (path === "/api/admin/config") return reply(route, {});
    if (path === "/api/admin/scrobbling/deliveries") return reply(route, { items: [] });
    if (path.startsWith("/api/admin/provider-favorite-links")) {
      state.reads++;
      if (path.endsWith("/members")) return reply(route, { activeProtocol: "jellyfin", items: state.members.filter(member =>
        !url.searchParams.get("protocol") || member.protocol === url.searchParams.get("protocol")), nextCursor: null });
      if (path.endsWith("/preview")) {
        state.previews.push(route.request().postDataJSON());
        if (state.previewError) return reply(route, { error: "A profile changed. Refresh profiles before continuing.", details: { code: "favorite_link_member_changed" } }, 409);
        return reply(route, { jellyfin: jf, subsonic: sub, confirmationVersion: 1, existingLinkId: null });
      }
      if (path.endsWith("/retry")) {
        state.retries.push(route.request().postDataJSON());
        if (state.retryConflict) return reply(route, { error: "The link changed. Refresh before retrying.", details: { code: "favorite_link_changed" } }, 409);
        state.links = [fixtureLink("merging")];
        return reply(route, { linkId, jobId, created: true, replayed: false }, 202);
      }
      if (route.request().method() === "POST") {
        state.writes.push(route.request().postDataJSON());
        if (state.hold) await new Promise(resolve => setTimeout(resolve, 400));
        if (state.createError) return reply(route, { error: "The request could not be completed. Try again." }, 503);
        state.links = [fixtureLink("merging")];
        return reply(route, { linkId, jobId, created: state.writes.length === 1, replayed: state.writes.length > 1 }, 201);
      }
      if (path.endsWith(linkId)) { state.detailReads++; return reply(route, state.links[0]); }
      return reply(route, { activeProtocol: "jellyfin", items: state.links, nextCursor: null });
    }
    if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return reply(route, {});
  });
  return state;
}

function fixtureLink(state: "merging" | "shared" | "needs_attention") {
  return { id: linkId, jellyfin: { ...jf, participation: "active", linkId }, subsonic: { ...sub, participation: "active", linkId },
    state, createdAt: "2026-10-25T03:00:00Z", revision: 7,
    union: { jobId, state: state === "shared" ? "succeeded" : state === "needs_attention" ? "failed" : "running",
      processedRows: 100, seededItems: 12, completedAt: state === "shared" ? "2026-10-25T03:01:00Z" : null,
      errorCode: state === "needs_attention" ? "favorite_link_owner_unavailable" : null, canRetry: state === "needs_attention" } };
}

async function choosePair(page: Page) {
  const card = page.getByRole("region", { name: "Shared provider hearts", exact: true });
  await card.getByRole("button", { name: "Jellyfin profile", exact: true }).click();
  await page.getByRole("option", { name: /Alex.*JF-0123456789ab/ }).click();
  await card.getByRole("button", { name: "Subsonic profile", exact: true }).click();
  await page.getByRole("option", { name: /Alex.*SUB-abcdef012345/ }).click();
  await card.getByRole("button", { name: "Review profiles", exact: true }).click();
  return card;
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`shared hearts confirms the exact profiles at ${width}px ${theme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    const state = await mockHearts(page);
    await page.goto("/#/integrations/accounts");
    const card = await choosePair(page);
    await expect(card).toContainText("The initial union cannot be undone here");
    const save = card.getByRole("button", { name: "Share provider hearts", exact: true });
    await expect(save).toBeDisabled();
    await card.getByRole("checkbox", { name: "These profiles belong to the same person", exact: true }).click();
    if (width === 390) await expectContainedMobileGeometry(page);
    await save.scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath(`shared-hearts-confirm-${width}-${theme}.png`), fullPage: true });
    await save.focus();
    await page.keyboard.press("Enter");
    await expect.poll(() => state.writes.length).toBe(1);
    expect(state.writes[0]).toMatchObject({ jellyfinUserId: jf.userId, jellyfinFingerprint: jf.identityFingerprint,
      subsonicUserId: sub.userId, subsonicFingerprint: sub.identityFingerprint, confirmedSamePerson: true, confirmationVersion: 1 });
    expect(state.writes[0].requestId).toMatch(/^[0-9a-f-]{36}$/);
    await expect(card.getByRole("status", { name: "Sharing status", exact: true })).toContainText("Combining hearts");
    await expect(card).toContainText("100 rows processed");
    await expect(card).toContainText("12 hearts added");
    if (width === 390) await expectContainedMobileGeometry(page);
    await page.screenshot({ path: testInfo.outputPath(`shared-hearts-merging-${width}-${theme}.png`), fullPage: true });
  });

  test(`shared hearts states remain readable at ${width}px ${theme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: theme, reducedMotion: "reduce" });
    const state = await mockHearts(page);
    const shared = fixtureLink("shared");
    state.links = [shared]; state.members = [shared.jellyfin, shared.subsonic];
    await page.goto("/#/integrations/accounts");
    const card = page.getByRole("region", { name: "Shared provider hearts", exact: true });
    await expect(card.getByRole("status", { name: "Sharing status", exact: true })).toHaveText("Shared");
    await expect(card).toContainText("100 rows processed");
    if (width === 390) await expectContainedMobileGeometry(page);
    await card.scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath(`shared-hearts-shared-${width}-${theme}.png`), fullPage: true });
    const stopped = fixtureLink("needs_attention"); stopped.subsonic.participation = "disabled";
    state.links = [stopped]; state.members = [stopped.jellyfin, stopped.subsonic];
    await card.getByRole("button", { name: "Refresh profiles", exact: true }).click();
    await expect(card.getByRole("status", { name: "Sharing status", exact: true })).toContainText("Needs attention");
    await expect(card).toContainText("Disabled profile");
    await expect(card).toContainText("A retry continues from the saved progress");
    if (width === 390) await expectContainedMobileGeometry(page);
    await card.getByRole("button", { name: "Retry combining hearts", exact: true }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath(`shared-hearts-stopped-${width}-${theme}.png`), fullPage: true });
  });
}

test("listeners never fetch profile links", async ({ page }) => {
  const state = await mockHearts(page, false);
  await page.goto("/#/integrations/accounts");
  await expect(page.getByRole("region", { name: "Shared provider hearts", exact: true })).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Sources", exact: true }).first()).toBeVisible();
  expect(state.reads).toBe(0);
});

test("profile review works by keyboard at 320px", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 900 });
  await page.emulateMedia({ reducedMotion: "reduce" });
  const state = await mockHearts(page);
  state.members = [jf, ...Array.from({ length: 40 }, (_, index) => ({ ...jf,
    userId: `10000000-0000-4000-8000-${String(index).padStart(12, "0")}`,
    displayName: index === 39 ? "A".repeat(150) : `Listener ${index + 1}`,
    profileCode: `JF-${String(index).padStart(12, "0")}` })), sub];
  await page.goto("/#/integrations/accounts");
  const card = page.getByRole("region", { name: "Shared provider hearts", exact: true });
  for (const name of ["Jellyfin profile", "Subsonic profile"]) {
    await card.getByRole("button", { name, exact: true }).click();
    await expectContainedMobileGeometry(page);
    if (name === "Jellyfin profile") {
      await page.keyboard.press("End");
      await expect(page.getByRole("option", { name: /^A{150}/ })).toBeInViewport();
      const bounds = await page.getByRole("listbox").boundingBox();
      expect(bounds!.height).toBeLessThanOrEqual(900);
      expect(bounds!.y).toBeGreaterThanOrEqual(0);
      expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(901);
    }
    await page.keyboard.press("Home");
    await page.keyboard.press("Enter");
  }
  await card.getByRole("button", { name: "Review profiles", exact: true }).focus();
  await page.keyboard.press("Enter");
  const confirmation = card.getByRole("checkbox", { name: "These profiles belong to the same person", exact: true });
  await confirmation.focus();
  await page.keyboard.press("Space");
  await expect(confirmation).toBeChecked();
  await expect(card.getByRole("button", { name: "Share provider hearts", exact: true })).toBeEnabled();
  await expectContainedMobileGeometry(page);
});

test("source detail routes never fetch profile links", async ({ page }) => {
  const state = await mockHearts(page);
  await page.goto("/#/integrations/services/deezer?from=accounts");
  await expect(page.getByText("This source isn't available", { exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "Shared provider hearts", exact: true })).toHaveCount(0);
  expect(state.reads).toBe(0);
});

test("duplicate clicks queue only one confirmed request", async ({ page }) => {
  const state = await mockHearts(page); state.hold = true;
  await page.goto("/#/integrations/accounts");
  const card = await choosePair(page);
  await card.getByRole("checkbox", { name: "These profiles belong to the same person", exact: true }).click();
  const save = card.getByRole("button", { name: "Share provider hearts", exact: true });
  await save.evaluate(button => { (button as HTMLButtonElement).click(); (button as HTMLButtonElement).click(); });
  await expect.poll(() => state.writes.length).toBe(1);
  await expect(card.getByRole("status", { name: "Sharing status", exact: true })).toContainText("Combining hearts");
  expect(state.writes).toHaveLength(1);
});

test("missing counterpart explains previously signed-in eligibility", async ({ page }) => {
  const state = await mockHearts(page); state.members = [jf];
  await page.goto("/#/integrations/accounts");
  const card = page.getByRole("region", { name: "Shared provider hearts", exact: true });
  await expect(card).toContainText("No eligible Subsonic profile is available");
  await expect(card.getByRole("button", { name: "Review profiles", exact: true })).toBeDisabled();
  await expect(card.getByRole("button", { name: "Share provider hearts", exact: true })).toHaveCount(0);
  expect(state.writes).toHaveLength(0);
});

test("an unchanged failed submission retries with the same request identity", async ({ page }) => {
  await page.addInitScript(() => Object.defineProperty(crypto, "randomUUID", { value: undefined }));
  const state = await mockHearts(page); state.createError = true;
  await page.goto("/#/integrations/accounts");
  const card = await choosePair(page);
  await card.getByRole("checkbox", { name: "These profiles belong to the same person", exact: true }).click();
  const save = card.getByRole("button", { name: "Share provider hearts", exact: true });
  await save.click();
  await expect(card.getByRole("alert")).toContainText("Try again");
  await expect(save).toBeEnabled();
  state.createError = false;
  await save.click();
  await expect.poll(() => state.writes.length).toBe(2);
  expect(state.writes[1]).toEqual(state.writes[0]);
});

test("progress polling stops on completion and leaving Accounts", async ({ page }) => {
  await page.clock.install();
  const state = await mockHearts(page); state.links = [fixtureLink("merging")];
  await page.goto("/#/integrations/accounts");
  const card = page.getByRole("region", { name: "Shared provider hearts", exact: true });
  await expect(card.getByRole("status", { name: "Sharing status", exact: true })).toContainText("Combining hearts");
  state.links = [fixtureLink("shared")];
  await page.clock.fastForward(5000);
  await expect(card.getByRole("status", { name: "Sharing status", exact: true })).toHaveText("Shared");
  expect(state.detailReads).toBe(1);
  await page.clock.fastForward(10000);
  expect(state.detailReads).toBe(1);
  state.links = [fixtureLink("merging")];
  await card.getByRole("button", { name: "Refresh profiles", exact: true }).click();
  await expect(card.getByRole("status", { name: "Sharing status", exact: true })).toContainText("Combining hearts");
  await page.goto("/#/integrations/services/deezer?from=accounts");
  await expect(card).toHaveCount(0);
  const reads = state.reads;
  await page.clock.fastForward(10000);
  expect(state.reads).toBe(reads);
});

test("synthetic and disabled profiles cannot be selected for a new link", async ({ page }) => {
  const state = await mockHearts(page);
  state.members = [{ ...jf, participation: "synthetic" }, { ...sub, participation: "disabled" }];
  await page.goto("/#/integrations/accounts");
  const card = page.getByRole("region", { name: "Shared provider hearts", exact: true });
  await expect(card.getByRole("button", { name: "Jellyfin profile", exact: true })).toBeDisabled();
  await expect(card.getByRole("button", { name: "Subsonic profile", exact: true })).toBeDisabled();
  await expect(card.getByRole("button", { name: "Review profiles", exact: true })).toBeDisabled();
  expect(state.writes).toHaveLength(0);
});

test("stale preview preserves selections and requires a fresh confirmation", async ({ page }) => {
  const state = await mockHearts(page); state.previewError = true;
  await page.goto("/#/integrations/accounts");
  const card = await choosePair(page);
  await expect(card.getByRole("alert")).toContainText("Refresh profiles");
  await expect(card.getByRole("button", { name: "Jellyfin profile", exact: true })).toContainText("JF-0123456789ab");
  await expect(card.getByRole("checkbox", { name: "These profiles belong to the same person", exact: true })).toHaveCount(0);
  expect(state.writes).toHaveLength(0);
});

test("retry fences the displayed link revision and keeps progress", async ({ page }) => {
  const state = await mockHearts(page); state.links = [fixtureLink("needs_attention")];
  await page.goto("/#/integrations/accounts");
  const card = page.getByRole("region", { name: "Shared provider hearts", exact: true });
  await expect(card).toContainText("Needs attention");
  await card.getByRole("button", { name: "Retry combining hearts", exact: true }).click();
  await expect.poll(() => state.retries.length).toBe(1);
  expect(state.retries[0].expectedRevision).toBe(7);
  await expect(card).toContainText("100 rows processed");
});

test("a retry conflict keeps the displayed data until an explicit refresh", async ({ page }) => {
  const state = await mockHearts(page); state.links = [fixtureLink("needs_attention")]; state.retryConflict = true;
  await page.goto("/#/integrations/accounts");
  const card = page.getByRole("region", { name: "Shared provider hearts", exact: true });
  await card.getByRole("button", { name: "Retry combining hearts", exact: true }).click();
  await expect(card.getByRole("alert")).toContainText("Refresh before retrying");
  await expect(card.getByRole("status", { name: "Sharing status", exact: true })).toContainText("Needs attention");
  await expect(card).toContainText("100 rows processed");
  expect(state.retries[0].expectedRevision).toBe(7);
});
