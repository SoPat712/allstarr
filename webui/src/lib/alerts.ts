import type { ProblemAlertThresholds } from "./api";

export function alertDeliveryLabel(delivery?: { status: string }) {
  if (!delivery) return "No deliveries yet";
  return ({ queued: "Waiting to send", sending: "Sending", delivered: "Delivered", retrying: "Retrying",
    failed: "Could not deliver", cancelled: "Cancelled" } as Record<string, string>)[delivery.status] ?? "Delivery pending";
}

export function alertDate(value?: string | null) {
  const date = value ? new Date(value) : null;
  return date && Number.isFinite(date.getTime())
    ? new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(date) : "Time unavailable";
}

export function destinationHost(value: string) {
  try {
    const url = new URL(value.trim());
    return ["https:", "http:"].includes(url.protocol) && !url.username && !url.password && !url.hash && value.length <= 2048 ? url.host : "";
  } catch { return ""; }
}

export function parsePermittedHosts(value: string): string[] | null {
  const entries = value.split(/\r?\n/).map(host => host.trim()).filter(Boolean);
  if (entries.length > 32) return null;
  const result: string[] = [];
  for (const host of entries) {
    if (host.length > 255 || /[/?#@\s]/.test(host)) return null;
    try { if (!new URL(`https://${host}`).hostname) return null; } catch { return null; }
    if (!result.some(existing => existing.toLowerCase() === host.toLowerCase())) result.push(host);
  }
  return result;
}

export type AlertThresholdDraft = Record<"warningGiB" | "warningPercent" | "criticalGiB" | "criticalPercent" | "hysteresisGiB" | "hysteresisPercent" | "cooldownMinutes", number | undefined>;
const gib = 1024 ** 3;
export function thresholdDraft(value: ProblemAlertThresholds): AlertThresholdDraft {
  return { warningGiB: value.warningBytes / gib, warningPercent: value.warningPercent,
    criticalGiB: value.criticalBytes / gib, criticalPercent: value.criticalPercent,
    hysteresisGiB: value.hysteresisBytes / gib, hysteresisPercent: value.hysteresisPercent, cooldownMinutes: value.cooldownMinutes };
}

export function thresholdsFromDraft(draft: AlertThresholdDraft): ProblemAlertThresholds | null {
  if (Object.values(draft).some(value => typeof value !== "number" || !Number.isFinite(value))) return null;
  const value = { warningBytes: Math.round(Number(draft.warningGiB) * gib), warningPercent: Number(draft.warningPercent),
    criticalBytes: Math.round(Number(draft.criticalGiB) * gib), criticalPercent: Number(draft.criticalPercent),
    hysteresisBytes: Math.round(Number(draft.hysteresisGiB) * gib), hysteresisPercent: Number(draft.hysteresisPercent), cooldownMinutes: Number(draft.cooldownMinutes) };
  return value.criticalBytes >= 0 && value.warningBytes >= value.criticalBytes && value.warningBytes <= 2 ** 50 &&
    value.criticalPercent >= 0 && value.warningPercent >= value.criticalPercent && value.warningPercent <= 99 &&
    value.hysteresisBytes >= 0 && value.hysteresisBytes <= 2 ** 50 && value.hysteresisPercent >= 0 && value.hysteresisPercent <= 50 &&
    Number.isInteger(value.cooldownMinutes) && value.cooldownMinutes >= 1 && value.cooldownMinutes <= 1440 ? value : null;
}
