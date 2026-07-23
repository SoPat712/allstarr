# Allstarr v3 Performance Audit

## Goal

Keep playlist ingestion, matching, provider routing, and target reconciliation responsive as libraries and playlists grow. Performance changes must preserve provider order, matching outcomes, and durable idempotency.

## Priority Findings

### P0: Remove per-track persistence queries

`PlaylistOrchestrationService` currently performs existence and latest-version queries for individual source tracks. At the collector limit this can produce hundreds of thousands of database round trips.

Required change:

- Load existing snapshots for distinct source hashes in bounded chunks.
- Build latest-version and content-hash dictionaries in memory.
- Insert missing snapshots as one tracked batch.
- Keep SQL command count constant or chunk-bounded as track count grows.

### P0: Bound local-library candidate work

Matching currently has paths that compare every source entry with every scoped library track and paths that load the entire scoped library for each playlist run.

Required change:

- Query exact ISRC candidates first.
- Build normalized title and artist match keys for unresolved source tracks.
- Query projected candidate columns only.
- Score each distinct source identity once.
- Retain only the best candidates needed by the review UI.
- Add a scoped normalized match-key index when the persisted key shape is finalized.

### P0: Claim idempotency before remote writes

Concurrent playlist runs can pass the same preflight check and both mutate the media server.

Required change:

- Persist an in-progress sync run under the existing idempotency key before any remote write, or acquire a database advisory lock for the link and key.
- Return the existing operation when a duplicate request arrives.
- Ensure failed claims are recoverable by the durable retry policy.

### P1: Deduplicate provider searches

Repeated source IDs can execute the same complete provider walk multiple times.

Required change:

- Group source entries by stable provider identity.
- Match one representative per distinct identity.
- Project the accepted route back to every source position.
- Reuse ISRC and normalized-query results within one run.

### P1: Make provider timeouts real

Metadata fan-out currently stops awaiting timed-out work but can leave provider operations running after the concurrency slot is released.

Required change:

- Create a linked cancellation token per provider operation.
- Apply the configured timeout with `CancelAfter`.
- Keep non-cancellable extension work inside a separate bounded executor until it actually completes.
- Never report an available concurrency slot while its underlying request is still active.

### P1: Select latest match versions in SQL

Virtualization and orchestration materialize all historical decision versions and group them in memory.

Required change:

- Select the latest decision per external snapshot in the database.
- Use no-tracking projections for read-only matching and virtualization paths.
- Return one decision row per source entry.

### P1: Reduce Jellyfin reconciliation complexity

Current order reconciliation contains repeated linear searches and can issue one sequential move request per item.

Required change:

- Use sets and position maps for membership and lookup.
- Prefer bulk delete/add operations when exact reordering is not required.
- When preserving order with moves, calculate a minimal move plan using a longest-increasing-subsequence strategy.

### P2: Batch cache validation

Cache validation can perform two Redis reads per track and repeated linear scans.

Required change:

- Skip mapping probes when another condition already requires a rebuild.
- Fetch cache keys in batches.
- Use the existing ID set for constant-time membership checks.

### P2: Stop ISRC fan-out after a priority winner

ISRC lookup currently invokes every provider and waits for the slowest result.

Required change:

- Follow configured provider priority.
- Stop after the first verified playable result.
- Optionally speculate over a small top-priority window, cancelling lower-priority work when the winner is known.

## Acceptance Measurements

1. Database scaling test:
   Measure SQL command count, returned rows, elapsed time, and allocations for 100, 1,000, and 10,000 source tracks. Command count must be constant or chunk-bounded.

2. Provider and cache call test:
   Run 500 entries containing repeated source IDs against instrumented fake providers, Redis, and Jellyfin. Calls must scale with unique identities, and observed provider concurrency must never exceed its configured limit.

3. Algorithm benchmark:
   Benchmark local matching and target reconciliation at 100, 1,000, and 5,000 tracks, including a reversed target playlist. CPU time and allocations must avoid quadratic growth while producing equivalent output.

4. Timeout drain test:
   Force provider timeouts and assert active operation count returns to zero before the concurrency slot is reused.

5. Idempotency race test:
   Start concurrent runs with the same idempotency key and assert exactly one target mutation sequence and one durable run owner.
