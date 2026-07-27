import type {
  ProviderAccount,
  ProviderDefinition,
  ProviderHealth,
  ProviderSetting,
  ProviderSummary,
} from "./api";

export const humanize = (value: string) =>
  value.replaceAll("_", " ").replaceAll("-", " ")
    .replace(/\b\w/g, (character) => character.toUpperCase());

export function audienceLabel(account: ProviderAccount) {
  if (account.scope === "Global") return "Everyone";
  if (account.scope === "Library") return `Library ${account.libraryScopeId || "scope"}`;
  return account.ownerDisplayName ? `Only ${account.ownerDisplayName}` : "Only me";
}

export function sourceStatus(
  provider: ProviderDefinition,
  accounts: ProviderAccount[],
  health: ProviderHealth[],
) {
  if (provider.status === "disabled") return "disabled";
  const connected = accounts.filter((item) =>
    item.providerId.toLowerCase() === provider.id.toLowerCase() && item.enabled);
  if (connected.length) {
    const ids = new Set(connected.map((item) => item.id));
    const checks = health.filter((item) => ids.has(item.providerAccountId) && item.canTest);
    if (checks.some((item) => item.health === "degraded")) return "degraded";
    if (checks.length && checks.every((item) => item.health === "healthy")) return "healthy";
    return connected.every((item) => item.secret.configured && !item.secret.revoked)
      ? "configured"
      : "needs_config";
  }
  if (sourceNeedsAccount(provider)) return "needs_config";
  return provider.status || "available";
}

export function sourceMetrics(
  provider: ProviderDefinition,
  summary: ProviderSummary | undefined,
  health: ProviderHealth[],
) {
  const checks = health.filter((item) =>
    item.provider.toLowerCase() === provider.id.toLowerCase() && item.canTest);
  const runtime = (provider.runtimeCapabilities ?? [])
    .filter((item) => item.supported !== false);
  const hasSummary = (summary?.capabilityTotal ?? 0) > 0;
  return {
    total: hasSummary ? summary!.capabilityTotal : runtime.length || checks.length,
    passing: hasSummary ? summary!.healthyCapabilityCount :
      runtime.length ? runtime.filter((item) => item.ready).length :
      checks.filter((item) => item.health === "healthy").length,
    failed: hasSummary ? summary!.failedCapabilityCount :
      runtime.length ? runtime.filter((item) => item.health === "degraded").length :
      checks.filter((item) => item.health === "degraded").length,
    checkedAt: summary?.lastCheckedAt ??
      runtime.map((item) => item.testedAt).filter(Boolean).toSorted().at(-1) ??
      checks.map((item) => item.testedAt).filter(Boolean).toSorted().at(-1) ??
      null,
  };
}

const builtInSettings: Record<string, ProviderSetting[]> = {
  spotify: [{
    key: "sessionCookie",
    label: "Spotify browser session (sp_dc)",
    type: "password",
    sensitive: true,
    required: true,
    helpText: "Paste the sp_dc value or full Cookie header.",
  }],
  deezer: [{
    key: "arl",
    label: "ARL cookie",
    type: "password",
    sensitive: true,
    required: true,
  }],
  qobuz: [
    { key: "userAuthToken", label: "User auth token", type: "password", sensitive: true, required: true },
    { key: "userId", label: "User ID", type: "text", required: true },
  ],
  lastfm: [
    { key: "apiKey", label: "Application API key", type: "password", sensitive: true, required: true },
    { key: "sharedSecret", label: "Application shared secret", type: "password", sensitive: true, required: true },
    { key: "username", label: "Last.fm username", type: "text", required: true },
    { key: "password", label: "Last.fm password", type: "password", sensitive: true, required: true,
      helpText: "Used once to request a session and never stored." },
  ],
  listenbrainz: [{
    key: "token",
    label: "ListenBrainz user token",
    type: "password",
    sensitive: true,
    required: true,
  }],
};

export function accountSettings(provider: ProviderDefinition) {
  return provider.accountSettings?.length
    ? provider.accountSettings
    : provider.implementationOrigin === "extension" ? [] : builtInSettings[provider.id] ?? [];
}

export const sourceNeedsAccount = (provider: ProviderDefinition) =>
  accountSettings(provider).some((field) => field.required);

export function secretFromForm(provider: ProviderDefinition, data: FormData) {
  return Object.fromEntries(accountSettings(provider)
    .filter((field) => !(provider.id === "lastfm" && field.key === "password"))
    .map((field) => [
      field.key,
      field.type === "toggle" ? data.get(field.key) === "on" : String(data.get(field.key) ?? ""),
    ]));
}
