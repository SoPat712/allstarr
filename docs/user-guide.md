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

### Library

- **Playlists** connects provider playlists and controls how each is exposed to the selected backend.
- **Mappings** reviews unresolved or ambiguous provider tracks and preserves accepted decisions for later syncs and playback.
- **Cached** shows disposable provider audio that may be evicted by cache policy.
- **Kept** shows explicitly retained audio and related sidecars. Kept media is not deleted by cache cleanup.

### Intelligence

- **Overview** summarizes the selected library's listening profile and generated output.
- **History** searches, filters, corrects, exports, or removes retained listening events.
- **Import** previews listening-history exports before adding anything.
- **Discover** explains recommendations and lets a permitted user create a playlist from a completed run.
- **Automation** controls automatic history, retention, recommendation signals, schedules, listening-app keys, and the built-in AudioMuse connection.

AudioMuse is not an extension. Connect a self-hosted AudioMuse server directly in **Intelligence → Automation**. Integrations still reports its health because it participates in the shared capability system.

### Integrations

- **Services** lists every built-in or extension-backed capability and its readiness.
- **Accounts** stores encrypted personal or shared credentials and audience policy.
- **Extensions** installs, updates, reviews permissions, disables, rolls back, and removes provider packages.
- **Routing** orders the eligible fallback services for metadata, streaming, download, lyrics, playlists, scrobbling, and other typed capabilities.

A Service is an implementation. An Account is a credential and access policy for that Service. An Extension is an optional package that can add Services. Routing decides which ready Service/account pair is tried for a capability. These are related but not interchangeable settings.

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

1. Open **Library → Playlists** and add a playlist from a connected playlist-capable account.
2. Choose the visible source view and destination behavior described by the form.
3. Preview the effect before enabling scheduled changes.
4. Open **Mappings** for ambiguous or unresolved tracks.
5. Accept only a candidate that represents the same recording. Use interactive search when automatic candidates are wrong.

An accepted match is reusable across playlist sync, search, playback, and later rematches. A matched local item is returned as the complete native backend object. A genuinely external item keeps a stable virtual identity and provider label.

Virtual playlists do not silently mutate the source service. Backend materialization adds only resolved local items unless the workflow explicitly says it will download or write back.

## Cached versus kept

- A **cached** track is a disposable playback/download artifact. It can be evicted by age or size policy and fetched again.
- A **kept** track was explicitly retained and is managed separately from cache cleanup.
- A database backup does not contain either audio folder. Back up kept/downloaded media according to your own storage policy.

If playback succeeded but Cached is empty, check the selected storage mode, provider route, durable job, and Activity outcome. A remote stream may not create a complete cache file until the provider download finishes and publishes atomically.

## Listening and scrobbling

Automatic history is opt-in. Enable it under **Intelligence → Automation** for the selected library. Listening apps can receive a private key there and may optionally forward completed listens to connected Last.fm or ListenBrainz accounts.

Scrobbling is checkpointed so a provider is not sent the same completed listen twice. Home and History show what Allstarr observed; Activity shows delivery failures and retries.

## Safety and recovery

- Keep the dashboard on a trusted network or behind an authenticated proxy.
- Use a separate provider account when a service allows it, and grant the smallest audience required.
- Preview imports, legacy configuration, playlist changes, and destructive maintenance actions.
- Back up PostgreSQL, the Allstarr key ring, configuration, and retained media as separate assets.
- Use `allstarr.sh upgrade` before an update that should have a rollback artifact.

For exact procedures, see [configuration](operations/configuration.md), [storage and recovery](operations/storage.md), and [client compatibility](operations/client-compatibility.md).
