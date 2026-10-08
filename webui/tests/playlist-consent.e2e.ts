import { expect, test, type Page, type Route } from "@playwright/test";

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}

async function mockConsentApp(page: Page) {
  let authenticated = false;
  let granted = false;
  const loginBodies: Array<Record<string, unknown>> = [];
  const consentWrites: Array<{ method: string; body?: Record<string, unknown> }> = [];

  await page.route("**/api/admin/**", async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === "/api/admin/auth/me") return json(route, {
      authenticated,
      backend: "Subsonic",
      user: authenticated ? { id: "listener", name: "Listener", isAdministrator: false } : undefined,
    });
    if (path === "/api/admin/auth/oidc/status") return json(route, {
      enabled: true,
      displayName: "Home SSO",
      loginUrl: "/api/admin/auth/oidc/login",
      linkPending: false,
      linked: false,
    });
    if (path === "/api/admin/auth/login") {
      const body = request.postDataJSON() as Record<string, unknown>;
      loginBodies.push(body);
      authenticated = true;
      granted = Boolean(body.managePlaylists);
      return json(route, {
        authenticated: true,
        backend: "Subsonic",
        user: { id: "listener", name: "Listener", isAdministrator: false },
      });
    }
    if (path === "/api/admin/auth/playlist-consent") {
      if (request.method() === "POST") {
        const body = request.postDataJSON() as Record<string, unknown>;
        consentWrites.push({ method: "POST", body });
        granted = true;
      } else if (request.method() === "DELETE") {
        consentWrites.push({ method: "DELETE" });
        granted = false;
      }
      return json(route, {
        supported: true,
        granted,
        updatedAt: granted ? "2026-10-08T14:00:00Z" : null,
      });
    }
    if (path === "/api/admin/ui/schema") return json(route, {
      listenersCanConnectOwnAccounts: true,
      providers: [],
      settings: [],
      priorityGroups: [],
    });
    if (path === "/api/admin/provider-accounts") return json(route, {
      listenersCanConnectOwnAccounts: true,
      audienceUsers: [],
      accounts: [],
    });
    if (path === "/api/admin/updates/stream")
      return route.fulfill({ contentType: "text/event-stream", body: ": fixture\n\n" });
    return json(route, {});
  });

  return { loginBodies, consentWrites };
}

test("Subsonic listener explicitly grants and revokes playlist management", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 844 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  const fixture = await mockConsentApp(page);

  await page.goto("/#/integrations/accounts");
  const consent = page.getByRole("checkbox", { name: "Let Allstarr manage my playlists" });
  await expect(consent).toBeVisible();
  await expect(consent).not.toBeChecked();
  await expect(page.getByRole("link", { name: "Continue with Home SSO" })).toHaveAttribute(
    "href",
    "/api/admin/auth/oidc/login",
  );

  await page.getByLabel("Username", { exact: true }).fill("listener");
  await page.getByLabel("Password", { exact: true }).fill("login-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Playlist management" })).toBeVisible();
  expect(fixture.loginBodies).toEqual([{
    username: "listener",
    password: "login-password",
    rememberMe: true,
    managePlaylists: false,
  }]);

  await expect(page.getByText("Library reads and sign-in work without this access.")).toBeVisible();
  await page.getByLabel("Subsonic password").fill("reauth-password");
  await page.getByRole("button", { name: "Allow playlist management" }).click();
  await expect(page.getByText("Allowed", { exact: true })).toBeVisible();
  await expect(page.getByRole("status").filter({ hasText: "Playlist management allowed" })).toBeVisible();
  expect(fixture.consentWrites[0]).toEqual({ method: "POST", body: { password: "reauth-password" } });

  await page.getByRole("button", { name: "Revoke access" }).click();
  await expect(page.getByRole("button", { name: "Allow playlist management" })).toBeVisible();
  await expect(page.getByRole("status").filter({ hasText: "Playlist management revoked." })).toBeVisible();
  expect(fixture.consentWrites[1]).toEqual({ method: "DELETE" });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test("Jellyfin login and unsupported account status do not show Subsonic consent", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.route("**/api/admin/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/admin/auth/me") return json(route, { authenticated: false, backend: "Jellyfin" });
    if (path === "/api/admin/auth/oidc/status") return json(route, { enabled: false });
    return json(route, {});
  });
  await page.goto("/");
  await expect(page.getByRole("checkbox", { name: "Let Allstarr manage my playlists" })).toHaveCount(0);
});


test("Subsonic native sign-in saves explicitly checked consent", async ({ page }) => {
  const fixture = await mockConsentApp(page);
  await page.goto("/#/integrations/accounts");
  await page.getByRole("checkbox", { name: "Let Allstarr manage my playlists" }).check();
  await page.getByLabel("Username", { exact: true }).fill("listener");
  await page.getByLabel("Password", { exact: true }).fill("login-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByText("Allowed", { exact: true })).toBeVisible();
  expect(fixture.loginBodies[0].managePlaylists).toBe(true);
  expect(fixture.consentWrites).toEqual([]);
});
