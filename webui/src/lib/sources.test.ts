import { describe, expect, it } from "vitest";
import type { ProviderAccount, ProviderDefinition, ProviderHealth } from "./api";
import {
  accountSettings,
  audienceLabel,
  ctsMeasurementLabel,
  sourceMetrics,
  sourceNeedsAccount,
  sourceStatus,
  supportsStreamingDiagnostic,
  humanize,
  settingDefault,
  sourceTimingLabel,
} from "./sources";

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
  it("humanizes camel-case lifecycle labels", () => {
    expect(humanize("reviewRequired")).toBe("Review Required");
  });

  it("uses manifest defaults without exposing sensitive values", () => {
    expect(settingDefault({ key: "storefront", label: "Storefront", type: "text", defaultValueJson: '"us"' })).toBe("us");
    expect(settingDefault({ key: "enabled", label: "Enabled", type: "toggle", defaultValueJson: "true" })).toBe(true);
    expect(settingDefault({ key: "token", label: "Token", type: "password", sensitive: true, defaultValueJson: '"secret"' })).toBe("");
  });

  it("labels timing sources and honest empty states", () => {
    expect(sourceTimingLabel({
      ...provider,
      runtimeCapabilities: [{ id: "metadata", ready: true, canAttempt: true, canTest: true, latencyMilliseconds: 42 }],
    })).toBe("Latest API 42 ms");
    expect(sourceTimingLabel(provider, { providerId: provider.id, enabledAccountCount: 1, capabilityTotal: 1, healthyCapabilityCount: 1, failedCapabilityCount: 0, p95LatencyMilliseconds: 17 }))
      .toBe("Managed p95 17 ms");
    expect(sourceTimingLabel({ ...provider, runtimeCapabilities: [{ id: "metadata", ready: false, canAttempt: true, canTest: true }] }))
      .toBe("Awaiting first sample");
    expect(sourceTimingLabel({ ...provider, categories: ["streaming"] })).toBe("Manual only");
    expect(sourceTimingLabel(provider)).toBe("Not applicable");
  });

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
    expect(sourceStatus({ id: "qobuz", name: "Qobuz", status: "degraded" }, [], []))
      .toBe("needs_config");
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
    const accountlessExtension: ProviderDefinition = {
      id: "qobuz",
      name: "Qobuz extension",
      implementationOrigin: "extension",
    };
    expect(accountSettings(accountlessExtension)).toEqual([]);
    expect(sourceNeedsAccount(accountlessExtension)).toBe(false);
    expect(sourceStatus({ ...accountlessExtension, status: "available" }, [], []))
      .toBe("available");
  });

  it("offers an optional Koito address without replacing the encrypted token", () => {
    const settings = accountSettings({ id: "listenbrainz", name: "ListenBrainz" });

    expect(settings).toMatchObject([
      { key: "token", type: "password", required: true },
      { key: "baseUrl", type: "url" },
    ]);
    expect(settings[1].required).not.toBe(true);
  });

  it("uses runtime readiness for operator-managed Sources", () => {
    const managed = {
      id: "apple-download",
      name: "Apple Music - Gamdl",
      connectionKind: "operator_managed",
      runtimeCapabilities: [
        { id: "download", ready: true, canAttempt: true, health: "healthy" },
        { id: "lyrics", ready: true, canAttempt: true, health: "healthy" },
      ],
    } satisfies ProviderDefinition;

    expect(sourceMetrics(managed, undefined, [])).toMatchObject({
      total: 2,
      passing: 2,
      failed: 0,
    });
  });

  it("offers CTS only for a typed streaming capability", () => {
    const streaming = {
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
    } satisfies ProviderHealth;

    expect(supportsStreamingDiagnostic([streaming])).toBe(true);
    expect(supportsStreamingDiagnostic([{ ...streaming, supported: false }])).toBe(false);
    expect(supportsStreamingDiagnostic([{ ...streaming, capability: "metadata" }])).toBe(false);
  });

  it("does not present a failed CTS probe as zero-millisecond playback", () => {
    const measurement = {
      providerAccountId: "account-1",
      providerId: "future-extension",
      health: "degraded",
      latencyMs: 0,
      bars: 0,
      testedAt: "2026-08-03T00:00:00Z",
      failureCode: "Unauthorized",
    };

    expect(ctsMeasurementLabel(measurement)).toBe("Failed");
    expect(ctsMeasurementLabel({ ...measurement, health: "healthy", latencyMs: 42.1 }))
      .toBe("42.1 ms");
  });
});
