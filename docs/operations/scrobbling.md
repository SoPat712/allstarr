# Scrobble delivery and recovery

Allstarr records playback through its durable playback jobs. Last.fm and ListenBrainz delivery uses one completed-listen receipt per listener, occurrence and target. Progress and stop reports for the same occurrence share that receipt. Now-playing updates have separate receipts and do not count as completed listens.

The selected destination account and revision are saved when the playback report is accepted. An account being disabled, revoked, replaced or edited does not redirect queued listens. Reconnect the original account and explicitly resume its deliveries at the new revision. Tokens and application secrets stay in encrypted provider-account storage.

## Retries and uncertain sends

A send attempt is committed before any request goes to the destination. Known temporary rejections retry with exponential backoff, beginning at thirty seconds and capped at six hours, while respecting a destination's Retry-After delay. A completed or ignored receipt prevents another send for the same listen. Exhausted retries remain available for repair.

ListenBrainz's public service deduplicates submissions by user, timestamp and track name. Retries preserve the original timestamp and metadata. Custom compatible servers do not inherit that guarantee. See the [ListenBrainz service description](https://listenbrainz.readthedocs.io/_/downloads/en/stable/pdf/).

Last.fm does not document an idempotency key for [track.scrobble](https://www.last.fm/api/show/track.scrobble). If the process stops after a request may have reached Last.fm, Allstarr cannot know whether that request was accepted. It keeps the receipt as needing review and does not resend automatically. Check the destination's history, then confirm delivery or explicitly retry. The same conservative handling applies to an uncertain custom-service send. A network timeout or an invalid response can also require review.

## Inspect and repair

The signed-in listener can read `GET /api/admin/scrobbling/deliveries` on the administration port. Results are limited to that listener, including for administrators. Each page contains up to 100 receipts; pass the last receipt ID as `before` for the next page. Results include the exact saved destination account ID/revision, delivery revision, status, retry time, send count, and original title, artist and occurrence time when available. They never include credentials or raw account configuration.

To resolve a receipt, send `POST /api/admin/scrobbling/deliveries/{id}/resolve` with its current `revision` and an `action`:

- `confirm-delivered`: use only after verifying an uncertain listen in the destination history. This sends nothing.
- `retry`: explicitly permits a new attempt to the same destination account. Include its current `accountRevision`, after reconnecting it if necessary. For uncertain sends, verify the destination history first to avoid creating a duplicate.

A stale revision is rejected. Another listener's receipt is unavailable. A running delivery cannot be repaired concurrently. Repair queues the existing playback job type and records the action in the audit history.

Listening-history retention and clearing history preserve delivery dedupe receipts and unresolved deliveries. Successful receipts lose their saved submission metadata during that history cleanup; their minimal dedupe identity remains. Back up SQLite and the encryption key ring together as described in [storage](storage.md).
