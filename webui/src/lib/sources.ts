import type {
  CtsMeasurement,
  ProviderAccount,
  ProviderDefinition,
  ProviderHealth,
  ProviderSetting,
  ProviderSummary,
} from "./api";

import { humanize } from "./activity";
export { humanize };

export const providerReadinessLabel = (reason: string) =>
  reason === "apple-download-sign-in-required" ? "Apple download needs sign-in" :
    reason === "apple-download-gateway-update-required" ? "Apple download gateway needs an update" : humanize(reason);

export interface SourceCheck {
  id: string;
  ready: boolean;
  health?: string | null;
  reasonCode?: string | null;
  configuration?: string | null;
}

export function checkStatusLabel(check: Omit<SourceCheck, "id">) {
  if (check.ready) return "Ready";
  if (check.reasonCode) return providerReadinessLabel(check.reasonCode);
  if (check.configuration === "needs_configuration") return "Needs setup";
  if (check.configuration === "unsupported") return "Not supported";
  if (check.health === "testing") return "Checking…";
  if (check.health === "degraded") return "Not working";
  if (check.health === "unavailable") return "Unavailable";
  return "Not checked yet";
}

export function checkStatusState(check: Omit<SourceCheck, "id">) {
  if (check.ready) return "healthy";
  if (check.health === "degraded" || check.health === "unavailable") return "degraded";
  if (check.reasonCode || check.configuration === "needs_configuration") return "suggested";
  return "";
}

const checkNames: Record<string, string> = {
  metadata: "Metadata",
  streaming: "Streaming",
  download: "Downloads",
  playlist: "Playlists",
  lyrics: "Lyrics",
  intelligence: "Recommendations",
  health: "Health",
};

export const checkName = (id: string) => checkNames[id.toLowerCase()] ?? humanize(id);

const listLabel = (items: string[]) =>
  items.length < 2 ? items.join("") : `${items.slice(0, -1).join(", ")} and ${items.at(-1)}`;

/** Merges checks that fail for the same reason so one cause is shown once. */
export function groupSourceChecks<T extends SourceCheck>(checks: T[]) {
  const groups: { label: string; checks: T[] }[] = [];
  for (const check of checks) {
    const group = !check.ready && check.reasonCode
      ? groups.find((item) => !item.checks[0].ready && item.checks[0].reasonCode === check.reasonCode)
      : undefined;
    if (group) group.checks.push(check);
    else groups.push({ label: "", checks: [check] });
  }
  for (const group of groups) {
    const names = group.checks.map((item, index) => {
      const name = checkName(item.id);
      return index === 0 ? name : name.toLowerCase();
    });
    group.label = listLabel(names);
  }
  return groups;
}

export const canonicalProviderId = (id: string) => ["apple-download", "applemusic", "apple-music", "apple_music"].includes(id.toLowerCase())
  ? "apple-musickit" : id.toLowerCase();

export const findProviderDefinition = (
  providers: ProviderDefinition[],
  providerId?: string | null,
) => providerId
  ? providers.find((provider) => canonicalProviderId(provider.id) === canonicalProviderId(providerId))
  : undefined;

export const providerDisplayName = (
  providers: ProviderDefinition[],
  providerId?: string | null,
  fallback = "Unknown source",
) => providerId ? findProviderDefinition(providers, providerId)?.name ?? fallback : fallback;

export function accountSubtitle(account: { scope: string; customLabel?: string | null }) {
  const audience = account.scope === "Shared" ? "Shared with everyone" : "Personal";
  return account.customLabel ? `${audience} · ${account.customLabel}` : audience;
}

export function audienceLabel(account: ProviderAccount) {
  if (account.scope === "Shared") return "Shared";
  return account.ownerDisplayName ? `Personal · ${account.ownerDisplayName}` : "Personal · you";
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

export const supportsPlaybackDiagnostic = (health: ProviderHealth[]) =>
  health.some((item) => ["streaming", "download"].includes(item.capability.toLowerCase()) && item.supported);

export const ctsMeasurementLabel = (measurement: CtsMeasurement) =>
  measurement.health === "healthy" ? `${measurement.latencyMs} ms` : "Failed";

export const sourceOriginLabel = (provider: ProviderDefinition) =>
  provider.implementationOrigin === "extension" ? "Extension" : "Built-in";

export function sourceTimingLabel(provider: ProviderDefinition, summary?: ProviderSummary) {
  if (summary?.p95LatencyMilliseconds != null)
    return `${summary.p95LatencyMilliseconds} ms typical`;
  const latest = (provider.runtimeCapabilities ?? [])
    .filter((item) => item.latencyMilliseconds != null)
    .toSorted((left, right) => String(right.testedAt ?? "").localeCompare(String(left.testedAt ?? "")))[0];
  if (latest?.latencyMilliseconds != null) return `${latest.latencyMilliseconds} ms at last check`;
  if ((provider.runtimeCapabilities ?? []).some((item) => item.canTest && item.testedAt))
    return "Checked";
  if ((provider.runtimeCapabilities ?? []).some((item) => item.canTest)) return "Not measured yet";
  if ((provider.categories ?? []).some((item) => ["streaming", "download"].includes(item.toLowerCase())))
    return "Not measured yet";
  return "Not applicable";
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
  listenbrainz: [
    {
      key: "token",
      label: "ListenBrainz or Koito token",
      type: "password",
      sensitive: true,
      required: true,
    },
    {
      key: "baseUrl",
      label: "Where to send listens (optional)",
      type: "url",
      helpText: "Leave blank to send listens to ListenBrainz. To use Koito, paste its HTTPS listening address.",
    },
  ],
};

export function accountSettings(provider: ProviderDefinition) {
  return provider.accountSettings?.length
    ? provider.accountSettings
    : provider.implementationOrigin === "extension" ? [] : builtInSettings[provider.id] ?? [];
}

export function settingDefault(field: ProviderSetting) {
  if (field.sensitive || field.defaultValueJson == null) return field.type === "toggle" ? false : "";
  try {
    const value: unknown = JSON.parse(field.defaultValueJson);
    return field.type === "toggle" ? value === true : value == null ? "" : String(value);
  } catch {
    return field.type === "toggle" ? false : "";
  }
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
