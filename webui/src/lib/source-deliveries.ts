import type { ScrobbleDelivery } from "./api";

export function groupDeliveries(items: ScrobbleDelivery[]) {
  const groups = new Map<string, { key: string; items: ScrobbleDelivery[] }>();
  for (const item of items) {
    const key = JSON.stringify([item.targetId, item.state, item.providerCode, item.safeMessage, item.requiresReauthentication,
      item.destinationAccountId, item.suggestedDestinationAccountId, [...(item.availableDestinationAccountIds ?? [])].sort(), Boolean(item.attemptStartedAt)]);
    const group = groups.get(key) ?? { key, items: [] };
    group.items.push(item);
    groups.set(key, group);
  }
  return [...groups.values()];
}
