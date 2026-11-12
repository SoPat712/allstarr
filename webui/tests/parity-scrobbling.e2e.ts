import { expect, test } from "@playwright/test";
import { expectContainedMobileGeometry } from "./mobile-geometry";
import { openSourceSettings, waitForSettledOptions, schema, mockApi } from "./parity-support";

test.afterEach(async ({ page }) => { expect(await page.pageErrors()).toEqual([]); });

for (const width of [320, 390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Scrobble repair keeps the saved destination at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.emulateMedia({ reducedMotion: "reduce" });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    const accountId = "11111111-1111-1111-1111-111111111111";
    const secondAccountId = "22222222-2222-2222-2222-222222222222";
    const accounts = [{ id: accountId, providerId: "lastfm", displayName: "Original Last.fm", scope: "Personal", ownerUserId: "user",
      enabled: true, revision: 3, canManage: true, secret: { configured: true, revoked: false } },
    { id: secondAccountId, providerId: "koito", displayName: "Original Koito", scope: "Personal", ownerUserId: "user",
      enabled: true, revision: 7, canManage: true, secret: { configured: true, revoked: false } }];
    await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: { accounts, listenersCanConnectOwnAccounts: true, audienceUsers: [] } }));
    let confirmed = false;
    let retried = false;
    let rejectStale = true;
    let deliveryRevision = 4;
    const resolutions: Record<string, unknown>[] = [];
    await page.route("**/api/admin/scrobbling/deliveries**", route => {
      if (route.request().method() === "POST") {
        const body = route.request().postDataJSON();
        resolutions.push(body);
        if (body.action === "confirm-delivered") confirmed = true;
        else if (rejectStale) { rejectStale = false; deliveryRevision = 5; return route.fulfill({ status: 409, json: { error: "Refresh the delivery before resolving it." } }); }
        else retried = true;
        return route.fulfill({ json: { resolved: true } });
      }
      return route.fulfill({ json: { items: [
        ...(!confirmed ? [{ id: "delivery-uncertain", revision: 2, targetId: "lastfm", state: "permanentfailure", providerCode: "ambiguous",
          kind: "completed", canResend: true, canConfirm: true, canDismiss: false,
          safeMessage: "The destination may already have accepted this listen.", requiresReauthentication: false,
          destinationAccountId: accountId, destinationAccountRevision: 3, sendCount: 1, updatedAt: "2026-10-15T12:00:00Z",
          submission: { title: "Uncertain song", artist: "Artist", occurredAt: "2026-10-15T11:56:00Z" } }] : []),
        ...(!retried ? [{ id: "delivery-reconnect", revision: deliveryRevision, targetId: "koito", state: "permanentfailure", providerCode: "destination-changed",
          kind: "completed", canResend: true, canConfirm: false, canDismiss: false,
          safeMessage: "Reconnect the saved destination account.", requiresReauthentication: true,
          destinationAccountId: secondAccountId, destinationAccountRevision: 2, sendCount: 1, updatedAt: "2026-10-15T12:00:00Z",
          submission: { title: "Reconnect song", artist: "Artist", occurredAt: "2026-10-15T11:52:00Z" } }] : []),
      ] } });
    });
    await page.goto("#/integrations/accounts");
    const panel = page.getByRole("region", { name: "Scrobble delivery" });
    await expect(panel.locator(".group-toggle")).toHaveCount(2);
    for (const group of await panel.locator(".group-toggle").all()) await group.click();
    await expect(panel.getByText("Uncertain song", { exact: true })).toBeVisible();
    await expect(panel.getByText("Original Last.fm", { exact: false })).toBeVisible();
    await panel.getByRole("button", { name: "Already in history" }).click();
    const confirm = page.getByRole("alertdialog", { name: "Confirm delivery from its history?" });
    await expect(confirm).toBeVisible();
    expect(resolutions).toHaveLength(0);
    await confirm.getByRole("button", { name: "Confirm already delivered", exact: true }).click();
    await expect(panel.getByText("Uncertain song", { exact: true })).toHaveCount(0);
    expect(resolutions[0]).toMatchObject({ revision: 2, action: "confirm-delivered" });
    await expect(panel.getByRole("button", { name: "Reconnect saved account" })).toBeVisible();
    await panel.getByRole("button", { name: "Retry", exact: true }).click();
    const retry = page.getByRole("alertdialog", { name: "Retry this saved listen?" });
    await retry.getByRole("button", { name: "Retry to saved account", exact: true }).click();
    await expect(page.getByRole("alert").filter({ hasText: "Refresh the delivery" })).toBeVisible();
    expect(resolutions[1]).toMatchObject({ revision: 4, action: "retry", accountRevision: 7 });
    await page.getByRole("button", { name: "Refresh", exact: true }).click();
    await expect(panel).toHaveAttribute("aria-busy", "false");
    await panel.getByRole("button", { name: "Retry", exact: true }).click();
    await retry.getByRole("button", { name: "Retry to saved account", exact: true }).click();
    await expect(panel.getByText("Reconnect song", { exact: true })).toHaveCount(0);
    expect(resolutions[2]).toMatchObject({ revision: 5, action: "retry", accountRevision: 7 });
    await expectContainedMobileGeometry(page);
  });
}

for (const width of [390, 1280]) {
  test(`Scrobble records without saved listens can only be dismissed at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await mockApi(page);
    let dismissed = false;
    const writes: Record<string, unknown>[] = [];
    await page.route("**/api/admin/scrobbling/deliveries**", route => {
      if (route.request().method() === "POST") {
        writes.push(route.request().postDataJSON());
        dismissed = true;
        return route.fulfill({ json: { resolved: true } });
      }
      return route.fulfill({ json: { items: [
        { id: "expired-notice", revision: 1, targetId: "listenbrainz", kind: "nowplaying", state: "permanentfailure",
          providerCode: "400", safeMessage: "Old now-playing notice", canResend: false, canConfirm: false, canDismiss: false,
          requiresReauthentication: false, sendCount: 1, updatedAt: "2026-10-20T12:00:00Z" },
        ...(!dismissed ? [{ id: "missing-listen", revision: 3, targetId: "lastfm", kind: "completed", state: "permanentfailure",
          providerCode: "payload-unavailable", canResend: false, canConfirm: false, canDismiss: true,
          unavailableReason: "This delivery has no saved completed listen, so it cannot be resent or confirmed. You can dismiss it.",
          requiresReauthentication: false, sendCount: 0, updatedAt: "2026-10-20T12:00:00Z" }] : []),
        { id: "unknown-song", revision: 2, targetId: "listenbrainz", kind: "completed", state: "permanentfailure",
          providerCode: "song-unidentified", canResend: false, canConfirm: false, canDismiss: true,
          unavailableReason: "We couldn't identify this song after seven days. This listen cannot be sent. You can dismiss it.",
          requiresReauthentication: false, sendCount: 0, updatedAt: "2026-10-20T12:00:00Z" },
      ] } });
    });
    await page.goto("#/integrations/accounts");
    const panel = page.getByRole("region", { name: "Scrobble delivery" });
    await expect(panel).not.toContainText("Old now-playing notice");
    await expect(panel).toContainText("no saved completed listen");
    await expect(panel).toContainText("couldn't identify this song after seven days");
    await expect(panel.getByRole("button", { name: /Retry|Already in history|Confirm destination/ })).toHaveCount(0);
    for (const group of await panel.locator(".group-toggle").all()) await group.click();
    await expect(panel.getByRole("button", { name: "Dismiss", exact: true })).toHaveCount(2);
    await panel.getByRole("button", { name: "Dismiss", exact: true }).first().click();
    await expect.poll(() => writes.length).toBe(1);
    expect(writes[0]).toEqual({ revision: 3, action: "dismiss" });
    await expect(panel).not.toContainText("no saved completed listen");
    await expectContainedMobileGeometry(page);
  });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Compatible scrobble targets keep encrypted tokens at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    await page.route("**/api/admin/ui/schema", route => route.fulfill({ json: {
      ...schema, providers: [...schema.providers, { id: "multi-scrobbler", name: "multi-scrobbler", categories: ["scrobbling"] },
        { id: "koito", name: "Koito", categories: ["scrobbling"] }],
    } }));
    let created: Record<string, unknown> | undefined;
    let replacement: Record<string, unknown> | undefined;
    let accounts: Record<string, unknown>[] = [];
    await page.route("**/api/admin/provider-accounts", route => {
      if (route.request().method() === "POST") {
        created = route.request().postDataJSON();
        const secret = created!.secret as Record<string, string>;
        const account = { id: "scrobble-account", providerId: created!.providerId, displayName: created!.providerId,
          scope: "Personal", ownerUserId: "user", enabled: true, revision: 1, canManage: true,
          configuredFields: ["token", "baseUrl"], configuration: { baseUrl: secret.baseUrl }, secret: { configured: true, revoked: false } };
        accounts = [account];
        return route.fulfill({ json: account });
      }
      return route.fulfill({ json: { accounts, listenersCanConnectOwnAccounts: true, audienceUsers: [] } });
    });
    await page.route("**/api/admin/provider-accounts/scrobble-account/secret", route => {
      replacement = route.request().postDataJSON();
      return route.fulfill({ json: { revision: 2 } });
    });
    for (const id of ["multi-scrobbler", "koito"]) {
      accounts = [];
      await page.goto(`#/integrations/services/${id}`);
      await openSourceSettings(page, id === "koito" ? "Koito" : id);
      await page.getByRole("button", { name: "Connect account", exact: true }).click();
      const dialog = page.locator(".connect-source-dialog");
      const token = dialog.locator('input[name="token"]');
      const address = dialog.locator('input[name="baseUrl"]');
      await expect(token).toHaveAttribute("type", "password");
      await expect(token).toHaveAttribute("required", "");
      await expect(address).toHaveAttribute("required", "");
      await token.fill("private-fixture-token");
      const baseUrl = id === "koito" ? "http://koito:4110/apis/listenbrainz/1/" : "http://multi-scrobbler:9078/1/";
      await address.fill(baseUrl);
      await dialog.getByRole("button", { name: /^(Connect|Save changes)$/, exact: true }).click();
      await expect(dialog).toHaveCount(0);
      expect(created).toMatchObject({ providerId: id, secret: { token: "private-fixture-token", baseUrl } });
      await expect(page.getByText("private-fixture-token", { exact: true })).toHaveCount(0);
      await page.getByRole("button", { name: "Edit configuration", exact: true }).click();
      const edit = page.getByRole("dialog", { name: `Configure ${id === "koito" ? "Koito" : id}` });
      await expect(edit.locator('input[name="token"]')).toHaveValue("");
      await expect(edit.locator('input[name="token"]')).not.toHaveAttribute("required", "");
      await expect(edit.locator('input[name="baseUrl"]')).toHaveValue(baseUrl);
      await edit.locator('input[name="baseUrl"]').fill("https://listening.example/1/");
      await edit.getByRole("button", { name: /^(Connect|Save changes)$/, exact: true }).click();
      await expect(edit).toHaveCount(0);
      expect(replacement).toMatchObject({ secret: { baseUrl: "https://listening.example/1/" } });
      expect(JSON.stringify(replacement)).not.toContain("private-fixture-token");
      await expectContainedMobileGeometry(page);
    }
  });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
  test(`Older scrobbles require a destination choice at ${width}px ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.addInitScript(value => localStorage.setItem("allstarr.theme", value), theme);
    await mockApi(page);
    const account = (id: string, displayName: string, revision: number, scope = "Personal", ownerUserId: string | null = "user") => ({
      id, displayName, providerId: "lastfm", enabled: true, revision, scope, ownerUserId, canManage: true,
      secret: { configured: true, revoked: false },
    });
    await page.route("**/api/admin/provider-accounts", route => route.fulfill({ json: {
      accounts: [account("first", "Primary", 3), account("second", "Secondary", 9, "Shared", null), account("foreign", "Other listener", 1, "Personal", "other")],
      listenersCanConnectOwnAccounts: true, audienceUsers: [],
    } }));
    let sent: Record<string, unknown> | undefined;
    await page.route("**/api/admin/scrobbling/deliveries**", route => {
      if (route.request().method() === "POST") {
        sent = route.request().postDataJSON();
        return route.fulfill({ json: { resolved: true } });
      }
      return route.fulfill({ json: { items: sent ? [] : [{ id: "old-listen", revision: 2, targetId: "lastfm", state: "permanentfailure",
        kind: "completed", canResend: true, canConfirm: true, canDismiss: false,
        providerCode: "destination-unbound", safeMessage: "This older listen has no saved destination or confirmed outcome.",
        requiresReauthentication: false, destinationAccountId: null, suggestedDestinationAccountId: "first",
        availableDestinationAccountIds: ["first", "second"], sendCount: 0, updatedAt: "2026-10-15T12:00:00Z",
        submission: { title: "Older listen", artist: "Artist", occurredAt: "2026-10-14T11:56:00Z" },
      }] } });
    });
    await page.goto("#/integrations/accounts");
    const panel = page.getByRole("region", { name: "Scrobble delivery" });
    await panel.locator(".group-toggle").click();
    await panel.getByRole("button", { name: "Destination account for older listen", exact: true }).click();
    await waitForSettledOptions(page.getByRole("listbox"), "Secondary (Shared)");
    await expect(page.getByRole("option", { name: /Other listener/ })).toHaveCount(0);
    await page.getByRole("option", { name: "Secondary (Shared)", exact: true }).click();
    await panel.getByRole("button", { name: "Retry", exact: true }).click();
    const confirm = page.getByRole("alertdialog", { name: "Confirm destination?" });
    await expect(confirm).toContainText("Check the destination's history first.");
    expect(sent).toBeUndefined();
    await confirm.getByRole("button", { name: "Retry to saved account", exact: true }).click();
    await expect(panel.getByText("Older listen", { exact: true })).toHaveCount(0);
    expect(sent).toMatchObject({ revision: 2, action: "retry", accountRevision: 9, destinationAccountId: "second" });
    await expectContainedMobileGeometry(page);
  });
}
