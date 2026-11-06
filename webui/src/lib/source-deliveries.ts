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
