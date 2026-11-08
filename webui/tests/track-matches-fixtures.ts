type MatchRow = { externalSnapshotId: string; state: string; title?: string | null; artist?: string | null; playlistNames?: string[] };

export function trackMatchStateFilter(state: string | null, item: MatchRow) {
  if (!state) return true;
  if (state === "needs" || state === "attention")
    return ["ambiguous", "suggested", "unresolved"].includes(item.state);
  if (state === "unresolved") return item.state === state;
  if (state === "automatic") return item.state === "accepted";
  if (state === "manual_matches" || state === "manual_rejections") return false;
  return item.state === state;
}

export function trackMatchSearch(term: string | null, item: MatchRow) {
  if (!term) return true;
  const lower = term.toLocaleLowerCase();
  const fields = [item.title, item.artist, ...(item.playlistNames ?? [])].filter(Boolean) as string[];
  return fields.some(value => value.toLocaleLowerCase().includes(lower));
}
