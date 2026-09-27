import { expect, test } from "@playwright/test";

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
