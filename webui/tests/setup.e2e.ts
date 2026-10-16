import { expect, test } from "@playwright/test";

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Fresh setup verifies and waits for restart ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript((theme) => localStorage.setItem("allstarr.theme", theme), theme);
    let saved = false;
    let pending = true, pendingPolls = 0;
    await page.route("**/api/admin/auth/me", (route) => { if (saved && pending) pendingPolls++; return route.fulfill({ json: {
      authenticated: false, backend: "Jellyfin", backendConfigured: saved, restartPending: saved && pending,
    } }); });
    await page.route("**/api/admin/auth/oidc/**", (route) => route.fulfill({ json: { enabled: false } }));
    await page.route("**/api/admin/auth/setup", async (route) => {
      if (route.request().method() === "GET") return route.fulfill({ json: { storageReady: true, configured: false, csrfToken: "fixture-csrf" } });
      const body = route.request().postDataJSON();
      expect(body.backend).toBe("Subsonic");
      expect(body.useForLibraryScans).toBe(false);
      expect(route.request().headers()["x-allstarr-csrf"]).toBe("fixture-csrf");
      saved = true;
      return route.fulfill({ json: { requiresRestart: true } });
    });
    await page.route("**/health", (route) => route.fulfill({ json: { status: "healthy" } }));
    await page.goto("#/");
    await expect(page.getByRole("heading", { name: "Connect your media server" })).toBeVisible();
    await page.getByLabel("Media server type").selectOption("Subsonic");
    await page.getByLabel("Media server address").fill("http://fixture.invalid:4533");
    await page.getByLabel("Administrator username").fill("fixture");
    await page.getByLabel("Administrator password").fill("fixture-only");
    await page.getByRole("button", { name: "Verify and save" }).click();
    await expect(page.getByRole("status")).toContainText("saved");
    await expect.poll(() => pendingPolls).toBeGreaterThan(0);
    await expect(page.getByRole("heading", { name: "Your music, connected." })).toBeHidden();
    pending = false;
    await expect(page.getByRole("heading", { name: "Your music, connected." })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
}

test("Fresh setup keeps verification errors recoverable and refuses unavailable storage", async ({ page }) => {
  await page.route("**/api/admin/auth/me", (route) => route.fulfill({ json: { authenticated: false, backendConfigured: false } }));
  await page.route("**/api/admin/auth/oidc/**", (route) => route.fulfill({ json: { enabled: false } }));
  let storageReady = false;
  await page.route("**/api/admin/auth/setup", (route) => route.request().method() === "GET"
    ? route.fulfill({ json: { storageReady, csrfToken: "fixture" } })
    : route.fulfill({ status: 403, json: { error: "The media server did not verify an administrator." } }));
  await page.goto("#/");
  await expect(page.getByText(/data folder is not ready/i)).toBeVisible();
  await expect(page.getByRole("button", { name: "Verify and save" })).toBeDisabled();
  storageReady = true;
  await page.getByRole("button", { name: "Check again" }).click();
  await page.getByLabel("Media server address").fill("http://fixture.invalid");
  await page.getByLabel("Administrator username").fill("fixture");
  await page.getByLabel("Administrator password").fill("fixture-only");
  await page.getByRole("button", { name: "Verify and save" }).click();
  await expect(page.getByRole("alert")).toContainText("did not verify");
  await expect(page.getByLabel("Administrator password")).toHaveValue("");
  await expect(page.getByRole("button", { name: "Verify and save" })).toBeEnabled();
});

const guideSteps = [
  { id: "backend-identity", title: "Your media server", explanation: "Your verified account owns library access.", required: true, href: null },
  { id: "legacy-environment", title: "Review previous settings", explanation: "Preview first. Original files stay read-only.", required: false, href: "#/settings/setup" },
  { id: "sources", title: "Choose your sources", explanation: "SquidWTF is retired. Choose your services; no replacement is selected automatically.", required: false, href: "#/integrations/services" },
  { id: "apple", title: "Optional Apple downloads", explanation: "Apple downloads are off by default. Run the displayed host command only when you want downloads.", required: false, href: "#/integrations/services/apple-musickit" },
];
async function guideFixture(page: import("@playwright/test").Page, finished = false) {
  const reviewed = new Set<string>(); const skipped = new Set<string>();
  if (finished) reviewed.add("backend-identity");
  let revision = 0, complete = finished;
  const state = () => ({ completed: complete, setupOpen: true, shouldRedirectToSetup: !reviewed.has("backend-identity"),
    completedSteps: [...reviewed], skippedSteps: [...skipped], revision, recoveryNotices: [],
    migration: { available: true, completed: false }, steps: guideSteps.map((step) => ({ ...step, state: reviewed.has(step.id) ? "reviewed" : skipped.has(step.id) ? "skipped" : "pending" })),
  });
  await page.route("**/api/admin/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith("/auth/me")) return route.fulfill({ json: { authenticated: true, backend: "Jellyfin", backendConfigured: true, user: { id: "fixture", name: "Fixture", isAdministrator: true } } });
    if (path.endsWith("/onboarding/status") || path.endsWith("/onboarding/reopen")) return route.fulfill({ json: state() });
    if (path.endsWith("/onboarding/step")) {
      const body = route.request().postDataJSON(); expect(body.expectedRevision).toBe(revision);
      reviewed.delete(body.stepId); skipped.delete(body.stepId);
      (body.action === "skip" ? skipped : reviewed).add(body.stepId); revision++;
      return route.fulfill({ json: state() });
    }
    if (path.endsWith("/onboarding/complete")) { complete = true; revision++; return route.fulfill({ json: state() }); }
    if (path.endsWith("/ui/home")) return route.fulfill({ json: { schema: { providers: [] }, status: {}, stats: {}, activity: { items: [] }, providerHealth: { providers: [] } } });
    if (path.endsWith("/ui/now-playing")) return route.fulfill({ json: { items: [] } });
    if (path.endsWith("/ui/schema")) return route.fulfill({ json: { configSections: [], providers: [], priorityGroups: [] } });
    if (path.endsWith("/config")) return route.fulfill({ json: {} });
    if (path.endsWith("/migration/status")) return route.fulfill({ json: { available: true, sourcePresent: true, completed: false } });
    if (path.endsWith("/access/backend")) return route.fulfill({ json: { settings: { supported: false, revision: "r1" }, csrfToken: "fixture" } });
    return route.fulfill({ json: {} });
  });
}
for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Shared guide saves review, skip and resume ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript((theme) => localStorage.setItem("allstarr.theme", theme), theme);
    await guideFixture(page);
    await page.goto("#/");
    const guide = page.getByRole("region", { name: "Set up Allstarr" });
    await expect(guide).toBeVisible();
    await expect(guide.getByRole("heading", { name: "Your media server", exact: true })).toBeVisible();
    await guide.getByRole("button", { name: "Reviewed, continue" }).focus();
    await page.keyboard.press("Enter");
    await expect(guide.getByRole("heading", { name: "Review previous settings", exact: true })).toBeVisible();
    await guide.getByRole("button", { name: "Skip for now" }).click();
    await expect(guide.getByRole("heading", { name: "Choose your sources", exact: true })).toBeVisible();
    await expect(guide.getByText(/no replacement is selected automatically/)).toBeVisible();
    await page.reload();
    await expect(guide.getByRole("heading", { name: "Choose your sources", exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: /Review previous settings Skipped/ })).toBeVisible();
    await page.getByRole("button", { name: /Optional Apple downloads Optional/ }).click();
    await expect(guide.getByText(/Apple downloads are off by default/)).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `/tmp/allstarr-setup-${width}-${theme}.png`, fullPage: true });
    await guide.getByRole("button", { name: "Finish setup", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Home", exact: true, level: 1 })).toBeVisible();
    await page.reload();
    await expect(guide).toBeHidden();
  });
}

test("Mounted import explains changes and queues originals only after review", async ({ page }) => {
  await guideFixture(page);
  let applied = false;
  await page.route("**/api/admin/config/migration/preview-mounted", (route) => route.fulfill({ json: {
    previewToken: "preview", revision: "r1", canApply: true, importedSettingCount: 1, providerAccountCount: 1,
    playlistLinkCount: 0, scheduleCount: 0, manualCount: 1,
    media: { files: 2, identified: 1, needsIdentification: 1, notices: [] }, conflicts: [], warnings: [],
    items: [
      { key: "AUDIO_QUALITY", action: "import_setting", reason: "Save your quality preference.", sensitive: false, valuePreview: "flac" },
      { key: "MUSIC_SERVICE", action: "choose_source", reason: "Choose a replacement for the retired service.", sensitive: false },
      { key: "DEEZER_ARL", action: "deprecated", reason: "Credential values stay hidden.", sensitive: true, valuePreview: "must-never-display" },
    ],
  } }));
  await page.route("**/api/admin/config/migration/apply", (route) => { applied = true; return route.fulfill({ json: { success: true, mediaFilesQueued: 2 } }); });
  await page.goto("#/settings/setup");
  await page.getByRole("button", { name: /Review previous settings Optional/ }).click();
  await page.getByRole("button", { name: "Preview mounted settings" }).click();
  await expect(page.getByRole("heading", { name: "Ready to import (1)" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Needs your choice (1)" })).toBeVisible();
  await expect(page.getByText("must-never-display")).toHaveCount(0);
  expect(applied).toBe(false);
  await expect(page.getByRole("button", { name: "Import preview" })).toBeDisabled();
  await page.getByRole("checkbox").check();
  await page.getByRole("button", { name: "Import preview" }).click();
  await expect(page.getByText(/2 original files queued/)).toBeVisible();
  expect(applied).toBe(true);
});

test("Guide revision conflict preserves the step and supports reload", async ({ page }) => {
  await guideFixture(page);
  await page.route("**/api/admin/onboarding/step", (route) => route.fulfill({ status: 409, json: { error: "Setup changed in another tab. Reload the guide and try again." } }));
  await page.goto("#/settings/setup");
  await page.getByRole("button", { name: "Reviewed, continue" }).click();
  await expect(page.getByRole("alert")).toContainText("another tab");
  await page.getByRole("button", { name: "Reload guide" }).click();
  await expect(page.getByRole("alert")).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Your media server", exact: true })).toBeVisible();
});

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Access and copied-file recovery are usable ${width} ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript((theme) => localStorage.setItem("allstarr.theme", theme), theme);
    await guideFixture(page, true);
    let queued = false;
    await page.route("**/api/admin/storage/files", (route) => route.fulfill({ json: { settings: {
      available: true, pending: queued, needsReview: !queued, files: 3, jobState: queued ? "Pending" : "Failed",
    }, csrfToken: "storage-csrf" } }));
    await page.route("**/api/admin/storage/files/verify", (route) => {
      expect(route.request().headers()["x-allstarr-csrf"]).toBe("storage-csrf"); queued = true;
      return route.fulfill({ json: { message: "Saved-file verification is queued." } });
    });
    await page.route("**/api/admin/cache**", (route) => route.fulfill({ json: { categories: [], memory: { payloadBytes: 0, misses: 0 }, media: { payloadBytes: 0, misses: 0, reclaimableBytes: 0 }, metadata: { reclaimableBytes: 0 }, activity: {}, artworkLimits: { maximumEntryBytes: 0, maximumDecodedPixels: 0 }, extensionStorage: { activeExtensions: 0, entryCount: 0, payloadBytes: 0, maximumBytes: 0 }, unreferencedArtworkBytes: 0 } }));
    let attempted = false;
    await page.route("**/api/admin/access/oidc", (route) => {
      if (route.request().method() === "GET") return route.fulfill({ json: { settings: {
        enabled: true, authority: "https://issuer.invalid", clientId: "fixture", publicUrl: "https://allstarr.invalid", displayName: "Household sign-in", hasClientSecret: true, needsReview: false, revision: "revision-1",
      }, csrfToken: "access-csrf" } });
      attempted = true;
      const body = route.request().postDataJSON();
      expect(body.expectedRevision).toBe("revision-1"); expect(body.clientSecret).toBeNull();
      expect(route.request().headers()["x-allstarr-csrf"]).toBe("access-csrf");
      return route.fulfill({ status: 409, json: { error: "Access settings changed. Reload and try again." } });
    });
    await page.goto("#/settings/network");
    const access = page.getByRole("article", { name: "Single sign-on" });
    await expect(access.getByLabel("Client secret", { exact: true })).toHaveValue("");
    await expect(access.getByText(/A secret is saved/)).toBeVisible();
    await access.getByRole("button", { name: "Save and restart" }).focus(); await page.keyboard.press("Enter");
    await expect(access.getByRole("alert")).toContainText("Reload and try again"); expect(attempted).toBe(true);
    await access.getByRole("button", { name: "Reload access settings" }).click();
    await expect(access.getByRole("alert")).toHaveCount(0);
    await page.goto("#/settings/storage");
    const files = page.getByRole("article", { name: "Saved-file verification" });
    await expect(files.getByRole("status")).toContainText("need review");
    await files.getByRole("button", { name: "Verify saved files" }).click();
    await expect(files.getByRole("button", { name: "Verify saved files" })).toBeDisabled();
    await expect(files.getByText(/Verifying saved files/)).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
}
