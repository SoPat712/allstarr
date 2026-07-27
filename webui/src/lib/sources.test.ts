import { describe, expect, it } from "vitest";
import type { ProviderAccount, ProviderDefinition, ProviderHealth } from "./api";
import { accountSettings, audienceLabel, sourceStatus } from "./sources";

const account = (scope: ProviderAccount["scope"] = "User"): ProviderAccount => ({
  id: "account-1",
  providerId: "future-extension",
  displayName: "My source",
  scope,
  enabled: true,
  revision: 1,
  secret: { configured: true, revoked: false },
  createdAt: "2026-07-27T00:00:00Z",
  updatedAt: "2026-07-27T00:00:00Z",
});
const provider: ProviderDefinition = { id: "future-extension", name: "Future Extension" };

describe("source presentation", () => {
  it("uses account readiness for arbitrary schema providers", () => {
    const health: ProviderHealth[] = [{
      provider: "future-extension",
      providerAccountId: "account-1",
      providerAccountName: "My source",
      capability: "streaming",
      accountScope: "user",
      supported: true,
      enabled: true,
      configuration: "configured",
      health: "healthy",
      ready: true,
      canAttempt: true,
      canTest: true,
    }];
    expect(sourceStatus(provider, [account()], health)).toBe("healthy");
    expect(sourceStatus(provider, [account()], [{ ...health[0], health: "degraded" }]))
      .toBe("degraded");
    expect(sourceStatus({ ...provider, status: "disabled" }, [account()], health))
      .toBe("disabled");
  });

  it("labels audiences without revealing credentials", () => {
    expect(audienceLabel({ ...account("Global") })).toBe("Everyone");
    expect(audienceLabel({ ...account(), ownerDisplayName: "Alex" })).toBe("Only Alex");
    expect(audienceLabel({ ...account("Library"), libraryScopeId: "music" }))
      .toBe("Library music");
  });

  it("uses schema-defined extension account settings", () => {
    expect(accountSettings({
      ...provider,
      accountSettings: [{ key: "token", label: "Token", type: "password" }],
    })).toEqual([{ key: "token", label: "Token", type: "password" }]);
  });
});
