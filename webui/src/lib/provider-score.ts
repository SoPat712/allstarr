import type { ProviderScore, ProviderUseMode } from "./api";

const feature = (score: ProviderScore | undefined, capability: string) =>
  score?.capabilities.find((item) => item.capability === capability);

/** Each order is judged only on the feature it orders, so a slow song start never lowers a metadata score. */
export const capabilityScore = (score: ProviderScore | undefined, capability: string) =>
  feature(score, capability)?.score ?? null;

/** How that score was earned, so the parts always add up to the number on the chip. */
export const scoreParts = (score: ProviderScore | undefined, capability: string) =>
  feature(score, capability)?.parts ?? null;

const qualityLabels: Record<string, string> = {
  DataSaver: "Data saver", High: "High lossy", CdLossless: "CD lossless", HiResLossless: "Hi-Res lossless", BestAvailable: "Best available",
};

/** The quality setting a score was judged against, as the listener knows it. */
export const qualityTargetLabel = (target?: string | null) => qualityLabels[target ?? ""] ?? "quality";

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
