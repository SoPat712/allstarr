import type { MatchCandidate, MatchReviewItem, ProviderDefinition } from "./api";

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
