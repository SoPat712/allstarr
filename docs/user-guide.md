# User guide

Allstarr has two surfaces:

- Music clients connect to the Jellyfin or Subsonic-compatible port, normally `5274`.
- Administrators and permitted users use the dashboard, normally on port `5275`.

The dashboard controls how Allstarr connects sources, matches music, projects playlists, stores temporary or kept files, and learns from listening. It is not a second music player.

## First setup

1. Start the stack and open the dashboard.
2. Sign in with a user from the selected Jellyfin or Subsonic backend.
3. Complete onboarding: confirm the backend connection, choose a music library, and verify the user mapping.
4. Open **Integrations → Services** to see built-in and installed capabilities.
5. Open **Integrations → Accounts** to connect personal or explicitly shared provider accounts.
6. Open **Integrations → Routing** to choose the fallback order for each capability.
7. Test ordinary local playback in a music client before adding provider playlists or external playback.

Administrators see deployment and shared-account controls. A non-administrator sees only the libraries, accounts, and actions allowed for that backend identity.

## Dashboard map

### Home

Home is the operational summary. It shows active listening sessions, the source serving playback, scrobble progress, storage totals, provider health, durable work, and recent activity. Start here when playback or background work seems wrong.

The source is explicitly marked as confirmed streaming, cached audio, or an unconfirmed catalog entry. See [external song labels and playback sources](operations/client-compatibility.md#external-song-labels-and-playback-sources) for `[A]`/`[E]` titles, automatic fallback, song details, and client limitations.

### Library

- **Playlists** connects provider playlists and controls how each is exposed to the selected backend.
- **Mappings** reviews unresolved or ambiguous provider tracks and preserves accepted decisions for later syncs and playback.
- **Cached** shows disposable provider audio that may be evicted by cache policy.
- **Kept** shows explicitly retained audio and related sidecars. Kept media is not deleted by cache cleanup.

### Intelligence (deferred)

Intelligence is not in the desktop navigation, mobile bar, or More sheet while the first release is being prepared. Existing `#/intelligence` links still open the development workspace. This navigation change does not delete history, disable existing opt-in jobs, or disconnect accounts. The [release plan](release-readiness.md#first-release-scope-decision-2026-09-13) tracks the remaining release-exclusion work; the following describes the retained workspace, not a first-release promise.

- **Overview** shows live playback, listening totals, and an interactive daily or monthly activity map for the selected library. Long and all-time ranges add an activity-year selector, default to the busiest imported year, and separate imported history from direct playback in each bucket.
- **History** searches, filters, corrects, exports, or removes retained listening events.
- **Import** previews listening-history exports before adding anything.
- **Discover** explains each recommendation and accepts direct like or dismissal feedback.
- **Playlists** keeps recommendation sets temporary until a permitted user injects one into Jellyfin or Subsonic. Pending materialization updates live.
- **Automation** controls automatic history, retention, recommendation signals, schedules, listening-app keys, and the built-in AudioMuse-AI connection.

AudioMuse-AI is not an extension. Connect a self-hosted AudioMuse-AI server directly in **Intelligence → Automation**. Integrations still reports its health because it participates in the shared capability system.

Subsonic users may see **Connect background access** the first time they open Intelligence. Protocol sign-in does not retain the backend password. This explicit step encrypts a credential for the exact Allstarr user and Subsonic identity, starts library indexing, and enables background recommendations and playlist creation without granting access to another listener.

### Integrations

- **Services** lists every built-in or extension-backed capability and its readiness.
- **Accounts** stores encrypted personal or shared credentials and audience policy.
- **Extensions** installs, updates, reviews permissions, disables, rolls back, and removes provider packages.
- **Routing** orders the eligible fallback services for metadata, streaming, download, lyrics, playlists, scrobbling, and other typed capabilities.

A Service is an implementation. An Account is a credential and access policy for that Service. An Extension is an optional package that can add Services. Routing decides which ready Service/account pair is tried for a capability. These are related but not interchangeable settings.

Ordinary Jellyfin and Subsonic users can choose **Private** (the default) or explicitly confirm **Global** when connecting their own account. Global allows other listeners to use eligible provider capabilities under the server's policy; it does not reveal the credential or give them control of the connection. Personal playlists, favorites, and scrobbling remain restricted by default, while the person who connected the account keeps access to their own personal capabilities.

To change this later, open the account's **Access → Edit access** dialog. The person who connected it can share it, make it private again, disable it, or remove it. An account privately assigned to you by an administrator needs administrator help to change its audience. Making an account private stops new shared account selections; it cannot retract audio already delivered. Provider limits may be consumed by shared use.

Server policy still applies: **AdminManaged** disables listener self-service; **UserManaged** limits everyone, including administrators, to their own accounts; **Hybrid** allows both owner self-service and administrator management. Connection probes remain administrator-only, so listeners see **Save connection**, not an unavailable “Save and test” action. See [provider account policy](operations/configuration.md#provider-accounts).

### Activity

Activity groups operational events by outcome and shows the actor, target, duration, source, and correlation details. Use it with container logs when a durable job or provider call fails.

### Settings

Settings owns deployment behavior rather than provider credentials: playback quality, matching preferences, cache behavior, maintenance, backup, restore, and other operator policy. Controls that affect one feature stay near that feature when possible.

## Connect a source

1. Open **Integrations → Services** and select the Service.
2. Read its capabilities and current readiness.
3. Open **Configuration** for operator-managed fields, or use **Connect account** for encrypted user/shared credentials.
4. Choose the smallest audience that needs access.
5. Save and test the connection.
6. Confirm its capability appears ready before changing Routing.

Sensitive values are never returned to the browser after saving. Leaving a secret field blank while editing keeps the saved secret unless the form explicitly says otherwise.

## Import listening history

Open **Intelligence → Import** and choose or drop one or more supported files. Allstarr accepts:

- Spotify Extended Streaming History audio JSON files;
- Last.fm, ListenBrainz, Koito, and Maloja JSON, JSONL, or ZIP exports.

Each file is limited to 64 MB and is previewed before import. Video-only Spotify history is rejected. Review completed, skipped, duplicate, and outside-retention counts before applying the preview.

Retention and reporting range are separate:

- **Retention** controls how long saved listening events remain. `Unlimited` keeps them until you remove them.
- **Overview/History range** controls what the dashboard reports and defaults to all time.

Imports stay private inside Allstarr unless a separate listening-app or scrobbling action is enabled. A completed receipt records what happened at import time; it cannot recreate events that were later removed. Re-upload the original export if the receipt says zero listens are currently retained.

## Add and review a provider playlist

1. Open **Library → Playlists** and import a playlist from your connected playlist-capable account. If no account is available, **Connect Spotify** opens a personal account flow.
2. Choose the visible source view and destination behavior described by the form.
3. Choose **Import once**, **Update when I ask**, or a schedule. An imported-once playlist keeps its published snapshot and never reads later source changes, even if the source account is subsequently disabled.
4. Choose **Stream when played** or **Keep every song**. Keep-all queues owner- and library-scoped durable downloads for every resolved external song; local songs are already permanent. An unavailable or unresolved song is reported in Activity without rolling back the playlist import.
5. Open **Mappings** for ambiguous or unresolved tracks.

The default **Mapped** view always preserves the source playlist's full order. Local matches play from the media server, external `[A]` tracks use their eligible mapped providers in the configured streaming order, and unresolved songs remain visible with a clear not-playable status. **Original** previews the source metadata, while **Native** is only a diagnostic view of the separate playlist written into Jellyfin or Subsonic; it can omit external and unresolved entries that the backend cannot store natively.
6. Accept only a candidate that represents the same recording. Use interactive search when automatic candidates are wrong.

An accepted match is reusable across playlist sync, search, playback, and later rematches. A matched local item is returned as the complete native backend object. A genuinely external item keeps a stable virtual identity and provider label.

Automatic matching keeps confidence separate from routing preference. A local Jellyfin or Subsonic result wins when it is within 7 confidence points of the strongest candidate. If local is outside that window, the configured playback providers are considered in order with windows of 5, 3, and 1 point; every later provider must have the highest confidence outright. Exact boundaries favor the earlier route. The acceptance threshold still uses the selected candidate's raw confidence.

Use **Library → Mappings → Rematch all** when library metadata or matching behavior has changed. The preview includes resolved, review, and unresolved tracks; active manual pins, rejections, and manually selected provider routes are protected. Allstarr replaces current automatic decisions in durable batches of 25 and yields between batches. Earlier decisions remain available as audit history. A new matching-algorithm version schedules the same gradual replacement automatically for stale decisions.

Mapping tabs separate current decisions by purpose: **Manual** contains authoritative matches, **Rejected** contains authoritative rejections, **Automatic** contains matcher-accepted results, **Tentative** contains viable candidates below the acceptance threshold, **Review** contains ambiguous near-ties, and **Unresolved** contains tracks without a safe candidate. **Rematch** releases one manual authority before running the current algorithm; **Delete** releases it without rematching. Both operations retain durable history, and deleting a provider pin preserves the verified provider identity for possible future automatic selection.

A high confidence score can still appear under **Review** when two distinct, same-priority recordings are within the ambiguity guard; Allstarr will not guess between them. A 100% row under **Manual** that says **Accepted** is already accepted—the tab identifies who made the durable decision, not whether it passed the threshold.

Virtual playlists do not silently mutate the source service. Backend materialization adds only resolved local items unless the workflow explicitly says it will download or write back.

Import behavior is fixed when a playlist is created so a later settings edit cannot accidentally turn a frozen copy into a live source link. Song retention can be enabled later; Allstarr immediately queues the already-imported downloadable songs and applies the same policy to later linked updates. Kept files remain permanent until they are explicitly released or removed and must be backed up separately from PostgreSQL.

## Cached versus kept

- A **cached** track is a disposable playback/download artifact. It can be evicted by age or size policy and fetched again.
- A **kept** track was explicitly retained and is managed separately from cache cleanup.
- A database backup does not contain either audio folder. Back up kept/downloaded media according to your own storage policy.

If playback succeeded but Cached is empty, check the selected storage mode, provider route, durable job, and Activity outcome. A remote stream may not create a complete cache file until the provider download finishes and publishes atomically.

## Listening and scrobbling

Automatic history is opt-in. Enable it under **Intelligence → Automation** for the selected library. Completed protocol plays then appear in **Overview** without a manual refresh. Listening apps can receive a private key there and may optionally forward completed listens to connected Last.fm or ListenBrainz accounts.

Recommendation learning stays within the exact user, backend, and library scope. Plays, skips, favorites, playlist membership, and direct Discover feedback can influence later runs only when their signal types are enabled. A favorite raises the track's preference signal; removing the favorite cancels that contribution. Generated sets remain ephemeral in Intelligence until **Create in Jellyfin** or **Create in Subsonic** is used, while Automation can refresh and publish rotating personal sets on a schedule.

Scrobbling is checkpointed so a provider is not sent the same completed listen twice. Home and History show what Allstarr observed; Activity shows delivery failures and retries.

## Safety and recovery

- Keep the dashboard on a trusted network or behind an authenticated proxy.
- Use a separate provider account when a service allows it, and grant the smallest audience required.
- Preview imports, legacy configuration, playlist changes, and destructive maintenance actions.
- Back up PostgreSQL, the Allstarr key ring, configuration, and retained media as separate assets.
- Use `allstarr.sh upgrade` before an update that should have a rollback artifact.

For exact procedures, see [configuration](operations/configuration.md), [storage and recovery](operations/storage.md), and [client compatibility](operations/client-compatibility.md).
