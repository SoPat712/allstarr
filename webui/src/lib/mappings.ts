import type { MatchCandidate, MatchReviewItem, MatchTarget, ProviderDefinition } from "./api";

export function isAttention(state: string) {
  return ["unresolved", "suggested", "ambiguous", "rejected"].includes(state.toLowerCase());
}

export function reviewStateLabel(state: string) {
  return state.toLowerCase() === "suggested"
    ? "Tentative"
    : state.replaceAll("_", " ");
}

export function percent(value?: number | null) {
  return value == null || !Number.isFinite(value) ? "—" : `${Math.round(value * 1_000) / 10}%`;
}

export function scoreComponents(candidate: MatchCandidate) {
  return Object.entries(candidate.components ?? {})
    .filter((entry): entry is [string, number] => Number.isFinite(entry[1]))
    .toSorted((left, right) => right[1] - left[1]);
}

export function providerResultCounts(targets: MatchTarget[]) {
  return [...targets.reduce((counts, target) => {
    const providerId = target.externalProvider || "local";
    counts.set(providerId, (counts.get(providerId) ?? 0) + 1);
    return counts;
  }, new Map<string, number>([["local", 0]]))]
    .map(([providerId, count]) => ({ providerId, count }))
    .toSorted((left, right) =>
      Number(right.providerId === "local") - Number(left.providerId === "local") ||
      right.count - left.count ||
      left.providerId.localeCompare(right.providerId));
}

export function playableProviderIds(providers: ProviderDefinition[]) {
  return new Set(providers
    .filter((provider) => [
      ...(provider.categories ?? []),
      ...(provider.runtimeCapabilities ?? [])
        .filter((capability) => capability.supported !== false)
        .map((capability) => capability.id),
      ...(provider.capabilityRoutes ?? []).flatMap((route) => route.capabilities),
    ].some((capability) =>
      ["stream", "streaming", "download", "downloads"].includes(capability.toLowerCase())))
    .map((provider) => provider.id.toLowerCase()));
}

export function rankedTargets(targets: MatchTarget[]) {
  return targets.toSorted((left, right) =>
    (right.components?.preferenceScore ?? right.confidence ?? -1) -
      (left.components?.preferenceScore ?? left.confidence ?? -1) ||
    (right.confidence ?? -1) - (left.confidence ?? -1) ||
    left.title.localeCompare(right.title));
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
      artist: match.localTrack.artist,
      album: match.localTrack.album,
      durationMilliseconds: match.localTrack.durationMilliseconds,
      identity: match.localTrack.id,
      providerId: "local",
      artworkUrl: match.localTrack.artworkUrl,
    };
  const route = match.providerIdentities.find(
    (identity) => identity.providerId.toLowerCase() !== match.providerId.toLowerCase(),
  );
  const candidate = route && match.candidates.find(
    (item) => item.providerTrackIds?.[route.providerId] === route.externalId,
  );
  return route
    ? {
        title: candidate?.title || "Metadata unavailable",
        artist: candidate?.artist || "Unknown artist",
        album: candidate?.album,
        durationMilliseconds: candidate?.durationMilliseconds,
        identity: route.externalId,
        providerId: route.providerId,
        artworkUrl: null,
      }
    : null;
}

export function candidateResolution(
  candidate: MatchCandidate | undefined,
  sourceProviderId: string,
  playableProviders: ReadonlySet<string>,
) {
  const provider = Object.entries(candidate?.providerTrackIds ?? {}).find(
    ([providerId]) =>
      providerId.toLowerCase() !== sourceProviderId.toLowerCase() &&
      playableProviders.has(providerId.toLowerCase()),
  );
  if (candidate?.isLocal === true && candidate.libraryTrackId)
    return { targetType: "local" as const, libraryTrackId: candidate.libraryTrackId };
  if (provider)
    return { targetType: "provider" as const, externalProvider: provider[0], externalId: provider[1] };
  return candidate?.isLocal !== false && candidate?.libraryTrackId
    ? { targetType: "local" as const, libraryTrackId: candidate.libraryTrackId }
    : null;
}
