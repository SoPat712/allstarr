import { describe, expect, it } from "vitest";
import { collectionCountsLine, collectionStateLabel, migrationError, migrationProgress, migrationStep, needsReview, reviewReason, songCount, addedCountLine, remainingCountLine, moveCountLine, readyToMove, collectionChoiceOrder, collectionCount, unavailableCollectionsLine, resourceDetails } from "./migration";
import type { MigrationCollectionStatus, MigrationReviewItem, MigrationStatus } from "./api";

const counts = { selected: 12, local: 3, streamed: 5, accepted: 8, review: 2, unmatched: 1, unsupported: 1, skipped: 0 };
const collection = { id: "one", sourceId: "one", collection: { name: "Evening", kind: "playlist", expected: 24, read: 12, state: "matching", keepInSync: false, error: null }, counts, unread: 12 } satisfies MigrationCollectionStatus;
const status = { id: "run", run: { providerAccountId: "account", state: "collecting", phase: "collecting", jobId: null }, collections: [collection], total: counts, provisional: true } satisfies MigrationStatus;

describe("music import copy", () => {
  it("uses plain state names and keeps interrupted work on its saved step", () => {
    expect(["collecting", "verifying", "matching", "review", "waiting", "cancelled"].map(collectionStateLabel))
      .toEqual(["Reading from Spotify", "Checking music", "Checking music", "Ready to review", "Waiting", "Stopped"]);
    expect(migrationStep(status)).toBe(1);
    expect(migrationStep({ ...status, run: { ...status.run, state: "review" } })).toBe(1);
    expect(migrationStep({ ...status, run: { ...status.run, state: "waiting", phase: "applying" } })).toBe(3);
    expect(migrationStep({ ...status, run: { ...status.run, state: "completed" } })).toBe(3);
    expect(migrationStep(null)).toBe(0);
    expect(collectionStateLabel("unknown")).toBe("Waiting");
  });
  it("reconciles all outcomes without calling them moved", () => {
    expect(counts.accepted + counts.review + counts.unmatched + counts.unsupported + counts.skipped).toBe(counts.selected);
    expect(counts.local + counts.streamed).toBe(counts.accepted);
    expect(collectionCountsLine(counts)).toBe("8 ready · 2 need a look · 1 not found · 1 can't be moved");
    expect(collectionCountsLine({ ...counts, review: 1 })).toContain("1 needs a look");
  });
  it("uses singular and plural song and heart counts", () => {
    expect([0, 1, 2].map(songCount)).toEqual(["0 songs", "1 song", "2 songs"]);
    expect([0, 1, 2].map(n => addedCountLine(n, "playlist"))).toEqual(["0 songs added", "1 song added", "2 songs added"]);
    expect([0, 1, 2].map(n => addedCountLine(n, "liked-songs"))).toEqual(["0 hearts added", "1 heart added", "2 hearts added"]);
    expect([0, 1, 2].map(n => remainingCountLine(n))).toEqual(["0 songs still need a look", "1 song still needs a look", "2 songs still need a look"]);
    expect(moveCountLine(1)).toBe("Move 1 song now");
    expect(moveCountLine(2)).toBe("Move 2 songs now");
    expect(moveCountLine(1, true)).toBe("Move 1 more song");
    expect(moveCountLine(2, true)).toBe("Move 2 more songs");
    expect(remainingCountLine(1, true)).toBe("1 item still needs a look");
    expect(remainingCountLine(2, true)).toBe("2 items still need a look");
    expect(moveCountLine(1, false, true)).toBe("Move 1 item now");
    expect(moveCountLine(2, true, true)).toBe("Move 2 more items");
  });
  it("offers another move only for accepted songs without a completed membership receipt", () => {
    const row = { metadata: { title: "Song" }, preview: { state: 2, status: 0, resolvedRoute: { kind: 1 } }, receipt: null } as MigrationReviewItem;
    expect(readyToMove(row)).toBe(true);
    for (const outcome of [1, 2, 3]) expect(readyToMove({ ...row, receipt: { outcome } })).toBe(false);
    expect(readyToMove({ ...row, receipt: { outcome: 4, outcomeCode: "unresolved" } })).toBe(true);
    for (const outcomeCode of ["duplicate", "streamed", "existing-or-later-intent"])
      expect(readyToMove({ ...row, receipt: { outcome: 4, outcomeCode } })).toBe(false);
    expect(readyToMove({ ...row, preview: { state: 0, status: 2, resolvedRoute: null } })).toBe(false);
    expect(readyToMove({ ...row, preview: { state: 3, status: 5, resolvedRoute: null } })).toBe(false);
    expect(readyToMove({ ...row, metadata: { title: null } })).toBe(false);
  });
  it("orders and counts saved collections and explains only unavailable kinds", () => {
    expect(["playlist", "followed-artists", "liked-songs", "saved-albums"].sort((a, b) => collectionChoiceOrder(a) - collectionChoiceOrder(b)))
      .toEqual(["liked-songs", "saved-albums", "followed-artists", "playlist"]);
    expect(collectionCount(1, "saved-albums")).toBe("1 album");
    expect(collectionCount(2, "followed-artists")).toBe("2 artists");
    expect(collectionCount(0, "playlist")).toBe("0 songs");
    expect(addedCountLine(1, "saved-albums")).toBe("1 album added");
    expect(addedCountLine(2, "saved-albums")).toBe("2 albums added");
    expect(addedCountLine(1, "followed-artists")).toBe("1 artist followed");
    expect(addedCountLine(2, "followed-artists")).toBe("2 artists followed");
    expect(unavailableCollectionsLine([])).toBe("");
    expect(unavailableCollectionsLine([{ kind: "saved-albums" }])).toBe("Saved albums can't be moved yet.");
    expect(unavailableCollectionsLine([{ kind: "followed-artists" }])).toBe("Followed artists can't be moved yet.");
    expect(unavailableCollectionsLine([{ kind: "saved-albums" }, { kind: "followed-artists" }])).toBe("Saved albums and followed artists can't be moved yet.");
    expect(resourceDetails({ year: 2020, trackCount: 1, albums: ["Evening"] })).toBe("2020 · 1 song · Evening");
    expect(resourceDetails({ year: null, trackCount: null, albums: [] })).toBe("");
  });
  it("reviews collection resources and moves only accepted unapplied resources", () => {
    const row: MigrationReviewItem = { collectionId: "albums", sourceEntryId: "album-1", position: 0, externalSnapshotId: "", preview: null, receipt: null, metadata: { title: "Evening" }, resource: { kind: "saved-albums", state: "review", candidates: [], chosen: null, applied: false } };
    expect(needsReview(row)).toBe(true);
    expect(reviewReason(row)).toBe("Choose the right album.");
    expect(readyToMove(row)).toBe(false);
    expect(needsReview({ ...row, resource: { ...row.resource!, state: "unmatched" } })).toBe(true);
    expect(needsReview({ ...row, resource: { ...row.resource!, state: "skipped" } })).toBe(false);
    expect(needsReview({ ...row, resource: { ...row.resource!, state: "accepted" } })).toBe(false);
    expect(readyToMove({ ...row, resource: { ...row.resource!, state: "accepted" } })).toBe(true);
    expect(readyToMove({ ...row, resource: { ...row.resource!, state: "accepted", applied: true } })).toBe(false);
    expect(reviewReason({ ...row, resource: { ...row.resource!, kind: "followed-artists", state: "unmatched" } })).toBe("No artist found yet.");
  });
  it("uses known enumeration coverage and stays indeterminate for unknown sizes", () => {
    expect(migrationProgress(status)).toBe(50);
    expect(migrationProgress({ ...status, collections: [{ ...collection, collection: { ...collection.collection, expected: null } }] })).toBeNull();
    expect(migrationProgress({ ...status, collections: [] })).toBeNull();
    expect(migrationProgress({ ...status, collections: [{ ...collection, collection: { ...collection.collection, expected: 0 } }] })).toBe(100);
  });
  it("never shows internal errors or unknown codes", () => {
    expect(migrationError("migration_waiting")).toContain("connection");
    expect(migrationError("Spotify changed during collection. Start another run; completed work is retained.")).toContain("Start another move");
    expect(migrationError("internal_secret_error")).toBe("Your progress is saved. Check your Spotify connection and playlist access, then try again.");
  });
  it("uses the effective route and state for review, including unavailable accepted matches", () => {
    const row = { metadata: { title: "Song" }, preview: { state: 2, status: 8, resolvedRoute: null } } as MigrationReviewItem;
    expect(needsReview(row)).toBe(true);
    expect(reviewReason(row)).toBe("This song is not available in your library right now.");
    expect(needsReview({ ...row, preview: { state: 2, status: 0, resolvedRoute: { kind: 1 } } })).toBe(false);
    expect(needsReview({ ...row, preview: { state: 2, status: 10, resolvedRoute: { kind: 2 } } })).toBe(false);
    expect(needsReview({ ...row, preview: { state: 3, status: 5, resolvedRoute: null } })).toBe(false);
    expect(needsReview({ ...row, metadata: { title: null } })).toBe(false);
    expect(reviewReason({ ...row, preview: null })).toBe("No match found yet.");
  });
});
