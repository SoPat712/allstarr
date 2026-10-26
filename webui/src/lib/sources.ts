import type {
  CtsMeasurement,
  ProviderAccount,
  ProviderDefinition,
  ProviderHealth,
  ProviderSetting,
  ProviderSummary,
} from "./api";

import { humanize } from "./activity";
import type { UpdateEvent } from "./live-updates.svelte";
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

export interface CheckGuide {
  label: string;
  step: string;
  signIn?: boolean;
}

// Plain labels and one next step for the reasons automatic checks report.
const checkGuides: Record<string, CheckGuide> = {
  probe_not_available: { label: "Not checked yet", step: "This capability has no automatic check." },
  "download-not-verified": { label: "Not verified", step: "Downloads have not been verified. Run a playback check to verify the download path." },
  "sample-unavailable": { label: "Sample unavailable", step: "Sample unavailable, retrying another recording." },
  "authentication-required": { label: "Source requires authentication", step: "The source endpoint requires authentication. Check its sign-in." },
  "access-denied": { label: "Source denied access", step: "The source endpoint denied access. Try again later." },
  "playlist-not-found": { label: "Test playlist unavailable", step: "The source returned no test playlist. Try again later." },
  "playlist-empty": { label: "Test playlist is empty", step: "The source returned a playlist without songs. Try again later." },
  "playlist-invalid": { label: "Invalid playlist response", step: "The source returned unusable playlist data. Try again later." },
  "metadata-not-found": { label: "Test song unavailable", step: "The source returned no test song. Try again later." },
  "metadata-invalid": { label: "Invalid song response", step: "The source returned unusable song data. Try again later." },
  "probe-limit-exceeded": { label: "Check limit reached", step: "The source exceeded the check's request or time limit. Try again later." },
  sign_in_needed: { label: "Needs verification", step: "Sign in again in this source's Sign-in section.", signIn: true },
  verification_required: { label: "Needs verification", step: "Sign in again in this source's Sign-in section.", signIn: true },
  "track-selection": {
    label: "No test song found",
    step: "The service returned no search results. This is usually on the service's side; it is checked again every 6 hours.",
  },
  resolve: {
    label: "Playback didn't start",
    step: "The service found the song but sent no audio in time. This is usually on the service's side; it is checked again every 6 hours.",
  },
  "blocked-by-challenge": {
    label: "Blocked by a Cloudflare check",
    step: "This source is behind a Cloudflare check. Add a FlareSolverr or Byparr URL in Settings to use it.",
  },
  service_unavailable: { label: "Source service unavailable", step: "The source service is unavailable. Try again later." },
  "lyrics-upstream-busy-or-timed-out": { label: "Lyrics upstream busy or timed out", step: "The lyrics upstream is busy or timed out. Try another lyrics source." },
  timeout: { label: "Timed out", step: "The service took too long to answer. It is checked again every 6 hours." },
  unreachable: { label: "Can't reach the service", step: "Check the server's internet connection, then use Test." },
  administrator_required: { label: "Needs an administrator", step: "Add an administrator in Settings so checks can run." },
};
// Playback checks can also report the provider's error kind.
checkGuides.TransientFailure = checkGuides["transient-failure"] = checkGuides.service_unavailable;
for (let status = 500; status < 600; status++) checkGuides[`http-${status}`] = checkGuides.service_unavailable;
checkGuides.AccountNeedsReauthentication = checkGuides.Unauthorized = checkGuides.sign_in_needed;
checkGuides.NotFound = { label: "Test song unavailable", step: "The service couldn't play the song it picked. It is checked again every 6 hours." };
checkGuides.RateLimited = { label: "Rate limited", step: "The service is limiting requests. Wait a few minutes, then use Test." };
checkGuides["rate-limited"] = checkGuides.RateLimited;
checkGuides["account-needs-reauthentication"] = checkGuides["verification-required"] = checkGuides.sign_in_needed;

export const checkGuide = (reasonCode?: string | null): CheckGuide | null =>
  reasonCode ? checkGuides[reasonCode] ?? null : null;

/** The plain reason a source is failing, for its status badge instead of a generic "Degraded". */
export function sourceProblemLabel(provider: ProviderDefinition, accounts: ProviderAccount[], health: ProviderHealth[]) {
  const ids = new Set(accounts.filter((item) =>
    item.providerId.toLowerCase() === provider.id.toLowerCase() && item.enabled).map((item) => item.id));
  const failing = health.filter((item) => isChecked(item) && item.health === "degraded" &&
    (item.providerAccountId != null && ids.has(item.providerAccountId) || item.provider?.toLowerCase() === provider.id.toLowerCase()));
  for (const item of failing) {
    const guide = checkGuide(item.reasonCode);
    if (guide) return guide.label;
  }
  return null;
}

export function sourceStatusLabel(state: string) {
  if (state === "needs_config") return "Needs setup";
  if (state === "needs_verification") return "Needs verification";
  if (state === "disabled") return "Turned off";
  return humanize(state);
}

export function checkStatusLabel(check: Omit<SourceCheck, "id">) {
  if (check.reasonCode === "probe_not_available") return "Not checked yet";
  if (check.reasonCode === "download-not-verified") return "Not verified";
  if (check.reasonCode === "sample-unavailable") return "Sample unavailable";
  if (check.ready) return "Ready";
  const guide = checkGuide(check.reasonCode);
  if (guide) return guide.label;
  if (check.reasonCode) return providerReadinessLabel(check.reasonCode);
  if (check.configuration === "needs_configuration") return "Needs setup";
  if (check.configuration === "unsupported") return "Not supported";
  if (check.health === "testing") return "Checking…";
  if (check.health === "degraded") return "Not working";
  if (check.health === "unavailable") return "Unavailable";
  return "Not checked yet";
}

export function checkStatusState(check: Omit<SourceCheck, "id">) {
  if (isNeutralCheck(check)) return "";
  if (check.ready) return "healthy";
  if (checkGuide(check.reasonCode)?.signIn) return "needs_verification";
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

export const lyricsSourceDisplayName = (providers: ProviderDefinition[], id: string) =>
  id === "backend" ? "Media server lyrics" : id === "kept" ? "Kept LRC" : providerDisplayName(providers, id, humanize(id));

export function accountSubtitle(account: { scope: string; customLabel?: string | null }) {
  const audience = account.scope === "Shared" ? "Shared with everyone" : "Personal";
  return account.customLabel ? `${audience} · ${account.customLabel}` : audience;
}

export function audienceLabel(account: ProviderAccount) {
  if (account.scope === "Shared") return "Shared";
  return account.ownerDisplayName ? `Personal · ${account.ownerDisplayName}` : "Personal · you";
}

export function accountOwnershipLabel(account: ProviderAccount) {
  const owner = account.ownerDisplayName || "Unknown user";
  const differentCreator = account.scope === "Personal" && account.creatorDisplayName &&
    (account.createdByUserId && account.ownerUserId ? account.createdByUserId !== account.ownerUserId : account.creatorDisplayName !== owner);
  const label = account.scope === "Shared" ? "Shared with everyone" : differentCreator
    ? `Personal · belongs to ${owner} (added by ${account.creatorDisplayName})` : `Personal · ${owner}`;
  return account.customLabel ? `${label} · ${account.customLabel}` : label;
}

export function sourceStatus(
  provider: ProviderDefinition,
  accounts: ProviderAccount[],
  health: ProviderHealth[],
) {
  if (provider.status === "disabled") return "disabled";
  const connected = accounts.filter((item) =>
    item.providerId.toLowerCase() === provider.id.toLowerCase() && item.enabled);
  const checks = sourceChecks(provider, health).filter(isChecked);
  const failing = checks.filter((item) => item.health === "degraded" || item.health === "unavailable");
  if (failing.some((item) => checkGuide(item.reasonCode)?.signIn)) return "needs_verification";
  if (failing.length) return "degraded";
  if (checks.some((item) => item.health === "healthy")) return "healthy";
  if (connected.length) {
    // Signed extensions keep their sign-in outside the credential store; the sign-in panel shows its state.
    return provider.usesSignedSession || connected.every((item) => item.secret.configured && !item.secret.revoked)
      ? "configured"
      : "needs_config";
  }
  if (sourceNeedsAccount(provider)) return "needs_config";
  return ["healthy", "degraded"].includes(provider.status ?? "") ? "available" : provider.status || "available";
}

export function sourceIsInUse(provider: ProviderDefinition, accounts: ProviderAccount[], health: ProviderHealth[]) {
  return provider.implementationOrigin === "extension" ||
    accounts.some(account => account.providerId.toLowerCase() === provider.id.toLowerCase()) ||
    provider.status !== "needs_config" && (["partial_config", "degraded"].includes(provider.status ?? "") ||
      sourceStatus(provider, accounts, health) !== "needs_config");
}

export function sourceUpdateNeedsRefresh(update: UpdateEvent) {
  if (!update.resource) return true;
  if (["track-match", "playlist-source"].includes(update.resource)) return false;
  if (update.resource === "audit" && update.data?.category)
    return /^(provider-|extension|runtime-settings|configuration|onboarding)/.test(update.data.category);
  if (update.resource === "job" && update.data?.type)
    return /^(provider[.-]|extension[.-]|configuration[.-])/.test(update.data.type);
  return true;
}

const isNeutralCheck = (check: { reasonCode?: string | null }) =>
  check.reasonCode === "probe_not_available" || check.reasonCode === "download-not-verified" ||
  check.reasonCode === "sample-unavailable";

const isChecked = (check: { health?: string | null; reasonCode?: string | null; canTest?: boolean; supported?: boolean; enabled?: boolean }) =>
  check.enabled !== false && check.supported !== false && check.canTest !== false && !isNeutralCheck(check) &&
  ["healthy", "degraded", "unavailable"].includes(check.health ?? "");

function sourceChecks(provider: ProviderDefinition, health: ProviderHealth[]) {
  const checks = health.filter((item) =>
    item.provider.toLowerCase() === provider.id.toLowerCase());
  return checks.length ? checks.filter((item) => item.enabled !== false) : provider.runtimeCapabilities ?? [];
}

export function sourceMetrics(
  provider: ProviderDefinition,
  health: ProviderHealth[],
) {
  const checks = sourceChecks(provider, health).filter(isChecked);
  return {
    total: checks.length,
    passing: checks.filter((item) => item.health === "healthy").length,
    failed: checks.filter((item) => item.health === "degraded" || item.health === "unavailable").length,
    checkedAt:
      checks.map((item) => item.testedAt).filter(Boolean).toSorted().at(-1) ??
      null,
  };
}

export const supportsPlaybackDiagnostic = (health: ProviderHealth[]) =>
  health.some((item) => ["streaming", "download"].includes(item.capability.toLowerCase()) && item.supported);

export const ctsMeasurementLabel = (measurement: CtsMeasurement) =>
  measurement.health === "healthy" ? `${measurement.latencyMs} ms` :
    measurement.health === "degraded" ? checkGuide(measurement.failureCode)?.label ?? "Failed" : "Not measured yet";

export const sourceOriginLabel = (provider: ProviderDefinition) =>
  provider.originName || (provider.implementationOrigin === "extension" ? "Extension" : "Built-in");

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

function listeningServiceSettings(required: boolean, tokenLabel = "Listening service token"): ProviderSetting[] {
  return [
    { key: "token", label: tokenLabel, type: "password", sensitive: true, required: true },
    { key: "baseUrl", label: required ? "Listening address" : "Listening address (optional)", type: "url", required,
      helpText: required ? "Paste the complete ListenBrainz-compatible base URL, including /1/. HTTPS is required outside your private network."
        : "Leave blank for ListenBrainz. A compatible server can use HTTPS, or HTTP on your private network." },
  ];
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
  listenbrainz: listeningServiceSettings(false, "ListenBrainz token"),
  "multi-scrobbler": listeningServiceSettings(true),
  koito: listeningServiceSettings(true),
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

export function sourceServerFields(provider: ProviderDefinition) {
  const fields = provider.configSchema ?? [];
  return {
    standard: fields.filter(field => !field.advanced),
    advanced: fields.filter(field => field.advanced),
  };
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


export function supportedAudiences(source: { supportedAudiences?: ("Personal" | "Shared")[] }) {
  return source.supportedAudiences ?? ["Personal", "Shared"];
}

export function sharingDescription(source: { providerId?: string; id?: string; sharingDescription?: string | null }) {
  if (source.sharingDescription) return source.sharingDescription;
  if (["lastfm", "listenbrainz", "multi-scrobbler", "koito"].includes(source.providerId ?? source.id ?? ""))
    return "Plays from listeners without their own account are sent here.";
  return "Everyone in the household can use this account. Personal accounts take priority.";
}
