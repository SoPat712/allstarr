import type { ExtensionPackage, ExtensionSession, ExtensionStoreItem } from "$lib/api";

export type SessionSummary = {
  label: string;
  badge: "healthy" | "degraded" | "suggested";
  detail: string;
};

export function sessionSummary(session: ExtensionSession, locale?: string): SessionSummary {
  switch (session.state) {
    case "signed_in":
      return {
        label: "Signed in",
        badge: "healthy",
        detail: session.expiresAt
          ? `Session valid until ${formatSessionTime(session.expiresAt, locale)}.`
          : "The extension can use its service.",
      };
    case "verification_pending":
      return {
        label: "Verification needed",
        badge: "suggested",
        detail: "Open the verification page, finish there, then paste the link or code it shows you.",
      };
    case "expired":
      return {
        label: "Sign-in expired",
        badge: "degraded",
        detail: "Reconnect to keep using this extension's service.",
      };
    default:
      return {
        label: "Not signed in",
        badge: "suggested",
        detail: "This extension needs a one-time verification before it can use its service.",
      };
  }
}

const sessionReasons: Record<string, string> = {
  grant_required: "Paste the link or code shown after verification.",
  grant_invalid: "That isn't a sign-in link. Copy the full link or code shown after verification.",
  grant_wrong_extension: "That sign-in link belongs to a different extension. Start verification here and use the new link.",
  grant_rejected: "The service rejected this code. It may have expired or already been used. Start verification again.",
  session_rejected: "The service refused to start a sign-in for this extension version. Check for an extension update.",
  provider_unavailable: "The sign-in service is unavailable right now. Try again in a few minutes.",
  origin_not_approved: "The sign-in service isn't in this extension's approved network access. Use Review access to allow it, then try again.",
  unexpected_response: "The sign-in service returned an unexpected response. Try again, or check for an extension update.",
};

export function sessionReasonText(reasonCode?: string | null) {
  if (!reasonCode) return "";
  return sessionReasons[reasonCode] ?? "Sign-in didn't complete. Try again.";
}

export function verificationHost(url?: string | null) {
  if (!url) return "";
  try {
    const parsed = new URL(url);
    return parsed.protocol === "https:" ? parsed.host : "";
  } catch {
    return "";
  }
}

function formatSessionTime(value: string, locale?: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? "at an unknown time"
    : date.toLocaleString(locale, { dateStyle: "medium", timeStyle: "short" });
}

export function compareVersions(left: string, right: string) {
  const parts = (value: string) => value.replace(/^v/i, "").split(/[.+-]/)
    .map((part) => /^\d+$/.test(part) ? Number(part) : part.toLowerCase());
  const a = parts(left);
  const b = parts(right);
  for (let index = 0; index < Math.max(a.length, b.length); index++) {
    const av = a[index] ?? 0;
    const bv = b[index] ?? 0;
    if (av === bv) continue;
    if (typeof av === "number" && typeof bv === "number") return av - bv;
    if (typeof av === "number") return 1;
    if (typeof bv === "number") return -1;
    return av.localeCompare(bv);
  }
  return 0;
}

export function currentPackages(items: ExtensionPackage[]) {
  const current = new Map<string, ExtensionPackage>();
  for (const item of items) {
    if (["uninstalled", "rolledback"].includes(item.state.toLowerCase())) continue;
    const key = item.extensionId.toLowerCase();
    const existing = current.get(key);
    if (!existing || new Date(item.stagedAt ?? 0) >= new Date(existing.stagedAt ?? 0))
      current.set(key, item);
  }
  return [...current.values()];
}

export function availablePackages(store: ExtensionStoreItem[], installed: ExtensionPackage[]) {
  const versions = new Map(installed.map((item) => [item.extensionId.toLowerCase(), item.version]));
  return store.filter((item) => !versions.has(item.id.toLowerCase()) ||
    compareVersions(item.version, versions.get(item.id.toLowerCase())!) > 0);
}

export function valueChanges(current: string[], previous: string[]) {
  const before = new Set(previous);
  const after = new Set(current);
  return [
    ...current.map((value) => ({ value, change: before.has(value) ? "unchanged" : "added" })),
    ...previous.filter((value) => !after.has(value))
      .map((value) => ({ value, change: "removed" })),
  ] as const;
}
