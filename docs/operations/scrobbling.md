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

## Connect a destination

Open **Integrations → Accounts → Connect Source** and select Last.fm, ListenBrainz, multi-scrobbler or Koito. Use a Personal account for your own listening history. A Shared account is eligible for other household listeners too. Tokens and listening addresses are saved in the encrypted account secret; transport logging is disabled for these connections.

| Target | Account configuration |
| --- | --- |
| Last.fm | Application API key and shared secret, username, and password for the initial sign-in. The resulting session is encrypted; the password is not retained. |
| ListenBrainz | Your ListenBrainz user token. Leave the optional address blank for the public service. |
| multi-scrobbler | The token configured on its ListenBrainz Endpoint Source, plus its API base URL, for example `http://multi-scrobbler:9078/1/`. |
| Koito | A Koito API key, plus the instance URL ending in `/apis/listenbrainz/1/`, for example `https://koito.example/apis/listenbrainz/1/`. |

Allstarr appends `submit-listens` or `validate-token` to the supplied base URL. Include the API prefix. A multi-scrobbler source using a URL slug can instead use the base ending in `/api/listenbrainz/your-slug/`. Follow the upstream [multi-scrobbler endpoint setup](https://docs.multi-scrobbler.app/configuration/sources/listenbrainz-endpoint/) or [Koito setup](https://koito.io/guides/scrobbler/). Run those services separately; Allstarr does not install a sidecar or script for them.

HTTPS is required for public addresses. HTTP permits RFC 1918 IPv4 addresses, loopback, unique-local IPv6, and intranet names: a single-label service name or a name under `.localhost`, `.local`, `.lan`, `.internal`, or `.home.arpa`. Every resolved address must be private; Allstarr connects directly to a checked address. Public DNS answers, unspecified addresses, link-local metadata addresses and redirects are rejected. These transports do not use a proxy. Use HTTPS or a private IP if your DNS name does not fit the intranet forms.

Connection tests validate the configured endpoint without creating a listen. Some compatible servers validate endpoint availability more loosely than the public service; a successful connection test does not prove downstream relay delivery. When a relay forwards to another account, choose one path to that final account. Enabling both direct delivery and a relay to the same account can duplicate listens outside Allstarr's per-target dedupe.

## Repair in Integrations

**Integrations → Accounts → Scrobble delivery** lists your waiting and failed listens even after playback stops. The same panel appears on each scrobbling service's detail view. It shows the original song, occurrence time, and saved destination account. Use **Refresh delivery** for the latest status and **Load earlier deliveries** for older outstanding work.

For reauthentication, choose **Reconnect saved account**, save its credentials, and choose **Retry saved listen**. A different account is never substituted. For uncertain sends, inspect that destination's history first: **Already in history** confirms delivery without another request; **Retry saved listen** explicitly permits a resend if it is missing. Both choices require confirmation. A revision conflict keeps the dialog open and asks you to refresh rather than applying an outdated repair.

Older queued listens from before destination snapshots were introduced cannot prove their original account or outcome. They are held with `destination-unbound` status. The repair panel offers only your eligible Personal or Shared accounts for that service. Choose the intended account, check its history, then explicitly confirm or retry. This initial repair is the only time a receipt without a saved account can acquire one; subsequent retries keep it fixed. Creating or editing an account alone never releases these older listens.

For an older unbound receipt, API retries must include `destinationAccountId` as well as `accountRevision`. The status response supplies `availableDestinationAccountIds` and a Personal-first suggestion limited to the signed-in listener's eligible accounts. A status read never binds the receipt. `attentionOnly=true` limits status reads to outstanding work, and `targetId` filters a single service before paging.
