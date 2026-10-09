import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, intelligence, normalizeResponse, sources } from "./api";

afterEach(() => vi.unstubAllGlobals());

describe("API response normalization", () => {
  it("normalizes nested ASP.NET property names without changing values", () => {
    expect(normalizeResponse({
      Accounts: [{ Id: "account", Secret: { Configured: true } }],
      TechnicalDetails: { sourceId: "track" },
    })).toEqual({
      accounts: [{ id: "account", secret: { configured: true } }],
      technicalDetails: { sourceId: "track" },
    });
  });

  it("preserves structured PascalCase diagnostic errors", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({
      Stage: "capability",
      Error: "The selected provider does not expose streaming diagnostics.",
      ValidationDetails: { ProviderId: "deezer" },
    }), { status: 400, statusText: "Bad Request" })));

    const error = await sources.deepStream("deezer", "account").catch((cause) => cause);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({
      message: "capability: The selected provider does not expose streaming diagnostics.",
      details: {
        stage: "capability",
        error: "The selected provider does not expose streaming diagnostics.",
        validationDetails: { providerId: "deezer" },
      },
    });
  });

  it("creates a listening-app key for the selected backend", async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({
      Id: "token-id", Token: "private-key", RelayExternally: false, CreatedAt: "2026-01-01T00:00:00Z",
    }), { status: 201 }));
    vi.stubGlobal("fetch", fetch);

    const created = await intelligence.createListeningApp({
      protocol: "jellyfin", backendInstanceId: "main",
    }, false);

    expect(created).toMatchObject({ id: "token-id", token: "private-key", relayExternally: false });
    expect(fetch).toHaveBeenCalledWith("/api/admin/intelligence/listening-apps", expect.objectContaining({
      method: "POST",
      body: JSON.stringify({
        protocol: "jellyfin", backendInstanceId: "main",
        sendToConnectedServices: false,
      }),
    }));
  });

  it("creates a Personal or Shared provider account without a library scope", async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({
      Id: "account-1", ProviderId: "qobuz", Scope: "Personal", CanManage: true,
      Enabled: true, Revision: 1, Secret: { Configured: true, Revoked: false },
    }), { status: 201 }));
    vi.stubGlobal("fetch", fetch);

    await sources.create({
      providerId: "qobuz", displayName: "My Qobuz", scope: "Personal", enabled: true,
      secret: { userId: "listener" },
    });

    expect(fetch).toHaveBeenCalledWith("/api/admin/provider-accounts", expect.objectContaining({
      method: "POST",
      body: JSON.stringify({
        providerId: "qobuz", displayName: "My Qobuz", scope: "Personal", enabled: true,
        secret: { userId: "listener" },
      }),
    }));
  });
});
