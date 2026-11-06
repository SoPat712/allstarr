type RankedLyricsSource = { sourceId: string; available: boolean; rank?: number | null };

export function rankedLyricsSources<T extends RankedLyricsSource>(sources: readonly T[]): T[] {
  return [...sources].sort((left, right) => {
    const leftRank = left.rank ?? Infinity;
    const rightRank = right.rank ?? Infinity;
    return leftRank === rightRank ? 0 : leftRank - rightRank;
  });
}

export function defaultLyricsSourceId(sources: readonly RankedLyricsSource[], savedSourceId?: string | null): string {
  return sources.find(source => source.sourceId === savedSourceId && source.available)?.sourceId
    ?? rankedLyricsSources(sources).find(source => source.available)?.sourceId ?? "";
}
