import type { ScrobbleDelivery } from "./api";

export const canResend = (item: ScrobbleDelivery) => item.kind !== "nowplaying" &&
  (item.state === "permanentfailure" || item.state === "retrying") && item.canResend === true && Boolean(item.submission);

export const visibleDeliveries = (items: ScrobbleDelivery[]) => items.filter(item => item.kind !== "nowplaying" &&
  (item.state === "permanentfailure" || item.state === "retrying"));

export function deliveryCounts(items: ScrobbleDelivery[]) {
  const visible = visibleDeliveries(items);
  const waiting = visible.filter(canResend).length;
  return { waiting, unsendable: visible.length - waiting };
}

export function groupDeliveries(items: ScrobbleDelivery[]) {
  const groups = new Map<string, { key: string; items: ScrobbleDelivery[] }>();
  for (const item of items) {
    const key = JSON.stringify([item.targetId, item.state, item.providerCode, item.safeMessage, item.requiresReauthentication,
      item.destinationAccountId, item.suggestedDestinationAccountId, [...(item.availableDestinationAccountIds ?? [])].sort(), Boolean(item.attemptStartedAt),
      item.canResend, item.canConfirm, item.canDismiss, item.unavailableReason]);
    const group = groups.get(key) ?? { key, items: [] };
    group.items.push(item);
    groups.set(key, group);
  }
  return [...groups.values()];
}

// A short name for why a listen is waiting, so an account can say what kinds of problems it has.
export function deliveryReasonLabel(item: ScrobbleDelivery) {
  if (item.requiresReauthentication) return "need the account signed in again";
  return ({
    "metadata-unavailable": "waiting for song details",
    "song-unidentified": "couldn't be identified",
    "delivery-not-attempted": "interrupted",
    "destination-unbound": "need their destination confirmed",
    "destination-changed": "need the account signed in again",
    ambiguous: "sent, not yet confirmed",
    "payload-unavailable": "have nothing saved to send",
  } as Record<string, string>)[item.providerCode ?? ""] ?? (item.state === "retrying" ? "waiting to retry" : "couldn't be sent");
}

/** Waiting listens counted by reason, largest first: "20 waiting for song details · 10 couldn't be identified". */
export function deliveryBreakdown(items: ScrobbleDelivery[]) {
  const counts = new Map<string, number>();
  for (const item of visibleDeliveries(items)) counts.set(deliveryReasonLabel(item), (counts.get(deliveryReasonLabel(item)) ?? 0) + 1);
  return [...counts.entries()].sort((left, right) => right[1] - left[1]).map(([label, count]) => `${count} ${label}`).join(" · ");
}
