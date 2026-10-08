import { expect, test, type Page, type Route } from "@playwright/test";

type PreferenceState = {
  explicitFilter: string;
  showExternalLabel: boolean;
  showExplicitLabel: boolean;
  revision: string;
  usesHouseholdDefaults: boolean;
};

function response(state: PreferenceState) {
  return {
    Values: {
      ExplicitFilter: state.explicitFilter,
      ShowExternalLabel: state.showExternalLabel,
      ShowExplicitLabel: state.showExplicitLabel,
    },
    HouseholdDefaults: {
      ExplicitFilter: "All",
      ShowExternalLabel: true,
      ShowExplicitLabel: true,
    },
    UsesHouseholdDefaults: state.usesHouseholdDefaults,
    Revision: state.revision,
  };
}

async function fulfillJson(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}

async function mockShell(page: Page, administrator: boolean, preferences: (route: Route) => Promise<void>) {
  await page.route("**/api/admin/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/admin/auth/me") return fulfillJson(route, {
      authenticated: true,
      backend: "Jellyfin",
      user: { id: "listener", name: administrator ? "Administrator" : "Listener", isAdministrator: administrator },
    });
    if (path === "/api/admin/auth/oidc/status") return fulfillJson(route, { enabled: false });
    if (path === "/api/admin/onboarding/status") return fulfillJson(route, {
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
    if (path === "/api/admin/preferences") return preferences(route);
    if (path === "/api/admin/updates/stream")
      return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return fulfillJson(route, {});
  });
}

test("listener saves and resets preferences with the latest returned revision", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  const writes: Array<{ method: string; body: Record<string, unknown> }> = [];
  let state: PreferenceState = {
    explicitFilter: "All",
    showExternalLabel: true,
    showExplicitLabel: true,
    revision: "revision-1",
    usesHouseholdDefaults: true,
  };
  await mockShell(page, false, async (route) => {
    const method = route.request().method();
    if (method === "GET") return fulfillJson(route, response(state));
    const body = route.request().postDataJSON() as Record<string, unknown>;
    writes.push({ method, body });
    if (method === "PUT") {
      expect(body.expectedRevision).toBe("revision-1");
      state = {
        explicitFilter: String(body.explicitFilter),
        showExternalLabel: Boolean(body.showExternalLabel),
        showExplicitLabel: Boolean(body.showExplicitLabel),
        revision: "revision-2",
        usesHouseholdDefaults: false,
      };
      return fulfillJson(route, response(state));
    }
    expect(method).toBe("DELETE");
    expect(body).toEqual({ expectedRevision: "revision-2" });
    state = {
      explicitFilter: "All",
      showExternalLabel: true,
      showExplicitLabel: true,
      revision: "revision-3",
      usesHouseholdDefaults: true,
    };
    return fulfillJson(route, response(state));
  });

  await page.goto("/");
  await page.getByRole("button", { name: "More destinations" }).click();
  await page.getByRole("button", { name: "Listening preferences" }).click();
  await expect(page).toHaveURL(/#\/preferences$/);
  await expect(page.getByRole("heading", { name: "Listening preferences", level: 1 })).toBeVisible();
  await expect(page.getByText("affect only your listener account", { exact: false })).toBeVisible();
  await expect(page.getByText("Native library titles remain untouched", { exact: false })).toBeVisible();

  await page.getByRole("radio", { name: "Clean only" }).check();
  await page.getByRole("checkbox", { name: "Show [A] for external tracks", exact: true }).click();
  await page.getByRole("checkbox", { name: "Show [E] for explicit tracks", exact: true }).click();
  await page.getByRole("button", { name: "Save preferences" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Listening preferences saved." })).toBeVisible();
  await expect(page.getByText("Personal choices", { exact: true })).toBeVisible();

  await page.getByRole("button", { name: "Reset to household defaults" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Household defaults restored." })).toBeVisible();
  await expect(page.getByRole("radio", { name: /^All\b/ })).toBeChecked();
  await expect(page.getByRole("checkbox", { name: "Show [A] for external tracks", exact: true })).toBeChecked();
  await expect(page.getByRole("checkbox", { name: "Show [E] for explicit tracks", exact: true })).toBeChecked();
  expect(writes.map(({ method }) => method)).toEqual(["PUT", "DELETE"]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test("administrator reloads a 409 conflict before saving against the refreshed revision", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  let getCount = 0;
  const putBodies: Array<Record<string, unknown>> = [];
  await mockShell(page, true, async (route) => {
    if (route.request().method() === "GET") {
      getCount++;
      return fulfillJson(route, response(getCount === 1 ? {
        explicitFilter: "All", showExternalLabel: true, showExplicitLabel: true,
        revision: "revision-1", usesHouseholdDefaults: true,
      } : {
        explicitFilter: "LegacyMode", showExternalLabel: false, showExplicitLabel: true,
        revision: "revision-server", usesHouseholdDefaults: false,
      }));
    }
    const body = route.request().postDataJSON() as Record<string, unknown>;
    putBodies.push(body);
    if (putBodies.length === 1)
      return fulfillJson(route, { error: "Preferences changed." }, 409);
    return fulfillJson(route, response({
      explicitFilter: String(body.explicitFilter),
      showExternalLabel: Boolean(body.showExternalLabel),
      showExplicitLabel: Boolean(body.showExplicitLabel),
      revision: "revision-next",
      usesHouseholdDefaults: false,
    }));
  });

  await page.goto("/");
  await page.getByRole("link", { name: "Listening preferences" }).click();
  await page.getByRole("radio", { name: "Clean only" }).check();
  await page.getByRole("button", { name: "Save preferences" }).click();
  await expect(page.getByRole("alert")).toContainText("changed in another session");
  await page.getByRole("button", { name: "Reload latest" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Saved explicit filter: LegacyMode" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Save preferences" })).toBeDisabled();
  await page.getByRole("radio", { name: "Clean only" }).check();
  await page.getByRole("button", { name: "Save preferences" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Listening preferences saved." })).toBeVisible();

  expect(putBodies[0].expectedRevision).toBe("revision-1");
  expect(putBodies[1].expectedRevision).toBe("revision-server");
});
