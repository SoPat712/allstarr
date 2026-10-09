import { expect, test, type Page, type Request, type Route } from "@playwright/test";

type AuthorityOptions = {
  administrator: boolean;
  resolve?: (route: Route) => Promise<void>;
  authorityAction?: (route: Route) => Promise<void>;
};

function matchFixture(administrator: boolean) {
  return {
    externalSnapshotId: "displayed-snapshot",
    providerId: "spotify",
    state: "ambiguous",
    decisionSource: "manual_authority",
    confidence: .92,
    threshold: .9,
    title: "Layered song",
    artist: "Example artist",
    album: "Example album",
    durationMilliseconds: 180_000,
    allowedAuthorityScopes: administrator ? ["personal", "household"] : ["personal"],
    effectiveAuthorityScope: "personal",
    manualAuthorities: [
      {
        id: "personal-row",
        kind: "provider_match",
        revision: 7,
        reason: "My preferred recording",
        createdAt: "2026-10-08T12:00:00Z",
        effective: true,
        targetProviderId: "qobuz",
        targetExternalId: "personal-track",
        authoritySnapshotId: "displayed-snapshot",
        scope: "personal",
        canEdit: true,
      },
      {
        id: "household-row",
        kind: "local_match",
        revision: 11,
        reason: "Household library recording",
        createdAt: "2026-10-08T11:00:00Z",
        effective: false,
        authoritySnapshotId: "displayed-snapshot",
        scope: "household",
        canEdit: administrator,
      },
    ],
    providerIdentities: [],
    reasons: ["title_match"],
    warnings: [],
    candidates: [{
      libraryTrackId: "local-track",
      backendItemId: "local-track",
      isLocal: true,
      title: "Layered song",
      artist: "Example artist",
      album: "Example album",
      durationMilliseconds: 180_000,
      confidence: .92,
      components: { title: 1, acceptanceQualified: 1, routingPriority: 0 },
    }],
  };
}

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}

async function mockMatching(page: Page, options: AuthorityOptions) {
  await page.route("**/api/admin/**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === "/api/admin/auth/me") return json(route, {
      authenticated: true,
      backend: "Jellyfin",
      user: { id: "listener", name: options.administrator ? "Administrator" : "Listener", isAdministrator: options.administrator },
    });
    if (url.pathname === "/api/admin/auth/oidc/status") return json(route, { enabled: false });
    if (url.pathname === "/api/admin/onboarding/status") return json(route, {
      completed: true,
      setupOpen: false,
      shouldRedirectToSetup: false,
      schemaVersion: "onboarding-v1",
      completedSteps: ["backend-identity"],
      completionSource: "setup-guide",
      revision: 1,
      recoveryNotices: [],
      migration: { available: false, completed: false, firstRun: false },
    });
    if (url.pathname === "/api/admin/ui/schema") return json(route, {
      activeBackend: "Jellyfin",
      providers: [
        { id: "spotify", name: "Spotify", categories: ["metadata"] },
        { id: "qobuz", name: "Qobuz", categories: ["streaming"] },
      ],
    });
    if (url.pathname === "/api/admin/track-matches/displayed-snapshot/resolve")
      return options.resolve?.(route) ?? json(route, { success: true });
    if (url.pathname.includes("/manual-authorities/"))
      return options.authorityAction?.(route) ?? json(route, { rematched: true, state: "accepted" });
    if (url.pathname === "/api/admin/track-matches") return json(route, {
      matches: [matchFixture(options.administrator)],
      stats: {
        total: 1, matched: 1, accepted: 1, unresolved: 0, suggested: 0, review: 1,
        ambiguous: 1, rejected: 0, attention: 1, manualMatches: 1, manualRejections: 0,
      },
      pagination: { page: 1, pageSize: 50, total: 1, totalPages: 1 },
    });
    if (url.pathname === "/api/admin/updates/stream")
      return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return json(route, {});
  });
}

test("listener uses personal authority and cannot choose or edit household", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  let resolution: Record<string, unknown> | null = null;
  await mockMatching(page, {
    administrator: false,
    resolve: async (route) => {
      resolution = route.request().postDataJSON() as Record<string, unknown>;
      await json(route, { success: true });
    },
  });
  await page.goto("#/library/mappings");

  const household = page.locator(".manual-authority-row").filter({ hasText: "Household" });
  await expect(household).toContainText("Superseded by personal");
  await expect(household.getByRole("button", { name: "Clear household", exact: true })).toBeDisabled();
  await expect(household.getByRole("button", { name: "Clear household and rematch", exact: true })).toBeDisabled();

  await page.getByRole("button", { name: "Interactive search" }).click();
  const dialog = page.getByRole("dialog", { name: "Layered song" });
  await expect(dialog.getByText("Save decision for")).toHaveCount(0);
  await expect(dialog.getByRole("radio", { name: /^Household\b/ })).toHaveCount(0);
  await dialog.getByRole("button", { name: "Cancel" }).click();

  await page.getByRole("button", { name: "Accept", exact: true }).click();
  await expect.poll(() => resolution).not.toBeNull();
  expect(resolution).toMatchObject({
    authorityScope: "personal",
    expectedAuthority: { id: "personal-row", revision: 7 },
  });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test("matching sends an actual local library filter as backendLibraryId", async ({ page }) => {
  await mockMatching(page, { administrator: true });
  await page.goto("#/library/mappings");
  await page.getByLabel("Library scope").fill("music-library");
  const filtered = page.waitForRequest((request) => {
    const url = new URL(request.url());
    return url.pathname === "/api/admin/track-matches" &&
      url.searchParams.get("backendLibraryId") === "music-library";
  });
  await page.getByRole("button", { name: "Apply" }).click();
  const query = new URL((await filtered).url()).searchParams;
  expect(query.has("libraryScopeId")).toBe(false);
});

test("administrator household save binds the household row while personal is effective", async ({ page }) => {
  let requestBody: Record<string, unknown> | null = null;
  await mockMatching(page, {
    administrator: true,
    resolve: async (route) => {
      requestBody = route.request().postDataJSON() as Record<string, unknown>;
      await json(route, { error: "The household authority changed on the server." }, 409);
    },
  });
  await page.goto("#/library/mappings");
  await page.getByRole("button", { name: "Interactive search" }).click();
  const dialog = page.getByRole("dialog", { name: "Layered song" });
  await dialog.getByRole("radio", { name: /^Household\b/ }).check();
  await dialog.getByRole("button", { name: "Choose candidate" }).click();

  expect(requestBody).toMatchObject({
    authorityScope: "household",
    expectedAuthority: { id: "household-row", revision: 11 },
  });
  await expect(dialog.getByRole("alert")).toContainText("household authority changed");
});

test("authority clear and rematch send their displayed scope and revision", async ({ page }) => {
  const requests: Request[] = [];
  await mockMatching(page, {
    administrator: true,
    authorityAction: async (route) => {
      requests.push(route.request());
      if (route.request().method() === "DELETE") return route.fulfill({ status: 204, body: "" });
      await json(route, { rematched: true, state: "accepted" });
    },
  });
  await page.goto("#/library/mappings");

  const household = page.locator(".manual-authority-row").filter({ hasText: "Household" });
  await household.getByRole("button", { name: "Clear household and rematch", exact: true }).click();
  const rematchDialog = page.getByRole("alertdialog", { name: "Clear household and rematch?" });
  await expect(rematchDialog).toContainText("personal decision remains effective");
  await rematchDialog.getByRole("button", { name: "Clear household and rematch", exact: true }).click();
  await expect.poll(() => requests.length).toBe(1);
  expect(new URL(requests.at(0)!.url()).pathname).toContain(
    "/displayed-snapshot/manual-authorities/household-row/rematch",
  );
  expect(requests.at(0)!.postDataJSON()).toEqual({
    kind: "local_match",
    expectedRevision: 11,
    authorityScope: "household",
  });

  const personal = page.locator(".manual-authority-row").filter({ hasText: "Personal" });
  await personal.getByRole("button", { name: "Clear personal", exact: true }).click();
  const clearDialog = page.getByRole("alertdialog", { name: "Clear personal decision?" });
  await expect(clearDialog).toContainText("household decision beneath it will become effective");
  await clearDialog.getByRole("button", { name: "Clear personal", exact: true }).click();
  await expect.poll(() => requests.length).toBe(2);
  const clearUrl = new URL(requests.at(1)!.url());
  expect(clearUrl.pathname).toContain("/displayed-snapshot/manual-authorities/personal-row");
  expect(clearUrl.searchParams.get("authorityScope")).toBe("personal");
  expect(clearUrl.searchParams.get("kind")).toBe("provider_match");
  expect(clearUrl.searchParams.get("expectedRevision")).toBe("7");
});
