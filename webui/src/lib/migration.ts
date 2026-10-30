import type { MigrationCounts, MigrationReviewItem, MigrationStatus } from "./api";

export function collectionStateLabel(state: string) {
  return ({ available: "Waiting", collecting: "Reading from Spotify", verifying: "Checking songs", matching: "Checking songs",
    review: "Ready to review", applying: "Adding songs", applied: "Moved", skipped: "Skipped", waiting: "Waiting", cancelled: "Stopped" } as Record<string, string>)[state] ?? "Waiting";
}

export function migrationStep(status: MigrationStatus | null) {
  const state = status?.run.state;
  const phase = state === "waiting" || state === "cancelled" ? status?.run.phase : state;
  if (phase === "completed" || phase === "applying") return 3;
  if (status?.provisional && phase !== "choosing") return 1;
  if (phase === "review") return 2;
  return phase && phase !== "choosing" ? 1 : 0;
}

export function collectionCountsLine(counts: MigrationCounts) {
  return `${counts.accepted} ready · ${counts.review} ${counts.review === 1 ? "needs" : "need"} a look · ${counts.unmatched} not found · ${counts.unsupported} can't be moved`;
}

export function songCount(count: number) {
  return `${count} ${count === 1 ? "song" : "songs"}`;
}

export function addedCountLine(count: number, kind: string) {
  return `${kind === "liked-songs" ? `${count} ${count === 1 ? "heart" : "hearts"}` : songCount(count)} added`;
}

export function remainingCountLine(count: number) {
  return `${songCount(count)} still ${count === 1 ? "needs" : "need"} a look`;
}

export function moveCountLine(count: number, more = false) {
  return more ? `Move ${count} more ${count === 1 ? "song" : "songs"}` : `Move ${songCount(count)} now`;
}

export function readyToMove(item: MigrationReviewItem) {
  if (!item.metadata.title || item.preview?.state === 3 || needsReview(item)) return false;
  return !item.receipt || item.receipt.outcome >= 4 && !["duplicate", "streamed", "existing-or-later-intent"].includes(item.receipt.outcomeCode ?? "");
}

export function migrationProgress(status: MigrationStatus) {
  const selected = status.collections.filter(item => item.collection.state !== "skipped");
  if (!selected.length || selected.some(item => item.collection.expected === null)) return null;
  const expected = Math.max(status.total.selected, selected.reduce((sum, item) => sum + item.collection.expected!, 0));
  const total = status.total;
  return expected ? Math.min(100, (total.accepted + total.review + total.unmatched + total.unsupported + total.skipped) / expected * 100) : 100;
}

export function migrationError(error?: string | null) {
  if (error?.startsWith("Spotify changed during collection."))
    return "Your music changed in Spotify. Start another move to continue. Everything already moved stays.";
  return "Your progress is saved. Check your Spotify connection and playlist access, then try again.";
}

export function needsReview(item: MigrationReviewItem) {
  if (!item.metadata.title || item.preview?.state === 3) return false;
  const preview = item.preview;
  return !preview || !([0, 1].includes(preview.status) && preview.resolvedRoute?.kind === 1 || preview.status === 10 && preview.resolvedRoute?.kind === 2);
}

export function reviewReason(item: MigrationReviewItem) {
  if ([7, 8].includes(item.preview?.status ?? -1)) return "This song is not available in your library right now.";
  if (item.preview?.status === 9) return "This match needs another check.";
  if (item.preview?.status === 6) return "This match needs your approval.";
  return [1, 2, 4, 5].includes(item.preview?.state ?? 0) ? "Choose the right version of this song." : "No match found yet.";
}
