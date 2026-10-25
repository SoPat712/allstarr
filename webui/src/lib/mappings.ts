import type { MatchCandidate, MatchReviewItem, MatchTarget, ProviderDefinition } from "./api";
import { canonicalProviderId } from "./sources";

export function isAttention(state: string) {
  return ["unresolved", "suggested", "ambiguous", "rejected"].includes(state.toLowerCase());
}

export function reviewStateLabel(state: string) {
  const labels: Record<string, string> = {
    suggested: "Unsure",
    ambiguous: "Needs review",
    review: "Needs review",
    unresolved: "No match",
    accepted: "Matched",
    pinned: "Matched by hand",
    rejected: "Rejected",
  };
  const normalized = state.toLowerCase();
  return labels[normalized] ?? (normalized.charAt(0).toUpperCase() + normalized.slice(1)).replaceAll("_", " ");
}

export function percent(value?: number | null) {
  return value == null || !Number.isFinite(value) ? "—" : `${Math.round(value * 1_000) / 10}%`;
}

export function matchScore(value?: number | null) {
  return value == null || !Number.isFinite(value) ? "—" : `${Math.round(value * 10_000) / 100}%`;
}

export function scoreComponents(candidate: MatchCandidate) {
  return Object.entries(candidate.components ?? {})
    .filter((entry): entry is [string, number] =>
      !["priorityWindow", "routingPriority", "acceptanceQualified"].includes(entry[0]) && Number.isFinite(entry[1]))
    .toSorted((left, right) => right[1] - left[1]);
}

export function routingPreference(components?: Record<string, number> | null) {
  if (components?.acceptanceQualified === 1) {
    const priority = components.routingPriority;
    return priority === 0 ? "Local first" : priority > 0 && priority < 2147483647
      ? `Streaming priority ${priority}` : "Meets acceptance checks";
  }
  return components?.priorityWindow ? `${percent(components.priorityWindow)} tentative window` : "";
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
    .map((provider) => canonicalProviderId(provider.id)));
}

export function rankedTargets(targets: MatchTarget[]) {
  const confidence = (value?: number | null) => value != null && Number.isFinite(value) ? value : -1;
  const compare = (left: string, right: string) => {
    const a = left.trim().toLowerCase(), b = right.trim().toLowerCase();
    return a < b ? -1 : a > b ? 1 : 0;
  };
  return targets.toSorted((left, right) =>
    confidence(right.confidence) - confidence(left.confidence) ||
    targetPriority(left) - targetPriority(right) ||
    Number(right.matchesSourceAlbum === true) - Number(left.matchesSourceAlbum === true) ||
    compare(left.externalProvider ?? "", right.externalProvider ?? "") ||
    compare(left.externalId || left.backendItemId || left.id, right.externalId || right.backendItemId || right.id) ||
    compare(left.title, right.title));
}

function targetPriority(target: MatchTarget) {
  const priority = target.routingPriority ?? target.components?.routingPriority;
  return priority != null && Number.isInteger(priority) && priority >= 0 && priority < 2147483647
    ? priority : 2147483647;
}

export function targetRoutingPreference(target: MatchTarget) {
  const priority = targetPriority(target);
  return priority === 0 ? "Local first" : priority < 2147483647 ? `Streaming priority ${priority}` : "";
}

export function reportedAudio(media?: MatchTarget["media"]) {
  const fact = (value?: number | null) => value != null && Number.isFinite(value) && value > 0;
  const token = (value?: string | null) => {
    const normalized = value?.trim().toLowerCase();
    return normalized && normalized !== "unknown" && /^[a-z0-9][a-z0-9._-]{0,31}$/.test(normalized)
      ? normalized : "";
  };
  const codec = token(media?.codec), container = token(media?.container);
  const parts = [codec.toUpperCase()];
  if (container && container !== codec) parts.push(container.toUpperCase());
  if (fact(media?.bitDepth) && !["mp3", "aac", "opus", "vorbis"].includes(codec)) parts.push(`${media!.bitDepth}-bit`);
  if (fact(media?.sampleRate)) parts.push(`${media!.sampleRate! / 1000} kHz`);
  if (fact(media?.bitrate)) parts.push(media!.bitrate! < 1000 ? "<1 kbps" : `${Math.round(media!.bitrate! / 1000)} kbps`);
  if (fact(media?.channels)) parts.push(`${media!.channels} ${media!.channels === 1 ? "channel" : "channels"}`);
  return parts.filter(Boolean).join(" · ") || "Audio format not reported";
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
  if (match.target)
    return {
      title: match.target.title || "Metadata unavailable",
      artist: match.target.artist || "Unknown artist",
      album: match.target.album,
      durationMilliseconds: match.target.durationMilliseconds,
      identity: match.target.externalId,
      providerId: match.target.providerId,
      artworkUrl: match.target.artworkUrl,
    };
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
      canonicalProviderId(providerId) !== canonicalProviderId(sourceProviderId) &&
      playableProviders.has(canonicalProviderId(providerId)),
  );
  if (candidate?.isLocal === true && candidate.libraryTrackId)
    return { targetType: "local" as const, libraryTrackId: candidate.libraryTrackId };
  if (provider)
    return { targetType: "provider" as const, externalProvider: provider[0], externalId: provider[1] };
  return candidate?.isLocal !== false && candidate?.libraryTrackId
    ? { targetType: "local" as const, libraryTrackId: candidate.libraryTrackId }
    : null;
}
