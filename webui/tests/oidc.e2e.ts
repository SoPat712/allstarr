import { expect, test } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { settingsFixture } from "./shared-design-fixtures";

for (const [width, theme] of [[1280, "dark"], [390, "light"], [1280, "light"], [390, "dark"]] as const) {
  test(`Single sign-on drafts save with one restart at ${width}px in ${theme}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await settingsFixture(page);
    const writes: Record<string, unknown>[] = [];
    let ready = false;
    let persisted = { enabled: false, authority: "", clientId: "", publicUrl: "", displayName: "Single sign-on", hasClientSecret: false, needsReview: false, revision: "1" };
    await page.route("**/*", route => {
      const url = new URL(route.request().url());
      if (["127.0.0.1", "localhost"].includes(url.hostname)) return route.fallback();
      return route.abort();
    });
    await page.route("**/health", route => route.fulfill({ status: ready ? 200 : 503, json: {} }));
    await page.route("**/api/admin/auth/me", route => route.fulfill({ json: {
      authenticated: true, backend: "Jellyfin", backendConfigured: true, restartPending: writes.length > 0 && !ready,
      user: { id: "admin", name: "Alex", isAdministrator: true },
    } }));
    await page.route("**/api/admin/access/oidc", async route => {
      if (route.request().method() === "GET") return route.fulfill({ json: { settings: persisted, csrfToken: "fixture-csrf" } });
      expect(route.request().method()).toBe("PUT");
      expect(route.request().headers()["x-allstarr-csrf"]).toBe("fixture-csrf");
      const body = route.request().postDataJSON();
      writes.push(body);
      persisted = { ...persisted, ...body, hasClientSecret: true, revision: "2" };
      delete (persisted as Record<string, unknown>).clientSecret;
      return route.fulfill({ json: { requiresRestart: true } });
    });
    await page.goto("/#/settings/people");
    const card = page.getByRole("article", { name: "Single sign-on", exact: true });
    const toggle = card.getByRole("switch", { name: "Enable single sign-on", exact: true });
    const save = card.getByRole("button", { name: "Save and restart", exact: true });
    const dialog = page.getByRole("alertdialog");
    const fillDraft = async () => {
      await toggle.check();
      await card.getByLabel("Issuer address", { exact: true }).fill("http://identity.home.arpa");
      await card.getByLabel("Client ID", { exact: true }).fill("allstarr");
      await card.getByLabel("Public Allstarr address", { exact: true }).fill("http://music.example.com");
      await card.getByLabel("Sign-in button name", { exact: true }).fill("Home SSO");
      await card.getByLabel("Client secret", { exact: true }).fill("fixture-secret");
    };
    const capture = async (state: string) => {
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      if (state !== "confirm") await page.evaluate(() => window.scrollTo(0, 0));
      await page.screenshot({ path: testInfo.outputPath(`single-sign-on-${state}-${width}-${theme}.png`), fullPage: state !== "confirm" });
    };
    await expect(save).toHaveCount(0);
    await fillDraft();
    await expect(dialog).toHaveCount(0);
    expect(writes).toHaveLength(0);
    await card.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(toggle).not.toBeChecked();
    await expect(save).toHaveCount(0);
    await toggle.check();
    await expect(card.getByLabel("Issuer address", { exact: true })).toHaveValue("");
    await expect(card.getByLabel("Client secret", { exact: true })).toHaveValue("");
    await save.click();
    await expect(card.getByText("Enter the client ID.", { exact: true })).toBeVisible();
    await expect(dialog).toHaveCount(0);
    await fillDraft();
    await expect(card.getByLabel("Redirect URI", { exact: true })).toHaveText("Redirect URI: http://music.example.com/api/admin/auth/oidc/callback");
    await save.click();
    await expect(card.getByLabel("Public Allstarr address", { exact: true })).toHaveAttribute("aria-invalid", "true");
    await expect(card.getByText("A public address must use https. A private or local address may use http.", { exact: true })).toBeVisible();
    await expect(dialog).toHaveCount(0);
    expect(writes).toHaveLength(0);
    await capture("validation");
    await card.getByLabel("Public Allstarr address", { exact: true }).fill("http://music.home.arpa");
    await expect(card.getByLabel("Public Allstarr address", { exact: true })).toHaveAttribute("aria-invalid", "false");
    await expect(card.getByLabel("Client secret", { exact: true })).toHaveAttribute("type", "password");
    await capture("draft");
    await save.click();
    await expect(dialog.getByRole("heading", { name: "Save single sign-on and restart Allstarr?", exact: true })).toBeVisible();
    await expect(dialog.getByText("Signed-in pages reconnect after about 20 seconds.", { exact: true })).toBeVisible();
    await capture("confirm");
    await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(card.getByLabel("Client ID", { exact: true })).toHaveValue("allstarr");
    expect(writes).toHaveLength(0);
    await save.click();
    await dialog.getByRole("button", { name: "Save and restart", exact: true }).click();
    await expect(card.getByRole("status")).toHaveText("Access settings are saved. Waiting for Allstarr to restart…");
    expect(writes).toEqual([{
      enabled: true, authority: "http://identity.home.arpa", clientId: "allstarr", publicUrl: "http://music.home.arpa",
      displayName: "Home SSO", clientSecret: "fixture-secret", removeClientSecret: false, expectedRevision: "1",
    }]);
    await capture("restarting");
    ready = true;
    await expect(card.getByLabel("Client secret", { exact: true })).toHaveValue("");
    await expect(card.getByText("Saved. Leave blank to keep it for the same issuer and client.", { exact: true })).toBeVisible();
    await expect(save).toHaveCount(0);
    expect(writes).toHaveLength(1);
  });
}

test("Turning single sign-on off waits for Save and restart and keeps the saved secret", async ({ page }) => {
  await settingsFixture(page);
  const writes: Record<string, unknown>[] = [];
  await page.route("**/health", route => route.fulfill({ status: 503, json: {} }));
  await page.route("**/api/admin/access/oidc", route => {
    if (route.request().method() === "PUT") {
      writes.push(route.request().postDataJSON());
      return route.fulfill({ json: { requiresRestart: true } });
    }
    return route.fulfill({ json: { settings: {
      enabled: true, authority: "https://identity.example.com", clientId: "allstarr", publicUrl: "https://music.example.com",
      displayName: "Home SSO", hasClientSecret: true, needsReview: false, revision: "1",
    }, csrfToken: "fixture-csrf" } });
  });
  await page.goto("/#/settings/people");
  const card = page.getByRole("article", { name: "Single sign-on", exact: true });
  const toggle = card.getByRole("switch", { name: "Enable single sign-on", exact: true });
  await toggle.uncheck();
  await expect(page.getByRole("alertdialog")).toHaveCount(0);
  expect(writes).toHaveLength(0);
  await card.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(toggle).toBeChecked();
  await expect(card.getByLabel("Client secret", { exact: true })).toHaveValue("");
  await expect(card.getByLabel("Issuer address", { exact: true })).toHaveValue("https://identity.example.com");
  await toggle.uncheck();
  await card.getByRole("button", { name: "Save and restart", exact: true }).click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Save and restart", exact: true }).click();
  await expect(card.getByRole("status")).toBeVisible();
  expect(writes).toHaveLength(1);
  expect(writes[0]).toMatchObject({ enabled: false, clientSecret: null, removeClientSecret: false, expectedRevision: "1" });
});

for (const width of [390, 1280]) {
  for (const theme of ["light", "dark"]) {
    test(`Single sign-on shows a live redirect URI at ${width}px in ${theme}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.emulateMedia({ reducedMotion: "reduce" });
      await page.addInitScript(value => {
        localStorage.setItem("allstarr.theme", value);
        Object.defineProperty(navigator, "clipboard", {
          configurable: true,
          value: { writeText: async (text: string) => {
            (window as typeof window & { copiedText?: string }).copiedText = text;
          } },
        });
      }, theme);
      const blocked: string[] = [];
      await settingsFixture(page);
      await page.route("**/*", route => {
        const url = new URL(route.request().url());
        if (["127.0.0.1", "localhost"].includes(url.hostname)) return route.fallback();
        blocked.push(url.href);
        return route.abort();
      });
      await page.route("**/api/admin/access/oidc", route => {
        expect(route.request().method()).toBe("GET");
        return route.fulfill({ json: { settings: {
          enabled: true, authority: "http://identity.home.arpa", clientId: "allstarr",
          publicUrl: "http://192.168.1.50:5275", displayName: "Home SSO",
          hasClientSecret: true, needsReview: false, revision: "1",
        }, csrfToken: "fixture-csrf" } });
      });
      await page.goto("/#/settings/people");
      const card = page.getByRole("article", { name: "Single sign-on", exact: true });
      const redirect = card.getByLabel("Redirect URI", { exact: true });
      const publicAddress = card.getByLabel("Public Allstarr address", { exact: true });
      await expect(card.getByLabel("Issuer address", { exact: true })).toHaveValue("http://identity.home.arpa");
      await expect(card.getByText("HTTP is allowed for private or local addresses. Public addresses need HTTPS.", { exact: true })).toBeVisible();
      await expect(redirect).toHaveText("Redirect URI: http://192.168.1.50:5275/api/admin/auth/oidc/callback");
      await card.getByRole("button", { name: "Copy redirect URI", exact: true }).click();
      await expect.poll(() => page.evaluate(() => (window as typeof window & { copiedText?: string }).copiedText))
        .toBe("http://192.168.1.50:5275/api/admin/auth/oidc/callback");
      await publicAddress.fill("");
      await expect(card.getByRole("button", { name: "Copy redirect URI", exact: true })).toBeDisabled();
      await publicAddress.fill("http://identity.home.arpa/allstarr///");
      await expect(redirect).toHaveText("Redirect URI: http://identity.home.arpa/allstarr/api/admin/auth/oidc/callback");
      await card.getByRole("button", { name: "Copy redirect URI", exact: true }).click();
      await expect.poll(() => page.evaluate(() => (window as typeof window & { copiedText?: string }).copiedText))
        .toBe("http://identity.home.arpa/allstarr/api/admin/auth/oidc/callback");
      await expect(page.getByRole("dialog")).toHaveCount(0);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      expect(await redirect.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
      expect(blocked).toEqual([]);
      const screenshots = new URL("../test-results/oidclan/", import.meta.url);
      await mkdir(screenshots, { recursive: true });
      await publicAddress.scrollIntoViewIfNeeded();
      await page.screenshot({ path: fileURLToPath(new URL(`single-sign-on-${width}-${theme}.png`, screenshots)), fullPage: true });
    });
  }
}

for (const width of [390, 1280]) {
  test(`SSO linking requires native credentials and allows cancellation at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    let pending = true;
    let linked = false;
    let authenticated = false;
    let submitted: unknown;
    await page.route("**/api/admin/**", async (route) => {
      const path = new URL(route.request().url()).pathname;
      if (path === "/api/admin/auth/me") return route.fulfill({ json: {
        authenticated, backend: "Subsonic", user: authenticated ? { name: "Listener", isAdministrator: false } : undefined,
      } });
      if (path === "/api/admin/auth/oidc/status") return route.fulfill({ json: {
        enabled: true, displayName: "Home SSO", loginUrl: "/api/admin/auth/oidc/login",
        linkPending: pending, linked, csrfToken: "fixture-csrf",
      } });
      if (path === "/api/admin/auth/oidc/pending") {
        expect(route.request().method()).toBe("DELETE");
        expect(route.request().headers()["x-allstarr-csrf"]).toBe("fixture-csrf");
        pending = false;
        return route.fulfill({ json: { success: true } });
      }
      if (path === "/api/admin/auth/login") {
        submitted = route.request().postDataJSON();
        expect(route.request().headers()["x-allstarr-csrf"]).toBe("fixture-csrf");
        pending = false;
        linked = authenticated = true;
        return route.fulfill({ json: { authenticated, backend: "Subsonic", user: { name: "Listener", isAdministrator: false } } });
      }
      if (path === "/api/admin/auth/oidc/link") {
        expect(route.request().method()).toBe("DELETE");
        expect(route.request().headers()["x-allstarr-csrf"]).toBe("fixture-csrf");
        linked = false;
        return route.fulfill({ json: { success: true } });
      }
      if (path === "/api/admin/updates/stream") return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
      return route.fulfill({ json: {} });
    });
    await page.goto("/");
    await expect(page.getByRole("button", { name: "Link account and sign in" })).toBeVisible();
    await expect(page.getByText("Allstarr will keep your media-server credential encrypted", { exact: false })).toBeVisible();
    await page.getByRole("button", { name: "Continue without linking" }).click();
    await expect(page.getByRole("link", { name: "Continue with Home SSO" })).toHaveAttribute("href", "/api/admin/auth/oidc/login");
    await expect(page.getByRole("button", { name: "Sign in", exact: true })).toBeVisible();
    pending = true;
    await page.reload();
    await page.getByLabel("Username", { exact: true }).fill("listener");
    await page.getByLabel("Password", { exact: true }).fill("fixture-password");
    await page.getByRole("button", { name: "Link account and sign in" }).click();
    await expect(page.getByRole("button", { name: "Disconnect SSO" })).toBeVisible();
    expect(submitted).toMatchObject({ username: "listener", password: "fixture-password", linkOidc: true });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    page.once("dialog", (dialog) => dialog.accept());
    await page.getByRole("button", { name: "Disconnect SSO" }).click();
    await expect(page.getByRole("button", { name: "Disconnect SSO" })).toHaveCount(0);
  });
}
