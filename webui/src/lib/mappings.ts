import type { MatchCandidate, MatchReviewItem, MatchTarget, ProviderDefinition } from "./api";

export function isAttention(state: string) {
  return ["unresolved", "suggested", "ambiguous", "rejected"].includes(state.toLowerCase());
}

export function percent(value?: number | null) {
  return value == null || !Number.isFinite(value) ? "—" : `${Math.round(value * 1_000) / 10}%`;
}

export function playableProviders(providers: ProviderDefinition[]) {
  return providers
    .filter((provider) => {
      const capabilities = [
        ...(provider.categories ?? []),
        ...(provider.capabilityRoutes ?? []).flatMap((route) => route.capabilities),
      ];
      if (!capabilities.some((value) => value === "streaming" || value === "download")) return false;
      const runtime = provider.runtimeCapabilities?.filter(
        (value) => value.id === "streaming" || value.id === "download",
      );
      return provider.status !== "disabled" && (!runtime?.length || runtime.some((value) => value.canAttempt));
    })
    .toSorted((left, right) => left.name.localeCompare(right.name));
}

export function scoreComponents(candidate: MatchCandidate) {
  return Object.entries(candidate.components ?? {})
    .filter((entry): entry is [string, number] => Number.isFinite(entry[1]))
    .toSorted((left, right) => right[1] - left[1]);
}

export function providerResultCounts(targets: MatchTarget[]) {
  return [...targets.reduce((counts, target) => {
    if (target.externalProvider)
      counts.set(target.externalProvider, (counts.get(target.externalProvider) ?? 0) + 1);
    return counts;
  }, new Map<string, number>())]
    .map(([providerId, count]) => ({ providerId, count }))
    .toSorted((left, right) => right.count - left.count || left.providerId.localeCompare(right.providerId));
}

export function differenceHash(pixels: ArrayLike<number>) {
  let hash = 0n;
  for (let row = 0; row < 8; row += 1) {
    for (let column = 0; column < 8; column += 1) {
      const left = (row * 9 + column) * 4;
      const right = left + 4;
      const brightness = (offset: number) =>
        pixels[offset] * 0.299 + pixels[offset + 1] * 0.587 + pixels[offset + 2] * 0.114;
      if (brightness(left) > brightness(right)) hash |= 1n << BigInt(row * 8 + column);
    }
  }
  return hash;
}

export function hashSimilarity(left: bigint, right: bigint) {
  let difference = left ^ right;
  let count = 0;
  while (difference) {
    count += Number(difference & 1n);
    difference >>= 1n;
  }
  return 1 - count / 64;
}

export function currentTarget(match: MatchReviewItem) {
  if (match.localTrack)
    return {
      title: match.localTrack.title,
      detail: match.localTrack.artist || match.localTrack.backendItemId,
      providerId: "local",
      artworkUrl: match.localTrack.artworkUrl,
    };
  const route = match.providerIdentities.find(
    (identity) => identity.providerId.toLowerCase() !== match.providerId.toLowerCase(),
  );
  return route
    ? {
        title: route.externalId,
        detail: route.verification,
        providerId: route.providerId,
        artworkUrl: null,
      }
    : null;
}
