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

Administrators see deployment and shared-account controls. Listeners can open Home, Playlists, Mappings, Services, their Accounts, Activity, and Listening preferences. Settings, Extensions, Routing, Cached, and Kept require administrator access, including when opened through a direct link.

## Dashboard map

### Home

Home shows your active listening sessions, playback source, scrobble progress, playlists, durable work, and recent activity. Administrators also see household activity, storage totals, and provider health. Start here when playback or background work seems wrong.

The source is explicitly marked as confirmed streaming, cached audio, or an unconfirmed catalog entry. See [external song labels and playback sources](operations/client-compatibility.md#external-song-labels-and-playback-sources) for `[A]`/`[E]` titles, automatic fallback, song details, and client limitations.

### Library

- **Playlists** connects provider playlists and controls how each is exposed to the selected backend.
- **Mappings** reviews unresolved or ambiguous provider tracks and preserves accepted decisions for later syncs and playback.
- **Cached** shows disposable provider audio that may be evicted by cache policy.
- **Kept** shows explicitly retained audio and related sidecars. Kept media is not deleted by cache cleanup.

### Intelligence (deferred)

Intelligence is not in the desktop navigation, mobile bar, or More sheet while the first release is being prepared. Administrators can open `#/intelligence` only in the explicit development composition. This navigation change does not delete history, disable existing opt-in jobs, or disconnect accounts. The following describes the retained workspace, not a first-release promise.

- **Overview** shows live playback, listening totals, and an interactive daily or monthly activity map for the selected library. Long and all-time ranges add an activity-year selector, default to the busiest imported year, and separate imported history from direct playback in each bucket.
- **History** searches, filters, corrects, exports, or removes retained listening events.
- **Import** previews listening-history exports before adding anything.
- **Discover** explains each recommendation and accepts direct like or dismissal feedback.
- **Playlists** keeps recommendation sets temporary until a permitted user injects one into Jellyfin or Subsonic. Pending materialization updates live.
- **Automation** controls automatic history, retention, recommendation signals, schedules, listening-app keys, and the built-in AudioMuse-AI connection.

AudioMuse-AI is not an extension. Connect a self-hosted AudioMuse-AI server directly in **Intelligence → Automation**. Integrations still reports its health because it participates in the shared capability system.

When the development Intelligence workspace requests background access, Subsonic listeners reauthenticate as themselves using the same playlist-management consent described below. This also starts library indexing.

### Integrations

- **Services** lists every built-in or extension-backed capability and its readiness.
- **Accounts** stores encrypted personal or shared credentials and audience policy.
- **Extensions** installs, updates, reviews permissions, disables, and removes provider packages. Packages that need a verified sign-in show a **Sign-in** section with the steps; see [signed sessions](extensions/sdk-v1.md#signed-sessions).
- **Routing** orders the eligible fallback services for metadata, streaming, download, lyrics, playlists, scrobbling, and other typed capabilities.

A Service is an implementation. An Account is a credential and access policy for that Service. An Extension is an optional package that can add Services. Routing decides which ready Service/account pair is tried for a capability. These are related but not interchangeable settings.

Listeners connect **Personal** accounts, which only they may use and manage. Administrators can assign Personal accounts to a user or create **Shared** accounts for the household. A listener’s eligible Personal account is preferred over Shared accounts. Administrators manage Shared accounts; listeners can see their availability without viewing credentials or changing them.

Administrators can change an account’s audience in **Access → Edit access**. Sharing makes the account available for supported capabilities, including playlists or scrobbling when supported, and may consume the provider’s limits. Making it Personal stops new shared selections; it cannot retract audio already delivered.

In Settings, **Listeners can connect their own accounts** defaults to on. Turning it off prevents new listener connections while preserving use, configuration and removal of their existing accounts. Connection probes remain administrator-only, so listeners see **Save connection**. See [provider account policy](operations/configuration.md#provider-accounts).

### Subsonic playlist consent

On Subsonic sign-in, **Let Allstarr manage my playlists** starts unchecked. Selecting it saves an encrypted backend write credential only after your backend authenticates you. Leave it unchecked to sign in and read playlists without granting background writes.

In **Integrations → Accounts**, **Playlist management** shows your consent status. Enter your own Subsonic password to grant access, or revoke it there. Revocation stops future writes, including queued writes; sign-in and reads continue. Granting access again does not reactivate old revoked credential references. Reopen and save affected playlist links to use the new grant.

SSO alone does not grant playlist access. Use native backend reauthentication to consent. Administrators cannot enter another listener's password through this flow. Jellyfin uses its configured service token, checking the listener's target playlist permissions before writing, and does not store listener passwords.

### Shared playlists

The user who links a source playlist owns its source account, refresh schedule and Allstarr management actions. A link with a backend playlist follows that backend's read and edit permissions; a virtual-only link without a backend target stays private to its owner. Backend sharing does not expose the owner's link settings or credentials.

Each viewer's external tracks use accounts available to that viewer. If no eligible route exists, the track is unavailable for that viewer. Playlist artwork also requires an authorized viewer. Change public/share permissions in your backend client; Allstarr does not keep a separate share list.

### Activity

Activity groups operational events by outcome and shows target, source, and correlation details. Listeners see only their own activity, downloads, job details, progress, and live updates. Administrators can inspect household activity. Use it with container logs when a durable job or provider call fails.

### Listening preferences

Open your name in the desktop sidebar, or **More → Listening preferences** on a small screen. These choices belong to your signed-in backend user and apply in both Jellyfin and Subsonic clients. **All** shows every external track; **Hide clean/edited versions** hides edited versions while keeping naturally clean and explicit songs; **Clean only** hides known explicit tracks. Unknown explicit status remains visible in every mode. Native library items are unchanged.

The **[A]** external and **[E]** explicit title markers can be toggled independently; both default to on. Personal choices override household defaults. **Reset to household defaults** removes your choices and follows the latest defaults again. Reload client search results, playlists or queues to see updated presentation.

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
4. Choose **Stream when played** or **Keep every song**. Keep-all queues durable downloads using the playlist owner's authorized account for every resolved external song; local songs are already permanent. An unavailable or unresolved song is reported in Activity without rolling back the playlist import.
5. Open **Mappings** for ambiguous or unresolved tracks.

The default **Mapped** view preserves source order among tracks visible under your listening preferences. Local matches play from the media server, external `[A]` tracks use their eligible mapped providers in the configured streaming order, and unresolved songs remain visible with a clear not-playable status. **Original** previews the source metadata, while **Native** is only a diagnostic view of the separate playlist written into Jellyfin or Subsonic; it can omit external and unresolved entries that the backend cannot store natively.
6. Accept only a candidate that represents the same recording. Use interactive search when automatic candidates are wrong.

An accepted match is reusable across playlist sync, search, playback, and later rematches. A matched local item is returned as the complete native backend object. A genuinely external item keeps a stable virtual identity and provider label.

Automatic matching keeps confidence separate from routing preference. Among candidates that pass the acceptance threshold and artist-evidence checks, the local Jellyfin or Subsonic match wins; external matches follow **Integrations → Routing → Streaming priority**, even when a later provider scores higher. Confidence selects valid matches, not the preferred playback provider. Saved external mappings are rescored against current metadata and local candidates, never automatically promoted to 100%. Matching uses all source artist credits consistently; an exact lead artist, title, album, and close duration can support a threshold-qualified match when secondary credits differ.

When no candidate qualifies for acceptance, tentative selection uses the local window (7 confidence points by default), then configured provider windows of 5, 3, and 1 point. These windows never increase confidence or turn a tentative result into an accepted match. Interactive search displays raw score rank separately from routing priority; manual choices remain authoritative.

Use **Library → Mappings → Rematch all** when library metadata or matching behavior has changed. The preview includes resolved, review, and unresolved tracks; active manual pins, rejections, and manually selected provider routes are protected. Allstarr replaces current automatic decisions in durable batches of 25 and yields between batches. Earlier decisions remain available as audit history. A new matching-algorithm version schedules the same gradual replacement automatically for stale decisions.

Mapping tabs separate current decisions by purpose: **Manual** contains saved match choices, **Rejected** contains saved rejections, **Automatic** contains matcher-accepted results, **Tentative** contains viable candidates below the acceptance threshold, **Review** contains ambiguous near-ties, and **Unresolved** contains tracks without a safe candidate.

A personal match choice takes precedence over a household choice, which takes precedence over automatic matching. Listeners edit their own choices; administrators can also select **Household** in the match dialog. Both layers remain visible, including the choice underneath the effective one. Household choices still require each listener to have access to the selected library or provider account. An unavailable target falls back to an authorized automatic route.

**Clear personal** reveals the household choice, or automatic matching when no household choice exists. **Clear household** leaves any personal choice in place. **Clear and rematch** clears only the selected layer before recalculating automatic evidence. Ordinary rematching preserves both layers. Changes use the displayed revision; refresh after a conflict before trying again. Choices survive source refreshes and restart, retain their history, and never alter the shared canonical catalog.

A high confidence score can still appear under **Review** when two distinct, same-priority recordings are within the ambiguity guard; Allstarr will not guess between them. A 100% row under **Manual** that says **Accepted** is already accepted—the tab identifies who made the durable decision, not whether it passed the threshold.

Virtual playlists do not silently mutate the source service. Backend materialization adds only resolved local items unless the workflow explicitly says it will download or write back.

Import behavior is fixed when a playlist is created so a later settings edit cannot accidentally turn a frozen copy into a live source link. Song retention can be enabled later; Allstarr immediately queues the already-imported downloadable songs and applies the same policy to later linked updates. Kept files remain permanent until they are explicitly released or removed and must be backed up separately from database archives.

## Cached versus kept

- A **cached** track is a disposable playback/download artifact. It can be evicted by age or size policy and fetched again.
- A **kept** track was explicitly retained and is managed separately from cache cleanup.
- A database backup does not contain either audio folder. Back up kept/downloaded media according to your own storage policy.

If playback succeeded but Cached is empty, check the selected storage mode, provider route, durable job, and Activity outcome. A remote stream may not create a complete cache file until the provider download finishes and publishes atomically.

## Favorites

Hearting an external song records your favorite and queues a durable download using an authorized provider account. An existing match in your backend library avoids a redundant download. Native songs, albums, artists, and playlists do not trigger favorite downloads.

Favorite actions do not tag files, place music in the backend library, trigger library rescans, or send Last.fm love requests. Removing a favorite cancels its pending work and clears its favorite state; it does not delete an existing downloaded file.

## Listening and scrobbling

Automatic history is opt-in in the development-only Intelligence workspace. Enable it under **Intelligence → Automation** for your backend account. Completed protocol plays then appear in **Overview** without a manual refresh. Listening apps can receive a private key there and may optionally forward completed listens to connected Last.fm or ListenBrainz accounts.

Recommendation learning stays within your user and backend account. Local recommendations include only music libraries you can access. Plays, skips, favorites, playlist membership, and direct Discover feedback can influence later runs only when their signal types are enabled. A favorite raises the track's preference signal; removing the favorite cancels that contribution. Generated sets remain ephemeral in Intelligence until **Create in Jellyfin** or **Create in Subsonic** is used, while Automation can refresh and publish rotating personal sets on a schedule.

Scrobbling is checkpointed so a provider is not sent the same completed listen twice. Home and History show what Allstarr observed; Activity shows delivery failures and retries.

## Safety and recovery

- Keep the dashboard on a trusted network or behind an authenticated proxy.
- Use a separate provider account when a service allows it, and grant the smallest audience required.
- Preview imports, legacy configuration, playlist changes, and destructive maintenance actions.
- Create and download a verified database-and-key-ring backup in Settings → Maintenance before an update.
- Back up deployment configuration, retained media, extension packages, and optional provider sessions separately; see [storage operations](operations/storage.md).

For exact procedures, see [configuration](operations/configuration.md), [storage and recovery](operations/storage.md), and [client compatibility](operations/client-compatibility.md).
