# Scrobble delivery and recovery

Allstarr sends completed listens to Last.fm, ListenBrainz and ListenBrainz-compatible services through its durable playback jobs. Each listen gets one delivery record per listener and destination. Progress and stop reports for the same play share that record. Now-playing updates are separate and never count as a completed listen.

The destination account and its revision are saved when the playback report arrives. Disabling, revoking, replacing or editing that account does not send waiting listens somewhere else. Reconnect the original account, then retry its listens. Tokens and application secrets stay encrypted.

## Connect a destination

In **Sources → Accounts**, choose **Connect account** and pick Last.fm, ListenBrainz, multi-scrobbler or Koito. **Settings → History and scrobbling** links there too. Use a Personal account for your own listening history; a Shared account is available to other listeners in the household. Tokens and addresses are saved encrypted, and request logging is off for these connections.

| Destination | What to enter |
| --- | --- |
| Last.fm | Application API key and shared secret, username, and password for the first sign-in. The resulting session is saved encrypted; the password is not kept. |
| ListenBrainz | Your ListenBrainz user token. Leave the address blank for the public service. |
| multi-scrobbler | The token set on its ListenBrainz Endpoint source, plus its API base URL, for example `http://multi-scrobbler:9078/1/`. |
| Koito | A Koito API key, plus the instance URL ending in `/apis/listenbrainz/1/`, for example `https://koito.example/apis/listenbrainz/1/`. |

Allstarr adds `submit-listens` or `validate-token` to the base URL, so include the API prefix. A multi-scrobbler source with a URL slug can use a base ending in `/api/listenbrainz/your-slug/` instead. Follow the upstream [multi-scrobbler endpoint setup](https://docs.multi-scrobbler.app/configuration/sources/listenbrainz-endpoint/) or [Koito setup](https://koito.io/guides/scrobbler/). Run those services yourself; Allstarr does not install them.

Public addresses need HTTPS. HTTP is allowed for private IPv4 ranges, loopback, unique-local IPv6, and intranet names: a single-label name, or a name under `.localhost`, `.local`, `.lan`, `.internal` or `.home.arpa`. Every address the name resolves to must be private. Public DNS answers, unspecified or link-local addresses, and redirects are refused, and no proxy is used. If your DNS name doesn't fit these forms, use HTTPS or a private IP address.

A connection test checks the endpoint without sending a listen. Some compatible servers check less strictly than the public service, so a passing test doesn't prove that a relay will deliver further. When a relay forwards to another account, use one path to that account: sending directly and through a relay to the same account can duplicate listens.

Songs streamed from services are always scrobbled. **Scrobble songs from your own library** in Settings → History and scrobbling is off by default; leave it off when your media server already scrobbles them (for example with Jellyfin's Last.fm or ListenBrainz plugin), so each play counts once.

## Retries and uncertain sends

Each send attempt is saved before the request goes out. Known temporary failures retry with growing waits, starting at thirty seconds and capped at six hours, and respect a destination's `Retry-After`. A delivered or ignored listen is never sent again. Listens that run out of retries wait for you to repair them.

ListenBrainz's public service ignores repeat submissions with the same user, timestamp and track name, and retries keep the original timestamp and details. Custom compatible servers may not do this. See the [ListenBrainz service description](https://listenbrainz.readthedocs.io/_/downloads/en/stable/pdf/).

Last.fm has no documented duplicate protection for [track.scrobble](https://www.last.fm/api/show/track.scrobble). After a send with an unknown outcome, Allstarr looks for the listen in [user.getRecentTracks](https://www.last.fm/api/show/user.getRecentTracks) for the saved account, artist, title and time. A match confirms delivery without sending again. A complete empty answer waits until two minutes after the attempt, then allows one automatic resend; that allowance survives a restart. If the lookup fails, is incomplete, or still can't tell after the resend, the listen needs review. At startup Allstarr rechecks up to 100 older Last.fm listens that need review.

This recovers through the destination's history; it is not a guaranteed exactly-once send. History delays, corrections, or another app sending the same song at the same moment can still need a manual check. Custom compatible services always use the manual path, because they have no documented history lookup.

## Repair waiting listens

**Scrobble delivery** at the top of **Sources → Accounts** lists your waiting and failed listens, even after playback stops. The same list appears on each scrobbling service and account. It shows the song, when you played it and the saved destination account. Use **Refresh delivery** for the latest status and **Load earlier deliveries** for older ones.

- **Reconnect saved account** appears when the account needs a new sign-in. Save its credentials, then choose **Retry all**. A different account is never used instead.
- For a send with an unknown outcome, check that destination's history first. **Already in history** confirms delivery without sending anything. **Retry** sends it again.
- **Dismiss** and **Dismiss all** clear deliveries that have no saved completed listen to send.

Each choice asks you to confirm. If the delivery changed meanwhile, the dialog asks you to refresh instead of applying an old repair.

Older listens queued before Allstarr saved destination accounts can't prove where they were meant to go. They wait with the status `destination-unbound`. Their group offers **Confirm destination**, listing only your own eligible Personal or Shared accounts for that service. Pick the intended account, check its history, then confirm or retry. This is the only time a listen can gain a destination; later retries keep it. Creating or editing an account alone never releases these listens.

Replacing credentials or renewing a Last.fm session saves the new secret and account revision together; a failed save keeps the previous pair.

## Repair through the API

The dashboard uses these routes on the administration port. Results are limited to the signed-in listener, including for administrators.

- `GET /api/admin/scrobbling/deliveries` returns up to 100 deliveries per page. Pass the last delivery ID as `before` for the next page. `attentionOnly=true` returns only work that needs attention, and `targetId` filters to one service. Each item has the saved destination account ID and revision, delivery revision, status, retry time, send count, and the song's title, artist and play time when known. Credentials and account settings are never included. For an older unbound listen, the response lists `availableDestinationAccountIds` with a Personal-first suggestion; reading never binds it.
- `POST /api/admin/scrobbling/deliveries/{id}/resolve` takes the current `revision` and an `action`:
  - `confirm-delivered` for an uncertain send you have found in the destination history. Nothing is sent.
  - `retry` to allow a new attempt to the same destination account. Include its current `accountRevision`. For an older unbound listen, also include `destinationAccountId`.
  - `dismiss` for a delivery with no saved completed listen.

A stale revision is refused. Another listener's delivery is not found. A delivery that is running can't be repaired at the same time. Each repair is recorded in the audit history.

## History and backups

Clearing listening history, or history retention, keeps the records that prevent duplicate sends and keeps unresolved deliveries. Delivered listens lose their saved song details but keep the minimum needed to avoid sending twice. Back up the database and key ring together, as described in [storage](storage.md#backups).
