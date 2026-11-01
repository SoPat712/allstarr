import type { ProviderScore, ProviderUseMode } from "./api";

/** Lyrics are judged on lyrics alone. Every other order decides which source plays, so it uses the overall score. */
export function capabilityScore(score: ProviderScore | undefined, capability: string) {
  if (!score) return null;
  return capability === "lyrics"
    ? score.capabilities.find((item) => item.capability === capability)?.score ?? score.score
    : score.score;
}

/** Only sources Allstarr can pick by itself compete on score. Manual and turned-off sources are never ranked. */
export const isRanked = (useMode?: ProviderUseMode | null) => useMode !== "manualOnly" && useMode !== "off";

export const scoreTier = (value: number | null) =>
  value == null ? "none" : value >= 90 ? "great" : value >= 70 ? "good" : value >= 50 ? "fair" : "poor";

/** Highest score first, then unscored sources, then unranked ones. Equal sources keep their current order. */
export function sortByScore(order: string[], value: (id: string) => number | null, ranked: (id: string) => boolean) {
  const rank = (id: string) => !ranked(id) ? -2 : value(id) ?? -1;
  return order.map((id, index) => ({ id, index }))
    .sort((left, right) => rank(right.id) - rank(left.id) || left.index - right.index)
    .map((item) => item.id);
}
