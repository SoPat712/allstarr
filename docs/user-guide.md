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

Provider cards and account titles use the provider's display name. The subtitle shows the Personal account owner or **Shared with everyone**, followed by an optional custom connection name. When someone else created a Personal account, the card distinguishes its owner from its creator. New Personal connections default to the signed-in administrator; changing the owner changes whose listening may use the account. Leave the connection name blank to use the provider name. Older automatically generated account labels are renamed on startup; custom labels and credentials are preserved. A provider without a health sample shows **Not checked yet**.

Apple Music has a three-step setup checklist: connect the Media User Token and storefront, sign the playback gateway into the same Apple account, then test playback. Package installation and gateway details are under Advanced setup. See [Apple setup](operations/apple-download-provider.md#connect-allstarr).

Administrators may test playback on accounts they manage, including another user’s Personal account. These diagnostics are audited to the administrator. Ordinary listener playback still uses only the listener’s eligible Personal or Shared accounts.

Action results appear in a shared notification area at the bottom right on desktop or above mobile navigation. Success and information messages close after five seconds, pausing while hovered or focused. Errors remain until dismissed. Form errors and warnings about stale page data remain beside the affected controls.

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

Use the up and down buttons in **Lyrics source order** to choose which available sources your music apps try first. Allstarr prefers synced lyrics and retains plain text when no source provides timing. Your order applies only to your listener account and still requires enabled sources and authorized accounts. Resetting preferences restores the latest household order as well as your display defaults.

**Media server lyrics** can be moved with the same controls. If they win, the music app receives the media server's original lyric response, including its language and timing details.

**Kept LRC** reads lyrics beside a verified household kept copy, including copies saved under a custom kept root or from an account that is now disconnected. Creating or reusing a kept copy also tries to save missing lyrics using household sources and preferences. It follows media-server lyrics by default and can be moved or disabled. Allstarr checks the listener's account and file ownership; missing or unreadable sidecars fall through to another source. Timed lyrics are preferred over plain words, and reading a sidecar never changes the audio or lyrics file.

To fix a song's lyrics, open its match details and select **Wrong lyrics?**. Preview each available source, then select **Use this source for this recording**. This saves a source preference, including a plain-text source when you prefer it; it does not freeze the words. If that source becomes unavailable or disabled, Allstarr uses the normal source order until it returns.

A personal choice overrides the household choice. **Use household default** removes your personal choice. Administrators can preview shared or public sources and save a household preference for listeners without a personal choice; **Use automatic selection** removes it. If the words or choice change while the picker is open, it reloads them for review before another save. Play or match a new song and reload its details if lyric choices are not available yet.

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

1. Open **Library → Playlists → Import playlist**. Choose **Public Spotify playlist** and paste a link, URI or ID without connecting an account. For a private playlist or **Liked Songs**, select your connected Spotify account; you can browse its playlists or paste a link. You can also use **Browse a Spotify user** with a user link, URI or ID to choose one of that profile’s public playlists. Each selected playlist is imported separately. Public imports and profile discovery never use a saved account.
2. Choose **Import once** to freeze the source snapshot, or **Keep in sync** to follow source changes. Sync defaults to daily, with hourly and weekly schedules available. A frozen import never reads later source changes, even if its source account is disabled.
3. Choose **New playlist** to create a destination once, or **Existing playlist** to select an exact backend playlist. Future updates use that destination; Allstarr does not adopt a playlist just because its name matches. Changing destinations leaves the old playlist's songs in place and starts ownership tracking separately for the new destination.
4. Turn on **Keep downloaded copies** to queue permanent downloads of resolved external songs using your authorized accounts. It defaults to off; local songs are already permanent. An unavailable or unresolved song is reported in Activity without rolling back the import.
5. Open **Mappings** for ambiguous or unresolved tracks. Accept only a candidate that represents the same recording; use interactive search when automatic candidates are wrong.

In playlist details, **Actions → Export missing songs as CSV/JSON** downloads the current source songs with no playable local or external route. Source order and duplicate songs are preserved, with positions starting at 1. Exports include title, artists, album, ISRC, duration, match state, and the source track ID when known. Account and backend identifiers are excluded.

Metadata follows the link's sync settings. Turning name or description sync off preserves the destination's current values. Jellyfin supports name, description and artwork; Subsonic supports name and description through its [playlist update API](https://opensubsonic.netlify.app/docs/endpoints/updateplaylist/), and reports artwork as unsupported. New Subsonic destinations use the ID returned by the backend, so an older playlist with the same name is left alone. If the backend cannot confirm the new destination's ID, creation is reported as unsupported; choose an existing playlist instead.

The **Mapped** view preserves source order among tracks visible under your listening preferences. Local matches play from the media server, external `[A]` tracks use eligible providers in configured streaming order, and unresolved songs remain visible with a clear not-playable status. The backend playlist contains matched local songs so it remains useful when opened directly.

Each source account and playlist has one link per backend; public sources have a separate account-free scope for each owner. Repeating the same import reuses that link; a different import behavior, new/existing destination choice, or selected existing destination is rejected with instructions to edit or remove the current link. Removing a link leaves its backend playlist intact.

When synchronization removes stale songs, it removes only memberships it added. A song already present before an import stays yours, including a former synchronized song that you manually add back after its removal.

An accepted match is reusable across playlist sync, search, playback, and later rematches. A matched local item is returned as the complete native backend object. A genuinely external item keeps a stable virtual identity and provider label.

Automatic matching keeps confidence separate from routing preference. Among candidates that pass the acceptance threshold and artist-evidence checks, the local Jellyfin or Subsonic match wins; external matches follow **Integrations → Routing → Streaming priority**, even when a later provider scores higher. Confidence selects valid matches, not the preferred playback provider. Saved external mappings are rescored against current metadata and local candidates, never automatically promoted to 100%. Matching uses all source artist credits consistently; an exact lead artist, title, album, and close duration can support a threshold-qualified match when secondary credits differ. Title explanations distinguish case folding, punctuation/diacritic normalization, ignored release decorators, and fuzzy comparison. Live, acoustic, instrumental, remix, clean, and radio-edit differences stay available for manual review but are never selected automatically from field scores, even with a lower acceptance threshold; manual choices remain authoritative.

When no candidate qualifies for acceptance, tentative selection uses the local window (7 confidence points by default), then configured provider windows of 5, 3, and 1 point. These windows never increase confidence or turn a tentative result into an accepted match. Interactive search displays raw score rank separately from routing priority; manual choices remain authoritative.

Use **Library → Mappings → Rematch all** when library metadata or matching behavior has changed. The preview includes resolved, review, and unresolved tracks; active manual pins, rejections, and manually selected provider routes are protected. Allstarr replaces current automatic decisions in durable batches of 25 and yields between batches. Earlier decisions remain available as audit history. A new matching-algorithm version schedules the same gradual replacement automatically for stale decisions.

Mapping tabs separate current decisions by purpose: **Manual** contains saved match choices, **Rejected** contains saved rejections, **Automatic** contains matcher-accepted results, **Tentative** contains viable candidates below the acceptance threshold, **Review** contains ambiguous near-ties, and **Unresolved** contains tracks without a safe candidate.

A personal match choice takes precedence over a household choice, which takes precedence over automatic matching. Listeners edit their own choices; administrators can also select **Household** in the match dialog. Both layers remain visible, including the choice underneath the effective one. Household choices still require each listener to have access to the selected library or provider account. An unavailable target falls back to an authorized automatic route.

**Clear personal** reveals the household choice, or automatic matching when no household choice exists. **Clear household** leaves any personal choice in place. **Clear and rematch** clears only the selected layer before recalculating automatic evidence. Ordinary rematching and matcher upgrades preserve both layers, including candidate-specific rejections; a rejection of one candidate does not block a different accepted match. Changes use the displayed revision; refresh after a conflict before trying again. Choices survive source refreshes and restart, retain their history, and never alter the shared canonical catalog.

A high confidence score can still appear under **Review** when two distinct, same-priority recordings are within the ambiguity guard; Allstarr will not guess between them. A 100% row under **Manual** that says **Accepted** is already accepted—the tab identifies who made the durable decision, not whether it passed the threshold.

Virtual playlists do not silently mutate the source service. Backend materialization adds only resolved local items unless the workflow explicitly says it will download or write back.

Import behavior is fixed when a playlist is created so a later settings edit cannot accidentally turn a frozen copy into a live source link. Song retention can be enabled later; Allstarr immediately queues the already-imported downloadable songs and applies the same policy to later linked updates. Kept files remain permanent until they are explicitly released or removed and must be backed up separately from database archives.

Playlist synchronization retains ownership of songs whose removal has not been confirmed by a backend read and retries their removal on the next sync. Once removal is confirmed, a manually re-added copy stays manual. On upgrade, older inactive memberships are treated as completed removals, preserving songs you manually added back. If a backend creates a destination or adds songs before a later write step fails, retry uses the same destination. Only additions confirmed by a backend read become sync-owned; an unavailable verification read leaves uncertain songs untouched.

## Cached versus kept

- A **cached** track is a disposable playback/download artifact. It can be evicted by age or size policy and fetched again.
- A **kept** track was explicitly retained and is managed separately from cache cleanup.
- A database backup does not contain either audio folder. Back up kept/downloaded media according to your own storage policy.

If playback succeeded but Cached is empty, check the selected storage mode, provider route, durable job, and Activity outcome. A remote stream may not create a complete cache file until the provider download finishes and publishes atomically.

A playlist with **Keep downloaded copies** retains its current external songs as household kept recordings. Existing copies are shared with hearts and other retained playlists. Turning the option off, deleting the link, or publishing a source update that removes a song releases only that playlist's interest. The final release starts the same twenty-four-hour grace period used by favorites. Re-enabling the option protects existing copies immediately and queues fresh work for missing ones. In-progress downloads keep their original jobs across refreshes and reordering when the source song and selected recording are unchanged. Changing the source or recording replaces the obsolete work.

Administrators can choose **Keep** on an indexed cached track. This queues a verified household copy without moving the cached audio or its lyrics. Activity shows the download result. Existing household copies are reused even if the original source account is disconnected. **Release Keep** removes only your explicit Keep interest. Hearts, retained playlists and other listeners' Keeps still protect the file; the final release starts a twenty-four-hour grace period. Bulk removal follows the same rule for your Keeps.

## Favorites

Hearting an external song records your favorite immediately and queues a durable kept copy using an authorized provider account. An accessible recording in your backend library avoids a redundant download. An existing household kept copy is reused without contacting its original provider. Otherwise, verified audio is attached to the recording and becomes available to everyone in your household. Its title, artists, album and duration remain available when the original provider account is disconnected. Native songs, albums, artists, and playlists do not trigger favorite downloads.

Jellyfin favorite views and Subsonic `getStarred` / `getStarred2` include your saved provider favorites alongside native favorites. The list uses saved display metadata and stays available when the provider is offline. Older favorites without display metadata use a “Saved track”, “Saved album”, “Saved artist” or “Saved playlist” label until they are favorited again. Pending or failed download work does not hide a heart. Subsonic exposes songs, albums and artists; Jellyfin also exposes playlists. Native folder and artist filters remain scoped to the backend.

Favorite actions do not tag files, place music in the backend library, trigger library rescans, or send Last.fm love requests. Removing a favorite cancels its pending work, clears its favorite state and releases that heart’s reference to the household copy. Once its last managed reference is released, the household kept file has a twenty-four-hour grace period before removal. Another listener’s heart or another retained reference protects it. Hearting it again protects an existing copy immediately and cancels its cleanup.

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
