import { describe, expect, it } from "vitest";
import type { ProviderAccount, ProviderDefinition, ProviderHealth } from "./api";
import {
  supportedAudiences,
  sharingDescription,
  accountSettings,
  accountSubtitle,
  accountOwnershipLabel,
  providerDisplayName,
  lyricsSourceDisplayName,
  audienceLabel,
  ctsMeasurementLabel,
  sourceMetrics,
  sourceNeedsAccount,
  sourceOriginLabel,
  sourceStatus,
  sourceIsInUse,
  sourceUpdateNeedsRefresh,
  supportsPlaybackDiagnostic,
  humanize,
  providerReadinessLabel,
  checkGuide,
  checkStatusLabel,
  checkStatusState,
  sourceProblemLabel,
  sourceStatusLabel,
  groupSourceChecks,
  settingDefault,
  sourceTimingLabel,
  sourceServerFields,
} from "./sources";

const account = (scope: ProviderAccount["scope"] = "Personal"): ProviderAccount => ({
  id: "account-1",
  providerId: "future-extension",
  displayName: "My source",
  scope,
  enabled: true,
  revision: 1,
  canManage: true,
  secret: { configured: true, revoked: false },
  createdAt: "2026-07-27T00:00:00Z",
  updatedAt: "2026-07-27T00:00:00Z",
});
const provider: ProviderDefinition = { id: "future-extension", name: "Future Extension" };

describe("source presentation", () => {
  it("keeps unused built-ins available without treating them as connected sources", () => {
    const unused = { id: "koito", name: "Koito", status: "needs_config" };
    expect(sourceIsInUse(unused, [], [])).toBe(false);
    expect(sourceIsInUse(unused, [], [{
      provider: "koito", providerAccountId: null, providerAccountName: null, capability: "scrobbling",
      accountScope: "public", supported: true, enabled: true, configuration: "needs_configuration",
      health: "degraded", ready: false, canAttempt: false, canTest: true,
    }])).toBe(false);
    expect(sourceIsInUse({ ...unused, implementationOrigin: "extension" }, [], [])).toBe(true);
    expect(sourceIsInUse(unused, [{ ...account(), providerId: "koito", enabled: false }], [])).toBe(true);
    expect(sourceIsInUse({ id: "lrclib", name: "LRCLib", status: "available" }, [], [])).toBe(true);
    expect(sourceIsInUse({ ...unused, status: "partial_config" }, [], [])).toBe(true);
    expect(sourceIsInUse({ ...unused, status: "degraded" }, [], [])).toBe(true);
  });

  it("refreshes source changes without fetching the catalog for ordinary playback work", () => {
    expect(sourceUpdateNeedsRefresh({})).toBe(true);
    expect(sourceUpdateNeedsRefresh({ resource: "audit", data: { category: "provider-check" } })).toBe(true);
    expect(sourceUpdateNeedsRefresh({ resource: "audit", data: { category: "runtime-settings" } })).toBe(true);
    expect(sourceUpdateNeedsRefresh({ resource: "job", data: { type: "extension.session.verify" } })).toBe(true);
    expect(sourceUpdateNeedsRefresh({ resource: "job", data: { type: "playback.signal" } })).toBe(false);
    expect(sourceUpdateNeedsRefresh({ resource: "audit", data: { category: "scrobbling" } })).toBe(false);
    expect(sourceUpdateNeedsRefresh({ resource: "track-match" })).toBe(false);
    expect(sourceUpdateNeedsRefresh({ resource: "playlist-source" })).toBe(false);
  });

  it("keeps routine settings visible and compatibility overrides in Advanced", () => {
    const standard = { key: "CACHE", label: "Cache minutes", type: "number" };
    const advanced = { key: "ENDPOINT", label: "Endpoint", type: "text", advanced: true, defaultValueJson: '"https://service.test/default"' };
    expect(sourceServerFields({ ...provider, configSchema: [standard, advanced] }))
      .toEqual({ standard: [standard], advanced: [advanced] });
    expect(settingDefault(advanced)).toBe("https://service.test/default");
    expect(sourceServerFields(provider)).toEqual({ standard: [], advanced: [] });
  });

  it("uses the same checked capabilities for counts and the badge", () => {
    const timestamp = "2026-10-20T12:00:00Z";
    const catalog = { ...provider, status: "degraded", runtimeCapabilities: [
      { id: "metadata", ready: false, canAttempt: true, health: "unknown", canTest: true },
    ] };
    const check = (capability: string, health: string, reasonCode: string | null = null) => ({
      provider: provider.id, providerAccountId: "account-1", capability, supported: true, enabled: true,
      canTest: true, health, reasonCode, testedAt: timestamp, ready: health === "healthy",
    }) as ProviderHealth;
    const checks = [check("metadata", "healthy"), check("streaming", "healthy"),
      check("download", "degraded", "download-not-verified"),
      check("playlist", "degraded", "probe_not_available")];
    expect(sourceMetrics(catalog, checks)).toEqual({ total: 2, passing: 2, failed: 0, checkedAt: timestamp });
    expect(sourceStatus(catalog, [account()], checks)).toBe("healthy");
    expect(sourceStatus(catalog, [account()], [...checks, check("lyrics", "degraded", "timeout")])).toBe("degraded");
    expect(sourceMetrics(catalog, [...checks, check("lyrics", "degraded", "timeout")]))
      .toMatchObject({ total: 3, passing: 2, failed: 1 });
    const disabled = { ...check("lyrics", "degraded", "sign_in_needed"), enabled: false };
    expect(sourceMetrics(catalog, [...checks, disabled])).toMatchObject({ total: 2, passing: 2, failed: 0 });
    expect(sourceStatus(catalog, [account()], [...checks, disabled])).toBe("healthy");
    expect(sourceProblemLabel(catalog, [account()], [disabled])).toBeNull();
  });

  it("excludes unchecked capabilities and labels neutral outcomes", () => {
    const untested = { ...provider, runtimeCapabilities: [
      { id: "metadata", ready: false, canAttempt: true, canTest: true, health: "unknown" },
      { id: "playlist", ready: false, canAttempt: true, canTest: false, health: "degraded", reasonCode: "probe_not_available" },
    ] };
    expect(sourceMetrics(untested, [])).toEqual({ total: 0, passing: 0, failed: 0, checkedAt: null });
    expect(sourceStatus(untested, [], [])).toBe("available");
    for (const reasonCode of ["probe_not_available", "download-not-verified"])
      expect(checkStatusState({ ready: false, health: "degraded", reasonCode })).toBe("");
    expect(checkStatusLabel({ ready: false, health: "degraded", reasonCode: "probe_not_available" })).toBe("Not checked yet");
    expect(checkStatusLabel({ ready: false, health: "degraded", reasonCode: "download-not-verified" })).toBe("Not verified");
  });

  it("keeps an unavailable diagnostic sample neutral", () => {
    const sample = { id: "streaming", ready: false, canAttempt: true, canTest: true,
      health: "degraded", reasonCode: "sample-unavailable" };
    const source = { ...provider, runtimeCapabilities: [sample] };
    expect(checkStatusState(sample)).toBe("");
    expect(checkStatusLabel(sample)).toBe("Sample unavailable");
    expect(checkGuide(sample.reasonCode)?.step).toBe("Sample unavailable, retrying another recording.");
    expect(sourceStatus(source, [], [])).toBe("available");
    expect(sourceMetrics(source, [])).toEqual({ total: 0, passing: 0, failed: 0, checkedAt: null });
    expect(sourceProblemLabel(source, [], [{ ...sample, provider: provider.id, capability: "streaming", providerAccountId: null, providerAccountName: null, accountScope: "public", supported: true, enabled: true, configuration: "not_required" } as ProviderHealth])).toBeNull();
  });

  it("gives automatic check failures a plain label and next step", () => {
    expect(checkStatusLabel({ ready: false, health: "degraded", reasonCode: "sign_in_needed" })).toBe("Needs verification");
    expect(checkStatusState({ ready: false, health: "degraded", reasonCode: "sign_in_needed" })).toBe("needs_verification");
    expect(checkGuide("sign_in_needed")?.signIn).toBe(true);
    expect(checkStatusLabel({ ready: false, health: "degraded", reasonCode: "track-selection" })).toBe("No test song found");
    expect(checkGuide("resolve")?.step).toContain("checked again every 6 hours");
    expect(checkStatusLabel({ ready: false, health: "degraded", reasonCode: "service_unavailable" })).toBe("Source service unavailable");
    expect(checkGuide("service_unavailable")?.step).toBe("The source service is unavailable. Try again later.");
    expect(checkStatusLabel({ ready: false, health: "degraded", reasonCode: "TransientFailure" })).toBe("Source service unavailable");
    expect(checkGuide("AccountNeedsReauthentication")?.signIn).toBe(true);
    expect(checkGuide("some-new-code")).toBeNull();
    expect(checkGuide(null)).toBeNull();
    expect(sourceStatusLabel("needs_verification")).toBe("Needs verification");
    const signed = { id: "signed", name: "Signed", usesSignedSession: true } as ProviderDefinition;
    const connection = { ...account(), id: "a", providerId: "signed" };
    const failing = (reasonCode: string, capability = "streaming") => ({ provider: "signed", providerAccountId: "a", capability,
      health: "degraded", ready: false, canAttempt: true, canTest: true, reasonCode }) as ProviderHealth;
    expect(sourceStatus(signed, [connection], [failing("resolve"), failing("sign_in_needed", "metadata")])).toBe("needs_verification");
    expect(sourceStatus(signed, [connection], [failing("resolve")])).toBe("degraded");
    expect(sourceProblemLabel(signed, [connection], [failing("resolve")])).toBe("Playback didn't start");
    expect(sourceStatusLabel("needs_config")).toBe("Needs setup");
  });

  it("names local lyric sources without fake provider services", () => {
    expect(lyricsSourceDisplayName([], "backend")).toBe("Media server lyrics");
    expect(lyricsSourceDisplayName([], "kept")).toBe("Kept LRC");
    expect(lyricsSourceDisplayName([{ id: "lrclib", name: "LRCLIB" }], "lrclib")).toBe("LRCLIB");
  });
  it("shows audience and only an explicitly supplied custom label", () => {
    expect(accountSubtitle({ scope: "Personal", customLabel: null })).toBe("Personal");
    expect(accountSubtitle({ scope: "Shared" })).toBe("Shared with everyone");
    expect(accountSubtitle({ scope: "Shared", customLabel: "Road trip" })).toBe("Shared with everyone · Road trip");
  });
  it("labels legacy Apple identities with the unified service without changing their IDs", () => {
    const providers = [{ id: "apple-musickit", name: "Apple Music" }];
    for (const id of ["apple-download", "applemusic", "apple-musickit"])
      expect(providerDisplayName(providers, id)).toBe("Apple Music");
    expect(providerDisplayName(providers, "spotiflac-apple-music")).toBe("Unknown source");
  });

  it("humanizes camel-case lifecycle labels", () => {
    expect(humanize("reviewRequired")).toBe("Review Required");
    expect(humanize("reviewrequired")).toBe("Review Required");
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
    })).toBe("42 ms at last check");
    expect(sourceTimingLabel(provider, { providerId: provider.id, enabledAccountCount: 1, capabilityTotal: 1, healthyCapabilityCount: 1, failedCapabilityCount: 0, p95LatencyMilliseconds: 17 }))
      .toBe("17 ms typical");
    expect(sourceTimingLabel({ ...provider, runtimeCapabilities: [{ id: "metadata", ready: false, canAttempt: true, canTest: true }] }))
      .toBe("Not measured yet");
    expect(sourceTimingLabel({ ...provider, runtimeCapabilities: [{ id: "metadata", ready: true, canAttempt: true, canTest: true, testedAt: "2026-08-07T00:00:00Z" }] }))
      .toBe("Checked");
    expect(sourceTimingLabel({ ...provider, categories: ["streaming"] })).toBe("Not measured yet");
    expect(sourceTimingLabel({ ...provider, categories: ["download"] })).toBe("Not measured yet");
    expect(sourceTimingLabel(provider)).toBe("Not applicable");
  });

  it("labels built-in and extension implementations", () => {
    expect(sourceOriginLabel(provider)).toBe("Built-in");
    expect(sourceOriginLabel({ ...provider, implementationOrigin: "extension" })).toBe("Extension");
    expect(sourceOriginLabel({ ...provider, implementationOrigin: "extension", originName: "SpotiFLAC" })).toBe("SpotiFLAC");
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
    const unsigned = { ...account(), secret: { ...account().secret, configured: false } };
    expect(sourceStatus(provider, [unsigned], [])).toBe("needs_config");
    expect(sourceStatus({ ...provider, usesSignedSession: true }, [unsigned], [])).toBe("configured");
  });

  it("labels audiences without revealing credentials", () => {
    expect(audienceLabel({ ...account("Shared") })).toBe("Shared");
    expect(audienceLabel({ ...account(), ownerDisplayName: "Alex" })).toBe("Personal · Alex");
    expect(audienceLabel({ ...account() })).toBe("Personal · you");
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

  it.each(["multi-scrobbler", "koito"])("requires a token and listening address for %s", (id) => {
    expect(accountSettings({ id, name: id })).toMatchObject([
      { key: "token", type: "password", required: true },
      { key: "baseUrl", type: "url", required: true },
    ]);
  });

  it("uses runtime readiness for operator-managed Sources", () => {
    const managed = {
      id: "apple-download",
      name: "Apple Music – GAMDL",
      connectionKind: "operator_managed",
      runtimeCapabilities: [
        { id: "download", ready: true, canAttempt: true, health: "healthy" },
        { id: "lyrics", ready: true, canAttempt: true, health: "healthy" },
      ],
    } satisfies ProviderDefinition;

    expect(sourceMetrics(managed, [])).toMatchObject({
      total: 2,
      passing: 2,
      failed: 0,
    });
  });

  it("offers CTS for typed streaming and download playback capabilities", () => {
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

    expect(supportsPlaybackDiagnostic([streaming])).toBe(true);
    expect(supportsPlaybackDiagnostic([{ ...streaming, capability: "download" }])).toBe(true);
    expect(supportsPlaybackDiagnostic([{ ...streaming, supported: false }])).toBe(false);
    expect(supportsPlaybackDiagnostic([{ ...streaming, capability: "metadata" }])).toBe(false);
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

    expect(ctsMeasurementLabel(measurement)).toBe("Needs verification");
    expect(ctsMeasurementLabel({ ...measurement, health: "unknown" })).toBe("Not measured yet");
    expect(ctsMeasurementLabel({ ...measurement, failureCode: "service_unavailable" })).toBe("Source service unavailable");
    expect(ctsMeasurementLabel({ ...measurement, failureCode: "http-503" })).toBe("Source service unavailable");
    expect(ctsMeasurementLabel({ ...measurement, failureCode: "http-501" })).toBe("Source service unavailable");
    expect(ctsMeasurementLabel({ ...measurement, health: "healthy", latencyMs: 42.1 }))
      .toBe("42.1 ms");
  });
});

it("explains a missing Apple download session", () => {
  expect(providerReadinessLabel("apple-download-sign-in-required")).toBe("Apple download needs sign-in");
  expect(providerReadinessLabel("apple-download-gateway-update-required")).toBe("Apple download gateway needs an update");
  expect(providerReadinessLabel("endpoint_unreachable")).toBe("Endpoint Unreachable");
});

it("labels unchecked capabilities plainly and merges checks that share a cause", () => {
  expect(checkStatusLabel({ ready: false, health: "unknown", configuration: "not_required" })).toBe("Not checked yet");
  expect(checkStatusLabel({ ready: false, health: "unknown", configuration: "configured" })).toBe("Not checked yet");
  expect(checkStatusLabel({ ready: false, configuration: "needs_configuration" })).toBe("Needs setup");
  expect(checkStatusLabel({ ready: true, configuration: "not_required" })).toBe("Ready");
  const gateway = "apple-download-gateway-update-required";
  const groups = groupSourceChecks([
    { id: "metadata", ready: false, health: "unknown", configuration: "not_required" },
    { id: "playlist", ready: false, health: "unknown", configuration: "not_required" },
    { id: "streaming", ready: false, health: "degraded", reasonCode: gateway },
    { id: "download", ready: false, health: "degraded", reasonCode: gateway },
    { id: "lyrics", ready: false, health: "degraded", reasonCode: gateway },
  ]);
  expect(groups.map((group) => group.label)).toEqual(["Metadata", "Playlists", "Streaming, downloads and lyrics"]);
  expect(checkStatusLabel(groups[2].checks[0])).toBe("Apple download gateway needs an update");
});

it("account cards distinguish the owner from the creator", () => {
  expect(accountOwnershipLabel({ ...account(), ownerUserId: "owner", ownerDisplayName: "Listener", createdByUserId: "admin", creatorDisplayName: "Admin" }))
    .toBe("Personal · belongs to Listener (added by Admin)");
  expect(accountOwnershipLabel({ ...account(), ownerUserId: "owner", ownerDisplayName: "Listener", createdByUserId: "owner", creatorDisplayName: "Listener" }))
    .toBe("Personal · Listener");
  expect(accountOwnershipLabel(account())).toBe("Personal · Unknown user");
});


describe("account audience choices", () => {
  it("offers only audiences declared by the server", () => {
    expect(supportedAudiences({ supportedAudiences: ["Personal"] })).toEqual(["Personal"]);
    expect(supportedAudiences({})).toEqual(["Personal", "Shared"]);
  });
  it("explains household playback and scrobble fallback", () => {
    expect(sharingDescription({ providerId: "lastfm" })).toContain("without their own account");
    expect(sharingDescription({ sharingDescription: "Everyone can import playlists." })).toBe("Everyone can import playlists.");
  });
});


it("labels an automatic lyrics timeout with a lyrics-specific next step", () => {
  const reasonCode = "lyrics-upstream-busy-or-timed-out";
  expect(checkStatusLabel({ ready: false, health: "degraded", reasonCode })).toBe("Lyrics upstream busy or timed out");
  expect(checkGuide(reasonCode)?.step).toBe("The lyrics upstream is busy or timed out. Try another lyrics source.");
});
