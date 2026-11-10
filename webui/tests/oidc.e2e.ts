import { expect, test } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { settingsFixture } from "./shared-design-fixtures";

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
